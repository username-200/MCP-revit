using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using McpRevit.Commands;
using McpRevit.Server;
using Newtonsoft.Json.Linq;

namespace McpRevit
{
    /// <summary>Точка входа аддина: поднимает мост между Revit и MCP-сервером.</summary>
    public class App : IExternalApplication
    {
        public const string Version = "1.0.0";

        public static HttpBridgeServer Server { get; private set; }
        public static RevitTaskRunner Runner { get; private set; }

        private static string _lastError;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                Runner = new RevitTaskRunner();
                Runner.Initialize();

                var config = LoadConfig();
                Server = new HttpBridgeServer(
                    Runner,
                    new CommandRegistry(),
                    port: config.Port,
                    token: config.Token);

                BuildRibbon(application);

                if (config.AutoStart)
                    Server.Start();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                TaskDialog.Show("MCP Revit Bridge", "Не удалось запустить мост:\n" + ex.Message);
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            Server?.Stop();
            return Result.Succeeded;
        }

        public static string LastError => _lastError;

        private static void BuildRibbon(UIControlledApplication application)
        {
            const string tab = "MCP";
            try { application.CreateRibbonTab(tab); }
            catch (Exception) { /* вкладка уже создана другим аддином */ }

            var panel = application.CreateRibbonPanel(tab, "Мост");
            var assembly = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData(
                "McpToggleServer", "Старт /\nСтоп",
                assembly, typeof(ToggleServerCommand).FullName)
            {
                ToolTip = "Запустить или остановить локальный HTTP-мост для MCP-сервера."
            });

            panel.AddItem(new PushButtonData(
                "McpStatus", "Статус",
                assembly, typeof(StatusCommand).FullName)
            {
                ToolTip = "Показать состояние моста: порт, авторизация, число команд."
            });
        }

        private class BridgeConfig
        {
            public int Port = 8765;
            public string Token = "";
            public bool AutoStart = true;
        }

        /// <summary>
        /// Настройки берутся из mcp-revit.config.json рядом со сборкой,
        /// затем из переменных окружения MCPREVIT_PORT / MCPREVIT_TOKEN / MCPREVIT_AUTOSTART.
        /// </summary>
        private static BridgeConfig LoadConfig()
        {
            var config = new BridgeConfig();

            var path = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                "mcp-revit.config.json");

            if (File.Exists(path))
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(path));
                    config.Port = (int?)json["port"] ?? config.Port;
                    config.Token = (string)json["token"] ?? config.Token;
                    config.AutoStart = (bool?)json["auto_start"] ?? config.AutoStart;
                }
                catch (Exception ex)
                {
                    _lastError = "mcp-revit.config.json прочитать не удалось: " + ex.Message;
                }
            }

            var envPort = Environment.GetEnvironmentVariable("MCPREVIT_PORT");
            if (int.TryParse(envPort, out var parsedPort))
                config.Port = parsedPort;

            var envToken = Environment.GetEnvironmentVariable("MCPREVIT_TOKEN");
            if (!string.IsNullOrEmpty(envToken))
                config.Token = envToken;

            var envAutoStart = Environment.GetEnvironmentVariable("MCPREVIT_AUTOSTART");
            if (bool.TryParse(envAutoStart, out var parsedAutoStart))
                config.AutoStart = parsedAutoStart;

            return config;
        }
    }
}
