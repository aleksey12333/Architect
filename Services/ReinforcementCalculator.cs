using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Расход стали с разбивкой: А240 (хомуты, конструктивная), рабочая А400/А500С по диаметрам, прокат С245 по толщинам.</summary>
public sealed class SteelBreakdown
{
    /// <summary>А240 (ГОСТ 34028 / 5781-82, гладкая): Ø, мм → кг.</summary>
    public SortedDictionary<int, double> A240 { get; } = [];

    /// <summary>Рабочая арматура периодического профиля (А400 или А500С): Ø, мм → кг.</summary>
    public SortedDictionary<int, double> Working { get; } = [];

    /// <summary>Закладные изделия, прокат С245 (ГОСТ 19903-74): толщина листа, мм → кг.</summary>
    public SortedDictionary<int, double> Plates { get; } = [];

    public double A240Total => A240.Values.Sum();
    public double WorkingTotal => Working.Values.Sum();
    public double PlatesTotal => Plates.Values.Sum();
    public double Total => A240Total + WorkingTotal + PlatesTotal;

    public void Add(SteelBreakdown other, double factor = 1)
    {
        foreach (var (d, kg) in other.A240) A240[d] = A240.GetValueOrDefault(d) + kg * factor;
        foreach (var (d, kg) in other.Working) Working[d] = Working.GetValueOrDefault(d) + kg * factor;
        foreach (var (t, kg) in other.Plates) Plates[t] = Plates.GetValueOrDefault(t) + kg * factor;
    }

    internal void AddA240(int d, double kg) { if (kg > 0) A240[d] = A240.GetValueOrDefault(d) + kg; }
    internal void AddWorking(int d, double kg) { if (kg > 0) Working[d] = Working.GetValueOrDefault(d) + kg; }
    internal void AddPlate(int t, double kg) { if (kg > 0) Plates[t] = Plates.GetValueOrDefault(t) + kg; }
}

/// <summary>
/// Расход арматурной стали железобетонных конструкций по укрупнённым нормам (кг на 1 м³ бетона, СП 63.13330 / ГОСТ 5781-82)
/// с увеличением при высоком коэффициенте использования K_исп из экспресс-расчёта: ρ = ρmin + (ρmax − ρmin)·min(K, 1),
/// при K &gt; 1 — дополнительно ×min(K, 1.8). Рабочие диаметры подбираются по размерам сечения.
/// </summary>
public static class ReinforcementCalculator
{
    /// <summary>Армируемый ж/б элемент: монолитный бетон (по материалу или несущему слою пирога), без кладки, стали и дерева.</summary>
    public static bool IsReinforced(ArchitecturalElement e)
    {
        if (e.Category is not (BimCategory.Foundation or BimCategory.Column or BimCategory.Beam or BimCategory.Slab or BimCategory.CantileverSlab
            or BimCategory.Core or BimCategory.Wall or BimCategory.Shell or BimCategory.Stairs or BimCategory.Roof or BimCategory.Pool or BimCategory.Terrace))
            return false;
        if (e.Layers.Count > 0) return e.Layers.Any(l => l.IsReinforcedConcrete);
        if (e.Category == BimCategory.Roof && !e.IsFlatRoof) return false;
        string m = (e.Material ?? string.Empty).ToLowerInvariant();
        if (m.Contains("steel") || m.Contains("metal") || m.Contains("wood") || m.Contains("timber") || m.Contains("glass") || m.Contains("brick") || m.Contains("block") || m.Contains("tile"))
            return false;
        // Стены и террасы армируются, только если они монолитные (у стен по умолчанию — кладка).
        if (e.Category is BimCategory.Wall or BimCategory.Terrace) return m.Contains("concrete") || m.Contains("бетон") || m.Contains("монолит");
        return true;
    }

