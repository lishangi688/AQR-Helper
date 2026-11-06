using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace AQRLiteHelper.Bridge
{
    internal static class AssetDownloader
    {
        private const int BufferSize = 81920;

        // Below this, extra connections cost more in setup than they save.
        private const long MinSegmentBytes = 2L * 1024L * 1024L;

        private static readonly HttpClient Client = CreateClient();

        /// <param name="connections">
        /// Parallel connections to use. Values above 1 split the file into HTTP Range
        /// segments when the server supports it; otherwise, or if any segment fails,
        /// the file is downloaded again over a single connection.
        /// </param>
        public static async Task<(bool Success, string Path, string Message)> DownloadAsync(
            string url,
            string directory,
            string fileName,
            int connections,
            CancellationToken cancellationToken,
            Action<long, long>? progress = null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return (false, string.Empty, "下载地址必须使用 HTTPS。");
            }

            var safeFileName = NormalizeFileName(fileName);
            var targetPath = Path.Combine(directory, safeFileName);
            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.part";

            try
            {
                Directory.CreateDirectory(directory);

                var completed = false;
                if (connections > 1)
                {
                    try
                    {
                        completed = await TrySegmentedDownloadAsync(
                            uri, temporaryPath, connections, cancellationToken, progress).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        Main.Mod?.Logger.Warning($"多线程下载失败，改用单线程重新下载：{ex.Message}");
                        TryDelete(temporaryPath);
                    }
                }

                if (!completed)
                {
                    await SingleDownloadAsync(uri, temporaryPath, cancellationToken, progress).ConfigureAwait(false);
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(temporaryPath, targetPath);
                return (true, targetPath, string.Empty);
            }
            catch (Exception ex)
            {
                TryDelete(temporaryPath);
                return (false, string.Empty, ex.Message);
            }
        }

        private static async Task SingleDownloadAsync(
            Uri uri,
            string path,
            CancellationToken cancellationToken,
            Action<long, long>? progress)
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            progress?.Invoke(0L, totalBytes);

            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[BufferSize];
            long receivedBytes = 0;
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                receivedBytes += bytesRead;
                progress?.Invoke(receivedBytes, totalBytes);
            }

            progress?.Invoke(receivedBytes, totalBytes);
        }

        /// <summary>
        /// Downloads <paramref name="uri"/> as parallel Range segments written into one
        /// preallocated file. Returns false without writing anything when the server
        /// does not support ranges or the file is too small to be worth splitting.
        /// </summary>
        private static async Task<bool> TrySegmentedDownloadAsync(
            Uri uri,
            string path,
            int connections,
            CancellationToken cancellationToken,
            Action<long, long>? progress)
        {
            long totalBytes;
            using (var probe = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                probe.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await Client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                var length = response.Content.Headers.ContentRange?.Length;
                if (response.StatusCode != HttpStatusCode.PartialContent || length == null)
                {
                    Main.Mod?.Logger.Log("下载服务器不支持分段请求，使用单线程下载。");
                    return false;
                }

                totalBytes = length.Value;
            }

            var segmentCount = (int)Math.Min(connections, totalBytes / MinSegmentBytes);
            if (segmentCount < 2)
            {
                return false;
            }

            AllowConnections(uri, segmentCount);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Write))
            {
                file.SetLength(totalBytes);
            }

            Main.Mod?.Logger.Log($"使用 {segmentCount} 线程下载（{totalBytes} 字节）。");
            var gate = new object();
            long receivedBytes = 0;
            progress?.Invoke(0L, totalBytes);
            void Report(int bytes)
            {
                lock (gate)
                {
                    receivedBytes += bytes;
                    progress?.Invoke(receivedBytes, totalBytes);
                }
            }

            // The first failing segment cancels the rest; its exception is the one reported.
            using var segmentsCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Exception? firstFailure = null;
            var segmentSize = totalBytes / segmentCount;
            var segments = Enumerable.Range(0, segmentCount).Select(async index =>
            {
                var start = index * segmentSize;
                var end = index == segmentCount - 1 ? totalBytes - 1 : start + segmentSize - 1;
                try
                {
                    await DownloadSegmentAsync(uri, path, start, end, segmentsCancellation.Token, Report).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                    segmentsCancellation.Cancel();
                }
            });
            await Task.WhenAll(segments).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (firstFailure != null)
            {
                throw firstFailure;
            }

            return true;
        }

        private static async Task DownloadSegmentAsync(
            Uri uri,
            string path,
            long start,
            long end,
            CancellationToken cancellationToken,
            Action<int> report)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(start, end);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.To != end)
            {
                throw new IOException($"服务器未按请求返回分段 {start}-{end}（HTTP {(int)response.StatusCode}）。");
            }

            var expected = end - start + 1;
            long written = 0;
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Write, BufferSize, useAsync: true);
            output.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[BufferSize];
            while (written < expected)
            {
                var toRead = (int)Math.Min(buffer.Length, expected - written);
                var bytesRead = await input.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    throw new IOException($"分段 {start}-{end} 数据不完整（{written}/{expected} 字节）。");
                }

                await output.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                written += bytesRead;
                report(bytesRead);
            }
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

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AQR-Helper/1.0");
            return client;
        }

        private static string NormalizeFileName(string fileName)
        {
            var name = Path.GetFileName(fileName?.Trim());
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "level.zip";
            }

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalidChar, '_');
            }

            var extension = Path.GetExtension(name);
            if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".adozip", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".adofai", StringComparison.OrdinalIgnoreCase))
            {
                name += ".zip";
            }

            return name;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
