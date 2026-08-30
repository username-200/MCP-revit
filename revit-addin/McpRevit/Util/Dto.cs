using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace McpRevit.Util
{
    /// <summary>Приведение объектов Revit к простым словарям для JSON-ответа.</summary>
    public static class Dto
    {
        public static Dictionary<string, object> Point(XYZ p) => new Dictionary<string, object>
        {
            ["x"] = Units.FeetToMm(p.X),
            ["y"] = Units.FeetToMm(p.Y),
            ["z"] = Units.FeetToMm(p.Z)
        };

        public static Dictionary<string, object> Vector(XYZ v) => new Dictionary<string, object>
        {
            ["x"] = v.X,
            ["y"] = v.Y,
            ["z"] = v.Z
        };

        public static Dictionary<string, object> Element(Element e) => new Dictionary<string, object>
        {
            ["id"] = RevitIds.ToLong(e.Id),
            ["name"] = e.Name,
            ["category"] = e.Category?.Name,
            ["type"] = e.GetType().Name
        };

        public static Dictionary<string, object> Level(Level level) => new Dictionary<string, object>
        {
            ["id"] = RevitIds.ToLong(level.Id),
            ["name"] = level.Name,
            ["elevation_mm"] = Units.FeetToMm(level.Elevation)
        };

        public static Dictionary<string, object> View(View view) => new Dictionary<string, object>
        {
            ["id"] = RevitIds.ToLong(view.Id),
            ["name"] = view.Name,
            ["view_type"] = view.ViewType.ToString(),
            ["scale"] = view.Scale,
            ["is_template"] = view.IsTemplate,
            ["can_be_placed"] = !view.IsTemplate && view.ViewType != ViewType.Internal
        };

        public static Dictionary<string, object> BoundingBox(BoundingBoxXYZ box)
        {
            if (box == null) return null;

            return new Dictionary<string, object>
            {
                ["min"] = Point(box.Transform.OfPoint(box.Min)),
                ["max"] = Point(box.Transform.OfPoint(box.Max)),
                ["size_mm"] = new Dictionary<string, object>
                {
                    ["x"] = Units.FeetToMm(box.Max.X - box.Min.X),
                    ["y"] = Units.FeetToMm(box.Max.Y - box.Min.Y),
                    ["z"] = Units.FeetToMm(box.Max.Z - box.Min.Z)
                }
            };
        }
    }
}
