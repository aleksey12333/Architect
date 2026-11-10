using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Models.Layouts;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Gpzu;

namespace AIArchitectStudio.Services.Layouts;

/// <summary>Колонка мульти-плана: подпись, отметка и пакет-представитель (несколько типовых пакетов — одной колонкой «Этажи 11–50»).</summary>
public sealed record PlanTier(string Title, string Elevation, FloorPackagePlan Package, int FromLevel, int ToLevel, int PackagesMerged);

/// <summary>
/// Волна 7 — пакетная планировка этажей (Раздел «3. Планировки и ИОС»): разбивка здания на пакеты (подвал, 1–5, 6–10 … 46–50),
/// исходные данные из 3D-модели (контур, этажность, оси по колоннам, подвал), вводы сетей участка, мульти-план из 2–4 колонок с
/// общей сеткой осей, правка планировки пакета (тип, зеркальная планировка) — для всех этажей пакета одним шагом журнала.
/// </summary>
public static class FloorTierLayoutService
{
    /// <summary>Пакеты по size этажей: 1–5, 6–10, …, последний — до верхнего этажа.</summary>
    public static List<(int From, int To)> SplitPackages(int levels, int size = 5)
    {
        var list = new List<(int, int)>();
        size = Math.Clamp(size, 1, 50);
        for (int from = 1; from <= levels; from += size) list.Add((from, Math.Min(levels, from + size - 1)));
        return list;
    }

    /// <summary>
    /// Запрос планировки по постройке сцены: контур — по наибольшему перекрытию (или фундаменту), этажность — по повторам этажей,
    /// подвал — элементы ниже −0,5 м (кроме фундамента), оси — по колоннам, вводы — от трасс сетей участка к контуру здания.
    /// </summary>
    public static LayoutRequest? RequestFor(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, string? groupId, GpzuSitePlan? site,
        int packageSize = 5, ApartmentMix? mix = null, CoreStairForm? stairForm = null)
    {
        var parts = elements.Where(GpzuComplianceVerifier.IsBuildingPart).ToList();
        if (parts.Count == 0) return null;
        groupId ??= parts.GroupBy(e => e.BuildingGroupId).OrderByDescending(g => g.Sum(e => Math.Max(1, e.RepeatLevels))).First().Key;
        var own = parts.Where(e => e.BuildingGroupId == groupId).ToList();
        if (own.Count == 0) return null;
        var slab = own.Where(e => e.Category is BimCategory.Slab).OrderByDescending(e => e.Width * e.Depth).FirstOrDefault()
                   ?? own.Where(e => e.Category is BimCategory.Foundation).OrderByDescending(e => e.Width * e.Depth).FirstOrDefault();
        if (slab is null) return null;
        bool turned = Math.Abs(Math.Abs(slab.RotationY % 180) - 90) < 1;
        double length = turned ? slab.Depth : slab.Width, width = turned ? slab.Width : slab.Depth;
        if (width > length) (length, width) = (width, length);
        int levels = own.Max(e => e.LevelIndex + Math.Max(1, e.RepeatLevels));
        double levelHeight = own.Where(e => e.RepeatLevels > 1 && e.LevelHeight > 2).Select(e => e.LevelHeight).DefaultIfEmpty(3.0).First();
        bool basement = own.Any(e => e.BaseY < -1.0 && e.Category is not BimCategory.Foundation);
        double cx = slab.X, cz = slab.Z;
        var axisX = own.Where(e => e.Category == BimCategory.Column).Select(e => Math.Round(e.X - cx, 2)).Distinct().OrderBy(v => v).ToList();
        var axisZ = own.Where(e => e.Category == BimCategory.Column).Select(e => Math.Round(e.Z - cz, 2)).Distinct().OrderBy(v => v).ToList();
        var entries = new List<(GpzuZoneKind, double, double)>();
        if (site is not null)
        {
            var summary = WorkspaceEnvironmentService.AnalyzeInteriors(own, site);
            foreach (var e in summary.Entries) entries.Add((e.Kind, e.At.X - cx, e.At.Z - cz));
        }
        bool gas = site?.Zones.Any(z => z.Zone.Kind == GpzuZoneKind.GasPipeline) ?? false;
        string name = groups.FirstOrDefault(g => g.Id == groupId)?.Name ?? "Жилой корпус";
        return new LayoutRequest
        {
            GroupId = groupId,
            Name = name,
            Levels = Math.Max(1, levels),
            LevelHeight = levelHeight,
            Length = Math.Round(length, 3),
            Width = Math.Round(width, 3),
            OriginX = cx,
            OriginZ = cz,
            Basement = basement,
            PackageSize = packageSize,
            Mix = mix ?? ApartmentMix.Default,
            StairForm = stairForm,
            GasAvailable = gas,
            SiteEntries = entries,
            AxisX = turned ? [] : axisX,
            AxisZ = turned ? [] : axisZ
        };
    }

