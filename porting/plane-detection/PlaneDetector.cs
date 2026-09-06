using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace McpRevit.Geometry
{
    public class PlaneDetectorOptions
    {
        /// <summary>Допуск попадания точки в плоскость, футы.</summary>
        public double Tolerance = 0.082; // ≈ 25 мм

        /// <summary>Сколько плоскостей искать максимум.</summary>
        public int MaxPlanes = 12;

        /// <summary>Минимальная доля точек от исходного облака, чтобы признать плоскость.</summary>
        public double MinInlierRatio = 0.02;

        /// <summary>Минимальное абсолютное число точек в плоскости.</summary>
        public int MinInliers = 200;

        /// <summary>Число случайных троек на одну плоскость.</summary>
        public int Trials = 400;

        public int Seed = 20240101;
    }

    /// <summary>
    /// Последовательный RANSAC: находит доминирующую плоскость, уточняет её по МНК (PCA),
    /// исключает вошедшие точки и повторяет для остатка.
    /// </summary>
    public static class PlaneDetector
    {
        public static List<DetectedPlane> Detect(IReadOnlyList<XYZ> points, PlaneDetectorOptions options)
        {
            var result = new List<DetectedPlane>();
            if (points == null || points.Count < 3) return result;

            var random = new Random(options.Seed);
            var remaining = points.ToList();
            var minInliers = Math.Max(options.MinInliers, (int)(points.Count * options.MinInlierRatio));

            for (var pass = 0; pass < options.MaxPlanes; pass++)
            {
                if (remaining.Count < minInliers) break;

                var best = FindBestPlane(remaining, options, random);
                if (best == null || best.Count < minInliers) break;

                var plane = Refine(best.Inliers);
                if (plane == null) break;

                // После уточнения набор точек мог измениться — пересобираем его.
                var inliers = remaining.Where(p => Math.Abs(plane.SignedDistance(p)) <= options.Tolerance).ToList();
                if (inliers.Count < minInliers) break;

                Describe(plane, inliers);
                result.Add(plane);

                var inlierSet = new HashSet<XYZ>(inliers);
                remaining = remaining.Where(p => !inlierSet.Contains(p)).ToList();
            }

            return result.OrderByDescending(p => p.InlierCount).ToList();
        }

        private class Candidate
        {
            public List<XYZ> Inliers;
            public int Count => Inliers.Count;
        }

        private static Candidate FindBestPlane(List<XYZ> points, PlaneDetectorOptions options, Random random)
        {
            XYZ bestNormal = null;
            var bestOffset = 0.0;
            var bestCount = 0;

            // На переборе гипотез считаем только число попаданий: собирать сами точки
            // на каждой из сотен итераций слишком дорого.
            for (var trial = 0; trial < options.Trials; trial++)
            {
                var a = points[random.Next(points.Count)];
                var b = points[random.Next(points.Count)];
                var c = points[random.Next(points.Count)];

                var normal = (b - a).CrossProduct(c - a);
                if (normal.GetLength() < 1e-9) continue;
                normal = normal.Normalize();

                var offset = normal.DotProduct(a);
                var count = 0;
                foreach (var point in points)
                {
                    if (Math.Abs(normal.DotProduct(point) - offset) <= options.Tolerance)
                        count++;
                }

                if (count > bestCount)
                {
                    bestCount = count;
                    bestNormal = normal;
                    bestOffset = offset;
                }
            }

            if (bestNormal == null) return null;

            var inliers = points
                .Where(p => Math.Abs(bestNormal.DotProduct(p) - bestOffset) <= options.Tolerance)
                .ToList();

            return new Candidate { Inliers = inliers };
        }

        /// <summary>
        /// Уточнение плоскости по всем точкам: нормаль — собственный вектор ковариационной
        /// матрицы с наименьшим собственным числом.
        /// </summary>
        private static DetectedPlane Refine(IReadOnlyList<XYZ> points)
        {
            if (points.Count < 3) return null;

            var centroid = Centroid(points);
            var covariance = Covariance(points, centroid);
            var (vectors, values) = Jacobi.Eigen(covariance);

            var smallest = 0;
            for (var i = 1; i < 3; i++)
                if (values[i] < values[smallest]) smallest = i;

            var normal = new XYZ(vectors[0, smallest], vectors[1, smallest], vectors[2, smallest]);
            if (normal.GetLength() < 1e-9) return null;
            normal = normal.Normalize();

            // Нормаль направляем вверх/наружу детерминированно, чтобы результат не «прыгал».
            if (normal.Z < -1e-9 || (Math.Abs(normal.Z) <= 1e-9 && normal.X + normal.Y < 0))
                normal = normal.Negate();

            return new DetectedPlane { Normal = normal, Centroid = centroid };
        }

        /// <summary>Заполняет габариты, толщину и след стены в плане.</summary>
        private static void Describe(DetectedPlane plane, List<XYZ> inliers)
        {
            plane.InlierCount = inliers.Count;

            var u = ArbitraryPerpendicular(plane.Normal);
            var v = plane.Normal.CrossProduct(u).Normalize();

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            double minN = double.MaxValue, maxN = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            var squareSum = 0.0;

            foreach (var point in inliers)
            {
                var delta = point - plane.Centroid;
                var du = delta.DotProduct(u);
                var dv = delta.DotProduct(v);
                var dn = delta.DotProduct(plane.Normal);

                if (du < minU) minU = du;
                if (du > maxU) maxU = du;
                if (dv < minV) minV = dv;
                if (dv > maxV) maxV = dv;
                if (dn < minN) minN = dn;
                if (dn > maxN) maxN = dn;
                if (point.Z < minZ) minZ = point.Z;
                if (point.Z > maxZ) maxZ = point.Z;

                squareSum += dn * dn;
            }

            plane.ExtentU = maxU - minU;
            plane.ExtentV = maxV - minV;
            plane.Thickness = maxN - minN;
            plane.MinZ = minZ;
            plane.MaxZ = maxZ;
            plane.Rmse = Math.Sqrt(squareSum / inliers.Count);

            BuildTrace(plane, inliers);
        }

        /// <summary>
        /// След вертикальной плоскости в плане: горизонтальное направление плоскости и
        /// крайние проекции точек на него. Даёт готовую осевую линию для стены.
        /// </summary>
        private static void BuildTrace(DetectedPlane plane, List<XYZ> inliers)
        {
            var horizontal = new XYZ(plane.Normal.X, plane.Normal.Y, 0);
            if (horizontal.GetLength() < 1e-6) return; // горизонтальная плоскость — следа нет

            var direction = new XYZ(-plane.Normal.Y, plane.Normal.X, 0).Normalize();

            double min = double.MaxValue, max = double.MinValue;
            foreach (var point in inliers)
            {
                var t = (point - plane.Centroid).DotProduct(direction);
                if (t < min) min = t;
                if (t > max) max = t;
            }

            var origin = new XYZ(plane.Centroid.X, plane.Centroid.Y, plane.Centroid.Z);
            plane.TraceStart = origin + direction * min;
            plane.TraceEnd = origin + direction * max;
        }

        private static XYZ ArbitraryPerpendicular(XYZ normal)
        {
            var reference = Math.Abs(normal.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            return normal.CrossProduct(reference).Normalize();
        }

        private static XYZ Centroid(IReadOnlyList<XYZ> points)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in points)
            {
                x += p.X;
                y += p.Y;
                z += p.Z;
            }

            return new XYZ(x / points.Count, y / points.Count, z / points.Count);
        }

        private static double[,] Covariance(IReadOnlyList<XYZ> points, XYZ centroid)
        {
            var m = new double[3, 3];
            foreach (var point in points)
            {
                var d = point - centroid;
                m[0, 0] += d.X * d.X;
                m[0, 1] += d.X * d.Y;
                m[0, 2] += d.X * d.Z;
                m[1, 1] += d.Y * d.Y;
                m[1, 2] += d.Y * d.Z;
                m[2, 2] += d.Z * d.Z;
            }

            m[1, 0] = m[0, 1];
            m[2, 0] = m[0, 2];
            m[2, 1] = m[1, 2];
            return m;
        }
    }
}
