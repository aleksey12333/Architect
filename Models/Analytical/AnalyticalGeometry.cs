namespace AIArchitectStudio.Models.Analytical;

/// <summary>Быстрые проверки на массивах точек плана (горячие циклы дискретизации плит и поиска опор).</summary>
public static class AnalyticalGeometry
{
    public static bool Pip((double X, double Z)[] p, double x, double z)
    {
        bool inside = false;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
        {
            double ax = p[i].X, az = p[i].Z, bx = p[j].X, bz = p[j].Z;
            if ((az > z) != (bz > z) && x < (bx - ax) * (z - az) / (bz - az) + ax) inside = !inside;
        }
        return inside;
    }

    public static double DistBoundary((double X, double Z)[] p, double x, double z)
    {
        double best = double.MaxValue;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
        {
            double ax = p[j].X, az = p[j].Z, vx = p[i].X - ax, vz = p[i].Z - az;
            double len2 = vx * vx + vz * vz;
            double t = len2 < 1e-18 ? 0 : ((x - ax) * vx + (z - az) * vz) / len2;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double dx = ax + vx * t - x, dz = az + vz * t - z, d = dx * dx + dz * dz;
            if (d < best) best = d;
        }
        return Math.Sqrt(best);
    }

    /// <summary>Точка внутри контура элемента и вне его отверстий.</summary>
    public static bool InsideNet(AnalyticalElement m, double x, double z)
    {
        if (!Pip(m.Poly, x, z)) return false;
        foreach (var h in m.HoleArr)
            if (Pip(h, x, z)) return false;
        return true;
    }

    /// <summary>
    /// Вписанный круг выпуклой оболочки (против часовой стрелки): центр — среднее вершин, радиус — наименьшее расстояние
    /// от центра до прямых рёбер с запасом 1 мкм. Точка внутри круга заведомо внутри оболочки (до каждого ребра не меньше
    /// запаса — на порядки больше погрешности векторного произведения), поэтому полный перебор рёбер для неё не нужен и
    /// результат не меняется. Вырожденная оболочка (ребро короче 1 мкм) — без круга (R2 &lt; 0).
    /// </summary>
    public static (double X, double Z, double R2) InscribedDisc((double X, double Z)[] hull)
    {
        if (hull.Length < 3) return (0, 0, -1);
        double cx = 0, cz = 0;
        foreach (var p in hull)
        {
            cx += p.X;
            cz += p.Z;
        }
        cx /= hull.Length;
        cz /= hull.Length;
        double r = double.MaxValue;
        for (int i = 0, j = hull.Length - 1; i < hull.Length; j = i++)
        {
            double ex = hull[i].X - hull[j].X, ez = hull[i].Z - hull[j].Z, len = Math.Sqrt(ex * ex + ez * ez);
            if (len < 1e-6) return (cx, cz, -1);
            r = Math.Min(r, (ex * (cz - hull[j].Z) - ez * (cx - hull[j].X)) / len);
        }
        r -= 1e-6;
        return r > 0 ? (cx, cz, r * r) : (cx, cz, -1);
    }

    public static bool InDisc((double X, double Z, double R2) disc, double x, double z)
    {
        double dx = x - disc.X, dz = z - disc.Z;
        return dx * dx + dz * dz < disc.R2;
    }

    /// <summary>Точка внутри выпуклой оболочки против часовой стрелки (все векторные произведения неотрицательны).</summary>
    public static bool InsideConvex((double X, double Z)[] hull, double x, double z)
    {
        for (int i = 0, j = hull.Length - 1; i < hull.Length; j = i++)
        {
            var a = hull[j];
            var b = hull[i];
            if ((b.X - a.X) * (z - a.Z) - (b.Z - a.Z) * (x - a.X) < -1e-9) return false;
        }
        return true;
    }
}
