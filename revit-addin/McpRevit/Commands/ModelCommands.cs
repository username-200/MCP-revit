using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using McpRevit.Util;
using Newtonsoft.Json.Linq;

namespace McpRevit.Commands
{
    /// <summary>Построение модели: стены по осевым линиям и по найденным плоскостям, перекрытия.</summary>
    public static class ModelCommands
    {
        public static void Register(CommandRegistry registry)
        {
            registry.Add("walls.create", (app, p) =>
            {
                var doc = Params.Document(app);
                var level = Params.Element<Level>(doc, p, "level_id");
                var wallTypeId = ResolveWallType(doc, p);
                var heightFt = Units.MmToFeet(Params.DoubleOr(p, "height_mm", 3000));
                var baseOffsetFt = Units.MmToFeet(Params.DoubleOr(p, "base_offset_mm", 0));
                var structural = Params.BoolOr(p, "structural", false);

                var segments = new List<(XYZ Start, XYZ End)>();
                foreach (var token in Params.Array(p, "segments"))
                {
                    var segment = (JObject)token;
                    segments.Add((
                        Params.PointFrom((JObject)segment["start"], "segments[].start"),
                        Params.PointFrom((JObject)segment["end"], "segments[].end")));
                }

                return CreateWalls(doc, segments, wallTypeId, level, heightFt, baseOffsetFt, structural);
            });

            registry.Add("walls.from_planes", (app, p) =>
            {
                var doc = Params.Document(app);
                var level = Params.Element<Level>(doc, p, "level_id");
                var wallTypeId = ResolveWallType(doc, p);
                var structural = Params.BoolOr(p, "structural", false);

                var minLengthFt = Units.MmToFeet(Params.DoubleOr(p, "min_length_mm", 500));
                var snapAngle = Params.DoubleOr(p, "snap_angle_deg", 0);
                var fixedHeightMm = Params.DoubleOr(p, "height_mm", 0);

                var segments = new List<(XYZ Start, XYZ End)>();
                var heights = new List<double>();
                var skipped = 0;

                foreach (var token in Params.Array(p, "planes"))
                {
                    var plane = (JObject)token;

                    var kind = (string)plane["kind"];
                    if (kind != null && !string.Equals(kind, "vertical", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    if (!(plane["trace"] is JObject trace))
                    {
                        skipped++;
                        continue;
                    }

                    var start = Params.PointFrom((JObject)trace["start"], "planes[].trace.start");
                    var end = Params.PointFrom((JObject)trace["end"], "planes[].trace.end");

                    var baseZ = Units.MmToFeet((double)(plane["min_z_mm"] ?? 0.0));
                    var topZ = Units.MmToFeet((double)(plane["max_z_mm"] ?? 0.0));

                    start = new XYZ(start.X, start.Y, baseZ);
                    end = new XYZ(end.X, end.Y, baseZ);

                    if (snapAngle > 0)
                        (start, end) = SnapToAngle(start, end, snapAngle);

                    if (start.DistanceTo(end) < minLengthFt)
                    {
                        skipped++;
                        continue;
                    }

                    segments.Add((start, end));
                    heights.Add(fixedHeightMm > 0
                        ? Units.MmToFeet(fixedHeightMm)
                        : Math.Max(topZ - baseZ, Units.MmToFeet(100)));
                }

                var result = CreateWalls(doc, segments, wallTypeId, level, heights, structural);
                result["skipped_planes"] = skipped;
                return result;
            });

            registry.Add("floors.create", (app, p) =>
            {
                var doc = Params.Document(app);
                var level = Params.Element<Level>(doc, p, "level_id");
                var floorTypeId = ResolveType(doc, p, "floor_type_id", ElementTypeGroup.FloorType, "перекрытия");

                var boundary = Params.Array(p, "boundary")
                    .Select(token => Params.PointFrom((JObject)token, "boundary[]"))
                    .ToList();

                if (boundary.Count < 3)
                    throw new CommandException("Контур перекрытия должен содержать минимум три точки.");

                var elevationFt = level.Elevation + Units.MmToFeet(Params.DoubleOr(p, "offset_mm", 0));
                var loop = BuildClosedLoop(boundary, elevationFt);

                using (var tx = new Transaction(doc, "MCP: создание перекрытия"))
                {
                    tx.Start();
                    var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, level.Id);
                    tx.Commit();

                    return new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.ToLong(floor.Id),
                        ["level"] = level.Name,
                        ["area_m2"] = Units.SqFeetToSqM(
                            floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0.0)
                    };
                }
            });

            registry.Add("walls.join", (app, p) =>
            {
                var doc = Params.Document(app);
                var ids = Params.IdList(p, "ids").Select(RevitIds.FromLong).ToList();
                var joined = 0;

                using (var tx = new Transaction(doc, "MCP: соединение стен"))
                {
                    tx.Start();
                    for (var i = 0; i < ids.Count; i++)
                    {
                        for (var j = i + 1; j < ids.Count; j++)
                        {
                            var a = doc.GetElement(ids[i]);
                            var b = doc.GetElement(ids[j]);
                            if (a == null || b == null) continue;

                            try
                            {
                                if (!JoinGeometryUtils.AreElementsJoined(doc, a, b))
                                {
                                    JoinGeometryUtils.JoinGeometry(doc, a, b);
                                    joined++;
                                }
                            }
                            catch (Autodesk.Revit.Exceptions.ApplicationException)
                            {
                                // Непересекающиеся стены соединить нельзя — это ожидаемо.
                            }
                        }
                    }

                    tx.Commit();
                }

                return new Dictionary<string, object> { ["joined_pairs"] = joined };
            });
        }

