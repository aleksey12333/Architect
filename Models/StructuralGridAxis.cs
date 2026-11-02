using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIArchitectStudio.Models;

/// <summary>
/// Координационная ось постройки, заданная явно: считанная с чертежа (BlueprintCompilerService) или введённая пользователем
/// в инспекторе осей. Без явных осей сетка вычисляется автоматически по несущим конструкциям.
/// </summary>
public sealed partial class StructuralGridAxis : ObservableObject
{
    /// <summary>Цифровая ось: линия x = Coordinate (вертикальная на плане).</summary>
    public const string AlongZ = "x";

    /// <summary>Буквенная ось: линия z = Coordinate (горизонтальная на плане).</summary>
    public const string AlongX = "z";

    [ObservableProperty]
    public partial string Label { get; set; } = "1";

    /// <summary>"x" — цифровая ось (x = const), "z" — буквенная ось (z = const).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNumeric), nameof(DirectionTitle))]
    public partial string Direction { get; set; } = AlongZ;

    /// <summary>Координата оси в метрах (X для цифровых, Z для буквенных).</summary>
    [ObservableProperty]
    public partial double Coordinate { get; set; }

    [ObservableProperty]
    public partial string BuildingGroupId { get; set; } = BuildingGroup.DefaultGroupId;

    [JsonIgnore] public bool IsNumeric => Direction != AlongX;

    /// <summary>Подпись направления для списка осей: «X =» или «Z =».</summary>
    [JsonIgnore] public string DirectionTitle => IsNumeric ? "X" : "Z";

    public StructuralGridAxis() { }

    public StructuralGridAxis(string label, string direction, double coordinate, string groupId)
    {
        Label = label;
        Direction = direction == AlongX ? AlongX : AlongZ;
        Coordinate = coordinate;
        BuildingGroupId = groupId;
    }

    public StructuralGridAxis Clone() => new(Label, Direction, Coordinate, BuildingGroupId);
}
