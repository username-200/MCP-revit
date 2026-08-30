using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace McpRevit.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleServerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var server = App.Server;
            if (server == null)
            {
                message = "Мост не инициализирован. " + (App.LastError ?? "");
                return Result.Failed;
            }

            if (server.IsRunning)
            {
                server.Stop();
                TaskDialog.Show("MCP Revit Bridge", "Мост остановлен.");
            }
            else
            {
                server.Start();
                TaskDialog.Show("MCP Revit Bridge", "Мост слушает http://127.0.0.1:" + server.Port);
            }

            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class StatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var server = App.Server;
            var registry = new CommandRegistry();

            var text =
                "Версия: " + App.Version + "\n" +
                "Состояние: " + (server != null && server.IsRunning ? "работает" : "остановлен") + "\n" +
                "Адрес: http://127.0.0.1:" + (server?.Port.ToString() ?? "—") + "\n" +
                "Команд зарегистрировано: " + registry.Names.Count;

            TaskDialog.Show("MCP Revit Bridge", text);
            return Result.Succeeded;
        }
    }
}
