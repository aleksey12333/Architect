using System.Collections.ObjectModel;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.ViewModels;

/// <summary>Узел дерева BIM: постройка → этаж → элемент → проёмы.</summary>
public sealed class BimTreeNode
{
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Glyph { get; init; } = "";
    public ArchitecturalElement? Element { get; init; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<BimTreeNode> Children { get; } = [];
}

/// <summary>Пункт фильтра этажей.</summary>
public sealed record LevelOption(int Index, string Title)
{
    public override string ToString() => Title;
}
