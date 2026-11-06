using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace AQRLiteHelper.Bridge
{
    /// <summary>
    /// Multi-connection download with dynamic segmentation (as in IDM/FDM):
    /// workers take 4 MB chunks from a shared pool; once the pool is empty an
    /// idle worker takes over the back half of the largest unfinished segment,
    /// so the tail is not left to a single slow connection. Failed or stalled
    /// segments go back to the pool and are retried by any worker.
    /// </summary>
    internal static class SegmentedDownloader
    {
        private const int BufferSize = 81920;
        private const long ChunkBytes = 4L * 1024L * 1024L;

        // A segment is only split when both halves keep at least this much.
        private const long MinSplitBytes = 256L * 1024L;

        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Returns false without writing anything when the server does not support
        /// Range requests or the file is too small to be worth splitting. Throws when
        /// segments keep failing, so the caller can fall back to one connection.
        /// </summary>
        public static async Task<bool> TryDownloadAsync(
            HttpClient client,
            Uri uri,
            string path,
            int connections,
            CancellationToken cancellationToken,
            Action<long, long>? progress)
        {
            var probe = await ProbeAsync(client, uri, cancellationToken).ConfigureAwait(false);
            if (probe == null)
            {
                Main.Mod?.Logger.Log("下载服务器不支持分段请求，使用单线程下载。");
                return false;
            }

            var (target, totalBytes) = probe.Value;
            if (totalBytes < ChunkBytes)
            {
                return false;
            }

            var workerCount = (int)Math.Min(connections, totalBytes / MinSplitBytes);
            AllowConnections(target, workerCount);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Write))
            {
                file.SetLength(totalBytes);
            }

            Main.Mod?.Logger.Log($"使用 {workerCount} 线程下载（{totalBytes} 字节）。");
            var scheduler = new Scheduler(totalBytes, 3 + workerCount * 2, progress);
            progress?.Invoke(0L, totalBytes);

            using var workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var workers = Enumerable.Range(0, workerCount)
                .Select(_ => Task.Run(() => RunWorkerAsync(client, target, path, scheduler, workersCancellation)))
                .ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            scheduler.ThrowIfFailed();
            return true;
        }

        /// <summary>
        /// Requests the first byte to learn the total size and whether ranges work.
        /// Also returns the URL after redirects (r2 → aqr-file), so each segment
        /// skips the redirect round trip.
        /// </summary>
        private static async Task<(Uri Target, long TotalBytes)?> ProbeAsync(
            HttpClient client,
            Uri uri,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var length = response.Content.Headers.ContentRange?.Length;
            if (response.StatusCode != HttpStatusCode.PartialContent || length == null)
            {
                return null;
            }

            return (response.RequestMessage?.RequestUri ?? uri, length.Value);
        }

        private static async Task RunWorkerAsync(
            HttpClient client,
            Uri uri,
            string path,
            Scheduler scheduler,
            CancellationTokenSource workersCancellation)
        {
            var token = workersCancellation.Token;
            var buffer = new byte[BufferSize];
            try
            {
                using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Write, BufferSize, useAsync: true);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var segment = scheduler.TryTake(out var finished);
                    if (segment == null)
                    {
                        if (finished)
                        {
                            return;
                        }

                        // Other workers still own segments too small to split; wait in
                        // case one of them fails and its range comes back to the pool.
                        await Task.Delay(IdleWait, token).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await DownloadSegmentAsync(client, uri, output, buffer, scheduler, segment, token).ConfigureAwait(false);
                        scheduler.Finish(segment);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        Main.Mod?.Logger.Warning($"下载分段失败，将重新分配：{ex.Message}");
                        if (!scheduler.Release(segment, ex))
                        {
                            workersCancellation.Cancel();
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Cancelled by the caller or because the failure budget ran out.
            }
        }

        private static async Task DownloadSegmentAsync(
            HttpClient client,
            Uri uri,
            FileStream output,
            byte[] buffer,
            Scheduler scheduler,
            Segment segment,
            CancellationToken token)
        {
            var (start, end) = scheduler.Remaining(segment);
            if (start > end)
            {
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(start, end);
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersTimeout.CancelAfter(StallTimeout);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"分段 {start}-{end} 等待响应超时。");
            }

            using (response)
            {
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.To != end)
                {
                    throw new IOException($"服务器未按请求返回分段 {start}-{end}（HTTP {(int)response.StatusCode}）。");
                }

                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                while (true)
                {
                    var read = await ReadWithStallTimeoutAsync(input, buffer, response, token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        if (scheduler.IsDone(segment))
                        {
                            return;
                        }

                        throw new IOException($"分段 {start}-{end} 连接提前关闭。");
                    }

                    // The segment's end may have moved because another worker took over
                    // its back half; only write what still belongs to this segment.
                    var (offset, count) = scheduler.Reserve(segment, read);
                    if (count > 0)
                    {
                        output.Seek(offset, SeekOrigin.Begin);
                        await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                        scheduler.Commit(segment, count);
                    }

                    if (count < read || scheduler.IsDone(segment))
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Network streams do not reliably honour cancellation under Mono, so a read
        /// that delivers nothing for <see cref="StallTimeout"/> aborts the response.
        /// </summary>
        private static async Task<int> ReadWithStallTimeoutAsync(
            Stream input,
            byte[] buffer,
            HttpResponseMessage response,
            CancellationToken token)
        {
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var readTask = input.ReadAsync(buffer, 0, buffer.Length, token);
            var delayTask = Task.Delay(StallTimeout, delayCancellation.Token);
            if (await Task.WhenAny(readTask, delayTask).ConfigureAwait(false) == readTask)
            {
                delayCancellation.Cancel();
                return await readTask.ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            response.Dispose();
            _ = readTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"连接 {StallTimeout.TotalSeconds:0} 秒没有收到数据。");
        }

        /// <summary>
        /// Mono's HttpClient runs on HttpWebRequest, which allows only two concurrent
        /// connections per host by default; raise it so segments really run in parallel.
        /// </summary>
        private static void AllowConnections(Uri uri, int connections)
        {
            try
            {
                var servicePoint = ServicePointManager.FindServicePoint(uri);
                servicePoint.ConnectionLimit = Math.Max(servicePoint.ConnectionLimit, connections);
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Warning($"无法调整连接数上限：{ex.Message}");
            }
        }

        /// <summary>
        /// A byte range owned by one worker. <see cref="Reserved"/> is the next byte
        /// the worker will write (others may only take bytes at or after it);
        /// <see cref="Written"/> is how far data has actually reached the file.
        /// <see cref="End"/> (inclusive) shrinks when another worker takes over the tail.
        /// </summary>
        private sealed class Segment
        {
            public Segment(long start, long end)
            {
                Reserved = start;
                Written = start;
                End = end;
            }

            public long Reserved;
            public long Written;
            public long End;
        }

        private sealed class Scheduler
        {
            private readonly object _sync = new object();
            private readonly long _totalBytes;
            private readonly Action<long, long>? _progress;
            private readonly List<Segment> _active = new List<Segment>();
            private readonly Queue<(long Start, long End)> _retry = new Queue<(long Start, long End)>();
            private long _nextOffset;
            private long _receivedBytes;
            private int _failuresLeft;
            private Exception? _failure;

            public Scheduler(long totalBytes, int failureBudget, Action<long, long>? progress)
            {
                _totalBytes = totalBytes;
                _failuresLeft = failureBudget;
                _progress = progress;
            }

            /// <summary>
            /// Hands out work in this order: ranges returned by failed segments, fresh
            /// chunks from the pool, then the back half of the largest active segment.
            /// <paramref name="finished"/> is true when nothing is left anywhere.
            /// </summary>
            public Segment? TryTake(out bool finished)
            {
                lock (_sync)
                {
                    finished = false;
                    if (_failure != null)
                    {
                        finished = true;
                        return null;
                    }

                    if (_retry.Count > 0)
                    {
                        var (start, end) = _retry.Dequeue();
                        return Activate(start, end);
                    }

                    if (_nextOffset < _totalBytes)
                    {
                        var start = _nextOffset;
                        var end = Math.Min(start + ChunkBytes, _totalBytes) - 1;
                        _nextOffset = end + 1;
                        return Activate(start, end);
                    }

                    Segment? largest = null;
                    var largestRemaining = 0L;
                    foreach (var segment in _active)
                    {
                        var remaining = segment.End - segment.Reserved + 1;
                        if (remaining > largestRemaining)
                        {
                            largest = segment;
                            largestRemaining = remaining;
                        }
                    }

                    if (largest != null && largestRemaining >= MinSplitBytes * 2)
                    {
                        var splitAt = largest.Reserved + largestRemaining / 2;
                        var tailEnd = largest.End;
                        largest.End = splitAt - 1;
                        return Activate(splitAt, tailEnd);
                    }

                    finished = _active.Count == 0;
                    return null;
                }
            }

            public (long Start, long End) Remaining(Segment segment)
            {
                lock (_sync)
                {
                    return (segment.Written, segment.End);
                }
            }

            /// <summary>Claims up to <paramref name="count"/> bytes at the segment's write position.</summary>
            public (long Offset, int Count) Reserve(Segment segment, int count)
            {
                lock (_sync)
                {
                    var available = segment.End - segment.Reserved + 1;
                    if (available <= 0)
                    {
                        return (0L, 0);
                    }

                    var claimed = (int)Math.Min(count, available);
                    var offset = segment.Reserved;
                    segment.Reserved += claimed;
                    return (offset, claimed);
                }
            }

            public void Commit(Segment segment, int count)
            {
                lock (_sync)
                {
                    segment.Written += count;
                    _receivedBytes += count;
                    _progress?.Invoke(_receivedBytes, _totalBytes);
                }
            }

            public bool IsDone(Segment segment)
            {
                lock (_sync)
                {
                    return segment.Written > segment.End;
                }
            }

            public void Finish(Segment segment)
            {
                lock (_sync)
                {
                    Deactivate(segment);
                }
            }

            /// <summary>
            /// Returns the unwritten part of a failed segment to the pool. Returns false
            /// once the failure budget is exhausted, which aborts the whole download.
            /// </summary>
            public bool Release(Segment segment, Exception failure)
            {
                lock (_sync)
                {
                    Deactivate(segment);
                    if (--_failuresLeft >= 0)
                    {
                        return true;
                    }

                    _failure ??= failure;
                    return false;
                }
            }

            public void ThrowIfFailed()
            {
                lock (_sync)
                {
                    if (_failure != null)
                    {
                        throw new IOException($"分段多次失败：{_failure.Message}", _failure);
                    }

                    if (_receivedBytes != _totalBytes)
                    {
                        throw new IOException($"分段下载不完整（{_receivedBytes}/{_totalBytes} 字节）。");
                    }
                }
            }

            private Segment Activate(long start, long end)
            {
                var segment = new Segment(start, end);
                _active.Add(segment);
                return segment;
            }

            private void Deactivate(Segment segment)
            {
                _active.Remove(segment);
                if (segment.Written <= segment.End)
                {
                    _retry.Enqueue((segment.Written, segment.End));
                }
            }
        }
    }
}