    /// <summary>Нормы расхода ρmin…ρmax, кг/м³.</summary>
    public static (double Min, double Max) Norm(string type) => type switch
    {
        ElementTypes.Foundation => (65, 85),
        ElementTypes.Column => (130, 220),
        ElementTypes.Beam => (160, 260),
        ElementTypes.Slab or ElementTypes.Roof or ElementTypes.Terrace => (75, 95),
        ElementTypes.CantileverSlab => (85, 110),
        ElementTypes.Wall or ElementTypes.Core => (45, 75),
        ElementTypes.Shell => (90, 120),
        ElementTypes.Stairs => (80, 110),
        ElementTypes.Pool => (70, 90),
        _ => (60, 80)
    };

    /// <summary>Объём армируемого бетона элемента (м³) по умолчанию: несущие ж/б слои пирога или полный объём.</summary>
    public static double ReinforcedVolume(ArchitecturalElement e, double concreteVolume)
    {
        if (e.Layers.Count == 0) return concreteVolume;
        double total = MaterialLayers.ElementThickness(e);
        double rc = MaterialLayers.ReinforcedThickness(e);
        double concrete = MaterialLayers.ConcreteThickness(e);
        // concreteVolume посчитан по бетонным слоям — доля армируемых слоёв в нём.
        return concrete > 1e-6 ? concreteVolume * rc / concrete : total > 0 ? concreteVolume * rc / total : 0;
    }

    /// <summary>
    /// Строгий контроль минимального армирования (настройка «Нормативы и Расчёт»): расход рабочей арматуры не ниже
    /// max(μmin по СП 63.13330, п. 10.3.6; μ, принятого в проверке прочности) · ρs. По умолчанию выключен — нормы расхода прежние.
    /// </summary>
    public static volatile bool StrictMinimum;

    /// <summary>
    /// μ рабочей арматуры, принятое экспресс-расчётом прочности (колонна 1.5 %, балка 1.2 %, плита и фундамент 0.4 %, стена и ядро
    /// 0.5 %), и μmin по СП 63.13330, п. 10.3.6 (изгибаемые 0.1 %, сжатые — до 0.25 % при l0/i ≥ 87; принято наихудшее).
    /// </summary>
    public static (double Design, double Minimum) MuOf(string type) => type switch
    {
        ElementTypes.Column => (0.015, 0.0025),
        ElementTypes.Beam => (0.012, 0.001),
        ElementTypes.Wall or ElementTypes.Core => (0.005, 0.001),
        ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Roof or ElementTypes.Terrace or ElementTypes.Foundation => (0.004, 0.001),
        _ => (0, 0.001)
    };

    /// <summary>Доля рабочей арматуры в расходе элемента (по разбивке <see cref="Compute"/>).</summary>
    private static double WorkingShare(string type) => type switch
    {
        ElementTypes.Column => 0.78,
        ElementTypes.Beam => 0.76,
        ElementTypes.Wall or ElementTypes.Core => 0.85,
        ElementTypes.Foundation => 0.85,
        ElementTypes.Stairs => 0.7,
        _ => 0.8
    };

    /// <summary>Расход стали, кг/м³, не ниже которого допускает строгий контроль μmin.</summary>
    public static double StrictMinimumRho(string type)
    {
        var (design, minimum) = MuOf(type);
        return Math.Max(design, minimum) * 7850 / WorkingShare(type);
    }

