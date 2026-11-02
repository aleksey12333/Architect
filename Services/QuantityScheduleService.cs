using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Строка ведомости бетона: группа конструкций, число элементов, объём, м³.</summary>
public sealed record ConcreteQuantity(string GroupKey, int Count, double Volume);

/// <summary>Строка ведомости проката: группа (колонны / балки), сечение, число, длина, м, масса, кг.</summary>
public sealed record RolledSteelQuantity(string GroupKey, string Profile, int Count, double Length, double Mass);

/// <summary>Смета монолитного каркаса: бетон, м³ × цена за м³ + арматура и закладные, т × цена за тонну.</summary>
public sealed record FrameCostEstimate(double ConcreteM3, double RebarKg, double ConcretePricePerM3, double RebarPricePerTon)
{
    public double RebarTonnes => RebarKg / 1000.0;
    public double ConcreteCost => ConcreteM3 * ConcretePricePerM3;
    public double RebarCost => RebarTonnes * RebarPricePerTon;
    public double Total => ConcreteCost + RebarCost;
}

/// <summary>Сводная ведомость объёмов каркаса (окно «Ведомость объёмов»).</summary>
public sealed class QuantitySchedule
{
    public static readonly QuantitySchedule Empty = new();

    public List<ConcreteQuantity> Concrete { get; } = [];
    public double ConcreteTotal => Concrete.Sum(c => c.Volume);

    /// <summary>Арматура и закладные: те же нормы и K_исп, что и смета (<see cref="BimAnalyticsService"/>).</summary>
    public SteelBreakdown Rebar { get; } = new();
    public RebarClass WorkingClass { get; init; } = RebarClass.A400;

    public int WindowCount { get; set; }
    public double WindowArea { get; set; }
    public int CurtainWallCount { get; set; }
    public double CurtainWallArea { get; set; }
    public double GlazingTotal => WindowArea + CurtainWallArea;

    public List<RolledSteelQuantity> RolledSteel { get; } = [];
    public double RolledSteelTotal => RolledSteel.Sum(r => r.Mass);

    public int ElementCount { get; set; }

    /// <summary>Общие (пользовательские) параметры, заданные хотя бы у одного элемента: варианты группировки ведомости, по имени.</summary>
    public IReadOnlyList<ParameterDefinition> UserParameters { get; init; } = [];
}

/// <summary>Строка группировки по свойству: значение (пусто — «не задано»), число элементов и объёмы ведомости этих элементов.</summary>
public sealed record ParameterQuantityGroup(ParameterValue Value, int Count, double Concrete, double Rebar, double Glazing, double RolledSteel)
{
    public bool IsUnset => Value.IsEmpty;
}

/// <summary>Ведомость, разбитая по значениям одного общего параметра: сумма строк ≡ итогам ведомости.</summary>
public sealed record ParameterQuantityBreakdown(ParameterDefinition Definition, IReadOnlyList<ParameterQuantityGroup> Groups);

/// <summary>
/// Ведомость объёмов: бетон по группам конструкций (плиты, колонны, балки, фундаменты, стены и ядра, прочее), арматура по классам
/// и диаметрам, остекление (окна и витражи) и примерная масса стального проката колонн и балок. Чистая функция по снимку сцены:
/// объёмы бетона и стали считаются теми же формулами, что и смета, поэтому итоги ведомости и сметы совпадают.
/// </summary>
public static class QuantityScheduleService
{
    /// <summary>Плотность проката, кг/м³.</summary>
    public const double SteelDensity = 7850;

    public static readonly string[] ConcreteGroups = ["QtySlabs", "QtyColumns", "QtyBeams", "QtyFoundations", "QtyWalls", "QtyOther"];

    public static QuantitySchedule Calculate(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyDictionary<string, double>? utilization = null,
        IReadOnlySet<string>? punchingReinforced = null, RebarClass rebarClass = RebarClass.A400, ParameterRegistry? registry = null)
    {
        var acc = new Accumulator(new QuantitySchedule { WorkingClass = rebarClass, ElementCount = elements.Count, UserParameters = UsedParameters(elements, registry) });
        var holeArea = HoleAreas(elements);
        foreach (var e in elements) acc.Add(e, elements, holeArea, utilization, punchingReinforced);
        return acc.Finish();
    }

