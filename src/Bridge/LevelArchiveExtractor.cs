using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AQRLiteHelper.Bridge
{
    internal static class LevelArchiveExtractor
    {
        public static (bool Success, string Path, string Message) ResolveLevelPath(string downloadedPath)
        {
            if (!IsZipArchive(downloadedPath))
            {
                return (true, downloadedPath, string.Empty);
            }

            var directory = downloadedPath + ".extracted";
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                Directory.CreateDirectory(directory);
                ExtractArchive(downloadedPath, directory);

                var levelPath = Directory
                    .GetFiles(directory, "*.adofai", SearchOption.AllDirectories)
                    .Where(path => !Path.GetFileName(path).Equals("backup.adofai", StringComparison.OrdinalIgnoreCase))
                    .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                    .OrderBy(path => Path.GetFileName(path).Equals("main.adofai", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(levelPath))
                {
                    return (false, string.Empty, "压缩包中没有找到 .adofai 谱面文件。");
                }

                return (true, levelPath, string.Empty);
            }
            catch (Exception ex)
            {
                TryDeleteDirectory(directory);
                return (false, string.Empty, ex.Message);
            }
        }

        private static bool IsZipArchive(string path)
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".adozip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using var stream = File.OpenRead(path);
                return stream.Length >= 2 && stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
            }
            catch
            {
                return false;
            }
        }

        private static void ExtractArchive(string archivePath, string directory)
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(directory, entry.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("压缩包包含不安全的文件路径。");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
