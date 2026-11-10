using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Photo;
using AIArchitectStudio.ViewModels;
using AIArchitectStudio.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT.Interop;
using static AIArchitectStudio.Views.DialogKit;

namespace AIArchitectStudio;

/// <summary>
/// Волна 4 в главном окне: «Реконструкция по фото и плану» — кнопка в верхнем баре, Drag &amp; Drop картинки из браузера или
/// проводника на 3D-вьюпорт, Ctrl+Shift+V (картинка, ссылка, HTML-фрагмент или файл из буфера обмена), студия подъёма с выбором
/// модели и мгновенной доводкой, закреплённая карточка «Фото-референс» в углу вьюпорта.
/// </summary>
public sealed partial class MainWindow
{
    private Border? _photoRefCard;
    private Image? _photoRefImage;
    private TextBlock? _photoRefTitle;
    private bool _photoStudioOpen;

    private void InitializeWave4()
    {
        // Кнопка «По фото / плану» в левой группе верхнего бара (после «Начального экрана»).
        // Иконка, как у соседних кнопок: подпись «По фото / плану» — в подсказке (в баре нет места для текста, боковые колонки ≈ 330 px).
        var button = new Button { Style = StyleRes("IconButtonStyle"), Content = new FontIcon { Glyph = "\uE722", FontSize = 14 } };
        ToolTipService.SetToolTip(button, L.Get("W4TopButton") + " — " + L.Get("W4TopButtonTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.Get("W4TopButton"));
        button.Click += (_, _) => ViewModel.RequestPhotoStudio(null);
        int index = TopLeftTools.Children.IndexOf(StartScreenButton);
        TopLeftTools.Children.Insert(index + 1, button);
        L.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LocalizationService.Language)) return;
            ToolTipService.SetToolTip(button, L.Get("W4TopButton") + " — " + L.Get("W4TopButtonTip"));
        };

        // Тот же вход с подписью — пункт меню «Открыть».
        foreach (var b in TopLeftTools.Children.OfType<Button>())
            if (b.Flyout is MenuFlyout menu && menu.Items.OfType<MenuFlyoutItem>().Any(i => i.Tag as string == "any"))
            {
                var item = new MenuFlyoutItem { Text = L.Get("W4TopButton") + "…", Icon = new FontIcon { Glyph = "\uE722" } };
                item.Click += (_, _) => ViewModel.RequestPhotoStudio(null);
                menu.Items.Insert(1, item);
                break;
            }

        ViewModel.PhotoStudioRequested += async (_, photo) => await ShowPhotoStudioAsync(photo);

        // Перетаскивание картинки из браузера или проводника на 3D-вьюпорт.
        RootGrid.AllowDrop = true;
        RootGrid.DragOver += (_, e) =>
        {
            if (!PhotoDataPackageReader.MayContainImage(e.DataView) || _photoStudioOpen) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = L.Get("W4DropAccept");
        };
        RootGrid.Drop += async (_, e) =>
        {
            if (_photoStudioOpen) return;
            var deferral = e.GetDeferral();
            LoadedPhoto? photo = null;
            try
            {
                photo = await PhotoDataPackageReader.ReadAsync(e.DataView);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or ArgumentException or IOException or TaskCanceledException or System.Runtime.InteropServices.COMException)
            {
                ViewModel.ShowError(L.Get("W4StudioTitle"), L.Format("W4LoadError", ex.Message), "AIA-PHOTO-DROP", ex.ToString());
            }
            finally
            {
                deferral.Complete();
            }
            if (photo is not null) await ShowPhotoStudioAsync(photo);
        };

        // Ctrl+Shift+V — картинка, ссылка, HTML-фрагмент или файл из буфера обмена.
        var paste = new KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        paste.Invoked += async (_, e) =>
        {
            e.Handled = true;
            await PastePhotoAsync();
        };
        RootGrid.KeyboardAccelerators.Add(paste);

        BuildPhotoReferenceCard();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsPhotoReferenceVisible) or nameof(MainViewModel.PhotoReference)) UpdatePhotoReferenceCard();
        };
        InitializeWave5();
#if DEBUG
        InitializeWave4Debug();
