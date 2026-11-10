using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>
/// Окно «Градостроительные ограничения и ЗОУИТ» (Волны 6–7, Liquid Glass). Сверху — четыре этапа глубокого анализа документа
/// (декомпозиция, пораздельный аудит блоков А–Г, перекрёстная верификация, нормативы) со сводкой полноты; ниже — вкладки
/// «Ограничения» (пункты для ГИПа), «Сведения по разделам» (в т. ч. «не подлежит установлению», «отсутствует») и «Сверка»
/// (разделы, сноски, реестровые номера, цитаты, учтённые отрицания). Ничего не применяется вслепую: «Принять» — пункт вступает
/// в силу в проверке посадки; «Исключить условие» раскрывает «Повторить анализ» с уточнением критериев и «Изменить параметры».
/// </summary>
public sealed partial class ConstraintVerificationDialog : ContentDialog
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly MainViewModel _vm;
    private readonly LocalizationService L;
    private readonly ConstraintDialogMode _mode;
    private readonly List<ProjectConstraintItem> _items;
    private readonly ExhaustiveAnalysisReport? _analysis;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private int _view;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _comments = new(StringComparer.Ordinal);
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBlock _summary = Text(string.Empty, 12, secondary: true, wrap: true);
    private readonly ComboBox _model = new() { FontSize = 12, MinWidth = 200, CornerRadius = new CornerRadius(8) };
    private readonly TextBox _query = new() { FontSize = 12.5, CornerRadius = new CornerRadius(8), MinWidth = 420 };
    private Button? _apply;
    private bool _busy;

    /// <summary>Сколько пунктов применено к участку (0 — окно закрыто без применения).</summary>
    public int Applied { get; private set; }

    /// <summary>Пункты в текущем состоянии (для автотеста и съёмки).</summary>
    public IReadOnlyList<ProjectConstraintItem> Items => _items;

    public ConstraintVerificationDialog(MainViewModel vm, ConstraintDialogMode mode)
    {
        _vm = vm;
        _mode = mode;
        L = vm.Loc;
        _items = mode == ConstraintDialogMode.Review
            ? [.. vm.ActiveConstraints]
            : [.. vm.LastConstraintExtraction?.Items ?? []];
        _analysis = mode == ConstraintDialogMode.Document ? vm.LastDocumentAnalysis : null;
        InitializeComponent();
        Background = GlassBrush("LiquidGlassSurfaceBrush", "IslandBrush");
        BorderBrush = GlassBrush("LiquidGlassSpecularBorderBrush", "StudioBorderBrush");
        CloseButtonText = L.Get("W41Close");
        BuildLayout();
        Refresh();
        Opened += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                Root.Width = Math.Min(1180, root.Size.Width - 110);
                Root.Height = Math.Min(760, root.Size.Height - 190);
            }
            Choreograph();
        };
    }

    private static Brush GlassBrush(string key, string fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : Res(fallback);

    private static Border Glass(UIElement child, double padding = 14, bool elevated = true) => new()
    {
        Background = GlassBrush(elevated ? "LiquidGlassElevatedBrush" : "LiquidGlassSurfaceBrush", "CardBrush"),
        BorderBrush = GlassBrush("LiquidGlassSpecularBorderBrush", "StudioBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(padding),
        Child = child
    };

    // ═════════════════════════════ Компоновка ═════════════════════════════

    private void BuildLayout()
    {
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(GlyphBadge(_mode == ConstraintDialogMode.Web ? "\uE774" : "\uE8A5", 34));
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(L.Get("W6ConstraintsTitle"), 19, strong: true));
        string source = _mode switch
        {
            ConstraintDialogMode.Web => L.Format("W6SourceWeb", _vm.LastConstraintQuery),
            ConstraintDialogMode.Review => L.Get("W6SourceReview"),
            _ => _analysis is { } an
                ? L.Format("W6SourceDoc", an.Structure.FileName, an.Structure.PageCount) + (an.Structure.Topo is { } t ? " · " + Path.GetFileName(t.SourceFile) : string.Empty)
                : L.Format("W6SourceDoc", _vm.ConstraintSource?.FileName ?? "—", _vm.ConstraintSource?.Pages.Count ?? 0)
        };
        titles.Children.Add(Text(source, 11.5, secondary: true, wrap: true));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        var modelBox = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        modelBox.Children.Add(Caption(L.Get("W6ModelCaption")));
        foreach (var (key, title) in _vm.ConstraintModelChoices) _model.Items.Add(new ComboBoxItem { Content = title, Tag = key });
        _model.SelectedIndex = 0;
        // Анализ выполнен ядром — в списке выбрано ядро (модель для повторного анализа выбирается здесь же).
        if (_analysis is { ModelTitle: null }) _model.SelectedIndex = Math.Max(0, _model.Items.Count - 1);
        modelBox.Children.Add(_model);
        Grid.SetColumn(modelBox, 2);
        head.Children.Add(modelBox);
        Root.Children.Add(head);

        // Этапы глубокого анализа и вкладки ведомости.
        var stagesHost = new StackPanel { Spacing = 10 };
        if (_analysis is { } a) stagesHost.Children.Add(BuildStages(a));
        stagesHost.Children.Add(_tabs);
        Grid.SetRow(stagesHost, 1);
        Root.Children.Add(stagesHost);

        // Повторный поиск в сети по уточнённому запросу (и для документа — поиск дополнительных норм).
        var search = new Grid { ColumnSpacing = 8 };
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _query.PlaceholderText = L.Get("W6QueryPlaceholder");
        _query.Text = _vm.LastConstraintQuery;
        search.Children.Add(_query);
        var go = ActionButton("\uE721", L.Get("W6SearchWeb"), async (_, _) => await SearchAgainAsync());
        Grid.SetColumn(go, 1);
        search.Children.Add(go);
        Grid.SetRow(search, 2);
        if (_analysis is not null) search.Visibility = Visibility.Collapsed;
        Root.Children.Add(search);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) };
        Grid.SetRow(scroll, 3);
        Root.Children.Add(scroll);

        var foot = new Grid { ColumnSpacing = 10 };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(_summary);
        var all = ActionButton("\uE73E", L.Get("W6ConfirmVerified"), (_, _) =>
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Status == ConstraintStatus.Pending && _items[i].QuoteVerified) _items[i] = _items[i] with { Status = ConstraintStatus.Confirmed };
            Refresh();
        });
        Grid.SetColumn(all, 1);
        foot.Children.Add(all);
        _apply = ActionButton("\uE930", L.Get("W6ApplyConfirmed"), (_, _) => ApplyAndClose(), accent: true);
        _apply.Background = Res("AccentBrush");
        _apply.Foreground = Res("OnAccentBrush");
        Grid.SetColumn(_apply, 2);
        foot.Children.Add(_apply);
        Grid.SetRow(foot, 4);
        Root.Children.Add(foot);
    }

    /// <summary>Четыре плитки этапов: номер, название, сводка, время; итог полноты — чипами.</summary>
    private Grid BuildStages(ExhaustiveAnalysisReport a)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var s in a.Stages.OrderBy(s => s.Number).Take(4))
        {
            var tile = new StackPanel { Spacing = 3 };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            top.Children.Add(new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Res("AccentBrush"),
                Child = new FontIcon { Glyph = "\uE73E", FontSize = 10, Foreground = Res("OnAccentBrush") }
            });
            top.Children.Add(Text(L.Format("W7StageN", s.Number), 10.5, secondary: true));
            top.Children.Add(Text(s.Elapsed.TotalSeconds.ToString("0.0", Ru) + " с", 10.5, mono: true, secondary: true));
            tile.Children.Add(top);
            tile.Children.Add(Text(s.Title, 12, strong: true, wrap: true));
            var summary = Text(s.Summary, 10.5, secondary: true, wrap: true);
            summary.MaxLines = 4;
            tile.Children.Add(summary);
            var card = Glass(tile, 10);
            Grid.SetColumn(card, s.Number - 1);
            grid.Children.Add(card);
        }
        return grid;
    }

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        if (_analysis is null) return;
        var items = new List<(string Text, object Tag)>
        {
            (L.Format("W7ViewItems", _items.Count), 0),
            (L.Format("W7ViewFacts", _analysis.Facts.Count), 1),
            (L.Format("W7ViewCheck", _analysis.Coverage.Count(c => c.Passed), _analysis.Coverage.Count), 2)
        };
        _tabs.Children.Add(Segments(items, _view, v => { _view = (int)v; Refresh(); }, "ConstraintView"));
        string completeness = string.Create(Ru, $"{L.Get("W7Sections")} {_analysis.SectionsFound}/{_analysis.SectionsExpected} · {L.Get("W7FootnotesShort")} {_analysis.FootnotesCovered}/{_analysis.FootnotesTotal} · ") +
                              string.Create(Ru, $"ЗОУИТ {_analysis.RegistryMatched}/{_analysis.RegistryTotal} · {L.Get("W7QuotesShort")} {_analysis.QuotesVerified}/{_analysis.QuotesTotal}");
        _tabs.Children.Add(Chip(completeness, _analysis.Complete ? "#10B981" : "#F59E0B", 10.5));
    }

    private void BuildFacts(ExhaustiveAnalysisReport a)
    {
        foreach (var block in Enum.GetValues<AnalysisBlock>())
        {
            var facts = a.Facts.Where(f => f.Block == block).ToList();
            if (facts.Count == 0) continue;
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(Text(ExhaustiveDocumentAnalyzerService.BlockTitle(block), 13.5, strong: true));
            foreach (var f in facts)
            {
                var g = new Grid { ColumnSpacing = 12 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
                var left = new StackPanel { Spacing = 2 };
                left.Children.Add(Text(f.Title, 12, strong: true, wrap: true));
                var value = Text(f.Value, 11.5, wrap: true, mono: f.Status == FactStatus.Established && f.Value.Any(char.IsDigit));
                if (f.Status == FactStatus.Established) value.Foreground = Res("AccentBrush");
                left.Children.Add(value);
                g.Children.Add(left);
                var quote = Text("«" + f.Quote + "»", 11, secondary: true, wrap: true);
                quote.FontStyle = Windows.UI.Text.FontStyle.Italic;
                quote.MaxLines = 3;
                var mid = new StackPanel { Spacing = 2 };
                mid.Children.Add(quote);
                mid.Children.Add(Text((f.Page > 0 ? L.Format("W6Page", f.Page) + " · " : string.Empty) + f.Clause + " · " + f.ExtractedBy, 10, secondary: true));
                Grid.SetColumn(mid, 1);
                g.Children.Add(mid);
                var chip = Chip(L.Get(f.Status switch
                {
                    FactStatus.Established => "W7FactEstablished",
                    FactStatus.NotEstablished => "W7FactNotEstablished",
                    FactStatus.Absent => "W7FactAbsent",
                    _ => "W7FactInfo"
                }), f.Status switch { FactStatus.Established => "#10B981", FactStatus.NotEstablished => "#64748B", FactStatus.Absent => "#94A3B8", _ => "#0A84FF" }, 10.5);
                chip.HorizontalAlignment = HorizontalAlignment.Right;
                chip.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(chip, 2);
                g.Children.Add(chip);
                stack.Children.Add(g);
            }
            _list.Children.Add(Glass(stack, 14));
        }
    }

    private void BuildCheck(ExhaustiveAnalysisReport a)
    {
        foreach (var (kind, title) in new[] { ("section", L.Get("W7CheckSections")), ("footnote", L.Get("W7CheckFootnotes")), ("registry", L.Get("W7CheckRegistry")), ("quote", L.Get("W7CheckQuotes")) })
        {
            var rows = a.Coverage.Where(c => c.Kind == kind).ToList();
            if (rows.Count == 0) continue;
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(Text(string.Create(Ru, $"{title} · {rows.Count(r => r.Passed)}/{rows.Count}"), 13, strong: true));
            foreach (var r in rows)
            {
                var g = new Grid { ColumnSpacing = 8 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
                g.Children.Add(Glyph(r.Passed ? "\uE73E" : "\uE7BA", 11, r.Passed ? "AccentBrush" : "TextPrimaryBrush"));
                var t = Text(r.Title, 11.5, wrap: true);
                Grid.SetColumn(t, 1);
                g.Children.Add(t);
                var d = Text(r.Detail, 11, secondary: true, wrap: true);
                if (!r.Passed) d.Foreground = Hex("#F59E0B");
                Grid.SetColumn(d, 2);
                g.Children.Add(d);
                stack.Children.Add(g);
            }
            _list.Children.Add(Glass(stack, 14));
        }
        if (a.Negations.Count > 0)
        {
            var neg = new StackPanel { Spacing = 4 };
            neg.Children.Add(Text(string.Create(Ru, $"{L.Get("W7CheckNegations")} · {a.Negations.Count}"), 13, strong: true));
            foreach (var n in a.Negations.Distinct().Take(40)) neg.Children.Add(Text(n, 11, secondary: true, wrap: true));
            _list.Children.Add(Glass(neg, 14));
        }
    }

    private string ModelKey => (_model.SelectedItem as ComboBoxItem)?.Tag as string ?? "core";

    // ═════════════════════════════ Карточки ═════════════════════════════

    public void Refresh()
    {
        _list.Children.Clear();
        BuildTabs();
        if (_view == 1 && _analysis is { } fa) BuildFacts(fa);
        else if (_view == 2 && _analysis is { } ca) BuildCheck(ca);
        else
        {
            if (_items.Count == 0)
                _list.Children.Add(Glass(Text(L.Get("W6NoItems"), 12.5, secondary: true, wrap: true)));
            foreach (var item in _items) _list.Children.Add(BuildCard(item));
        }
        int confirmed = _items.Count(i => i.IsActive), rejected = _items.Count(i => i.Status == ConstraintStatus.Rejected);
        _summary.Text = string.Create(Ru, $"{L.Get("W6Total")} {_items.Count} · {L.Get("W6Confirmed")} {confirmed} · {L.Get("W6Rejected")} {rejected} · {L.Get("W6Pending")} {_items.Count - confirmed - rejected}") +
                        (_vm.LastConstraintExtraction is { ModelError: { Length: > 0 } err } ? " · " + L.Get("W6ModelFailed") + ": " + err : string.Empty);
        if (_apply is not null) _apply.IsEnabled = confirmed > 0 && !_busy;
    }

    private Border BuildCard(ProjectConstraintItem item)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

        // 1. Наименование условия, категория, значение для ядра.
        var left = new StackPanel { Spacing = 5 };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(Chip(DeepDocumentConstraintService.CategoryTitle(item.Category), CategoryColor(item.Category)));
        chips.Children.Add(Chip(ImpactTitle(item.Impact), item.Impact == ConstraintImpact.Blocking ? "#EF4444" : item.Impact == ConstraintImpact.RequiresApproval ? "#F59E0B" : "#64748B"));
        if (item.IsFootnote) chips.Children.Add(Chip(L.Get("W6Footnote"), "#A855F7"));
        if (item.Origin == ConstraintOrigin.Normative) chips.Children.Add(Chip(L.Get("W7NormativeChip"), "#0EA5E9"));
        left.Children.Add(chips);
        left.Children.Add(Text(item.Title, 13.5, strong: true, wrap: true));
        var value = Text(item.ValueText(Ru), 12.5, mono: true, wrap: true);
        value.Foreground = Res("AccentBrush");
        left.Children.Add(value);
        if (item.ImpactText.Length > 0) left.Children.Add(Text(item.ImpactText, 11, secondary: true, wrap: true));
        grid.Children.Add(left);

        // 2. Источник и цитата.
        var mid = new StackPanel { Spacing = 5 };
        string where = (item.Page > 0 ? L.Format("W6Page", item.Page) : string.Empty) + (item.Clause.Length > 0 ? (item.Page > 0 ? ", " : string.Empty) + item.Clause : string.Empty);
        var srcRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        srcRow.Children.Add(Glyph(item.Origin == ConstraintOrigin.Web ? "\uE774" : item.Origin == ConstraintOrigin.Normative ? "\uE82D" : "\uE8A5", 11, "TextSecondaryBrush"));
        srcRow.Children.Add(Text(Short(item.Source, 70) + (where.Length > 0 ? " · " + where : string.Empty), 11, secondary: true));
        mid.Children.Add(srcRow);
        if (item.Quote.Length > 0)
        {
            var quote = Text("«" + item.Quote + "»", 12, wrap: true);
            quote.FontStyle = Windows.UI.Text.FontStyle.Italic;
            quote.MaxLines = 5;
            mid.Children.Add(new Border { BorderBrush = Res("AccentBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 2, 0, 2), Child = quote });
        }
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        meta.Children.Add(Chip(item.QuoteVerified ? L.Get("W6QuoteVerified") : L.Get("W6QuoteUnverified"), item.QuoteVerified ? "#10B981" : "#F59E0B"));
        meta.Children.Add(Text(item.ExtractedBy + string.Create(Ru, $" · {item.Confidence * 100:0} %"), 10.5, secondary: true));
        mid.Children.Add(meta);
        if (item.UserComment.Length > 0) mid.Children.Add(Text(L.Format("W6UserComment", item.UserComment), 10.5, secondary: true, wrap: true));
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        // 3. Действия.
        var right = new StackPanel { Spacing = 6 };
        right.Children.Add(StatusChip(item.Status));
        if (item.Status != ConstraintStatus.Rejected)
        {
            var confirm = ActionButton("\uE73E", item.IsActive ? L.Get("W6Confirmed1") : L.Get("W6Confirm"), (_, _) => SetStatus(item, item.IsActive ? ConstraintStatus.Pending : ConstraintStatus.Confirmed),
                accent: !item.IsActive);
            if (!item.IsActive)
            {
                confirm.Background = Res("AccentBrush");
                confirm.Foreground = Res("OnAccentBrush");
            }
            confirm.HorizontalAlignment = HorizontalAlignment.Stretch;
            right.Children.Add(confirm);
            var reject = ActionButton("\uE711", L.Get("W6Reject"), (_, _) =>
            {
                SetStatus(item, ConstraintStatus.Rejected);
                _expanded.Add(item.Id);
                Refresh();
            });
            reject.HorizontalAlignment = HorizontalAlignment.Stretch;
            right.Children.Add(reject);
        }
        else
        {
            var back = ActionButton("\uE7A7", L.Get("W6Restore"), (_, _) => SetStatus(item, ConstraintStatus.Pending));
            back.HorizontalAlignment = HorizontalAlignment.Stretch;
            right.Children.Add(back);
        }
        if (item.Status != ConstraintStatus.Rejected && !_expanded.Contains(item.Id))
        {
            var edit = new HyperlinkButton { Content = L.Get("W6EditManually"), FontSize = 11, Padding = new Thickness(2) };
            edit.Click += (_, _) => { _expanded.Add(item.Id); Refresh(); };
            right.Children.Add(edit);
        }
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(grid);
        if (_expanded.Contains(item.Id)) stack.Children.Add(BuildCorrection(item));
        var card = Glass(stack, 14);
        if (item.Status == ConstraintStatus.Rejected) card.Opacity = 0.92;
        return card;
    }

    /// <summary>Панель исправления под отклонённым пунктом: «Повторить поиск с уточнением» и «Задать / исправить вручную».</summary>
    private Border BuildCorrection(ProjectConstraintItem item)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var retry = new StackPanel { Spacing = 6 };
        retry.Children.Add(Caption(L.Get("W6RetryCaption")));
        var comment = new TextBox
        {
            PlaceholderText = L.Get("W6CommentPlaceholder"), FontSize = 12, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false,
            Text = _comments.GetValueOrDefault(item.Id, string.Empty), CornerRadius = new CornerRadius(8)
        };
        comment.TextChanged += (_, _) => _comments[item.Id] = comment.Text;
        retry.Children.Add(comment);
        retry.Children.Add(ActionButton("\uE72C", L.Get("W6Retry"), async (_, _) => await RetryAsync(item, comment.Text), enabled: !_busy));
        grid.Children.Add(retry);

        var manual = new StackPanel { Spacing = 6 };
        var (name, current) = DeepDocumentConstraintService.PrimaryValue(item);
        manual.Children.Add(Caption(L.Get("W6ManualCaption") + " · " + name));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var number = new NumberBox { Value = current ?? double.NaN, PlaceholderText = name, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
        row.Children.Add(number);
        var title = new TextBox { Text = item.Title, FontSize = 12, CornerRadius = new CornerRadius(8) };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        manual.Children.Add(row);
        var relocation = new CheckBox { Content = L.Get("W6RelocationFlag"), IsChecked = item.RequiresNetworkRelocation, FontSize = 12 };
        manual.Children.Add(relocation);
        manual.Children.Add(ActionButton("\uE70F", L.Get("W6Manual"), (_, _) =>
        {
            double? v = double.IsFinite(number.Value) ? number.Value : null;
            Replace(item, DeepDocumentConstraintService.Manual(item, v, title.Text.Trim(), relocation.IsChecked == true));
            _expanded.Remove(item.Id);
            Refresh();
        }));
        Grid.SetColumn(manual, 1);
        grid.Children.Add(manual);
        return new Border
        {
            Background = GlassBrush("LiquidGlassSurfaceBrush", "CardBrush"),
            BorderBrush = Res("StudioBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = grid
        };
    }

    private Border StatusChip(ConstraintStatus s) => s switch
    {
        ConstraintStatus.Confirmed => Chip(L.Get("W6StatusConfirmed"), "#10B981", 11),
        ConstraintStatus.Manual => Chip(L.Get("W6StatusManual"), "#0A84FF", 11),
        ConstraintStatus.Rejected => Chip(L.Get("W6StatusRejected"), "#EF4444", 11),
        _ => Chip(L.Get("W6StatusPending"), "#94A3B8", 11)
    };

    private string ImpactTitle(ConstraintImpact i) => L.Get(i switch
    {
        ConstraintImpact.Blocking => "W6ImpactBlocking",
        ConstraintImpact.RequiresApproval => "W6ImpactApproval",
        _ => "W6ImpactInfo"
    });

    private static string CategoryColor(ConstraintCategory c) => c switch
    {
        ConstraintCategory.HeightLimit => "#3B82F6",
        ConstraintCategory.NetworkRelocation or ConstraintCategory.ProtectionZone => "#EF4444",
        ConstraintCategory.SanitaryZone => "#84CC16",
        ConstraintCategory.FireAccess => "#F97316",
        ConstraintCategory.LivingAreaLimit or ConstraintCategory.Parking => "#14B8A6",
        ConstraintCategory.CulturalHeritage => "#D946EF",
        ConstraintCategory.Setback => "#10B981",
        _ => "#8B5CF6"
    };

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ═════════════════════════════ Действия ═════════════════════════════

    private void SetStatus(ProjectConstraintItem item, ConstraintStatus status)
    {
        Replace(item, item with { Status = status });
        if (status != ConstraintStatus.Rejected) _expanded.Remove(item.Id);
        Refresh();
    }

    private void Replace(ProjectConstraintItem old, ProjectConstraintItem next)
    {
        int i = _items.FindIndex(x => x.Id == old.Id);
        if (i >= 0) _items[i] = next;
    }

    /// <summary>«Повторить поиск с уточнением»: исправленные пункты встают сразу после отклонённого.</summary>
    public async Task RetryAsync(ProjectConstraintItem item, string comment)
    {
        if (_busy) return;
        _busy = true;
        _summary.Text = L.Get("W6Retrying");
        try
        {
            var fresh = await _vm.RefineConstraintAsync(item, comment.Trim(), ModelKey);
            int at = _items.FindIndex(x => x.Id == item.Id);
            _items.InsertRange(at < 0 ? _items.Count : at + 1, fresh.Where(f => _items.All(x => x.MergeKey != f.MergeKey || x.Status == ConstraintStatus.Rejected)));
            _expanded.Remove(item.Id);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async Task SearchAgainAsync()
    {
        string q = _query.Text.Trim();
        if (q.Length < 4 || _busy) return;
        _busy = true;
        _summary.Text = L.Get("W6Searching");
        try
        {
            var found = await _vm.SearchConstraintsAsync(q, ModelKey, openDialog: false);
            foreach (var f in found.Items.Where(f => _items.All(x => x.MergeKey != f.MergeKey))) _items.Add(f);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void ApplyAndClose()
    {
        Applied = _vm.ApplyConfirmedConstraints(_items.Where(i => i.IsActive));
        Hide();
    }

    /// <summary>Пункты проявляются с шагом 24 мс (не больше 8), с учётом системной настройки анимаций.</summary>
    private void Choreograph()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        int i = 0;
        foreach (var child in _list.Children.OfType<UIElement>().Take(8))
        {
            child.Opacity = 0;
            var sb = new Storyboard();
            var a = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180), BeginTime = TimeSpan.FromMilliseconds(24 * i++), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, child);
            Storyboard.SetTargetProperty(a, "Opacity");
            sb.Children.Add(a);
            sb.Begin();
        }
        foreach (var child in _list.Children.OfType<UIElement>().Skip(8)) child.Opacity = 1;
    }

#if DEBUG
    /// <summary>Съёмка и автотест: вкладка 0 — ограничения, 1 — сведения по разделам, 2 — сверка.</summary>
    public void DebugView(int view)
    {
        _view = Math.Clamp(view, 0, 2);
        Refresh();
    }

    /// <summary>Съёмка: первый пункт подтверждён, второй отклонён с раскрытой панелью уточнения и комментарием, третий задан вручную.</summary>
    public void DebugShowcase()
    {
        // Подтверждён пункт со сверенной цитатой и числом; отклонён — ограничение Hабс выше определяющего (у эталонного ГПЗУ
        // определяющее — 365,00 м БС), с комментарием; вручную — машино-места.
        int confirm = _items.FindIndex(i => i.QuoteVerified && i.HasNumbers && i.MaxHeightAbsM is null);
        int reject = _items.FindIndex(i => i.MaxHeightAbsM is > 370);
        int manual = _items.FindIndex(i => i.MinParkingSpots.HasValue);
        if (confirm < 0) confirm = 0;
        if (reject < 0) reject = Math.Min(1, _items.Count - 1);
        if (manual < 0 || manual == reject || manual == confirm) manual = Enumerable.Range(0, _items.Count).FirstOrDefault(i => i != confirm && i != reject, -1);
        if (confirm >= 0 && confirm < _items.Count) _items[confirm] = _items[confirm] with { Status = ConstraintStatus.Confirmed };
        if (reject >= 0 && reject < _items.Count)
        {
            _items[reject] = _items[reject] with { Status = ConstraintStatus.Rejected };
            _expanded.Add(_items[reject].Id);
            _comments[_items[reject].Id] = L.Get("W6DemoComment");
        }
        if (manual >= 0 && manual < _items.Count)
            _items[manual] = DeepDocumentConstraintService.Manual(_items[manual], (DeepDocumentConstraintService.PrimaryValue(_items[manual]).Value ?? 130) + 10);
        // Наверх — показательные пункты: подтверждённый, отклонённый с панелью уточнения, заданный вручную.
        var order = new[] { confirm, reject, manual }.Where(i => i >= 0 && i < _items.Count).Distinct().Select(i => _items[i]).ToList();
        _items.RemoveAll(order.Contains);
        _items.InsertRange(0, order);
        Refresh();
    }
#endif
}
