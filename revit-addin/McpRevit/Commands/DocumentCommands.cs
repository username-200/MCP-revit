using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using McpRevit.Util;

namespace McpRevit.Commands
{
    /// <summary>Общие сведения о документе, уровни, типоразмеры, сохранение.</summary>
    public static class DocumentCommands
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Add("ping", (app, p) => new Dictionary<string, object>
            {
                ["pong"] = true,
                ["version"] = App.Version,
                ["revit_version"] = app.Application.VersionNumber,
                ["revit_build"] = app.Application.VersionBuild
            });

            registry.Add("document.info", (app, p) =>
            {
                var doc = Params.Document(app);
                return new Dictionary<string, object>
                {
                    ["title"] = doc.Title,
                    ["path"] = doc.PathName,
                    ["is_workshared"] = doc.IsWorkshared,
                    ["is_modified"] = doc.IsModified,
                    ["active_view"] = doc.ActiveView == null ? null : Dto.View(doc.ActiveView),
                    ["level_count"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount(),
                    ["point_cloud_count"] = new FilteredElementCollector(doc)
                        .OfClass(typeof(PointCloudInstance)).GetElementCount()
                };
            });

            registry.Add("document.save", (app, p) =>
            {
                var doc = Params.Document(app);
                var path = Params.StringOr(p, "path", null);

                if (string.IsNullOrEmpty(path))
                {
                    if (string.IsNullOrEmpty(doc.PathName))
                        throw new CommandException(
                            "Документ ещё ни разу не сохранялся — укажите параметр 'path'.");
                    doc.Save();
                }
                else
                {
                    doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                }

                return new Dictionary<string, object> { ["path"] = doc.PathName };
            });

            registry.Add("levels.list", (app, p) =>
            {
                var doc = Params.Document(app);
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(Dto.Level)
                    .ToList();

                return new Dictionary<string, object> { ["levels"] = levels };
            });

            registry.Add("levels.create", (app, p) =>
            {
                var doc = Params.Document(app);
                var name = Params.StringOr(p, "name", null);
                var elevationFt = Units.MmToFeet(Params.Double(p, "elevation_mm"));

                using (var tx = new Transaction(doc, "MCP: создание уровня"))
                {
                    tx.Start();
                    var level = Level.Create(doc, elevationFt);

                    if (!string.IsNullOrEmpty(name))
                    {
                        try { level.Name = name; }
                        catch (Autodesk.Revit.Exceptions.ArgumentException)
                        {
                            throw new CommandException("Уровень с именем '" + name + "' уже существует.");
                        }
                    }

                    tx.Commit();
                    return Dto.Level(level);
                }
            });

            // Типоразмеры нужны, чтобы MCP-клиент мог выбрать конкретный тип стены/перекрытия
            // до вызова команд построения.
            registry.Add("types.list", (app, p) =>
            {
                var doc = Params.Document(app);
                var kind = Params.StringOr(p, "kind", "wall").ToLowerInvariant();

                var types = CollectTypes(doc, kind)
                    .Select(t => new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.ToLong(t.Id),
                        ["name"] = t.Name,
                        ["family"] = FamilyNameOf(t)
                    })
                    .OrderBy(t => (string)t["family"])
                    .ThenBy(t => (string)t["name"])
                    .ToList();

                return new Dictionary<string, object> { ["kind"] = kind, ["types"] = types };
            });

            registry.Add("elements.delete", (app, p) =>
            {
                var doc = Params.Document(app);
                var ids = Params.IdList(p, "ids").Select(RevitIds.FromLong).ToList();

                using (var tx = new Transaction(doc, "MCP: удаление элементов"))
                {
                    tx.Start();
                    var deleted = doc.Delete(ids);
                    tx.Commit();
                    return new Dictionary<string, object> { ["deleted_count"] = deleted.Count };
                }
            });
        }

        private static IEnumerable<ElementType> CollectTypes(Document doc, string kind)
        {
            switch (kind)
            {
                case "wall":
                    return new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<ElementType>();
                case "floor":
                    return new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<ElementType>();
                case "roof":
                    return new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<ElementType>();
                case "ceiling":
                    return new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<ElementType>();
                case "titleblock":
                    return new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsElementType()
                        .Cast<ElementType>();
                case "column":
                    return new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralColumns)
                        .WhereElementIsElementType()
                        .Cast<ElementType>();
                case "viewfamilytype":
                    return new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ElementType>();
                default:
                    throw new CommandException(
                        "Неизвестный вид типоразмеров '" + kind + "'. Допустимо: " +
                        "wall, floor, roof, ceiling, column, titleblock, viewfamilytype.");
            }
        }

        private static string FamilyNameOf(ElementType type)
        {
            if (type is FamilySymbol symbol)
                return symbol.FamilyName;
            if (type is ViewFamilyType viewFamilyType)
                return viewFamilyType.ViewFamily.ToString();
            return type.FamilyName;
        }
    }
}