#endif
    }

    private async Task PastePhotoAsync()
    {
        try
        {
            if (await PhotoDataPackageReader.ReadAsync(Clipboard.GetContent()) is { } photo) await ShowPhotoStudioAsync(photo);
            else ViewModel.ShowError(L.Get("W4StudioTitle"), L.Get("W4PasteNoImage"), "AIA-PHOTO-PASTE", L.Get("W4DropHint"));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or ArgumentException or IOException or TaskCanceledException or System.Runtime.InteropServices.COMException)
        {
            ViewModel.ShowError(L.Get("W4StudioTitle"), L.Format("W4LoadError", ex.Message), "AIA-PHOTO-PASTE", ex.ToString());
        }
    }

    /// <summary>Студия подъёма; «Поднять в проект» — выбранным маршрутом (ответ модели сверяется со снимком и проверяется).</summary>
    private async Task ShowPhotoStudioAsync(LoadedPhoto? photo, Func<PhotoToBimDialog, Task>? debug = null)
    {
        if (_photoStudioOpen || AnyDialogOpen) return;
        _photoStudioOpen = true;
        _block34Open = true;
        PhotoLiftRequest? request = null;
        try
        {
            var dialog = new PhotoToBimDialog(ViewModel, WindowNative.GetWindowHandle(this), photo) { XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme };
            if (debug is not null) dialog.Opened += async (_, _) => await debug(dialog);
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) request = dialog.Result;
        }
        finally
        {
            _photoStudioOpen = false;
            _block34Open = false;
        }
        if (request is not null)
            await ViewModel.RunPhotoLiftAsync(request.Route, request.Descriptor, request.Options, request.Jpeg, request.Name, request.VisualCore);
    }

    // ───────────────────────────── Фото-референс ─────────────────────────────

    /// <summary>Стеклянная карточка с исходным снимком в левом нижнем углу 3D-холста.</summary>
    private void BuildPhotoReferenceCard()
    {
        _photoRefImage = new Image { Stretch = Stretch.UniformToFill, Height = 158, Width = 252 };
        var imageFrame = new Border { CornerRadius = new CornerRadius(9), Child = _photoRefImage };
        _photoRefTitle = Text(string.Empty, 10.5, secondary: true);
        _photoRefTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _photoRefTitle.MaxWidth = 200;
        var head = new Grid { ColumnSpacing = 6 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Glyph("\uE722", 12));
        var titles = new StackPanel { Spacing = 0 };
        titles.Children.Add(Text(L.Get("W4RefTitle"), 12, strong: true));
        titles.Children.Add(_photoRefTitle);
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        var hide = new Button { Style = StyleRes("IconButtonStyle"), Content = new FontIcon { Glyph = "\uE711", FontSize = 11 } };
        ToolTipService.SetToolTip(hide, L.Get("W4RefHide"));
        hide.Click += (_, _) => ViewModel.HidePhotoReferenceCommand.Execute(null);
        Grid.SetColumn(hide, 2);
        head.Children.Add(hide);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(head);
        stack.Children.Add(imageFrame);
        _photoRefCard = new Border
        {
            Style = StyleRes("IslandStyle"), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(14, 0, 0, 16), Child = stack, Visibility = Visibility.Collapsed
        };
        Canvas.SetZIndex(_photoRefCard, 40);
        RootGrid.Children.Add(_photoRefCard);
    }

    private void UpdatePhotoReferenceCard()
    {
        if (_photoRefCard is null || _photoRefImage is null || _photoRefTitle is null) return;
        _photoRefCard.Visibility = ViewModel.IsPhotoReferenceVisible ? Visibility.Visible : Visibility.Collapsed;
        if (ViewModel.PhotoReference is not { Length: > 0 } bytes) return;
        _photoRefTitle.Text = ViewModel.PhotoReferenceTitle;
        var bitmap = new BitmapImage();
        _ = LoadBitmapAsync(bitmap, bytes);
        _photoRefImage.Source = bitmap;
    }

