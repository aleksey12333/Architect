using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// «Библиотека конструкций ГОСТ и СП»: слева — разделы (колонны ж/б, ригели и двутавры, плиты, фундаменты, стеновые пироги,
/// перегородки) с числом типоразмеров, справа — карточки с маркой, документом, классом материала, размерами и пирогом.
/// «Назначить выделенным» и «Вставить на этаж» — один шаг истории каждый; «Сохранить тип выделенного» пополняет библиотеку
/// проекта, «Импорт / Экспорт» — обмен корпоративными стандартами в файлах .aialib.json. Окно после действия не закрывается.
/// </summary>
public sealed partial class StandardLibraryDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private readonly Func<Task<string?>> _pickImport;
    private readonly Func<string, Task<string?>> _pickExport;
    private StandardLibrary _library;
    private StandardLibrarySection? _section;
    private string _query = string.Empty;
    private TextBlock? _status;
    private TextBox? _markBox;
    private ComboBox? _libraryBox;

    public StandardLibraryDialog(MainViewModel viewModel, Func<Task<string?>> pickImport, Func<string, Task<string?>> pickExport)
    {
        _vm = viewModel;
        _loc = viewModel.Loc;
        _pickImport = pickImport;
        _pickExport = pickExport;
        _library = StandardLibraryService.BuiltIn;
        InitializeComponent();
        CloseButtonText = _loc.Get("Close");
        TitleText.Text = _loc.Get("LibTitle");
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);
        BuildChrome();
        _section = DefaultSection();
        Refresh();
    }

    /// <summary>Раздел, соответствующий выделенному элементу (колонна → «Колонны ж/б»…), иначе все.</summary>
    private StandardLibrarySection? DefaultSection() => _vm.Scene.SelectedElement?.Category switch
    {
        BimCategory.Column => StandardLibrarySection.Columns,
        BimCategory.Beam => StandardLibrarySection.Beams,
        BimCategory.Slab or BimCategory.CantileverSlab or BimCategory.Roof or BimCategory.Terrace => StandardLibrarySection.Slabs,
        BimCategory.Foundation => StandardLibrarySection.Foundations,
        BimCategory.Wall when _vm.Scene.SelectedElement is { } w && StructuralRole.IsNonBearingPartition(w) => StandardLibrarySection.Partitions,
        BimCategory.Wall => StandardLibrarySection.Walls,
        _ => null
    };

    /// <summary>Только Debug-съёмка: раздел по имени (columns|beams|slabs|foundations|walls|partitions|all).</summary>
    public void DebugSelectSection(string name)
    {
        _section = Enum.TryParse<StandardLibrarySection>(name, true, out var s) ? s : null;
        Refresh();
    }

    private void BuildChrome()
    {
        HeaderActions.Children.Add(ActionButton("", _loc.Get("LibImport"), async (_, _) => await ImportAsync()));
        HeaderActions.Children.Add(ActionButton("", _loc.Get("LibExport"), async (_, _) => await ExportAsync()));

        _libraryBox = new ComboBox { MinWidth = 260, Height = 30, FontSize = 12, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0), Background = Res("TrackBrush") };
        _libraryBox.SelectionChanged += (_, _) =>
        {
            if (_libraryBox.SelectedItem is ComboBoxItem { Tag: StandardLibrary lib } && !ReferenceEquals(lib, _library))
            {
                _library = lib;
                Refresh();
            }
        };
        LibraryHost.Child = _libraryBox;

        var search = SearchBox(_loc.Get("LibSearch"));
        search.TextChanged += (_, _) =>
        {
            _query = search.Text.Trim();
            RefreshEntries();
        };
        SearchHost.Child = search;

        // Подвал: сохранить тип выделенного элемента в библиотеку проекта.
        var footer = new Grid { ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var save = ActionButton("", _loc.Get("LibSaveSelected"), (_, _) => SaveSelected(), accent: true);
        _markBox = SearchBox(_loc.Get("LibMarkPlaceholder"));
        _status = Text(string.Empty, 11, secondary: true, wrap: true);
        Grid.SetColumn(_markBox, 1);
        Grid.SetColumn(_status, 2);
        footer.Children.Add(save);
        footer.Children.Add(_markBox);
        footer.Children.Add(_status);
        FooterHost.Child = footer;
    }

    private void Refresh()
    {
        var selected = _vm.Scene.SelectedElements.Count > 0 ? _vm.Scene.SelectedElements.ToList() : _vm.Scene.SelectedElement is { } s ? [s] : [];
        SubtitleText.Text = selected.Count > 0
            ? _loc.Format("LibSubtitleAssign", selected.Count == 1 ? $"{selected[0].DisplayTitle} · {StandardTypeMark.Read(selected[0]) ?? _loc.Get("LibNoType")}" : _vm.SelectionHeaderText)
            : _loc.Format("LibSubtitleBrowse", Math.Max(0, _vm.Scene.ActiveLevelFilter) + 1);

        if (_libraryBox is not null)
        {
            _libraryBox.Items.Clear();
            foreach (var lib in _vm.Libraries.Libraries)
            {
                var item = new ComboBoxItem { Content = $"{lib.Name} · {_loc.Format("LibCount", lib.Entries.Count)}", Tag = lib };
                _libraryBox.Items.Add(item);
                if (ReferenceEquals(lib, _library) || lib.Name == _library.Name && !lib.IsBuiltIn) _libraryBox.SelectedItem = item;
            }
        }
        RefreshSections();
        RefreshEntries();
    }

    private static string SectionKey(StandardLibrarySection s) => "LibSection" + s;

    private static string SectionGlyph(StandardLibrarySection s) => s switch
    {
        StandardLibrarySection.Columns => ElementTypes.Glyph(ElementTypes.Column),
        StandardLibrarySection.Beams => ElementTypes.Glyph(ElementTypes.Beam),
        StandardLibrarySection.Slabs => ElementTypes.Glyph(ElementTypes.Slab),
        StandardLibrarySection.Foundations => ElementTypes.Glyph(ElementTypes.Foundation),
        _ => ElementTypes.Glyph(ElementTypes.Wall)
    };

    private void RefreshSections()
    {
        SectionList.Children.Clear();
        SectionList.Children.Add(Caption(_library.IsBuiltIn ? _loc.Get("LibBuiltIn") : _library.Name));
        AddSection(null, "", _loc.Get("LibAll"), _library.Entries.Count);
        foreach (var s in Enum.GetValues<StandardLibrarySection>())
            AddSection(s, SectionGlyph(s), _loc.Get(SectionKey(s)), _library.Entries.Count(e => e.Section == s));

        void AddSection(StandardLibrarySection? section, string glyph, string title, int count)
        {
            bool active = section == _section;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = Glyph(glyph, 12, active ? "AccentBrush" : "TextSecondaryBrush");
            var label = Text(title, 12, strong: active);
            var number = Text(count.ToString(_loc.Culture), 11, secondary: true, mono: true);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(number, 2);
            row.Children.Add(icon);
            row.Children.Add(label);
            row.Children.Add(number);
            var b = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = active ? Res("AccentSoftBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            b.Click += (_, _) =>
            {
                _section = section;
                RefreshSections();
                RefreshEntries();
            };
            SectionList.Children.Add(b);
        }
    }

    private void RefreshEntries()
    {
        EntryList.Children.Clear();
        var entries = _library.Entries.Where(e => _section is null || e.Section == _section).Where(Matches).ToList();
        if (entries.Count == 0)
        {
            EntryList.Children.Add(Card(Text(_library.IsBuiltIn ? _loc.Get("LibEmpty") : _loc.Get("LibUserEmpty"), 12, secondary: true, wrap: true), 16));
            return;
        }
        var selected = _vm.Scene.SelectedElements.Count > 0 ? _vm.Scene.SelectedElements.ToList() : _vm.Scene.SelectedElement is { } s ? [s] : [];
        foreach (var entry in entries) EntryList.Children.Add(EntryCard(entry, selected));
    }

    private bool Matches(StandardLibraryEntry e)
    {
        if (_query.Length == 0) return true;
        string hay = $"{e.Mark} {e.Name} {e.Standard} {e.MaterialGrade} {e.SizeText} {e.Notes}";
        return _query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private Border EntryCard(StandardLibraryEntry entry, IReadOnlyList<ArchitecturalElement> selected)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(GlyphBadge(ElementTypes.Glyph(entry.ElementType), 32));

        var info = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Text(entry.Mark, 15, strong: true));
        head.Children.Add(Chip(entry.ShortStandard));
        if (entry.MaterialGrade.Length > 0) head.Children.Add(Chip(entry.MaterialGrade, "#8E8E93"));
        if (!entry.IsLoadBearing) head.Children.Add(Chip(_loc.Get("LibNonBearing"), "#FF9F0A"));
        if (!entry.IsBuiltIn) head.Children.Add(Chip(_loc.Get("LibUser"), "#30D158"));
        info.Children.Add(head);
        info.Children.Add(Text(entry.Name, 12, secondary: true));
        info.Children.Add(Text(entry.SizeText, 11.5, mono: true));
        if (entry.Layers.Count > 1) info.Children.Add(LayerBar(entry));
        info.Children.Add(Text(entry.Standard + (entry.Notes.Length > 0 ? " · " + entry.Notes : string.Empty), 10.5, secondary: true, wrap: true));
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, MinWidth = 196 };
        int compatible = selected.Count(entry.IsCompatible);
        actions.Children.Add(Stretch(ActionButton("", _loc.Get("LibApplySelected") + (compatible > 1 ? $" · {compatible}" : string.Empty), (_, _) =>
        {
            var r = _vm.ApplyLibraryEntry(entry);
            SetStatus(r.Applied.Count > 0 ? _loc.Format("LibApplied", entry.Caption, r.Applied.Count, entry.SizeText, r.Skipped.Count > 0 ? _loc.Format("LibSkipped", r.Skipped.Count) : string.Empty)
                : _loc.Format("LibIncompatible", entry.Caption));
            Refresh();
        }, accent: true, enabled: compatible > 0)));
        actions.Children.Add(Stretch(ActionButton("", _loc.Get("LibInsert") + $" {Math.Max(0, _vm.Scene.ActiveLevelFilter) + 1}", (_, _) =>
        {
            var r = _vm.InsertLibraryEntry(entry);
            SetStatus(r.Message);
            Refresh();
        })));
        if (!entry.IsBuiltIn && ReferenceEquals(_library, _vm.Libraries.UserLibrary))
            actions.Children.Add(Stretch(ActionButton("", _loc.Get("LibRemove"), (_, _) =>
            {
                _vm.Libraries.RemoveFromUserLibrary(entry.Id);
                Refresh();
            })));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return Card(grid, 12);

        static Button Stretch(Button b)
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            return b;
        }
    }

    /// <summary>Полоса пирога: слои пропорционально толщине в своих цветах, подписи толщин.</summary>
    private static Grid LayerBar(StandardLibraryEntry entry)
    {
        var bar = new Grid { Height = 16, ColumnSpacing = 1, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left, Width = 420 };
        foreach (var l in entry.Layers)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(4, l.ThicknessMm), GridUnitType.Star) });
            var layer = l.ToLayer();
            var cell = new Border
            {
                Background = Hex(layer.ColorHex),
                CornerRadius = new CornerRadius(3),
                Child = new TextBlock
                {
                    Text = l.ThicknessMm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    FontSize = 9, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E))
                }
            };
            ToolTipService.SetToolTip(cell, $"{l.Name} · {l.ThicknessMm:0.#} мм · {l.DensityKgM3:0} кг/м³");
            Grid.SetColumn(cell, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(cell);
        }
        return bar;
    }

    private void SetStatus(string text)
    {
        if (_status is not null) _status.Text = text;
    }

    private void SaveSelected()
    {
        if (_vm.SaveSelectedTypeToLibrary(_markBox?.Text) is { } entry)
        {
            _library = _vm.Libraries.UserLibrary;
            _section = entry.Section;
            if (_markBox is not null) _markBox.Text = string.Empty;
            SetStatus(_loc.Format("LibSaved", entry.Mark, entry.SizeText));
            Refresh();
        }
        else SetStatus(_loc.Get("LibSaveNeedsSelection"));
    }

    private async Task ImportAsync()
    {
        if (await _pickImport() is not { } path) return;
        try
        {
            var lib = _vm.Libraries.Import(path);
            _library = lib;
            _section = null;
            SetStatus(_loc.Format("LibImported", lib.Name, lib.Entries.Count));
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            SetStatus(_loc.Get("LibImportError") + ": " + ex.Message);
        }
    }

    private async Task ExportAsync()
    {
        string name = _library.IsBuiltIn ? _loc.Get("LibFileName") : _library.Name;
        if (await _pickExport(name) is not { } path) return;
        try
        {
            var lib = _library.IsBuiltIn ? new StandardLibrary { Name = StandardLibraryService.BuiltInName, Author = _library.Author, Description = _library.Description, Entries = _library.Entries.Select(e => e.Clone()).ToList() } : _library;
            StandardLibraryService.Write(lib, path);
            SetStatus(_loc.Format("LibExported", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(ex.Message);
        }
    }
}
