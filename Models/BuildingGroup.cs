using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIArchitectStudio.Models;

/// <summary>Отдельная постройка на участке (Главный дом, Баня с бассейном, Гараж…).</summary>
public sealed partial class BuildingGroup : ObservableObject, IBimElement
{
    public const string DefaultGroupId = "Главный дом";

    private BimUniqueId.Cache? _uniqueId;

    /// <summary>Ключ группы; на него ссылается ArchitecturalElement.BuildingGroupId.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UniqueId))]
    public partial string Id { get; set; } = DefaultGroupId;

    [JsonIgnore] public Guid UniqueId => BimUniqueId.Resolve(ref _uniqueId, Id, BimUniqueId.BuildingNamespace);

    [JsonIgnore] public BimCategory Category => BimCategory.Building;

    [ObservableProperty]
    public partial string Name { get; set; } = DefaultGroupId;

    /// <summary>Назначение: residential, bathhouse, garage, tower, pavilion…</summary>
    [ObservableProperty]
    public partial string Purpose { get; set; } = "residential";

    [ObservableProperty]
    public partial double OriginX { get; set; }

    [ObservableProperty]
    public partial double OriginZ { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial string ColorHex { get; set; } = "#3B82F6";

    public BuildingGroup() { }

    public BuildingGroup(string id, string purpose = "residential", string colorHex = "#3B82F6")
    {
        Id = id;
        Name = id;
        Purpose = purpose;
        ColorHex = colorHex;
    }

    public BuildingGroup Clone() => new()
    {
        Id = Id,
        Name = Name,
        Purpose = Purpose,
        OriginX = OriginX,
        OriginZ = OriginZ,
        IsVisible = IsVisible,
        ColorHex = ColorHex
    };
}
