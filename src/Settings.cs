using System;
using System.IO;
using UnityFileDialog;
using UnityModManagerNet;
using UnityEngine;

namespace AQRLiteHelper
{
    public class Settings : UnityModManager.ModSettings
    {
        public const int MinDownloadThreads = 2;
        public const int MaxDownloadThreads = 32;

        // Above this, some servers or CDNs may start rate-limiting the connections.
        public const int HighDownloadThreads = 16;

        public string DownloadDirectory = string.Empty;

        public bool MultiThreadDownload;

        public int DownloadThreads = 8;

        [NonSerialized]
        private string _directoryError = string.Empty;

        public string EffectiveDownloadDirectory => string.IsNullOrWhiteSpace(DownloadDirectory)
            ? GetDefaultDownloadDirectory()
            : DownloadDirectory.Trim();

        public int EffectiveDownloadConnections => MultiThreadDownload
            ? Mathf.Clamp(DownloadThreads, MinDownloadThreads, MaxDownloadThreads)
            : 1;

        public void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("AQR Helper 设置");
            GUILayout.Space(6f);
            GUILayout.Label("谱面保存目录");

            GUILayout.BeginHorizontal();
            DownloadDirectory = GUILayout.TextField(EffectiveDownloadDirectory);
            if (GUILayout.Button("浏览", GUILayout.Width(70f)))
            {
                BrowseDownloadDirectory();
            }

            if (GUILayout.Button("打开文件夹", GUILayout.Width(100f)))
            {
                RevealDownloadDirectory();
            }

            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_directoryError))
            {
                var previousColor = GUI.color;
                GUI.color = Color.red;
                GUILayout.Label(_directoryError);
                GUI.color = previousColor;
            }
            else
            {
                GUILayout.Label("目录不存在时会自动创建，下载的谱面不会在游戏退出时删除。");
            }

            GUILayout.Space(10f);
            MultiThreadDownload = GUILayout.Toggle(MultiThreadDownload, " 多线程下载");

            // Always drawn (only disabled) so IMGUI's control count stays stable between events.
            var previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && MultiThreadDownload;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"线程数：{DownloadThreads}", GUILayout.Width(90f));
            DownloadThreads = Mathf.RoundToInt(GUILayout.HorizontalSlider(
                Mathf.Clamp(DownloadThreads, MinDownloadThreads, MaxDownloadThreads),
                MinDownloadThreads,
                MaxDownloadThreads,
                GUILayout.Width(220f)));
            GUILayout.EndHorizontal();
            GUI.enabled = previousEnabled;
            GUILayout.Label(MultiThreadDownload && DownloadThreads > HighDownloadThreads
                ? $"线程数超过 {HighDownloadThreads} 时可能被服务器限速，反而变慢。"
                : "下载末段会自动拆分慢连接的剩余部分；服务器不支持分段下载或多次失败时，会改用单线程重新下载。");
        }

        public void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            TryPrepareDownloadDirectory(out _);
            Save(modEntry);
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public static Settings Load(UnityModManager.ModEntry modEntry)
        {
            var settings = Load<Settings>(modEntry);
            if (string.IsNullOrWhiteSpace(settings.DownloadDirectory))
            {
                settings.DownloadDirectory = GetDefaultDownloadDirectory(modEntry);
            }

            return settings;
        }

        public bool TryPrepareDownloadDirectory(out string directory)
        {
            directory = EffectiveDownloadDirectory;
            _directoryError = string.Empty;

            try
            {
                directory = Path.GetFullPath(directory);
                Directory.CreateDirectory(directory);

                var testPath = Path.Combine(directory, $".aqr-write-test-{Guid.NewGuid():N}.tmp");
                using (File.Create(testPath))
                {
                }

                File.Delete(testPath);
                return true;
            }
            catch (Exception ex)
            {
                _directoryError = $"目录不可用：{ex.Message}";
                Main.Mod?.Logger.Warning(_directoryError);
                return false;
            }
        }

        private void BrowseDownloadDirectory()
        {
            var selected = FileBrowser.PickFolder(EffectiveDownloadDirectory, title: "选择谱面保存目录");
            if (!string.IsNullOrWhiteSpace(selected))
            {
                DownloadDirectory = selected;
                TryPrepareDownloadDirectory(out _);
            }
        }

        private void RevealDownloadDirectory()
        {
            if (!TryPrepareDownloadDirectory(out var directory))
            {
                return;
            }

            if (!FileBrowser.Reveal(directory))
            {
                _directoryError = "无法打开该目录。";
            }
        }

        private static string GetDefaultDownloadDirectory()
        {
            return GetDefaultDownloadDirectory(Main.Mod);
        }

        private static string GetDefaultDownloadDirectory(UnityModManager.ModEntry? modEntry)
        {
            var modPath = modEntry?.Path;
            return Path.Combine(
                string.IsNullOrWhiteSpace(modPath) ? AppDomain.CurrentDomain.BaseDirectory : modPath,
                "Downloads");
        }
    }
}
