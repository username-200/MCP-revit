using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using McpRevit.Util;
using Newtonsoft.Json.Linq;

namespace McpRevit.Commands
{
    /// <summary>Создание планов, разрезов, фасадов и 3D-видов.</summary>
    public static class ViewCommands
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Add("views.list", (app, p) =>
            {
                var doc = Params.Document(app);
                var includeTemplates = Params.BoolOr(p, "include_templates", false);
                var placeableOnly = Params.BoolOr(p, "placeable_only", false);

                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => includeTemplates || !v.IsTemplate)
                    .Where(v => !placeableOnly || (!v.IsTemplate && v.ViewType != ViewType.Internal))
                    .OrderBy(v => v.ViewType.ToString())
                    .ThenBy(v => v.Name)
                    .Select(Dto.View)
                    .ToList();

                return new Dictionary<string, object> { ["views"] = views };
            });

            registry.Add("views.create_plan", (app, p) =>
            {
                var doc = Params.Document(app);
                var level = Params.Element<Level>(doc, p, "level_id");
                var family = ParseViewFamily(Params.StringOr(p, "view_family", "FloorPlan"));
                var typeId = FindViewFamilyType(doc, family, Params.IdOr(p, "view_family_type_id", -1));

                using (var tx = new Transaction(doc, "MCP: создание плана"))
                {
                    tx.Start();
                    var view = ViewPlan.Create(doc, typeId, level.Id);
                    ApplyCommonSettings(doc, view, p);
                    tx.Commit();
                    return Dto.View(view);
                }
            });

            registry.Add("views.create_section", (app, p) =>
            {
                var doc = Params.Document(app);
                // Фасад — тот же секущий контейнер, отличается только семейство вида.
                var family = ParseViewFamily(Params.StringOr(p, "view_family", "Section"));
                var typeId = FindViewFamilyType(doc, family, Params.IdOr(p, "view_family_type_id", -1));

                var origin = Params.Point(p, "origin");
                var direction = Params.Direction(p, "direction");
                var widthFt = Units.MmToFeet(Params.DoubleOr(p, "width_mm", 10000));
                var heightFt = Units.MmToFeet(Params.DoubleOr(p, "height_mm", 4000));
                var depthFt = Units.MmToFeet(Params.DoubleOr(p, "depth_mm", 10000));

                var box = BuildSectionBox(origin, direction, widthFt, heightFt, depthFt);

                using (var tx = new Transaction(doc, "MCP: создание разреза"))
                {
                    tx.Start();
                    var view = ViewSection.CreateSection(doc, typeId, box);
                    ApplyCommonSettings(doc, view, p);
                    tx.Commit();
                    return Dto.View(view);
                }
            });

            registry.Add("views.create_3d", (app, p) =>
            {
                var doc = Params.Document(app);
                var typeId = FindViewFamilyType(doc, ViewFamily.ThreeDimensional,
                    Params.IdOr(p, "view_family_type_id", -1));

                using (var tx = new Transaction(doc, "MCP: создание 3D-вида"))
                {
                    tx.Start();
                    var view = Params.BoolOr(p, "perspective", false)
                        ? View3D.CreatePerspective(doc, typeId)
                        : View3D.CreateIsometric(doc, typeId);

                    ApplyCommonSettings(doc, view, p);
                    tx.Commit();
                    return Dto.View(view);
                }
            });

            registry.Add("views.update", (app, p) =>
            {
                var doc = Params.Document(app);
                var view = Params.Element<View>(doc, p, "view_id");

                using (var tx = new Transaction(doc, "MCP: настройка вида"))
                {
                    tx.Start();
                    ApplyCommonSettings(doc, view, p);
                    tx.Commit();
                    return Dto.View(view);
                }
            });

            registry.Add("views.templates.list", (app, p) =>
            {
                var doc = Params.Document(app);
                var templates = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.IsTemplate)
                    .Select(v => new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.ToLong(v.Id),
                        ["name"] = v.Name,
                        ["view_type"] = v.ViewType.ToString()
                    })
                    .OrderBy(v => (string)v["name"])
                    .ToList();

                return new Dictionary<string, object> { ["view_templates"] = templates };
            });
        }

        /// <summary>Имя, масштаб, шаблон вида и подрезка — общая часть для всех типов видов.</summary>
        private static void ApplyCommonSettings(Document doc, View view, JObject p)
        {
            var name = Params.StringOr(p, "name", null);
            if (!string.IsNullOrEmpty(name))
            {
                try { view.Name = name; }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    throw new CommandException("Вид с именем '" + name + "' уже существует.");
                }
            }

            var templateId = Params.IdOr(p, "view_template_id", -1);
            if (templateId >= 0)
                view.ViewTemplateId = RevitIds.FromLong(templateId);

            // Масштаб задаём после шаблона: шаблон может перекрыть значение,
            // но явно переданный масштаб важнее, если параметр не заблокирован.
            var scale = Params.IntOr(p, "scale", 0);
            if (scale > 0)
            {
                var parameter = view.get_Parameter(BuiltInParameter.VIEW_SCALE);
                if (parameter != null && !parameter.IsReadOnly)
                    view.Scale = scale;
            }

            if (p["detail_level"] != null)
            {
                var text = (string)p["detail_level"];
                if (Enum.TryParse<ViewDetailLevel>(text, true, out var level))
                    view.DetailLevel = level;
            }
        }

        /// <summary>
        /// Секущий контейнер разреза. BasisZ смотрит на наблюдателя, поэтому взгляд —
        /// вдоль -BasisZ, а глубина откладывается по +Z в локальных координатах.
        /// </summary>
        private static BoundingBoxXYZ BuildSectionBox(
            XYZ origin, XYZ viewDirection, double widthFt, double heightFt, double depthFt)
        {
            var basisZ = viewDirection.Negate().Normalize();
            var up = Math.Abs(basisZ.Z) > 0.99 ? XYZ.BasisY : XYZ.BasisZ;
            var basisX = up.CrossProduct(basisZ).Normalize();
            var basisY = basisZ.CrossProduct(basisX).Normalize();

            var transform = Transform.Identity;
            transform.Origin = origin;
            transform.BasisX = basisX;
            transform.BasisY = basisY;
            transform.BasisZ = basisZ;

            return new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-widthFt / 2, -heightFt / 2, 0),
                Max = new XYZ(widthFt / 2, heightFt / 2, depthFt)
            };
        }

        private static ViewFamily ParseViewFamily(string text)
        {
            if (Enum.TryParse<ViewFamily>(text, true, out var family))
                return family;

            throw new CommandException(
                "Неизвестное семейство видов '" + text + "'. Например: FloorPlan, CeilingPlan, " +
                "Section, Elevation, ThreeDimensional.");
        }

        private static ElementId FindViewFamilyType(Document doc, ViewFamily family, long explicitId)
        {
            if (explicitId >= 0)
                return RevitIds.FromLong(explicitId);

            var type = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == family);

            if (type == null)
                throw new CommandException(
                    "В проекте нет типоразмера вида для семейства " + family +
                    ". Проверьте шаблон проекта.", "not_found");

            return type.Id;
        }
    }
}
