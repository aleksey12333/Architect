using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Gpzu;

namespace AIArchitectStudio.Services;

/// <summary>Три Среды девелопера над одним живым проектом.</summary>
public enum DeveloperEnvironment
{
    /// <summary>Среда 1: «Участок и ГПЗУ (Земля и Сети)».</summary>
    Site,
    /// <summary>Среда 2: «Объём и Конструктив (посадка здания на участке и КЖ/КМ)».</summary>
    Building,
    /// <summary>Среда 3: «Планировки, Ядра и Инженерия».</summary>
    Interiors
}

/// <summary>Описание Среды: номер, подписи (ключи локализации), глиф, цвет и набор инструментов в фокусе.</summary>
public sealed record EnvironmentDescriptor(DeveloperEnvironment Id, int Number, string TitleKey, string ShortKey, string HintKey, string Glyph, string ColorHex,
    bool SiteEditing, bool SiteLayers, bool StructuralTools, bool FloorPackages);

/// <summary>Пакет этажей Среды 3: этажи с … по … с одной планировкой, ядра, лестницы, лифты в пакете.</summary>
public sealed record FloorPackage(int Number, int FromLevel, int ToLevel, string Layout, int Cores, int Stairs, int Lifts, double FloorArea);

/// <summary>Ввод инженерной сети в подвал / техподполье: вид сети, точка на стене здания, расстояние до трассы, допустимость.</summary>
public sealed record UtilityEntry(GpzuZoneKind Kind, string Title, SitePoint At, double DistanceToNetwork, bool IntoBasement, bool Allowed, string Rule);

/// <summary>Замечание проверки мокрых и газовых зон здания.</summary>
public sealed record ZoneCheck(bool Ok, string Title, string Detail);

/// <summary>Сводка Среды 3 по зданию: пакеты этажей, тамбурно-лифтовые узлы, лестницы, подвал и вводы сетей, проверки зон.</summary>
public sealed class InteriorsSummary
{
    public List<FloorPackage> Packages { get; } = [];
    public int Levels { get; init; }
    public int Cores { get; init; }
    public int StairFlights { get; init; }
    public bool HasBasement { get; init; }
    public List<UtilityEntry> Entries { get; } = [];
    public List<ZoneCheck> Checks { get; } = [];
}

