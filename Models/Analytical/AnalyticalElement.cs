using AIArchitectStudio.Services;

namespace AIArchitectStudio.Models.Analytical;

/// <summary>Работа элемента в обходе нагрузок сверху вниз (порядок имён — порядок фаз профиля расчёта).</summary>
public enum AnalyticalRole
{
    /// <summary>Ненесущий элемент: собственный вес и силы сверху передаются вниз без проверки (витраж, лестница, бассейн).</summary>
    LoadOnly,

    /// <summary>Перекрытие, покрытие, терраса: грузовые площади на опоры, изгиб, прогиб, продавливание.</summary>
    Slab,

    /// <summary>Купол / оболочка: нагрузка по контуру опирания.</summary>
    Shell,

    /// <summary>Балка, ферма, кольцевая балка: правило рычага по пролётам между опорами.</summary>
    Line,

    /// <summary>Колонна, несущая стена, ядро, диагрид: продольная сила N вниз, горизонтальные силы по EI.</summary>
    Vertical,

    /// <summary>Фундаментная плита: давление на грунт, отпор, продавливание.</summary>
    Foundation
}

/// <summary>Форма элемента в плане для поиска опор: контур или круг колонны, ось стены/балки, коробка ядра, кольцо диагрида.</summary>
public enum AnalyticalShape
{
    Polygon,
    Circle,
    Segment,
    Polyline,
    Box,
    Ring
}

/// <summary>Распределённая (по грузовой площади) нагрузка в точке плана: расчётное D и нормативное N значения, кН.</summary>
public readonly record struct TributaryLoad(double X, double Z, double D, double N);

/// <summary>
/// Элемент расчётной схемы — стержень (<see cref="AnalyticalMember"/>) или пластина (<see cref="AnalyticalPanel"/>). Хранит всё, что
/// расчёту нужно знать о геометрии: отметки низа и верха, форму в плане (контур, ось, круг, коробка, кольцо) с габаритом для
/// быстрой отсечки, материал, собственный вес, а также найденные построителем связи — опоры снизу, точки передачи нагрузки и их
/// получателей. Ссылки <see cref="Source"/> / <see cref="Instance"/> ведут к физическому элементу BIM (результаты расчёта
/// пишутся по его Id); солвер к геометрии экземпляра не обращается. Поля, а не свойства — горячие циклы расчёта.
/// </summary>
public abstract class AnalyticalElement
{
    /// <summary>Запас нижней оценки на погрешность округления координат (0.1 мм).</summary>
    public const double BoundMargin = 1e-4;

    /// <summary>Порядковый номер в схеме (порядок построения = порядок обхода с равными отметками).</summary>
    public int Index;

    /// <summary>Исходный элемент BIM (у поэтажного экземпляра параметрического элемента — сам параметрический элемент).</summary>
    public required ArchitecturalElement Source;

    /// <summary>Поэтажный экземпляр, по которому построен элемент схемы (у непараметрического — сам <see cref="Source"/>).</summary>
    public required ArchitecturalElement Instance;

    public required AnalyticalMaterial Material;
    public BimCategory Category;

    /// <summary>Постройка исходного элемента (горизонтальные силы собираются по постройке).</summary>
    public string? GroupId;

    /// <summary>Номер поэтажного экземпляра параметрического элемента (0 — первый).</summary>
    public int Floor;

    public AnalyticalRole Role;
    public AnalyticalShape Shape;
    public double Base, Top;

    /// <summary>Поворот элемента в плане вокруг Y, градусы.</summary>
    public double RotationY;

    /// <summary>Контур задан Polygon2D (не прямоугольник W × D).</summary>
    public bool HasPolygon;

    public List<(double X, double Z)> Outline = [];

    /// <summary>Контур и отверстия массивами — быстрые проверки «точка в полигоне» в горячих циклах.</summary>
    public (double X, double Z)[] Poly = [];

    public readonly List<(double X, double Z)[]> HoleArr = [];
    public List<List<(double X, double Z)>> Holes = [];
    public double MinX, MaxX, MinZ, MaxZ;

    /// <summary>Ось стены/балки в плане: от −W/2 до +W/2 вдоль локальной X.</summary>
    public (double X, double Z) AxisStart, AxisEnd;

    public double HalfT;

    /// <summary>Толщина стен коробки ядра, м.</summary>
    public double WallT;

    public (double X, double Z) Center;
    public double Radius;
    public List<(double X, double Z)> Axis = [];
    public bool AxisClosed;
    public bool Bearing;
    public bool Checked = true;

    /// <summary>Собственный вес элемента: расчётный и нормативный, кН.</summary>
    public double SelfD, SelfN;

