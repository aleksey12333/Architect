using AIArchitectStudio.Models;
using Microsoft.UI.Dispatching;

namespace AIArchitectStudio.Services;

/// <summary>
/// BIM-аналитика: смета пересчитывается автоматически после экспресс-расчёта конструктива (<see cref="StructuralAnalysisService"/>
/// пересчитывается при любом изменении сцены), при смене расценок <see cref="SettingsService"/> и языка интерфейса.
/// Показатели считаются по каждой постройке (BuildingGroupId) и «Итого по участку»: объём бетона (по бетонным слоям пирогов),
/// ведомость расхода стали по классам и диаметрам (нормы на 1 м³ с учётом K_исп), объёмы и площади слоёв многослойных конструкций.
/// Параметрические элементы учитываются с RepeatLevels и сужением TaperScalePerFloor.
/// </summary>
public sealed class BimAnalyticsService
{
    /// <summary>Толщина стенок и дна чаши бассейна, м.</summary>
    private const double PoolShellThickness = 0.2;

    /// <summary>Отступ уровня воды от верха борта бассейна, м.</summary>
    private const double PoolFreeboard = 0.2;

    private readonly SceneStateService _scene;
    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly StructuralAnalysisService _structure;
    private readonly DispatcherQueue? _dispatcher;
    private bool _pending;

    public BimSpecification Current { get; private set; } = BimSpecification.Empty;

    /// <summary>Смета пересчитана (всегда в UI-потоке).</summary>
    public event EventHandler<BimSpecification>? SpecificationChanged;

