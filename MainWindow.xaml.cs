using System.Collections.Specialized;
using System.ComponentModel;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.ViewModels;
using AIArchitectStudio.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace AIArchitectStudio;

public sealed partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;

    public MainViewModel ViewModel { get; }

    /// <summary>Короткий доступ к локализации для x:Bind.</summary>
    public LocalizationService L => ViewModel.Loc;

    public MainWindow(MainViewModel viewModel, ThemeService theme, SettingsService settings)
    {
        ViewModel = viewModel;
        _theme = theme;
        _settings = settings;
        InitializeComponent();

        ConfigureTitleBar();
        _theme.AttachWindow(this);
        Title = ViewModel.WindowTitle;
        Viewport.Initialize(ViewModel);
        WebBridgePanel.Initialize(ViewModel);
        ReviewPanel.Initialize(ViewModel);
        ViewModel.DxfLiftVerifier = VerifyDxfLiftAsync;
        // Авто-снимок вида: чистый кадр вьюпорта (без Gizmo, привязок, ViewCube) для запроса проектировщику.
        ViewModel.ViewportSnapshotProvider = longSide => Viewport.CaptureViewSnapshotAsync(longSide);
        ViewModel.AttachFileRequested += OnAttachFileRequested;
        ViewModel.PlanReconstructionRequested += OnPlanReconstructionRequested;
        ViewModel.StartScreenRequested += OnStartScreenRequested;
        ViewModel.CouncilDockChanged += (_, _) => UpdateAiDockMargin();
        InspectorIsland.SizeChanged += (_, _) => UpdateAiDockMargin();
        InitializeBlock34();
        InitializeWave2();
        InitializeWave3();
        InitializeWave4();
#if DEBUG
        ApplyDebugStartupOptions();
#endif

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.PassportRequested += OnPassportRequested;
        ViewModel.ChatMessages.CollectionChanged += OnChatMessagesChanged;
        ViewModel.CouncilTimeline.CollectionChanged += OnCouncilTimelineChanged;
        // Цифры (точный размер в мм), Enter/Backspace и клавиши прогулки уходят во вьюпорт, если фокус не в поле ввода.
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRootPreviewKeyDown), true);
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.ChatMessages.CollectionChanged -= OnChatMessagesChanged;
            ViewModel.CouncilTimeline.CollectionChanged -= OnCouncilTimelineChanged;
            // Рендер-хост Helix и веб-панель освобождаются явно: поток рендер-хоста иначе держит процесс после закрытия окна.
            Viewport.Shutdown();
            WebBridgePanel.Shutdown();
        };
    }

    /// <summary>
    /// «Начальный экран»: при несохранённых изменениях — подтверждение, затем стартовое окно поверх редактора; выбор проекта или
    /// шаблона применяется к этому же окну, закрытие стартового окна возвращает к проекту без изменений.
    /// </summary>
    private async void OnStartScreenRequested(object? sender, EventArgs e)
    {
        try
        {
            if (ViewModel.Scene.IsDirty && RootGrid.XamlRoot is { } root)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = root,
                    Title = L.Get("W1StartConfirmTitle"),
                    Content = new TextBlock { Text = L.Get("W1StartConfirm"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                    PrimaryButtonText = L.Get("W1Continue"),
                    CloseButtonText = L.Get("W1Cancel"),
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            (Application.Current as App)?.ShowStartScreenFromEditor();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("start-screen", ex);
        }
    }

    /// <summary>
    /// Боковой док сужает колонку холста: нижняя панель команд центрируется по свободной от развёрнутого инспектора части,
    /// иначе её капсула наезжает на инспектор. Без дока отступы прежние.
    /// </summary>
    private void UpdateAiDockMargin()
    {
        // Режим «Конструкции»: панель конструктора стоит слева от инспектора — панель команд центрируется левее обеих.
        bool structural = ViewModel.IsStructuralMode && !ViewModel.IsInspectorCollapsed;
        // Волна 6: панель Среды стоит там же, где панель «Конструкции».
        bool environment = !structural && ViewModel.IsEnvironmentPanelVisible && !ViewModel.IsInspectorCollapsed;
        bool reserve = (ViewModel.IsCouncilDockVisible || structural || environment) && !ViewModel.IsInspectorCollapsed && InspectorIsland.Visibility == Visibility.Visible;
        double right = 14 + (reserve ? InspectorIsland.ActualWidth + 14 : 0) + (structural ? StructuralPanel.Width + 14 : 0) + (environment ? EnvironmentPanel.Width + 14 : 0);
        var margin = new Thickness(14, 0, right, 16);
        if (AiDock.Margin != margin) AiDock.Margin = margin;
    }

    /// <summary>Открыто ли стартовое окно из редактора (автотест кнопки «Начальный экран»).</summary>
    internal static bool IsStartScreenOpen => App.StartScreenWindow is not null;

    /// <summary>
    /// Появление редактора после стартового окна: острова и холст проявляются (0 → 1) с лёгким увеличением парящих панелей.
    /// Без стартовой оболочки (Debug-сценарии) окно появляется сразу.
    /// </summary>
    public void PrepareEntranceAnimation()
    {
        RootGrid.Opacity = 0;
        RootGrid.Loaded += OnEntranceLoaded;
    }

    private void OnEntranceLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnEntranceLoaded;
        WindowChrome.Fade(RootGrid, 0, 1, 1, 1, 420).Begin();
        // Верхний бар только проявляется: масштаб сдвинул бы области сквозных кликов заголовка окна.
        WindowChrome.Fade(TopBar, 0, 1, 1, 1, 520, 80).Begin();
        WindowChrome.Fade(InspectorIsland, 0, 1, 0.97, 1, 560, 150).Begin();
        WindowChrome.Fade(AiDock, 0, 1, 0.97, 1, 560, 220).Begin();
        OfferFirstRunTour();
    }

    // ───────────────────────────── TitleBar поверх 3D-холста ─────────────────────────────

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TopBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        AppWindow.Resize(new SizeInt32(1680, 980));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();

        TopBar.Loaded += (_, _) => UpdateTitleBarRegions();
        TopBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TopLeftTools.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TopCenterTools.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TopRightTools.SizeChanged += (_, _) => UpdateTitleBarRegions();
    }

    /// <summary>
    /// Верхний бар оставляет место под системные кнопки окна; интерактивные группы бара пропускают клики,
    /// остальная площадь бара — область перетаскивания окна.
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (TopBar.XamlRoot is null) return;
        double scale = TopBar.XamlRoot.RasterizationScale;
        double captionWidth = AppWindow.TitleBar.RightInset / scale;
        var margin = new Thickness(14, 10, 14 + captionWidth, 0);
        if (TopBar.Margin != margin) TopBar.Margin = margin;

        var rects = new FrameworkElement[] { TopLeftTools, TopCenterTools, TopRightTools }
            .Where(e => e.ActualWidth > 0)
            .Select(e => ToPhysicalRect(e, scale))
            .ToArray();

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rects);
    }

    private static RectInt32 ToPhysicalRect(FrameworkElement element, double scale)
    {
        var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.WindowTitle)) Title = ViewModel.WindowTitle;
    }

    // ───────────────────────────── Меню тем и моделей ИИ ─────────────────────────────

    private void OnThemeFlyoutOpening(object sender, object e)
    {
        ThemeFlyout.Items.Clear();
        foreach (var name in ViewModel.ThemeNames)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = name,
                GroupName = "Themes",
                IsChecked = name == ViewModel.SelectedThemeName
            };
            item.Click += (_, _) => ViewModel.SelectedThemeName = name;
            ThemeFlyout.Items.Add(item);
        }
    }

    /// <summary>
    /// Меню моделей: локальные Qwen (всегда) → облачные модели проверенных ключей, сгруппированные по провайдеру
    /// («Gemini · …», «ChatGPT · …», «Claude · …», «OpenRouter · …») → веб-аккаунты без API-ключа.
    /// </summary>
    private void OnModelFlyoutOpening(object sender, object e)
    {
        ModelFlyout.Items.Clear();
        var registry = ViewModel.ModelRegistry;

        // Локальный сервер Ollama: установленные модели (статус движка — в подписи раздела).
        var ollama = registry.OllamaModels;
        ModelFlyout.Items.Add(SectionHeader($"{L.Get("ModelsOllama")} · {ViewModel.OllamaStatusText}"));
        foreach (var model in ollama)
            ModelFlyout.Items.Add(ModelItem(model));
        if (ollama.Count == 0 || !ViewModel.IsOllamaReady)
        {
            var refresh = new MenuFlyoutItem { Text = L.Get("OllamaRefresh"), Icon = new FontIcon { Glyph = "" } };
            refresh.Click += async (_, _) => await ViewModel.RefreshOllamaCommand.ExecuteAsync(null);
            ModelFlyout.Items.Add(refresh);
        }

        ModelFlyout.Items.Add(new MenuFlyoutSeparator());
        ModelFlyout.Items.Add(SectionHeader(L.Get("ModelsBuiltIn")));
        foreach (var model in registry.LocalModels)
            ModelFlyout.Items.Add(ModelItem(model));

        ModelFlyout.Items.Add(new MenuFlyoutSeparator());
        ModelFlyout.Items.Add(SectionHeader(L.Get("ModelsCloud")));
        bool anyCloud = false;
        foreach (var provider in AiModelCatalog.CloudProviders)
        {
            var models = registry.CloudModels(provider);
            if (models.Count == 0) continue;
            anyCloud = true;
            bool containsSelected = models.Any(m => m.Id == ViewModel.SelectedAiModel);
            var sub = new MenuFlyoutSubItem
            {
                Text = $"{AiModelCatalog.ProviderTitle(provider)} · {models.Count}",
                Icon = new FontIcon { Glyph = containsSelected ? "" : AiModelCatalog.ProviderGlyph(provider) }
            };
            foreach (var model in models) sub.Items.Add(ModelItem(model));
            ModelFlyout.Items.Add(sub);
        }
        if (!anyCloud)
        {
            var connect = new MenuFlyoutItem { Text = L.Get("ConnectCloud"), Icon = new FontIcon { Glyph = "" } };
            connect.Click += async (_, _) => await ShowSettingsAsync();
            ModelFlyout.Items.Add(connect);
        }

        ModelFlyout.Items.Add(new MenuFlyoutSeparator());
        ModelFlyout.Items.Add(SectionHeader(L.Get("ModelsWeb")));
        foreach (var model in AiModelCatalog.WebOptions)
            ModelFlyout.Items.Add(ModelItem(model));
    }

    private RadioMenuFlyoutItem ModelItem(AiModelOption model)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = ViewModel.ModelLabel(model),
            GroupName = "AiModels",
            IsChecked = model.Id == ViewModel.SelectedAiModel,
            Icon = new FontIcon { Glyph = model.IsWeb ? "" : model.SupportsVision ? "" : AiModelCatalog.ProviderGlyph(model.Provider) }
        };
        item.Click += (_, _) => ViewModel.SelectAiModelByUser(model.Id);
        return item;
    }

    private static MenuFlyoutItem SectionHeader(string text) => new()
    {
        Text = text,
        IsEnabled = false,
        FontSize = 11
    };

    // ───────────────────────────── Файлы проекта .aia ─────────────────────────────

    private T InitPicker<T>(T picker) where T : class
    {
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return picker;
    }

    private async void OnSaveProjectClick(object sender, RoutedEventArgs e) => await SaveProjectAsync();

    /// <summary>Меню «Открыть»: любой поддерживаемый файл, проект .aia, модель IFC, DXF как подложка или сразу в 3D.</summary>
    private async void OnOpenMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag }) return;
        switch (tag)
        {
            case "aia":
                if (await ViewModel.ProjectFiles.PickOpenPathAsync(this) is { } aia) await ViewModel.OpenProjectAsync(aia);
                break;
            case "ifc":
                if (await PickFileAsync(".ifc") is { } ifc) await ViewModel.ImportIfcAsync(ifc);
                break;
            case "dxf":
            case "dxf3d":
                if (await PickFileAsync(".dxf") is { } dxf) await ViewModel.LoadDxfAsync(dxf, liftTo3D: tag == "dxf3d");
                break;
            default:
                await OpenProjectAsync();
                break;
        }
    }

    /// <summary>Ctrl+O и «Открыть файл…»: .aia — проект, .ifc — модель Renga/Revit, .dxf — подложка AutoCAD.</summary>
    private async Task OpenProjectAsync()
    {
        if (await PickFileAsync(SceneProjectFile.Extension, ".ifc", ".dxf") is { } path)
            await ViewModel.OpenAnyAsync(path);
    }

    private async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = InitPicker(new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        });
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>«Рабочий чертёж ГОСТ (.PDF)»: Tag «A3|plan», «A2|frame», «A3|section» и т.п.</summary>
    private async void OnGostExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } || !MainViewModel.TryParseGost(tag, out var format, out var view)) return;
        var picker = InitPicker(new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ProjectFileService.SafeFileName($"{ViewModel.Scene.ProjectName} — {GostDrawingPdfExporter.FormatTitle(format)}")
        });
        picker.FileTypeChoices.Add("PDF (ГОСТ Р 21.101)", [".pdf"]);
        if (await picker.PickSaveFileAsync() is { } file)
            await ViewModel.ExportGostAsync(format, view, file.Path);
    }

    /// <summary>«Выпуск альбома чертежей (3 листа PDF)»: план, разрез и спецификация с ведомостью стали в одном файле.</summary>
    private async void OnGostAlbumClick(object sender, RoutedEventArgs e)
    {
        var picker = InitPicker(new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = ProjectFileService.SafeFileName($"{ViewModel.Scene.ProjectName} — альбом")
        });
        picker.FileTypeChoices.Add("PDF (ГОСТ Р 21.101)", [".pdf"]);
        if (await picker.PickSaveFileAsync() is { } file)
            await ViewModel.ExportGostAlbumAsync(file.Path);
    }

    private async Task SaveProjectAsync()
    {
        if (await ViewModel.ProjectFiles.PickSavePathAsync(this) is { } path)
            await ViewModel.SaveProjectAsync(path);
    }

    // ───────────────────────────── Экспорт и Vision ─────────────────────────────

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } || !CadExportService.TryParse(tag, out var format)) return;

        if (await ViewModel.CadExport.PickTargetAsync(this, format, ViewModel.Scene.ProjectName) is { } path)
            await ViewModel.ExportAsync(format, path);
    }

    /// <summary>«Снимок экрана 4K (PNG)»: фон неба (Tag = sky) или прозрачный (Tag = alpha), путь — через FileSavePicker.</summary>
    private async void OnRender4KClick(object sender, RoutedEventArgs e)
    {
        bool transparent = sender is MenuFlyoutItem { Tag: "alpha" };
        var picker = InitPicker(new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = ProjectFileService.SafeFileName($"{ViewModel.Scene.ProjectName} — 4K")
        });
        picker.FileTypeChoices.Add("PNG", [".png"]);
        if (await picker.PickSaveFileAsync() is { } file) await RenderHighResAsync(file.Path, transparent);
    }

    private async Task RenderHighResAsync(string path, bool transparent)
    {
        ViewModel.IsBusy = true;
        ViewModel.BusyText = L.Get("Render4KBusy");
        try
        {
            // Кадр после закрытия меню: Helix успевает отрисовать текущую сцену без всплывающих элементов.
            await Task.Delay(120);
            var result = await Viewport.RenderHighResolutionAsync(path, MainViewModel.RenderWidth, MainViewModel.RenderHeight, transparent);
            ViewModel.ReportHighResRender(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SharpDX.SharpDXException or System.Runtime.InteropServices.COMException)
        {
            ViewModel.ReportHighResRenderError(ex);
        }
        finally
        {
            ViewModel.IsBusy = false;
            ViewModel.BusyText = string.Empty;
        }
    }

    private async void OnVisionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string modeKey }) return;

        var picker = InitPicker(new FileOpenPicker
        {
            SuggestedStartLocation = modeKey == "VisionDrawings" ? PickerLocationId.DocumentsLibrary : PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail
        });
        var extensions = modeKey == "VisionDrawings" ? [".pdf", ".png", ".jpg", ".jpeg", ".webp"] : new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".pdf" };
        foreach (var ext in extensions)
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var (mode, kind) = modeKey switch
        {
            "VisionPlan" => (VisionMode.FloorPlan2DTo3D, BlueprintKind.Architectural),
            "VisionSketch" => (VisionMode.HandSketchTo3D, BlueprintKind.SketchOrPhoto),
            "VisionPhoto" => (VisionMode.RealPhotoTo3D, BlueprintKind.SketchOrPhoto),
            _ => (VisionMode.DrawingSet, BlueprintKind.Auto)
        };

        // Любой PDF — через инспектор листов (миниатюры, автовыбор, тип материала, комментарий).
        if (Path.GetExtension(file.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await PickPdfSheetsAsync(file.Path, kind);
            return;
        }
        await ViewModel.RunVisionAsync(mode, L.Get(modeKey), file.Path, kind);
    }

    // ───────────────────────────── Вложения запроса и реконструкция по плану ─────────────────────────────

    /// <summary>«Прикрепить план / эскиз»: файл изображения уходит во вложения следующего запроса.</summary>
    private async void OnAttachFileRequested(object? sender, EventArgs e)
    {
        var picker = InitPicker(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail });
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ViewModel.AddAttachmentFileAsync(file.Path);
    }

    private void OnAttachmentRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RefreshSnapshotCommand.CanExecute(null)) ViewModel.RefreshSnapshotCommand.Execute(null);
    }

    private void OnAttachmentRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RequestAttachment attachment }) ViewModel.RemoveAttachmentCommand.Execute(attachment);
    }

    private async void OnPlanReconstructionRequested(object? sender, EventArgs e)
    {
        var picker = InitPicker(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.Thumbnail });
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".pdf", ".dxf" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ShowPlanReconstructionAsync(file.Path);
    }

    private bool _planDialogOpen;

    /// <summary>Распознавание плана (растр / PDF / DXF) → окно проверки с предпросмотром → вставка в сцену одним шагом Undo.</summary>
    private async Task ShowPlanReconstructionAsync(string path, int storeys = 2)
    {
        if (_planDialogOpen) return;
        _planDialogOpen = true;
        try
        {
            ViewModel.IsBusy = true;
            ViewModel.BusyText = L.Get("PlanEngine");
            RecognizedPlan plan;
            try
            {
                plan = await RasterPlanTracer.LoadAsync(path);
            }
            finally
            {
                ViewModel.IsBusy = false;
                ViewModel.BusyText = string.Empty;
            }
            var dialog = new PlanToBimDialog(ViewModel, plan, path, storeys)
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = _theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { Elements.Count: > 0 } result) ViewModel.InsertPlanResult(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidDataException)
        {
            ViewModel.ShowError(L.Get("PlanEngine"), ex.Message, $"AIA-PLAN-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            _planDialogOpen = false;
        }
    }

    private bool _pdfDialogOpen;

    /// <summary>Диалог выбора листов PDF → рендер выбранных листов → адаптивный конвейер чертежей.</summary>
    private async Task PickPdfSheetsAsync(string path, BlueprintKind kind, bool debugAutoSend = false)
    {
        if (_pdfDialogOpen) return;
        _pdfDialogOpen = true;
        try
        {
            var dialog = new PdfPagePickerDialog(path, L, kind)
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = _theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark
            };
            bool debugDirect = false;
#if DEBUG
            if (debugAutoSend)
                dialog.Opened += async (_, _) =>
                {
                    await dialog.WaitLoadedAsync();
                    // AIA_DEBUG_PDFRENDER=папка: рендер выбранных листов и промпта (с блоком векторного слоя) в файлы — без отправки в нейросеть.
                    if (Environment.GetEnvironmentVariable("AIA_DEBUG_PDFRENDER") is { Length: > 0 } dir && dialog.Pdf is { } doc)
                    {
                        Directory.CreateDirectory(dir);
                        var images = await PdfBlueprintExtractor.RenderForAiAsync(doc, dialog.SelectedPages);
                        for (int i = 0; i < images.Count; i++)
                            await File.WriteAllBytesAsync(Path.Combine(dir, $"sheet_{dialog.SelectedPages[i] + 1}.jpg"), images[i].Bytes);
                        await File.WriteAllBytesAsync(Path.Combine(dir, "web_sheet.jpg"), await PdfBlueprintExtractor.ComposeSheetAsync(images));
                        var ai = App.Services.GetRequiredService<AIOrchestrator>();
                        var labels = dialog.SelectedPages.Select(p => L.Format("PdfPageShort", p + 1)).ToList();
                        var ctx = ai.CreateContext(dialog.Comment, ViewModel.SelectedModel, false, VisionMode.DrawingSet, images, Path.GetFileName(path), dialog.Kind,
                            AdaptivePromptBuilder.DescribeSheets(labels, (int)doc.PageCount, dialog.Kind, Path.GetFileName(path), dialog.Facts,
                                dialog.IsDefinitionValid ? dialog.Definition : null));
                        var (system, user) = ai.BuildPrompts(ctx);
                        await File.WriteAllTextAsync(Path.Combine(dir, "prompt.txt"), system + "\n\n=== USER ===\n" + user + "\n\n=== IMAGES ===\n" +
                            string.Join("\n", images.Select(im => $"{im.Label}: {im.Bytes.Length / 1024} KB")));
                    }
                    // AIA_DEBUG_PDFCOMPILE=1: нажать «Прямая компиляция 3D по параметрам (Без ИИ)».
                    if (Environment.GetEnvironmentVariable("AIA_DEBUG_PDFCOMPILE") == "1" && dialog.IsDefinitionValid)
                    {
                        debugDirect = true;
                        dialog.Hide();
                    }
                    if (Environment.GetEnvironmentVariable("AIA_DEBUG_PDFSEND") == "1") dialog.Hide();
                };
#endif
            var result = await dialog.ShowAsync();
            if ((result == ContentDialogResult.Secondary || debugDirect) && dialog.IsDefinitionValid && dialog.Definition is { } definition)
            {
                ViewModel.CompileBlueprintDirect(definition, Path.GetFileName(path));
                return;
            }
            bool send = result == ContentDialogResult.Primary || (debugAutoSend && Environment.GetEnvironmentVariable("AIA_DEBUG_PDFSEND") == "1");
            if (send && dialog.Pdf is { } pdf && dialog.SelectedPages.Count > 0)
            {
                var prompt = dialog.Comment.Length > 0 ? dialog.Comment : ViewModel.ChatInput.Trim();
                ViewModel.ChatInput = string.Empty;
                await ViewModel.RunDrawingSetAsync(pdf, path, dialog.SelectedPages, dialog.Kind, prompt, dialog.Facts, dialog.IsDefinitionValid ? dialog.Definition : null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            ViewModel.ShowError(L.Get("ErrorVision"), L.Format("ErrorImageRead", ex.Message), $"AIA-PDF-{ex.HResult:X8}", ex.ToString());
        }
        finally
        {
            _pdfDialogOpen = false;
        }
    }

    private bool _dxfDialogOpen;

    /// <summary>Окно верификации высот, толщин и сечений перед подъёмом плоского DXF в 3D; null — отмена.</summary>
    private async Task<DxfLiftOptions?> VerifyDxfLiftAsync(DxfLiftPreview preview, DxfLiftOptions defaults)
    {
#if DEBUG
        // Сценарии съёмки: AIA_DEBUG_DXFLIFT=1 без DXFVERIFY — подъём по умолчанию без окна; DXFVERIFY=1 — окно остаётся открытым,
        // DXFVERIFY=auto — окно подтверждается через 2 с.
        string? verifyMode = Environment.GetEnvironmentVariable("AIA_DEBUG_DXFVERIFY");
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_DXFLIFT") == "1" && string.IsNullOrEmpty(verifyMode)) return defaults;
#endif
        if (_dxfDialogOpen || Content.XamlRoot is null) return null;
        _dxfDialogOpen = true;
        try
        {
            var dialog = new DxfLiftDialog(preview, defaults, L)
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = _theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark
            };
            bool autoConfirmed = false;
#if DEBUG
            if (verifyMode == "auto")
                dialog.Opened += async (_, _) =>
                {
                    await Task.Delay(2000);
                    autoConfirmed = true;
                    dialog.Hide();
                };
#endif
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary || autoConfirmed ? dialog.Options : null;
        }
        finally
        {
            _dxfDialogOpen = false;
        }
    }

    private void OnRemoveAxisClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StructuralGridAxis axis }) ViewModel.RemoveAxis(axis);
    }

    // ───────────────────────────── Пирог конструкции и коллизии ─────────────────────────────

    private void OnRemoveLayerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MaterialLayer layer }) ViewModel.RemoveLayer(layer);
    }

    private void OnLayerRoleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MaterialLayer layer }) ViewModel.CycleLayerRole(layer);
    }

    private void OnRemoveUserParameterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UserParameterItem item }) ViewModel.RemoveUserParameter(item);
    }

    /// <summary>Enter в поле имени — «+ Свойство».</summary>
    private void OnUserParameterNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !ViewModel.AddUserParameterCommand.CanExecute(null)) return;
        ViewModel.AddUserParameterCommand.Execute(null);
        e.Handled = true;
    }

    private void OnInsolationRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is InsolationRow row) ViewModel.SelectInsolationRow(row);
    }

    // ───────────────────────────── Совет КБ ─────────────────────────────

    private void OnCouncilFlyoutOpening(object sender, object e) => ViewModel.RefreshCouncilSlots();

    private async void OnCouncilVariantClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ConsultationVariantRow row }) await ViewModel.ChooseVariantAsync(row);
    }

    private void OnCouncilIssueClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConsultationIssueRow row) ViewModel.FocusCouncilIssue(row);
    }

    /// <summary>Enter в поле ответа карточки согласования — отправить решение Совету.</summary>
    private void OnConsultationReplyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (ViewModel.SendConsultationReplyCommand.CanExecute(null)) ViewModel.SendConsultationReplyCommand.Execute(null);
    }

    private void OnCouncilTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel.CouncilTimeline.Count > 0)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ViewModel.CouncilTimeline.Count > 0) CouncilTimelineList.ScrollIntoView(ViewModel.CouncilTimeline[^1]);
            });
    }

    private void OnClashFocusClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClashItem item }) ViewModel.FocusClash(item);
    }

    private void OnClashFixClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClashItem item }) ViewModel.FixClash(item);
    }

    // ───────────────────────────── Настройки и ошибки ─────────────────────────────

    private async void OnSettingsClick(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private bool _settingsOpen;

    private async Task ShowSettingsAsync()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            var dialog = new SettingsDialog(_settings, L, ViewModel.ModelRegistry,
                App.Services.GetRequiredService<AiKeyInspectorService>(), m => ViewModel.ModelLabel(m), ViewModel.ApplySiteNorms, () => PrimaryModelPlate(compact: false))
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = _theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark
            };
