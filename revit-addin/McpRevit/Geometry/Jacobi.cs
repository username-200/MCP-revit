using System;

namespace McpRevit.Geometry
{
    /// <summary>
    /// Собственные числа и векторы симметричной матрицы 3×3 методом вращений Якоби.
    /// Нужен для подгонки плоскости по облаку точек, брать зависимость ради 3×3 незачем.
    /// </summary>
    public static class Jacobi
    {
        /// <returns>Матрица собственных векторов по столбцам и массив собственных чисел.</returns>
        public static (double[,] Vectors, double[] Values) Eigen(double[,] matrix, int sweeps = 32)
        {
            var a = (double[,])matrix.Clone();
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

            for (var sweep = 0; sweep < sweeps; sweep++)
            {
                var off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-14) break;

                for (var p = 0; p < 2; p++)
                {
                    for (var q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-18) continue;

                        var theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                        var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                        if (theta == 0.0) t = 1.0;

                        var c = 1.0 / Math.Sqrt(t * t + 1.0);
                        var s = t * c;

                        Rotate(a, v, p, q, c, s);
                    }
                }
            }

            return (v, new[] { a[0, 0], a[1, 1], a[2, 2] });
        }

        private static void Rotate(double[,] a, double[,] v, int p, int q, double c, double s)
        {
            var app = a[p, p];
            var aqq = a[q, q];
            var apq = a[p, q];

            a[p, p] = c * c * app - 2 * s * c * apq + s * s * aqq;
            a[q, q] = s * s * app + 2 * s * c * apq + c * c * aqq;
            a[p, q] = 0.0;
            a[q, p] = 0.0;

            for (var k = 0; k < 3; k++)
            {
                if (k == p || k == q) continue;

                var akp = a[k, p];
                var akq = a[k, q];
                a[k, p] = a[p, k] = c * akp - s * akq;
                a[k, q] = a[q, k] = s * akp + c * akq;
            }

            for (var k = 0; k < 3; k++)
            {
                var vkp = v[k, p];
                var vkq = v[k, q];
                v[k, p] = c * vkp - s * vkq;
                v[k, q] = s * vkp + c * vkq;
            }
        }
    }
}