#if DEBUG
    /// <summary>Витрина пяти сооружений: снимок над кадром поднятой модели, морфология и K_исп.</summary>
    private async Task ShowShowcaseAsync(List<(byte[] Photo, byte[] Render, int W, int H, string Title, PhotoLiftResult Lift)> items)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        for (int i = 0; i < items.Count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < items.Count; i++)
        {
            var (photo, render, _, _, title, lift) = items[i];
            void Put(UIElement e, int row) { Grid.SetColumn((FrameworkElement)e, i); Grid.SetRow((FrameworkElement)e, row); grid.Children.Add(e); }
            Put(Text($"{i + 1}. {title}", 12, strong: true, wrap: true), 0);
            var p = new BitmapImage(); _ = LoadBitmapAsync(p, photo);
            Put(new Border { CornerRadius = new CornerRadius(8), Child = new Image { Source = p, Stretch = Stretch.UniformToFill, Height = 150 } }, 1);
            var m = new BitmapImage(); _ = LoadBitmapAsync(m, render);
            Put(new Border { CornerRadius = new CornerRadius(8), Child = new Image { Source = m, Stretch = Stretch.UniformToFill, Height = 150 } }, 2);
            var caption = new StackPanel { Spacing = 1 };
            caption.Children.Add(Text($"{PhotoToBimService.MorphologyTitle(lift.Model.Morphology)} · этажей {lift.Model.Floors}", 11, secondary: true, wrap: true));
            var k = Text($"K_исп {lift.MaxUtilization.ToString("0.00", L.Culture)} · ΣR = ΣF · висящих {lift.Hanging}", 12, strong: true);
            k.Foreground = Hex(lift.IsStructurallySound ? "#30D158" : "#FF9F0A");
            caption.Children.Add(k);
            Put(caption, 3);
        }
        var body = new StackPanel { Spacing = 10, Width = 1500 };
        body.Children.Add(Text(L.Get("W4ShowcaseHint"), 11, secondary: true, wrap: true));
        var labels = new Grid();
        body.Children.Add(grid);
        var dialog = new ContentDialog
        {
            Title = L.Get("W4Showcase"), Content = body, CloseButtonText = L.Get("W4Close"), XamlRoot = Content.XamlRoot, RequestedTheme = DialogTheme,
            Background = Res("IslandBrush"), CornerRadius = new CornerRadius(14)
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1600.0;
        await dialog.ShowAsync();
    }
#endif

#if DEBUG
    /// <summary>Ракурс как на снимке: спереди-слева (фронт здания — сторона −Z), чуть сверху, по габариту модели.</summary>
    private void FrontCamera(PhotoBimModel model, double azimuthDeg = -32, double elevationDeg = 16)
    {
        var pts = model.Elements.Where(e => e.Category is not (BimCategory.Window or BimCategory.Door)).SelectMany(e => AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(e)).ToList();
        if (pts.Count == 0) return;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
        double top = model.Elements.Max(e => e.TopY);
        var c = new SharpDX.Vector3((float)((minX + maxX) / 2), (float)(top * 0.45), (float)((minZ + maxZ) / 2));
        double r = Math.Max(Math.Max(maxX - minX, maxZ - minZ), top) / 2;
        // Высокие здания: высота должна войти в вертикальный угол обзора вьюпорта (≈ 27°).
        double dist = Math.Max(r * 3.6, top * 2.9), az = azimuthDeg * Math.PI / 180, el = elevationDeg * Math.PI / 180;
        var eye = c + new SharpDX.Vector3((float)(Math.Sin(az) * Math.Cos(el) * dist), (float)(Math.Sin(el) * dist), (float)(-Math.Cos(az) * Math.Cos(el) * dist));
        Viewport.DebugSetCamera(eye, c);
    }

    /// <summary>Азимут ракурса как у демонстрационного снимка морфологии (градусы; 0 — строго с фронта −Z, плюс — справа).</summary>
    private static double CameraAzimuth(PhotoMorphology m) => m switch
    {
        PhotoMorphology.Triangular => 4,
        PhotoMorphology.Cascading => 30,
        PhotoMorphology.Rotunda => -16,
        PhotoMorphology.ArchSpan => -36,
        _ => -28
    };
#endif

    private static async Task LoadBitmapAsync(BitmapImage bitmap, byte[] bytes)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
    }

