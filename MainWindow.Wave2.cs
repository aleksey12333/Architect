using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using AIArchitectStudio.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio;

/// <summary>
/// Волна 2 в главном окне: панель режима «Конструкции (КЖ / КМ)» (фактор и шкала оверлея, напряжения выбранного элемента, закрепление
/// концов, армирование, продавливание, профили КМ, аудит расчётной схемы), паспорт свойств Renga в инспекторе, окна «Аудит расчётной
/// схемы» и «Мастер размеров и пакетное редактирование».
/// </summary>
public sealed partial class MainWindow
{
    private bool _wave2Queued;

    private void InitializeWave2()
    {
        ViewModel.AuditReportReady += async (_, report) => await ShowAuditAsync(report);
        ViewModel.BuildingDimensionsRequested += async (_, _) => await ShowBuildingDimensionsAsync();
        ViewModel.StructuralOverlayChanged += (_, _) => QueueWave2Refresh();
        ViewModel.Scene.SelectionChanged += (_, _) => QueueWave2Refresh();
        ViewModel.Scene.DocumentChanged += (_, _) => QueueWave2Refresh();
        ViewModel.Structure.AnalysisChanged += (_, _) => DispatcherQueue.TryEnqueue(QueueWave2Refresh);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(MainViewModel.IsStructuralMode)) return;
            QueueWave2Refresh();
            UpdateAiDockMargin();
        };
        L.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.Language)) QueueWave2Refresh();
        };
        QueueWave2Refresh();
#if DEBUG
        InitializeWave2Debug();
