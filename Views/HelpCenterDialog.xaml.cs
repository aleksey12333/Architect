using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// «Справочный центр и методика расчёта»: четыре вкладки — «Быстрый старт» (схема рабочего процесса в 5 шагов и запуск
/// интерактивного тура), «Инженерная методика и СП» (нормативы ядра, K_исп, продавливание, прогиб, армирование, γn),
/// «Ручное редактирование и клавиши» (таблица горячих клавиш и жестов мыши), «Импорт прайсов и планов» (формат CSV поставщика,
/// требования к поэтажным планам). Поиск фильтрует карточки всех вкладок на лету.
/// </summary>
public sealed partial class HelpCenterDialog : ContentDialog
{
    private readonly LocalizationService _loc;
    private HelpTab _tab;
    private string _query = string.Empty;
    private TextBox? _search;

    /// <summary>Пользователь нажал «Запустить интерактивный тур» (окно закрыто, тур запускает главное окно).</summary>
    public bool StartTour { get; private set; }

    public HelpCenterDialog(MainViewModel viewModel, string? tab = null)
    {
        _loc = viewModel.Loc;
        _tab = ParseTab(tab);
        InitializeComponent();
        CloseButtonText = _loc.Get("Close");
        TitleText.Text = _loc.Get("HelpTitle");
        SubtitleText.Text = _loc.Get("HelpSubtitle");
        _search = SearchBox(_loc.Get("HelpSearch"));
        _search.TextChanged += (_, _) =>
        {
            _query = _search.Text.Trim();
            Refresh();
        };
        SearchHost.Child = _search;
        TabsHost.Child = Segments(
        [
            (_loc.Get("HelpTabQuick"), HelpTab.QuickStart), (_loc.Get("HelpTabMethods"), HelpTab.Methods),
            (_loc.Get("HelpTabKeys"), HelpTab.Keys), (_loc.Get("HelpTabImports"), HelpTab.Imports)
        ], _tab, t =>
        {
            _tab = (HelpTab)t;
            if (_search is not null && _search.Text.Length > 0) _search.Text = string.Empty;
            else Refresh();
        }, "HelpTabs");
        Opened += (_, _) => FocusSink.Focus(FocusState.Programmatic);
        Refresh();
    }

    public static HelpTab ParseTab(string? tab) => tab?.ToLowerInvariant() switch
    {
        "methods" or "sp" or "norms" => HelpTab.Methods,
        "keys" or "hotkeys" or "manual" => HelpTab.Keys,
        "imports" or "prices" or "plans" => HelpTab.Imports,
        _ => HelpTab.QuickStart
    };

    /// <summary>Только Debug-съёмка: строка поиска.</summary>
    public void DebugSearch(string query)
    {
        if (_search is not null) _search.Text = query;
    }

    private void Refresh()
    {
        Body.Children.Clear();
        if (_query.Length > 0)
        {
            ShowSearch();
            return;
        }
        switch (_tab)
        {
            case HelpTab.QuickStart: ShowQuickStart(); break;
            case HelpTab.Methods: ShowTopics(HelpTab.Methods, columns: 2); break;
            case HelpTab.Keys: ShowKeys(HelpCenterContent.HotKeys); break;
            default: ShowTopics(HelpTab.Imports, columns: 1); break;
        }
    }

    // ───────────────────────────── Быстрый старт ─────────────────────────────

    private void ShowQuickStart()
    {
        Body.Children.Add(Caption(_loc.Get("HelpWorkflow")));
        var chain = new Grid { ColumnSpacing = 4 };
        var steps = HelpCenterContent.Workflow;
        for (int i = 0; i < steps.Count; i++)
        {
            chain.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = StepCard(steps[i]);
            Grid.SetColumn(card, chain.ColumnDefinitions.Count - 1);
            chain.Children.Add(card);
            if (i == steps.Count - 1) break;
            chain.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var arrow = Glyph("", 12, "TextSecondaryBrush");
            Grid.SetColumn(arrow, chain.ColumnDefinitions.Count - 1);
            chain.Children.Add(arrow);
        }
        Body.Children.Add(chain);

        // Интерактивный тур.
        var tour = new Grid { ColumnSpacing = 12 };
        tour.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tour.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tour.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tour.Children.Add(GlyphBadge("", 34));
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(_loc.Get("HelpTour"), 13, strong: true));
        text.Children.Add(Text(_loc.Get("HelpTourHint"), 11, secondary: true, wrap: true));
        Grid.SetColumn(text, 1);
        tour.Children.Add(text);
        var start = ActionButton("", _loc.Get("HelpTourStart"), (_, _) =>
        {
            StartTour = true;
            Hide();
        }, accent: true);
        Grid.SetColumn(start, 2);
        tour.Children.Add(start);
        Body.Children.Add(Card(tour, 14, accent: true));

