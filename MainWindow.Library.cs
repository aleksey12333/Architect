using AIArchitectStudio.Services;
using AIArchitectStudio.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AIArchitectStudio;

/// <summary>
/// Блок девелопера 3–4 в главном окне: «Добавить элемент», библиотека конструкций ГОСТ/СП, история изменений и контрольные
/// точки, справочный центр (кнопка «?» и F1) и интерактивный тур по интерфейсу — подсветка ключевых зон экрана по шагам.
/// </summary>
public sealed partial class MainWindow
{
    private bool _block34Open;

    private void InitializeBlock34()
    {
        ViewModel.LibraryRequested += async (_, _) => await ShowLibraryAsync();
        ViewModel.HistoryRequested += async (_, _) => await ShowHistoryAsync();
        ViewModel.HelpRequested += async (_, tab) => await ShowHelpAsync(tab);
        ViewModel.TourRequested += (_, _) => StartTour(persist: false);
        RootGrid.SizeChanged += (_, _) => LayoutTour();
    }

    private bool AnyDialogOpen => _block34Open || _settingsOpen || _quantitiesOpen || _passportOpen;

    private ElementTheme DialogTheme => _theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark;

    // ───────────────────────────── Верхний бар ─────────────────────────────

    private void OnAddElementClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind }) ViewModel.AddElementOfKindCommand.Execute(kind);
    }

    private async void OnLibraryClick(object sender, RoutedEventArgs e) => await ShowLibraryAsync();

    private async void OnHistoryClick(object sender, RoutedEventArgs e) => await ShowHistoryAsync();

    private async void OnHelpClick(object sender, RoutedEventArgs e) => await ShowHelpAsync(null);

    private async void OnHelpAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowHelpAsync(null);
    }

    private async void OnVersionTapped(object sender, TappedRoutedEventArgs e) => await ShowHistoryAsync();

    // ───────────────────────────── Окна ─────────────────────────────

    /// <param name="section">Debug-съёмка: раздел библиотеки.</param>
    private async Task ShowLibraryAsync(string? section = null)
    {
        if (AnyDialogOpen) return;
        _block34Open = true;
        try
        {
            var dialog = new StandardLibraryDialog(ViewModel, PickLibraryFileAsync, PickLibraryExportPathAsync) { XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme };
            if (section is not null) dialog.Opened += (_, _) => dialog.DebugSelectSection(section);
            await dialog.ShowAsync();
        }
        finally
        {
            _block34Open = false;
        }
    }

    private async Task ShowHistoryAsync(string? checkpointName = null)
    {
        if (AnyDialogOpen) return;
        _block34Open = true;
        try
        {
            var dialog = new HistoryTimelineDialog(ViewModel) { XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme };
            if (checkpointName is not null) dialog.Opened += (_, _) => dialog.DebugSetCheckpointName(checkpointName);
            await dialog.ShowAsync();
        }
        finally
        {
            _block34Open = false;
        }
    }

    private async Task ShowHelpAsync(string? tab, string? query = null)
    {
        if (AnyDialogOpen) return;
        _block34Open = true;
        bool tour;
        try
        {
            var dialog = new HelpCenterDialog(ViewModel, tab) { XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme };
            if (query is not null) dialog.Opened += (_, _) => dialog.DebugSearch(query);
            await dialog.ShowAsync();
            tour = dialog.StartTour;
        }
        finally
        {
            _block34Open = false;
        }
        if (tour) StartTour(persist: false);
    }

    private async Task<string?> PickLibraryFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".json");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task<string?> PickLibraryExportPathAsync(string name)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ProjectFileService.SafeFileName(name) + ".aialib"
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add(L.Get("LibFileType"), [".json"]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    // ───────────────────────────── Интерактивный тур ─────────────────────────────

    private sealed record TourStep(Func<FrameworkElement?> Target, string TitleKey, string TextKey, bool Canvas = false);

    private List<TourStep>? _tourSteps;
    private int _tourIndex;
    private bool _tourPersist;
    private Grid? _tourLayer;
    private Border? _tourHighlight;
    private Border? _tourCard;
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle[] _tourShade = new Microsoft.UI.Xaml.Shapes.Rectangle[4];

    /// <summary>Тур по интерфейсу: 3D-холст → инспектор → добавление элементов и библиотека ГОСТ → смета → Коллоквиум КБ → история и справка.</summary>
    public void StartTour(bool persist, int step = 0)
    {
        _tourPersist = persist;
        _tourSteps =
        [
            new(() => Viewport, "TourViewportTitle", "TourViewportText", Canvas: true),
            new(() => InspectorIsland.Visibility == Visibility.Visible ? InspectorIsland : null, "TourInspectorTitle", "TourInspectorText"),
            new(() => ToolPalette.Visibility == Visibility.Visible ? ToolPalette : LibraryButton, "TourLibraryTitle", "TourLibraryText"),
            new(() => QuantitiesButton.Visibility == Visibility.Visible ? QuantitiesButton : InspectorIsland, "TourEstimateTitle", "TourEstimateText"),
            new(() => CouncilBar.Visibility == Visibility.Visible ? CouncilBar : AiDock, "TourColloquiumTitle", "TourColloquiumText"),
            new(() => VersionCapsule.ActualWidth > 0 ? VersionCapsule : HelpButton, "TourHistoryTitle", "TourHistoryText")
        ];
        _tourIndex = Math.Clamp(step, 0, _tourSteps.Count - 1);
        EnsureTourLayer();
        _tourLayer!.Visibility = Visibility.Visible;
        ShowTourStep();
    }

    private void EnsureTourLayer()
    {
        if (_tourLayer is not null) return;
        _tourLayer = new Grid { IsHitTestVisible = true };
        Canvas.SetZIndex(_tourLayer, 100);
        var canvas = new Canvas();
        for (int i = 0; i < 4; i++)
        {
            _tourShade[i] = new Microsoft.UI.Xaml.Shapes.Rectangle { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x70, 0, 0, 0)) };
            canvas.Children.Add(_tourShade[i]);
        }
        _tourHighlight = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["AccentBrush"],
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(16),
            IsHitTestVisible = false
        };
        canvas.Children.Add(_tourHighlight);
        _tourCard = new Border { Style = (Style)Application.Current.Resources["IslandStyle"], Width = 360, Padding = new Thickness(16, 14, 16, 14) };
        canvas.Children.Add(_tourCard);
        _tourLayer.Children.Add(canvas);
        RootGrid.Children.Add(_tourLayer);
    }

    private void ShowTourStep()
    {
        if (_tourSteps is null || _tourCard is null) return;
        var step = _tourSteps[_tourIndex];
        var panel = new StackPanel { Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(DialogKit.Chip(L.Format("TourStep", _tourIndex + 1, _tourSteps.Count)));
        panel.Children.Add(head);
        panel.Children.Add(DialogKit.Text(L.Get(step.TitleKey), 15, strong: true, wrap: true));
        panel.Children.Add(DialogKit.Text(L.Get(step.TextKey), 12, secondary: true, wrap: true));
        // Индикатор шагов.
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 2, 0, 2) };
        for (int i = 0; i < _tourSteps.Count; i++)
            dots.Children.Add(new Border
            {
                Width = i == _tourIndex ? 18 : 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = (Brush)Application.Current.Resources[i == _tourIndex ? "AccentBrush" : "StudioBorderBrush"]
            });
        panel.Children.Add(dots);
        var buttons = new Grid { ColumnSpacing = 6 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var skip = DialogKit.ActionButton("", L.Get("TourSkip"), (_, _) => EndTour());
        skip.HorizontalAlignment = HorizontalAlignment.Left;
        buttons.Children.Add(skip);
        if (_tourIndex > 0)
        {
            var back = DialogKit.ActionButton("", L.Get("TourBack"), (_, _) => { _tourIndex--; ShowTourStep(); });
            Grid.SetColumn(back, 1);
            buttons.Children.Add(back);
        }
        bool last = _tourIndex == _tourSteps.Count - 1;
        var next = DialogKit.ActionButton(last ? "" : "", L.Get(last ? "TourFinish" : "TourNext"), (_, _) =>
        {
            if (last) EndTour();
            else
            {
                _tourIndex++;
                ShowTourStep();
            }
        }, accent: true);
        Grid.SetColumn(next, 2);
        buttons.Children.Add(next);
        panel.Children.Add(buttons);
        _tourCard.Child = panel;
        DispatcherQueue.TryEnqueue(LayoutTour);
    }

    /// <summary>Затемнение вокруг подсвеченной зоны, рамка и карточка рядом с ней (в пределах окна).</summary>
    private void LayoutTour()
    {
        if (_tourLayer is not { Visibility: Visibility.Visible } || _tourSteps is null || _tourHighlight is null || _tourCard is null) return;
        double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var step = _tourSteps[_tourIndex];
        Rect r;
        if (step.Canvas)
        {
            // 3D-холст: свободная зона между островами.
            double left = 340, right = InspectorIsland.Visibility == Visibility.Visible ? 330 : 40;
            r = new Rect(left, 70, Math.Max(200, w - left - right), Math.Max(160, h - 70 - 280));
        }
        else if (step.Target() is { ActualWidth: > 0 } target)
        {
            var b = target.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            r = new Rect(b.X - 6, b.Y - 6, b.Width + 12, b.Height + 12);
        }
        else r = new Rect(w / 2 - 100, h / 2 - 60, 200, 120);
        r = new Rect(Math.Max(0, r.X), Math.Max(0, r.Y), Math.Min(r.Width, w - Math.Max(0, r.X)), Math.Min(r.Height, h - Math.Max(0, r.Y)));

        Place(_tourShade[0], 0, 0, w, r.Y);
        Place(_tourShade[1], 0, r.Bottom, w, h - r.Bottom);
        Place(_tourShade[2], 0, r.Y, r.X, r.Height);
        Place(_tourShade[3], r.Right, r.Y, w - r.Right, r.Height);
        Place(_tourHighlight, r.X, r.Y, r.Width, r.Height);

        _tourCard.Measure(new Size(360, double.PositiveInfinity));
        double cw = 360, ch = Math.Max(160, _tourCard.DesiredSize.Height);
        double x, y;
        if (r.Right + 16 + cw <= w) (x, y) = (r.Right + 16, r.Y);
        else if (r.X - 16 - cw >= 0) (x, y) = (r.X - 16 - cw, r.Y);
        else if (r.Bottom + 16 + ch <= h) (x, y) = (r.X + r.Width / 2 - cw / 2, r.Bottom + 16);
        else (x, y) = (r.X + r.Width / 2 - cw / 2, r.Y - 16 - ch);
        if (step.Canvas) (x, y) = (r.X + r.Width / 2 - cw / 2, r.Y + r.Height / 2 - ch / 2);
        Canvas.SetLeft(_tourCard, Math.Clamp(x, 14, Math.Max(14, w - cw - 14)));
        Canvas.SetTop(_tourCard, Math.Clamp(y, 14, Math.Max(14, h - ch - 14)));

        static void Place(FrameworkElement e, double x, double y, double width, double height)
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            e.Width = Math.Max(0, width);
            e.Height = Math.Max(0, height);
        }
    }

    private void EndTour()
    {
        if (_tourLayer is not null) _tourLayer.Visibility = Visibility.Collapsed;
        _tourSteps = null;
        if (_tourPersist && !_settings.Current.InterfaceTourCompleted) _settings.Update(s => s.InterfaceTourCompleted = true);
    }

    public bool IsTourActive => _tourLayer is { Visibility: Visibility.Visible };

    /// <summary>Первый вход после стартового окна: тур по интерфейсу один раз (отметка в settings.json после завершения или пропуска).</summary>
    private void OfferFirstRunTour()
    {
        if (_settings.Current.InterfaceTourCompleted) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1400);
            if (!AnyDialogOpen && !IsTourActive) StartTour(persist: true);
        });
    }
}
