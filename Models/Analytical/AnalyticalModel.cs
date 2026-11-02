using AIArchitectStudio.Services;

namespace AIArchitectStudio.Models.Analytical;

/// <summary>
/// Расчётная (аналитическая) схема здания, отделённая от 3D-геометрии: узлы с 6 степенями свободы, стержни и пластины со
/// своими жёсткостями, связи «элемент → опоры снизу» и распределение нагрузок перекрытий по грузовым площадям. Строится
/// <see cref="AnalyticalModelBuilder"/> по элементам сцены; <see cref="StructuralAnalysisService"/> считает по ней усилия и K_исп,
/// не обращаясь к габаритам элементов BIM. Схема при расчёте не меняется — её можно считать повторно.
/// </summary>
public interface IAnalyticalModel
{
    /// <summary>Параметры площадки и материалов, по которым построена схема (нагрузки входят в схему).</summary>
    StructuralLoadSettings Settings { get; }

    /// <summary>Все элементы схемы в порядке построения (стержни и пластины вперемешку, как элементы сцены).</summary>
    IReadOnlyList<AnalyticalElement> Elements { get; }

    IReadOnlyList<AnalyticalMember> Members { get; }
    IReadOnlyList<AnalyticalPanel> Panels { get; }
    IReadOnlyList<AnalyticalNode> Nodes { get; }

    /// <summary>Порядок глобальной матрицы жёсткости: 6 уравнений на узел.</summary>
    int EquationCount => Nodes.Count * AnalyticalNode.DegreesOfFreedom;

    /// <summary>Время построения схемы по фазам.</summary>
    AnalyticalBuildProfile Profile { get; }

    /// <summary>
    /// Опора под точкой плана у низа элемента: колонна/стена (верх у низа элемента), сквозь плиту — колонна под ней, балка,
    /// плита или фундамент; при зазоре в модели — ближайший элемент ниже; null — грунт или пустота.
    /// </summary>
    AnalyticalElement? SupportBelow(AnalyticalElement element, double x, double z);
}

/// <summary>Время построения расчётной схемы, мс: элементы и узлы, открытые сверху перекрытия, грузовые площади и связи (параллельно).</summary>
public sealed class AnalyticalBuildProfile
{
    public double ModelMs, ExposedMs, PlansMs;

    /// <summary>Подфазы грузовых площадей плит (сетка, опоры, площади), суммарно по плитам.</summary>
    public readonly Dictionary<string, double> Phases = [];
}

/// <inheritdoc cref="IAnalyticalModel"/>
public sealed class AnalyticalModel : IAnalyticalModel
{
    /// <summary>Допуск совпадения отметок верха опоры и низа элемента, м.</summary>
    public const double Tolerance = 0.12;

    /// <summary>Отметка, ниже которой элемент считается стоящим на грунте (нет опоры — не ошибка), м.</summary>
    public const double GroundLevel = 0.35;

    private readonly List<AnalyticalElement> _elements = [];
    private readonly List<AnalyticalMember> _members = [];
    private readonly List<AnalyticalPanel> _panels = [];
    private readonly List<AnalyticalNode> _nodes = [];
    private readonly Dictionary<(long X, long Y, long Z), AnalyticalNode> _nodeIndex = [];

    internal AnalyticalModel(StructuralLoadSettings settings) => Settings = settings;

    public StructuralLoadSettings Settings { get; }
    public IReadOnlyList<AnalyticalElement> Elements => _elements;
    public IReadOnlyList<AnalyticalMember> Members => _members;
    public IReadOnlyList<AnalyticalPanel> Panels => _panels;
    public IReadOnlyList<AnalyticalNode> Nodes => _nodes;
    public AnalyticalBuildProfile Profile { get; } = new();

    internal void Add(AnalyticalElement element)
    {
        element.Index = _elements.Count;
        _elements.Add(element);
        if (element is AnalyticalMember member) _members.Add(member);
        else if (element is AnalyticalPanel panel) _panels.Add(panel);
    }

    /// <summary>Узел в точке (общий для всех элементов, сходящихся в ней с точностью 1 мм).</summary>
    internal AnalyticalNode NodeAt(double x, double y, double z)
    {
        var key = ((long)Math.Round(x * 1000), (long)Math.Round(y * 1000), (long)Math.Round(z * 1000));
        if (_nodeIndex.TryGetValue(key, out var node)) return node;
        node = new AnalyticalNode(_nodes.Count, x, y, z);
        _nodes.Add(node);
        _nodeIndex[key] = node;
        return node;
    }

