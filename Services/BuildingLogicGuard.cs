using System.Globalization;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Итог проверки проёма: принят ли он, стена-хост, сдвиг вдоль оси стены (м), была ли новая привязка и пояснение.</summary>
public sealed record OpeningGuardResult(bool Success, ArchitecturalElement? Host, double Shift, bool Reattached, string Message)
{
    public static OpeningGuardResult Fail(string message, ArchitecturalElement? host = null) => new(false, host, 0, false, message);
}

/// <summary>Запретная зона вдоль оси стены (локальная X, м) и её причина.</summary>
public readonly record struct GuardZone(double A, double B, string Reason);

/// <summary>
/// Конструктивная логика проёмов: окно и дверь не висят в воздухе, не лежат «наклейкой» на глухой стене и не врезаются в несущие
/// колонны, поперечные стены и балки. Проём без хоста привязывается к ближайшей стене в радиусе 0,5 м (поворот и ось — по стене,
/// глубина — толщина стены + 0,06 м, дверь — от пола стены); проём, попавший в тело колонны или ближе 150 мм к её грани, к торцу
/// стены или к поперечной стене, сдвигается вдоль оси стены в ближайший свободный простенок; не помещается — отказ с причиной
/// (вызывающий откатывает шаг). Тело стены режется по привязке HostElementId в <see cref="Rendering.ElementGeometryBuilder"/>.
/// </summary>
public static class BuildingLogicGuard
{
    /// <summary>Отступ кромки проёма от грани колонны, торца стены и поперечной стены, м.</summary>
    public const double Clearance = 0.15;

    /// <summary>Радиус поиска стены для проёма без хоста, м.</summary>
    public const double AttachRadius = 0.5;

    /// <summary>Зазор между соседними проёмами одной стены, м.</summary>
    public const double OpeningGap = 0.1;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static bool IsGuarded(ArchitecturalElement e) => e.Category is BimCategory.Window or BimCategory.Door;

    private static bool IsWallHost(ArchitecturalElement w) => w.Category == BimCategory.Wall && w.Polygon2D.Count < 3;

    /// <summary>Стена проёма: своя (HostElementId) или ближайшая к центру проёма в радиусе 0,5 м по высоте этажа.</summary>
    public static ArchitecturalElement? FindHostWall(IReadOnlyList<ArchitecturalElement> scene, ArchitecturalElement opening, out bool reattached)
    {
        reattached = false;
        if (opening.HostElementId is { } id && scene.FirstOrDefault(e => e.Id == id) is { } own && IsWallHost(own)) return own;
        ArchitecturalElement? best = null;
        double bestScore = double.MaxValue;
        foreach (var w in scene)
        {
            if (!IsWallHost(w) || ReferenceEquals(w, opening) || w.BuildingGroupId != opening.BuildingGroupId) continue;
            if (opening.TopY <= w.BaseY + 0.05 || opening.BaseY >= w.TopY - 0.05) continue;
            var (lx, lz) = ArchitecturalElement.RotateY(opening.X - w.X, opening.Z - w.Z, -w.RotationY);
            double along = Math.Max(0, Math.Abs(lx) - w.Width / 2), across = Math.Max(0, Math.Abs(lz) - w.Depth / 2);
            double dist = Math.Sqrt(along * along + across * across);
            if (dist > AttachRadius) continue;
            double score = dist + Math.Abs(lz) * 1e-3 + (w.LevelIndex == opening.LevelIndex ? 0 : 1);
            if (score < bestScore)
            {
                bestScore = score;
                best = w;
            }
        }
        reattached = best is not null;
        return best;
    }

    /// <summary>
    /// Запретные зоны вдоль оси стены для кромок проёма в интервале высот [y0, y1]: колонны и ядра, поперечные стены и балки,
    /// пересекающие полосу стены (с отступом 150 мм), и соседние проёмы этой стены (зазор 100 мм).
    /// </summary>
    public static List<GuardZone> ForbiddenZones(IReadOnlyList<ArchitecturalElement> scene, ArchitecturalElement wall, ArchitecturalElement opening, double y0, double y1)
    {
        var zones = new List<GuardZone>();
        double band = wall.Depth / 2 + Clearance;
        foreach (var e in scene)
        {
            if (ReferenceEquals(e, wall) || ReferenceEquals(e, opening) || e.BuildingGroupId != wall.BuildingGroupId) continue;
            if (e.TopY <= y0 + 1e-6 || e.BaseY >= y1 - 1e-6) continue;
            bool sibling = ElementTypes.IsHostedType(e.Category) && e.HostElementId == wall.Id;
            bool structure = e.Category is BimCategory.Column or BimCategory.Core or BimCategory.Beam
                             || IsWallHost(e) && Math.Abs(Math.Cos((e.RotationY - wall.RotationY) * Math.PI / 180)) < 0.9;
            if (!sibling && !structure) continue;
            var outline = ClashDetectionService.PlanOutline(e).Select(p => ArchitecturalElement.RotateY(p.X - wall.X, p.Z - wall.Z, -wall.RotationY)).ToList();
            double minZ = outline.Min(p => p.Z), maxZ = outline.Max(p => p.Z);
            if (maxZ < -band || minZ > band) continue;
            double minX = outline.Min(p => p.X), maxX = outline.Max(p => p.X);
            if (sibling) zones.Add(new GuardZone(minX - OpeningGap, maxX + OpeningGap, $"проём «{e.DisplayTitle}»"));
            else
            {
                string what = e.Category switch
                {
                    BimCategory.Column => "колонна",
                    BimCategory.Core => "ядро жёсткости",
                    BimCategory.Beam => "балка",
                    _ => "поперечная стена"
                };
                zones.Add(new GuardZone(minX - Clearance, maxX + Clearance, $"{what} «{e.DisplayTitle}»"));
            }
        }
        zones.Sort((a, b) => a.A.CompareTo(b.A));
        return zones;
    }

