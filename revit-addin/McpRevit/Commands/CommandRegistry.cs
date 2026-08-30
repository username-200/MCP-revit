using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace McpRevit.Commands
{
    public delegate object CommandHandler(UIApplication app, JObject parameters);

    /// <summary>Реестр команд моста. Имена совпадают с тем, что вызывает MCP-сервер.</summary>
    public class CommandRegistry
    {
        private readonly Dictionary<string, CommandHandler> _handlers =
            new Dictionary<string, CommandHandler>(StringComparer.OrdinalIgnoreCase);

        public CommandRegistry()
        {
            DocumentCommands.Register(this);
            PointCloudCommands.Register(this);
            ModelCommands.Register(this);
            ViewCommands.Register(this);
            SheetCommands.Register(this);
            ExportCommands.Register(this);
        }

        public void Add(string name, CommandHandler handler) => _handlers[name] = handler;

        public IReadOnlyList<string> Names => _handlers.Keys.OrderBy(x => x).ToList();

        public object Invoke(string name, UIApplication app, JObject parameters)
        {
            if (!_handlers.TryGetValue(name, out var handler))
                throw new CommandException(
                    "Неизвестная команда '" + name + "'. Доступные: " + string.Join(", ", Names),
                    "unknown_command");

            return handler(app, parameters);
        }
    }
}
