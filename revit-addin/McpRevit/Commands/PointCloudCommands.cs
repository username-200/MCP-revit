using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using McpRevit.Geometry;
using McpRevit.Util;
using Newtonsoft.Json.Linq;

namespace McpRevit.Commands
{
    /// <summary>Подключение облаков точек, выборка точек и поиск плоскостей.</summary>
    public static class PointCloudCommands
    {
        private const int HardPointLimit = 500_000;

        public static void Register(CommandRegistry registry)
        {
            registry.Add("pointcloud.list", (app, p) =>
            {
                var doc = Params.Document(app);

                var clouds = new FilteredElementCollector(doc)
                    .OfClass(typeof(PointCloudInstance))
                    .Cast<PointCloudInstance>()
                    .Select(instance => new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.ToLong(instance.Id),
                        ["name"] = instance.Name,
                        ["type_id"] = RevitIds.ToLong(instance.GetTypeId()),
                        ["path"] = ResolvePath(doc, instance),
                        ["bounding_box"] = Dto.BoundingBox(instance.get_BoundingBox(null))
                    })
                    .ToList();

                return new Dictionary<string, object> { ["point_clouds"] = clouds };
            });

            registry.Add("pointcloud.link", (app, p) =>
            {
                var doc = Params.Document(app);
                var path = Params.String(p, "path");

                if (!File.Exists(path))
                    throw new CommandException("Файл облака точек не найден: " + path, "not_found");

                var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (extension != "rcp" && extension != "rcs")
                    throw new CommandException(
                        "Revit принимает только .rcp и .rcs. Исходные .e57/.las/.pts сконвертируйте " +
                        "в Autodesk ReCap.");

                var offset = UnitConv.PointFromMm(
                    Params.DoubleOr(p, "offset_x_mm", 0),
                    Params.DoubleOr(p, "offset_y_mm", 0),
                    Params.DoubleOr(p, "offset_z_mm", 0));
                var rotation = UnitConv.DegreesToRadians(Params.DoubleOr(p, "rotation_deg", 0));

                var transform = Transform.CreateTranslation(offset);
                if (Math.Abs(rotation) > 1e-12)
                    transform = transform * Transform.CreateRotation(XYZ.BasisZ, rotation);

                using (var tx = new Transaction(doc, "MCP: подключение облака точек"))
                {
                    tx.Start();
                    var type = PointCloudType.Create(doc, extension, path);
                    var instance = PointCloudInstance.Create(doc, type.Id, transform);
                    tx.Commit();

                    return new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.ToLong(instance.Id),
                        ["name"] = instance.Name,
                        ["path"] = path,
                        ["bounding_box"] = Dto.BoundingBox(instance.get_BoundingBox(null))
                    };
                }
            });

            registry.Add("pointcloud.sample", (app, p) =>
            {
                var doc = Params.Document(app);
                var instance = Params.Element<PointCloudInstance>(doc, p, "id");
                var maxPoints = Math.Min(Params.IntOr(p, "max_points", 20_000), HardPointLimit);

                var points = SamplePoints(instance, p, maxPoints, out var requestedDensityFt);

                return new Dictionary<string, object>
                {
                    ["count"] = points.Count,
                    ["average_distance_mm"] = UnitConv.FeetToMm(requestedDensityFt),
                    ["points"] = points.Select(Dto.Point).ToList()
                };
            });