    /// <summary>Сталь элемента по объёму бетона, коэффициенту использования и классу рабочей арматуры.</summary>
    public static SteelBreakdown Compute(ArchitecturalElement e, double volume, double utilization, bool punchingReinforcement = false)
    {
        var steel = new SteelBreakdown();
        if (volume <= 0 || !IsReinforced(e)) return steel;
        var (min, max) = Norm(e.Type);
        double k = double.IsFinite(utilization) ? Math.Max(0, utilization) : 0;
        double rho = min + (max - min) * Math.Min(k, 1);
        if (k > 1) rho *= Math.Min(k, 1.8);
        if (StrictMinimum) rho = Math.Max(rho, StrictMinimumRho(e.Type));
        double kg = rho * volume;
        double h = Math.Max(0.05, MaterialLayers.StructuralThickness(e));

        switch (e.Type)
        {
            case ElementTypes.Foundation:
            {
                // Основная сетка Ø12…16, усиление Ø20…25 (доля растёт с K), каркасы Кр Ø8 А240, анкерные пластины −20.
                double extra = 0.12 + 0.12 * Math.Min(k, 1);
                steel.AddWorking(h <= 0.5 ? 12 : 16, kg * (0.85 - extra));
                steel.AddWorking(h <= 0.8 ? 20 : 25, kg * extra);
                steel.AddA240(8, kg * 0.12);
                steel.AddPlate(20, kg * 0.03);
                break;
            }
            case ElementTypes.Column:
            {
                double side = Math.Min(e.Width, e.Depth);
                steel.AddWorking(side <= 0.4 ? 16 : side <= 0.6 ? 20 : 25, kg * 0.78);
                steel.AddA240(8, kg * 0.19);
                steel.AddPlate(10, kg * 0.03);
                break;
            }
            case ElementTypes.Beam:
            {
                double depth = e.Height;
                steel.AddWorking(depth <= 0.4 ? 16 : depth <= 0.6 ? 20 : depth <= 0.8 ? 25 : 28, kg * 0.76);
                steel.AddA240(depth <= 0.6 ? 8 : 10, kg * 0.21);
                steel.AddPlate(6, kg * 0.03);
                break;
            }
            case ElementTypes.Slab:
            case ElementTypes.CantileverSlab:
            case ElementTypes.Roof:
            case ElementTypes.Terrace:
            {
                // Основная сетка Ø8 (h ≤ 160) / Ø10 (≤ 220) / Ø12, дополнительное армирование Ø12…20, каркасы продавливания Кр Ø8 А240.
                double kr = punchingReinforcement ? 0.16 : 0.08;
                double add = 0.22 + 0.10 * Math.Min(k, 1);
                steel.AddWorking(h <= 0.16 ? 8 : h <= 0.22 ? 10 : 12, kg * (0.98 - kr - add));
                steel.AddWorking(k <= 0.5 ? 12 : k <= 0.95 ? 16 : 20, kg * add);
                steel.AddA240(8, kg * kr);
                steel.AddPlate(6, kg * 0.02);
                break;
            }
            case ElementTypes.Wall:
            case ElementTypes.Core:
            {
                double t = e.Category == BimCategory.Core ? Math.Clamp(Math.Min(e.Width, e.Depth) * 0.06, 0.25, 0.5) : h;
                steel.AddWorking(t <= 0.18 ? 8 : t <= 0.3 ? 10 : 12, kg * 0.85);
                steel.AddA240(6, kg * 0.13);
                steel.AddPlate(10, kg * 0.02);
                break;
            }
            case ElementTypes.Shell:
                steel.AddWorking(10, kg * 0.8);
                steel.AddA240(6, kg * 0.18);
                steel.AddPlate(10, kg * 0.02);
                break;
            case ElementTypes.Stairs:
                steel.AddWorking(12, kg * 0.7);
                steel.AddA240(8, kg * 0.27);
                steel.AddPlate(6, kg * 0.03);
                break;
            default:
                steel.AddWorking(12, kg * 0.8);
                steel.AddA240(8, kg * 0.2);
                break;
        }
        return steel;
    }

    /// <summary>Группа конструкций для ведомости сметы.</summary>
    public static string GroupTitle(string type) => type switch
    {
        ElementTypes.Foundation => "Фундаменты",
        ElementTypes.Column => "Колонны",
        ElementTypes.Beam => "Балки",
        ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Roof or ElementTypes.Terrace => "Плиты перекрытий и покрытия",
        ElementTypes.Wall or ElementTypes.Core => "Стены и ядра жёсткости",
        ElementTypes.Shell => "Оболочки",
        ElementTypes.Stairs => "Лестницы",
        _ => "Прочие"
    };
}