    /// <summary>
    /// Ведомость по значениям общего параметра (например, «Этап заливки» = «Секция 1»): каждый элемент попадает ровно в одну строку
    /// (без значения — в строку «не задано»), объёмы считаются теми же формулами, что и <see cref="Calculate"/>, поэтому сумма
    /// строк равна итогам ведомости. Строки — по значению (числа по возрастанию, текст по алфавиту), «не задано» — последней.
    /// </summary>
    public static ParameterQuantityBreakdown GroupByParameter(IReadOnlyList<ArchitecturalElement> elements, ParameterDefinition definition,
        IReadOnlyDictionary<string, double>? utilization = null, IReadOnlySet<string>? punchingReinforced = null, RebarClass rebarClass = RebarClass.A400)
    {
        var holeArea = HoleAreas(elements);
        var groups = new Dictionary<ParameterValue, (Accumulator Acc, int Count)>();
        foreach (var e in elements)
        {
            var value = e.GetUserParameter(definition.Guid);
            if (!groups.TryGetValue(value, out var g)) g = (new Accumulator(new QuantitySchedule { WorkingClass = rebarClass }), 0);
            bool measured = g.Acc.Add(e, elements, holeArea, utilization, punchingReinforced);
            // Элементы без значения считаются, только если дают объёмы (проёмы и пустоты строку «не задано» не раздувают).
            groups[value] = (g.Acc, g.Count + (measured || !value.IsEmpty ? 1 : 0));
        }

        var rows = groups.Select(p =>
            {
                var s = p.Value.Acc.Finish();
                return new ParameterQuantityGroup(p.Key, p.Value.Count, s.ConcreteTotal, s.Rebar.Total, s.GlazingTotal, s.RolledSteelTotal);
            })
            .Where(r => !r.IsUnset || r.Count > 0)
            .OrderBy(r => r.IsUnset ? 1 : 0)
            .ThenBy(r => r.Value.StorageType is ParameterStorageType.String or ParameterStorageType.ElementId ? 0 : r.Value.AsDouble())
            .ThenBy(r => r.Value.AsString() ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new ParameterQuantityBreakdown(definition, rows);
    }

    /// <summary>Определения общих параметров, значения которых есть у элементов (неизвестный Guid — запасное определение), по имени.</summary>
    public static IReadOnlyList<ParameterDefinition> UsedParameters(IReadOnlyList<ArchitecturalElement> elements, ParameterRegistry? registry = null)
    {
        registry ??= ParameterRegistry.Shared;
        Dictionary<Guid, ParameterDefinition>? used = null;
        foreach (var e in elements)
        {
            if (e.UserParameters is not { } values) continue;
            used ??= [];
            foreach (var (guid, value) in values)
                if (!used.ContainsKey(guid))
                    used[guid] = registry.Find(guid) ?? ParameterDefinition.Fallback(guid, value);
        }
        return used is null ? [] : [.. used.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Guid)];
    }

    /// <summary>Отверстия плит по хостам — как в смете: площадь вычитается из бетона хоста.</summary>
    private static Dictionary<string, double> HoleAreas(IReadOnlyList<ArchitecturalElement> elements)
    {
        var holeArea = new Dictionary<string, double>();
        foreach (var hole in elements.Where(e => e.Category == BimCategory.SlabOpening))
        {
            var host = elements.FirstOrDefault(h => h.Id == hole.HostElementId);
            if (host is null) continue;
            holeArea[host.Id] = Math.Min(host.PlanArea(), holeArea.GetValueOrDefault(host.Id) + Math.Min(hole.PlanArea(), host.PlanArea()));
        }
        return holeArea;
    }

    /// <summary>Накопление ведомости по элементам (одно для полной ведомости и для каждой строки группировки — одни формулы).</summary>
    private sealed class Accumulator(QuantitySchedule schedule)
    {
        private readonly Dictionary<string, (int Count, double Volume)> _concrete = ConcreteGroups.ToDictionary(g => g, _ => (Count: 0, Volume: 0.0));
        private readonly Dictionary<(string Group, string Profile), (int Count, double Length, double Mass)> _rolled = [];

        /// <summary>Добавляет элемент; true — элемент дал объём бетона, остекления или проката.</summary>
        public bool Add(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> elements, Dictionary<string, double> holeArea,
            IReadOnlyDictionary<string, double>? utilization, IReadOnlySet<string>? punchingReinforced)
        {
            bool measured = false;
            double volume = BimAnalyticsService.ConcreteVolume(e, elements, holeArea.GetValueOrDefault(e.Id));
            if (volume > 0)
            {
                measured = true;
                string group = ConcreteGroup(e.Type);
                var cur = _concrete[group];
                _concrete[group] = (cur.Count + 1, cur.Volume + volume);
                if (ReinforcementCalculator.IsReinforced(e))
                    schedule.Rebar.Add(ReinforcementCalculator.Compute(e, ReinforcementCalculator.ReinforcedVolume(e, volume),
                        utilization?.GetValueOrDefault(e.Id) ?? 0, punchingReinforced?.Contains(e.Id) == true));
            }

            if (e.Category == BimCategory.Window)
            {
                measured = true;
                schedule.WindowCount += Math.Max(1, e.RepeatLevels);
                schedule.WindowArea += BimAnalyticsService.GlazingArea(e) * Math.Max(1, e.RepeatLevels);
            }
            else if (e.Category == BimCategory.CurtainWall)
            {
                measured = true;
                schedule.CurtainWallCount += Math.Max(1, e.RepeatLevels);
                schedule.CurtainWallArea += BimAnalyticsService.GlazingArea(e) * (e.Polygon2D.Count >= 3 ? BimAnalyticsService.TaperedPerimeterFactor(e) : Math.Max(1, e.RepeatLevels));
            }

            if (RolledSection(e) is { } section)
            {
                measured = true;
                var key = (e.Category == BimCategory.Column ? "QtyColumns" : "QtyBeams", section.Label);
                var cur = _rolled.GetValueOrDefault(key);
                _rolled[key] = (cur.Count + Math.Max(1, e.RepeatLevels), cur.Length + section.Length, cur.Mass + section.Area * section.Length * SteelDensity);
            }
            return measured;
        }