    /// <summary>
    /// Приводит окно/дверь к честному проёму в стене (меняет элемент на месте): привязка, поворот и ось по стене, высота и низ,
    /// сдвиг из зоны колонн/поперечных стен/торцов. Отказ — элемент не меняется.
    /// </summary>
    public static OpeningGuardResult Enforce(IReadOnlyList<ArchitecturalElement> scene, ArchitecturalElement opening)
    {
        if (!IsGuarded(opening)) return new OpeningGuardResult(true, null, 0, false, string.Empty);
        bool door = opening.Category == BimCategory.Door;
        string kind = door ? "Дверь" : "Окно";
        var host = FindHostWall(scene, opening, out bool reattached);
        if (host is null)
            return OpeningGuardResult.Fail($"{kind} «{opening.DisplayTitle}» должно стоять в стене: в радиусе {Mm(AttachRadius)} мм нет стены — проём не создан");

        double width = Math.Max(0.3, opening.Width);
        double height = Math.Min(Math.Max(0.3, opening.Height), host.Height - 0.05);
        double sill = door ? 0 : opening.BaseY - host.BaseY;
        if (!door && (sill < 0.2 || sill + height > host.Height - 0.05)) sill = Math.Min(0.9, Math.Max(0.2, host.Height - height - 0.2));
        if (height < 0.6)
            return OpeningGuardResult.Fail($"{kind} «{opening.DisplayTitle}» не помещается по высоте в стену «{host.DisplayTitle}» ({Mm(host.Height)} мм)", host);
        double y0 = host.BaseY + sill, y1 = y0 + height;

        var (lx, _) = ArchitecturalElement.RotateY(opening.X - host.X, opening.Z - host.Z, -host.RotationY);
        double lo = -host.Width / 2 + Clearance + width / 2, hi = host.Width / 2 - Clearance - width / 2;
        if (lo > hi + 1e-9)
            return OpeningGuardResult.Fail($"Стена «{host.DisplayTitle}» длиной {Mm(host.Width)} мм слишком короткая для проёма {Mm(width)} мм с отступом {Mm(Clearance)} мм от торцов", host);

        var zones = ForbiddenZones(scene, host, opening, y0, y1);
        bool Free(double c) => c >= lo - 1e-9 && c <= hi + 1e-9 && zones.All(z => c + width / 2 <= z.A + 1e-9 || c - width / 2 >= z.B - 1e-9);
        double desired = Math.Clamp(lx, lo, hi);
        double? best = Free(desired) ? desired : null;
        if (best is null)
        {
            var candidates = zones.SelectMany(z => new[] { z.A - width / 2, z.B + width / 2 }).Append(lo).Append(hi).Where(Free).ToList();
            if (candidates.Count > 0) best = candidates.OrderBy(c => Math.Abs(c - lx)).First();
        }
        if (best is not { } center)
        {
            string blockers = string.Join(", ", zones.Select(z => z.Reason).Distinct().Take(3));
            return OpeningGuardResult.Fail($"В стене «{host.DisplayTitle}» нет простенка для проёма {Mm(width)} мм с отступом {Mm(Clearance)} мм" +
                                           (blockers.Length > 0 ? $" (мешают: {blockers})" : string.Empty) + " — шаг отменён", host);
        }

        var (wx, wz) = ArchitecturalElement.RotateY(center, 0, host.RotationY);
        opening.SetSize(width, height, host.Depth + ParametricSolverService.OpeningClearance);
        opening.SetPosition(host.X + wx, y0 + height / 2, host.Z + wz);
        opening.SetRotationY(host.RotationY);
        if (opening.BuildingGroupId != host.BuildingGroupId) opening.BuildingGroupId = host.BuildingGroupId;
        if (opening.LevelIndex != host.LevelIndex) opening.LevelIndex = host.LevelIndex;
        if (opening.HostElementId != host.Id) opening.HostElementId = host.Id;
        double shift = center - lx;
        var notes = new List<string>();
        if (reattached) notes.Add($"привязано к стене «{host.DisplayTitle}»");
        if (Math.Abs(shift) > 1e-4)
        {
            var hit = zones.Where(z => lx + width / 2 > z.A && lx - width / 2 < z.B).Select(z => z.Reason).FirstOrDefault();
            notes.Add($"сдвинуто вдоль стены на {Mm(Math.Abs(shift))} мм" + (hit is null ? " (отступ от торца)" : $" от: {hit}"));
        }
        return new OpeningGuardResult(true, host, shift, reattached, notes.Count == 0 ? string.Empty : $"{kind} «{opening.DisplayTitle}»: {string.Join(", ", notes)}");
    }

    private static string Mm(double m) => Math.Round(m * 1000).ToString("N0", Ru);
}