    /// <summary>Толщина несущего слоя, м (0 — не задана).</summary>
    public double ThickStruct;

    /// <summary>Перекрытие открыто сверху (кровля, эксплуатируемая кровля стилобата) — снеговая нагрузка вместо полезной.</summary>
    public bool Exposed;

    /// <summary>Площадь контура за вычетом отверстий, м² (не меньше 0.01).</summary>
    public double NetArea;

    /// <summary>Момент инерции для распределения горизонтальных сил между стойками, м⁴ (стена — в своей плоскости).</summary>
    public double LateralInertia;

    /// <summary>Коэффициент расчётной длины μ по закреплению концов стойки (<see cref="StructuralDesign"/>); 0 — по материалу (ж/б 0,7, прочие 1,0).</summary>
    public double LengthFactor;

    /// <summary>Поперечная арматура продавливания плиты: q_sw, кН/м контура (0 — нет, F_sw,ult не учитывается).</summary>
    public double PunchingQsw;

    /// <summary>Расстояние от грани до оси рабочих стержней a, м, по явному армированию режима «Конструкции» (0 — норма расчёта).</summary>
    public double RebarCover;

    /// <summary>Процент армирования μ = As / (b·h0) (колонна — As / A) по явным стержням (0 — принятый расчётом).</summary>
    public double RebarMu;

    /// <summary>Погонное усилие хомутов балки q_sw = R_sw·A_sw / s_w, кН/м, по явному шагу (0 — μsw = 0,25 %).</summary>
    public double StirrupQsw;

    /// <summary>Квадратурные точки фундамента / ненесущей площадки (вес — площадь).</summary>
    public List<(double X, double Z, double A)>? Samples;

    /// <summary>
    /// Передача нагрузки вниз: статические точки (ось стены, центр колонны, контур ядра, кольцо диагрида и купола, ось витража,
    /// ось балки без опор) с весами и найденные построителем получатели этих точек.
    /// </summary>
    public List<(double X, double Z, double W)>? TransferPoints;

    public AnalyticalElement?[]? Receivers;

    /// <summary>Кандидаты в опоры снизу (кэш схемы для сил, пришедших сверху): все и ближние по высоте.</summary>
    internal List<AnalyticalElement>? Candidates, Near;

    /// <summary>Габарит фигуры, до которой меряет <see cref="DistanceTo"/> (с полутолщиной оси) — для нижней оценки расстояния.</summary>
    public double BoundMinX, BoundMaxX, BoundMinZ, BoundMaxZ;

    /// <summary>Узлы элемента: торцы стержня или вершины контура пластины.</summary>
    public abstract IReadOnlyList<AnalyticalNode> Nodes { get; }

    public bool IsPointLike => Shape is AnalyticalShape.Polygon or AnalyticalShape.Circle;
    public bool IsColumnLike => Category is BimCategory.Column or BimCategory.Core;
    public double Height => Math.Max(0.01, Top - Base);

    /// <summary>Расстояние от точки плана до опоры (0 — внутри сечения).</summary>
    public double DistanceTo(double x, double z) => Shape switch
    {
        AnalyticalShape.Circle => Math.Max(0, Math.Sqrt((x - Center.X) * (x - Center.X) + (z - Center.Z) * (z - Center.Z)) - Radius),
        AnalyticalShape.Segment => Math.Max(0, Geometry2D.DistanceToSegment(x, z, AxisStart.X, AxisStart.Z, AxisEnd.X, AxisEnd.Z) - HalfT),
        AnalyticalShape.Polyline => Math.Max(0, Geometry2D.DistanceToBoundary(Axis, x, z, AxisClosed) - HalfT),
        // Ядро — коробка из стен толщиной WallT: внутри шахты расстояние до внутренней грани стен.
        AnalyticalShape.Box => AnalyticalGeometry.Pip(Poly, x, z)
            ? Math.Max(0, AnalyticalGeometry.DistBoundary(Poly, x, z) - WallT)
            : AnalyticalGeometry.DistBoundary(Poly, x, z),
        AnalyticalShape.Ring => Math.Abs(Math.Sqrt((x - Center.X) * (x - Center.X) + (z - Center.Z) * (z - Center.Z)) - Radius),
        _ => AnalyticalGeometry.Pip(Poly, x, z) ? 0 : AnalyticalGeometry.DistBoundary(Poly, x, z)
    };

