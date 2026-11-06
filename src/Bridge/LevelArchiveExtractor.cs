using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AQRLiteHelper.Bridge
{
    internal static class LevelArchiveExtractor
    {
        // Windows MAX_PATH (260) minus the terminating null.
        private const int MaxPathLength = 259;

        public static (bool Success, string Path, string Message) ResolveLevelPath(string downloadedPath)
        {
            if (!IsZipArchive(downloadedPath))
            {
                return (true, downloadedPath, string.Empty);
            }

            // "X.zip" extracts to "X"; the old "X.zip.extracted" wasted 14 characters of path.
            var directory = Path.Combine(
                Path.GetDirectoryName(downloadedPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(downloadedPath));
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
            var names = ZipEntryNames.Read(archivePath);
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries;
            if (names != null && names.Count != entries.Count)
            {
                Main.Mod?.Logger.Warning($"压缩包目录条目数不一致（{names.Count}/{entries.Count}），将使用默认文件名解码。");
                names = null;
            }

            var relativeNames = StripSharedFolders(entries.Select((entry, i) => names?[i] ?? entry.FullName).ToList());

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var entryName = relativeNames[i];
                if (entryName == null)
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(directory, entryName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("压缩包包含不安全的文件路径。");
                }

                if (destination.Length > MaxPathLength)
                {
                    throw new PathTooLongException(
                        $"解压后的文件路径过长（{destination.Length} 字符，Windows 上限 {MaxPathLength}）。"
                        + "请在 Mod 设置中把谱面保存目录换到更短的路径，例如 D:\\ADOFAI。");
                }

                if (entryName.EndsWith("/", StringComparison.Ordinal))
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

        /// <summary>
        /// Normalizes separators to "/", drops macOS metadata (__MACOSX/) and removes
        /// the leading folders every entry shares, e.g. "Level/Level/x.ogg" becomes
        /// "x.ogg". Levels are often zipped inside one or two long wrapper folders,
        /// which pushes paths past the Windows 260-character limit. Relative layout
        /// is unchanged, so .adofai files still find their assets. Null marks entries
        /// that are skipped.
        /// </summary>
        internal static List<string?> StripSharedFolders(IReadOnlyList<string> entryNames)
        {
            var parsed = entryNames
                .Select(name => name.Replace('\\', '/'))
                .Select(name => name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : new
                    {
                        IsDirectory = name.EndsWith("/", StringComparison.Ordinal),
                        Parts = name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    })
                .ToList();

            // Folders shared by every file. Directory entries are ignored here: an entry
            // for the wrapper folder itself ("A/") must not stop "A/A/" being stripped,
            // and folders are created anyway when their files are written.
            string[]? shared = null;
            var sharedCount = 0;
            foreach (var entry in parsed)
            {
                if (entry == null || entry.IsDirectory || entry.Parts.Length == 0)
                {
                    continue;
                }

                var folderCount = entry.Parts.Length - 1;
                if (shared == null)
                {
                    shared = entry.Parts;
                    sharedCount = folderCount;
                    continue;
                }

                var common = 0;
                while (common < sharedCount && common < folderCount
                       && shared[common].Equals(entry.Parts[common], StringComparison.OrdinalIgnoreCase))
                {
                    common++;
                }

                sharedCount = common;
            }

            return parsed
                .Select(entry =>
                {
                    if (entry == null || entry.Parts.Length <= sharedCount)
                    {
                        return null;
                    }

                    var relative = string.Join("/", entry.Parts.Skip(sharedCount));
                    return entry.IsDirectory ? relative + "/" : relative;
                })
                .ToList();
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
