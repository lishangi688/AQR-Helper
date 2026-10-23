using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AQRLiteHelper.Bridge
{
    internal sealed class LocalBridgeServer : IDisposable
    {
        public const int FixedPort = 2770;

        private readonly BridgeRequestQueue _queue = new BridgeRequestQueue();
        private readonly SseEventHub _events = new SseEventHub();
        private readonly BridgeHandlers _handlers;
        private HttpListener? _listener;
        private Thread? _workerThread;
        private volatile bool _running;
        private HealthResponse _cachedHealth = new HealthResponse
        {
            connected = true,
            modVersion = "unknown",
            port = FixedPort
        };

        public LocalBridgeServer()
        {
            _handlers = new BridgeHandlers(_queue, _events);
        }

        public int Port => FixedPort;

        public bool Start()
        {
            if (_running)
            {
                return true;
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{FixedPort}/");
                _listener.Start();
                _running = true;

                _workerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "AQRHelperBridge"
                };
                _workerThread.Start();

                Main.Mod?.Logger.Log($"本地桥接服务已启动：http://127.0.0.1:{FixedPort}/");
                return true;
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Error($"本地桥接服务启动失败：{ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            _events.Dispose();

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // Best-effort shutdown.
            }

            _listener = null;
            _workerThread = null;
        }

        public void ProcessPending()
        {
            _queue.ProcessPending();
            _cachedHealth = _handlers.BuildHealth(FixedPort);
        }

        public void Dispose()
        {
            Stop();
        }

        private void ListenLoop()
        {
            while (_running && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    Task.Run(() => HandleRequest(context));
                }
                catch (HttpListenerException)
                {
                    if (!_running)
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Main.Mod?.Logger.Error($"本地桥接监听异常：{ex.Message}");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var origin = request.Headers["Origin"];

            try
            {
                if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    BridgeHandlers.WriteOptions(response, origin);
                    return;
                }

                var path = request.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;
                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    && path.Equals("/events", StringComparison.OrdinalIgnoreCase))
                {
                    await _events.HandleAsync(context, origin).ConfigureAwait(false);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    && path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    BridgeHandlers.WriteJson(response, 200, BridgeJson.Serialize(_cachedHealth), origin);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                    && path.Equals("/open-level", StringComparison.OrdinalIgnoreCase))
                {
                    var body = await ReadBodyAsync(request).ConfigureAwait(false);
                    var result = await _handlers.HandleOpenLevelAsync(body).ConfigureAwait(false);
                    BridgeHandlers.WriteJson(response, result.StatusCode, BridgeJson.Serialize(result.Body), origin);
                    return;
                }

                BridgeHandlers.WriteJson(
                    response,
                    404,
                    BridgeJson.Serialize(BridgeResponse.Fail("NOT_FOUND", "接口不存在。")),
                    origin);
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Error($"桥接请求处理失败：{ex.Message}");
                try
                {
                    BridgeHandlers.WriteJson(
                        response,
                        500,
                        BridgeJson.Serialize(BridgeResponse.Fail("SERVER_ERROR", "服务异常，请稍后重试。")),
                        origin);
                }
                catch
                {
                    // The client may have disconnected already.
                }
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch
                {
                    // Ignore closed responses.
                }
            }
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
