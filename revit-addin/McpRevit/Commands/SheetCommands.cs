using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using McpRevit.Util;
using Newtonsoft.Json.Linq;

namespace McpRevit.Commands
{
    /// <summary>Листы, размещение видов на листах и заполнение штампа.</summary>
    public static class SheetCommands
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Add("sheets.list", (app, p) =>
            {
                var doc = Params.Document(app);

                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .OrderBy(s => s.SheetNumber)
                    .Select(s => new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.ToLong(s.Id),
                        ["number"] = s.SheetNumber,
                        ["name"] = s.Name,
                        ["viewport_count"] = s.GetAllViewports().Count,
                        ["is_placeholder"] = s.IsPlaceholder
                    })
                    .ToList();

                return new Dictionary<string, object> { ["sheets"] = sheets };
            });

            registry.Add("sheets.create", (app, p) =>
            {
                var doc = Params.Document(app);
                var titleBlockId = ResolveTitleBlock(doc, Params.IdOr(p, "titleblock_id", -1));
                var number = Params.StringOr(p, "number", null);
                var name = Params.StringOr(p, "name", null);

                using (var tx = new Transaction(doc, "MCP: создание листа"))
                {
                    tx.Start();

                    var sheet = ViewSheet.Create(doc, titleBlockId);

                    if (!string.IsNullOrEmpty(number))
                    {
                        try { sheet.SheetNumber = number; }
                        catch (Autodesk.Revit.Exceptions.ArgumentException)
                        {
                            throw new CommandException("Лист с номером '" + number + "' уже существует.");
                        }
                    }

                    if (!string.IsNullOrEmpty(name))
                        sheet.Name = name;

                    var applied = ApplyParameters(sheet, p["parameters"] as JObject);
                    tx.Commit();

                    return new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.ToLong(sheet.Id),
                        ["number"] = sheet.SheetNumber,
                        ["name"] = sheet.Name,
                        ["parameters_set"] = applied
                    };
                }
            });

            registry.Add("sheets.place_view", (app, p) =>
            {
                var doc = Params.Document(app);
                var sheet = Params.Element<ViewSheet>(doc, p, "sheet_id");
                var view = Params.Element<View>(doc, p, "view_id");
                var center = Params.Point(p, "center");

                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    throw new CommandException(
                        "Вид '" + view.Name + "' нельзя разместить на листе '" + sheet.SheetNumber +
                        "': он уже размещён на другом листе либо не поддерживает размещение.");

                using (var tx = new Transaction(doc, "MCP: размещение вида на листе"))
                {
                    tx.Start();
                    var viewport = Viewport.Create(doc, sheet.Id, view.Id, center);

                    if (viewport == null)
                        throw new CommandException("Revit не создал видовой экран для вида " + view.Name + ".");

                    var typeId = Params.IdOr(p, "viewport_type_id", -1);
                    if (typeId >= 0)
                        viewport.ChangeTypeId(RevitIds.FromLong(typeId));

                    tx.Commit();

                    var outline = viewport.GetBoxOutline();
                    return new Dictionary<string, object>
                    {
                        ["viewport_id"] = RevitIds.ToLong(viewport.Id),
                        ["sheet_id"] = RevitIds.ToLong(sheet.Id),
                        ["view_id"] = RevitIds.ToLong(view.Id),
                        ["center"] = Dto.Point(viewport.GetBoxCenter()),
                        ["size_mm"] = new Dictionary<string, object>
                        {
                            ["width"] = Units.FeetToMm(outline.MaximumPoint.X - outline.MinimumPoint.X),
                            ["height"] = Units.FeetToMm(outline.MaximumPoint.Y - outline.MinimumPoint.Y)
                        }
                    };
                }
            });

            registry.Add("sheets.move_viewport", (app, p) =>
            {
                var doc = Params.Document(app);
                var viewport = Params.Element<Viewport>(doc, p, "viewport_id");
                var center = Params.Point(p, "center");

                using (var tx = new Transaction(doc, "MCP: перемещение видового экрана"))
                {
                    tx.Start();
                    viewport.SetBoxCenter(center);
                    tx.Commit();
                }

                return new Dictionary<string, object>
                {
                    ["viewport_id"] = RevitIds.ToLong(viewport.Id),
                    ["center"] = Dto.Point(viewport.GetBoxCenter())
                };
            });

            registry.Add("sheets.set_parameters", (app, p) =>
            {
                var doc = Params.Document(app);
                var sheet = Params.Element<ViewSheet>(doc, p, "sheet_id");

                if (!(p["parameters"] is JObject parameters))
                    throw new CommandException("Параметр 'parameters' должен быть объектом {имя: значение}.");

                using (var tx = new Transaction(doc, "MCP: заполнение штампа"))
                {
                    tx.Start();
                    var applied = ApplyParameters(sheet, parameters);
                    tx.Commit();

                    return new Dictionary<string, object>
                    {
                        ["sheet_id"] = RevitIds.ToLong(sheet.Id),
                        ["parameters_set"] = applied
                    };
                }
            });

            registry.Add("sheets.info", (app, p) =>
            {
                var doc = Params.Document(app);
                var sheet = Params.Element<ViewSheet>(doc, p, "sheet_id");

                var viewports = sheet.GetAllViewports()
                    .Select(id => (Viewport)doc.GetElement(id))
                    .Select(viewport => new Dictionary<string, object>
                    {
                        ["viewport_id"] = RevitIds.ToLong(viewport.Id),
                        ["view_id"] = RevitIds.ToLong(viewport.ViewId),
                        ["view_name"] = doc.GetElement(viewport.ViewId)?.Name,
                        ["center"] = Dto.Point(viewport.GetBoxCenter())
                    })
                    .ToList();

                return new Dictionary<string, object>
                {
                    ["id"] = RevitIds.ToLong(sheet.Id),
                    ["number"] = sheet.SheetNumber,
                    ["name"] = sheet.Name,
                    ["titleblock_size_mm"] = TitleBlockSize(doc, sheet),
                    ["viewports"] = viewports
                };
            });
        }

        /// <summary>Размер рамки листа — нужен, чтобы раскладывать виды в пределах формата.</summary>
        private static Dictionary<string, object> TitleBlockSize(Document doc, ViewSheet sheet)
        {
            var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();

            if (titleBlock == null) return null;

            var width = titleBlock.get_Parameter(BuiltInParameter.SHEET_WIDTH)?.AsDouble() ?? 0;
            var height = titleBlock.get_Parameter(BuiltInParameter.SHEET_HEIGHT)?.AsDouble() ?? 0;

            return new Dictionary<string, object>
            {
                ["width"] = Units.FeetToMm(width),
                ["height"] = Units.FeetToMm(height)
            };
        }

        private static List<string> ApplyParameters(Element element, JObject parameters)
        {
            var applied = new List<string>();
            if (parameters == null) return applied;

            foreach (var entry in parameters)
            {
                var parameter = element.LookupParameter(entry.Key);
                if (parameter == null || parameter.IsReadOnly)
                    continue;

                var value = entry.Value;
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        parameter.Set(value?.ToString() ?? "");
                        break;
                    case StorageType.Integer:
                        parameter.Set((int)value);
                        break;
                    case StorageType.Double:
                        // Числовые параметры листа задаются в мм — переводим во внутренние единицы.
                        parameter.Set(Units.MmToFeet((double)value));
                        break;
                    case StorageType.ElementId:
                        parameter.Set(RevitIds.FromLong((long)value));
                        break;
                    default:
                        continue;
                }

                applied.Add(entry.Key);
            }

            return applied;
        }

        private static ElementId ResolveTitleBlock(Document doc, long explicitId)
        {
            if (explicitId >= 0)
                return RevitIds.FromLong(explicitId);

            var titleBlock = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .FirstElementId();

            if (titleBlock == null || titleBlock == ElementId.InvalidElementId)
                throw new CommandException(
                    "В проекте нет ни одного семейства основной надписи. Загрузите рамку " +
                    "и повторите вызов, либо передайте 'titleblock_id'.", "not_found");

            return titleBlock;
        }
    }
}
