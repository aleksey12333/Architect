using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.ViewModels;
using AIArchitectStudio.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Windows.ApplicationModel.DataTransfer;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio;

/// <summary>
/// Волна 3 в главном окне: панель Инженерного агента в боковом доке (плашка основной локальной модели, задача, развёрнутая
/// «Цепочка рассуждений (N шагов · расчёт проверен)» по пяти фазам, проверенные гипотезы, карточки вызванных инструментов, отчёт,
/// отмена одним Ctrl+Z), Flyout «Локальное ядро» у чипа панели команд (рейтинг установленных моделей, автовыбор, ручной выбор,
/// замер скорости), окно веб-импорта прайса и проверки подтянутых расценок.
/// </summary>
public sealed partial class MainWindow
{
    private bool _agentQueued;

    private void InitializeWave3()
    {
        ViewModel.PriceVerifier = ShowPriceVerificationAsync;
        ViewModel.PricePullRequested += async (_, _) => await ShowPriceVerificationAsync(null);
        ViewModel.AgentRunCompleted += (_, _) => QueueAgentPanel();
        ViewModel.ModelDirector.Changed += (_, _) => DispatcherQueue.TryEnqueue(QueueAgentPanel);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsAgentPanelVisible) or nameof(MainViewModel.IsMeasuringSpeed) or nameof(MainViewModel.PrimaryModelChip)) QueueAgentPanel();
        };
        L.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalizationService.Language)) QueueAgentPanel();
        };
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        if (Application.Current.Resources.TryGetValue("CouncilFlyoutPresenterStyle", out var style)) flyout.FlyoutPresenterStyle = (Style)style;
        flyout.Opening += (_, _) => flyout.Content = PrimaryModelFlyoutContent(flyout);
        PrimaryModelChipButton.Flyout = flyout;
        QueueAgentPanel();
#if DEBUG
        InitializeWave3Debug();
