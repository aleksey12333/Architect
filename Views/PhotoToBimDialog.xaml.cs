using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Photo;
using AIArchitectStudio.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio.Views;

/// <summary>Что выбрал пользователь в студии: снимок, паспорт, доводка, маршрут модели и уже проверенный результат Визуального ядра.</summary>
public sealed record PhotoLiftRequest(PhotoLiftRoute Route, PhotoArchitectureDescriptor Descriptor, PhotoLiftOptions Options, byte[] Jpeg, string Name, PhotoLiftResult? VisualCore);

/// <summary>
/// Студия «Реконструкция по фото и плану». Шапка — выбор модели подъёма (Авто: Визуальное ядро + Локальная модель, основная
/// локальная модель, Совет КБ, веб-проектировщик); источник — ссылка на фото из интернета (URL или страница с картинкой), файл,
/// буфер обмена, перетаскивание на окно; слева — снимок с оверлеями Визуального ядра (силуэт, ярусы, линии этажей, векторы
/// кривизны, точки схода, кружки палитры), справа — вращаемое мышью 3D-превью модели в цветах снимка и сводка «Этажей · Морфология ·
/// K_исп · Смета каркаса»; внизу — профиль здания, этажность 1…50, кривизна 0…100 %, доля остекления 10…95 %, покрытие.
/// Синтез перестраивается сразу (миллисекунды), проверка каркаса — в фоне после паузы в правках.
/// </summary>
public sealed partial class PhotoToBimDialog : ContentDialog
{
    private const int PreviewW = 600, PreviewH = 400, SourceW = 600, SourceH = 400;
    private readonly MainViewModel _vm;
    private readonly LocalizationService _loc;
    private readonly nint _hwnd;
    private readonly HttpMessageHandler? _handler;
    private PhotoArchitectureDescriptor? _descriptor;
    private byte[]? _jpeg;
    private string _name = string.Empty;
    private PhotoBimModel? _model;
    private PhotoLiftResult? _verified;
    private int _version;
    private double _azimuth = -35, _elevation = 24;
    private bool _dragging, _suppress;
    private Point _dragStart;

    private TextBox _url = null!;
    private ComboBox _route = null!, _profile = null!, _roof = null!;
    private Slider _floors = null!, _curvature = null!, _glazing = null!;
    private TextBlock _floorsText = null!, _curvText = null!, _glazText = null!, _summary = null!, _check = null!, _status = null!, _timing = null!;
    private Image _source = null!, _preview = null!;
    private Canvas _overlay = null!;
    private StackPanel _evidence = null!, _palette = null!;
    private ProgressRing _busy = null!;

    public PhotoLiftRequest? Result { get; private set; }

