using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.ViewModels;
using AIArchitectStudio.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio;

/// <summary>
/// Волна 6 в главном окне: панель активной Среды девелопера (участок и «НО» / здание на участке / пакеты этажей и вводы сетей),
/// окно подтверждения ограничений, защита выхода (AppWindow.Closing → «Сохранить прогресс проекта перед выходом?») и индикатор
/// автосохранения.
/// </summary>
public sealed partial class MainWindow
{
    private bool _wave6Queued;
    private bool _wave6DialogOpen;
    private bool _exitConfirmed;
    private ConstraintDialogMode? _constraintsPending;

    /// <summary>Последнее открытое окно подтверждения ограничений (автотест и съёмка).</summary>
    internal ConstraintVerificationDialog? LastConstraintDialog { get; private set; }

    private static Brush GlassRes(string key, string fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : Res(fallback);

    private void InitializeWave6()
    {
        EnvironmentPanel.Background = GlassRes("LiquidGlassSurfaceBrush", "IslandBrush");
        EnvironmentPanel.BorderBrush = GlassRes("LiquidGlassSpecularBorderBrush", "StudioBorderBrush");
        AutoSavePill.Background = GlassRes("LiquidGlassElevatedBrush", "IslandBrush");
        AutoSavePill.BorderBrush = GlassRes("LiquidGlassSpecularBorderBrush", "StudioBorderBrush");
        ViewModel.EnvironmentChanged += (_, _) => { QueueWave6Refresh(); UpdateAiDockMargin(); };
        ViewModel.SiteMetricsChanged += (_, _) => QueueWave6Refresh();
        ViewModel.InteriorsChanged += (_, _) => QueueWave6Refresh();
        ViewModel.Scene.SitePlanChanged += (_, _) => QueueWave6Refresh();
        ViewModel.Scene.DocumentChanged += (_, _) =>
        {
            if (ViewModel.ActiveEnvironment == DeveloperEnvironment.Interiors) ViewModel.RefreshInteriors();
        };
        ViewModel.ConstraintDialogRequested += async (_, mode) => await ShowConstraintDialogAsync(mode);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsEnvironmentPanelVisible)) UpdateAiDockMargin();
            if (e.PropertyName is nameof(MainViewModel.HasSitePlan) or nameof(MainViewModel.IsSiteEditing)) QueueWave6Refresh();
        };
        L.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.Language)) QueueWave6Refresh();
        };
        AppWindow.Closing += OnAppWindowClosing;
        QueueWave6Refresh();
#if DEBUG
        InitializeWave6Debug();
