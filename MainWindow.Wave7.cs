using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Models.Layouts;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.Services.Layouts;
using AIArchitectStudio.ViewModels;
using AIArchitectStudio.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio;

/// <summary>
/// Волна 7 в главном окне: три раздела с одним правым доком 340 px (1 — Паспорт ЗУ · Регламент и ЗОУИТ · Сети ИГДИ; 2 — инспектор
/// или панель КЖ/КМ; 3 — Пакеты этажей · Квартиры и МОП · Инженерные стояки), левая палитра раздела, мульти-план и подвал в главной
/// области, одна кнопка режима ассистента вместо россыпи чипов (состав бюро, протокол, аудит, журнал, вложения — в её меню).
/// </summary>
public sealed partial class MainWindow
{
    private FloorTierPlanView? _planView;

    /// <summary>Представление мульти-плана (автотест и съёмка).</summary>
    internal FloorTierPlanView? PlanView => _planView;

    private void InitializeWave7()
    {
        _planView = new FloorTierPlanView();
        FloorPlanHost.Child = _planView;
        FloorPlanHost.Background = GlassRes("LiquidGlassSurfaceBrush", "IslandBrush");
        FloorPlanHost.BorderBrush = GlassRes("LiquidGlassSpecularBorderBrush", "StudioBorderBrush");
        BuildSectionPalettes();
        ViewModel.LayoutChanged += (_, _) => { RefreshPlanView(); QueueWave6Refresh(); };
        ViewModel.EnvironmentChanged += (_, _) => UpdateSectionChrome();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsMultiPlanVisible)) RefreshPlanView();
            if (e.PropertyName is nameof(MainViewModel.IsInspectorDockVisible) or nameof(MainViewModel.IsEnvironmentPanelVisible)) UpdateAiDockMargin();
        };
        L.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LocalizationService.Language)) return;
            BuildSectionPalettes();
            QueueWave6Refresh();
        };
        UpdateSectionChrome();
#if DEBUG
        InitializeWave7Debug();