#if DEBUG
    private void InitializeWave4Debug()
    {
        InitializeWave41Debug();
        // AIA_DEBUG_PHOTOBIM=путь — студия «Реконструкция по фото и плану» со снимком (проверка каркаса готова, превью повёрнуто).
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOBIM") is { Length: > 0 } studioPath)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                await ShowPhotoStudioAsync(await PhotoImageLoader.FromFileAsync(studioPath), d => d.DebugWaitReadyAsync(-38, 22));
            };
        // AIA_DEBUG_PHOTOINSERT=путь[;structural] — подъём Визуальным ядром и вставка в проект с карточкой «Фото-референс»;
        // structural — режим «Конструкции» с самой нагруженной колонной и окном «Аудит расчётной схемы».
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOINSERT") is { Length: > 0 } insertSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                var parts = insertSpec.Split(';');
                var photo = await PhotoImageLoader.FromFileAsync(parts[0]);
                var (descriptor, _, jpeg) = await PhotoToBimService.AnalyzeAsync(photo);
                var lift = await Task.Run(() => ViewModel.LiftPhoto(descriptor, new PhotoLiftOptions { GroupName = Path.GetFileNameWithoutExtension(photo.Name) }));
                ViewModel.InsertPhotoResult(lift, jpeg, photo.Name);
                // AIA_DEBUG_PHOTOCAM=x,y,z;tx,ty,tz — ракурс после «Показать всё».
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOCAM") is null) { await Task.Delay(1800); FrontCamera(lift.Model, CameraAzimuth(lift.Model.Morphology)); }
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOCAM") is { Length: > 0 } cam)
                {
                    await Task.Delay(1800);
                    var v = cam.Split(',', ';').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    Viewport.DebugSetCamera(new SharpDX.Vector3(v[0], v[1], v[2]), new SharpDX.Vector3(v[3], v[4], v[5]));
                }
                if (parts.Length > 1 && parts[1] == "structural")
                {
                    await Task.Delay(1500);
                    if (ViewModel.MostLoaded(BimCategory.Column) is { } column) ViewModel.SelectElement(column);
                    ViewModel.IsStructuralMode = true;
                    await Task.Delay(1500);
                    await ViewModel.RunSchemeAuditCommand.ExecuteAsync(null);
                }
                else if (parts.Length > 1 && parts[1] == "select" && ViewModel.Scene.Elements.Where(e => CurvedDesign.IsArc(e)).MaxBy(e => e.Width) is { } arc)
                    ViewModel.SelectElement(arc);
            };
        // AIA_DEBUG_PHOTOSHOWCASE=папка — витрина: пять снимков DemoKit и поднятые по ним модели (кадры вьюпорта) с K_исп.
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOSHOWCASE") is { Length: > 0 } showcaseDir)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                var items = new List<(byte[] Photo, byte[] Render, int W, int H, string Title, PhotoLiftResult Lift)>();
                foreach (var (file, title) in PhotoSceneStudio.Photos)
                {
                    var photo = await PhotoImageLoader.FromFileAsync(Path.Combine(showcaseDir, file));
                    var (descriptor, _, jpeg) = await PhotoToBimService.AnalyzeAsync(photo);
                    var lift = await Task.Run(() => ViewModel.LiftPhoto(descriptor, new PhotoLiftOptions { GroupName = title }));
                    ViewModel.InsertPhotoResult(lift, jpeg, photo.Name);
                    await Task.Delay(1800);
                    FrontCamera(lift.Model, CameraAzimuth(lift.Model.Morphology));
                    await Task.Delay(1200);
                    if (ViewModel.ViewportSnapshotProvider is { } snap && await snap(900) is { } frame)
                        items.Add((jpeg, await PhotoImageLoader.EncodeAsync(frame.Bgra, frame.Width, frame.Height, png: false), frame.Width, frame.Height, title, lift));
                }
                ViewModel.IsPhotoReferenceHidden = true;
                await ShowShowcaseAsync(items);
            };
        // AIA_DEBUG_GENPHOTOS=папка — фотореалистичные рендеры пяти демонстрационных сооружений (PNG 1280 × 800) и выход при EXIT=1.
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_GENPHOTOS") is { Length: > 0 } photoDir)
            RootGrid.Loaded += async (_, _) =>
            {
                Directory.CreateDirectory(photoDir);
                for (int i = 0; i < PhotoSceneStudio.Photos.Length; i++)
                {
                    var bgra = await Task.Run(() => PhotoSceneStudio.Render(i));
                    var png = await PhotoImageLoader.EncodeAsync(bgra, PhotoSceneStudio.Width, PhotoSceneStudio.Height, png: true);
                    await File.WriteAllBytesAsync(Path.Combine(photoDir, PhotoSceneStudio.Photos[i].File), png);
                }
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOLIFT") is null && Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
            };
        // AIA_DEBUG_PHOTOLIFT=папка — анализ и подъём всех PNG/JPG папки, отчёт photolift.txt (морфология, паспорт, K_исп, статика).
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOLIFT") is { Length: > 0 } liftDir)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(Environment.GetEnvironmentVariable("AIA_DEBUG_GENPHOTOS") is null ? 500 : 30000);
                var sb = new System.Text.StringBuilder();
                string only = Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOONLY") ?? string.Empty;
                bool verifyGate = Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOVERIFY") != "0";
                foreach (var file in Directory.GetFiles(liftDir).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).Where(f => only.Length == 0 || Path.GetFileName(f).Contains(only)).OrderBy(f => f))
                {
                    try
                    {
                        var photo = await PhotoImageLoader.FromFileAsync(file);
                        var (descriptor, _, _) = await PhotoToBimService.AnalyzeAsync(photo);
                        sb.AppendLine("=== " + Path.GetFileName(file));
                        if (PhotoArchitectureAnalyzer.LastMask is { } lm)
                        {
                            var px = new byte[lm.W * lm.H * 4];
                            for (int i = 0; i < lm.Mask.Length; i++) { byte v = lm.Mask[i] ? (byte)255 : (byte)40; px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 255; }
                            await File.WriteAllBytesAsync(Path.Combine(liftDir, "mask_" + Path.GetFileNameWithoutExtension(file) + ".png"), await PhotoImageLoader.EncodeAsync(px, lm.W, lm.H, true));
                        }
                        if (PhotoArchitectureAnalyzer.LastComposite is { } lc)
                            await File.WriteAllBytesAsync(Path.Combine(liftDir, "dbg_" + Path.GetFileNameWithoutExtension(file) + ".png"), await PhotoImageLoader.EncodeAsync(lc.Bgra, lc.W, lc.H, true));
                        sb.AppendLine(descriptor.ToPromptMatrix());
                        sb.AppendLine(PhotoArchitectureAnalyzer.LastTrace);
                        if (Environment.GetEnvironmentVariable("AIA_DEBUG_PHOTOPROFILE") == "1")
                        {
                            var probe = PhotoBimSynthesizer.Build(descriptor);
                            PhotoToBimService.PreSize(probe.Elements, probe.Groups, new Models.StructuralLoadSettings());
                            var sbx = Services.Verification.DesignFixEngine.Sandbox(probe.Elements, probe.Groups, probe.Axes);
                            var vctx = Services.Verification.VerificationContext.FromScene(sbx, new Models.StructuralLoadSettings(), null);
                            foreach (var v in Services.Verification.BimVerificationPipeline.CreateDefault().Verifiers)
                            {
                                var swv = System.Diagnostics.Stopwatch.StartNew();
                                var task = Task.Run(() => v.Verify(vctx).Count());
                                bool done = await Task.WhenAny(task, Task.Delay(60000)) == task;
                                sb.AppendLine($"  profile {v.Id}: {(done ? task.Result + " findings" : "TIMEOUT")} {swv.ElapsedMilliseconds} ms");
                                await File.WriteAllTextAsync(Path.Combine(liftDir, "photolift.txt"), sb.ToString(), new System.Text.UTF8Encoding(true));
                            }
                            continue;
                        }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var lift = await Task.Run(() => PhotoToBimService.Lift(descriptor, null, null, verify: verifyGate, monotonicity: true));
                        sb.AppendLine($"lift {sw.ElapsedMilliseconds} ms, elements {lift.Model.Elements.Count}");
                        foreach (var line in lift.Log) sb.AppendLine("  " + line);
                        foreach (var c in lift.Audit?.Criteria ?? []) sb.AppendLine($"  audit {c.Number} {(c.Passed ? "OK" : "FAIL")} {c.Title}: {c.Detail}");
                        sb.AppendLine("  traits: " + PhotoFidelityEnforcer.Traits(lift.Model.Elements).Describe());
                        await File.WriteAllTextAsync(Path.Combine(liftDir, "photolift.txt"), sb.ToString(), new System.Text.UTF8Encoding(true));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("=== " + Path.GetFileName(file) + " EXCEPTION " + ex);
                    }
                }
                await File.WriteAllTextAsync(Path.Combine(liftDir, "photolift.txt"), sb.ToString(), new System.Text.UTF8Encoding(true));
                if (Environment.GetEnvironmentVariable("AIA_DEBUG_EXIT") == "1") Environment.Exit(0);
            };
    }
#endif
}
