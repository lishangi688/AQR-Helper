using AQRLiteHelper.Bridge;
using UnityModManagerNet;

namespace AQRLiteHelper
{
    public static class Main
    {
        public static UnityModManager.ModEntry? Mod { get; private set; }

        public static Settings Settings { get; private set; } = null!;

        private static LocalBridgeServer? _bridgeServer;

        public static int BridgePort => _bridgeServer?.Port ?? 0;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            Settings = Settings.Load(modEntry);

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = Settings.OnGUI;
            modEntry.OnSaveGUI = Settings.OnSaveGUI;
            modEntry.OnUpdate = OnUpdate;

            modEntry.Logger.Log("Mod loaded / Mod 已加载");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value)
            {
                modEntry.Logger.Log("Mod enabled / Mod 已启用");
                StartBridge();
            }
            else
            {
                modEntry.Logger.Log("Mod disabled / Mod 已禁用");
                StopBridge();
            }

            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            _bridgeServer?.ProcessPending();
        }

        private static void StartBridge()
        {
            StopBridge();

            var server = new LocalBridgeServer();
            if (!server.Start())
            {
                Mod?.Logger.Error("本地桥接服务启动失败，请确认 2770 端口未被占用。");
                return;
            }

            _bridgeServer = server;
        }

        private static void StopBridge()
        {
            if (_bridgeServer == null)
            {
                return;
            }

            _bridgeServer.Dispose();
            _bridgeServer = null;
        }
    }
}