#endif
    }

    private void QueueWave6Refresh()
    {
        if (_wave6Queued) return;
        _wave6Queued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _wave6Queued = false;
            RefreshEnvironmentPanel();
        });
    }

    // ═════════════════════════════ Панель Среды ═════════════════════════════

    private void RefreshEnvironmentPanel()
    {
        var panel = EnvironmentPanelContent;
        panel.Children.Clear();
        var vm = ViewModel;
        var d = WorkspaceEnvironmentService.Of(vm.ActiveEnvironment);
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(GlyphBadge(d.Glyph, 30, Hex(d.ColorHex, 0x33), Hex(d.ColorHex)));
        var titles = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(L.Format("W6EnvN", d.Number), 10.5, secondary: true));
        titles.Children.Add(Text(L.Get(d.TitleKey), 13.5, strong: true, wrap: true));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        var close = new Button { Style = (Style)Application.Current.Resources["IconButtonStyle"], Content = new FontIcon { Glyph = "\uE711", FontSize = 11 }, VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(close, L.Get("W6PanelHide"));
        close.Click += (_, _) => vm.IsEnvironmentPanelOpen = false;
        Grid.SetColumn(close, 2);
        head.Children.Add(close);
        panel.Children.Add(head);
        panel.Children.Add(Text(L.Get(d.HintKey), 11, secondary: true, wrap: true));
        switch (vm.ActiveEnvironment)
        {
            case DeveloperEnvironment.Site:
                BuildSitePanel(panel);
                break;
            case DeveloperEnvironment.Building:
                BuildBuildingPanel(panel);
                break;
            default:
                BuildInteriorsPanel(panel);
                break;
        }
    }

    private static Border PanelCard(UIElement child) => new()
    {
        Background = GlassRes("LiquidGlassElevatedBrush", "CardBrush"),
        BorderBrush = GlassRes("LiquidGlassSpecularBorderBrush", "StudioBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 10, 12, 10),
        Child = child
    };

    /// <summary>Строки «подпись · число · единица»: число моноширинно, по правому краю.</summary>
    private static Grid Rows(IEnumerable<(string Name, string Value, string Unit)> rows)
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        int r = 0;
        foreach (var (name, value, unit) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var n = Text(name, 11, secondary: true);
            var v = Text(value, 11.5, mono: true);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            var u = Text(unit, 10.5, secondary: true);
            Grid.SetRow(n, r); Grid.SetRow(v, r); Grid.SetRow(u, r);
            Grid.SetColumn(v, 1); Grid.SetColumn(u, 2);
            grid.Children.Add(n); grid.Children.Add(v); grid.Children.Add(u);
            r++;
        }
        return grid;
    }

    private static Button StretchButton(Button b)
    {
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Left;
        return b;
    }

    // ── Среда 1: участок и ГПЗУ ──

    private void BuildSitePanel(StackPanel panel)
    {
        var vm = ViewModel;
        var c = L.Culture;
        if (vm.Scene.SitePlan is not { } plan)
        {
            var empty = new StackPanel { Spacing = 8 };
            empty.Children.Add(Text(L.Get("W6NoSiteTitle"), 12.5, strong: true));
            empty.Children.Add(Text(L.Get("W6NoSiteHint"), 11, secondary: true, wrap: true));
            empty.Children.Add(StretchButton(ActionButton("\uE707", L.Get("W5MenuOpen"), async (_, _) => await ShowGpzuStudioAsync(), accent: true)));
            empty.Children.Add(StretchButton(ActionButton("\uE8B7", L.Get("W6DemoSite"), async (_, _) => await ViewModel.LoadDemoSiteAsync())));
            panel.Children.Add(PanelCard(empty));
            panel.Children.Add(ConstraintsCard());
            return;
        }
        var m = vm.SiteMetrics ?? vm.RefreshSiteMetrics();
        if (m is not null)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(Caption(L.Get("W6SiteLive") + " · " + plan.Document.Cadastral));
            stack.Children.Add(Rows(
            [
                (L.Get("W6GaussArea"), m.GaussArea.ToString("N2", c), "м²"),
                (L.Get("W6Perimeter"), m.Perimeter.ToString("N2", c), "м"),
                (L.Get("W6AllowedArea"), m.AllowedArea.ToString("N2", c), "м²"),
                (L.Get("W6Relief"), $"{m.MinRelief.ToString("+0.00;-0.00;0.00", c)}…{m.MaxRelief.ToString("+0.00;-0.00;0.00", c)}", "м"),
                (L.Get("W6Cut"), m.Earthworks.CutM3.ToString("N1", c), "м³"),
                (L.Get("W6Fill"), m.Earthworks.FillM3.ToString("N1", c), "м³"),
                (L.Get("W6Buildings"), m.Buildings.ToString(c), string.Empty)
            ]));
            var verdict = Text(m.Compliance.Passed ? L.Get("W6PlacementOk") : L.Format("W6PlacementBad", m.Compliance.Critical), 11.5, strong: true, wrap: true);
            verdict.Foreground = Hex(m.Compliance.Passed ? "#10B981" : "#EF4444");
            stack.Children.Add(verdict);
            panel.Children.Add(PanelCard(stack));
        }

        // Выбранная точка: координаты МСК и отметка рельефа — правка с клавиатуры (каждое «Применить» — шаг журнала).
        var point = new StackPanel { Spacing = 6 };
        if (vm.SelectedSiteVertex is { } sel && EditableSiteTerrainService.PointOf(plan, sel) is { } sp)
        {
            var (gx, gy) = plan.ToGeo(sp.X, sp.Z);
            string label = sel.Layer == SiteVertexLayer.Boundary ? L.Format("W6PointN", plan.PointNumbers[sel.Index]) : L.Format("W6ZoneVertexN", sel.Index + 1);
            point.Children.Add(Caption(label));
            var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBox Field(string name, double value, int row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var n = Text(name, 11, secondary: true);
                n.VerticalAlignment = VerticalAlignment.Center;
                var box = new TextBox { Text = value.ToString("0.00", c), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextAlignment = TextAlignment.Right, Height = 28, MinHeight = 28, Padding = new Thickness(6, 3, 6, 3) };
                Grid.SetRow(n, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1);
                grid.Children.Add(n); grid.Children.Add(box);
                return box;
            }
            var xBox = Field("X (С), м", gx, 0);
            var yBox = Field("Y (В), м", gy, 1);
            TextBox? zBox = sel.Layer == SiteVertexLayer.Boundary ? Field("Z БС, м", EditableSiteTerrainService.EffectiveVertexElevation(plan, sel.Index), 2) : null;
            point.Children.Add(grid);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(ActionButton("\uE73E", L.Get("W6Apply"), (_, _) =>
            {
                if (double.TryParse(xBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(yBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                    (Math.Abs(x - gx) > 0.004 || Math.Abs(y - gy) > 0.004))
                    vm.MoveSiteVertexGeo(sel, x, y);
                if (zBox is not null && double.TryParse(zBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double zb) &&
                    Math.Abs(zb - EditableSiteTerrainService.EffectiveVertexElevation(plan, sel.Index)) > 0.004)
                    vm.SetSiteVertexElevation(sel.Index, zb);
            }, accent: true));
            buttons.Children.Add(ActionButton("\uE710", L.Get("W6InsertAfter"), (_, _) => vm.InsertSiteVertex(sel)));
            buttons.Children.Add(ActionButton("\uE74D", string.Empty, (_, _) => vm.RemoveSiteVertex(sel), tip: L.Get("W6RemovePoint")));
            point.Children.Add(buttons);
        }
        else
        {
            point.Children.Add(Caption(L.Get("W6PointCaption")));
            point.Children.Add(Text(L.Get("W6PointHint"), 11, secondary: true, wrap: true));
        }
        panel.Children.Add(PanelCard(point));

        // Стороны 1–2, 2–3, … — длины моноширинно.
        var sides = new StackPanel { Spacing = 4 };
        sides.Children.Add(Caption(L.Get("W6Sides")));
        sides.Children.Add(Rows(plan.Segments.Take(24).Select(s => ($"{s.From}–{s.To}", s.Length.ToString("N2", c), "м"))));
        panel.Children.Add(PanelCard(sides));
        panel.Children.Add(ConstraintsCard());
        if (m is not null)
        {
            var findings = new StackPanel { Spacing = 4 };
            findings.Children.Add(Caption(L.Get("W6Compliance")));
            foreach (var f in m.Compliance.Findings.Where(f => f.Severity != GpzuSeverity.Info).Take(8))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(Glyph(f.Severity == GpzuSeverity.Ok ? "\uE73E" : "\uE711", 11, f.Severity == GpzuSeverity.Ok ? "AccentBrush" : "TextSecondaryBrush"));
                var t = Text(f.Title, 11, wrap: true);
                t.MaxWidth = 240;
                if (f.Severity == GpzuSeverity.Critical) t.Foreground = Hex("#EF4444");
                row.Children.Add(t);
                findings.Children.Add(row);
            }
            panel.Children.Add(PanelCard(findings));
        }
    }

    /// <summary>Карточка «Система ограничений и «НО»»: действующие пункты, чтение документа, поиск в сети, проверка.</summary>
    private Border ConstraintsCard()
    {
        var vm = ViewModel;
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(Caption(L.Get("W6ConstraintsCard")));
        var active = vm.ActiveConstraints;
        stack.Children.Add(Text(active.Count == 0 ? L.Get("W6NoConstraints") : L.Format("W6ActiveConstraints", active.Count), 11.5, wrap: true));
        foreach (var c in active.Take(5))
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = Text(c.Title, 11, secondary: true);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(t);
            var v = Text(c.ValueText(L.Culture), 10.5, mono: true);
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            stack.Children.Add(row);
        }
        stack.Children.Add(StretchButton(ActionButton("\uE8A5", L.Get("W6ReadDocument"), async (_, _) => await PickConstraintDocumentAsync())));
        stack.Children.Add(StretchButton(ActionButton("\uE721", L.Get("W6FindWeb"), async (_, _) => await ViewModel.SearchConstraintsAsync(L.Get("W6DefaultQuery")))));
        if (active.Count > 0) stack.Children.Add(StretchButton(ActionButton("\uE9D5", L.Get("W6ReviewConstraints"), (_, _) => ViewModel.OpenConstraintReviewCommand.Execute(null))));
        return PanelCard(stack);
    }

    private async Task PickConstraintDocumentAsync()
    {
        var picker = InitPicker(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary });
        foreach (var ext in new[] { ".pdf", ".docx", ".txt", ".xml", ".json", ".csv" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is { } file) await ViewModel.ExtractConstraintsAsync(file.Path);
    }

    // ── Среда 2: объём и конструктив ──

    private void BuildBuildingPanel(StackPanel panel)
    {
        var vm = ViewModel;
        var add = new StackPanel { Spacing = 6 };
        add.Children.Add(Caption(L.Get("W6CallBuilding")));
        add.Children.Add(Text(vm.HasSitePlan ? L.Get("W6CallHintSite") : L.Get("W6CallHintNoSite"), 11, secondary: true, wrap: true));
        foreach (var (kind, glyph) in new[] { (SiteStructureKind.ResidentialSection, "\uE80F"), (SiteStructureKind.Tower, "\uE9D9"), (SiteStructureKind.TransformerSubstation, "\uE945"), (SiteStructureKind.Parking, "\uE804") })
            add.Children.Add(StretchButton(ActionButton(glyph, EditableSiteTerrainService.StructureTitle(kind), (_, _) => vm.AddStructureToSite(kind))));
        panel.Children.Add(PanelCard(add));

        if (vm.SiteMetrics is { } m)
        {
            var check = new StackPanel { Spacing = 4 };
            check.Children.Add(Caption(L.Get("W6Compliance")));
            check.Children.Add(Rows(
            [
                (L.Get("W6Footprint"), m.Compliance.FootprintArea.ToString("N0", L.Culture), "м²"),
                (L.Get("W6Coverage"), m.Compliance.CoveragePercent.ToString("0.0", L.Culture), "%"),
                (L.Get("W6Height"), m.Compliance.MaxHeight.ToString("0.0", L.Culture), "м"),
                (L.Get("W6Living"), m.Compliance.LivingAreaEstimate.ToString("N0", L.Culture), "м²")
            ]));
            var verdict = Text(m.Compliance.Passed ? L.Get("W6PlacementOk") : L.Format("W6PlacementBad", m.Compliance.Critical), 11.5, strong: true, wrap: true);
            verdict.Foreground = Hex(m.Compliance.Passed ? "#10B981" : "#EF4444");
            check.Children.Add(verdict);
            panel.Children.Add(PanelCard(check));
        }

        var tools = new StackPanel { Spacing = 6 };
        tools.Children.Add(Caption(L.Get("W6FrameTools")));
        tools.Children.Add(StretchButton(ActionButton("\uE9D9", L.Get("W2StructuralMode"), (_, _) => vm.IsStructuralMode = true)));
        if (vm.IsQuantityModule) tools.Children.Add(StretchButton(ActionButton("\uE9F9", L.Get("QtyButtonTip"), (s, e) => OnQuantitiesClick(s, e))));
        tools.Children.Add(StretchButton(ActionButton("\uE8F1", L.Get("W6OpenLibrary"), (_, _) => vm.OpenLibraryCommand.Execute(null))));
        panel.Children.Add(PanelCard(tools));
    }

    // ── Среда 3: планировки, ядра и инженерия ──

    private void BuildInteriorsPanel(StackPanel panel)
    {
        var vm = ViewModel;
        var s = vm.Interiors ?? vm.RefreshInteriors();
        var c = L.Culture;
        var packages = new StackPanel { Spacing = 6 };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.Children.Add(Caption(L.Get("W6Packages")));
        var size = new NumberBox { Value = vm.FloorPackageSize, Minimum = 1, Maximum = 50, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, FontSize = 12 };
        ToolTipService.SetToolTip(size, L.Get("W6PackageSize"));
        size.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) vm.FloorPackageSize = (int)e.NewValue; };
        Grid.SetColumn(size, 1);
        row.Children.Add(size);
        packages.Children.Add(row);
        packages.Children.Add(Text(L.Format("W6LevelsSummary", s.Levels, s.Cores, s.StairFlights), 11, secondary: true, wrap: true));
        foreach (var p in s.Packages)
        {
            var g = new Grid { ColumnSpacing = 6 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Text($"{p.FromLevel}–{p.ToLevel}", 12, mono: true));
            var mid = Text($"{p.Layout} · {L.Format("W6PackageCounts", p.Cores, p.Stairs, p.Lifts)}", 11, secondary: true);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);
            var area = Text(p.FloorArea.ToString("N0", c) + " м²", 11, mono: true);
            Grid.SetColumn(area, 2);
            g.Children.Add(area);
            packages.Children.Add(g);
        }
        panel.Children.Add(PanelCard(packages));

        var entries = new StackPanel { Spacing = 5 };
        entries.Children.Add(Caption(s.HasBasement ? L.Get("W6BasementYes") : L.Get("W6BasementNo")));
        if (s.Entries.Count == 0) entries.Children.Add(Text(L.Get("W6NoEntries"), 11, secondary: true, wrap: true));
        foreach (var e in s.Entries)
        {
            var g = new Grid { ColumnSpacing = 6 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Hex(SiteBoundaryBuilder.StyleOf(e.Kind).ColorHex), VerticalAlignment = VerticalAlignment.Center });
            var t = Text(e.Title + (e.IntoBasement ? " → " + L.Get("W6IntoBasement") : " → " + L.Get("W6IntoFloor1")), 11, wrap: true);
            ToolTipService.SetToolTip(t, e.Rule);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var dist = Text(e.DistanceToNetwork.ToString("0.0", c) + " м", 11, mono: true);
            Grid.SetColumn(dist, 2);
            g.Children.Add(dist);
            entries.Children.Add(g);
        }
        panel.Children.Add(PanelCard(entries));

        var checks = new StackPanel { Spacing = 5 };
        checks.Children.Add(Caption(L.Get("W6ZoneChecks")));
        foreach (var k in s.Checks)
        {
            var r = new StackPanel { Spacing = 1 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(Glyph(k.Ok ? "\uE73E" : "\uE7BA", 11, k.Ok ? "AccentBrush" : "TextSecondaryBrush"));
            head.Children.Add(Text(k.Title, 11.5, strong: true));
            r.Children.Add(head);
            r.Children.Add(Text(k.Detail, 10.5, secondary: true, wrap: true));
            checks.Children.Add(r);
        }
        panel.Children.Add(PanelCard(checks));
        panel.Children.Add(Text(L.Get("W6Wave7Note"), 10.5, secondary: true, wrap: true));
    }

    // ═════════════════════════════ Окно подтверждения ограничений ═════════════════════════════

    private async Task<ConstraintVerificationDialog?> ShowConstraintDialogAsync(ConstraintDialogMode mode, Func<ConstraintVerificationDialog, Task>? debug = null)
    {
        if (AnyDialogOpen || _gpzuOpen)
        {
            // Одно окно за раз: подтверждение откроется после закрытия текущего (например, студии ГПЗУ).
            _constraintsPending = mode;
            return null;
        }
        _wave6DialogOpen = true;
        try
        {
            var dialog = new ConstraintVerificationDialog(ViewModel, mode) { XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme };
            LastConstraintDialog = dialog;
            if (debug is not null) dialog.Opened += async (_, _) => await debug(dialog);
            await dialog.ShowAsync();
            return dialog;
        }
        finally
        {
            _wave6DialogOpen = false;
            QueueWave6Refresh();
        }
    }

    /// <summary>После закрытия другого окна — отложенное окно подтверждения ограничений.</summary>
    private void ShowPendingConstraints()
    {
        if (_constraintsPending is not { } mode) return;
        _constraintsPending = null;
        DispatcherQueue.TryEnqueue(async () => await ShowConstraintDialogAsync(mode));
    }

    // ═════════════════════════════ Защита выхода ═════════════════════════════

    /// <summary>
    /// Крестик окна или Alt+F4: закрытие перехватывается (args.Cancel), пока пользователь не выберет «Сохранить и выйти»,
    /// «Сохранить копию и выйти», «Выйти без сохранения» или «Отмена». Пустой проект без изменений закрывается сразу.
    /// </summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!EvaluateCloseRequest()) return;
        args.Cancel = true;
        if (AnyDialogOpen || _gpzuOpen) return;
        var decision = await AskExitAsync();
        await ExecuteExitDecisionAsync(decision);
    }

