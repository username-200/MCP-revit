using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using McpRevit.Commands;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpRevit.Server
{
    /// <summary>
    /// Локальный HTTP-мост. Слушает только 127.0.0.1 и принимает команды из MCP-сервера.
    ///
    ///   GET  /health          — проверка живости, без авторизации;
    ///   POST /command         — {"command": "...", "params": {...}, "timeout_sec": 120}
    ///
    /// Ответ: {"ok": true, "result": ...} либо {"ok": false, "error": {"type": "...", "message": "..."}}
    /// </summary>
    public class HttpBridgeServer
    {
        private readonly RevitTaskRunner _runner;
        private readonly CommandRegistry _registry;
        private readonly string _token;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        public int Port { get; }
        public bool IsRunning => _running;

        public HttpBridgeServer(RevitTaskRunner runner, CommandRegistry registry, int port, string token)
        {
            _runner = runner;
            _registry = registry;
            Port = port;
            _token = token;
        }

        public void Start()
        {
            if (_running) return;

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            _listener.Start();
            _running = true;

            _thread = new Thread(Loop) { IsBackground = true, Name = "McpRevitHttp" };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener?.Stop(); } catch { /* сервер уже остановлен */ }
            try { _listener?.Close(); } catch { /* сервер уже закрыт */ }
            _listener = null;
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch (Exception)
                {
                    // Штатная остановка слушателя либо разрыв соединения.
                    if (!_running) return;
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');

                if (path == "/health" && ctx.Request.HttpMethod == "GET")
                {
                    WriteJson(ctx, 200, new JObject
                    {
                        ["ok"] = true,
                        ["result"] = new JObject
                        {
                            ["status"] = "ready",
                            ["version"] = App.Version,
                            ["auth_required"] = !string.IsNullOrEmpty(_token)
                        }
                    });
                    return;
                }

                if (!string.IsNullOrEmpty(_token) && ctx.Request.Headers["X-Mcp-Token"] != _token)
                {
                    WriteError(ctx, 401, "unauthorized", "Неверный или отсутствующий заголовок X-Mcp-Token.");
                    return;
                }

                if (path == "/commands" && ctx.Request.HttpMethod == "GET")
                {
                    WriteJson(ctx, 200, new JObject
                    {
                        ["ok"] = true,
                        ["result"] = new JObject { ["commands"] = JArray.FromObject(_registry.Names) }
                    });
                    return;
                }

                if (path != "/command" || ctx.Request.HttpMethod != "POST")
                {
                    WriteError(ctx, 404, "not_found", "Неизвестный маршрут: " + ctx.Request.HttpMethod + " " + path);
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = reader.ReadToEnd();

                JObject request;
                try
                {
                    request = JObject.Parse(body);
                }
                catch (JsonException ex)
                {
                    WriteError(ctx, 400, "bad_json", "Тело запроса не является корректным JSON: " + ex.Message);
                    return;
                }

                var command = (string)request["command"];
                if (string.IsNullOrWhiteSpace(command))
                {
                    WriteError(ctx, 400, "bad_request", "Не задано поле 'command'.");
                    return;
                }

                var parameters = request["params"] as JObject ?? new JObject();
                var timeout = TimeSpan.FromSeconds((double?)request["timeout_sec"] ?? 120.0);

                try
                {
                    var result = _runner.Run(app => _registry.Invoke(command, app, parameters), timeout);
                    WriteJson(ctx, 200, new JObject
                    {
                        ["ok"] = true,
                        ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result)
                    });
                }
                catch (TimeoutException ex)
                {
                    WriteError(ctx, 504, "timeout", ex.Message);
                }
                catch (CommandException ex)
                {
                    WriteError(ctx, 400, ex.Kind, ex.Message);
                }
                catch (Exception ex)
                {
                    WriteError(ctx, 500, "revit_error", ex.GetType().Name + ": " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                try { WriteError(ctx, 500, "internal", ex.Message); } catch { /* клиент отключился */ }
            }
        }

        private static void WriteError(HttpListenerContext ctx, int status, string type, string message)
        {
            WriteJson(ctx, status, new JObject
            {
                ["ok"] = false,
                ["error"] = new JObject { ["type"] = type, ["message"] = message }
            });
        }

        private static void WriteJson(HttpListenerContext ctx, int status, JObject payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