    public BimAnalyticsService(SceneStateService scene, SettingsService settings, LocalizationService loc, StructuralAnalysisService structure)
    {
        _scene = scene;
        _settings = settings;
        _loc = loc;
        _structure = structure;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Сцена → расчёт конструктива → смета (армирование зависит от K_исп).
        _structure.AnalysisChanged += (_, _) => Recalculate();
        _loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.Language)) Recalculate();
        };
        Recalculate();
    }

    /// <summary>Пересчёт сметы. Из фоновых потоков (проверка API-ключей сохраняет настройки) — через UI-диспетчер.</summary>
    public void Recalculate()
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            if (_pending) return;
            _pending = true;
            _dispatcher.TryEnqueue(() =>
            {
                _pending = false;
                Recalculate();
            });
            return;
        }

        var s = _settings.Current;
        var analysis = _structure.Current;
        Current = Calculate(_scene.Elements, _scene.BuildingGroups, s.ConcretePricePerM3, s.GlazingPricePerM2, _loc.Get,
            analysis.UtilizationById, s.RebarPricePerTon, s.StructuralLoads.Rebar, PunchingReinforcedIds(analysis));
        foreach (var line in Current.Lines) line.Culture = _loc.Culture;
        SpecificationChanged?.Invoke(this, Current);
    }

    /// <summary>Плиты и фундаменты с узлами продавливания, требующими каркасов Кр.</summary>
    public static HashSet<string> PunchingReinforcedIds(StructuralAnalysisResult analysis) =>
        analysis.PunchingNodes.Where(n => n.Status != PunchingStatus.Ok).Select(n => n.SlabId).ToHashSet();

    /// <summary>Рассчитывает смету по произвольному набору элементов (используется и для отчётов экспорта).</summary>
    /// <param name="translate">Функция локализации подписей (ключ → текст).</param>
    public static BimSpecification Calculate(
        IEnumerable<ArchitecturalElement> elements,
        IEnumerable<BuildingGroup> groups,
        double concretePricePerM3,
        double glazingPricePerM2,
        Func<string, string> translate,
        IReadOnlyDictionary<string, double>? utilization = null,
        double rebarPricePerTon = 0,
        RebarClass rebarClass = RebarClass.A400,
        IReadOnlySet<string>? punchingReinforced = null)
    {
        var spec = new BimSpecification
        {
            ConcretePricePerM3 = concretePricePerM3,
            GlazingPricePerM2 = glazingPricePerM2,
            RebarPricePerTon = rebarPricePerTon,
            RebarClass = rebarClass
        };
        var all = elements.ToList();
        var groupList = groups.ToList();
        var groupOrder = groupList.Select(g => g.Id).Concat(all.Select(e => e.BuildingGroupId)).Distinct().ToList();

        foreach (var groupId in groupOrder)
        {
            var items = all.Where(e => e.BuildingGroupId == groupId).ToList();
            if (items.Count == 0) continue;

            var m = MeasureBuilding(groupId, items, concretePricePerM3, glazingPricePerM2, utilization, rebarPricePerTon, punchingReinforced);
            spec.Buildings.Add(m);

            string title = groupList.FirstOrDefault(g => g.Id == groupId)?.Name is { Length: > 0 } name ? name : groupId;
            spec.Lines.Add(new BimSpecificationLine { Section = groupId, Item = title, IsHeader = true });
            AddBuildingLines(spec.Lines, m, translate);
        }

        if (spec.Buildings.Count > 0)
        {
            var total = new BuildingMetrics
            {
                GroupId = translate("SpecSiteTotal"),
                Storeys = spec.Buildings.Max(b => b.Storeys),
                FootprintArea = spec.Buildings.Sum(b => b.FootprintArea),
                FloorArea = spec.Buildings.Sum(b => b.FloorArea),
                ConcreteVolume = spec.TotalConcrete,
                GlazingArea = spec.TotalGlazing,
                PoolVolume = spec.TotalPool,
                ConcreteCost = spec.Buildings.Sum(b => b.ConcreteCost),
                GlazingCost = spec.Buildings.Sum(b => b.GlazingCost),
                SteelCost = spec.Buildings.Sum(b => b.SteelCost)
            };
            foreach (var b in spec.Buildings)
            {
                total.Steel.Add(b.Steel);
                foreach (var (key, steel) in b.SteelByType)
                {
                    if (!total.SteelByType.TryGetValue(key, out var acc)) total.SteelByType[key] = acc = new SteelBreakdown();
                    acc.Add(steel);
                }
                foreach (var (key, q) in b.Layers)
                {
                    var cur = total.Layers.GetValueOrDefault(key);
                    total.Layers[key] = (cur.Volume + q.Volume, cur.Area + q.Area);
                }
            }
            spec.SiteTotal = total;
            spec.Lines.Add(new BimSpecificationLine { Section = total.GroupId, Item = total.GroupId, IsHeader = true, IsTotal = true });
            AddBuildingLines(spec.Lines, total, translate);
            spec.Lines.Add(new BimSpecificationLine
            {
                Section = total.GroupId, Item = translate("SpecCost"), IsTotal = true, Cost = total.TotalCost
            });
            AddSteelStatement(spec.Lines, total, rebarClass, rebarPricePerTon, translate);
            AddLayerLines(spec.Lines, total, translate);
        }
        return spec;
    }

    private static BuildingMetrics MeasureBuilding(string groupId, List<ArchitecturalElement> items, double concretePrice, double glazingPrice,
        IReadOnlyDictionary<string, double>? utilization, double rebarPrice, IReadOnlySet<string>? punchingReinforced)
    {
        var m = new BuildingMetrics
        {
            GroupId = groupId,
            // Этажность — по несущим и ограждающим конструкциям (кровля и купол не добавляют этаж).
            Storeys = StoreyCount(items)
        };

        // Сквозные отверстия плит (лестницы, лифты, шахты) по хостам: площадь вычитается из бетона и перекрытий.
        var holeArea = new Dictionary<string, double>();
        foreach (var hole in items.Where(e => e.Category == BimCategory.SlabOpening))
        {
            var host = items.FirstOrDefault(h => h.Id == hole.HostElementId);
            if (host is null) continue;
            holeArea[host.Id] = Math.Min(host.PlanArea(), holeArea.GetValueOrDefault(host.Id) + Math.Min(hole.PlanArea(), host.PlanArea()));
        }

        foreach (var e in items)
        {
            double factor = e.RepeatAreaFactor();
            int repeat = Math.Max(1, e.RepeatLevels);

            double concrete = ConcreteVolume(e, items, holeArea.GetValueOrDefault(e.Id));
            m.ConcreteVolume += concrete;

            if (ElementTypes.IsGlazingType(e.Category))
                m.GlazingArea += GlazingArea(e) * (e.Polygon2D.Count >= 3 ? TaperedPerimeterFactor(e) : repeat);

            if (e.Category == BimCategory.Pool)
                m.PoolVolume += Math.Max(0, e.Width - 2 * PoolShellThickness) * Math.Max(0, e.Depth - 2 * PoolShellThickness)
                                * Math.Max(0, e.Height - PoolShellThickness - PoolFreeboard);

            // Полезная площадь — все перекрытия (slab, консольные плиты) по всем повторяемым этажам.
            if (e.Category is BimCategory.Slab or BimCategory.CantileverSlab)
                m.FloorArea += Math.Max(0, e.PlanArea() * factor - holeArea.GetValueOrDefault(e.Id));

            // Армирование: нормы на 1 м³ армируемого бетона с учётом K_исп.
            if (concrete > 0 && ReinforcementCalculator.IsReinforced(e))
            {
                double k = utilization?.GetValueOrDefault(e.Id) ?? 0;
                var steel = ReinforcementCalculator.Compute(e, ReinforcementCalculator.ReinforcedVolume(e, concrete), k, punchingReinforced?.Contains(e.Id) == true);
                m.Steel.Add(steel);
                string key = SteelGroupKey(e.Type);
                if (!m.SteelByType.TryGetValue(key, out var acc)) m.SteelByType[key] = acc = new SteelBreakdown();
                acc.Add(steel);
            }

            if (e.Layers.Count > 0) MeasureLayers(e, items, holeArea.GetValueOrDefault(e.Id), m.Layers);
        }

        m.FootprintArea = FootprintArea(items);
        // Одноэтажная постройка без перекрытий (баня, павильон): полезная площадь — по площади застройки.
        if (m.FloorArea <= 0) m.FloorArea = m.FootprintArea * Math.Max(1, m.Storeys);

        m.ConcreteCost = m.ConcreteVolume * concretePrice;
        m.GlazingCost = m.GlazingArea * glazingPrice;
        m.SteelCost = m.Steel.Total / 1000.0 * rebarPrice;
        return m;
    }

    /// <summary>
    /// Объём бетона элемента: без пирога — по материалу (монолит), с пирогом — только бетонные слои (несущий ж/б и бетонная
    /// подготовка; стяжки, утеплитель и изоляция — отдельными строками). Стены — за вычетом проёмов, плиты — за вычетом отверстий.
    /// </summary>
    public static double ConcreteVolume(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> items, double holes)
    {
        if (!ElementTypes.IsConcreteType(e.Category) && !(e.Category == BimCategory.Roof && e.IsFlatRoof)) return 0;
        double factor = e.RepeatAreaFactor();
        if (e.Layers.Count > 0)
        {
            double t = MaterialLayers.ConcreteThickness(e);
            if (t <= 0) return 0;
            if (e.Category == BimCategory.Wall) return WallArea(e, items) * t;
            return Math.Max(0, e.PlanArea() * factor - holes) * t;
        }
        if (!IsMonolithicMaterial(e.Material)) return 0;
        // Плоская кровля учитывается как ж/б плита покрытия, только если она монолитная.
        if (e.Category == BimCategory.Roof && !(e.Material ?? string.Empty).Contains("concrete", StringComparison.OrdinalIgnoreCase)) return 0;
        return e.Type switch
        {
            ElementTypes.Pool => PoolShellVolume(e),
            ElementTypes.Wall => WallArea(e, items) * e.Depth,
            ElementTypes.Core => CoreVolume(e) * factor,
            ElementTypes.Shell => ShellVolume(e),
            _ => Math.Max(0, e.PlanArea() * factor - holes) * e.Height
        };
    }

    /// <summary>Площадь стены за вычетом привязанных проёмов (с учётом повторов этажей).</summary>
    private static double WallArea(ArchitecturalElement wall, IReadOnlyList<ArchitecturalElement> items)
    {
        double gross = wall.Width * CurvedDesign.LengthFactor(wall) * wall.Height * Math.Max(1, wall.RepeatLevels);
        double openings = items
            .Where(o => o.HostElementId == wall.Id && ElementTypes.IsHostedType(o.Category))
            .Sum(o => Math.Min(o.Width, wall.Width) * Math.Min(o.Height, wall.Height));
        return Math.Max(0, gross - openings);
    }

    /// <summary>Слои пирога: м³ для утеплителя, стяжек, отделки и неармированных несущих слоёв; м² для гидроизоляции.</summary>
    private static void MeasureLayers(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> items, double holes,
        Dictionary<(string Name, MaterialLayerRole Role), (double Volume, double Area)> target)
    {
        double area = e.Category == BimCategory.Wall ? WallArea(e, items) : Math.Max(0, e.PlanArea() * e.RepeatAreaFactor() - holes);
        foreach (var layer in e.Layers)
        {
            // Бетонные слои уже учтены в объёме бетона.
            if (layer.IsConcrete && layer.Role is MaterialLayerRole.Structural or MaterialLayerRole.Screed && !layer.Name.Contains("стяжк", StringComparison.OrdinalIgnoreCase)
                && !layer.Name.Contains("разуклон", StringComparison.OrdinalIgnoreCase)) continue;
            var key = (layer.Name, layer.Role);
            var cur = target.GetValueOrDefault(key);
            target[key] = (cur.Volume + area * layer.ThicknessM, cur.Area + area);
        }
    }

    public static string SteelGroupKey(string type) => type switch
    {
        ElementTypes.Foundation => "SteelFoundation",
        ElementTypes.Column => "SteelColumns",
        ElementTypes.Beam => "SteelBeams",
        ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Roof or ElementTypes.Terrace => "SteelSlabs",
        ElementTypes.Wall or ElementTypes.Core => "SteelWalls",
        _ => "SteelOther"
    };

    /// <summary>Деревянные (баня, терраса), стальные и стеклянные конструкции в объём бетона не входят.</summary>
    private static bool IsMonolithicMaterial(string? material)
    {
        string m = (material ?? string.Empty).ToLowerInvariant();
        return !(m.Contains("wood") || m.Contains("timber") || m.Contains("steel") || m.Contains("glass"));
    }

    private static int StoreyCount(List<ArchitecturalElement> items)
    {
        var storeyItems = items.Where(e => e.Category is not (BimCategory.Roof or BimCategory.Shell or BimCategory.Asset)).ToList();
        if (storeyItems.Count == 0) storeyItems = items;
        return storeyItems.Max(e => e.LevelIndex + Math.Max(1, e.RepeatLevels));
    }

    /// <summary>
    /// Площадь застройки: по контуру фундамента; без фундамента — по габариту стен 1 этажа;
    /// иначе — по плитам 1 этажа или общему габариту постройки.
    /// </summary>
    private static double FootprintArea(List<ArchitecturalElement> items)
    {
        double foundation = items.Where(e => e.Category == BimCategory.Foundation && e.LevelIndex == 0).Sum(e => e.PlanArea());
        if (foundation > 0) return foundation;

        var groundWalls = items.Where(e => e.LevelIndex == 0 && e.Category is BimCategory.Wall or BimCategory.CurtainWall).ToList();
        if (groundWalls.Count >= 3)
        {
            var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(groundWalls);
            return (maxX - minX) * (maxZ - minZ);
        }

        var groundSlabs = items.Where(e => e.LevelIndex == 0 && e.Category is BimCategory.Slab or BimCategory.CantileverSlab).ToList();
        if (groundSlabs.Count > 0) return groundSlabs.Max(e => e.PlanArea());

        var (x0, x1, z0, z1) = SceneStateService.Bounds(items);
        return (x1 - x0) * (z1 - z0);
    }

    /// <summary>Ядро жёсткости — полая коробка: та же толщина стен, что и в 3D-геометрии.</summary>
    private static double CoreVolume(ArchitecturalElement core)
    {
        double t = Math.Clamp(Math.Min(core.Width, core.Depth) * 0.06, 0.25, 0.5);
        double inner = Math.Max(0, core.Width - 2 * t) * Math.Max(0, core.Depth - 2 * t);
        return (core.Width * core.Depth - inner) * core.Height;
    }

    /// <summary>Купольная оболочка толщиной 0.15 м: площадь поверхности полуэллипсоида (формула Кнуда Томсена) × толщина.</summary>
    private static double ShellVolume(ArchitecturalElement shell)
    {
        const double thickness = 0.15, p = 1.6075;
        double a = shell.Width / 2, b = shell.Depth / 2, c = shell.Height;
        double area = 4 * Math.PI * Math.Pow((Math.Pow(a * b, p) + Math.Pow(a * c, p) + Math.Pow(b * c, p)) / 3, 1 / p) / 2;
        return area * thickness;
    }

    /// <summary>Площадь остекления одного этажа: периметр контура × высота (полигональный витраж) или ширина × высота.</summary>
    internal static double GlazingArea(ArchitecturalElement e)
    {
        if (e.Polygon2D.Count < 3) return e.Width * e.Height;
        double perimeter = 0;
        for (int i = 0; i < e.Polygon2D.Count; i++)
        {
            var a = e.Polygon2D[i];
            var b = e.Polygon2D[(i + 1) % e.Polygon2D.Count];
            perimeter += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        }
        return perimeter * e.Height;
    }

    /// <summary>Сумма линейных масштабов по этажам (периметр сужается пропорционально TaperScalePerFloor).</summary>
    internal static double TaperedPerimeterFactor(ArchitecturalElement e)
    {
        int count = Math.Max(1, e.RepeatLevels);
        double taper = e.TaperScalePerFloor <= 0 ? 1 : e.TaperScalePerFloor, scale = 1, sum = 0;
        for (int i = 0; i < count; i++, scale *= taper) sum += scale;
        return sum;
    }

    /// <summary>Объём бетона чаши бассейна: дно + 4 стенки толщиной 0.2 м.</summary>
    private static double PoolShellVolume(ArchitecturalElement pool)
    {
        const double t = PoolShellThickness;
        double bottom = pool.Width * pool.Depth * t;
        double walls = 2 * (pool.Width + pool.Depth - 2 * t) * pool.Height * t;
        return bottom + walls;
    }

    private static void AddBuildingLines(List<BimSpecificationLine> lines, BuildingMetrics m, Func<string, string> t)
    {
        string s = m.GroupId;
        lines.Add(new() { Section = s, Item = t("SpecLevels"), Quantity = m.Storeys, Unit = t("UnitPcs") });
        lines.Add(new() { Section = s, Item = t("SpecFootprint"), Quantity = m.FootprintArea, Unit = t("UnitM2") });
        lines.Add(new() { Section = s, Item = t("SpecFloorArea"), Quantity = m.FloorArea, Unit = t("UnitM2") });
        lines.Add(new() { Section = s, Item = t("SpecConcrete"), Quantity = m.ConcreteVolume, Unit = t("UnitM3"), Cost = m.ConcreteCost });
        if (m.Steel.Total > 0)
            lines.Add(new() { Section = s, Item = t("SpecSteel"), Quantity = m.Steel.Total / 1000.0, Unit = t("UnitT"), Cost = m.SteelCost });
        lines.Add(new() { Section = s, Item = t("SpecGlazing"), Quantity = m.GlazingArea, Unit = t("UnitM2"), Cost = m.GlazingCost });
        if (m.PoolVolume > 0)
            lines.Add(new() { Section = s, Item = t("SpecPool"), Quantity = m.PoolVolume, Unit = t("UnitM3") });
    }

    /// <summary>Ведомость расхода стали по участку: классы и диаметры, прокат С245, группы конструкций, итог в тоннах и стоимость.</summary>
    private static void AddSteelStatement(List<BimSpecificationLine> lines, BuildingMetrics total, RebarClass rebarClass, double price, Func<string, string> t)
    {
        var steel = total.Steel;
        if (steel.Total <= 0) return;
        string s = t("SpecSteelHeader");
        string kg = t("UnitKg");
        lines.Add(new() { Section = s, Item = s, IsHeader = true });
        foreach (var (d, mass) in steel.A240)
            lines.Add(new() { Section = s, Item = $"А240 Ø{d}", Quantity = Math.Round(mass), Unit = kg });
        lines.Add(new() { Section = s, Item = t("SpecSteelA240"), Quantity = Math.Round(steel.A240Total), Unit = kg, IsTotal = true });
        string working = StructuralNorms.Title(rebarClass == RebarClass.A240 ? RebarClass.A400 : rebarClass);
        foreach (var (d, mass) in steel.Working)
            lines.Add(new() { Section = s, Item = $"{working} Ø{d}", Quantity = Math.Round(mass), Unit = kg });
        lines.Add(new() { Section = s, Item = string.Format(t("SpecSteelWorking"), working), Quantity = Math.Round(steel.WorkingTotal), Unit = kg, IsTotal = true });
        if (steel.PlatesTotal > 0)
            lines.Add(new() { Section = s, Item = t("SpecSteelPlates") + " (" + string.Join(", ", steel.Plates.Keys.Select(p => "−" + p)) + ")", Quantity = Math.Round(steel.PlatesTotal), Unit = kg });
        foreach (var (key, part) in total.SteelByType.OrderByDescending(p => p.Value.Total))
            lines.Add(new() { Section = s, Item = t(key), Quantity = Math.Round(part.Total), Unit = kg });
        lines.Add(new() { Section = s, Item = t("SpecSteelTotal"), Quantity = steel.Total / 1000.0, Unit = t("UnitT"), Cost = steel.Total / 1000.0 * price, IsTotal = true });
    }

    private static void AddLayerLines(List<BimSpecificationLine> lines, BuildingMetrics total, Func<string, string> t)
    {
        if (total.Layers.Count == 0) return;
        string s = t("SpecLayersHeader");
        lines.Add(new() { Section = s, Item = s, IsHeader = true });
        foreach (var ((name, role), (volume, area)) in total.Layers.OrderBy(p => p.Key.Role).ThenBy(p => p.Key.Name))
        {
            bool membrane = role == MaterialLayerRole.Waterproofing;
            lines.Add(new()
            {
                Section = s,
                Item = name,
                Quantity = membrane ? area : volume,
                Unit = membrane ? t("UnitM2") : t("UnitM3")
            });
        }
    }
}
