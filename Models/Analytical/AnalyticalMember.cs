namespace AIArchitectStudio.Models.Analytical;

/// <summary>Точка дискретизации оси стержня: положение по длине оси S, точка плана и длина участка (вес собственного веса), м.</summary>
public readonly record struct MemberStation(double S, double X, double Z, double Length);

/// <summary>Пролёт стержня между соседними опорами: положения по оси и опоры слева и справа.</summary>
public readonly record struct MemberSpan(double S0, double S1, AnalyticalElement Left, AnalyticalElement Right);

/// <summary>
/// Стержень расчётной схемы: колонна, ядро жёсткости (консольная коробчатая стойка), этаж диагрида (кольцо из
/// <see cref="BarCount"/> наклонных труб), балка, ферма, кольцевая балка. Узлы — торцы оси (у кольцевой балки — путь по
/// контуру, <see cref="Path"/>); жёсткостные характеристики — <see cref="A"/>, <see cref="Iy"/>, <see cref="Iz"/>, <see cref="E"/>,
/// <see cref="G"/>, <see cref="J"/>. У балок построитель уже нашёл опоры, их положение на оси и пролёты между ними.
/// </summary>
public sealed class AnalyticalMember : AnalyticalElement
{
    public AnalyticalNode StartNode = null!;
    public AnalyticalNode EndNode = null!;

    /// <summary>Узлы оси по порядку (у прямого стержня — два торца, у кольцевой балки — вершины контура).</summary>
    public IReadOnlyList<AnalyticalNode> Path = [];

    /// <summary>Сечение для проверок: колонна W × D, балка D × H, диагрид — одна труба, ядро — коробка; у фермы и кольцевой балки — пустое.</summary>
    public AnalyticalSection Section;

    /// <summary>Размеры сечения как в модели (колонна — W и D, балка — D и H, ядро — W и D, диагрид — W и D), м.</summary>
    public double SectionB, SectionH;

    /// <summary>Момент инерции изгиба балки в вертикальной плоскости, м⁴ (ферма — по поясам, кольцевая балка — 0.6 × H).</summary>
    public double BendingInertia;

    /// <summary>Момент инерции при кручении, м⁴.</summary>
    public double J;

    /// <summary>Число одинаковых стержней в элементе схемы (этаж диагрида — 16, прочие — 1).</summary>
    public int BarCount = 1;

    /// <summary>Длина оси в плане, м.</summary>
    public double AxisLength;

    /// <summary>Опоры балки (вертикальные элементы и ригели) в порядке схемы.</summary>
    public List<AnalyticalElement> Supports = [];

    /// <summary>Точки дискретизации оси (16…240 участков).</summary>
    public List<MemberStation> Stations = [];

    /// <summary>Опоры по возрастанию положения на оси.</summary>
    public List<(AnalyticalElement Support, double S)> SupportStations = [];

    public List<MemberSpan> Spans = [];

    public override IReadOnlyList<AnalyticalNode> Nodes => Path.Count > 0 ? Path : [StartNode, EndNode];

    /// <summary>Площадь сечения, м².</summary>
    public double A => Section.A;

    /// <summary>Момент инерции относительно сильной оси, м⁴.</summary>
    public double Iy => Section.IStrong;

    /// <summary>Момент инерции относительно слабой оси, м⁴.</summary>
    public double Iz => Section.IWeak;

    /// <summary>Модуль упругости, кПа.</summary>
    public double E => Material.E;

    /// <summary>Модуль сдвига, кПа.</summary>
    public double G => Material.G;

    /// <summary>Площадь пояса фермы, м²: высота пояса 0.15·H (не более 0.25 м) × ширина D.</summary>
    public double ChordArea => Math.Min(0.25, SectionH * 0.15) * Math.Max(0.05, SectionB);

    /// <summary>Наклонный стержень этажа диагрида: длина и косинус угла к вертикали.</summary>
    public (double Length, double Cos) InclinedBar()
    {
        double chord = 2 * Radius * Math.Sin(Math.PI / BarCount);
        double h = Height;
        double len = Math.Sqrt(chord * chord + h * h);
        return (len, h / Math.Max(1e-6, len));
    }

    /// <summary>Положение проекции точки на оси стержня (длина дуги от начала).</summary>
    public double ArcPosition(double x, double z)
    {
        double best = double.MaxValue, pos = 0, acc = 0;
        int count = AxisClosed ? Axis.Count : Axis.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var a = Axis[i];
            var b = Axis[(i + 1) % Axis.Count];
            double vx = b.X - a.X, vz = b.Z - a.Z, len = Math.Sqrt(vx * vx + vz * vz);
            if (len < 1e-9) continue;
            double t = Math.Clamp(((x - a.X) * vx + (z - a.Z) * vz) / (len * len), 0, 1);
            double cx = a.X + vx * t - x, cz = a.Z + vz * t - z, d = cx * cx + cz * cz;
            if (d < best)
            {
                best = d;
                pos = acc + t * len;
            }
            acc += len;
        }
        return pos;
    }
}