#endif
    }

    private void QueueAgentPanel()
    {
        if (_agentQueued) return;
        _agentQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _agentQueued = false;
            if (ViewModel.IsAgentPanelVisible) RefreshAgentPanel();
        });
    }

    // ───────────────────────────── Панель агента ─────────────────────────────

    private void RefreshAgentPanel()
    {
        var panel = AgentPanelContent;
        panel.Children.Clear();
        var vm = ViewModel;

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Glyph("\uE90F", 13));
        var titles = new StackPanel();
        titles.Children.Add(Text(L.Get("W3AgentTitle"), 13, strong: true));
        titles.Children.Add(Text(L.Format("W3AgentHistory", vm.AgentRuns.Count), 10, secondary: true));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        var collapse = IconButton("\uE76C", L.Get("W1DockCollapse"), (_, _) => vm.ToggleCouncilDockCollapsedCommand.Execute(null));
        Grid.SetColumn(collapse, 2);
        head.Children.Add(collapse);
        var close = IconButton("\uE711", L.Get("Close"), (_, _) => vm.CloseAgentPanelCommand.Execute(null));
        Grid.SetColumn(close, 3);
        head.Children.Add(close);
        panel.Children.Add(head);

        panel.Children.Add(PrimaryModelPlate(compact: true));

        if (vm.LastAgentRun is not { } run)
        {
            panel.Children.Add(Card(Text(L.Get("W3AgentEmpty"), 11.5, secondary: true, wrap: true), 10));
            return;
        }
        var c = L.Culture;

        // Задача и план.
        var task = new StackPanel { Spacing = 3 };
        task.Children.Add(Caption(L.Get("W3AgentTask")));
        task.Children.Add(Text(run.Task, 12, wrap: true));
        task.Children.Add(Text(L.Format("W3AgentPlanner", run.PlannerName) + " · " + L.Format("W3AgentElapsed", run.ElapsedMs.ToString("N0", c)), 10.5, secondary: true, wrap: true));
        panel.Children.Add(Card(task, 10));

        // Цепочка рассуждений.
        panel.Children.Add(TracePanel(run.Trace));

        // Вызванные инструменты.
        panel.Children.Add(Caption(L.Format("W3AgentTools", run.ToolCalls.Count)));
        foreach (var call in run.ToolCalls) panel.Children.Add(ToolCard(call));

        // Отчёт и действия.
        var report = new StackPanel { Spacing = 3 };
        report.Children.Add(Caption(L.Get("W3AgentReport")));
        foreach (var line in run.Report) report.Children.Add(Text(line, 11.5, wrap: true));
        panel.Children.Add(Card(report, 10, accent: run.Success));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var undo = ActionButton("\uE7A7", L.Get("W3AgentUndo"), (_, _) => { vm.UndoAgentCommand.Execute(null); QueueAgentPanel(); }, enabled: run.Changed && vm.Scene.CanUndo);
        actions.Children.Add(undo);
        actions.Children.Add(ActionButton("\uE8C8", L.Get("W3AgentCopy"), (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(run.ToText());
            Clipboard.SetContent(package);
            vm.ChatMessages.Add(ChatMessageModel.System(L.Get("W3AgentCopied")));
        }));
        panel.Children.Add(actions);
    }

    private static Button IconButton(string glyph, string tip, RoutedEventHandler click)
    {
        var b = new Button { Style = (Style)Application.Current.Resources["IconButtonStyle"], Content = new FontIcon { Glyph = glyph, FontSize = 11, Foreground = Res("TextSecondaryBrush") } };
        ToolTipService.SetToolTip(b, tip);
        b.Click += click;
        return b;
    }

    /// <summary>«Цепочка рассуждений (N шагов · расчёт проверен)»: пять фаз со строками выкладок, показателями «было → стало» и гипотезами.</summary>
    private UIElement TracePanel(ReasoningTrace trace)
    {
        var c = L.Culture;
        var stack = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Glyph("\uE9D5", 13, trace.IsVerified ? "AccentBrush" : "TextSecondaryBrush"));
        head.Children.Add(Text(trace.Summary, 12.5, strong: true, wrap: true));
        stack.Children.Add(head);
        if (trace.Corrections > 0)
            stack.Children.Add(Chip($"корректировок шага: {trace.Corrections}", "#FF9F0A"));
        foreach (var step in trace.Steps)
        {
            var body = new StackPanel { Spacing = 3 };
            foreach (var line in step.Lines)
            {
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 7, 0, 0) });
                var t = Text(line, 11, wrap: true);
                Grid.SetColumn(t, 1);
                row.Children.Add(t);
                body.Children.Add(row);
            }
            foreach (var m in step.Metrics)
            {
                var metric = Text(m.ToText(c), 11, mono: true, wrap: true);
                metric.Foreground = Res("AccentBrush");
                body.Children.Add(metric);
            }
            if (step.Phase == ReasoningPhase.Sandbox && trace.Hypotheses.Count > 0)
            {
                body.Children.Add(Caption(L.Get("W3ReasoningHypotheses")));
                foreach (var h in trace.Hypotheses)
                {
                    var line = new Grid { ColumnSpacing = 6 };
                    line.ColumnDefinitions.Add(new ColumnDefinition());
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var name = Text($"{h.Title} · K {h.KAfter.ToString("0.00", c)} · {h.CostRub.ToString("N0", c)} ₽", 10.5, wrap: true);
                    line.Children.Add(name);
                    var chip = Chip(h.Chosen ? L.Get("W3ReasoningChosen") : h.Passed ? L.Get("W3ReasoningPassed") : L.Get("W3ReasoningRejected"),
                        h.Chosen ? "#30D158" : h.Passed ? "#0A84FF" : "#FF453A", 9.5);
                    if (!h.Passed && h.RejectReason.Length > 0) ToolTipService.SetToolTip(chip, h.RejectReason);
                    Grid.SetColumn(chip, 1);
                    line.Children.Add(chip);
                    body.Children.Add(line);
                }
            }
            if (step.Phase == ReasoningPhase.Decision && trace.Actions.Count > 0)
            {
                body.Children.Add(Caption(L.Get("W3ReasoningActions")));
                for (int i = 0; i < trace.Actions.Count; i++) body.Children.Add(Text($"{i + 1}) {trace.Actions[i]}", 11, wrap: true));
            }

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var badge = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Hex(step.StatusColorHex, 0x26),
                Child = new TextBlock { Text = step.Number.ToString(c), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Hex(step.StatusColorHex), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            header.Children.Add(badge);
            var title = Text(step.Title[(step.Title.IndexOf(' ') + 1)..], 11.5, strong: true, wrap: true);
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            var status = new FontIcon { Glyph = step.StatusGlyph, FontSize = 11, Foreground = Hex(step.StatusColorHex) };
            Grid.SetColumn(status, 2);
            header.Children.Add(status);
            stack.Children.Add(new Expander
            {
                Header = header,
                Content = body,
                // Исходные данные и нормативы — свёрнуты, расчётные фазы (песочница, самопроверка, решение) — развёрнуты.
                IsExpanded = step.Phase >= ReasoningPhase.Sandbox || step.Status != ReasoningStepStatus.Passed,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 8),
                MinHeight = 0
            });
        }
        return Card(stack, 10);
    }

    private UIElement ToolCard(AgentToolCall call)
    {
        var c = L.Culture;
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(GlyphBadge(call.Glyph, 26, Hex(call.StatusColorHex, 0x26), Hex(call.StatusColorHex)));
        var body = new StackPanel { Spacing = 3 };
        var top = new Grid { ColumnSpacing = 6 };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel();
        names.Children.Add(Text(call.Title, 12, strong: true));
        names.Children.Add(Text($"{call.Name} · {L.Format("W3AgentElapsed", call.ElapsedMs.ToString("N0", c))}", 10, secondary: true, mono: true));
        top.Children.Add(names);
        var chip = Chip(call.Status switch
        {
            AgentToolStatus.Succeeded => L.Get("W3AgentStatusOk"),
            AgentToolStatus.Failed => L.Get("W3AgentStatusFailed"),
            _ => L.Get("W3AgentStatusSkipped")
        }, call.StatusColorHex, 9.5);
        Grid.SetColumn(chip, 1);
        top.Children.Add(chip);
        body.Children.Add(top);
        if (call.Summary.Length > 0) body.Children.Add(Text(call.Summary, 11, wrap: true));
        if (call.Results.Count > 0)
        {
            var results = new Grid { ColumnSpacing = 8, RowSpacing = 1 };
            results.ColumnDefinitions.Add(new ColumnDefinition());
            results.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int r = 0;
            foreach (var (name, value) in call.Results)
            {
                results.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var n = Text(name, 10.5, secondary: true);
                var v = Text(value, 11, mono: true);
                v.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(n, r);
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                results.Children.Add(n);
                results.Children.Add(v);
                r++;
            }
            body.Children.Add(results);
        }
        if (call.Details.Count > 0)
        {
            var details = new StackPanel { Spacing = 2 };
            foreach (var d in call.Details) details.Children.Add(Text(d, 10.5, secondary: true, wrap: true));
            body.Children.Add(new Expander
            {
                Header = Text($"{L.Get("W3ReasoningExpand")} · {call.Details.Count}", 10.5, secondary: true),
                Content = details, IsExpanded = false, Padding = new Thickness(8, 4, 8, 6), MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
            });
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return Card(grid, 10);
    }

    // ───────────────────────────── Основная локальная модель ─────────────────────────────

    /// <summary>Плашка выбранной лучшей локальной модели: модель, рейтинг, специализация, скорость, «Автовыбор лучшей модели».</summary>
    private UIElement PrimaryModelPlate(bool compact)
    {
        var vm = ViewModel;
        var stack = new StackPanel { Spacing = 4 };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(GlyphBadge("\uE950", 28, Hex(vm.PrimaryModelColorHex, 0x26), Hex(vm.PrimaryModelColorHex)));
        var names = new StackPanel();
        names.Children.Add(Caption(L.Get("W3CoreModel")));
        names.Children.Add(Text(vm.PrimaryModelTitle, 12.5, strong: true));
        Grid.SetColumn(names, 1);
        top.Children.Add(names);
        if (vm.HasPrimaryModel)
        {
            var rating = Chip(vm.PrimaryModelRating, vm.PrimaryModelColorHex, 10.5);
            ToolTipService.SetToolTip(rating, L.Get("W3CoreRating"));
            Grid.SetColumn(rating, 2);
            top.Children.Add(rating);
        }
        stack.Children.Add(top);
        stack.Children.Add(Text(vm.PrimaryModelDetail, 10.5, secondary: true, wrap: true));
        if (vm.HasPrimaryModel)
        {
            var speed = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            speed.Children.Add(Glyph("\uEC4A", 11, "TextSecondaryBrush"));
            speed.Children.Add(Text(vm.IsMeasuringSpeed ? L.Get("W3CoreMeasuring") : vm.PrimaryModelSpeed, 10.5, mono: true));
            stack.Children.Add(speed);
        }
        if (!compact) stack.Children.Add(Text(L.Get("W3CoreRole"), 10.5, secondary: true, wrap: true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(ActionButton("\uE945", L.Get("W3CoreAuto"), (_, _) => vm.AutoSelectPrimaryModelCommand.Execute(null), accent: true, enabled: vm.ModelDirector.Candidates.Count > 0));
        if (!compact && vm.HasPrimaryModel && vm.ModelDirector.Primary?.Option.IsOllama == true)
            buttons.Children.Add(ActionButton("\uEC4A", L.Get("W3CoreMeasure"), (_, _) => vm.MeasurePrimarySpeedCommand.Execute(null), enabled: !vm.IsMeasuringSpeed));
        stack.Children.Add(buttons);
        return Card(stack, 10);
    }

    private UIElement PrimaryModelFlyoutContent(Flyout flyout)
    {
        var vm = ViewModel;
        var c = L.Culture;
        var root = new StackPanel { Width = 470, Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Glyph("\uE950", 14));
        head.Children.Add(Text(L.Get("W3CoreTitle"), 14, strong: true));
        root.Children.Add(head);
        root.Children.Add(PrimaryModelPlate(compact: false));
        root.Children.Add(Caption(L.Get("W3CoreCandidates")));
        var primaryId = vm.ModelDirector.Primary?.Option.Id;
        foreach (var candidate in vm.ModelDirector.Candidates.Take(10))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var score = Text(candidate.EngineeringScore.ToString("0", c), 14, strong: true, mono: true);
            score.Foreground = candidate.Available ? Hex(candidate.EngineeringScore >= 75 ? "#30D158" : candidate.EngineeringScore >= 55 ? "#0A84FF" : "#FF9F0A") : Res("TextSecondaryBrush");
            row.Children.Add(score);
            var info = new StackPanel();
            info.Children.Add(Text(candidate.Option.ModelId, 11.5, strong: candidate.Option.Id == primaryId));
            info.Children.Add(Text($"{candidate.Family} · {candidate.ParameterBillions.ToString("0.#", c)}B · {candidate.SizeGb.ToString("0.#", c)} ГБ · {candidate.Specialization}" +
                                   (candidate.Available ? string.Empty : " · " + L.Get("W3CoreUnavailable")), 10, secondary: true, wrap: true));
            ToolTipService.SetToolTip(info, string.Join("\n", candidate.Reasons));
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            if (candidate.Option.Id != primaryId && candidate.Available)
            {
                var pick = new Button { Style = (Style)Application.Current.Resources["PillChipStyle"], Content = L.Get("W3CoreChoose"), FontSize = 11 };
                string id = candidate.Option.Id;
                pick.Click += (_, _) =>
                {
                    vm.SetPrimaryModel(id);
                    flyout.Content = PrimaryModelFlyoutContent(flyout);
                };
                Grid.SetColumn(pick, 2);
                row.Children.Add(pick);
            }
            root.Children.Add(row);
        }
        if (vm.ModelDirector.Candidates.Count == 0) root.Children.Add(Text(L.Get("W3CoreNoneHint"), 11, secondary: true, wrap: true));
        root.Children.Add(Text(L.Get("W3CoreSettingsHint"), 10.5, secondary: true, wrap: true));
        return new ScrollViewer { MaxHeight = 620, Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ───────────────────────────── Веб-импорт прайса ─────────────────────────────

    /// <summary>Окно источников и проверки расценок; null — пользователь отменил (смета не меняется).</summary>
    private async Task<PriceExtractionBatch?> ShowPriceVerificationAsync(PriceImportRequest? request) => await ShowPriceVerificationAsync(request, null, null);

    private async Task<PriceExtractionBatch?> ShowPriceVerificationAsync(PriceImportRequest? request, HttpMessageHandler? handler, Action<PriceVerificationDialog>? opened)
    {
        if (AnyDialogOpen) return null;
        _block34Open = true;
        try
        {
            var dialog = new PriceVerificationDialog(ViewModel, WinRT.Interop.WindowNative.GetWindowHandle(this), request)
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = DialogTheme,
                Handler = handler
            };
            if (opened is not null) dialog.Opened += (_, _) => opened(dialog);
            await dialog.ShowAsync();
            if (!dialog.Applied) ViewModel.ChatMessages.Add(ChatMessageModel.System(L.Get("W3PriceCancelled")));
            return dialog.Applied ? dialog.Batch : null;
        }
        finally
        {
            _block34Open = false;
        }
    }

#if DEBUG
    /// <summary>
    /// Съёмка волны 3: AIA_DEBUG_AGENT=1|demo — задание Инженерному агенту (аудит + высота 1-го этажа + подбор колонн по ГОСТ +
    /// показатели Renga) с развёрнутой цепочкой рассуждений, плашкой основной локальной модели (demo — тестовый список моделей вместо
    /// сервера); AIA_DEBUG_PRICEVERIFY=1 — окно проверки расценок после разбора демонстрационной страницы магазина и прошлой сметы;
    /// AIA_DEBUG_REBAR3D=1 — 3D-армирование: шаг хомутов колонн 100 мм, Ø25, защитный слой 30 мм и каркасы продавливания плиты.
    /// </summary>
    private void InitializeWave3Debug()
    {
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_AGENT") is { Length: > 0 } agent)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2400);
                if (agent is "demo" or "tools") ViewModel.OverrideDirectorModels(CadSelfTestService.DemoLocalModels(), CadSelfTestService.DemoModelSpeed());
                ViewModel.IsAgentMode = true;
                await ViewModel.RunAgentAsync(CadSelfTestService.AgentDemoTask);
                ViewModel.IsHistoryOpen = agent != "tools";
                if (agent == "tools")
                {
                    // Карточки вызванных инструментов — прокрутка панели агента к ним.
                    await Task.Delay(900);
                    if (AgentPanelHost.Child is ScrollViewer sv) sv.ChangeView(null, sv.ScrollableHeight, null, true);
                }
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_PRICEVERIFY") is { Length: > 0 })
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2400);
                string past = CadSelfTestService.WriteDemoPastProject(Path.Combine(Path.GetTempPath(), "AIArchitectStudio-demo"));
                await ShowPriceVerificationAsync(new PriceImportRequest([CadSelfTestService.DemoStoreUrl], [past], string.Empty, 5), new CadSelfTestService.DemoStoreHandler(), null);
            };
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_REBAR3D") is { Length: > 0 })
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                CadSelfTestService.PrepareRebarDemo(ViewModel);
                // Ближний план оголовка средней колонны: каркасы продавливания и хомуты с шагом 100 мм.
                await Task.Delay(900);
                Viewport.DebugSetCamera(new SharpDX.Vector3(4.2f, 5.6f, 5.4f), new SharpDX.Vector3(0.4f, 2.6f, 0.4f));
            };
    }
#endif
}