#if DEBUG
    /// <summary>Только автотест: вернуть перехват выхода после проверки «Выйти без сохранения» без закрытия окна.</summary>
    internal void DebugResetExitGuard() => _exitConfirmed = false;
#endif

    /// <summary>true — закрытие нужно перехватить и спросить (есть проект и выход ещё не подтверждён).</summary>
    internal bool EvaluateCloseRequest() => !_exitConfirmed && ViewModel.ShouldConfirmExit;

    internal async Task<bool> ExecuteExitDecisionAsync(ExitDecision decision, bool closeWindow = true)
    {
        switch (decision)
        {
            case ExitDecision.Cancel:
                return false;
            case ExitDecision.Save:
            {
                string? path = ViewModel.Scene.ProjectFilePath ?? await ViewModel.ProjectFiles.PickSavePathAsync(this);
                if (path is null) return false;
                await ViewModel.SaveProjectAsync(path);
                if (ViewModel.Scene.IsDirty) return false;
                AutoSaveService.Discard();
                break;
            }
            case ExitDecision.SaveCopy:
            {
                var picker = InitPicker(new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = ProjectFileService.SafeFileName(ViewModel.Scene.ProjectName + " — копия")
                });
                picker.FileTypeChoices.Add("Architect Studio Pro", [Models.SceneProjectFile.Extension]);
                if (await picker.PickSaveFileAsync() is not { } file) return false;
                await ViewModel.SaveProjectAsync(file.Path);
                AutoSaveService.Discard();
                break;
            }
            case ExitDecision.Discard:
                // Последнее автосохранение остаётся страховкой: при следующем запуске сессию можно восстановить.
                break;
        }
        _exitConfirmed = true;
        if (closeWindow) Close();
        return true;
    }

    /// <summary>Окно «Сохранить прогресс проекта перед выходом из Architect Studio Pro?» (Liquid Glass, 4 действия).</summary>
    private async Task<ExitDecision> AskExitAsync()
    {
        var result = ExitDecision.Cancel;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = DialogTheme,
            Background = GlassRes("LiquidGlassSurfaceBrush", "IslandBrush"),
            BorderBrush = GlassRes("LiquidGlassSpecularBorderBrush", "StudioBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16)
        };
        var root = new StackPanel { Spacing = 14, Width = 560 };
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(GlyphBadge("\uE74E", 34));
        var titles = new StackPanel { Spacing = 3 };
        titles.Children.Add(Text(L.Get("W6ExitTitle"), 16, strong: true, wrap: true));
        string when = ViewModel.AutoSave?.LastSaved is { } t ? L.Format("W6ExitAutosave", t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)) : L.Get("W6ExitNoAutosave");
        titles.Children.Add(Text(L.Format("W6ExitHint", ViewModel.Scene.ProjectName, ViewModel.Scene.Elements.Count) + " " + when, 11.5, secondary: true, wrap: true));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        root.Children.Add(head);
        var buttons = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < 4; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Add(int col, string glyph, string text, ExitDecision d, bool accent = false)
        {
            var b = ActionButton(glyph, text, (_, _) => { result = d; dialog.Hide(); }, accent: accent);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (accent)
            {
                b.Background = Res("AccentBrush");
                b.Foreground = Res("OnAccentBrush");
            }
            Grid.SetColumn(b, col);
            buttons.Children.Add(b);
        }
        Add(0, "\uE74E", L.Get("W6ExitSave"), ExitDecision.Save, accent: true);
        Add(1, "\uE8C8", L.Get("W6ExitSaveCopy"), ExitDecision.SaveCopy);
        Add(2, "\uE7E8", L.Get("W6ExitDiscard"), ExitDecision.Discard);
        Add(3, "\uE7A7", L.Get("W6ExitCancel"), ExitDecision.Cancel);
        root.Children.Add(buttons);
        dialog.Content = root;
        _wave6DialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _wave6DialogOpen = false;
        }
        return result;
    }
}