        public QuantitySchedule Finish()
        {
            foreach (var group in ConcreteGroups)
                if (_concrete[group] is { Count: > 0 } c) schedule.Concrete.Add(new ConcreteQuantity(group, c.Count, c.Volume));
            foreach (var ((group, profile), r) in _rolled.OrderBy(p => p.Key.Group == "QtyColumns" ? 0 : 1).ThenByDescending(p => p.Value.Mass))
                schedule.RolledSteel.Add(new RolledSteelQuantity(group, profile, r.Count, r.Length, r.Mass));
            return schedule;
        }
    }

    /// <summary>Стоимость монолитного каркаса по ведомости: тот же объём бетона и та же масса стали, что в смете.</summary>
    public static FrameCostEstimate EstimateFrameCost(QuantitySchedule schedule, double concretePricePerM3, double rebarPricePerTon) =>
        new(schedule.ConcreteTotal, schedule.Rebar.Total, Math.Max(0, concretePricePerM3), Math.Max(0, rebarPricePerTon));

    public static string ConcreteGroup(string type) => type switch
    {
        ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Roof or ElementTypes.Terrace => "QtySlabs",
        ElementTypes.Column => "QtyColumns",
        ElementTypes.Beam => "QtyBeams",
        ElementTypes.Foundation => "QtyFoundations",
        ElementTypes.Wall or ElementTypes.Core => "QtyWalls",
        _ => "QtyOther"
    };

    /// <summary>Стальной элемент каркаса: колонна или балка из стали либо двутавр.</summary>
    public static bool IsRolledSteel(ArchitecturalElement e)
    {
        if (e.Category is not (BimCategory.Column or BimCategory.Beam)) return false;
        string m = (e.Material ?? string.Empty).ToLowerInvariant();
        return m.Contains("steel") || m.Contains("metal") || m.Contains("стал") || e.EffectiveProfile == ProfileTypes.IBeam;
    }

    /// <summary>
    /// Сечение проката, м², длина с повторами этажей, м, и подпись сечения (мм). Колонна: сечение W × D, длина H; балка: сечение
    /// D × H, длина W (у кольцевой балки — периметр Polygon2D). Прямоугольник и круг стального элемента — замкнутый гнутый профиль
    /// (трубы прямоугольного и круглого сечения), толщины стенок и полок — заданные или по пропорциям сечения, как в 3D-геометрии.
    /// </summary>
    public static (double Area, double Length, string Label)? RolledSection(ArchitecturalElement e)
    {
        if (!IsRolledSteel(e)) return null;
        bool column = e.Category == BimCategory.Column;
        double b = column ? e.Width : e.Depth;
        double h = column ? e.Depth : e.Height;
        double length = column ? e.Height : e.Polygon2D.Count >= 3 ? Perimeter(e.Polygon2D) : e.Width;
        length *= Math.Max(1, e.RepeatLevels);
        if (b <= 0 || h <= 0 || length <= 0) return null;

        string profile = column ? e.EffectiveProfile : ProfileTypes.Normalize(e.ProfileType);
        var (wall, web) = e.ProfileThicknesses(b, h);
        static string Mm(double m) => (m * 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        switch (profile)
        {
            case ProfileTypes.IBeam:
                return (2 * b * wall + Math.Max(0, h - 2 * wall) * web, length, $"I {Mm(h)}×{Mm(b)}×{Mm(web)}/{Mm(wall)}");
            case ProfileTypes.Circle:
            case ProfileTypes.Pipe:
            {
                double d = (b + h) / 2, inner = Math.Max(0, d - 2 * wall);
                return (Math.PI / 4 * (d * d - inner * inner), length, $"Ø{Mm(d)}×{Mm(wall)}");
            }
            case ProfileTypes.Polygon when column:
                return (e.PlanArea(), length, $"Polygon {Mm(b)}×{Mm(h)}");
            default:
                return (b * h - Math.Max(0, b - 2 * wall) * Math.Max(0, h - 2 * wall), length, $"□{Mm(b)}×{Mm(h)}×{Mm(wall)}");
        }
    }

    private static double Perimeter(IReadOnlyList<Point2D> polygon)
    {
        double sum = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var c = polygon[(i + 1) % polygon.Count];
            sum += Math.Sqrt((c.X - a.X) * (c.X - a.X) + (c.Z - a.Z) * (c.Z - a.Z));
        }
        return sum;
    }
}