    /// <summary>
    /// Колонки мульти-плана: подвал, первый пакет, второй, остальные типовые — одной колонкой («Этажи 11–50»); count = 2…4.
    /// Пакеты с одинаковой планировкой (код и состав помещений) сливаются, разные — остаются отдельными колонками в пределах count.
    /// </summary>
    public static List<PlanTier> Tiers(BuildingLayoutPlan plan, int count = 4)
    {
        count = Math.Clamp(count, 2, 4);
        var list = new List<PlanTier>();
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        if (plan.Basement is { } b) list.Add(new PlanTier(b.Title.Split(' ')[0], b.Elevation.ToString("+0.000;-0.000", ru), b, 0, 0, 1));
        var floors = plan.Floors.ToList();
        int i = 0;
        while (i < floors.Count && list.Count < count)
        {
            var p = floors[i];
            int j = i;
            // Последняя колонка забирает все оставшиеся пакеты, если они типовые (одна планировка).
            if (list.Count == count - 1)
            {
                bool typical = floors.Skip(i).All(q => Signature(q) == Signature(p));
                if (typical) j = floors.Count - 1;
            }
            var last = floors[j];
            string title = p.FromLevel == last.ToLevel ? $"Этаж {p.FromLevel}" : $"Этажи {p.FromLevel}–{last.ToLevel}";
            list.Add(new PlanTier(title, p.Elevation.ToString("+0.000;-0.000", ru), p, p.FromLevel, last.ToLevel, j - i + 1));
            i = j + 1;
        }
        return list;
    }

    /// <summary>Подпись планировки пакета: код и состав помещений (для слияния типовых пакетов).</summary>
    public static string Signature(FloorPackagePlan p) =>
        p.LayoutCode + "|" + string.Join(";", p.Rooms.Where(r => r.ApartmentId is not null).OrderBy(r => r.X0).ThenBy(r => r.Z0)
            .Select(r => FormattableString.Invariant($"{(int)r.Kind}:{r.X0:0.##},{r.Z0:0.##},{r.X1:0.##},{r.Z1:0.##}")));

    /// <summary>Зеркальная планировка пакета: мокрые блоки всех (или одной) квартир переставляются к противоположной межквартирной стене.</summary>
    public static BuildingLayoutPlan Mirror(BuildingLayoutPlan plan, string packageId, string? apartmentKey = null)
    {
        var p = plan.Package(packageId);
        if (p is null || p.IsBasement) return plan;
        var rooms = p.Rooms.ToList();
        var risers = p.Risers.ToList();
        var apartments = p.Apartments.ToList();
        for (int i = 0; i < apartments.Count; i++)
        {
            var a = apartments[i];
            if (apartmentKey is not null && a.Mark != apartmentKey) continue;
            var (r, st, done) = DeveloperLayoutGenerator.BuildApartment(a with { WetAtHighX = !a.WetAtHighX }, rooms.Any(x => x.ApartmentId == a.Id && x.Kind == LayoutRoomKind.KitchenGas), plan.Mix.CorridorWidth);
            rooms.RemoveAll(x => x.ApartmentId == a.Id);
            risers.RemoveAll(x => x.ApartmentId == a.Id);
            rooms.AddRange(r);
            risers.AddRange(st);
            apartments[i] = done;
        }
        var next = p with { Rooms = rooms, Risers = risers, Apartments = apartments };
        return plan.With(packages: plan.Packages.Select(x => x.Id == packageId ? next : x).ToList());
    }

    /// <summary>Тип планировки пакета: «А» — классические квартиры, «Б» — евро (кухня-гостиная), геометрия мокрых блоков сохраняется.</summary>
    public static BuildingLayoutPlan SetPackageCode(BuildingLayoutPlan plan, string packageId, string code)
    {
        var p = plan.Package(packageId);
        if (p is null || p.IsBasement || p.LayoutCode == code) return plan;
        var rooms = p.Rooms.Where(r => r.ApartmentId is null).ToList();
        var risers = p.Risers.Where(r => r.ApartmentId is null).ToList();
        var apartments = new List<Apartment>();
        foreach (var a in p.Apartments)
        {
            var type = code == "Б"
                ? a.Type switch { ApartmentType.OneRoom => ApartmentType.Euro2, ApartmentType.TwoRoom => ApartmentType.Euro3, _ => a.Type }
                : a.Type switch { ApartmentType.Euro2 => ApartmentType.OneRoom, ApartmentType.Euro3 => ApartmentType.TwoRoom, _ => a.Type };
            bool gas = p.Rooms.Any(x => x.ApartmentId == a.Id && x.Kind == LayoutRoomKind.KitchenGas);
            var (r, st, done) = DeveloperLayoutGenerator.BuildApartment(a with { Type = type }, gas, plan.Mix.CorridorWidth);
            rooms.AddRange(r);
            risers.AddRange(st);
            apartments.Add(done);
        }
        var next = p with { Rooms = rooms, Risers = risers, Apartments = apartments, LayoutCode = code };
        return plan.With(packages: plan.Packages.Select(x => x.Id == packageId ? next : x).ToList());
    }

    /// <summary>Перенарезка квартир всех пакетов по новой смеси застройщика (ядро, подвал и вводы — заново, соосно по вертикали).</summary>
    public static BuildingLayoutPlan Regenerate(BuildingLayoutPlan plan, LayoutRequest request) => DeveloperLayoutGenerator.Generate(request with
    {
        GroupId = plan.BuildingGroupId,
        Name = plan.BuildingName,
        OriginX = plan.OriginX,
        OriginZ = plan.OriginZ
    });

    /// <summary>План здания в документ: ровно один шаг журнала (Undo — прежний план).</summary>
    public static void Apply(SceneStateService scene, BuildingLayoutPlan plan, string name) => scene.ApplyBuildingLayout(plan, name);
}