#endif
    }

    /// <summary>Перестройка панели конструктора и свойств Renga — не чаще раза за кадр UI.</summary>
    private void QueueWave2Refresh()
    {
        if (_wave2Queued) return;
        _wave2Queued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _wave2Queued = false;
            RefreshRengaProperties();
            if (ViewModel.IsStructuralMode) RefreshStructuralPanel();
        });
    }

    // ───────────────────────────── Свойства Renga ─────────────────────────────

    private void RefreshRengaProperties()
    {
        RengaPropertiesHost.Children.Clear();
        if (ViewModel.Scene.IsMultiSelection || ViewModel.SelectedRengaProperties() is not { } p) return;
        var c = L.Culture;
        string M3(double v) => v.ToString("0.###", c) + " м³";
        string M2(double v) => v.ToString("0.##", c) + " м²";
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = L.Get("W2RengaTitle"), Style = (Style)Application.Current.Resources["SectionHeaderStyle"], Margin = new Thickness(0, 4, 0, 0) });
        var assign = new Button
        {
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(8, 0, 8, 0),
            Command = ViewModel.AssignMarksCommand,
            Content = new TextBlock { Text = L.Get("W2MarksAssign"), FontSize = 11 }
        };
        ToolTipService.SetToolTip(assign, L.Get("W2MarksAssignTip"));
        header.Children.Add(assign);
        RengaPropertiesHost.Children.Add(header);

        void Group(string caption, IEnumerable<(string Name, string Value)> rows)
        {
            var grid = new Grid { ColumnSpacing = 8, RowSpacing = 2 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int r = 0;
            foreach (var (name, value) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var n = Text(name, 11, secondary: true);
                var v = Text(value, 11.5, mono: true);
                v.HorizontalAlignment = HorizontalAlignment.Right;
                v.MaxWidth = 170;
                Grid.SetRow(n, r);
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                grid.Children.Add(n);
                grid.Children.Add(v);
                r++;
            }
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(Caption(caption));
            stack.Children.Add(grid);
            RengaPropertiesHost.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["WalletCardStyle"],
                Padding = new Thickness(10, 8, 10, 8),
                Child = stack
            });
        }
        Group(L.Get("W2RengaIdent"),
        [
            (L.Get("W2RengaMark"), p.Mark), (L.Get("W2RengaPosition"), p.Position > 0 ? p.Position.ToString(c) : "—"), (L.Get("W2RengaLevel"), p.LevelName),
            (L.Get("W2RengaOffset"), p.BaseOffsetMm.ToString("N0", c) + " мм"), (L.Get("W2RengaRole"), L.Get("W2Role" + p.Role))
        ]);
        Group(L.Get("W2RengaCalc"),
        [
            (L.Get("W2RengaNet"), M3(p.NetVolume)), (L.Get("W2RengaGross"), M3(p.GrossVolume)), (L.Get("W2RengaOpenings"), M2(p.OpeningArea)),
            (L.Get("W2RengaFormwork"), M2(p.FormworkArea)), (L.Get("W2RengaBase"), M2(p.BaseArea)), (L.Get("W2RengaPerimeter"), p.Perimeter.ToString("0.##", c) + " м"),
            (L.Get("W2RengaMass"), p.MassTonnes.ToString("0.###", c) + " т"), (L.Get("W2RengaRebar"), p.RebarKg > 0 ? p.RebarKg.ToString("N0", c) + " кг" : "—")
        ]);
        Group(L.Get("W2RengaEng"),
        [
            (L.Get("W2RengaFire"), p.FireResistance),
            (L.Get("W2RengaR0"), p.ThermalResistance is { } r0 ? r0.ToString("0.00", c) + " м²·°С/Вт" : "—")
        ]);
    }

    // ───────────────────────────── Панель «Конструкции» ─────────────────────────────

    private void RefreshStructuralPanel()
    {
        var panel = StructuralPanelContent;
        panel.Children.Clear();
        var vm = ViewModel;
        var c = L.Culture;

        var head = new Grid();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Glyph("\uE9D9", 13));
        title.Children.Add(Text(L.Get("W2PanelTitle"), 13, strong: true));
        head.Children.Add(title);
        var close = new Button { Style = (Style)Application.Current.Resources["IconButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right, Content = new FontIcon { Glyph = "\uE711", FontSize = 11 } };
        close.Click += (_, _) => vm.IsStructuralMode = false;
        head.Children.Add(close);
        panel.Children.Add(head);
        if (vm.StructuralModel is { } model)
            panel.Children.Add(Text(L.Format("W2PanelModel", model.Members.Count, model.Panels.Count, model.Nodes.Count), 11, secondary: true, wrap: true));

        // Фактор оверлея и шкала.
        panel.Children.Add(Caption(L.Get("W2PanelFactor")));
        var factor = new ComboBox { ItemsSource = vm.StructuralFactorTitles, SelectedIndex = vm.StructuralFactorIndex, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 30, FontSize = 12 };
        factor.SelectionChanged += (_, _) => { if (factor.SelectedIndex >= 0) vm.StructuralFactorIndex = factor.SelectedIndex; };
        panel.Children.Add(factor);
        var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        foreach (var (k, offset) in new[] { (0.0, 0.0), (0.35, 0.3), (0.7, 0.6), (0.95, 0.85), (1.2, 1.0) })
            gradient.GradientStops.Add(new GradientStop { Color = StressPalette.ToUi(StressPalette.ColorFor(k)), Offset = offset });
        panel.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = gradient });
        var (low, mid, high) = vm.StructuralScale();
        var scale = new Grid();
        var lowText = Text(low, 10.5, secondary: true);
        var midText = Text(mid, 10.5, secondary: true);
        midText.HorizontalAlignment = HorizontalAlignment.Center;
        var highText = Text(high, 10.5, secondary: true);
        highText.HorizontalAlignment = HorizontalAlignment.Right;
        scale.Children.Add(lowText);
        scale.Children.Add(midText);
        scale.Children.Add(highText);
        panel.Children.Add(scale);
        panel.Children.Add(Text(L.Get("W2PanelLegend"), 10.5, secondary: true, wrap: true));

        var info = vm.StructuralSelection();
        if (info is null)
        {
            panel.Children.Add(Card(Text(L.Get("W2PanelSelect"), 11.5, secondary: true, wrap: true), 10));
        }
        else
        {
            var card = new StackPanel { Spacing = 3 };
            card.Children.Add(Text(info.Element.DisplayTitle, 12.5, strong: true));
            if (info.Stress is { } s)
            {
                card.Children.Add(Text($"σmax = {s.SigmaN.ToString("0.00", c)} + {(s.SigmaMx + s.SigmaMy).ToString("0.00", c)} = {s.SigmaMax.ToString("0.00", c)} МПа", 11.5, mono: true));
                card.Children.Add(Text($"R = {s.R.ToString("0.0", c)} МПа · Rсеч = {s.RSection.ToString("0.0", c)} МПа", 11.5, mono: true));
                card.Children.Add(Text($"τ = {s.Tau.ToString("0.000", c)} МПа · Rs = {s.RShear.ToString("0.00", c)} МПа", 11.5, mono: true));
                var eta = Text($"η = {s.StressRatio.ToString("0.00", c)} · Kисп = {s.Utilization.ToString("0.00", c)}", 12, strong: true, mono: true);
                eta.Foreground = new SolidColorBrush(StressPalette.ToUi(StressPalette.ColorFor(s.Utilization)));
                card.Children.Add(eta);
            }
            else if (vm.Analysis.For(info.Element.Id) is { } r)
                card.Children.Add(Text($"Kисп = {r.Utilization.ToString("0.00", c)} · {r.Governing}", 11.5, mono: true));
            else card.Children.Add(Text(L.Get("W2PanelNonStructural"), 11, secondary: true, wrap: true));
            if (info.Punching is { } p)
                card.Children.Add(Text(L.Format("W2PanelPunch", p.Position, p.Faces, p.Tau.ToString("0.000", c), p.Rbt.ToString("0.00", c), p.Ratio.ToString("0.00", c)), 11, wrap: true));
            panel.Children.Add(Card(card, 10));

            if (info.IsVertical)
            {
                panel.Children.Add(Caption(L.Get("W2PanelFixity")));
                foreach (var f in new[] { EndFixity.Fixed, EndFixity.PinnedFixed, EndFixity.Pinned, EndFixity.Cantilever })
                {
                    var rb = new RadioButton
                    {
                        GroupName = "W2Fixity",
                        Content = L.Get("W2Fixity" + f),
                        FontSize = 12,
                        MinHeight = 28,
                        IsChecked = info.Fixity == f || (info.Fixity == EndFixity.Default && f == EndFixity.Fixed && IsConcrete(info.Element))
                                    || (info.Fixity == EndFixity.Default && f == EndFixity.Pinned && !IsConcrete(info.Element))
                    };
                    var fixity = f;
                    rb.Checked += (_, _) => { if (StructuralDesign.Fixity(info.Element) != fixity) vm.SetFixity(fixity); };
                    panel.Children.Add(rb);
                }
            }

            if (ReinforcementCalculator.IsReinforced(info.Element))
            {
                panel.Children.Add(Caption(L.Get("W2PanelRebar")));
                var grid = new Grid { ColumnSpacing = 6, RowSpacing = 4 };
                for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var diameters = StructuralDesign.BarDiameters.ToList();
                int dIndex = Math.Max(0, diameters.IndexOf((int)Math.Round((info.BarDiameter ?? 0.016) * 1000)));
                var diameter = new ComboBox { ItemsSource = diameters.Select(d => "Ø" + d).ToList(), SelectedIndex = dIndex, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 28, FontSize = 12 };
                NumberBox Box(double value, double min, double max) => new()
                {
                    Value = value, Minimum = min, Maximum = max, SmallChange = 10, Height = 28, FontSize = 12, MinWidth = 0,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden
                };
                var stirrups = Box(Math.Round((info.StirrupSpacing ?? 0.2) * 1000), 100, 300);
                var cover = Box(Math.Round((info.Cover ?? 0.04) * 1000), 20, 50);
                (string Cap, FrameworkElement El)[] cells = [(L.Get("W2RebarDiameter"), diameter), (L.Get("W2RebarStirrups"), stirrups), (L.Get("W2RebarCover"), cover)];
                for (int i = 0; i < cells.Length; i++)
                {
                    var cap = Text(cells[i].Cap, 10.5, secondary: true);
                    Grid.SetColumn(cap, i);
                    Grid.SetColumn(cells[i].El, i);
                    Grid.SetRow(cells[i].El, 1);
                    grid.Children.Add(cap);
                    grid.Children.Add(cells[i].El);
                }
                panel.Children.Add(grid);
                var apply = ActionButton("\uE73E", L.Get("W2RebarApply"), (_, _) =>
                {
                    if (diameter.SelectedIndex < 0 || !double.IsFinite(stirrups.Value) || !double.IsFinite(cover.Value)) return;
                    vm.SetRebarDesign(diameters[diameter.SelectedIndex] / 1000.0, stirrups.Value / 1000, cover.Value / 1000);
                });
                apply.HorizontalAlignment = HorizontalAlignment.Stretch;
                panel.Children.Add(apply);
            }

            if (info.IsSlab)
            {
                var toggle = new ToggleSwitch { Header = L.Get("W2PunchRebar"), IsOn = info.PunchingRebar, FontSize = 12 };
                toggle.Toggled += (_, _) => vm.SetPunchingRebar(toggle.IsOn);
                panel.Children.Add(toggle);
                panel.Children.Add(Text(L.Get("W2PunchRebarHint"), 10.5, secondary: true, wrap: true));
            }

            if (info.IsSteelCandidate)
            {
                panel.Children.Add(Caption(L.Get("W2PanelSteel")));
                var profiles = vm.SteelProfiles;
                var box = new ComboBox { ItemsSource = profiles.Select(x => $"{x.Mark} · {x.Standard}").ToList(), SelectedIndex = profiles.Count > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 28, FontSize = 12 };
                panel.Children.Add(box);
                var apply = ActionButton("\uE8F1", L.Get("W2SteelApply"), (_, _) => { if (box.SelectedIndex >= 0) vm.ApplySteelProfile(profiles[box.SelectedIndex]); });
                apply.HorizontalAlignment = HorizontalAlignment.Stretch;
                panel.Children.Add(apply);
            }
        }

        panel.Children.Add(Divider(6));
        var audit = ActionButton("\uE9F5", vm.IsAuditRunning ? L.Get("W2AuditRunning") : L.Get("W2Audit"), (_, _) => vm.RunSchemeAuditCommand.Execute(null), accent: true);
        audit.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(audit);
        var dims = ActionButton("\uE740", L.Get("W2Dims"), (_, _) => vm.OpenBuildingDimensionsCommand.Execute(null));
        dims.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(dims);
    }

    private static bool IsConcrete(ArchitecturalElement e) => (e.Material ?? string.Empty).Contains("concrete", StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── Окна ─────────────────────────────

    private async Task ShowAuditAsync(FeaAuditReport report)
    {
        if (AnyDialogOpen) return;
        _block34Open = true;
        try
        {
            var dialog = new FeaAuditDialog(ViewModel, report) { XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme };
            await dialog.ShowAsync();
        }
        finally
        {
            _block34Open = false;
        }
    }

    private async Task ShowBuildingDimensionsAsync(int tab = 0, Action<BuildingDimensionsDialog>? debug = null)
    {
        if (AnyDialogOpen) return;
        _block34Open = true;
        try
        {
            var dialog = new BuildingDimensionsDialog(ViewModel, tab) { XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme };
            if (debug is not null) dialog.Opened += (_, _) => debug(dialog);
            await dialog.ShowAsync();
        }
        finally
        {
            _block34Open = false;
        }
    }

#if DEBUG
    /// <summary>
    /// Съёмка волны 2: AIA_DEBUG_STRUCTURAL=1|panel|stress — режим «Конструкции» (самая нагруженная колонна выделена; 1 — ещё и окно аудита,
    /// stress — фактор «Напряжения σ»), AIA_DEBUG_DIMS=1|batch — мастер размеров (batch — вкладка пакетной правки).
    /// </summary>
    private void InitializeWave2Debug()
    {
        string? structural = Environment.GetEnvironmentVariable("AIA_DEBUG_STRUCTURAL");
        if (structural is { Length: > 0 })
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2400);
                if (ViewModel.MostLoaded(BimCategory.Column) is { } column) ViewModel.SelectElement(column);
                if (structural == "stress") ViewModel.StructuralFactorIndex = (int)StructuralFactor.Stress;
                ViewModel.IsStructuralMode = true;
                await Task.Delay(1200);
                if (structural == "1") await ViewModel.RunSchemeAuditCommand.ExecuteAsync(null);
            };
        string? dims = Environment.GetEnvironmentVariable("AIA_DEBUG_DIMS");
        if (dims is { Length: > 0 })
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2400);
                await ShowBuildingDimensionsAsync(dims == "batch" ? 1 : 0, d =>
                {
                    if (dims != "batch")
                    {
                        d.DebugSetRow("x", 1, 7200);
                        d.DebugSetRow("storey", 0, 4200);
                    }
                });
            };
    }
#endif
}