        ShowTopics(HelpTab.QuickStart, columns: 3);
    }

    private Border StepCard(HelpWorkflowStep step)
    {
        var panel = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = step.Number.ToString(_loc.Culture), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
        });
        head.Children.Add(Glyph(step.Glyph, 14));
        panel.Children.Add(head);
        panel.Children.Add(Text(step.Title, 12.5, strong: true, wrap: true));
        panel.Children.Add(Text(step.Body, 10.5, secondary: true, wrap: true));
        var where = Text(_loc.Get("HelpWhere") + ": " + step.Where, 10, wrap: true);
        where.Foreground = Res("AccentBrush");
        panel.Children.Add(where);
        var card = Card(panel, 11);
        card.VerticalAlignment = VerticalAlignment.Stretch;
        return card;
    }

    // ───────────────────────────── Карточки и клавиши ─────────────────────────────

    private void ShowTopics(HelpTab tab, int columns)
    {
        var topics = HelpCenterContent.Topics.Where(t => t.Tab == tab).ToList();
        if (tab == HelpTab.Methods) Body.Children.Add(Caption(HelpCenterContent.TabTitle(tab)));
        Body.Children.Add(TopicGrid(topics, columns, showTab: false));
    }

    private Grid TopicGrid(IReadOnlyList<HelpTopic> topics, int columns, bool showTab)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < topics.Count; i++)
        {
            if (i % columns == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = TopicCard(topics[i], showTab);
            Grid.SetRow(card, i / columns);
            Grid.SetColumn(card, i % columns);
            grid.Children.Add(card);
        }
        return grid;
    }

    private Border TopicCard(HelpTopic t, bool showTab)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(GlyphBadge(t.Glyph, 28));
        var panel = new StackPanel { Spacing = 4 };
        if (showTab) panel.Children.Add(Chip(HelpCenterContent.TabTitle(t.Tab), "#8E8E93", 9));
        panel.Children.Add(Text(t.Title, 12.5, strong: true, wrap: true));
        bool code = t.Body.Contains('\n') || t.Body.Contains("; ") && t.Title.StartsWith("Пример", StringComparison.Ordinal);
        panel.Children.Add(Text(t.Body, code ? 10.5 : 11, secondary: !code, wrap: true, mono: code));
        if (t.Reference.Length > 0)
        {
            var reference = Text(t.Reference, 10, wrap: true);
            reference.Foreground = Res("AccentBrush");
            panel.Children.Add(reference);
        }
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        var card = Card(grid, 12);
        card.VerticalAlignment = VerticalAlignment.Stretch;
        return card;
    }

    private void ShowKeys(IEnumerable<HelpHotKey> keys)
    {
        if (_query.Length == 0)
        {
            var manual = new StackPanel { Spacing = 4 };
            manual.Children.Add(Text(_loc.Get("HelpManualTitle"), 12.5, strong: true));
            manual.Children.Add(Text(_loc.Get("HelpManualBody"), 11, secondary: true, wrap: true));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var k in new[] { "AddColumn", "AddBeam", "AddWall", "AddSlab", "AddFoundation", "AddStairs", "ManualSplit", "ManualSnap", "ManualDuplicate" })
                row.Children.Add(Chip(_loc.Get(k)));
            manual.Children.Add(row);
            Body.Children.Add(Card(manual, 12, accent: true));
        }
        Body.Children.Add(Caption(_loc.Get("HelpKeysTitle")));
        var table = new StackPanel { Spacing = 0 };
        table.Children.Add(KeyRow(_loc.Get("HelpKeysKey"), _loc.Get("HelpKeysAction"), _loc.Get("HelpKeysContext"), header: true));
        foreach (var k in keys) table.Children.Add(KeyRow(k.Keys, k.Action, k.Context, header: false));
        Body.Children.Add(Card(table, 10));
    }

    private static Grid KeyRow(string keys, string action, string context, bool header)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = header ? 22 : 30, Padding = new Thickness(4, 0, 4, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        if (header)
        {
            row.Children.Add(Caption(keys));
            var a = Caption(action);
            Grid.SetColumn(a, 1);
            row.Children.Add(a);
            var c = Caption(context);
            Grid.SetColumn(c, 2);
            row.Children.Add(c);
            return row;
        }
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var parts = keys.Split(" + ");
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (i > 0) chips.Children.Add(Text("+", 10, secondary: true));
            chips.Children.Add(new Border
            {
                Background = Res("CardBrush"),
                BorderBrush = Res("StudioBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 2),
                Child = new TextBlock { Text = part, FontSize = 11, FontFamily = Mono, Foreground = Res("TextPrimaryBrush") }
            });
        }
        row.Children.Add(chips);
        var act = Text(action, 11.5, wrap: true);
        Grid.SetColumn(act, 1);
        row.Children.Add(act);
        var ctx = Text(context, 10.5, secondary: true, wrap: true);
        Grid.SetColumn(ctx, 2);
        row.Children.Add(ctx);
        return row;
    }

    // ───────────────────────────── Поиск ─────────────────────────────

    private void ShowSearch()
    {
        var topics = HelpCenterContent.Search(_query).ToList();
        var steps = HelpCenterContent.Workflow.Where(s => $"{s.Title} {s.Body} {s.Where}".Contains(_query, StringComparison.OrdinalIgnoreCase)).ToList();
        var keys = HelpCenterContent.HotKeys.Where(k => k.Matches(_query)).ToList();
        int total = topics.Count + steps.Count + keys.Count;
        Body.Children.Add(Caption(total == 0 ? _loc.Get("HelpNoResults") : _loc.Format("HelpResults", total)));
        if (steps.Count > 0)
        {
            var grid = new Grid { ColumnSpacing = 10 };
            foreach (var s in steps)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var card = StepCard(s);
                Grid.SetColumn(card, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(card);
            }
            Body.Children.Add(grid);
        }
        if (topics.Count > 0) Body.Children.Add(TopicGrid(topics, 2, showTab: true));
        if (keys.Count > 0) ShowKeys(keys);
    }
}
