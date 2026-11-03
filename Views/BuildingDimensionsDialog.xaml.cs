using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// «Мастер размеров и пакетное редактирование». Вкладка 1 — габариты постройки L_X × B_Z × H_Y, таблицы шагов цифровых и буквенных осей и
/// высот этажей: новое значение строки или «Задать выбранным» и «Применить» — одна транзакция журнала (оси сдвигаются, колонны
/// переставляются, балки, стены и плиты растягиваются, отметки этажей пересчитываются). Вкладка 2 — фильтр (категория, этаж, марка или тип)
/// и правка у всех совпавших: сечение W × D, толщина, высота окна/двери, подоконник, материал — тоже одним шагом.
/// </summary>
public sealed partial class BuildingDimensionsDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private string _groupId;
    private int _tab;
    private readonly Dictionary<string, List<(CheckBox Pick, NumberBox Value, double Current)>> _rows = [];

    private static readonly (BimCategory? Category, string Key)[] Categories =
    [
        (null, "W2CatAll"), (BimCategory.Column, "W2CatColumn"), (BimCategory.Beam, "W2CatBeam"), (BimCategory.Wall, "W2CatWall"),
        (BimCategory.Slab, "W2CatSlab"), (BimCategory.Foundation, "W2CatFoundation"), (BimCategory.Roof, "W2CatRoof"),
        (BimCategory.Window, "W2CatWindow"), (BimCategory.Door, "W2CatDoor")
    ];

    private static readonly string[] Materials = ["", "concrete", "brick", "aerated_concrete", "steel", "wood", "glass"];

    private ComboBox? _category, _level, _material;
    private TextBox? _mark;
    private NumberBox? _w, _d, _t, _oh, _sill;
    private TextBlock? _found;

    public BuildingDimensionsDialog(MainViewModel viewModel, int tab = 0)
    {
        _vm = viewModel;
        _loc = viewModel.Loc;
        _groupId = viewModel.DimensionsGroupId();
        _tab = tab;
        InitializeComponent();
        CloseButtonText = _loc.Get("Close");
        TitleText.Text = _loc.Get("W2DimsTitle");
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);

        var groups = _vm.Scene.Elements.Select(e => e.BuildingGroupId).Distinct().ToList();
        if (groups.Count > 1)
        {
            var box = new ComboBox { ItemsSource = groups, SelectedItem = _groupId, MinWidth = 180, Height = 30, FontSize = 12, CornerRadius = new CornerRadius(8) };
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is string g) _groupId = g;
                Refresh();
            };
            HeaderActions.Children.Add(box);
        }
        TabsRow.Children.Add(Segments([(_loc.Get("W2DimsTab1"), (object)0), (_loc.Get("W2DimsTab2"), 1)], _tab, t =>
        {
            _tab = (int)t;
            Refresh();
        }, "DimsTabs"));
        Refresh();
    }

    /// <summary>Только Debug-съёмка и автотест: новое значение строки таблицы (ключ таблицы «x», «z», «storey»).</summary>
    public void DebugSetRow(string table, int index, double mm)
    {
        if (_rows.TryGetValue(table, out var rows) && index < rows.Count)
        {
            rows[index].Value.Value = mm;
            rows[index].Pick.IsChecked = true;
        }
    }

    private void Refresh()
    {
        BodyPanel.Children.Clear();
        _rows.Clear();
        var info = BatchDimensionService.Describe(_vm.Scene, _groupId);
        var c = _loc.Culture;
        string Mm(double m) => (m * 1000).ToString("N0", c);
        SubtitleText.Text = info is null
            ? _loc.Get("W2DimsEmpty")
            : _loc.Format("W2DimsSubtitle", _groupId, Mm(info.LengthX), Mm(info.WidthZ), Mm(info.HeightY), info.Storeys.Count);
        if (info is null) return;
        if (_tab == 0) BuildDimensions(info);
        else BuildBatch(info);
    }

    // ───────────────────────────── Вкладка 1 ─────────────────────────────

    private void BuildDimensions(BuildingDimensionsInfo info)
    {
        var c = _loc.Culture;
        var tiles = new Grid { ColumnSpacing = 10 };
        (string Caption, double Value)[] values = [(_loc.Get("W2DimsLx"), info.LengthX), (_loc.Get("W2DimsBz"), info.WidthZ), (_loc.Get("W2DimsHy"), info.HeightY)];
        for (int i = 0; i < values.Length; i++)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            var p = new StackPanel { Spacing = 2 };
            p.Children.Add(Caption(values[i].Caption));
            p.Children.Add(Text((values[i].Value * 1000).ToString("N0", c) + " мм", 18, strong: true, mono: true));
            var card = Card(p, 12);
            Grid.SetColumn(card, i);
            tiles.Children.Add(card);
        }
        BodyPanel.Children.Add(tiles);
        if (!info.ExplicitAxes) BodyPanel.Children.Add(Text(_loc.Get("W2DimsAutoAxes"), 11, secondary: true, wrap: true));

        var columns = new Grid { ColumnSpacing = 12 };
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var x = SpanCard("x", _loc.Get("W2DimsSpansX"), info.SpansX.Select(s => ($"{s.From}–{s.To}", s.Length)).ToList(), rows =>
            _vm.ReportBatch(BatchDimensionService.SetSpans(_vm.Scene, _groupId, true, rows), _loc.Get("W2DimsTitle")));
        var z = SpanCard("z", _loc.Get("W2DimsSpansZ"), info.SpansZ.Select(s => ($"{s.From}–{s.To}", s.Length)).ToList(), rows =>
            _vm.ReportBatch(BatchDimensionService.SetSpans(_vm.Scene, _groupId, false, rows), _loc.Get("W2DimsTitle")));
        var levels = SpanCard("storey", _loc.Get("W2DimsStoreys"),
            info.Storeys.Select((s, i) => ($"{i + 1} · {(s.Elevation >= 0 ? "+" : "")}{s.Elevation.ToString("0.000", c)}", s.Height)).ToList(), rows =>
                _vm.ReportBatch(BatchDimensionService.SetStoreyHeights(_vm.Scene, _groupId, rows), _loc.Get("W2DimsTitle")));
        Grid.SetColumn(z, 1);
        Grid.SetColumn(levels, 2);
        columns.Children.Add(x);
        columns.Children.Add(z);
        columns.Children.Add(levels);
        BodyPanel.Children.Add(columns);
        BodyPanel.Children.Add(Text(_loc.Get("W2DimsHint"), 11, secondary: true, wrap: true));
    }

    private static NumberBox Field(double value, double min, double max) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        SmallChange = 100,
        LargeChange = 600,
        Height = 28,
        FontSize = 12,
        MinWidth = 0,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>Таблица «подпись · текущее · новое · выбор» с полем «Задать выбранным» и кнопкой «Применить».</summary>
    private Border SpanCard(string key, string title, List<(string Label, double Current)> items, Action<Dictionary<int, double>> apply)
    {
        var c = _loc.Culture;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Text(title, 13, strong: true));
        if (items.Count == 0)
        {
            panel.Children.Add(Text(_loc.Get("W2DimsNoAxes"), 11, secondary: true, wrap: true));
            return Card(panel, 12);
        }
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        var rows = new List<(CheckBox, NumberBox, double)>();
        for (int i = 0; i < items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var pick = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            var label = Text(items[i].Label, 11.5, mono: true);
            var current = Text((items[i].Current * 1000).ToString("N0", c), 11.5, secondary: true, mono: true);
            current.HorizontalAlignment = HorizontalAlignment.Right;
            var value = Field(Math.Round(items[i].Current * 1000), key == "storey" ? 1800 : 300, key == "storey" ? 30000 : 60000);
            var row = i;
            value.ValueChanged += (_, _) => { if (Math.Abs(value.Value - Math.Round(items[row].Current * 1000)) > 0.5) pick.IsChecked = true; };
            foreach (var (el, col) in new (FrameworkElement, int)[] { (pick, 0), (label, 1), (current, 2), (value, 3) })
            {
                Grid.SetRow(el, i);
                Grid.SetColumn(el, col);
                grid.Children.Add(el);
            }
            rows.Add((pick, value, items[i].Current));
        }
        _rows[key] = rows;
        panel.Children.Add(grid);

        var bulk = new Grid { ColumnSpacing = 6 };
        bulk.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bulk.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        var all = Field(Math.Round(items[0].Current * 1000), key == "storey" ? 1800 : 300, key == "storey" ? 30000 : 60000);
        var set = ActionButton("\uE70F", _loc.Get("W2DimsSetPicked"), (_, _) =>
        {
            bool any = rows.Any(r => r.Item1.IsChecked == true);
            foreach (var r in rows)
                if (!any || r.Item1.IsChecked == true)
                {
                    r.Item2.Value = all.Value;
                    r.Item1.IsChecked = true;
                }
        });
        set.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(all, 1);
        bulk.Children.Add(set);
        bulk.Children.Add(all);
        panel.Children.Add(bulk);
        var applyButton = ActionButton("\uE73E", _loc.Get("W2DimsApply"), (_, _) =>
        {
            var changes = new Dictionary<int, double>();
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Item1.IsChecked == true && double.IsFinite(rows[i].Item2.Value)) changes[i] = rows[i].Item2.Value / 1000;
            if (changes.Count == 0) return;
            apply(changes);
            Refresh();
        }, accent: true);
        applyButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(applyButton);
        return Card(panel, 12);
    }

    // ───────────────────────────── Вкладка 2 ─────────────────────────────

    private void BuildBatch(BuildingDimensionsInfo info)
    {
        var filterGrid = new Grid { ColumnSpacing = 10, RowSpacing = 4 };
        for (int i = 0; i < 4; i++) filterGrid.ColumnDefinitions.Add(new ColumnDefinition());
        filterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        filterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _category = new ComboBox { ItemsSource = Categories.Select(x => _loc.Get(x.Key)).ToList(), SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 30, FontSize = 12 };
        var levels = new List<string> { _loc.Get("W2CatAllLevels") };
        levels.AddRange(info.Storeys.Select((_, i) => _loc.Format("W2Storey", i + 1)));
        _level = new ComboBox { ItemsSource = levels, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 30, FontSize = 12 };
        _mark = SearchBox(_loc.Get("W2MarkPlaceholder"));
        _found = Text(string.Empty, 12, strong: true);
        (string, FrameworkElement)[] filters = [(_loc.Get("W2FilterCategory"), _category), (_loc.Get("W2FilterLevel"), _level), (_loc.Get("W2FilterMark"), _mark), (_loc.Get("W2FilterFound"), _found)];
        for (int i = 0; i < filters.Length; i++)
        {
            var cap = Caption(filters[i].Item1);
            Grid.SetColumn(cap, i);
            Grid.SetColumn(filters[i].Item2, i);
            Grid.SetRow(filters[i].Item2, 1);
            filterGrid.Children.Add(cap);
            filterGrid.Children.Add(filters[i].Item2);
        }
        _category.SelectionChanged += (_, _) => UpdateFound();
        _level.SelectionChanged += (_, _) => UpdateFound();
        _mark.TextChanged += (_, _) => UpdateFound();
        BodyPanel.Children.Add(Card(filterGrid, 12));

        var edit = new Grid { ColumnSpacing = 10, RowSpacing = 4 };
        for (int i = 0; i < 6; i++) edit.ColumnDefinitions.Add(new ColumnDefinition());
        edit.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        edit.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        NumberBox Optional() => new() { Value = double.NaN, Minimum = 0, Maximum = 60000, Height = 28, FontSize = 12, PlaceholderText = "—", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        _w = Optional();
        _d = Optional();
        _t = Optional();
        _oh = Optional();
        _sill = Optional();
        _material = new ComboBox { ItemsSource = Materials.Select(m => m.Length == 0 ? _loc.Get("W2NoChange") : m).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 30, FontSize = 12 };
        (string, FrameworkElement)[] fields = [(_loc.Get("W2EditW"), _w), (_loc.Get("W2EditD"), _d), (_loc.Get("W2EditT"), _t), (_loc.Get("W2EditOpening"), _oh), (_loc.Get("W2EditSill"), _sill), (_loc.Get("W2EditMaterial"), _material)];
        for (int i = 0; i < fields.Length; i++)
        {
            var cap = Caption(fields[i].Item1);
            Grid.SetColumn(cap, i);
            Grid.SetColumn(fields[i].Item2, i);
            Grid.SetRow(fields[i].Item2, 1);
            edit.Children.Add(cap);
            edit.Children.Add(fields[i].Item2);
        }
        var editPanel = new StackPanel { Spacing = 10 };
        editPanel.Children.Add(Text(_loc.Get("W2EditTitle"), 13, strong: true));
        editPanel.Children.Add(edit);
        editPanel.Children.Add(Text(_loc.Get("W2EditHint"), 11, secondary: true, wrap: true));
        var apply = ActionButton("\uE73E", _loc.Get("W2EditApply"), (_, _) => ApplyBatch(), accent: true);
        editPanel.Children.Add(apply);
        BodyPanel.Children.Add(Card(editPanel, 12));
        UpdateFound();
    }

    private BatchFilter CurrentFilter() => new(
        _category is null ? null : Categories[Math.Max(0, _category.SelectedIndex)].Category,
        _level is { SelectedIndex: > 0 } l ? l.SelectedIndex - 1 : null,
        _mark?.Text,
        _groupId);

    private void UpdateFound()
    {
        if (_found is null) return;
        int n = BatchDimensionService.Filter(_vm.Scene.Elements.ToList(), CurrentFilter()).Count;
        _found.Text = _loc.Format("W2FoundCount", n);
    }

    private void ApplyBatch()
    {
        static double? V(NumberBox? b) => b is not null && double.IsFinite(b.Value) ? b.Value : null;
        string? material = _material is { SelectedIndex: > 0 } m ? Materials[m.SelectedIndex] : null;
        var result = BatchDimensionService.Apply(_vm.Scene, CurrentFilter(), new BatchEdit(V(_w), V(_d), V(_t), V(_oh), V(_sill), material));
        _vm.ReportBatch(result, _loc.Get("W2DimsTitle"));
        UpdateFound();
    }
}