    public PhotoToBimDialog(MainViewModel viewModel, nint hwnd, LoadedPhoto? initial = null, HttpMessageHandler? handler = null)
    {
        _vm = viewModel;
        _loc = viewModel.Loc;
        _hwnd = hwnd;
        _handler = handler;
        InitializeComponent();
        PrimaryButtonText = _loc.Get("W4Lift");
        CloseButtonText = _loc.Get("W4Close");
        IsPrimaryButtonEnabled = false;
        BuildHeader();
        BuildSource();
        BuildBody();
        BuildControls();
        PrimaryButtonClick += OnPrimary;
        DragOver += (_, e) =>
        {
            if (!PhotoDataPackageReader.MayContainImage(e.DataView)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _loc.Get("W4DropAccept");
        };
        Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                if (await PhotoDataPackageReader.ReadAsync(e.DataView, _handler) is { } photo) await LoadAsync(photo);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or ArgumentException or IOException or TaskCanceledException or System.Runtime.InteropServices.COMException)
            {
                SetStatus(_loc.Format("W4LoadError", ex.Message), error: true);
            }
            finally
            {
                deferral.Complete();
            }
        };
        Opened += async (_, _) =>
        {
            FocusSink.Focus(FocusState.Programmatic);
            if (initial is not null) await LoadAsync(initial);
        };
    }

    private CultureInfo Culture => _loc.Culture;

    // ───────────────────────────── Разметка ─────────────────────────────

    private void BuildHeader()
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(GlyphBadge("\uE722", 38));
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        titles.Children.Add(Text(_loc.Get("W4StudioTitle"), 17, strong: true));
        titles.Children.Add(Text(_loc.Get("W4StudioSubtitle"), 11, secondary: true, wrap: true));
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        var route = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        route.Children.Add(Caption(_loc.Get("W4Route")));
        _route = new ComboBox { Width = 330, FontSize = 12 };
        foreach (var key in new[] { "W4RouteAuto", "W4RoutePrimary", "W4RouteCouncil", "W4RouteWeb" }) _route.Items.Add(_loc.Get(key));
        // Волна 4.1: облачный сервис сеанса (ключ в переменных окружения) — маршрут по умолчанию: Sonnet 5.5, затем GPT 5.6 Luna.
        bool cloud = Services.AI.UnifiedCloudAiClient.Shared is not null;
        if (cloud) { _route.Items.Add(_loc.Get("W41RouteCloud")); _route.Items.Add(_loc.Get("W41RouteCloudLuna")); }
        _route.SelectedIndex = cloud ? (int)PhotoLiftRoute.Cloud : 0;
        route.Children.Add(_route);
        Grid.SetColumn(route, 2);
        grid.Children.Add(route);
        HeaderHost.Children.Add(grid);
    }

    private void BuildSource()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _url = new TextBox { PlaceholderText = _loc.Get("W4UrlPlaceholder"), FontSize = 12, FontFamily = Mono };
        _url.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await LoadUrlAsync(); };
        grid.Children.Add(_url);
        var load = ActionButton("\uE896", _loc.Get("W4Load"), async (_, _) => await LoadUrlAsync(), accent: true);
        Grid.SetColumn(load, 1);
        grid.Children.Add(load);
        var file = ActionButton("\uE8E5", _loc.Get("W4PickFile"), async (_, _) => await PickFileAsync());
        Grid.SetColumn(file, 2);
        grid.Children.Add(file);
        var paste = ActionButton("\uE77F", _loc.Get("W4Paste"), async (_, _) => await PasteAsync());
        Grid.SetColumn(paste, 3);
        grid.Children.Add(paste);
        _busy = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_busy, 4);
        grid.Children.Add(_busy);
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(grid);
        _status = Text(_loc.Get("W4DropHint"), 11, secondary: true, wrap: true);
        stack.Children.Add(_status);
        SourceHost.Children.Add(stack);
    }

    private void BuildBody()
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(Caption(_loc.Get("W4Original")));
        var frame = new Grid { Width = SourceW, Height = SourceH, CornerRadius = new CornerRadius(10), Background = Res("TrackBrush") };
        _source = new Image { Stretch = Stretch.Uniform };
        _overlay = new Canvas { Width = SourceW, Height = SourceH, IsHitTestVisible = false };
        frame.Children.Add(_source);
        frame.Children.Add(_overlay);
        left.Children.Add(frame);
        left.Children.Add(Caption(_loc.Get("W4Palette")));
        _palette = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        left.Children.Add(_palette);
        grid.Children.Add(left);

        var right = new StackPanel { Spacing = 6 };
        right.Children.Add(Caption(_loc.Get("W4Preview")));
        var pframe = new Grid { Width = PreviewW, Height = PreviewH, CornerRadius = new CornerRadius(10), Background = Res("TrackBrush") };
        _preview = new Image { Stretch = Stretch.Uniform };
        pframe.Children.Add(_preview);
        pframe.PointerPressed += (s, e) =>
        {
            _dragging = true;
            _dragStart = e.GetCurrentPoint(pframe).Position;
            pframe.CapturePointer(e.Pointer);
        };
        pframe.PointerMoved += (s, e) =>
        {
            if (!_dragging) return;
            var p = e.GetCurrentPoint(pframe).Position;
            _azimuth += (p.X - _dragStart.X) * 0.6;
            _elevation = Math.Clamp(_elevation + (p.Y - _dragStart.Y) * 0.3, 6, 80);
            _dragStart = p;
            _ = RenderPreviewAsync(fast: true);
        };
        pframe.PointerReleased += (s, e) =>
        {
            _dragging = false;
            pframe.ReleasePointerCapture(e.Pointer);
            _ = RenderPreviewAsync(fast: false);
        };
        right.Children.Add(pframe);
        _summary = Text(string.Empty, 13, strong: true, wrap: true);
        right.Children.Add(_summary);
        _check = Text(string.Empty, 11, secondary: true, wrap: true);
        right.Children.Add(_check);
        _timing = Text(string.Empty, 10.5, secondary: true);
        right.Children.Add(_timing);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        BodyHost.Children.Add(grid);
    }

    private void BuildControls()
    {
        var grid = new Grid { ColumnSpacing = 14 };
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StackPanel Column(string caption, UIElement control, TextBlock? value = null)
        {
            var s = new StackPanel { Spacing = 2 };
            var head = new Grid();
            head.Children.Add(Caption(caption));
            if (value is not null) { value.HorizontalAlignment = HorizontalAlignment.Right; head.Children.Add(value); }
            s.Children.Add(head);
            s.Children.Add(control);
            return s;
        }
        _profile = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        foreach (var key in new[] { "W4ProfAuto", "W4ProfCurved", "W4ProfRounded", "W4ProfCascade", "W4ProfTriangle", "W4ProfRotunda", "W4ProfArch", "W4ProfPitched", "W4ProfBox" })
            _profile.Items.Add(_loc.Get(key));
        _profile.SelectedIndex = 0;
        _profile.SelectionChanged += (_, _) => OnControlsChanged();
        _floors = new Slider { Minimum = 1, Maximum = 50, StepFrequency = 1, Value = 10 };
        _floors.ValueChanged += (_, _) => OnControlsChanged(floorsTouched: true);
        _curvature = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = 50 };
        _curvature.ValueChanged += (_, _) => OnControlsChanged(curvTouched: true);
        _glazing = new Slider { Minimum = 10, Maximum = 95, StepFrequency = 1, Value = 40 };
        _glazing.ValueChanged += (_, _) => OnControlsChanged(glazTouched: true);
        _roof = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        foreach (var key in new[] { "W4RoofAuto", "W4RoofFlat", "W4RoofParapet", "W4RoofDome", "W4RoofVault", "W4RoofGable" }) _roof.Items.Add(_loc.Get(key));
        _roof.SelectedIndex = 0;
        _roof.SelectionChanged += (_, _) => OnControlsChanged();
        _floorsText = Text("—", 11, mono: true);
        _curvText = Text("—", 11, mono: true);
        _glazText = Text("—", 11, mono: true);
        var cols = new UIElement[]
        {
            Column(_loc.Get("W4Profile"), _profile), Column(_loc.Get("W4Floors"), _floors, _floorsText), Column(_loc.Get("W4Curvature"), _curvature, _curvText),
            Column(_loc.Get("W4Glazing"), _glazing, _glazText), Column(_loc.Get("W4Roof"), _roof)
        };
        for (int i = 0; i < cols.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)cols[i], i);
            grid.Children.Add(cols[i]);
        }
        ControlsHost.Children.Add(Card(grid, 12));
        FooterHost.Children.Add(Caption(_loc.Get("W4Evidence")));
        _evidence = new StackPanel { Spacing = 2 };
        FooterHost.Children.Add(_evidence);
    }

    // ───────────────────────────── Источник ─────────────────────────────

    private async Task LoadUrlAsync()
    {
        string url = _url.Text.Trim();
        if (url.Length == 0) return;
        await GuardAsync(async () => await LoadAsync(await PhotoImageLoader.FromUrlAsync(url, _handler)));
    }

    private async Task PickFileAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await GuardAsync(async () => await LoadAsync(await PhotoImageLoader.FromFileAsync(file.Path)));
    }

    private async Task PasteAsync() => await GuardAsync(async () =>
    {
        if (await PhotoDataPackageReader.ReadAsync(Clipboard.GetContent(), _handler) is { } photo) await LoadAsync(photo);
        else SetStatus(_loc.Get("W4PasteNoImage"), error: true);
    });

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or ArgumentException or IOException or TaskCanceledException or UriFormatException
                                       or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            SetBusy(false);
            SetStatus(_loc.Format("W4LoadError", ex.Message), error: true);
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = error ? Hex("#FF453A") : Res("TextSecondaryBrush");
    }

    private void SetBusy(bool busy)
    {
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Загрузка снимка: нормализация, анализ Визуальным ядром, оверлеи, значения ползунков по паспорту, синтез и превью.</summary>
    public async Task LoadAsync(LoadedPhoto photo)
    {
        SetBusy(true);
        SetStatus(_loc.Get("W4Analyzing"));
        var (descriptor, raster, jpeg) = await PhotoToBimService.AnalyzeAsync(photo);
        _descriptor = descriptor;
        _jpeg = jpeg;
        _name = photo.Name;
        if (photo.SourceUrl is { } u && _url.Text.Length == 0) _url.Text = u;
        var scaled = PhotoImageLoader.Resize(raster, Math.Min(raster.Width, 1200), (int)Math.Round(raster.Height * Math.Min(1.0, 1200.0 / raster.Width)));
        _source.Source = ToBitmap(scaled.Bgra, scaled.Width, scaled.Height);
        DrawOverlays(descriptor, scaled.Width, scaled.Height);
        _suppress = true;
        _profile.SelectedIndex = 0;
        _roof.SelectedIndex = 0;
        _floors.Value = Math.Clamp(descriptor.Floors, 1, 50);
        _curvature.Value = Math.Round(Math.Clamp(descriptor.ArcBulgeRatio / 0.3, 0, 1) * 100);
        _glazing.Value = Math.Round(Math.Clamp(descriptor.GlazingRatio, 0.1, 0.95) * 100);
        _suppress = false;
        _floorsTouched = _curvTouched = _glazTouched = false;
        SetStatus(string.Create(Culture, $"{photo.Name} · {descriptor.PixelWidth} × {descriptor.PixelHeight} px · {PhotoToBimService.MorphologyTitle(descriptor.Morphology)} · " +
                                         $"{_loc.Get("W4Engine")} {descriptor.AnalysisMs:0} мс"));
        _evidence.Children.Clear();
        foreach (var line in descriptor.Evidence) _evidence.Children.Add(Text("· " + line, 11, secondary: true, wrap: true));
        _palette.Children.Clear();
        foreach (var c in descriptor.Palette.Take(6))
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            chip.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = Hex(c.Hex), Stroke = Res("StudioBorderBrush"), StrokeThickness = 1 });
            chip.Children.Add(Text($"{c.Hex} · {RoleTitle(c.Role)}", 10.5, mono: true));
            _palette.Children.Add(chip);
        }
        await RebuildAsync();
        SetBusy(false);
    }

    private bool _floorsTouched, _curvTouched, _glazTouched;

    private static readonly PhotoMorphology[] Profiles =
    [
        PhotoMorphology.Auto, PhotoMorphology.Curved, PhotoMorphology.Rounded, PhotoMorphology.Cascading, PhotoMorphology.Triangular,
        PhotoMorphology.Rotunda, PhotoMorphology.ArchSpan, PhotoMorphology.Pitched, PhotoMorphology.Box
    ];

    private static readonly PhotoRoofKind?[] Roofs = [null, PhotoRoofKind.Flat, PhotoRoofKind.Parapet, PhotoRoofKind.Dome, PhotoRoofKind.Vault, PhotoRoofKind.Gable];

    private PhotoLiftOptions Options => new()
    {
        Profile = Profiles[Math.Max(0, _profile.SelectedIndex)],
        Floors = _floorsTouched ? (int)_floors.Value : null,
        Curvature = _curvTouched ? _curvature.Value / 100 : null,
        Glazing = _glazTouched ? _glazing.Value / 100 : null,
        Roof = Roofs[Math.Max(0, _roof.SelectedIndex)],
        GroupName = string.IsNullOrWhiteSpace(_name) ? "По фото" : System.IO.Path.GetFileNameWithoutExtension(_name)
    };

    private void OnControlsChanged(bool floorsTouched = false, bool curvTouched = false, bool glazTouched = false)
    {
        if (_floorsText is null || _curvText is null || _glazText is null) return;
        _floorsText.Text = ((int)_floors.Value).ToString(Culture);
        _curvText.Text = $"{_curvature.Value:0} %";
        _glazText.Text = $"{_glazing.Value:0} %";
        if (_suppress || _descriptor is null) return;
        _floorsTouched |= floorsTouched;
        _curvTouched |= curvTouched;
        _glazTouched |= glazTouched;
        _ = RebuildAsync();
    }

    /// <summary>Синтез сразу (без проверки — миллисекунды), превью; проверка каркаса — в фоне через 300 мс тишины.</summary>
    private async Task RebuildAsync()
    {
        if (_descriptor is null) return;
        int version = ++_version;
        var options = Options;
        var descriptor = _descriptor;
        var sw = Stopwatch.StartNew();
        var model = PhotoBimSynthesizer.Build(descriptor, options);
        double synthMs = sw.Elapsed.TotalMilliseconds;
        _model = model;
        _verified = null;
        IsPrimaryButtonEnabled = true;
        _timing.Text = string.Create(Culture, $"{_loc.Get("W4Engine")}: перестроение {synthMs:0.0} мс · элементов {model.Elements.Count}");
        _summary.Text = _loc.Format("W4Summary", model.Floors, PhotoToBimService.MorphologyTitle(model.Morphology), "…", "…");
        _check.Text = _loc.Get("W4Checking");
        await RenderPreviewAsync(fast: false);
        await Task.Delay(300);
        if (version != _version) return;
        var loads = _vm.PhotoLoads;
        var lift = await Task.Run(() => PhotoToBimService.Lift(descriptor, options, loads, verify: true));
        if (version != _version) return;
        _verified = lift;
        _model = lift.Model;
        _summary.Text = _loc.Format("W4Summary", lift.Model.Floors, PhotoToBimService.MorphologyTitle(lift.Model.Morphology),
            lift.MaxUtilization.ToString("0.00", Culture), lift.FrameCostRub.ToString("N0", Culture));
        _summary.Foreground = Hex(lift.IsStructurallySound ? "#30D158" : "#FF9F0A");
        int clean = lift.Audit?.Criteria.Count(c => c.Passed) ?? 0;
        string balance = lift.Audit is { } a ? string.Create(Culture, $"{a.AppliedLoad:N0} = {a.GroundReaction:N0} кН") : "—";
        _check.Text = _loc.Format("W4Checked", balance, lift.Hanging, lift.DoorColumnClashes, $"{clean}/{lift.Audit?.Criteria.Count ?? 0}");
        _timing.Text = string.Create(Culture, $"{_loc.Get("W4Engine")}: перестроение {synthMs:0.0} мс · проверка каркаса {lift.ElapsedMs:0} мс · элементов {lift.Model.Elements.Count}");
        await RenderPreviewAsync(fast: false);
    }

    private bool _rendering, _renderAgain;

    private async Task RenderPreviewAsync(bool fast)
    {
        if (_model is null) return;
        if (_rendering) { _renderAgain = true; return; }
        _rendering = true;
        try
        {
            do
            {
                _renderAgain = false;
                var elements = _model.Elements.ToList();
                double az = _azimuth, el = _elevation;
                bool dark = ActualTheme == ElementTheme.Dark;
                var px = await Task.Run(() => ElementPreviewRenderer.Render(elements, PreviewW, PreviewH, az, el, dark, fast ? 1 : 2, shadows: !fast));
                _preview.Source = ToBitmap(px, PreviewW, PreviewH);
            } while (_renderAgain);
        }
        finally
        {
            _rendering = false;
        }
    }

    // ───────────────────────────── Оверлеи ─────────────────────────────

    private void DrawOverlays(PhotoArchitectureDescriptor d, int imageW, int imageH)
    {
        _overlay.Children.Clear();
        double scale = Math.Min((double)SourceW / imageW, (double)SourceH / imageH);
        double ox = (SourceW - imageW * scale) / 2, oy = (SourceH - imageH * scale) / 2, w = imageW * scale, h = imageH * scale;
        Point P(double x, double y) => new(ox + x * w, oy + y * h);
        var accent = (Brush)Res("AccentBrush");
        // Волна 4.1: отфильтрованные помехи — растительность перед фасадом (зелёным) и линия тротуара, до которой достроены грани.
        var veg = Hex("#30D158", 0x55);
        foreach (var (x0, y0, x1, y1) in d.ClutterCells)
        {
            var a = P(x0, y0); var b = P(x1, y1);
            var cell = new Rectangle { Width = Math.Max(1, b.X - a.X), Height = Math.Max(1, b.Y - a.Y), Fill = veg };
            Canvas.SetLeft(cell, a.X);
            Canvas.SetTop(cell, a.Y);
            _overlay.Children.Add(cell);
        }
        if (d.BaseLineY0 is { } by0 && d.BaseLineY1 is { } by1)
        {
            var a = P(0, by0); var b = P(1, by1);
            _overlay.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Hex("#FF9F0A"), StrokeThickness = 2, StrokeDashArray = [5, 3] });
        }
        // Силуэт (верх и низ).
        var top = new Polyline { Stroke = accent, StrokeThickness = 2 };
        foreach (var (x, y) in d.TopProfile) top.Points.Add(P(x, y));
        _overlay.Children.Add(top);
        var bottom = new Polyline { Stroke = accent, StrokeThickness = 1.2, StrokeDashArray = [3, 2] };
        foreach (var (x, y) in d.BottomProfile) bottom.Points.Add(P(x, y));
        _overlay.Children.Add(bottom);
        // Линии этажей.
        foreach (double y in d.FloorLines)
        {
            var a = P(d.BoxX0, y); var b = P(d.BoxX1, y);
            _overlay.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Hex("#FFFFFF", 0x55), StrokeThickness = 0.8 });
        }
        // Ярусы.
        foreach (var t in d.Tiers)
        {
            double y = d.BoxBase - t.TopFraction * (d.BoxBase - d.BoxTop);
            var a = P(t.X0, y); var b = P(t.X1, y);
            _overlay.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Hex("#FF9F0A"), StrokeThickness = 2.5 });
        }
        // Векторы кривизны и линии схода.
        foreach (var (from, to) in d.CurvatureVectors)
        {
            var a = P(from.X, from.Y); var b = P(to.X, to.Y);
            _overlay.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Hex("#30D158"), StrokeThickness = 2 });
            _overlay.Children.Add(Dot(b, 3, "#30D158"));
        }
        foreach (var (from, to) in d.VanishingLines)
        {
            var a = P(from.X, from.Y); var b = P(to.X, to.Y);
            _overlay.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Hex("#BF5AF2"), StrokeThickness = 1.2, StrokeDashArray = [4, 3] });
        }
        // Кружки извлечённых цветов — в центрах их областей.
        foreach (var c in d.Palette.Take(6))
        {
            var p = P(c.X, c.Y);
            var dot = Dot(p, 9, c.Hex);
            dot.Stroke = new SolidColorBrush(Colors.White);
            dot.StrokeThickness = 2;
            _overlay.Children.Add(dot);
        }
    }

    private string RoleTitle(string role) => _loc.IsRussian
        ? role switch { "wall" => "стена", "glass" => "стекло", "roof" => "кровля", "accent" => "акцент", "frame" => "обвязка", _ => "фасад" }
        : role;

    private static Ellipse Dot(Point p, double r, string hex)
    {
        var e = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = Hex(hex) };
        Canvas.SetLeft(e, p.X - r);
        Canvas.SetTop(e, p.Y - r);
        return e;
    }

    private static WriteableBitmap ToBitmap(byte[] bgra, int w, int h)
    {
        var wb = new WriteableBitmap(w, h);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(bgra, 0, Math.Min(bgra.Length, w * h * 4));
        wb.Invalidate();
        return wb;
    }

    // ───────────────────────────── Итог ─────────────────────────────

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_descriptor is null || _jpeg is null) { args.Cancel = true; return; }
        var route = (PhotoLiftRoute)Math.Max(0, _route.SelectedIndex);
        var verified = _verified ?? PhotoToBimService.Lift(_descriptor, Options, _vm.PhotoLoads, verify: true);
        Result = new PhotoLiftRequest(route, _descriptor, Options, _jpeg, _name, verified);
    }

#if DEBUG
    /// <summary>Только Debug-съёмка: ждать готовности проверки каркаса и повернуть превью.</summary>
    public async Task DebugWaitReadyAsync(double azimuth, double elevation)
    {
        for (int i = 0; i < 200 && _verified is null; i++) await Task.Delay(100);
        _azimuth = azimuth;
        _elevation = elevation;
        await RenderPreviewAsync(fast: false);
    }

    public void DebugSetRoute(PhotoLiftRoute route) => _route.SelectedIndex = (int)route;
#endif
}
