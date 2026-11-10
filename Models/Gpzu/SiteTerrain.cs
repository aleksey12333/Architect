namespace AIArchitectStudio.Models.Gpzu;

/// <summary>Высотная отметка рельефа в координатах сцены: X, Z (м) и абсолютная отметка Z БС (м).</summary>
public readonly record struct TerrainSample(double X, double Z, double ElevationBs);

/// <summary>Планировочная площадка здания: контур в плане и проектная отметка над землёй участка (м, 0 — пол первого этажа).</summary>
public sealed record TerrainPad(IReadOnlyList<SitePoint> Outline, double DesignLevel);

/// <summary>Итог земляных работ: выемка, насыпь (м³), площадь планировки (м²) и средний перепад рельефа под пятнами.</summary>
public sealed record SiteEarthworks(double CutM3, double FillM3, double PadArea, double MeanDelta, double MinRelief, double MaxRelief);

/// <summary>
/// Рельеф земельного участка: поверхность по высотным отметкам (обратные расстояния, степень 2, до 12 ближайших точек) —
/// отметки топосъёмки и отметки поворотных точек границы; высота в сцене = Z БС − отметка земли участка (Y = 0 — средняя земля).
/// Планировочные площадки зданий срезают/подсыпают рельеф до проектной отметки с откосом 1 : 2 (заложение 2 м на 1 м перепада).
/// Чистая модель, без вьюпорта: её же используют проверка посадки, объёмы земляных работ, прогулка и автотест.
/// </summary>
public sealed class SiteTerrain
{
    private readonly TerrainSample[] _samples;
    private readonly double _ground;

    public SiteTerrain(IEnumerable<TerrainSample> samples, double groundElevation, IReadOnlyList<TerrainPad>? pads = null)
    {
        _samples = samples.Where(s => double.IsFinite(s.ElevationBs) && s.ElevationBs > 0).ToArray();
        _ground = groundElevation;
        Pads = pads ?? [];
    }

    public IReadOnlyList<TerrainSample> Samples => _samples;
    public IReadOnlyList<TerrainPad> Pads { get; }
    public double GroundElevation => _ground;
    public bool HasRelief => _samples.Length > 0 && _ground > 0;

    /// <summary>Естественный рельеф над/под нулём сцены, м (без площадок).</summary>
    public double NaturalAt(double x, double z)
    {
        if (!HasRelief) return 0;
        // До 12 ближайших отметок: частичная сортировка без аллокаций на больших наборах не нужна — у участка сотни точек.
        Span<(double D, double Z)> best = stackalloc (double, double)[12];
        int count = 0;
        foreach (var s in _samples)
        {
            double dx = s.X - x, dz = s.Z - z, d = dx * dx + dz * dz;
            if (d < 1e-8) return s.ElevationBs - _ground;
            if (count < best.Length) { best[count++] = (d, s.ElevationBs); continue; }
            int worst = 0;
            for (int i = 1; i < count; i++) if (best[i].D > best[worst].D) worst = i;
            if (d < best[worst].D) best[worst] = (d, s.ElevationBs);
        }
        double wsum = 0, zsum = 0;
        for (int i = 0; i < count; i++)
        {
            double w = 1 / best[i].D;
            wsum += w;
            zsum += w * best[i].Z;
        }
        return wsum > 0 ? zsum / wsum - _ground : 0;
    }

    /// <summary>Проектный рельеф: внутри площадки — её отметка, вокруг — откос 1 : 2 к естественному рельефу.</summary>
    public double HeightAt(double x, double z)
    {
        double natural = NaturalAt(x, z);
        if (Pads.Count == 0) return natural;
        double result = natural;
        double bestWeight = 0;
        foreach (var pad in Pads)
        {
            double d = DistanceOutside(pad.Outline, x, z);
            if (d <= 0) return pad.DesignLevel;
            double delta = Math.Abs(natural - pad.DesignLevel);
            double run = Math.Max(0.5, delta * 2);
            if (d >= run) continue;
            double k = 1 - d / run;
            if (k > bestWeight)
            {
                bestWeight = k;
                result = pad.DesignLevel + (natural - pad.DesignLevel) * (d / run);
            }
        }
        return result;
    }

    /// <summary>
    /// Объёмы земляных работ по площадкам: сетка шагом step, разность естественного и проектного рельефа внутри площадок
    /// (выемка — рельеф выше проектной отметки, насыпь — ниже).
    /// </summary>
    public SiteEarthworks Earthworks(double step = 1.0)
    {
        double cut = 0, fill = 0, area = 0, sumDelta = 0, minR = double.MaxValue, maxR = double.MinValue;
        int cells = 0;
        foreach (var pad in Pads)
        {
            if (pad.Outline.Count < 3) continue;
            double x0 = pad.Outline.Min(p => p.X), x1 = pad.Outline.Max(p => p.X), z0 = pad.Outline.Min(p => p.Z), z1 = pad.Outline.Max(p => p.Z);
            for (double x = x0 + step / 2; x < x1; x += step)
                for (double z = z0 + step / 2; z < z1; z += step)
                {
                    if (!Contains(pad.Outline, x, z)) continue;
                    double n = NaturalAt(x, z), d = n - pad.DesignLevel;
                    if (d > 0) cut += d * step * step;
                    else fill -= d * step * step;
                    area += step * step;
                    sumDelta += d;
                    minR = Math.Min(minR, n);
                    maxR = Math.Max(maxR, n);
                    cells++;
                }
        }
        return new SiteEarthworks(cut, fill, area, cells > 0 ? sumDelta / cells : 0, cells > 0 ? minR : 0, cells > 0 ? maxR : 0);
    }

    public static bool Contains(IReadOnlyList<SitePoint> poly, double x, double z)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Z > z) != (b.Z > z) && x < (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Расстояние от точки до контура снаружи (≤ 0 — внутри).</summary>
    public static double DistanceOutside(IReadOnlyList<SitePoint> poly, double x, double z)
    {
        if (poly.Count < 3) return double.MaxValue;
        if (Contains(poly, x, z)) return 0;
        double best = double.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            double dx = b.X - a.X, dz = b.Z - a.Z, l2 = dx * dx + dz * dz;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / l2, 0, 1);
            double px = a.X + t * dx - x, pz = a.Z + t * dz - z;
            best = Math.Min(best, px * px + pz * pz);
        }
        return Math.Sqrt(best);
    }
}
