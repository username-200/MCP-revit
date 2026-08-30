using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using McpRevit.Util;

namespace McpRevit.Geometry
{
    public enum PlaneKind
    {
        /// <summary>Нормаль близка к вертикали — пол, перекрытие, потолок.</summary>
        Horizontal,

        /// <summary>Нормаль близка к горизонтали — стена.</summary>
        Vertical,

        /// <summary>Всё остальное — скаты кровли, пандусы.</summary>
        Sloped
    }

    /// <summary>Плоскость, найденная в облаке точек. Координаты — внутренние (футы).</summary>
    public class DetectedPlane
    {
        public XYZ Normal;
        public XYZ Centroid;
        public int InlierCount;

        /// <summary>Габариты множества точек вдоль осей плоскости и по нормали.</summary>
        public double ExtentU;
        public double ExtentV;
        public double Thickness;

        public double MinZ;
        public double MaxZ;

        /// <summary>Для вертикальных плоскостей — след стены в плане (крайние точки по горизонтали).</summary>
        public XYZ TraceStart;
        public XYZ TraceEnd;

        /// <summary>Среднее расстояние точек до плоскости — мера качества подгонки.</summary>
        public double Rmse;

        public double SignedDistance(XYZ point) => Normal.DotProduct(point - Centroid);

        public PlaneKind Kind(double angleToleranceDegrees)
        {
            var cos = Math.Abs(Normal.Z);
            var tolerance = Math.Cos(UnitConv.DegreesToRadians(90.0 - angleToleranceDegrees));

            if (cos >= Math.Cos(UnitConv.DegreesToRadians(angleToleranceDegrees)))
                return PlaneKind.Horizontal;
            if (cos <= tolerance)
                return PlaneKind.Vertical;
            return PlaneKind.Sloped;
        }

        public Dictionary<string, object> ToDto(double angleToleranceDegrees)
        {
            var kind = Kind(angleToleranceDegrees);

            var dto = new Dictionary<string, object>
            {
                ["kind"] = kind.ToString().ToLowerInvariant(),
                ["normal"] = Dto.Vector(Normal),
                ["centroid"] = Dto.Point(Centroid),
                ["inlier_count"] = InlierCount,
                ["rmse_mm"] = UnitConv.FeetToMm(Rmse),
                ["extent_u_mm"] = UnitConv.FeetToMm(ExtentU),
                ["extent_v_mm"] = UnitConv.FeetToMm(ExtentV),
                ["min_z_mm"] = UnitConv.FeetToMm(MinZ),
                ["max_z_mm"] = UnitConv.FeetToMm(MaxZ),
                ["elevation_mm"] = UnitConv.FeetToMm(Centroid.Z),
                // Угол поворота следа стены в плане — удобно для проверки ортогональности обмера.
                ["heading_deg"] = TraceStart == null
                    ? (object)null
                    : UnitConv.RadiansToDegrees(Math.Atan2(
                        TraceEnd.Y - TraceStart.Y, TraceEnd.X - TraceStart.X))
            };

            if (TraceStart != null)
            {
                dto["trace"] = new Dictionary<string, object>
                {
                    ["start"] = Dto.Point(TraceStart),
                    ["end"] = Dto.Point(TraceEnd),
                    ["length_mm"] = UnitConv.FeetToMm(TraceStart.DistanceTo(TraceEnd))
                };
            }

            return dto;
        }
    }
}