#if DEBUG
            if (Environment.GetEnvironmentVariable("AIA_DEBUG_KEYTEST") is { Length: > 0 } testKey)
                dialog.Opened += (_, _) => dialog.DebugInjectKey(testKey);
            // AIA_DEBUG_SETTINGS=general|appearance|modules|assistant|storage — открыть раздел настроек.
            if (Environment.GetEnvironmentVariable("AIA_DEBUG_SETTINGS") is { Length: > 1 } section)
                dialog.Opened += (_, _) => dialog.DebugSelectSection(section);
#endif

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (dialog.SaveError is { } error)
                    ViewModel.ShowError(L.Get("ErrorSettings"), error.Message, $"AIA-SETTINGS-{error.HResult:X8}", error.ToString());
                else
                    ViewModel.ApplySettingsFromDialog();
            }
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    // ───────────────────────────── Ведомость объёмов и армирование ─────────────────────────────

    private bool _quantitiesOpen;

    private async void OnQuantitiesClick(object sender, RoutedEventArgs e) => await ShowQuantitiesAsync();

    /// <param name="showParameters">Открыть вид «По свойствам» (съёмка AIA_DEBUG_QUANTITIES=params).</param>
    private async Task ShowQuantitiesAsync(bool showParameters = false)
    {
        if (_quantitiesOpen || _settingsOpen) return;
        _quantitiesOpen = true;
        bool pull = false;
        try
        {
            var dialog = new QuantityScheduleDialog(ViewModel, SaveProjectForEstimateAsync, PickPriceFileAsync, PickPriceTemplatePathAsync)
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = _theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark
            };
            if (showParameters) dialog.Opened += (_, _) => dialog.ShowParameterView();
#if DEBUG
            // AIA_DEBUG_PRICEEDIT=ключ строки;цена;поставщик — ручная правка строки сметы тем же путём, что и ввод в окне.
            if (Environment.GetEnvironmentVariable("AIA_DEBUG_PRICEEDIT") is { Length: > 0 } edit && edit.Split(';') is { Length: >= 3 } part &&
                double.TryParse(part[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double editPrice))
                dialog.Opened += async (_, _) =>
                {
                    await Task.Delay(400);
                    dialog.DebugEditLine(part[0], editPrice, part[2]);
                };
#endif
            await dialog.ShowAsync();
            pull = dialog.PullPricesRequested;
        }
        finally
        {
            _quantitiesOpen = false;
        }
        // «Подтянуть цены по ссылке / из сметы…»: одно окно за раз — веб-импорт с проверкой, затем ведомость снова.
        if (pull)
        {
            await ShowPriceVerificationAsync(null);
            await ShowQuantitiesAsync();
        }
    }

    /// <summary>«Загрузить прайс (CSV)»: файл прайс-листа поставщика (CSV для Excel или JSON).</summary>
    private async Task<string?> PickPriceFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".txt");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    /// <summary>«Шаблон прайса»: путь шаблона для сметчика (CSV для Excel или JSON).</summary>
    private async Task<string?> PickPriceTemplatePathAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = L.Get("PriceTemplateFileName")
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add(L.Get("PriceTemplateCsv"), [".csv"]);
        picker.FileTypeChoices.Add("JSON", [".json"]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    // ───────────────────────────── Паспорт расчёта по СП ─────────────────────────────

    private bool _passportOpen;

    private async void OnPassportRequested(object? sender, string elementId) => await ShowPassportAsync(elementId);

    /// <param name="materialEditor">Сразу открыть «Задать свои характеристики материала» (съёмка).</param>
    /// <param name="scrollToSteps">Прокрутить к пошаговому расчёту (съёмка).</param>
    private async Task ShowPassportAsync(string elementId, bool materialEditor = false, bool scrollToSteps = false)
    {
        if (_passportOpen || _settingsOpen || _quantitiesOpen) return;
        _passportOpen = true;
        try
        {
            var dialog = new CalculationPassportDialog(ViewModel, elementId)
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = _theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark
            };
            if (materialEditor) dialog.Opened += (_, _) => dialog.ShowMaterialEditor();
            if (scrollToSteps)
                dialog.Opened += async (_, _) =>
                {
                    await Task.Delay(500);
                    dialog.ScrollToSteps();
                };
            await dialog.ShowAsync();
        }
        finally
        {
            _passportOpen = false;
        }
    }

    /// <summary>«Сохранить смету в проект»: открытый проект — по его пути, новый — через выбор файла .aia.</summary>
    private async Task<bool> SaveProjectForEstimateAsync()
    {
        string? path = ViewModel.Scene.ProjectFilePath ?? await ViewModel.ProjectFiles.PickSavePathAsync(this);
        if (path is null) return false;
        await ViewModel.SaveProjectAsync(path);
        return !ViewModel.Scene.IsDirty && ViewModel.Scene.ProjectFilePath == path;
    }

    private void OnRebarCloseClick(object sender, RoutedEventArgs e) => ViewModel.IsRebarView = false;

    /// <summary>Кнопка «Копировать» во Flyout полного журнала ошибки / отчёта солвера.</summary>
    private void OnCopyErrorLogClick(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.ErrorReportText);
        Clipboard.SetContent(package);
        ErrorLogFlyout.Hide();
    }

    // ───────────────────────────── Дерево и чат ─────────────────────────────

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not BimTreeNode { Element: { } element }) return;
        // Shift + клик в дереве BIM — добавить/убрать элемент из мультивыделения.
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            ViewModel.Scene.ToggleSelection(element);
        else
            ViewModel.SelectElement(element);
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || IsTextInputFocused()) return;
        if (Viewport.HandleKey(e.Key)) e.Handled = true;
    }

    private void OnChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel.ChatMessages.Count > 0)
            DispatcherQueue.TryEnqueue(() => ChatList.ScrollIntoView(ViewModel.ChatMessages[^1]));
    }

    /// <summary>Enter — отправить, Shift+Enter — новая строка.</summary>
    private void OnChatInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

        e.Handled = true;
        if (ViewModel.SendChatCommand.CanExecute(null)) ViewModel.SendChatCommand.Execute(null);
    }

    // ───────────────────────────── Горячие клавиши ─────────────────────────────

    private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused()) return;
        args.Handled = true;
        if (ViewModel.UndoCommand.CanExecute(null)) ViewModel.UndoCommand.Execute(null);
    }

    private void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused()) return;
        args.Handled = true;
        if (ViewModel.RedoCommand.CanExecute(null)) ViewModel.RedoCommand.Execute(null);
    }

    private async void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Scene.ProjectFilePath is { } path) await ViewModel.SaveProjectAsync(path);
        else await SaveProjectAsync();
    }

    private async void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await OpenProjectAsync();
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Сначала — закрытие тура, отмена набора размера в живой линейке и выход из прогулки, затем снятие выделения.
        if (IsTourActive)
        {
            EndTour();
            args.Handled = true;
            return;
        }
        if (Viewport.HandleEscape())
        {
            args.Handled = true;
            return;
        }
        if (!ViewModel.HasSelection) return;
        args.Handled = true;
        ViewModel.DeselectCommand.Execute(null);
    }

    private void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused() || Viewport.IsTypingDimension) return;
        // Рулетка: выбранный закреплённый размер удаляется (один шаг Undo).
        if (Viewport.HandleMeasureDelete())
        {
            args.Handled = true;
            return;
        }
        if (!ViewModel.HasSelection) return;
        args.Handled = true;
        // Наведённая вершина контура полигональной плиты удаляется сама (элемент остаётся).
        if (Viewport.HandleDelete()) return;
        ViewModel.DeleteSelectedCommand.Execute(null);
    }

    private bool IsTextInputFocused() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or NumberBox or PasswordBox;

