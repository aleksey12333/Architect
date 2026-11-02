using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Verification;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// «История изменений и контрольные точки»: слева — журнал транзакций (сверху отменённые шаги Redo, затем текущее состояние,
/// ниже — совершённые шаги и «Исходное состояние»), щелчок по шагу — прыжок пакетом Undo/Redo с одним обновлением вьюпорта;
/// справа — контрольные точки вариантов проекта: создание именованного слепка, переход одним шагом истории, сравнение вариантов
/// с текущей сценой по стоимости каркаса (₽) и K_исп max.
/// </summary>
public sealed partial class HistoryTimelineDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private TextBox? _nameBox;

    public HistoryTimelineDialog(MainViewModel viewModel)
    {
        _vm = viewModel;
        _loc = viewModel.Loc;
        InitializeComponent();
        CloseButtonText = _loc.Get("Close");
        TitleText.Text = _loc.Get("HistoryTitle");
        TimelineCaption.Text = _loc.Get("HistoryTimeline").ToUpperInvariant();
        HeaderActions.Children.Add(ActionButton("", _loc.Get("HistoryBack"), (_, _) => Jump(_vm.Scene.Transactions.UndoCount - 1)));
        HeaderActions.Children.Add(ActionButton("", _loc.Get("HistoryForward"), (_, _) => Jump(_vm.Scene.Transactions.UndoCount + 1)));
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);
        Refresh();
    }

    private System.Globalization.CultureInfo Culture => _loc.Culture;

    /// <summary>Только Debug-съёмка: имя поля новой контрольной точки.</summary>
    public void DebugSetCheckpointName(string name)
    {
        if (_nameBox is not null) _nameBox.Text = name;
    }

    private void Jump(int index)
    {
        _vm.JumpToHistory(index);
        Refresh();
    }

    private void Refresh()
    {
        var entries = _vm.HistoryEntries();
        var journal = _vm.Scene.Transactions;
        SubtitleText.Text = _loc.Format("HistorySubtitle", journal.UndoCount + journal.RedoCount, journal.RedoCount, journal.MaxHistory);
        RefreshTimeline(entries);
        RefreshCheckpoints();
    }

    // ───────────────────────────── Журнал транзакций ─────────────────────────────

    private void RefreshTimeline(List<HistoryEntry> entries)
    {
        TimelineList.Children.Clear();
        if (entries.Count == 1) TimelineList.Children.Add(Card(Text(_loc.Get("HistoryEmpty"), 12, secondary: true, wrap: true), 14));
        // Сверху — самые новые (отменённые шаги Redo), снизу — исходное состояние.
        for (int i = entries.Count - 1; i >= 0; i--)
            TimelineList.Children.Add(Row(entries[i], first: i == entries.Count - 1, last: i == 0));
    }

    private Button Row(HistoryEntry entry, bool first, bool last)
    {
        var grid = new Grid { ColumnSpacing = 10, MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Рейка шкалы: линия через всю строку (обрезается у крайних шагов) и точка шага.
        var rail = new Grid();
        rail.Children.Add(new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, first ? 24 : 0, 0, last ? 24 : 0),
            Background = Res(entry.IsUndone ? "StudioBorderBrush" : "AccentSoftBrush")
        });
        rail.Children.Add(new Ellipse
        {
            Width = entry.IsCurrent ? 14 : 10,
            Height = entry.IsCurrent ? 14 : 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = entry.IsUndone ? Res("IslandBrush") : Res("AccentBrush"),
            Stroke = entry.IsUndone ? Res("TextSecondaryBrush") : Res("AccentBrush"),
            StrokeThickness = entry.IsCurrent ? 3 : 1.5
        });
        grid.Children.Add(rail);

        var body = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 6, 0, 6) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var number = Text(entry.IsInitial ? "0" : entry.Index.ToString(Culture), 11, secondary: true, mono: true);
        number.MinWidth = 26;
        head.Children.Add(number);
        var name = Text(entry.Name, 12.5, strong: entry.IsCurrent);
        name.MaxWidth = 340;
        if (entry.IsUndone) name.Opacity = 0.55;
        head.Children.Add(name);
        if (entry.IsCurrent) head.Children.Add(Chip(_loc.Get("HistoryCurrent")));
        else if (entry.IsUndone) head.Children.Add(Chip(_loc.Get("HistoryUndone"), "#8E8E93"));
        body.Children.Add(head);
        if (!entry.IsInitial)
        {
            var sub = Text(_loc.Format("HistoryElements", entry.AffectedElements), 10.5, secondary: true);
            sub.Margin = new Thickness(34, 0, 0, 0);
            if (entry.IsUndone) sub.Opacity = 0.55;
            body.Children.Add(sub);
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var time = Text(entry.Time.ToString("HH:mm:ss", Culture), 10.5, secondary: true, mono: true);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        var b = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 0, 10, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Background = entry.IsCurrent ? Res("AccentSoftBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsEnabled = !entry.IsCurrent
        };
        ToolTipService.SetToolTip(b, entry.IsCurrent ? _loc.Get("HistoryCurrent") : _loc.Get("HistoryJumpTip"));
        b.Click += (_, _) => Jump(entry.Index);
        return b;
    }

    // ───────────────────────────── Контрольные точки ─────────────────────────────

    private void RefreshCheckpoints()
    {
        CheckpointPanel.Children.Clear();
        CheckpointPanel.Children.Add(Caption(_loc.Get("CheckpointsTitle")));

        // Создание точки.
        var create = new StackPanel { Spacing = 8 };
        string keep = _nameBox?.Text ?? string.Empty;
        _nameBox = SearchBox(_loc.Get("CheckpointNamePlaceholder"));
        _nameBox.Text = keep;
        create.Children.Add(_nameBox);
        var button = ActionButton("", _loc.Get("CheckpointCreate"), (_, _) =>
        {
            _vm.CreateCheckpoint(_nameBox.Text);
            _nameBox.Text = string.Empty;
            Refresh();
        }, accent: true);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        create.Children.Add(button);
        CheckpointPanel.Children.Add(Card(create, 12));

        // Текущая сцена.
        var (cost, k, concrete, elements) = _vm.CurrentVariantMetrics();
        var current = new StackPanel { Spacing = 4 };
        current.Children.Add(Text(_loc.Get("CheckpointCurrent"), 11, secondary: true));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        line.Children.Add(Text(Rub(cost), 18, strong: true, mono: true));
        line.Children.Add(KChip(k));
        current.Children.Add(line);
        current.Children.Add(Text(_loc.Format("CheckpointMetrics", _vm.HistoryEntries().FirstOrDefault(e => e.IsCurrent)?.Name ?? string.Empty,
            VerificationText.K(k), concrete.ToString("N1", Culture), elements), 10.5, secondary: true, wrap: true));
        CheckpointPanel.Children.Add(Card(current, 12, accent: true));

        if (_vm.Checkpoints.Count == 0)
        {
            CheckpointPanel.Children.Add(Card(Text(_loc.Get("CheckpointEmpty"), 11.5, secondary: true, wrap: true), 12));
            return;
        }

        // Сравнение вариантов: столбцы стоимости каркаса.
        CheckpointPanel.Children.Add(Caption(_loc.Get("CheckpointCompare")));
        double max = Math.Max(cost, _vm.Checkpoints.Max(c => c.FrameCostRub));
        double min = Math.Min(cost, _vm.Checkpoints.Min(c => c.FrameCostRub));
        var chart = new StackPanel { Spacing = 6 };
        chart.Children.Add(Bar(_loc.Get("CheckpointCurrent"), cost, k, min, max, current: true));
        foreach (var cp in _vm.Checkpoints) chart.Children.Add(Bar(cp.Name, cp.FrameCostRub, cp.MaxUtilization, min, max, current: false));
        CheckpointPanel.Children.Add(Card(chart, 12));

        foreach (var cp in _vm.Checkpoints.Reverse()) CheckpointPanel.Children.Add(CheckpointCard(cp, cost, k, concrete, elements));
    }

    /// <summary>Столбец стоимости: шкала по диапазону вариантов (35 % — самый дешёвый, 100 % — самый дорогой), чтобы разница в 1–2 % была видна.</summary>
    private Grid Bar(string name, double value, double k, double min, double max, bool current)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(name, 11, strong: current);
        grid.Children.Add(label);
        var track = new Grid { Height = 14, VerticalAlignment = VerticalAlignment.Center };
        double share = max - min > 1 ? 0.35 + 0.65 * Math.Clamp((value - min) / (max - min), 0, 1) : max > 0 ? 1 : 0.02;
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - share + 1e-6, GridUnitType.Star) });
        var color = StressPalette.ToUi(StressPalette.ColorFor(k));
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = current ? Res("AccentBrush") : new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, color.R, color.G, color.B))
        });
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);
        var amount = Text(ShortRub(value), 10.5, mono: true);
        Grid.SetColumn(amount, 2);
        grid.Children.Add(amount);
        return grid;
    }

    private Border CheckpointCard(ProjectCheckpoint cp, double cost, double k, double concrete, int elements)
    {
        var cmp = ProjectCheckpointService.Compare(cp, cost, k, concrete, elements);
        var panel = new StackPanel { Spacing = 5 };
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(cp.Name, 13, strong: true));
        var time = Text($"{cp.CreatedAt:HH:mm} · шаг {cp.HistoryDepth}", 10, secondary: true, mono: true);
        Grid.SetColumn(time, 1);
        head.Children.Add(time);
        panel.Children.Add(head);

        var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        values.Children.Add(Text(Rub(cp.FrameCostRub), 15, strong: true, mono: true));
        values.Children.Add(KChip(cp.MaxUtilization));
        panel.Children.Add(values);

        var delta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bool cheaper = cmp.CostDeltaRub < -0.5, dearer = cmp.CostDeltaRub > 0.5;
        delta.Children.Add(Chip((cmp.CostDeltaRub >= 0 ? "+" : "−") + Rub(Math.Abs(cmp.CostDeltaRub)) + " к текущей", cheaper ? "#30D158" : dearer ? "#FF9F0A" : "#8E8E93"));
        delta.Children.Add(Chip("ΔK " + (cmp.UtilizationDelta >= 0 ? "+" : "−") + Math.Abs(cmp.UtilizationDelta).ToString("0.00", Culture),
            cmp.UtilizationDelta > 0.005 ? "#FF9F0A" : cmp.UtilizationDelta < -0.005 ? "#30D158" : "#8E8E93"));
        panel.Children.Add(delta);
        panel.Children.Add(Text($"бетон {cp.ConcreteM3.ToString("N1", Culture)} м³ · арматура {(cp.RebarKg / 1000).ToString("N2", Culture)} т · элементов {cp.ElementCount}" +
                                (cp.GoverningElement.Length > 0 ? $" · max — {cp.GoverningElement}" : string.Empty), 10.5, secondary: true, wrap: true));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(ActionButton("", _loc.Get("CheckpointGo"), (_, _) =>
        {
            _vm.RestoreCheckpoint(cp);
            Refresh();
        }, accent: true));
        actions.Children.Add(ActionButton("", _loc.Get("CheckpointRemove"), (_, _) =>
        {
            _vm.RemoveCheckpoint(cp);
            Refresh();
        }));
        panel.Children.Add(actions);
        return Card(panel, 12);
    }

    private Border KChip(double k)
    {
        var c = StressPalette.ToUi(StressPalette.ColorFor(k));
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2A, c.R, c.G, c.B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "K_исп " + VerificationText.K(k), FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = Mono, Foreground = new SolidColorBrush(c) }
        };
    }

    private string Rub(double v) => v.ToString("N0", Culture) + " ₽";

    private string ShortRub(double v) => v >= 1e6 ? (v / 1e6).ToString("0.00", Culture) + " млн ₽" : Rub(v);
}
