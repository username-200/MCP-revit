using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace McpRevit.Util
{
    /// <summary>
    /// Сериализация геометрии наружу: внутри Revit — футы, в ответах — миллиметры.
    /// Здесь оставлено только то, чем пользуется детекция плоскостей.
    /// </summary>
    public static class Dto
    {
        public static Dictionary<string, object> Point(XYZ p) => new Dictionary<string, object>
        {
            ["x"] = UnitConv.FeetToMm(p.X),
            ["y"] = UnitConv.FeetToMm(p.Y),
            ["z"] = UnitConv.FeetToMm(p.Z)
        };

        public static Dictionary<string, object> Vector(XYZ v) => new Dictionary<string, object>
        {
            ["x"] = v.X,
            ["y"] = v.Y,
            ["z"] = v.Z
        };
    }
}