    /// <summary>
    /// Нижняя оценка <see cref="DistanceTo"/> — расстояние до габарита фигуры опоры (0 внутри габарита). Опоры, чья оценка
    /// заведомо не меньше порога, в горячих циклах не вычисляются точно: результат совпадает с полным перебором до бита.
    /// </summary>
    public double BoundDistance(double x, double z)
    {
        double dx = x < BoundMinX ? BoundMinX - x : x > BoundMaxX ? x - BoundMaxX : 0;
        double dz = z < BoundMinZ ? BoundMinZ - z : z > BoundMaxZ ? z - BoundMaxZ : 0;
        return Math.Sqrt(dx * dx + dz * dz) - BoundMargin;
    }

    /// <summary>Габарит фигуры расстояния: ось ± полутолщина, круг и кольцо — центр ± радиус, контур — по вершинам.</summary>
    public void SetDistanceBounds()
    {
        (double MinX, double MaxX, double MinZ, double MaxZ) b = Shape switch
        {
            AnalyticalShape.Circle or AnalyticalShape.Ring => (Center.X - Radius, Center.X + Radius, Center.Z - Radius, Center.Z + Radius),
            AnalyticalShape.Segment => (Math.Min(AxisStart.X, AxisEnd.X), Math.Max(AxisStart.X, AxisEnd.X), Math.Min(AxisStart.Z, AxisEnd.Z), Math.Max(AxisStart.Z, AxisEnd.Z)),
            AnalyticalShape.Polyline => Geometry2D.Bounds(Axis),
            _ => Geometry2D.Bounds(Poly)
        };
        double pad = Shape is AnalyticalShape.Segment or AnalyticalShape.Polyline ? Math.Max(0, HalfT) : 0;
        // Вырожденная фигура (пустой контур) — без оценки: такая опора всегда вычисляется точно.
        (BoundMinX, BoundMaxX, BoundMinZ, BoundMaxZ) = b.MinX <= b.MaxX && b.MinZ <= b.MaxZ
            ? (b.MinX - pad, b.MaxX + pad, b.MinZ - pad, b.MaxZ + pad)
            : (double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity);
    }

    /// <summary>Ближайшая точка опоры (направление на опору — для определения плиты, опёртой по контуру).</summary>
    public (double X, double Z) NearestPoint(double x, double z)
    {
        static (double X, double Z) OnSegment((double X, double Z) a, (double X, double Z) b, double x, double z)
        {
            double vx = b.X - a.X, vz = b.Z - a.Z, len2 = vx * vx + vz * vz;
            double t = len2 < 1e-18 ? 0 : Math.Clamp(((x - a.X) * vx + (z - a.Z) * vz) / len2, 0, 1);
            return (a.X + vx * t, a.Z + vz * t);
        }
        (double X, double Z) OnLoop(IReadOnlyList<(double X, double Z)> loop, bool closed)
        {
            (double X, double Z) best = loop[0];
            double bestD = double.MaxValue;
            int count = closed ? loop.Count : loop.Count - 1;
            for (int i = 0; i < count; i++)
            {
                var q = OnSegment(loop[i], loop[(i + 1) % loop.Count], x, z);
                double d = (q.X - x) * (q.X - x) + (q.Z - z) * (q.Z - z);
                if (d < bestD) { bestD = d; best = q; }
            }
            return best;
        }
        switch (Shape)
        {
            case AnalyticalShape.Circle:
            case AnalyticalShape.Ring:
            {
                double dx = x - Center.X, dz = z - Center.Z, len = Math.Sqrt(dx * dx + dz * dz);
                return len < 1e-9 ? Center : (Center.X + dx / len * Radius, Center.Z + dz / len * Radius);
            }
            case AnalyticalShape.Segment:
                return OnSegment(AxisStart, AxisEnd, x, z);
            case AnalyticalShape.Polyline:
                return OnLoop(Axis, AxisClosed);
            default:
                return Shape == AnalyticalShape.Polygon && AnalyticalGeometry.Pip(Poly, x, z) ? (x, z) : OnLoop(Poly, true);
        }
    }

    /// <summary>Опорные точки для выпуклой оболочки опор плиты.</summary>
    public IEnumerable<(double X, double Z)> Anchors() => Shape switch
    {
        AnalyticalShape.Segment => [AxisStart, AxisEnd],
        AnalyticalShape.Polyline => Axis,
        AnalyticalShape.Ring => Enumerable.Range(0, 16).Select(i => 2 * Math.PI * i / 16).Select(a => (Center.X + Radius * Math.Cos(a), Center.Z + Radius * Math.Sin(a))),
        _ => Outline
    };

    public bool Overlaps(AnalyticalElement o, double pad) =>
        MinX - pad < o.MaxX && MaxX + pad > o.MinX && MinZ - pad < o.MaxZ && MaxZ + pad > o.MinZ;

    public override string ToString() => $"{GetType().Name} #{Index} {Role} «{Source.Id}»{(Floor > 0 ? $" эт. {Floor}" : string.Empty)}";
}
