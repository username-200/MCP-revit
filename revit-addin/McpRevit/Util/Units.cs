using System;
using Autodesk.Revit.DB;

namespace McpRevit.Util
{
    /// <summary>
    /// Внутренние единицы Revit — десятичные футы. Наружу отдаём миллиметры,
    /// чтобы MCP-клиенту не приходилось знать про футы.
    /// </summary>
    public static class Units
    {
        public const double MmPerFoot = 304.8;

        public static double MmToFeet(double mm) => mm / MmPerFoot;

        public static double FeetToMm(double feet) => feet * MmPerFoot;

        public static double SqFeetToSqM(double sqFeet) => sqFeet * 0.09290304;

        public static XYZ PointFromMm(double x, double y, double z) =>
            new XYZ(MmToFeet(x), MmToFeet(y), MmToFeet(z));

        public static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

        public static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
    }
}
