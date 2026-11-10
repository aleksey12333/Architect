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
/// Волна 6 в главном окне (с перестройкой Волны 7): правый док разделов, окно «Градостроительные ограничения и ЗОУИТ»,
/// защита выхода (AppWindow.Closing → «Сохранить прогресс проекта перед выходом?») и индикатор
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

    // ═════════════════════════════ Правый док разделов ═════════════════════════════

    /// <summary>Волна 7: правый док разделов 1 и 3 (вкладки и содержимое — MainWindow.Wave7.cs).</summary>
    private void RefreshEnvironmentPanel() => BuildSectionDock();

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
