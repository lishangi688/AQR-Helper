using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AQRLiteHelper.Bridge
{
    internal static class AssetDownloader
    {
        private const int BufferSize = 81920;

        private static readonly HttpClient Client = CreateClient();

        /// <param name="connections">
        /// Parallel connections to use. Values above 1 use <see cref="SegmentedDownloader"/>
        /// when the server supports Range requests; otherwise, or if segments keep
        /// failing, the file is downloaded again over a single connection.
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
                        completed = await SegmentedDownloader.TryDownloadAsync(
                            Client, uri, temporaryPath, connections, cancellationToken, progress).ConfigureAwait(false);
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