        private static Dictionary<string, object> CreateWalls(
            Document doc, List<(XYZ Start, XYZ End)> segments, ElementId wallTypeId,
            Level level, double heightFt, double baseOffsetFt, bool structural)
        {
            var heights = Enumerable.Repeat(heightFt, segments.Count).ToList();
            var result = CreateWalls(doc, segments, wallTypeId, level, heights, structural, baseOffsetFt);
            return result;
        }

        private static Dictionary<string, object> CreateWalls(
            Document doc, List<(XYZ Start, XYZ End)> segments, ElementId wallTypeId,
            Level level, List<double> heights, bool structural, double baseOffsetFt = 0)
        {
            var created = new List<Dictionary<string, object>>();
            var failed = new List<Dictionary<string, object>>();

            using (var tx = new Transaction(doc, "MCP: создание стен"))
            {
                tx.Start();

                for (var i = 0; i < segments.Count; i++)
                {
                    var (start, end) = segments[i];
                    try
                    {
                        if (start.DistanceTo(end) < doc.Application.ShortCurveTolerance)
                            throw new CommandException("Осевая линия короче минимальной длины кривой Revit.");

                        var line = Line.CreateBound(start, end);
                        var wall = Wall.Create(doc, line, wallTypeId, level.Id, heights[i], baseOffsetFt,
                            false, structural);

                        created.Add(new Dictionary<string, object>
                        {
                            ["id"] = RevitIds.ToLong(wall.Id),
                            ["length_mm"] = Units.FeetToMm(start.DistanceTo(end)),
                            ["height_mm"] = Units.FeetToMm(heights[i])
                        });
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            ["index"] = i,
                            ["reason"] = ex.Message
                        });
                    }
                }

                tx.Commit();
            }

            return new Dictionary<string, object>
            {
                ["created_count"] = created.Count,
                ["walls"] = created,
                ["failed"] = failed
            };
        }

        /// <summary>
        /// Подтягивает осевую линию к ближайшему кратному углу (обычно 90°) вокруг середины отрезка.
        /// Обмер по облаку почти всегда даёт стены с отклонением в доли градуса.
        /// </summary>
        private static (XYZ Start, XYZ End) SnapToAngle(XYZ start, XYZ end, double stepDegrees)
        {
            var delta = end - start;
            var length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (length < 1e-9) return (start, end);

            var step = Units.DegreesToRadians(stepDegrees);
            var angle = Math.Atan2(delta.Y, delta.X);
            var snapped = Math.Round(angle / step) * step;

            var middle = (start + end) / 2.0;
            var direction = new XYZ(Math.Cos(snapped), Math.Sin(snapped), 0);
            var half = direction * (length / 2.0);

            return (middle - half, middle + half);
        }

        private static CurveLoop BuildClosedLoop(List<XYZ> boundary, double elevationFt)
        {
            var points = boundary.Select(p => new XYZ(p.X, p.Y, elevationFt)).ToList();
            if (points.First().DistanceTo(points.Last()) > 1e-9)
                points.Add(points.First());

            var loop = new CurveLoop();
            for (var i = 0; i < points.Count - 1; i++)
            {
                if (points[i].DistanceTo(points[i + 1]) < 1e-6) continue;
                loop.Append(Line.CreateBound(points[i], points[i + 1]));
            }

            return loop;
        }

        private static ElementId ResolveWallType(Document doc, JObject p) =>
            ResolveType(doc, p, "wall_type_id", ElementTypeGroup.WallType, "стены");

        private static ElementId ResolveType(
            Document doc, JObject p, string paramName, ElementTypeGroup group, string what)
        {
            var explicitId = Params.IdOr(p, paramName, -1);
            if (explicitId >= 0)
            {
                var id = RevitIds.FromLong(explicitId);
                if (doc.GetElement(id) == null)
                    throw CommandException.NotFound("Типоразмер " + explicitId);
                return id;
            }

            var defaultId = doc.GetDefaultElementTypeId(group);
            if (defaultId == null || defaultId == ElementId.InvalidElementId)
                throw new CommandException(
                    "В проекте не задан тип " + what + " по умолчанию — передайте '" + paramName +
                    "' (список доступен командой types.list).");

            return defaultId;
        }
    }
}