    public AnalyticalElement? SupportBelow(AnalyticalElement element, double x, double z)
    {
        EnsureCandidates(element);
        return Receiver(element, x, z);
    }

    /// <summary>Получатели точек нагрузки элемента (кандидаты в опоры снизу кэшируются на элементе — для сил, пришедших сверху).</summary>
    internal AnalyticalElement?[] ReceiversOf(AnalyticalElement m, IEnumerable<(double X, double Z)> points)
    {
        EnsureCandidates(m);
        return points.Select(p => Receiver(m, p.X, p.Z)).ToArray();
    }

    /// <summary>
    /// Кандидаты в опоры снизу: ниже низа элемента и рядом в плане; ближние — верх у низа элемента (колонна под плитой).
    /// Кэш элемента заполняется один раз и одинаково при любом порядке запросов.
    /// </summary>
    private void EnsureCandidates(AnalyticalElement m)
    {
        if (m.Candidates is not null) return;
        var candidates = _elements.Where(o => o != m && o.Base < m.Base + 0.05 && o.Overlaps(m, 0.4) && o.Role != AnalyticalRole.LoadOnly && o.Role != AnalyticalRole.Shell).ToList();
        m.Near = candidates.Where(o => o.Top >= m.Base - 1.6).ToList();
        m.Candidates = candidates;
    }

    /// <summary>Опора под точкой: среди ближних по высоте элементов, остальные — только при зазоре в модели.</summary>
    private static AnalyticalElement? Receiver(AnalyticalElement m, double x, double z) =>
        FindReceiver(m, m.Near!, x, z) ?? (m.Base > GroundLevel && m.Near!.Count < m.Candidates!.Count ? FindReceiver(m, m.Candidates, x, z) : null);

    private static double DistanceToAxis(AnalyticalElement m, double x, double z) => Geometry2D.DistanceToBoundary(m.Axis, x, z, m.AxisClosed);

    private static AnalyticalElement? FindReceiver(AnalyticalElement m, List<AnalyticalElement> candidates, double x, double z)
    {
        double b = m.Base;
        AnalyticalElement? best = null;
        double bestScore = double.MaxValue;
        foreach (var v in candidates)
        {
            if (v.Role != AnalyticalRole.Vertical || !v.Bearing || Math.Abs(v.Top - b) > Tolerance) continue;
            double d = v.DistanceTo(x, z);
            if (d <= 0.15 && d < bestScore) { best = v; bestScore = d; }
        }
        if (best is not null) return best;

        foreach (var h in candidates)
        {
            if (h.Role is not (AnalyticalRole.Slab or AnalyticalRole.Foundation or AnalyticalRole.Line) || h.Top < b - Tolerance || h.Top > b + 0.05) continue;
            bool hit = h.Role == AnalyticalRole.Line
                ? DistanceToAxis(h, x, z) <= h.HalfT + 0.1
                : Geometry2D.Contains(h.Outline, x, z) || Geometry2D.DistanceToBoundary(h.Outline, x, z) < 0.1;
            if (!hit) continue;
            if (h.Role == AnalyticalRole.Slab)
            {
                // Колонна над колонной через плиту: нагрузка идёт напрямую, плита не продавливается.
                foreach (var u in candidates)
                    if (u.Role == AnalyticalRole.Vertical && u.Bearing && Math.Abs(u.Top - h.Base) <= Tolerance && u.DistanceTo(x, z) <= 0.15)
                        return u;
            }
            double score = b - h.Top;
            if (best is null || score < bestScore) { best = h; bestScore = score; }
        }
        if (best is not null) return best;
        if (b <= GroundLevel) return null;

        // Зазор в модели: ближайший элемент ниже, содержащий точку.
        foreach (var o in candidates)
        {
            if (o.Top > b + 0.05 || !o.Bearing && o.Role != AnalyticalRole.Foundation && o.Role != AnalyticalRole.Slab) continue;
            bool hit = o.Role == AnalyticalRole.Line ? DistanceToAxis(o, x, z) <= o.HalfT + 0.1 : o.DistanceTo(x, z) <= 0.1;
            if (!hit) continue;
            double score = b - o.Top;
            if (score < bestScore) { best = o; bestScore = score; }
        }
        return best;
    }
}