/// <summary>
/// Волна 6 — три Среды девелопера над одним открытым проектом (тот же документ, журнал и участок; переключение — 1 клик из
/// верхнего бара или начального экрана). Среда определяет, какие инструменты в фокусе: участок (рельеф, точки, зоны, сети,
/// «НО»), объём и конструктив (посадка, каркас, КЭ-расчёт, армирование, смета) или планировки (пакеты этажей, ядра, лестницы,
/// подвал с вводами сетей участка, мокрые и газовые зоны). Инструменты не удаляются — Среда их показывает или убирает из фокуса.
/// Каркас Среды 3 — сводка по зданию (<see cref="AnalyzeInteriors"/>); квартирография — следующая волна.
/// </summary>
public static class WorkspaceEnvironmentService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static readonly IReadOnlyList<EnvironmentDescriptor> All =
    [
        new(DeveloperEnvironment.Site, 1, "W6EnvSite", "W6EnvSiteShort", "W6EnvSiteHint", "\uE707", "#10B981", SiteEditing: true, SiteLayers: true, StructuralTools: false, FloorPackages: false),
        new(DeveloperEnvironment.Building, 2, "W6EnvBuilding", "W6EnvBuildingShort", "W6EnvBuildingHint", "\uE9D9", "#0A84FF", SiteEditing: false, SiteLayers: true, StructuralTools: true, FloorPackages: false),
        new(DeveloperEnvironment.Interiors, 3, "W6EnvInteriors", "W6EnvInteriorsShort", "W6EnvInteriorsHint", "\uE8A9", "#FF9F0A", SiteEditing: false, SiteLayers: false, StructuralTools: false, FloorPackages: true)
    ];

    public static EnvironmentDescriptor Of(DeveloperEnvironment env) => All.First(d => d.Id == env);

    public static DeveloperEnvironment Parse(string? s, DeveloperEnvironment fallback = DeveloperEnvironment.Building) => s?.Trim().ToLowerInvariant() switch
    {
        "site" or "1" or "участок" => DeveloperEnvironment.Site,
        "building" or "2" or "объём" or "объем" => DeveloperEnvironment.Building,
        "interiors" or "3" or "планировки" => DeveloperEnvironment.Interiors,
        _ => fallback
    };

    public static string Code(DeveloperEnvironment env) => env switch
    {
        DeveloperEnvironment.Site => "site",
        DeveloperEnvironment.Interiors => "interiors",
        _ => "building"
    };

    // ═════════════════════════════ Среда 3: каркас ═════════════════════════════

    /// <summary>
    /// Сводка здания для Среды 3: этажи (с повторами), пакеты по 5 этажей (или заданные), ядра (core) и лестницы по пакетам, подвал
    /// (элементы ниже нуля), вводы сетей участка (ближайшая точка контура здания к трассе каждого вида) и проверки:
    /// газ не вводится в подвал (СП 62.13330, п. 5.2.1), вода и канализация — через подвал/техподполье, мокрые зоны (санузлы) —
    /// над мокрыми зонами нижнего пакета (вертикальные стояки), газифицированные кухни — с окном наружу.
    /// </summary>
    public static InteriorsSummary AnalyzeInteriors(IReadOnlyList<ArchitecturalElement> elements, GpzuSitePlan? site, int packageSize = 5, IReadOnlyList<(int From, int To, string Layout)>? packages = null)
    {
        var building = elements.Where(GpzuComplianceVerifier.IsBuildingPart).ToList();
        int levels = building.Count == 0 ? 0 : building.Max(e => e.LevelIndex + Math.Max(1, e.RepeatLevels));
        bool basement = building.Any(e => e.TopY < -0.5 && e.Category is not BimCategory.Foundation);
        var cores = building.Where(e => e.Category == BimCategory.Core).ToList();
        var stairs = elements.Where(e => e.Category == BimCategory.Stairs).ToList();
        var summary = new InteriorsSummary { Levels = levels, Cores = cores.Count, StairFlights = stairs.Count, HasBasement = basement };

        var ranges = packages is { Count: > 0 } ? packages.Select(p => (p.From, p.To, p.Layout)).ToList() : DefaultPackages(levels, packageSize);
        int number = 0;
        foreach (var (from, to, layout) in ranges)
        {
            bool In(ArchitecturalElement e) => e.LevelIndex + Math.Max(1, e.RepeatLevels) - 1 >= from - 1 && e.LevelIndex <= to - 1;
            int c = cores.Count(In);
            int s = stairs.Count(In);
            double area = building.Where(e => e.Category is BimCategory.Slab && In(e)).Sum(e => GpzuComplianceVerifier.Area(ElementGeometryBuilder.WorldOutline(e)));
            // Лифты — по ядрам: ядро ≥ 6 м по меньшей стороне — 2 лифта, иначе 1 (оценка до квартирографии).
            int lifts = cores.Where(In).Sum(k => Math.Min(k.Width, k.Depth) >= 6 ? 2 : 1);
            summary.Packages.Add(new FloorPackage(++number, from, to, layout, c, s, lifts, area));
        }

        // Вводы сетей: ближайшая к трассе точка контура здания (стена первого этажа / фундамент).
        if (site is not null && building.Count > 0)
        {
            var outline = EditableSiteTerrainService.ConvexHull(building.Where(e => e.LevelIndex == building.Min(b => b.LevelIndex))
                .SelectMany(e => ElementGeometryBuilder.WorldOutline(e)).Select(p => new SitePoint(p.X, p.Z)).ToList());
            foreach (var zone in site.Zones.Where(z => z.Zone.IsNetwork && z.Lines.Count > 0))
            {
                double best = double.MaxValue;
                SitePoint at = default;
                foreach (var line in zone.Lines)
                    for (int i = 0; i < outline.Count; i++)
                    {
                        var a = outline[i];
                        var b = outline[(i + 1) % outline.Count];
                        for (int k = 0; k <= 8; k++)
                        {
                            var p = new SitePoint(a.X + (b.X - a.X) * k / 8, a.Z + (b.Z - a.Z) * k / 8);
                            double d = SiteBoundaryBuilder.DistanceToLine(line, p.X, p.Z);
                            if (d < best) { best = d; at = p; }
                        }
                    }
                var kind = zone.Zone.Kind;
                bool gas = kind == GpzuZoneKind.GasPipeline;
                bool intoBasement = !gas && kind is GpzuZoneKind.WaterSupply or GpzuZoneKind.Sewer or GpzuZoneKind.HeatNetwork or GpzuZoneKind.PowerCable or GpzuZoneKind.Communication;
                string rule = gas ? "Газопровод вводится в помещение 1-го этажа (кухня / котельная), ввод в подвал запрещён — СП 62.13330.2011, п. 5.2.1"
                    : kind is GpzuZoneKind.WaterSupply ? "Ввод водопровода — в подвал / техподполье (водомерный узел), СП 30.13330"
                    : kind is GpzuZoneKind.Sewer ? "Выпуск канализации — из подвала / техподполья, СП 30.13330"
                    : kind is GpzuZoneKind.HeatNetwork ? "Ввод теплосети — в ИТП подвала, СП 124.13330"
                    : "Ввод кабеля — в электрощитовую / помещение связи 1-го этажа или подвала";
                summary.Entries.Add(new UtilityEntry(kind, SiteBoundaryBuilder.StyleOf(kind).Title, at, best, intoBasement && basement, true, rule));
            }
            bool gasPresent = summary.Entries.Any(e => e.Kind == GpzuZoneKind.GasPipeline);
            summary.Checks.Add(gasPresent
                ? new ZoneCheck(true, "Газовый ввод — не в подвал", basement
                    ? "Подвал есть: ввод газопровода закладывается в помещение 1-го этажа с окном и вентиляцией, в обход подвала (СП 62.13330, п. 5.2.1)."
                    : "Подвала нет: ввод газопровода — в помещение 1-го этажа (СП 62.13330, п. 5.2.1).")
                : new ZoneCheck(true, "Газ на участке", "Газопровода рядом со зданием нет — дом без газа, кухни электрические."));
            var water = summary.Entries.FirstOrDefault(e => e.Kind == GpzuZoneKind.WaterSupply);
            summary.Checks.Add(water is null
                ? new ZoneCheck(false, "Ввод водопровода", "Трасса водопровода на участке не найдена — нужны ТУ водоканала на подключение.")
                : new ZoneCheck(true, "Ввод водопровода", string.Create(Ru, $"Ввод у точки ({water.At.X:0.0}; {water.At.Z:0.0}) м, до трассы {water.DistanceToNetwork:0.0} м{(basement ? ", водомерный узел в подвале" : ", водомерный узел в техподполье / 1-м этаже")}.")));
        }
        // Мокрые зоны — стояки: пакеты с разной планировкой требуют проверки соосности санузлов (до квартирографии — предупреждение).
        if (summary.Packages.Select(p => p.Layout).Distinct().Count() > 1)
            summary.Checks.Add(new ZoneCheck(false, "Мокрые зоны между пакетами", "Планировки пакетов различаются: санузлы и кухни верхнего пакета должны стоять над мокрыми зонами нижнего (стояки) — проверьте после квартирографии (Волна 7)."));
        else if (summary.Packages.Count > 0)
            summary.Checks.Add(new ZoneCheck(true, "Мокрые зоны между пакетами", "Одна планировка на все этажи — стояки сквозные."));
        if (summary.Cores == 0 && levels >= 6)
            summary.Checks.Add(new ZoneCheck(false, "Тамбурно-лифтовой узел", string.Create(Ru, $"Здание {levels} эт. без ядра (лестнично-лифтового узла): при высоте > 28 м — незадымляемая лестница Н2/Н3 с тамбур-шлюзом (СП 1.13130).")));
        else if (summary.Cores > 0)
            summary.Checks.Add(new ZoneCheck(true, "Тамбурно-лифтовой узел", $"Ядер: {summary.Cores}; лифтов (оценка): {summary.Packages.Select(p => p.Lifts).DefaultIfEmpty(0).Max()} на этаж."));
        return summary;
    }

    /// <summary>Пакеты по умолчанию: 1…5, 6…10, … (последний — до верхнего этажа); планировки «Тип A», «Тип B», …</summary>
    public static List<(int From, int To, string Layout)> DefaultPackages(int levels, int size = 5)
    {
        var list = new List<(int, int, string)>();
        if (levels <= 0) return list;
        size = Math.Max(1, size);
        for (int from = 1, k = 0; from <= levels; from += size, k++)
            list.Add((from, Math.Min(levels, from + size - 1), "Тип " + (char)('A' + Math.Min(k, 25))));
        return list;
    }
}
