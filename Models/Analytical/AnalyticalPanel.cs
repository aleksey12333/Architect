namespace AIArchitectStudio.Models.Analytical;

/// <summary>
/// Распределение равномерной нагрузки пластины по грузовым площадям, найденное построителем: квадратурная сетка, опоры,
/// реакции опор (у балок — ещё и линейная нагрузка вдоль оси), определяющий пролёт и вылет консоли. Солвер копирует реакции
/// и добавляет к ним сосредоточенные силы сверху — сама схема при расчёте не меняется.
/// </summary>
public sealed class PanelTributary
{
    public required List<(double X, double Z, double A)> Samples;
    public required List<AnalyticalElement> Supports;
    public double[] ReactD = [], ReactN = [];
    public List<TributaryLoad>?[] LineLoads = [];
    public double Span, Cantilever, SpanFactor = 1;

    /// <summary>Пластина без опор: получатели точек сетки снизу.</summary>
    public AnalyticalElement?[]? Receivers;

    /// <summary>Время подфаз (сетка, опоры, площади), мс — для профиля расчёта.</summary>
    public double GridMs, SupportsMs, AreasMs;
}

/// <summary>
/// Пластина расчётной схемы: перекрытие, покрытие, терраса, фундаментная плита, купол, а также вертикальные пластины —
/// несущие стены и ненесущие витражи; лестницы и бассейны — площадки нагрузки. Узлы — вершины контура (стена — четыре угла
/// срединной плоскости). Перекрытия и фундаменты несут найденные построителем нагрузку на 1 м² и её распределение по
/// грузовым площадям (<see cref="Tributary"/>).
/// </summary>
public sealed class AnalyticalPanel : AnalyticalElement
{
    public IReadOnlyList<AnalyticalNode> Contour = [];

    /// <summary>Вертикальная пластина (стена, витраж).</summary>
    public bool IsVertical;

    /// <summary>Расчётная толщина, м: несущий слой пирога, иначе толщина элемента (стена — D, плита — H).</summary>
    public double Thickness;

    /// <summary>Размеры пластины как в модели (длина стены, габарит плиты в плане, высота купола), м.</summary>
    public double Width, ModelHeight, Depth;

    /// <summary>Собственный вес 1 м²: расчётный и нормативный, кПа (перекрытия и фундаменты).</summary>
    public double AreaSelfD, AreaSelfN;

    /// <summary>Полная равномерная нагрузка на 1 м² (собственный вес + полезная или снег): расчётная и нормативная, кПа.</summary>
    public double AreaLoadD, AreaLoadN;

    public PanelTributary? Tributary;

    /// <summary>Контур опирания купола (32 точки эллипса W × D).</summary>
    public List<(double X, double Z)>? SupportRing;

    public override IReadOnlyList<AnalyticalNode> Nodes => Contour;

    /// <summary>Модуль упругости, кПа.</summary>
    public double E => Material.E;
}
