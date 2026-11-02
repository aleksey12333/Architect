namespace AIArchitectStudio.Models.Analytical;

/// <summary>Степени свободы узла пространственной расчётной схемы: три перемещения и три поворота (оси сцены, Y — вверх).</summary>
[Flags]
public enum NodeDof
{
    None = 0,
    Ux = 1,
    Uy = 2,
    Uz = 4,
    Rx = 8,
    Ry = 16,
    Rz = 32,
    Translations = Ux | Uy | Uz,
    Rotations = Rx | Ry | Rz,
    All = Translations | Rotations
}

/// <summary>
/// Узел расчётной схемы: точка сопряжения стержней и пластин (координаты сцены, м). Узлы с совпадающими до 1 мм координатами
/// общие для всех элементов — верх колонны и торец балки над ней сходятся в одном узле. У узла 6 степеней свободы; закреплённые
/// перечислены в <see cref="Restraints"/> (жёсткая опора — все шесть).
/// </summary>
public sealed class AnalyticalNode
{
    /// <summary>Число степеней свободы узла.</summary>
    public const int DegreesOfFreedom = 6;

    public AnalyticalNode(int id, double x, double y, double z)
    {
        Id = id;
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Порядковый номер узла в схеме (0, 1, 2…).</summary>
    public int Id { get; }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    /// <summary>Закреплённые степени свободы.</summary>
    public NodeDof Restraints { get; internal set; }

    /// <summary>Жёсткая опора: закреплены все 6 степеней свободы (основание на грунте, подошва фундамента).</summary>
    public bool IsFixedSupport => Restraints == NodeDof.All;

    /// <summary>Номер первого уравнения узла в глобальной матрице жёсткости (6 уравнений на узел).</summary>
    public int FirstEquation => Id * DegreesOfFreedom;

    public override string ToString() => $"N{Id} ({X:0.###}; {Y:0.###}; {Z:0.###}){(IsFixedSupport ? " опора" : string.Empty)}";
}
