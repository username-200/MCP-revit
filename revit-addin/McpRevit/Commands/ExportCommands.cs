using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using McpRevit.Util;
using Newtonsoft.Json.Linq;

namespace McpRevit.Commands
{
    /// <summary>Выпуск чертежей: PDF и DWG.</summary>
    public static class ExportCommands
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Add("export.pdf", (app, p) =>
            {
                var doc = Params.Document(app);
                var folder = PrepareFolder(Params.String(p, "folder"));
                var sheetIds = ResolveSheets(doc, p);

                var options = new PDFExportOptions
                {
                    Combine = Params.BoolOr(p, "combine", true),
                    HideCropBoundaries = Params.BoolOr(p, "hide_crop_boundaries", true),
                    HideScopeBoxes = Params.BoolOr(p, "hide_scope_boxes", true),
                    HideReferencePlane = Params.BoolOr(p, "hide_reference_planes", true),
                    HideUnreferencedViewTags = Params.BoolOr(p, "hide_unreferenced_view_tags", true),
                    StopOnError = false
                };

                var fileName = Params.StringOr(p, "file_name", null);
                if (!string.IsNullOrEmpty(fileName))
                {
                    if (!options.Combine)
                        throw new CommandException(
                            "Имя файла задаётся только при combine=true: при раздельном экспорте " +
                            "имена формируются Revit по номерам листов.");
                    options.FileName = Path.GetFileNameWithoutExtension(fileName);
                }

                var before = SnapshotFiles(folder, "*.pdf");

                if (!doc.Export(folder, sheetIds, options))
                    throw new CommandException("Revit сообщил об ошибке при экспорте в PDF.", "export_failed");

                return new Dictionary<string, object>
                {
                    ["format"] = "pdf",
                    ["folder"] = folder,
                    ["sheet_count"] = sheetIds.Count,
                    ["files"] = NewFiles(folder, "*.pdf", before)
                };
            });

            registry.Add("export.dwg", (app, p) =>
            {
                var doc = Params.Document(app);
                var folder = PrepareFolder(Params.String(p, "folder"));
                var sheetIds = ResolveSheets(doc, p);

                var options = ResolveDwgOptions(doc, Params.StringOr(p, "setup_name", null));
                options.MergedViews = Params.BoolOr(p, "merged_views", true);

                var prefix = Params.StringOr(p, "prefix", doc.Title);
                var before = SnapshotFiles(folder, "*.dwg");

                if (!doc.Export(folder, prefix, sheetIds, options))
                    throw new CommandException("Revit сообщил об ошибке при экспорте в DWG.", "export_failed");

                return new Dictionary<string, object>
                {
                    ["format"] = "dwg",
                    ["folder"] = folder,
                    ["sheet_count"] = sheetIds.Count,
                    ["files"] = NewFiles(folder, "*.dwg", before)
                };
            });
        }

        private static DWGExportOptions ResolveDwgOptions(Document doc, string setupName)
        {
            if (string.IsNullOrEmpty(setupName))
                return new DWGExportOptions();

            var options = DWGExportOptions.GetPredefinedOptions(doc, setupName);
            if (options == null)
            {
                // Сохранённые наборы экспорта живут в документе как элементы.
                var available = new FilteredElementCollector(doc)
                    .OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>()
                    .Select(s => s.Name)
                    .ToList();
                throw new CommandException(
                    "Настройка экспорта DWG '" + setupName + "' не найдена. Доступные: " +
                    (available.Count == 0 ? "нет сохранённых настроек" : string.Join(", ", available)),
                    "not_found");
            }

            return options;
        }

        /// <summary>Листы задаются явными id либо номерами; без параметров — все листы проекта.</summary>
        private static List<ElementId> ResolveSheets(Document doc, JObject p)
        {
            if (p["sheet_ids"] is JArray)
            {
                var ids = Params.IdList(p, "sheet_ids").Select(RevitIds.FromLong).ToList();
                foreach (var id in ids)
                {
                    if (!(doc.GetElement(id) is ViewSheet))
                        throw new CommandException(
                            "Элемент " + RevitIds.ToLong(id) + " не является листом.");
                }

                return ids;
            }

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .ToList();

            if (p["sheet_numbers"] is JArray numbersToken)
            {
                var wanted = numbersToken.Select(t => (string)t).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var matched = sheets.Where(s => wanted.Contains(s.SheetNumber)).ToList();

                var missing = wanted.Except(matched.Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (missing.Count > 0)
                    throw new CommandException(
                        "Листы с номерами не найдены: " + string.Join(", ", missing), "not_found");

                sheets = matched;
            }

            if (sheets.Count == 0)
                throw new CommandException("В проекте нет листов для экспорта.", "not_found");

            return sheets.OrderBy(s => s.SheetNumber).Select(s => s.Id).ToList();
        }

        private static string PrepareFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                throw new CommandException(
                    "Не удалось создать папку выгрузки '" + folder + "': " + ex.Message);
            }

            return Path.GetFullPath(folder);
        }

        private static HashSet<string> SnapshotFiles(string folder, string pattern) =>
            new HashSet<string>(Directory.GetFiles(folder, pattern), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Revit не сообщает, какие файлы создал, поэтому сравниваем содержимое папки
        /// до и после экспорта.
        /// </summary>
        private static List<string> NewFiles(string folder, string pattern, HashSet<string> before) =>
            Directory.GetFiles(folder, pattern)
                .Where(file => !before.Contains(file))
                .OrderBy(file => file)
                .ToList();
    }
}