            registry.Add("pointcloud.detect_planes", (app, p) =>
            {
                var doc = Params.Document(app);
                var instance = Params.Element<PointCloudInstance>(doc, p, "id");
                var maxPoints = Math.Min(Params.IntOr(p, "max_points", 40_000), HardPointLimit);
                var angleTolerance = Params.DoubleOr(p, "angle_tolerance_deg", 12.0);

                var options = new PlaneDetectorOptions
                {
                    Tolerance = UnitConv.MmToFeet(Params.DoubleOr(p, "distance_tolerance_mm", 25.0)),
                    MaxPlanes = Params.IntOr(p, "max_planes", 12),
                    MinInliers = Params.IntOr(p, "min_inliers", 200),
                    MinInlierRatio = Params.DoubleOr(p, "min_inlier_ratio", 0.02),
                    Trials = Params.IntOr(p, "trials", 300),
                    Seed = Params.IntOr(p, "seed", 20240101)
                };

                var points = SamplePoints(instance, p, maxPoints, out _);
                if (points.Count < 3)
                    throw new CommandException(
                        "Из облака получено меньше трёх точек — проверьте область выборки " +
                        "и плотность (average_distance_mm).", "empty_sample");

                var planes = PlaneDetector.Detect(points, options);
                var filter = Params.StringOr(p, "filter_kind", null);

                var dtos = planes
                    .Where(plane => filter == null ||
                                    string.Equals(plane.Kind(angleTolerance).ToString(), filter,
                                        StringComparison.OrdinalIgnoreCase))
                    .Select(plane => plane.ToDto(angleTolerance))
                    .ToList();

                return new Dictionary<string, object>
                {
                    ["sampled_points"] = points.Count,
                    ["plane_count"] = dtos.Count,
                    ["planes"] = dtos
                };
            });
        }

        /// <summary>
        /// Выборка точек в координатах модели. Область ограничивается либо габаритами облака,
        /// либо параметром 'box' = {min: {...}, max: {...}} в мм.
        /// </summary>
        private static List<XYZ> SamplePoints(
            PointCloudInstance instance, JObject p, int maxPoints, out double densityFt)
        {
            var toModel = instance.GetTransform();
            var toCloud = toModel.Inverse;

            var (minModel, maxModel) = ResolveBox(instance, p);
            var densityMm = Params.DoubleOr(p, "average_distance_mm", 0);
            densityFt = densityMm > 0
                ? UnitConv.MmToFeet(densityMm)
                : EstimateDensity(minModel, maxModel, maxPoints);

            var filter = BuildBoxFilter(toCloud.OfPoint(minModel), toCloud.OfPoint(maxModel));

            PointCollection collection;
            try
            {
                collection = instance.GetPoints(filter, densityFt, maxPoints);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new CommandException(
                    "Revit не смог прочитать точки: " + ex.Message +
                    ". Убедитесь, что файл ReCap доступен и облако не выгружено из проекта.",
                    "point_cloud_read_failed");
            }

            var result = new List<XYZ>(Math.Min(maxPoints, 1024));
            foreach (CloudPoint point in collection)
                result.Add(toModel.OfPoint(new XYZ(point.X, point.Y, point.Z)));

            return result;
        }

        private static (XYZ Min, XYZ Max) ResolveBox(PointCloudInstance instance, JObject p)
        {
            if (p["box"] is JObject box)
            {
                var min = Params.PointFrom((JObject)box["min"], "box.min");
                var max = Params.PointFrom((JObject)box["max"], "box.max");
                return (
                    new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
                    new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)));
            }

            var bounds = instance.get_BoundingBox(null);
            if (bounds == null)
                throw new CommandException(
                    "У облака точек нет габаритного контейнера — задайте область выборки параметром 'box'.");

            // Небольшой запас, чтобы точки на самой границе не отсекались фильтром.
            var margin = new XYZ(0.05, 0.05, 0.05);
            return (bounds.Transform.OfPoint(bounds.Min) - margin,
                    bounds.Transform.OfPoint(bounds.Max) + margin);
        }

        /// <summary>
        /// Шаг выборки подбираем так, чтобы запрошенное число точек примерно покрыло весь объём:
        /// иначе Revit вернёт плотный «пятачок» в углу облака вместо всей сцены.
        /// </summary>
        private static double EstimateDensity(XYZ min, XYZ max, int maxPoints)
        {
            var volume = Math.Max((max.X - min.X) * (max.Y - min.Y) * (max.Z - min.Z), 1e-6);
            var step = Math.Pow(volume / Math.Max(maxPoints, 1), 1.0 / 3.0);
            return Math.Max(step, UnitConv.MmToFeet(5.0));
        }

        private static PointCloudFilter BuildBoxFilter(XYZ min, XYZ max)
        {
            var lo = new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z));
            var hi = new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z));

            // Фильтр оставляет точки с положительной стороны каждой плоскости,
            // поэтому нормали шести граней смотрят внутрь параллелепипеда.
            var planes = new List<Plane>
            {
                Plane.CreateByNormalAndOrigin(XYZ.BasisX, lo),
                Plane.CreateByNormalAndOrigin(XYZ.BasisX.Negate(), hi),
                Plane.CreateByNormalAndOrigin(XYZ.BasisY, lo),
                Plane.CreateByNormalAndOrigin(XYZ.BasisY.Negate(), hi),
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, lo),
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ.Negate(), hi)
            };

            return PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
        }

        private static string ResolvePath(Document doc, PointCloudInstance instance)
        {
            try
            {
                var typeId = instance.GetTypeId();
                if (!ExternalFileUtils.IsExternalFileReference(doc, typeId))
                    return null;

                var reference = ExternalFileUtils.GetExternalFileReference(doc, typeId);
                return ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            }
            catch (Exception)
            {
                // Путь — справочная информация; недоступность ссылки не должна ронять команду.
                return null;
            }
        }
    }
}