#endif
    }

    /// <summary>Кнопки, которые живут в коде (фото в верхнем баре), — по разделу; мульти-план — по планировке.</summary>
    private void UpdateSectionChrome()
    {
        if (_photoTopButton is not null) _photoTopButton.Visibility = ViewModel.IsEnvBuilding ? Visibility.Visible : Visibility.Collapsed;
        RefreshPlanView();
        UpdateAiDockMargin();
    }

    private void RefreshPlanView()
    {
        if (_planView is null || !ViewModel.IsMultiPlanVisible) return;
        _planView.Show(ViewModel.Scene.BuildingLayout, ViewModel.StackReport ?? ViewModel.RefreshStackReport(), ViewModel.MultiPlanCount, ViewModel.LayoutView == LayoutViewKind.Basement);
    }

    // ═════════════════════════════ Левые палитры разделов 1 и 3 ═════════════════════════════

    private Button PaletteButton(string glyph, string tip, Action click)
    {
        var b = new Button { Style = StyleRes("IconButtonStyle"), Content = new FontIcon { Glyph = glyph, FontSize = 14 } };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private ToggleButton PaletteToggle(string glyph, string tip, bool on, Action<bool> changed)
    {
        var b = new ToggleButton { Style = StyleRes("IconToggleStyle"), Content = new FontIcon { Glyph = glyph, FontSize = 14 }, IsChecked = on };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => changed(b.IsChecked == true);
        return b;
    }

    private static Border PaletteDivider() => new() { Height = 1, Margin = new Thickness(6, 2, 6, 2), Background = Res("StudioBorderBrush") };

    private void BuildSectionPalettes()
    {
        var vm = ViewModel;
        // Раздел 1: только инструменты участка.
        SitePalette.Children.Clear();
        SitePalette.Children.Add(PaletteButton("\uE8E5", L.Get("W7ToolImportGpzu"), async () => await ShowGpzuStudioAsync()));
        SitePalette.Children.Add(PaletteToggle("\uE70F", L.Get("W7ToolVertices"), vm.IsSiteVertexEditEnabled, on => vm.IsSiteVertexEditEnabled = on));
        SitePalette.Children.Add(PaletteToggle("\uE81E", L.Get("W7ToolZones"), true, on =>
        {
            if (vm.Scene.SitePlan is not { } plan) return;
            foreach (var z in plan.Zones.Where(z => !z.Zone.IsNetwork && z.Zone.Kind != GpzuZoneKind.AllowedArea)) vm.SetSiteLayerVisible(z.Id, on);
        }));
        SitePalette.Children.Add(PaletteButton("\uE909", L.Get("W7ToolRelief"), () => vm.SiteDockTab = 2));
        var place = PaletteButton("\uE80F", L.Get("W7ToolPlace"), () => { });
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        foreach (var kind in new[] { SiteStructureKind.ResidentialSection, SiteStructureKind.HighRise, SiteStructureKind.Tower, SiteStructureKind.TransformerSubstation, SiteStructureKind.Parking })
        {
            var item = new MenuFlyoutItem { Text = EditableSiteTerrainService.StructureTitle(kind) };
            item.Click += (_, _) => vm.AddStructureToSite(kind, kind == SiteStructureKind.HighRise ? 50 : 0);
            menu.Items.Add(item);
        }
        place.Flyout = menu;
        SitePalette.Children.Add(place);
        SitePalette.Children.Add(PaletteDivider());
        SitePalette.Children.Add(PaletteButton("\uE749", L.Get("W7ToolGpzuSheet"), async () => await ExportGpzuSheetAsync()));

        // Раздел 3: пакеты, мульти-план, ядро, квартирография, подвал, контроль зон.
        LayoutPalette.Children.Clear();
        LayoutPalette.Children.Add(PaletteButton("\uE8A1", L.Get("W7ToolPackages"), () => { EnsureLayout(); vm.LayoutDockTab = 0; }));
        LayoutPalette.Children.Add(PaletteButton("\uF0E2", L.Get("W7ToolMultiPlan"), () => vm.ToggleMultiPlanCommand.Execute(null)));
        var core = PaletteButton("\uE8B9", L.Get("W7ToolCore"), () => { });
        var coreMenu = new MenuFlyout { Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        foreach (var form in Enum.GetValues<CoreStairForm>())
        {
            var item = new MenuFlyoutItem { Text = L.Get("W7StairForm") + ": " + LayoutRooms.StairTitle(form) };
            item.Click += (_, _) => { vm.LayoutStairForm = form; vm.GenerateBuildingLayout(); };
            coreMenu.Items.Add(item);
        }
        core.Flyout = coreMenu;
        LayoutPalette.Children.Add(core);
        LayoutPalette.Children.Add(PaletteButton("\uE80F", L.Get("W7ToolApartments"), () => { EnsureLayout(); vm.LayoutDockTab = 1; }));
        LayoutPalette.Children.Add(PaletteDivider());
        LayoutPalette.Children.Add(PaletteButton("\uE7C0", L.Get("W7ToolBasement"), () => vm.ShowBasementViewCommand.Execute(null)));
        LayoutPalette.Children.Add(PaletteButton("\uE9D9", L.Get("W7ToolStacks"), () => { EnsureLayout(); vm.RefreshStackReport(); vm.LayoutDockTab = 2; vm.ShowBasementViewCommand.Execute(null); }));
    }

    private void EnsureLayout()
    {
        if (ViewModel.Scene.BuildingLayout is null) ViewModel.GenerateBuildingLayout();
    }

    private async Task ExportGpzuSheetAsync()
    {
        if (ViewModel.Scene.SitePlan is not { } plan)
        {
            await ShowGpzuStudioAsync();
            return;
        }
        var picker = InitPicker(new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "ГПЗУ " + plan.Document.Cadastral.Replace(':', '-') });
        picker.FileTypeChoices.Add("PDF", [".pdf"]);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        var report = ViewModel.RefreshSiteMetrics()?.Compliance;
        var res = await Task.Run(() => GostDrawingPdfExporter.ExportGpzuSheet(plan, report, file.Path, ViewModel.Scene.Elements.ToList()));
        ViewModel.ChatMessages.Add(Models.ChatMessageModel.System(res.Summary));
    }

    // ═════════════════════════════ Правый док разделов 1 и 3 ═════════════════════════════

    private void BuildSectionDock()
    {
        var vm = ViewModel;
        EnvironmentPanelTabs.Children.Clear();
        EnvironmentPanelContent.Children.Clear();
        if (vm.ActiveEnvironment == DeveloperEnvironment.Building) return;
        bool site = vm.ActiveEnvironment == DeveloperEnvironment.Site;
        var titles = site
            ? new[] { L.Get("W7TabPassport"), L.Get("W7TabRegulation"), L.Get("W7TabNetworks") }
            : new[] { L.Get("W7TabPackages"), L.Get("W7TabApartments"), L.Get("W7TabRisers") };
        int current = site ? vm.SiteDockTab : vm.LayoutDockTab;
        var track = new Grid();
        for (int i = 0; i < 3; i++)
        {
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rb = new RadioButton
            {
                Content = titles[i], Tag = i, GroupName = site ? "SiteDockTab" : "LayoutDockTab", Style = StyleRes("SegmentRadioStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 10.5, Padding = new Thickness(0), IsChecked = i == current
            };
            rb.Checked += (s, _) =>
            {
                int tab = (int)((RadioButton)s).Tag;
                if (site) vm.SiteDockTab = tab;
                else vm.LayoutDockTab = tab;
            };
            Grid.SetColumn(rb, i);
            track.Children.Add(rb);
        }
        EnvironmentPanelTabs.Children.Add(new Border { Style = StyleRes("SegmentTrackStyle"), Child = track });
        var panel = EnvironmentPanelContent;
        if (site)
        {
            if (vm.Scene.SitePlan is not { } plan) { panel.Children.Add(NoSiteCard()); return; }
            switch (vm.SiteDockTab)
            {
                case 1: BuildRegulationTab(panel, plan); break;
                case 2: BuildNetworksTab(panel, plan); break;
                default: BuildPassportTab(panel, plan); break;
            }
        }
        else
        {
            switch (vm.LayoutDockTab)
            {
                case 1: BuildApartmentsTab(panel); break;
                case 2: BuildRisersTab(panel); break;
                default: BuildPackagesTab(panel); break;
            }
        }
    }

    private Border NoSiteCard()
    {
        var empty = new StackPanel { Spacing = 8 };
        empty.Children.Add(Text(L.Get("W6NoSiteTitle"), 13, strong: true));
        empty.Children.Add(Text(L.Get("W6NoSiteHint"), 11, secondary: true, wrap: true));
        empty.Children.Add(StretchButton(AccentButton("\uE8E5", L.Get("W7ToolImportGpzu"), async (_, _) => await ShowGpzuStudioAsync())));
        empty.Children.Add(StretchButton(ActionButton("\uE8B7", L.Get("W6DemoSite"), async (_, _) => await ViewModel.LoadDemoSiteAsync())));
        return PanelCard(empty);
    }

    private static Button AccentButton(string glyph, string text, RoutedEventHandler click)
    {
        var b = ActionButton(glyph, text, click, accent: true);
        b.Background = Res("AccentBrush");
        b.Foreground = Res("OnAccentBrush");
        return b;
    }

    // ── Раздел 1 · Паспорт ЗУ ──

    private void BuildPassportTab(StackPanel panel, GpzuSitePlan plan)
    {
        var vm = ViewModel;
        var c = L.Culture;
        var doc = plan.Document;
        var m = vm.SiteMetrics ?? vm.RefreshSiteMetrics();
        var head = new StackPanel { Spacing = 6 };
        head.Children.Add(Caption(L.Get("W6SiteLive")));
        head.Children.Add(Text(doc.Cadastral, 15, strong: true, mono: true));
        if (doc.Address.Length > 0) head.Children.Add(Text(doc.Address, 11, secondary: true, wrap: true));
        head.Children.Add(Rows(
        [
            (L.Get("W7AreaDoc"), doc.DeclaredArea.ToString("N2", c), "м²"),
            (L.Get("W6GaussArea"), (m?.GaussArea ?? plan.Area).ToString("N2", c), "м²"),
            (L.Get("W7AreaDelta"), ((m?.GaussArea ?? plan.Area) - doc.DeclaredArea).ToString("+0.00;-0.00;0.00", c), "м²"),
            (L.Get("W6Perimeter"), (m?.Perimeter ?? 0).ToString("N2", c), "м"),
            (L.Get("W7Points"), plan.Boundary.Count.ToString(c), string.Empty)
        ]));
        panel.Children.Add(PanelCard(head));
        panel.Children.Add(PointEditorCard(plan));

        // Каталог координат поворотных точек (МСК) и длины сторон — моноширинно, по правому краю.
        var cat = new StackPanel { Spacing = 4 };
        cat.Children.Add(Caption(L.Format("W7Catalog", doc.CoordinateSystem)));
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        void Cell(string text, int row, int col, bool head = false, bool right = true)
        {
            var t = Text(text, head ? 10 : 11, secondary: head, mono: !head);
            t.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell("№", 0, 0, true, false); Cell("X, м", 0, 1, true); Cell("Y, м", 0, 2, true); Cell("L, м", 0, 3, true);
        for (int i = 0; i < plan.Boundary.Count && i < 60; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (gx, gy) = plan.ToGeo(plan.Boundary[i].X, plan.Boundary[i].Z);
            var seg = plan.Segments.Count > i ? plan.Segments[i].Length : 0;
            Cell(plan.PointNumbers[i], i + 1, 0, right: false);
            Cell(gx.ToString("N2", c), i + 1, 1);
            Cell(gy.ToString("N2", c), i + 1, 2);
            Cell(seg.ToString("0.00", c), i + 1, 3);
        }
        cat.Children.Add(grid);
        panel.Children.Add(PanelCard(cat));
    }

    /// <summary>Выбранная поворотная точка: X/Y МСК и Z БС с клавиатуры (каждое «Применить» — шаг журнала).</summary>
    private Border PointEditorCard(GpzuSitePlan plan)
    {
        var vm = ViewModel;
        var c = L.Culture;
        var point = new StackPanel { Spacing = 6 };
        if (vm.SelectedSiteVertex is { } sel && EditableSiteTerrainService.PointOf(plan, sel) is { } sp)
        {
            var (gx, gy) = plan.ToGeo(sp.X, sp.Z);
            point.Children.Add(Caption(sel.Layer == SiteVertexLayer.Boundary ? L.Format("W6PointN", plan.PointNumbers[sel.Index]) : L.Format("W6ZoneVertexN", sel.Index + 1)));
            var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
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
            var xBox = Field("X, м", gx, 0);
            var yBox = Field("Y, м", gy, 1);
            TextBox? zBox = sel.Layer == SiteVertexLayer.Boundary ? Field("Z БС, м", EditableSiteTerrainService.EffectiveVertexElevation(plan, sel.Index), 2) : null;
            point.Children.Add(grid);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(AccentButton("\uE73E", L.Get("W6Apply"), (_, _) =>
            {
                if (double.TryParse(xBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(yBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                    (Math.Abs(x - gx) > 0.004 || Math.Abs(y - gy) > 0.004))
                    vm.MoveSiteVertexGeo(sel, x, y);
                if (zBox is not null && double.TryParse(zBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double zb) &&
                    Math.Abs(zb - EditableSiteTerrainService.EffectiveVertexElevation(plan, sel.Index)) > 0.004)
                    vm.SetSiteVertexElevation(sel.Index, zb);
            }));
            buttons.Children.Add(ActionButton("\uE710", L.Get("W6InsertAfter"), (_, _) => vm.InsertSiteVertex(sel)));
            buttons.Children.Add(ActionButton("\uE74D", string.Empty, (_, _) => vm.RemoveSiteVertex(sel), tip: L.Get("W6RemovePoint")));
            point.Children.Add(buttons);
        }
        else
        {
            point.Children.Add(Caption(L.Get("W6PointCaption")));
            point.Children.Add(Text(L.Get("W6PointHint"), 11, secondary: true, wrap: true));
        }
        return PanelCard(point);
    }

    // ── Раздел 1 · Регламент и ЗОУИТ ──

    private void BuildRegulationTab(StackPanel panel, GpzuSitePlan plan)
    {
        var vm = ViewModel;
        var c = L.Culture;
        var reg = plan.Document.Regulation;
        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(Caption(L.Get("W7Regulation")));
        card.Children.Add(Text($"{reg.ZoneCode} {reg.ZoneName}".Trim() + (reg.Subzone.Length > 0 ? " · " + L.Format("W7Subzone", reg.Subzone) : string.Empty), 12.5, strong: true, wrap: true));
        var rows = new List<(string, string, string)>();
        if (reg.MaxLivingArea is { } la) rows.Add((L.Get("W7LivingLimit"), "≤ " + la.ToString("N0", c), "м²"));
        if (reg.MinParkingPlaces is { } pk) rows.Add((L.Get("W7ParkingLimit"), "≥ " + pk.ToString(c), "м/м"));
        if (reg.MinParcelArea is { } ma) rows.Add((L.Get("W7MinArea"), ma.ToString("N0", c), "м²"));
        if (plan.Document.CeilingAbs is { } ceil)
        {
            rows.Add((L.Get("W7HeightLimit"), ceil.ToString("0.00", c), "м БС"));
            rows.Add((L.Get("W7HeightOverGround"), (ceil - plan.GroundElevation).ToString("0.00", c), "м"));
        }
        if (rows.Count > 0) card.Children.Add(Rows(rows));
        panel.Children.Add(PanelCard(card));

        // ЗОУИТ и подзоны ПАТ: цвет, номер, отметка; переключатель слоя.
        var zones = new StackPanel { Spacing = 5 };
        zones.Children.Add(Caption(L.Get("W7Zones")));
        foreach (var z in plan.Zones.Where(z => !z.Zone.IsNetwork && z.Zone.Kind is not GpzuZoneKind.AllowedArea).Take(24))
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = Hex(z.Zone.ColorHex), VerticalAlignment = VerticalAlignment.Center });
            var t = new StackPanel { Spacing = 0 };
            t.Children.Add(Text(z.Zone.Code.Length > 0 ? z.Zone.Code : L.Format("W7Explication", z.Zone.ExplicationNo), 11, mono: z.Zone.Code.Length > 0));
            var name = Text(z.Zone.Name + (z.Zone.HeightLimitMin is { } h ? " · Hабс " + h.ToString("0.00", c) : string.Empty), 10.5, secondary: true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            t.Children.Add(name);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var toggle = new ToggleSwitch { IsOn = vm.IsSiteLayerVisible(z.Id), OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            string id = z.Id;
            toggle.Toggled += (_, _) => vm.SetSiteLayerVisible(id, toggle.IsOn);
            Grid.SetColumn(toggle, 2);
            g.Children.Add(toggle);
            zones.Children.Add(g);
        }
        panel.Children.Add(PanelCard(zones));
        panel.Children.Add(ConstraintsCard());

        if (vm.SiteMetrics is { } m)
        {
            var findings = new StackPanel { Spacing = 4 };
            findings.Children.Add(Caption(L.Get("W6Compliance")));
            var verdict = Text(m.Compliance.Passed ? L.Get("W6PlacementOk") : L.Format("W6PlacementBad", m.Compliance.Critical), 12, strong: true);
            verdict.Foreground = Hex(m.Compliance.Passed ? "#10B981" : "#EF4444");
            findings.Children.Add(verdict);
            foreach (var f in m.Compliance.Findings.Where(f => f.Severity != GpzuSeverity.Info).Take(8))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(Glyph(f.Severity == GpzuSeverity.Ok ? "\uE73E" : "\uE711", 11, f.Severity == GpzuSeverity.Ok ? "AccentBrush" : "TextSecondaryBrush"));
                var t = Text(f.Title, 11, wrap: true);
                t.MaxWidth = 270;
                if (f.Severity == GpzuSeverity.Critical) t.Foreground = Hex("#EF4444");
                row.Children.Add(t);
                findings.Children.Add(row);
            }
            panel.Children.Add(PanelCard(findings));
        }
    }

    /// <summary>Карточка «Градостроительные ограничения и ЗОУИТ»: действующие пункты, анализ документа, ведомость, нормативы.</summary>
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
        stack.Children.Add(StretchButton(AccentButton("\uE8A5", L.Get("W6ReadDocument"), async (_, _) => await PickConstraintDocumentAsync())));
        var two = new Grid { ColumnSpacing = 6 };
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var review = StretchButton(ActionButton("\uE9D5", L.Get("W6ReviewConstraints"), (_, _) => vm.OpenConstraintReviewCommand.Execute(null)));
        two.Children.Add(review);
        var web = StretchButton(ActionButton("\uE721", L.Get("W6FindWeb"), async (_, _) => await vm.SearchConstraintsAsync(L.Get("W6DefaultQuery"))));
        Grid.SetColumn(web, 1);
        two.Children.Add(web);
        stack.Children.Add(two);
        return PanelCard(stack);
    }

    private async Task PickConstraintDocumentAsync()
    {
        var picker = InitPicker(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary });
        foreach (var ext in new[] { ".pdf", ".docx", ".txt", ".xml", ".json", ".csv" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is { } file) await ViewModel.AnalyzeDocumentAsync(file.Path);
    }

    // ── Раздел 1 · Сети ИГДИ ──

    private void BuildNetworksTab(StackPanel panel, GpzuSitePlan plan)
    {
        var vm = ViewModel;
        var c = L.Culture;
        var nets = new StackPanel { Spacing = 5 };
        nets.Children.Add(Caption(L.Get("W7Networks")));
        var list = plan.Zones.Where(z => z.Zone.IsNetwork).ToList();
        if (list.Count == 0) nets.Children.Add(Text(L.Get("W6NoEntries"), 11, secondary: true, wrap: true));
        foreach (var z in list)
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = Hex(z.Zone.ColorHex), VerticalAlignment = VerticalAlignment.Center });
            var t = new StackPanel { Spacing = 0 };
            t.Children.Add(Text(SiteBoundaryBuilder.StyleOf(z.Zone.Kind).Title, 11.5));
            var norm = Text(z.Zone.Normative, 10, secondary: true);
            norm.TextTrimming = TextTrimming.CharacterEllipsis;
            t.Children.Add(norm);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var w = Text("± " + z.Zone.CorridorHalfWidth.ToString("0.0", c) + " м", 11, mono: true);
            Grid.SetColumn(w, 2);
            g.Children.Add(w);
            nets.Children.Add(g);
        }
        panel.Children.Add(PanelCard(nets));
        var m = vm.SiteMetrics ?? vm.RefreshSiteMetrics();
        if (m is not null)
        {
            var relief = new StackPanel { Spacing = 6 };
            relief.Children.Add(Caption(L.Get("W7ReliefCard")));
            relief.Children.Add(Rows(
            [
                (L.Get("W7Ground"), plan.GroundElevation.ToString("0.00", c), "м БС"),
                (L.Get("W6Relief"), $"{m.MinRelief.ToString("+0.00;-0.00;0.00", c)}…{m.MaxRelief.ToString("+0.00;-0.00;0.00", c)}", "м"),
                (L.Get("W7Samples"), plan.Relief.Count.ToString(c), string.Empty),
                (L.Get("W6Cut"), m.Earthworks.CutM3.ToString("N1", c), "м³"),
                (L.Get("W6Fill"), m.Earthworks.FillM3.ToString("N1", c), "м³"),
                (L.Get("W7Balance"), (m.Earthworks.FillM3 - m.Earthworks.CutM3).ToString("+0.0;-0.0;0.0", c), "м³"),
                (L.Get("W6Buildings"), m.Buildings.ToString(c), string.Empty)
            ]));
            panel.Children.Add(PanelCard(relief));
        }
        var place = new StackPanel { Spacing = 6 };
        place.Children.Add(Caption(L.Get("W6CallBuilding")));
        place.Children.Add(Text(L.Get("W6CallHintSite"), 11, secondary: true, wrap: true));
        foreach (var (kind, glyph) in new[] { (SiteStructureKind.ResidentialSection, "\uE80F"), (SiteStructureKind.HighRise, "\uE8A1"), (SiteStructureKind.TransformerSubstation, "\uE945"), (SiteStructureKind.Parking, "\uE804") })
            place.Children.Add(StretchButton(ActionButton(glyph, EditableSiteTerrainService.StructureTitle(kind), (_, _) => vm.AddStructureToSite(kind, kind == SiteStructureKind.HighRise ? 50 : 0))));
        panel.Children.Add(PanelCard(place));
    }

    // ── Раздел 3 · Пакеты этажей ──

    private void BuildPackagesTab(StackPanel panel)
    {
        var vm = ViewModel;
        var c = L.Culture;
        if (vm.Scene.BuildingLayout is not { } plan)
        {
            var empty = new StackPanel { Spacing = 8 };
            empty.Children.Add(Text(L.Get("W7NoLayoutTitle"), 13, strong: true));
            empty.Children.Add(Text(L.Get("W7NoLayoutHint"), 11, secondary: true, wrap: true));
            empty.Children.Add(StretchButton(AccentButton("\uE8A1", L.Get("W7Generate"), (_, _) => vm.GenerateBuildingLayout())));
            empty.Children.Add(StretchButton(ActionButton("\uE80F", L.Get("W7PlaceHighRise"), (_, _) => vm.PlaceHighRiseWithLayout())));
            panel.Children.Add(PanelCard(empty));
            return;
        }
        var head = new StackPanel { Spacing = 6 };
        head.Children.Add(Caption(plan.BuildingName));
        head.Children.Add(Rows(
        [
            (L.Get("W7Floors"), plan.Levels.ToString(c), "эт."),
            (L.Get("W7Footprint"), $"{plan.Length.ToString("0.0", c)} × {plan.Width.ToString("0.0", c)}", "м"),
            (L.Get("W7Lifts"), $"{plan.Core.Lifts} ({plan.Core.FireLifts})", string.Empty),
            (L.Get("W7Stairs"), $"{plan.Core.Stairs} · {plan.Core.StairCategory}", string.Empty)
        ]));
        var sizeRow = new Grid { ColumnSpacing = 8 };
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        sizeRow.Children.Add(Text(L.Get("W6PackageSize"), 11, secondary: true));
        var size = new NumberBox { Value = vm.FloorPackageSize, Minimum = 1, Maximum = 50, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, FontSize = 12 };
        size.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue) && (int)e.NewValue != vm.FloorPackageSize) { vm.FloorPackageSize = (int)e.NewValue; vm.GenerateBuildingLayout(); } };
        Grid.SetColumn(size, 1);
        sizeRow.Children.Add(size);
        head.Children.Add(sizeRow);
        panel.Children.Add(PanelCard(head));

        var list = new StackPanel { Spacing = 3 };
        list.Children.Add(Caption(L.Get("W6Packages")));
        foreach (var p in plan.Packages)
        {
            var tep = BuildingLayoutPlan.TepOf(p);
            var g = new Grid { ColumnSpacing = 6 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Text(p.IsBasement ? L.Get("W7Basement") : $"{p.FromLevel}–{p.ToLevel}", 11.5, mono: !p.IsBasement));
            var mid = Text(p.IsBasement ? p.Elevation.ToString("+0.000;-0.000", c) : $"{p.Elevation.ToString("+0.000;-0.000", c)} · {L.Get("W7Type")} {p.LayoutCode} · {tep.Apartments} кв.", 10.5, secondary: true);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);
            var area = Text(p.IsBasement ? "—" : tep.ApartmentArea.ToString("N0", c) + " м²", 11, mono: true);
            area.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(area, 2);
            g.Children.Add(area);
            if (!p.IsBasement)
            {
                var more = new Button { Style = StyleRes("IconButtonStyle"), Content = new FontIcon { Glyph = "\uE712", FontSize = 11 }, Height = 24, MinWidth = 24 };
                ToolTipService.SetToolTip(more, L.Get("W7PackageEdit"));
                var menu = new MenuFlyout();
                foreach (var (text, action) in new[] { (L.Get("W7TypeA"), "А"), (L.Get("W7TypeB"), "Б"), (L.Get("W7Mirror"), "mirror") })
                {
                    var item = new MenuFlyoutItem { Text = text };
                    string id = p.Id;
                    item.Click += (_, _) => vm.EditLayoutPackage(id, action);
                    menu.Items.Add(item);
                }
                more.Flyout = menu;
                Grid.SetColumn(more, 3);
                g.Children.Add(more);
            }
            list.Children.Add(g);
        }
        panel.Children.Add(PanelCard(list));

        var view = new StackPanel { Spacing = 6 };
        view.Children.Add(Caption(L.Get("W7ToolMultiPlan")));
        view.Children.Add(Segments([("2", (object)2), ("3", 3), ("4", 4)], vm.MultiPlanCount, v => vm.MultiPlanCount = (int)v, "MultiPlanCount"));
        var buttons = new StackPanel { Spacing = 6 };
        var multi = StretchButton(vm.IsMultiPlanVisible && vm.LayoutView == LayoutViewKind.MultiPlan
            ? ActionButton("\uE711", L.Get("W7ClosePlans"), (_, _) => vm.IsMultiPlanOpen = false)
            : AccentButton("\uF0E2", L.Get("W7OpenPlans"), (_, _) => { vm.LayoutView = LayoutViewKind.MultiPlan; vm.IsMultiPlanOpen = true; }));
        buttons.Children.Add(multi);
        buttons.Children.Add(StretchButton(ActionButton("\uE7C0", L.Get("W7ToolBasement"), (_, _) => vm.ShowBasementViewCommand.Execute(null))));
        view.Children.Add(buttons);
        panel.Children.Add(PanelCard(view));
    }

    // ── Раздел 3 · Квартиры и МОП ──

    private void BuildApartmentsTab(StackPanel panel)
    {
        var vm = ViewModel;
        var c = L.Culture;
        var mix = vm.LayoutMix;
        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(Caption(L.Get("W7Mix")));
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        var values = new Dictionary<string, double> { ["studio"] = mix.Studio, ["one"] = mix.OneRoom, ["two"] = mix.TwoRoom, ["three"] = mix.ThreeRoom, ["euro"] = mix.Euro };
        int row = 0;
        foreach (var (key, title) in new[] { ("studio", L.Get("W7MixStudio")), ("one", L.Get("W7MixOne")), ("two", L.Get("W7MixTwo")), ("three", L.Get("W7MixThree")), ("euro", L.Get("W7MixEuro")) })
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = Text(title, 11, secondary: true);
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(t, row);
            grid.Children.Add(t);
            var box = new NumberBox { Value = Math.Round(values[key] * 100), Minimum = 0, Maximum = 100, SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, FontSize = 12 };
            string k = key;
            box.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) values[k] = e.NewValue / 100; };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            row++;
        }
        card.Children.Add(grid);
        card.Children.Add(StretchButton(AccentButton("\uE8A1", L.Get("W7Generate"), (_, _) =>
        {
            vm.LayoutMix = vm.LayoutMix with { Studio = values["studio"], OneRoom = values["one"], TwoRoom = values["two"], ThreeRoom = values["three"], Euro = values["euro"] };
            vm.GenerateBuildingLayout();
        })));
        panel.Children.Add(PanelCard(card));

        if (vm.Scene.BuildingLayout is not { } plan) return;
        var tep = plan.Tep();
        var sum = new StackPanel { Spacing = 6 };
        sum.Children.Add(Caption(L.Get("W7Tep")));
        sum.Children.Add(Rows(
        [
            (L.Get("W7Apartments"), tep.Apartments.ToString("N0", c), "кв."),
            (L.Get("W7ApartmentArea"), tep.ApartmentArea.ToString("N0", c), "м²"),
            (L.Get("W7LivingArea"), tep.LivingArea.ToString("N0", c), "м²"),
            (L.Get("W7CommonArea"), tep.CommonArea.ToString("N0", c), "м²"),
            (L.Get("W7K1"), tep.K1.ToString("0.00", c), string.Empty)
        ]));
        panel.Children.Add(PanelCard(sum));
        // Квартирография типового этажа: число квартир по типам.
        if (plan.Floors.FirstOrDefault() is { } first)
        {
            var types = new StackPanel { Spacing = 4 };
            types.Children.Add(Caption(L.Format("W7TypicalFloor", first.RangeText)));
            types.Children.Add(Rows(first.Apartments.GroupBy(a => a.Type).OrderBy(g => g.Key)
                .Select(g => (LayoutRooms.ApartmentTitle(g.Key), g.Count().ToString(c) + " · " + g.Average(a => a.TotalArea).ToString("0.0", c), "м²"))));
            panel.Children.Add(PanelCard(types));
        }
    }

    // ── Раздел 3 · Инженерные стояки ──

    private void BuildRisersTab(StackPanel panel)
    {
        var vm = ViewModel;
        var c = L.Culture;
        if (vm.Scene.BuildingLayout is not { } plan)
        {
            BuildPackagesTab(panel);
            return;
        }
        var report = vm.StackReport ?? vm.RefreshStackReport();
        var check = new StackPanel { Spacing = 6 };
        check.Children.Add(Caption(L.Get("W7StackCheck")));
        if (report is not null)
        {
            var verdict = Text(report.Passed ? L.Get("W7StackOk") : L.Format("W7StackBad", report.Critical), 12.5, strong: true);
            verdict.Foreground = Hex(report.Passed ? "#10B981" : "#EF4444");
            check.Children.Add(verdict);
            foreach (var (f, count) in FloorTierPlanView.Grouped(report.Findings.Where(f => f.Severity == StackSeverity.Critical)).Take(6))
            {
                var t = Text(f.Title + (count > 1 ? string.Create(c, $" · {count}") : string.Empty), 11, wrap: true);
                t.Foreground = Hex("#EF4444");
                check.Children.Add(t);
            }
            if (report.Passed)
                foreach (var f in report.Findings.Where(f => f.Severity == StackSeverity.Ok))
                    check.Children.Add(Text(f.Title, 11, secondary: true, wrap: true));
        }
        check.Children.Add(StretchButton(AccentButton("\uE9F5", L.Get("W7AlignStacks"), (_, _) => vm.AlignStacks())));
        check.Children.Add(StretchButton(ActionButton("\uE7C0", L.Get("W7ToolBasement"), (_, _) => vm.ShowBasementViewCommand.Execute(null))));
        panel.Children.Add(PanelCard(check));

        var risers = new StackPanel { Spacing = 4 };
        risers.Children.Add(Caption(L.Get("W7Risers")));
        var first = plan.Floors.FirstOrDefault();
        if (first is not null)
            risers.Children.Add(Rows(first.Risers.GroupBy(r => r.Kind).OrderBy(g => g.Key).Select(g => (LayoutRooms.RiserTitle(g.Key), g.Count().ToString(c), "шт."))));
        panel.Children.Add(PanelCard(risers));

        if (plan.Basement is { } b)
        {
            var bas = new StackPanel { Spacing = 4 };
            bas.Children.Add(Caption(L.Get("W7BasementRooms")));
            bas.Children.Add(Rows(b.Rooms.Where(r => r.Kind is LayoutRoomKind.Itp or LayoutRoomKind.WaterMeter or LayoutRoomKind.Electrical or LayoutRoomKind.Ventilation)
                .Select(r => (r.Name, r.Area.ToString("0.0", c), "м²"))));
            bas.Children.Add(Caption(L.Get("W7Inlets")));
            foreach (var i in plan.Inlets)
            {
                var g = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                g.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Hex(SiteBoundaryBuilder.StyleOf(i.Kind).ColorHex), VerticalAlignment = VerticalAlignment.Center });
                g.Children.Add(Text(i.Title + (i.IntoBasement ? string.Empty : " · " + L.Get("W6IntoFloor1")), 11));
                bas.Children.Add(g);
            }
            panel.Children.Add(PanelCard(bas));
        }
    }

    // ═════════════════════════════ Меню ассистента ═════════════════════════════

    /// <summary>
    /// Одна кнопка режима вместо чипов: «Проектировщик / Совет КБ / Агент», затем состав бюро, протокол обсуждения, аудит модели,
    /// журнал команд, основная локальная модель, веб-аккаунт, авто-снимок вида и вложение плана.
    /// </summary>
    private void OnAssistantFlyoutOpening(object? sender, object e)
    {
        var vm = ViewModel;
        var f = AssistantFlyout;
        f.Items.Clear();
        void Mode(string text, string glyph, bool on, Action set)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "AssistantMode", IsChecked = on, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => set();
            f.Items.Add(item);
        }
        Mode(L.Get("CouncilSingle"), "\uE8BD", vm.IsSingleAgentMode, () => vm.IsSingleAgentMode = true);
        Mode(L.Get("CouncilMulti"), "\uE716", vm.IsCouncilMode, () => vm.IsCouncilMode = true);
        Mode(L.Get("W3AgentModeShort"), "\uE90F", vm.IsAgentMode, () => vm.IsAgentMode = true);
        f.Items.Add(new MenuFlyoutSeparator());
        void Item(string text, string glyph, Action click)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => click();
            f.Items.Add(item);
        }
        Item(L.Get("CouncilTeam") + "…", "\uE902", () => CouncilTeamFlyout.ShowAt(AssistantButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft }));
        var timeline = new ToggleMenuFlyoutItem { Text = L.Get("CouncilTimeline"), IsChecked = vm.IsCouncilTimelineOpen, Icon = new FontIcon { Glyph = "\uE8F2" } };
        timeline.Click += (_, _) => vm.IsCouncilTimelineOpen = timeline.IsChecked;
        f.Items.Add(timeline);
        Item(L.Get("CouncilAudit"), "\uE721", () => vm.RunCouncilAuditCommand.Execute(null));
        f.Items.Add(new MenuFlyoutSeparator());
        var history = new ToggleMenuFlyoutItem { Text = vm.HistoryLabel, IsChecked = vm.IsHistoryOpen, Icon = new FontIcon { Glyph = "\uE81C" } };
        history.Click += (_, _) => vm.ToggleHistoryCommand.Execute(null);
        f.Items.Add(history);
        Item(L.Get("W7PrimaryModel"), "\uE950", () => PrimaryModelChipButton.Flyout?.ShowAt(AssistantButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft }));
        Item(L.Get("WebOpenPanel"), "\uE774", () => vm.ToggleWebBridgeCommand.Execute(null));
        var snapshot = new ToggleMenuFlyoutItem { Text = L.Get("AttachAuto"), IsChecked = vm.IsAutoSnapshot, Icon = new FontIcon { Glyph = "\uE722" } };
        snapshot.Click += (_, _) => vm.IsAutoSnapshot = snapshot.IsChecked;
        f.Items.Add(snapshot);
        Item(L.Get("AttachFile"), "\uE723", () => vm.AttachFileCommand.Execute(null));
    }
}