#if DEBUG
    /// <summary>Только Debug: переменные окружения AIA_DEBUG_* для автоматической визуальной проверки (скриншотов) UI.</summary>
    private void ApplyDebugStartupOptions()
    {
        string? Env(string name) => Environment.GetEnvironmentVariable("AIA_DEBUG_" + name);

        if (Env("THEME") is { Length: > 0 } theme) ViewModel.SelectedThemeName = theme;
        if (Env("PRESET") is { Length: > 0 } preset) ViewModel.LoadPresetCommand.Execute(preset);
        if (Env("SCENE") == "frame")
        {
            // Тестовое каркасное здание самопроверки (колонны всех профилей, балки, плиты с отверстиями, оси).
            var (frameElements, frameGroups, frameAxes) = CadSelfTestService.CreateFrameScene();
            CadSelfTestService.ApplyFrameUserParameters(frameElements);
            ViewModel.Scene.LoadScene(frameElements, frameGroups, recordUndo: false, clearHistory: true, axes: frameAxes);
            ViewModel.Scene.ProjectName = "Каркасный корпус";
        }
        if (Env("SCENE") == "council")
        {
            // Сцена автотеста Совета КБ: консольная колонна, плита 200 мм на пролёте 10.5 м, марш без проёма, окно на колонне.
            var (councilElements, councilGroups, councilAxes) = CadSelfTestService.CreateCouncilTestScene();
            ViewModel.Scene.LoadScene(councilElements, councilGroups, recordUndo: false, clearHistory: true, axes: councilAxes);
            ViewModel.Scene.ProjectName = CadSelfTestService.CouncilGroup;
        }
        // Совет КБ: AIA_DEBUG_COUNCILDEMO=1 — временный состав (Gemini / Claude / ChatGPT / Qwen) и сценарный шлюз без сети,
        // AIA_DEBUG_COUNCIL=mode|flyout|audit — режим «Совет КБ», панель состава, аудит с карточкой согласования;
        // AIA_DEBUG_COUNCILPICK=n — после аудита выбрать вариант n, AIA_DEBUG_COUNCILREPLY=текст — ответить текстом.
        if (Env("COUNCILDEMO") == "1")
            ViewModel.DebugUseCouncilDemo(new Dictionary<string, string>
            {
                ["architect"] = "gemini:gemini-2.5-pro",
                ["structural"] = "anthropic:claude-opus-5-5",
                ["norms"] = "openai:gpt-4o",
                ["synthesizer"] = AiModelCatalog.LocalQwen7BId
            }, injectModels: true);
        // AIA_DEBUG_COUNCILDEMO=ollama — состав из моделей Ollama (gemma4 / qwen2.5-coder / deepseek-r1 / gpt-oss) и сценарный
        // шлюз с потоковыми рассуждениями <think> и дефектным первым черновиком (цикл самоисправления), без сети.
        if (Env("COUNCILDEMO") == "ollama")
            ViewModel.DebugUseCouncilDemo(new Dictionary<string, string>
            {
                ["architect"] = "ollama:gemma4:12b",
                ["structural"] = "ollama:qwen2.5-coder:14b",
                ["norms"] = "ollama:deepseek-r1:14b",
                ["synthesizer"] = "ollama:gpt-oss:20b"
            }, injectModels: true, ollama: true);
        // AIA_DEBUG_OLLAMAHOST=http://127.0.0.1:11435 — другой адрес сервера (проверка автозапуска ollama serve без записи в settings.json).
        if (Env("OLLAMAHOST") is { Length: > 0 } ollamaHost)
            App.Services.GetRequiredService<OllamaProcessManager>().OverrideEndpoint(ollamaHost);
        if (Env("COUNCIL") is { Length: > 0 } council)
        {
            ViewModel.IsCouncilMode = true;
            if (council == "flyout")
                RootGrid.Loaded += async (_, _) =>
                {
                    await Task.Delay(1800);
                    // Transient — без программного фокуса (иначе на первой кнопке появляется подсказка).
                    CouncilTeamFlyout.ShowAt(CouncilTeamButton, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Transient });
                };
            if (council == "audit")
                RootGrid.Loaded += async (_, _) =>
                {
                    await Task.Delay(2200);
                    if (ViewModel.RunCouncilAuditCommand.CanExecute(null)) await ViewModel.RunCouncilAuditCommand.ExecuteAsync(null);
                    if (int.TryParse(Env("COUNCILPICK"), out int pick) && pick >= 1 && pick <= ViewModel.ConsultationVariants.Count)
                        await ViewModel.ChooseVariantAsync(ViewModel.ConsultationVariants[pick - 1]);
                    else if (Env("COUNCILREPLY") is { Length: > 0 } reply && ViewModel.HasConsultation)
                    {
                        ViewModel.ConsultationReply = reply;
                        if (ViewModel.SendConsultationReplyCommand.CanExecute(null)) await ViewModel.SendConsultationReplyCommand.ExecuteAsync(null);
                    }
                };
        }
        if (Enum.TryParse<ViewportMode>(Env("VIEW"), out var mode)) ViewModel.ViewMode = mode;
        if (int.TryParse(Env("LEVEL"), out var level)) ViewModel.Scene.ActiveLevelFilter = level;
        if (Env("SELECT") is { Length: > 0 } name)
            ViewModel.SelectElement(ViewModel.Scene.Elements.FirstOrDefault(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        if (Env("TAB") == "estimate") ViewModel.IsEstimateTab = true;
        if (Env("TAB") == "axes") ViewModel.IsAxesTab = true;
        if (Env("TAB") == "analysis") ViewModel.IsAnalysisTab = true;
        // Пирог выбранного элемента: AIA_DEBUG_LAYERS=floor|flat_roof|foundation|exterior_wall.
        if (Env("LAYERS") is { Length: > 0 } layersPreset) ViewModel.ApplyLayerPresetCommand.Execute(layersPreset);
        // Свойства пользователя выделенных: AIA_DEBUG_USERPARAMS=имя:Text|Integer|Length|YesNo…=значение;… (числа — в единицах инспектора).
        if (Env("USERPARAMS") is { Length: > 0 } userParams) ViewModel.ApplyUserParametersText(userParams);
        if (Env("DESELECT") == "1") ViewModel.SelectElement(null);
        // Тактильное моделирование: AIA_DEBUG_SIMILAR=1 — «Однотипные на этаже» для выбранного элемента,
        // AIA_DEBUG_ARRAY=n;шагX;шагZ (мм) — линейный массив выделения, AIA_DEBUG_FLOORUP=1 — копировать этаж вверх,
        // AIA_DEBUG_PUSHPULL=имя|PosX|мм — вытянуть грань мышиным путём (с OSnap) и оставить капсулу размера на экране,
        // AIA_DEBUG_WALK=1 — прогулка (+ AIA_DEBUG_WALKAT=x,z,курс°[,шагов вперёд, м]), AIA_DEBUG_SUN=19:30;summer|equinox|winter — инсоляция и тени.
        if (Env("SIMILAR") == "1") ViewModel.SelectSimilarOnFloorCommand.Execute(null);
        if (Env("ARRAY") is { Length: > 0 } array)
        {
            var a = array.Split(';').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (a.Length == 3)
            {
                (ViewModel.ArrayCount, ViewModel.ArrayStepXMm, ViewModel.ArrayStepZMm) = (a[0], a[1], a[2]);
                ViewModel.CreateArrayCommand.Execute(null);
            }
        }
        if (Env("FLOORUP") == "1") ViewModel.CopyFloorUpCommand.Execute(null);
        // Журнал транзакций: AIA_DEBUG_UNDO=n — n шагов Undo после правок выше (точечная перестройка вьюпорта по DocumentChanged).
        if (Env("UNDO") is { Length: > 0 } undos)
            foreach (var _ in Enumerable.Range(0, int.TryParse(undos, out int u) ? u : 1)) ViewModel.UndoCommand.Execute(null);
        // Параметрические инструменты: AIA_DEBUG_TOOL=stair[;ширина мм] — П-образная лестница (с SELECT марша — на его месте),
        // AIA_DEBUG_TOOL=gable|hip[;угол°] — скатная кровля для выбранной кровли или плиты.
        if (Env("TOOL") is { Length: > 0 } tool)
        {
            var parts = tool.Split(';');
            double value = 0;
            bool hasValue = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
            if (parts[0] == "stair")
            {
                if (hasValue) ViewModel.StairFlightWidthMm = value;
                ViewModel.GenerateUShapedStairCommand.Execute(null);
            }
            else
            {
                if (hasValue) ViewModel.RoofPitchDeg = value;
                ViewModel.MakePitchedRoofCommand.Execute(parts[0]);
            }
        }
        if (Env("SUN") is { Length: > 0 } sun)
        {
            var parts = sun.Split(';');
            if (TimeSpan.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var time)) ViewModel.SolarHours = time.TotalHours;
            if (parts.Length > 1)
                ViewModel.SolarSeason = parts[1] switch { "winter" => SolarSeason.Winter, "equinox" => SolarSeason.Equinox, _ => SolarSeason.Summer };
            ViewModel.IsSolarEnabled = true;
        }
        // Регион строительства: AIA_DEBUG_REGION=vladivostok|sochi|yuzhno_sakhalinsk|… (сохраняется в settings.json),
        // AIA_DEBUG_INSOLATION=1 — проверка инсоляции окон по СанПиН (выделенных или всех видимых).
        if (Env("REGION") is { Length: > 0 } region) ViewModel.SelectRegion(region);
        if (Env("INSOLATION") == "1")
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                ViewModel.IsSolarEnabled = true;
                if (ViewModel.CheckInsolationCommand.CanExecute(null)) await ViewModel.CheckInsolationCommand.ExecuteAsync(null);
            };
        if (Env("PUSHPULL") is { Length: > 0 } pushPull)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var p = pushPull.Split('|');
                if (p.Length == 3 && Enum.TryParse<PushPullFace>(p[1], out var face) &&
                    ViewModel.Scene.Elements.FirstOrDefault(e => e.Name.Contains(p[0], StringComparison.OrdinalIgnoreCase)) is { } target)
                    Viewport.DebugPushPull(target, face, double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) / 1000);
            };
        // Вершина контура: AIA_DEBUG_VERTEX=имя|индекс|x,z (м) — перетащить вершину полигональной плиты мышиным путём с OSnap.
        if (Env("VERTEX") is { Length: > 0 } vertexSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var p = vertexSpec.Split('|');
                if (p.Length == 3 && int.TryParse(p[1], out int vertexIndex) &&
                    ViewModel.Scene.Elements.FirstOrDefault(e => SceneStateService.IsContourEditable(e) && e.Name.Contains(p[0], StringComparison.OrdinalIgnoreCase)) is { } contour)
                {
                    var xz = p[2].Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    if (xz.Length == 2) Viewport.DebugDragVertex(contour, vertexIndex, xz[0], xz[1]);
                }
            };
        if (Env("WALK") == "1")
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                ViewModel.IsWalkthrough = true;
                if (Env("WALKAT") is { Length: > 0 } at)
                {
                    var v = at.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length >= 3) Viewport.DebugWalkTo(v[0], v[1], v[2], v.Length >= 4 ? v[3] : 0);
                }
            };
        // Карта напряжений: AIA_DEBUG_STRESS=utilization|deflection|punching.
        if (Env("STRESS") is { Length: > 0 } stress)
        {
            ViewModel.StressMode = stress switch { "deflection" => StressMapMode.Deflection, "punching" => StressMapMode.Punching, _ => StressMapMode.Utilization };
            ViewModel.IsStressMapEnabled = true;
        }
        // Коллизии: AIA_DEBUG_CLASH=n — подсветить n-ю коллизию списка (с 1) и сфокусировать камеру.
        if (Env("CLASH") is { Length: > 0 } clash && int.TryParse(clash, out int clashIndex))
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2200);
                ViewModel.RunClashDetectionCommand.Execute(null);
                if (clashIndex >= 1 && clashIndex <= ViewModel.Clashes.Count) ViewModel.FocusClash(ViewModel.Clashes[clashIndex - 1]);
            };
        // Архитектурная подача: AIA_DEBUG_CLAY=1 — белый макет, AIA_DEBUG_MEASURE=x,y,z;x,y,z[;pin|select] (м, несколько через «|») —
        // рулетка: обмер тем же путём, что и клики (pin — закрепить, select — закрепить и выбрать), AIA_DEBUG_RENDER4K=путь.png[;alpha] —
        // рендер 3840 × 2160 без диалога (alpha — прозрачный фон).
        if (Env("CLAY") == "1") ViewModel.IsClayMode = true;
        // AIA_DEBUG_AXES=0 — без координационных осей (превью шаблонов стартового окна).
        if (Env("AXES") == "0") ViewModel.ShowGridAxes = false;
        if (Env("MEASURE") is { Length: > 0 } measureSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                foreach (var spec in measureSpec.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = spec.Split(';');
                    if (parts.Length < 2) continue;
                    var a = parts[0].Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var b = parts[1].Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    string mode = parts.Length > 2 ? parts[2] : string.Empty;
                    if (a.Length == 3 && b.Length == 3)
                        Viewport.DebugMeasure(new SharpDX.Vector3(a[0], a[1], a[2]), new SharpDX.Vector3(b[0], b[1], b[2]), pin: mode is "pin" or "select", select: mode == "select", snap: mode == "snap");
                }
            };
        if (Env("RENDER4K") is { Length: > 0 } renderSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(3600);
                var parts = renderSpec.Split(';');
                await RenderHighResAsync(parts[0], transparent: parts.Length > 1 && parts[1] == "alpha");
            };
        if (Env("HISTORY") == "1") ViewModel.IsHistoryOpen = true;
        if (Env("WIREFRAME") == "1") ViewModel.IsWireframe = true;
        if (Env("LANG") is LocalizationService.English or LocalizationService.Russian) ViewModel.Loc.Language = Env("LANG")!;
        if (Env("EXPORT") is { Length: > 0 } exportDir)
            RootGrid.Loaded += async (_, _) => await DebugExportAllAsync(exportDir);
        if (Env("ROUNDTRIP") is { Length: > 0 } aiaPath)
            RootGrid.Loaded += async (_, _) =>
            {
                await ViewModel.SaveProjectAsync(aiaPath);
                await ViewModel.OpenProjectAsync(aiaPath);
            };
        if (Env("AIJSON") is { Length: > 0 } jsonPaths)
        {
            // Несколько ответов через «|»: первый — новая сцена, остальные — «+ К участку».
            bool append = Env("APPEND") == "1";
            foreach (var path in jsonPaths.Split('|', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists))
            {
                ViewModel.DebugApplyJson(File.ReadAllText(path), append);
                append = true;
            }
        }
        if (Env("WEB") is { Length: > 0 } web)
        {
            var option = AiModelCatalog.WebOptions.FirstOrDefault(o => o.ModelId.StartsWith(web, StringComparison.OrdinalIgnoreCase));
            if (option is not null) ViewModel.SelectedAiModel = option.Id;
        }
        if (Env("KEYCLEAR") is { Length: > 0 } clear && Enum.TryParse<AiProviderKind>(clear, out var clearProvider))
        {
            _settings.SetApiKey(clearProvider, null);
            ViewModel.ModelRegistry.ClearCloudModels(clearProvider);
        }
        if (Env("MODEL") is { Length: > 0 } modelId) ViewModel.SelectedAiModel = ViewModel.ModelRegistry.Resolve(modelId).Id;
        if (Env("APPEND") == "1") ViewModel.IsAppendToSite = true;
        if (Env("PROMPT") is { Length: > 0 } prompt)
            RootGrid.Loaded += (_, _) =>
            {
                ViewModel.ChatInput = prompt;
                if (ViewModel.SendChatCommand.CanExecute(null)) ViewModel.SendChatCommand.Execute(null);
            };
        if (Env("SETTINGS") is { Length: > 0 }) RootGrid.Loaded += async (_, _) => await ShowSettingsAsync();
        // Блок девелопера: AIA_DEBUG_PRICELIST=путь.csv|json — прайс поставщика на сессию (settings.json не меняется),
        // AIA_DEBUG_PASSPORT=1|column|beam|slab|foundation|имя[;material] — паспорт расчёта самого нагруженного элемента категории,
        // выбранного (SELECT) или по имени; material — сразу открыть правку характеристик материала.
        if (Env("PRICELIST") is { Length: > 0 } priceList && File.Exists(priceList))
            try
            {
                ViewModel.ImportPriceCatalog(priceList, persist: false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                App.WriteCrashLog("debug-pricelist", ex);
            }
        if (Env("PASSPORT") is { Length: > 0 } passportSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                var parts = passportSpec.Split(';');
                var target = parts[0] switch
                {
                    "1" => ViewModel.Scene.SelectedElement is { } sel && ViewModel.Analysis.For(sel.Id) is not null ? sel : ViewModel.MostLoaded(BimCategory.Column) ?? ViewModel.MostLoaded(),
                    "column" => ViewModel.MostLoaded(BimCategory.Column),
                    "beam" => ViewModel.MostLoaded(BimCategory.Beam),
                    "slab" => ViewModel.MostLoaded(BimCategory.Slab),
                    "foundation" => ViewModel.MostLoaded(BimCategory.Foundation),
                    var name => ViewModel.Scene.Elements.FirstOrDefault(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                };
                if (target is null) return;
                ViewModel.SelectElement(target);
                await ShowPassportAsync(target.Id, materialEditor: parts.Contains("material"), scrollToSteps: parts.Contains("steps"));
            };
        // Блок девелопера 2: AIA_DEBUG_REVIEW=1 — сцена с дефектами как ответ внешнего генератора и лист согласования правок
        // (+ AIA_DEBUG_REVIEWFOCUS=n — фокус на n-й правке, AIA_DEBUG_REVIEWUNCHECK=n — снять отметку n-й правки),
        // AIA_DEBUG_PLANBIM=demo|путь — окно реконструкции по поэтажному плану (demo — синтетический растровый план 16 × 10 м,
        // AIA_DEBUG_PLANSTOREYS=n), AIA_DEBUG_ATTACH=1 — авто-снимок вида, план во вложениях, индикатор достоверности и версии сцены.
        // AIA_DEBUG_WAVE1=roof|guard — сцены волны 1: дом с полой мансардной кровлей и маршем на косоурах / проёмы, разведённые от колонн.
        if (Env("WAVE1") is "roof" or "guard")
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(900);
                var (w1Elements, w1Groups, w1Axes) = CadSelfTestService.CreateWave1DemoScene(Env("WAVE1")!);
                ViewModel.Scene.LoadScene(w1Elements, w1Groups, recordUndo: false, clearHistory: true, axes: w1Axes, dimensions: []);
                ViewModel.RequestZoomExtents();
            };
        if (Env("REVIEW") == "1")
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2200);
                var (gateElements, gateGroups, gateAxes) = CadSelfTestService.CreateGateDefectScene();
                await ViewModel.DebugReviewAsync(gateElements, gateGroups, gateAxes, "ChatGPT · веб-аккаунт");
                if (int.TryParse(Env("REVIEWUNCHECK"), out int uncheck) && uncheck >= 1 && uncheck <= ViewModel.ReviewFixes.Count) ViewModel.ReviewFixes[uncheck - 1].IsAccepted = false;
                if (int.TryParse(Env("REVIEWFOCUS"), out int focus) && focus >= 1 && focus <= ViewModel.ReviewFixes.Count) ViewModel.FocusReviewFix(ViewModel.ReviewFixes[focus - 1]);
            };
        if (Env("PLANBIM") is { Length: > 0 } planSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2000);
                string planPath = planSpec == "demo"
                    ? await CadSelfTestService.WriteDemoPlanAsync(Path.Combine(Path.GetTempPath(), "AIArchitectStudio", "План 1 этажа.png"))
                    : planSpec;
                await ShowPlanReconstructionAsync(planPath, int.TryParse(Env("PLANSTOREYS"), out int planStoreys) ? planStoreys : 2);
            };
        if (Env("ATTACH") == "1")
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2600);
                await ViewModel.DebugAttachmentsAsync(await CadSelfTestService.DemoPlanPngAsync());
            };
        // Блок девелопера 3–4: AIA_DEBUG_LIBRARY=1|columns|beams|slabs|foundations|walls|partitions — окно библиотеки ГОСТ/СП
        // (библиотека проекта только на сессию, без файлов на диске; с SELECT — назначение выделенным),
        // AIA_DEBUG_TIMELINE=1|demo — история изменений и контрольные точки (demo — ручные операции и два варианта),
        // AIA_DEBUG_HELP=quickstart|methods|keys|imports[;поиск] — справочный центр, AIA_DEBUG_TOUR=n — шаг n интерактивного тура.
        if (Env("LIBRARY") is { Length: > 0 } || Env("TIMELINE") is { Length: > 0 })
            ViewModel.UseSessionLibraries(new StandardLibraryService(Path.Combine(Path.GetTempPath(), "AIArchitectStudio", "Libraries-debug")) { IsSessionOnly = true });
        if (Env("LIBRARY") is { Length: > 0 } librarySpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2400);
                // «раздел;select» — выделить самую нагруженную колонну (назначение типа) и показать карточку ручного редактирования.
                var libraryParts = librarySpec.Split(';');
                if (libraryParts.Contains("select") && ViewModel.MostLoaded(BimCategory.Column) is { } loaded) ViewModel.SelectElement(loaded);
                if (ViewModel.Scene.SelectedElement is { } sample) ViewModel.Libraries.AddToUserLibrary(StandardLibraryService.CaptureEntry(sample, "К-1 корп."));
                await Task.Delay(500);
                ManualEditCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.1, AnimationDesired = false });
                await Task.Delay(400);
                await ShowLibraryAsync(libraryParts[0] == "1" ? null : libraryParts[0]);
            };
        if (Env("TIMELINE") is { Length: > 0 } historySpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2400);
                if (historySpec == "demo") ViewModel.DebugHistoryDemo();
                await ShowHistoryAsync(historySpec == "demo" ? (L.IsRussian ? "Вариант 3 · сетка 7,2 × 7,2 м" : "Option 3 · 7.2 × 7.2 m grid") : null);
            };
        if (Env("HELP") is { Length: > 0 } helpSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2000);
                var parts = helpSpec.Split(';');
                await ShowHelpAsync(parts[0], parts.Length > 1 ? parts[1] : null);
            };
        if (int.TryParse(Env("TOUR"), out int tourStep))
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2200);
                StartTour(persist: false, Math.Max(0, tourStep - 1));
            };
        // Модули: AIA_DEBUG_QUANTITIES=1 — окно «Ведомость объёмов», AIA_DEBUG_REBAR=1 — схема 3D-армирования (включает модуль).
        if (Env("QUANTITIES") is "1" or "params")
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2400);
                await ShowQuantitiesAsync(Env("QUANTITIES") == "params");
            };
        if (Env("REBAR") == "1")
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2000);
                if (!ViewModel.IsRebarModule) ViewModel.Modules = ViewModel.Modules with { Reinforcement3D = true };
                ViewModel.IsRebarView = true;
            };
        if (Env("NOWEB") == "1") RootGrid.Loaded += (_, _) => ViewModel.IsWebBridgeOpen = false;
        if (Env("CAMERA") is { Length: > 0 } camera)
        {
            // «x,y,z;tx,ty,tz» — положение камеры и точка взгляда.
            var v = camera.Split(';', ',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (v.Length == 6)
                RootGrid.Loaded += async (_, _) =>
                {
                    await Task.Delay(2500);
                    Viewport.DebugSetCamera(new SharpDX.Vector3(v[0], v[1], v[2]), new SharpDX.Vector3(v[3], v[4], v[5]));
                };
        }
        // Сценарии самопроверки: профиль выбранной колонны/балки, удаление (окно → проём), новое отверстие в плите.
        if (Env("SELECT") is { Length: > 0 } reselect && Env("AIJSON") is { Length: > 0 })
            ViewModel.SelectElement(ViewModel.Scene.Elements.FirstOrDefault(e => e.Name.Contains(reselect, StringComparison.OrdinalIgnoreCase)));
        if (Env("PROFILE") is { Length: > 0 } profile)
            switch (ProfileTypes.Normalize(profile))
            {
                case ProfileTypes.Circle: ViewModel.IsProfileCircle = true; break;
                case ProfileTypes.Pipe: ViewModel.IsProfilePipe = true; break;
                case ProfileTypes.IBeam: ViewModel.IsProfileIBeam = true; break;
                default: ViewModel.IsProfileRect = true; break;
            }
        if (Env("ROOF") is { Length: > 0 } roof && ViewModel.Scene.SelectedElement is { Type: ElementTypes.Roof } roofElement)
            ViewModel.Scene.UpdateElement(roofElement, r => r.RoofShape = RoofShapes.Normalize(roof));
        if (Env("DELETE") is { Length: > 0 } deletes)
            foreach (var _ in Enumerable.Range(0, int.TryParse(deletes, out int n) ? n : 1))
                if (ViewModel.DeleteSelectedCommand.CanExecute(null)) ViewModel.DeleteSelectedCommand.Execute(null);
        if (Env("SLABHOLE") is { Length: > 0 } slabName &&
            ViewModel.Scene.Elements.FirstOrDefault(e => e.CanHostSlabOpenings && e.Name.Contains(slabName, StringComparison.OrdinalIgnoreCase)) is { } slab)
        {
            var hole = ViewModel.Scene.AddSlabOpening(slab, 2.4, 3.2);
            ViewModel.Scene.Translate(hole, 2.5, 0, -1.5, recordUndo: false);
            if (Env("SELECTHOLE") == "1") ViewModel.SelectElement(hole);
        }
        if (Env("PDF") is { Length: > 0 } pdfPath && File.Exists(pdfPath))
            RootGrid.Loaded += async (_, _) => await PickPdfSheetsAsync(pdfPath, BlueprintKind.Auto, debugAutoSend: true);
        // Импорт IFC / DXF без диалогов: AIA_DEBUG_IFC=путь, AIA_DEBUG_DXF=путь (+ DXFLIFT=1 — автоподъём в 3D),
        // AIA_DEBUG_UNDERLAYPICK=x,y — выбрать контур подложки у точки чертежа (м) и показать пилюлю быстрого создания,
        // AIA_DEBUG_MAKE=wall|column|beam|slab|opening|axis — нажать кнопку пилюли.
        if (Env("IFC") is { Length: > 0 } ifcPath && File.Exists(ifcPath))
            RootGrid.Loaded += async (_, _) => await ViewModel.ImportIfcAsync(ifcPath);
        if (Env("DXF") is { Length: > 0 } dxfPath && File.Exists(dxfPath))
            RootGrid.Loaded += async (_, _) =>
            {
                await ViewModel.LoadDxfAsync(dxfPath, liftTo3D: Env("DXFLIFT") == "1");
                if (Env("UNDERLAYPICK") is { Length: > 0 } pick && ViewModel.Underlay is { } drawing)
                {
                    var v = pick.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var click = new DxfPoint(v[0], v[1]);
                    var contour = drawing.Contours.OrderBy(c => c.Nearest(click).Distance).FirstOrDefault();
                    ViewModel.SelectUnderlay(contour, click);
                    if (Env("MAKE") is { Length: > 0 } make) ViewModel.UnderlayCreateCommand.Execute(make);
                }
            };
        // Section Box: AIA_DEBUG_SECTION=MaxZ:0.05 (грань:значение) или long / trans / floor; AIA_DEBUG_SECTIONCAM=1 — камера по разрезу.
        if (Env("SECTION") is { Length: > 0 } section)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                switch (section)
                {
                    case "long": ViewModel.SectionLongitudinalCommand.Execute(null); break;
                    case "trans": ViewModel.SectionTransverseCommand.Execute(null); break;
                    case "floor": ViewModel.SectionFloorCutCommand.Execute(null); break;
                    default:
                        ViewModel.IsSectionBoxEnabled = true;
                        var parts = section.Split(':');
                        if (parts.Length == 2 && Enum.TryParse<Rendering.SectionFace>(parts[0], out var face))
                            ViewModel.SetSectionFace(face, double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                        break;
                }
                if (Env("SECTIONCAM") == "1")
                {
                    await Task.Delay(800);
                    ViewModel.SectionCameraCommand.Execute(null);
                }
            };
        // Выпуск листа ГОСТ без диалога: AIA_DEBUG_GOST=путь.pdf, AIA_DEBUG_GOSTVIEW=A3|section и т.п.
        if (Env("GOST") is { Length: > 0 } gostPath)
            RootGrid.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                // AIA_DEBUG_GOSTVIEW=album — альбом из 3 листов (план, разрез, спецификация и ведомость стали).
                if (Env("GOSTVIEW") == "album") await ViewModel.ExportGostAlbumAsync(gostPath);
                else if (MainViewModel.TryParseGost(Env("GOSTVIEW") ?? "A3|plan", out var format, out var view))
                    await ViewModel.ExportGostAsync(format, view, gostPath);
            };
        // Автотест обмена IFC/DXF и выпуска листов ГОСТ: отчёт selftest.txt, PDF и PNG — в папку; AIA_DEBUG_EXIT=1 — закрыть после теста.
        if (Env("SELFTEST") is { Length: > 0 } selfTestDir)
            RootGrid.Loaded += async (_, _) =>
            {
                try
                {
                    await CadSelfTestService.RunAsync(selfTestDir);
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(selfTestDir);
                    await File.WriteAllTextAsync(Path.Combine(selfTestDir, "selftest.txt"), "ИТОГ: ИСКЛЮЧЕНИЕ\n" + ex);
                }
                // Только Debug-автотест: немедленный выход процесса (скрипт проверки ждёт его завершения);
                // запущенный приложением сервер Ollama останавливается и здесь (событие Closed не наступит).
                if (Env("EXIT") == "1")
                {
                    App.Services.GetService<OllamaProcessManager>()?.Dispose();
                    Environment.Exit(0);
                }
            };
        // Демонстрационный комплект: AIA_DEBUG_DEMOKIT=папка DemoKit;отчёт.txt — прайс, библиотека и план читаются сервисами
        // (нет PNG плана — записывается синтетический); вне основного автотеста, AIA_DEBUG_EXIT=1 — закрыть после проверки.
        if (Env("DEMOKIT") is { Length: > 0 } demoKitSpec)
            RootGrid.Loaded += async (_, _) =>
            {
                var parts = demoKitSpec.Split(';', 2);
                string report = parts.Length > 1 ? parts[1] : Path.Combine(parts[0], "..", "demokit-check.txt");
                try
                {
                    await CadSelfTestService.VerifyDemoKitAsync(parts[0], report);
                }
                catch (Exception ex)
                {
                    await File.WriteAllTextAsync(report, "ИТОГ: ИСКЛЮЧЕНИЕ\n" + ex);
                }
                if (Env("EXIT") == "1")
                {
                    App.Services.GetService<OllamaProcessManager>()?.Dispose();
                    Environment.Exit(0);
                }
            };
        if (Env("MENU") == "model") RootGrid.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => ModelFlyout.ShowAt(ModelButton));
        if (Env("ERRORLOG") == "1") RootGrid.Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => ErrorLogFlyout.ShowAt(ErrorInfoBar));
    }

    /// <summary>Только Debug (AIA_DEBUG_EXPORT=папка): экспорт текущей сцены во все три формата без диалогов.</summary>
    private async Task DebugExportAllAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        string name = ProjectFileService.SafeFileName(ViewModel.Scene.ProjectName);
        foreach (var format in new[] { CadExportFormat.Ifc, CadExportFormat.Dxf, CadExportFormat.Obj })
            await ViewModel.ExportAsync(format, Path.Combine(directory, name + CadExportService.Extension(format)));
    }
#endif
}
