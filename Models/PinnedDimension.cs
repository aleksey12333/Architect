using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models;

/// <summary>
/// Пользовательский размер, закреплённый рулеткой (клавиша M): две точки в мировых координатах сцены (м, Y-Up).
/// Хранится в SceneStateService.PinnedDimensions (Undo, .aia v5), рисуется во вьюпорте засечками 45°, выгружается в DXF
/// (слой DIMENSIONS, ALIGNED DIMENSION) и на листы ГОСТ.
/// </summary>
public sealed class PinnedDimension : IBimElement
{
    private BimUniqueId.Cache? _uniqueId;

    public string Id { get; set; } = NewId();

    [JsonIgnore] public Guid UniqueId => BimUniqueId.Resolve(ref _uniqueId, Id, BimUniqueId.DimensionNamespace);

    [JsonIgnore] public BimCategory Category => BimCategory.Dimension;

    string IBimElement.Name => Label;

    /// <summary>Начальная точка P1 [x, y, z], м.</summary>
    public double[] Start { get; set; } = [0, 0, 0];

    /// <summary>Конечная точка P2 [x, y, z], м.</summary>
    public double[] End { get; set; } = [0, 0, 0];

    /// <summary>Подпись размера (пусто — значение длины в мм).</summary>
    public string Label { get; set; } = string.Empty;

    public PinnedDimension() { }

    public PinnedDimension((double X, double Y, double Z) start, (double X, double Y, double Z) end, string label = "")
    {
        Start = [start.X, start.Y, start.Z];
        End = [end.X, end.Y, end.Z];
        Label = label;
    }

    [JsonIgnore] public (double X, double Y, double Z) A => (Start[0], Start[1], Start[2]);

    [JsonIgnore] public (double X, double Y, double Z) B => (End[0], End[1], End[2]);

    [JsonIgnore]
    public double Length
    {
        get
        {
            double dx = End[0] - Start[0], dy = End[1] - Start[1], dz = End[2] - Start[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>Точки конечны и не совпадают (размер нулевой длины не закрепляется).</summary>
    [JsonIgnore]
    public bool IsValid => Start is { Length: 3 } && End is { Length: 3 } && Start.Concat(End).All(double.IsFinite) && Length > 1e-4;

    public PinnedDimension Clone() => new() { Id = Id, Start = [.. Start], End = [.. End], Label = Label };

    public static string NewId() => "dim-" + Guid.NewGuid().ToString("N")[..10];
}
