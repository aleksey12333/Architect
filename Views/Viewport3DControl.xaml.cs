using System.ComponentModel;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Models.Transactions;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Rendering;
using AIArchitectStudio.ViewModels;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SharpDX;
using Color = Windows.UI.Color;
using FillMode = SharpDX.Direct3D11.FillMode;
using CullMode = SharpDX.Direct3D11.CullMode;
using PerspectiveCamera = HelixToolkit.WinUI.PerspectiveCamera;
using OrthographicCamera = HelixToolkit.WinUI.OrthographicCamera;

namespace AIArchitectStudio.Views;

/// <summary>
/// 3D/2D вьюпорт HelixToolkit (SharpDX): программная инициализация по Loaded, Y-Up, студийный свет,
/// двухуровневая сетка 100×100 м, строительные оси, процедурная геометрия всех типов элементов,
/// выбор кликом, Gizmo X/Y/Z с перетаскиванием (шаг 0.1 м) и синхронным переносом проёмов стены.
/// </summary>
public sealed partial class Viewport3DControl : UserControl
{
    private const float GridHalfSize = 50f;
    private const double SnapStep = 0.1;

    private MainViewModel? _vm;
    private SceneStateService? _scene;
    private ThemeService? _theme;
    private Viewport3DX? _viewport;
    private DefaultEffectsManager? _effects;

    private readonly GroupModel3D _elementsGroup = new();
    private readonly GroupModel3D _axesGroup = new();
    private readonly TopMostGroup3D _overlayGroup = new() { EnableTopMost = true };
    private readonly LineGeometryModel3D _gridMinor = new() { Thickness = 0.5, IsHitTestVisible = false };
    private readonly LineGeometryModel3D _gridMajor = new() { Thickness = 0.9, IsHitTestVisible = false };
    private readonly AmbientLight3D _ambient = new();
    private readonly DirectionalLight3D _keyLight = new() { Direction = new Vector3(0.45f, -1f, -0.65f) };
    private readonly DirectionalLight3D _fillLight = new() { Direction = new Vector3(-0.6f, -0.35f, 0.75f) };

    /// <summary>Модели элементов по Id и обратная карта «модель → элемент» для хит-теста.</summary>
    private readonly Dictionary<string, List<Element3D>> _modelsById = [];
    private readonly Dictionary<Element3D, ArchitecturalElement> _hitMap = [];
    private readonly Dictionary<MeshGeometryModel3D, MaterialKind> _partKinds = [];
    private readonly Dictionary<string, ElementGeometry> _geometryById = [];

    private readonly Dictionary<Element3D, Vector3> _gizmoAxes = [];
    private HashSet<string> _lastIds = [];

    // Подложка DXF и секущий параллелепипед.
    private readonly GroupModel3D _underlayGroup = new();
    private readonly GroupModel3D _sectionGroup = new();
    private readonly Dictionary<Element3D, SectionFace> _sectionHandles = [];
    private bool _sectionRebuildQueued;
    private SectionFace? _sectionCameraFace;
    private SectionFace? _dragFace;
    private double _dragFaceStart;

    // Карта напряжений (собственные материалы моделей, анимация перекраски), контуры продавливания, подсветка коллизий.
    private readonly GroupModel3D _stressGroup = new();
    private readonly TopMostGroup3D _clashGroup = new() { EnableTopMost = true };
    private readonly Dictionary<MeshGeometryModel3D, (PhongMaterial Material, Color4 From, Color4 To)> _stress = [];
    private DispatcherTimer? _stressTimer;
    private DateTime _stressStart;
    private DispatcherTimer? _cameraTimer;

    // Состояние перетаскивания Gizmo.
    private ArchitecturalElement? _dragElement;
    private Vector3 _dragAxis;
    private Vector3 _dragPlanePoint;
    private Vector3 _dragPlaneNormal;
    private Vector3 _dragStartHit;
    private double[] _dragStartPosition = [0, 0, 0];
    private bool _dragUndoPushed;

    public Viewport3DControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Подключает ViewModel; сам Viewport3DX создаётся только после Loaded.</summary>
    public void Initialize(MainViewModel viewModel)
    {
        _vm = viewModel;
        _scene = viewModel.Scene;
        _theme = viewModel.Theme;
        if (IsLoaded) CreateViewport();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_viewport is null) CreateViewport();
        // Повторная загрузка (перенос контрола в дереве) — подписки восстанавливаются, иначе вьюпорт «замерзает».
        else if (!_subscribed)
        {
            Subscribe();
            RebuildScene();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    /// <summary>
    /// Закрытие окна: отписка от событий сцены и ViewModel, остановка таймеров и рендер-хоста Helix с освобождением
    /// ресурсов DirectX. Поток очереди действий рендер-хоста (AsyncActionThread) не фоновый: без EndD3D он удерживает
    /// процесс после закрытия окна — приложение исчезает с экрана, но остаётся в памяти.
    /// </summary>
    public void Shutdown()
    {
        Unsubscribe();
        // Ссылка на вьюпорт сохраняется: уже поставленные в очередь перестроения не должны обращаться к null.
        if (_viewport is { } viewport)
        {
            try
            {
                viewport.RenderHost?.EndD3D();
                viewport.RenderHost?.Dispose();
            }
            catch (Exception ex) when (ex is SharpDX.SharpDXException or ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                App.WriteCrashLog("viewport-shutdown", ex);
            }
        }
        _effects?.Dispose();
        _effects = null;
    }

    private bool _subscribed;

    private void Unsubscribe()
    {
        if (_vm is null || !_subscribed) return;
        _subscribed = false;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.ZoomExtentsRequested -= OnZoomExtentsRequested;
        _scene!.DocumentChanged -= OnDocumentChanged;
        _scene.PropertyChanged -= OnScenePropertyChanged;
        _scene.GridAxesChanged -= OnGridAxesChanged;
        _theme!.ThemeChanged -= OnThemeChanged;
        _vm.UnderlayChanged -= OnUnderlayChanged;
        _vm.SectionBoxChanged -= OnSectionBoxChanged;
        _vm.SectionCameraRequested -= OnSectionCameraRequested;
        _vm.StressMapChanged -= OnStressMapChanged;
        _vm.ClashHighlightChanged -= OnClashHighlightChanged;
        _vm.ClashFocusRequested -= OnClashFocusRequested;
        _vm.SolarChanged -= OnSolarChanged;
        _vm.StructuralOverlayChanged -= OnStructuralOverlayChanged;
        _scene.SelectionChanged -= OnSelectionChanged;
        _scene.DimensionsChanged -= OnDimensionsChanged;
        _stressTimer?.Stop();
        _cameraTimer?.Stop();
        _walkTimer?.Stop();
        SetOverlayTracking(false);
    }

    private void OnStressMapChanged(object? sender, bool toggled) => DispatcherQueue.TryEnqueue(() =>
    {
        ApplyStress(animate: toggled && _vm?.IsStressMapEnabled == true);
        // Диаметры схемы армирования зависят от K_исп.
        QueueRebarRebuild();
    });

    private void OnClashHighlightChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RebuildClashHighlight);

    private void OnClashFocusRequested(object? sender, ClashFocus focus) => DispatcherQueue.TryEnqueue(() => AnimateCameraTo(focus));

    private void OnUnderlayChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RebuildUnderlay);

    /// <summary>Сдвиг грани (ползунок или мышь) — одна перестройка отсечения на кадр UI, без очереди из десятков пересборок.</summary>
    private void OnSectionBoxChanged(object? sender, EventArgs e)
    {
        if (_sectionRebuildQueued) return;
        _sectionRebuildQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _sectionRebuildQueued = false;
            if (_vm?.IsSectionBoxEnabled != true && _sectionCameraFace is not null)
            {
                _sectionCameraFace = null;
                ResetCamera();
            }
            RebuildSectionBox();
            RebuildScene();
        });
    }

    private void OnSectionCameraRequested(object? sender, SectionFace face) => DispatcherQueue.TryEnqueue(() => ApplySectionCamera(face));

    private void OnGridAxesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RebuildAxes);

    // ───────────────────────────── Инициализация ─────────────────────────────

    private void CreateViewport()
    {
        if (_viewport is not null || _vm is null) return;

        _effects = new DefaultEffectsManager();
        _viewport = new Viewport3DX
        {
            EffectsManager = _effects,
            ModelUpDirection = new Vector3(0, 1, 0),
            ShowViewCube = true,
            ShowCoordinateSystem = true,
            ViewCubeHorizontalPosition = -0.88,
            ViewCubeVerticalPosition = 0.62,
            ViewCubeSize = 0.9,
            CoordinateSystemHorizontalPosition = -0.9,
            CoordinateSystemVerticalPosition = -0.82,
            CoordinateSystemSize = 0.9,
            IsViewCubeEdgeClicksEnabled = true,
            ZoomAroundMouseDownPoint = true,
            RotateAroundMouseDownPoint = true,
            IsInertiaEnabled = true,
            FXAALevel = HelixToolkit.SharpDX.Core.FXAALevel.High,
            EnableSSAO = false,
            ShowFrameRate = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _viewport.Items.Add(_ambient);
        _viewport.Items.Add(_keyLight);
        _viewport.Items.Add(_fillLight);
        _viewport.Items.Add(_gridMinor);
        _viewport.Items.Add(_gridMajor);
        _viewport.Items.Add(_shadowGroup);
        _viewport.Items.Add(_axesGroup);
        _viewport.Items.Add(_siteGroup);
        _viewport.Items.Add(_sitePreviewGroup);
        _viewport.Items.Add(_underlayGroup);
        _viewport.Items.Add(_elementsGroup);
        _viewport.Items.Add(_stressGroup);
        _viewport.Items.Add(_sectionGroup);
        _viewport.Items.Add(_overlayGroup);
        _viewport.Items.Add(_clashGroup);
        _viewport.Items.Add(_rebarGroup);
        _viewport.Items.Add(_structuralGroup);
        _viewport.Items.Add(_measureGroup);
        _viewport.Items.Add(_measureLiveGroup);
        _viewport.Items.Add(_snapGroup);

        BuildGrid();

        _viewport.OnMouse3DDown += OnMouse3DDown;
        // handledEventsToo: Helix помечает события указателя обработанными внутри своих InputController.
        _viewport.AddHandler(PointerPressedEvent, new PointerEventHandler(OnViewportPointerPressed), true);
        _viewport.AddHandler(PointerMovedEvent, new PointerEventHandler(OnViewportPointerMoved), true);
        _viewport.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnViewportPointerReleased), true);
        _viewport.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { EndDrag(); EndLook(); }), true);
        _viewport.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => OnViewportPointerExited()), true);
        _viewport.RenderExceptionOccurred += (_, ex) =>
            _vm?.ShowError("3D", ex.Exception?.Message ?? "Render error", "AIA-RENDER-001", ex.Exception?.ToString());

        HostGrid.Children.Add(_viewport);
        Subscribe();

        ApplyTheme(_theme!.Current);
        RebuildScene();
        RebuildSite();
        ResetCamera();

        // Начальный фокус — на 3D-холст (без рамки клавиатурного фокуса на первой кнопке бара).
        IsTabStop = true;
        Focus(FocusState.Pointer);
    }

    // ───────────────────────────── Подписки ─────────────────────────────

    private void Subscribe()
    {
        if (_vm is null || _subscribed) return;
        _subscribed = true;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ZoomExtentsRequested += OnZoomExtentsRequested;
        _scene!.DocumentChanged += OnDocumentChanged;
        _scene.PropertyChanged += OnScenePropertyChanged;
        _scene.GridAxesChanged += OnGridAxesChanged;
        _theme!.ThemeChanged += OnThemeChanged;
        _vm.UnderlayChanged += OnUnderlayChanged;
        _vm.SectionBoxChanged += OnSectionBoxChanged;
        _vm.SectionCameraRequested += OnSectionCameraRequested;
        _vm.StressMapChanged += OnStressMapChanged;
        _vm.ClashHighlightChanged += OnClashHighlightChanged;
        _vm.ClashFocusRequested += OnClashFocusRequested;
        _vm.SolarChanged += OnSolarChanged;
        _vm.StructuralOverlayChanged += OnStructuralOverlayChanged;
        _scene.SelectionChanged += OnSelectionChanged;
        _scene.DimensionsChanged += OnDimensionsChanged;
        _scene.SitePlanChanged += (_, _) => QueueSiteRebuild();
        if (_vm is not null) _vm.SiteLayersChanged += (_, _) => QueueSiteRebuild();
        // Волна 6: площадки зданий срезают рельеф — платформа участка перестраивается после правок (с задержкой).
        _scene.DocumentChanged += (_, _) => { if (_scene.SitePlan is not null) QueueSiteTerrainRebuild(); };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ViewMode):
                _sectionCameraFace = null;
                if (_vm!.IsWalkthrough && _vm.ViewMode != ViewportMode.Perspective3D) _vm.IsWalkthrough = false;
                RebuildScene();
                ResetCamera();
                RebuildAxes();
                if (_vm.IsSunLighting) RebuildShadows();
                RebuildDimensions();
                break;
            case nameof(MainViewModel.IsMeasureMode):
                OnMeasureModeChanged();
                break;
            case nameof(MainViewModel.IsClayMode):
                RebuildScene();
                RebuildDimensions();
                break;
            case nameof(MainViewModel.IsWalkthrough):
                if (_vm!.IsWalkthrough) EnterWalk();
                else ExitWalk();
                break;
            case nameof(MainViewModel.ShowGridAxes):
                _axesGroup.Visibility = _vm!.ShowGridAxes ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(MainViewModel.IsWireframe):
                ApplyFillMode();
                break;
            case nameof(MainViewModel.IsRebarView):
            case nameof(MainViewModel.Modules):
                QueueRebarRebuild();
                break;
            case nameof(MainViewModel.IsSiteEditing):
                QueueSiteRebuild();
                break;
            case nameof(MainViewModel.SelectedSiteVertex):
                RefreshSiteHandleColors();
                break;
            case nameof(MainViewModel.IsStructuralMode):
                ApplyStress(animate: _vm!.IsStructuralMode);
                QueueStructuralRebuild();
                break;
        }
    }

    private void OnScenePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SceneStateService.SelectedElement)) return;
        UpdateSelectionVisuals();
        QueueRebarRebuild();
    }

    /// <summary>
    /// DocumentChanged: перестраивается только геометрия изменённых элементов и их хостов, новые добавляются, удалённые
    /// убираются. Полная перестройка — при новой сцене, смене фильтра этажа, изменении построек или очень большом пакете.
    /// </summary>
    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (e.IsReset || e.Added.Count + e.Deleted.Count > Math.Max(64, _scene!.Elements.Count / 2))
        {
            DispatcherQueue.TryEnqueue(RebuildWholeScene);
            return;
        }
        if (DispatcherQueue.HasThreadAccess) ApplyDocumentChanges(e);
        else DispatcherQueue.TryEnqueue(() => ApplyDocumentChanges(e));
    }

    private void RebuildWholeScene()
    {
        RebuildScene();
        // Подложка лежит на отметке активного этажа фильтра.
        RebuildUnderlay();
        _walkSurfaces = null;
        _walkObstacles = null; _walkWorld = null;
        _measureTargets = null;
        QueueShadowRebuild();
    }

    private void ApplyDocumentChanges(DocumentChangedEventArgs e)
    {
        if (_viewport is null || _scene is null) return;
        // Перестраиваются сами элементы и их хосты (вырезы проёмов в стенах и плитах зависят от положения проёмов).
        var rebuild = new Dictionary<string, ArchitecturalElement?>(StringComparer.Ordinal);
        bool full = false;
        void Host(ArchitecturalElement el)
        {
            if (el.HostElementId is { } hostId) rebuild.TryAdd(hostId, null);
            // Непривязанное отверстие режет плиты своего этажа по попаданию в контур — перестраиваем всю сцену.
            else if (el.Category == BimCategory.SlabOpening) full = true;
        }
        foreach (var el in e.Deleted)
        {
            RemoveElementModels(el.Id);
            _lastIds.Remove(el.Id);
            Host(el);
        }
        foreach (var el in e.Added)
        {
            rebuild[el.Id] = el;
            _lastIds.Add(el.Id);
            Host(el);
        }
        foreach (var el in e.Modified)
        {
            rebuild[el.Id] = el;
            Host(el);
        }
        if (full)
        {
            RebuildScene();
        }
        else if (rebuild.Count > 0)
        {
            foreach (var (id, known) in rebuild)
            {
                RemoveElementModels(id);
                if ((known ?? _scene.FindById(id)) is { } el) AddElementModels(el);
            }
            ApplyFillMode();
            if (StressColoring) ApplyStress(animate: false);
            RebuildClashHighlight();
            QueueRebarRebuild();
            QueueStructuralRebuild();
        }

        RebuildAxes();
        UpdateSelectionVisuals();
        _walkSurfaces = null;
        _walkObstacles = null; _walkWorld = null;
        _measureTargets = null;
        QueueShadowRebuild();
    }

    private void OnThemeChanged(object? sender, ThemeDefinition theme)
    {
        ApplyTheme(theme);
        QueueRebarRebuild();
        QueueSiteRebuild();
    }

    /// <summary>
    /// В очередь UI, а не сразу: перестройка геометрии после SceneChanged тоже отложена (TryEnqueue), и габариты
    /// новой постройки («+ К участку», ответ ИИ) должны учитываться при вписывании камеры.
    /// </summary>
    private void OnZoomExtentsRequested(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ResetCamera);

    // ───────────────────────────── Тема ─────────────────────────────

    private Color _edgeColor = Color.FromArgb(255, 40, 40, 44);
    private static readonly Color PhysicsErrorColor = Color.FromArgb(255, 255, 69, 58);

    private void ApplyTheme(ThemeDefinition theme)
    {
        if (_viewport is null) return;
        _viewport.BackgroundColor = theme.ViewportBackground;
        _viewport.CoordinateSystemLabelForeground = theme.TextSecondary;
        _gridMinor.Color = theme.GridMinor;
        _gridMajor.Color = theme.GridMajor;
        _ambient.Color = theme.AmbientLight;
        _keyLight.Color = theme.KeyLight;
        _fillLight.Color = theme.FillLight;
        _edgeColor = theme.IsLight ? Color.FromArgb(255, 72, 72, 78) : Color.FromArgb(255, 34, 34, 38);

        foreach (var models in _modelsById.Values)
            foreach (var m in models.OfType<LineGeometryModel3D>())
                m.Color = _hitMap.TryGetValue(m, out var owner) && owner.HasPhysicsError ? PhysicsErrorColor : EdgeColor;

        ApplyViewCubeTexture(theme);
        RebuildAxes();
        RebuildDimensions();
        RebuildUnderlay();
        RebuildSectionBox();
        UpdateSelectionVisuals();
        // Инсоляция перекрывает свет темы солнечным; цвет теней зависит от светлой/тёмной темы.
        ApplySun();
    }

    /// <summary>Монохромный ViewCube в цветах темы (вместо стандартной разноцветной текстуры Helix).</summary>
    private void ApplyViewCubeTexture(ThemeDefinition theme)
    {
        if (_viewport is null || _effects is not IDevice2DResources device) return;
        try
        {
            var face = ToColor4(theme.IsLight ? Color.FromArgb(255, 255, 255, 255) : theme.Card);
            var top = ToColor4(theme.IsLight ? Color.FromArgb(255, 242, 242, 247) : Blend(theme.Card, Color.FromArgb(255, 255, 255, 255), 0.08));
            var text = ToColor4(theme.TextPrimary);
            var stream = BitmapExtensions.CreateViewBoxTexture(device,
                "СПЕРЕДИ", "СЗАДИ", "СЛЕВА", "СПРАВА", "СВЕРХУ", "СНИЗУ",
                face, face, face, face, top, face,
                text, text, text, text, text, text,
                "Segoe UI", SharpDX.DirectWrite.FontWeight.SemiBold, SharpDX.DirectWrite.FontStyle.Normal, 22, 128);
            _viewport.ViewCubeTexture = new TextureModel(stream, true);
        }
        catch (SharpDX.SharpDXException)
        {
            // Нет Direct2D-ресурсов (например, WARP без D2D) — остаётся стандартная текстура Helix.
        }
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ───────────────────────────── Сетка 100×100 м ─────────────────────────────

    private void BuildGrid()
    {
        var minor = new LineBuilder();
        var major = new LineBuilder();
        for (int i = -(int)GridHalfSize; i <= GridHalfSize; i++)
        {
            var target = i % 5 == 0 ? major : minor;
            target.AddLine(new Vector3(i, 0, -GridHalfSize), new Vector3(i, 0, GridHalfSize));
            target.AddLine(new Vector3(-GridHalfSize, 0, i), new Vector3(GridHalfSize, 0, i));
        }
        _gridMinor.Geometry = minor.ToLineGeometry3D();
        _gridMajor.Geometry = major.ToLineGeometry3D();
    }

    // ───────────────────────────── Геометрия сцены ─────────────────────────────

    private void RebuildScene()
    {
        if (_viewport is null || _scene is null) return;

        _elementsGroup.Children.Clear();
        _modelsById.Clear();
        _hitMap.Clear();
        _partKinds.Clear();
        _partTints.Clear();
        _geometryById.Clear();
        _stress.Clear();

        foreach (var el in _scene.Elements)
            AddElementModels(el);

        ApplyFillMode();
        RebuildAxes();
        if (StressColoring) ApplyStress(animate: false);
        UpdateSelectionVisuals();
        RebuildClashHighlight();
        QueueRebarRebuild();
        QueueStructuralRebuild();

        // Камера «показать всё» при загрузке новой сцены (полная замена набора элементов).
        var ids = _scene.Elements.Select(e => e.Id).ToHashSet();
        bool replaced = ids.Count > 0 && (_lastIds.Count == 0 || !ids.Overlaps(_lastIds));
        _lastIds = ids;
        if (replaced) ResetCamera();
    }

    private void RemoveElementModels(string id)
    {
        if (!_modelsById.Remove(id, out var models)) return;
        foreach (var m in models)
        {
            _elementsGroup.Children.Remove(m);
            _hitMap.Remove(m);
            if (m is MeshGeometryModel3D mesh)
            {
                _partKinds.Remove(mesh);
                _partTints.Remove(mesh);
                _stress.Remove(mesh);
            }
        }
        _geometryById.Remove(id);
    }

    private void AddElementModels(ArchitecturalElement el)
    {
        if (!_scene!.IsElementVisible(el)) return;
        // 2D План — горизонтальный разрез: перекрытия, кровли и оболочки не закрывают планировку стен.
        if (_vm?.ViewMode == ViewportMode.Plan2D && (ElementTypes.IsCeilingType(el.Type) || el.Type == ElementTypes.CantileverSlab)) return;

        var group = _scene.FindGroup(el.BuildingGroupId);
        var ctx = new GeometryContext(_scene.ActiveLevelFilter, group?.OriginX ?? 0, group?.OriginZ ?? 0);
        var hosted = _scene.GetOpeningsFor(el);

        ElementGeometry geometry;
        ClippedGeometry? clipped = null;
        try
        {
            geometry = ElementGeometryBuilder.Build(el, hosted, ctx);
            // Section Box: честное отсечение полигонов и заливка плоскости среза (null — элемент целиком внутри коробки).
            if (_vm?.IsSectionBoxEnabled == true && !geometry.IsEmpty)
                clipped = SectionBoxClipper.Clip(geometry, _vm.SectionBox);
        }
        catch (Exception ex)
        {
            _vm?.ShowError("3D", $"{el.DisplayTitle}: {ex.Message}", "AIA-GEOMETRY-001", ex.ToString());
            return;
        }
        if (geometry.IsEmpty) return;

        var parts = clipped?.Parts ?? geometry.Parts;
        var edgeGeometry = clipped is null ? geometry.Edges : clipped.Edges;
        var transform = clipped is null ? geometry.Transform.ToDx() : Matrix.Identity;
        var models = new List<Element3D>();
        string? tint = CurvedDesign.FacadeColor(el);
        foreach (var part in parts)
        {
            bool transparent = ArchitecturalMaterials.IsTransparent(part.Material);
            var model = new MeshGeometryModel3D
            {
                Geometry = part.Mesh.ToMeshGeometry(),
                Material = MaterialFor(part.Material, tint),
                HxTransform3D = transform,
                CullMode = CullMode.None,
                IsTransparent = transparent,
                IsThrowingShadow = false
            };
            _partKinds[model] = part.Material;
            if (tint is not null && ArchitecturalMaterials.IsTintable(part.Material)) _partTints[model] = tint;
            models.Add(model);
        }
        // Заливка среза по материалам: несущий бетон — тёмным тоном, каждый слой пирога — своим.
        foreach (var capPart in clipped?.CapParts ?? [])
        {
            var cap = new MeshGeometryModel3D
            {
                Geometry = capPart.Mesh.ToMeshGeometry(),
                Material = MaterialFor(capPart.Material),
                CullMode = CullMode.None,
                IsThrowingShadow = false
            };
            _partKinds[cap] = capPart.Material;
            models.Add(cap);
        }
        if (edgeGeometry is { } edges)
        {
            // Элементы с ошибкой StructuralAuditor (висит в воздухе, проём без стены) обводятся красным.
            // Макет (White Clay): графитовые рёбра #2C2C2E чуть толще — подчёркивают грани плит, окон и колонн.
            bool clay = _vm?.IsClayMode == true;
            models.Add(new LineGeometryModel3D
            {
                Geometry = edges.ToLineGeometry(),
                Color = el.HasPhysicsError ? PhysicsErrorColor : EdgeColor,
                Thickness = el.HasPhysicsError ? 1.8 : clay ? 1.15 : 0.9,
                Smoothness = 0.5,
                // Небольшой сдвиг глубины: рёбра не мерцают на гранях, но и не просвечивают сквозь соседние объёмы.
                DepthBias = -30,
                HxTransform3D = transform
            });
        }

        foreach (var m in models)
        {
            _elementsGroup.Children.Add(m);
            _hitMap[m] = el;
        }
        _modelsById[el.Id] = models;
        _geometryById[el.Id] = geometry;
    }

    /// <summary>Материал части: архитектурный или единый гипс макета (White Clay).</summary>
    private PhongMaterial MaterialFor(MaterialKind kind) =>
        _vm?.IsClayMode == true ? ArchitecturalMaterials.GetClay(kind) : ArchitecturalMaterials.Get(kind);

    /// <summary>Части, окрашенные в цвет фасада с фотографии (волна 4).</summary>
    private readonly Dictionary<MeshGeometryModel3D, string> _partTints = [];

    private PhongMaterial MaterialFor(MaterialKind kind, string? tint) =>
        tint is not null && _vm?.IsClayMode != true && ArchitecturalMaterials.IsTintable(kind) ? ArchitecturalMaterials.GetTinted(kind, tint) : MaterialFor(kind);

    private PhongMaterial SelectedMaterialFor(MaterialKind kind, Color4 accent) =>
        _vm?.IsClayMode == true ? ArchitecturalMaterials.GetClaySelected(kind, accent) : ArchitecturalMaterials.GetSelected(kind, accent);

    /// <summary>Цвет рёбер: графит макета или тон темы.</summary>
    private Color EdgeColor => _vm?.IsClayMode == true ? ArchitecturalMaterials.ClayEdgeColor : _edgeColor;

    private void ApplyFillMode()
    {
        // Макет всегда сплошной: рёбра рисуются поверх заливки отдельными линиями.
        var mode = _vm?.IsWireframe == true && _vm.IsClayMode != true ? FillMode.Wireframe : FillMode.Solid;
        foreach (var model in _partKinds.Keys) model.FillMode = mode;
    }

    // ───────────────────────────── Строительные оси ─────────────────────────────

    /// <summary>
    /// Координационные оси каждой постройки на отметке каждого её этажа: на активном этаже фильтра (или первом этаже при
    /// показе всех) — яркие линии, марки на обоих концах и размеры; на остальных этажах — приглушённые линии и одна марка.
    /// В режиме фасада — вертикальные цифровые оси и линии уровней с отметками.
    /// </summary>
    private void RebuildAxes()
    {
        if (_viewport is null || _scene is null || _theme is null) return;
        _axesGroup.Children.Clear();

        var visible = _scene.VisibleElements.ToList();
        var grids = StructuralAxesBuilder.BuildGrids(visible, _scene.GridAxes.Where(a => _scene.FindGroup(a.BuildingGroupId) is not { IsVisible: false }).ToList());
        if (grids.Count == 0) return;

        var theme = _theme.Current;
        var accentLine = Blend(theme.AxisLine, theme.ViewportBackground, 0.25);
        var mutedLine = Blend(theme.AxisLine, theme.ViewportBackground, 0.72);
        var bubbleBg = ToColor4(theme.AxisLine);
        var mutedBubbleBg = ToColor4(Blend(theme.AxisLine, theme.ViewportBackground, 0.5));
        var bubbleText = ToColor4(theme.OnAccent);
        var dimText = ToColor4(theme.TextPrimary);
        var levelText = ToColor4(theme.TextSecondary);
        var none = new Color4(0, 0, 0, 0);
        var accentLines = new LineBuilder();
        var mutedLines = new LineBuilder();
        bool anyAccent = false, anyMuted = false;
        int filter = _scene.ActiveLevelFilter;

        // Камера по разрезу: в плоскости среза — линии уровней с отметками СПДС и оси, перпендикулярные разрезу.
        bool sectionOverlay = _sectionCameraFace is { } sf && sf is not (SectionFace.MinY or SectionFace.MaxY) && _vm?.IsSectionBoxEnabled == true;
        if (sectionOverlay)
        {
            var face = _sectionCameraFace!.Value;
            var box = _vm!.SectionBox;
            var n = SectionBox.Normal(face).ToDx();
            float plane = (float)box.Value(face);
            bool alongX = face is SectionFace.MinZ or SectionFace.MaxZ;
            Vector3 P(double u, double y) => alongX
                ? new Vector3((float)u, (float)y, plane + n.Z * 0.08f)
                : new Vector3(plane + n.X * 0.08f, (float)y, (float)u);
            var right = Vector3.Cross(-n, Vector3.UnitY);
            double u0 = alongX ? box.MinX : box.MinZ, u1 = alongX ? box.MaxX : box.MaxZ;
            bool rightIsMax = (alongX ? right.X : right.Z) > 0;
            double uRight = rightIsMax ? u1 + 1.6 : u0 - 1.6;
            foreach (var grid in grids)
            {
                double yLow = Math.Max(box.MinY, (grid.Levels.Count > 0 ? grid.Levels.Min(l => l.Elevation) : 0) - 1.0);
                double yHigh = Math.Min(box.MaxY, grid.TopY + 0.8);
                foreach (var level in grid.Levels)
                {
                    mutedLines.AddLine(P(u0, level.Elevation), P(u1, level.Elevation));
                    AddLabel(GostDrawingPdfExporter.Elevation(level.Elevation), P(uRight, level.Elevation), dimText, none, 13, bold: false);
                    anyMuted = true;
                }
                var cross = grid.Axes.Where(a => a.IsNumeric == alongX).OrderBy(a => a.Coordinate).ToList();
                for (int i = 0; i < cross.Count; i++)
                {
                    accentLines.AddLine(P(cross[i].Coordinate, yLow - 0.4), P(cross[i].Coordinate, yHigh + 0.4));
                    AddLabel(cross[i].Label, P(cross[i].Coordinate, yLow - 1.2), bubbleText, bubbleBg, 15, bold: true);
                    if (i > 0)
                        AddLabel(StructuralAxesBuilder.Millimeters(cross[i].Coordinate - cross[i - 1].Coordinate),
                            P((cross[i].Coordinate + cross[i - 1].Coordinate) / 2, yLow - 0.55), dimText, none, 12, bold: false);
                    anyAccent = true;
                }
            }
        }

        foreach (var grid in grids)
        {
            if (sectionOverlay) break;
            if (_vm?.ViewMode == ViewportMode.Section2D)
            {
                var (axes, dims) = StructuralAxesBuilder.ElevationAxes(grid);
                foreach (var a in axes)
                {
                    accentLines.AddLine(a.Start.ToDx(), a.End.ToDx());
                    AddLabel(a.Label, a.LabelStart.ToDx(), bubbleText, bubbleBg, 15, bold: true);
                    AddLabel(a.Label, a.LabelEnd.ToDx(), bubbleText, bubbleBg, 15, bold: true);
                    anyAccent = true;
                }
                foreach (var d in dims) AddLabel(d.Text, d.Position.ToDx(), dimText, none, 13, bold: false);

                // Уровни этажей: горизонтальные линии на фасаде и отметки справа.
                float x0 = (float)grid.MinX - 2f, x1 = (float)grid.MaxX + 2f, front = (float)grid.MaxZ + 1.0f;
                foreach (var level in grid.Levels)
                {
                    float y = (float)level.Elevation;
                    mutedLines.AddLine(new Vector3(x0, y, front), new Vector3(x1, y, front));
                    AddLabel(StructuralAxesBuilder.ElevationText(level.Elevation), new Vector3(x1 + 1.6f, y, front), levelText, none, 12, bold: false);
                    anyMuted = true;
                }
                continue;
            }

            var levels = grid.Levels.Where(l => filter < 0 || l.Level <= filter).ToList();
            if (levels.Count == 0) levels = grid.Levels.Take(1).ToList();
            int accentLevel = filter >= 0 && levels.Any(l => l.Level == filter) ? filter : levels[0].Level;
            // На плане (вид сверху) сетки этажей совпадают — показываем только сетку активного этажа.
            if (_vm?.ViewMode == ViewportMode.Plan2D) levels = levels.Where(l => l.Level == accentLevel).ToList();

            foreach (var level in levels)
            {
                bool accent = level.Level == accentLevel;
                float y = (float)level.Elevation + 0.02f;
                var axes = grid.AxesAt(y);
                foreach (var a in axes)
                {
                    (accent ? accentLines : mutedLines).AddLine(a.Start.ToDx(), a.End.ToDx());
                    if (accent)
                    {
                        AddLabel(a.Label, a.LabelStart.ToDx(), bubbleText, bubbleBg, 15, bold: true);
                        AddLabel(a.Label, a.LabelEnd.ToDx(), bubbleText, bubbleBg, 15, bold: true);
                    }
                    else AddLabel(a.Label, a.LabelStart.ToDx(), bubbleText, mutedBubbleBg, 11, bold: true);
                }
                if (accent)
                {
                    foreach (var d in grid.DimensionsAt(y)) AddLabel(d.Text, d.Position.ToDx(), dimText, none, 13, bold: false);
                    anyAccent = true;
                }
                else anyMuted = true;

                // Отметка этажа у начала первой буквенной (или цифровой) оси.
                var anchor = axes.FirstOrDefault(a => a.Start.X < a.End.X - 0.1f) ?? axes[0];
                AddLabel(StructuralAxesBuilder.ElevationText(level.Elevation), anchor.LabelStart.ToDx() + new Vector3(-2.2f, 0, 0.9f),
                    accent ? dimText : levelText, none, accent ? 12 : 10, bold: false);
            }
        }

        if (anyAccent)
            _axesGroup.Children.Add(new LineGeometryModel3D
            {
                Geometry = accentLines.ToLineGeometry3D(),
                // Оси — приглушённый акцент, чтобы не спорить с геометрией здания.
                Color = accentLine,
                Thickness = 0.9,
                DepthBias = -100,
                IsHitTestVisible = false
            });
        if (anyMuted)
            _axesGroup.Children.Add(new LineGeometryModel3D
            {
                Geometry = mutedLines.ToLineGeometry3D(),
                Color = mutedLine,
                Thickness = 0.6,
                DepthBias = -100,
                IsHitTestVisible = false
            });

        _axesGroup.Visibility = _vm?.ShowGridAxes == false ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddLabel(string text, Vector3 position, Color4 foreground, Color4 background, int size, bool bold)
    {
        var label = new BillboardSingleText3D
        {
            TextInfo = new TextInfo(text, position),
            FontColor = foreground,
            BackgroundColor = background,
            FontSize = size,
            FontFamily = "Segoe UI",
            FontWeight = bold ? SharpDX.DirectWrite.FontWeight.SemiBold : SharpDX.DirectWrite.FontWeight.Normal,
            Padding = new HelixToolkit.SharpDX.Core.Model.Scene2D.Thickness(7, 3, 7, 3)
        };
        _axesGroup.Children.Add(new BillboardTextModel3D { Geometry = label, FixedSize = true, IsHitTestVisible = false });
    }

    // ───────────────────────────── Выделение и Gizmo ─────────────────────────────

    private void UpdateSelectionVisuals()
    {
        if (_viewport is null || _scene is null || _theme is null) return;
        var accent = ToColor4(_theme.Current.Selection);

        // В режиме карты напряжений модели сохраняют свои материалы шкалы K_исп (выделение — рамкой и Gizmo).
        foreach (var (model, kind) in _partKinds)
            if (!_stress.ContainsKey(model)) model.Material = MaterialFor(kind, _partTints.GetValueOrDefault(model));

        _overlayGroup.Children.Clear();
        _gizmoAxes.Clear();
        _gizmoTips.Clear();
        _handleModels.Clear();
        _handleCenters.Clear();
        _vertexHandles.Clear();
        _edgeHandles.Clear();

        if (_scene.SelectedElement is not { } sel || !_modelsById.TryGetValue(sel.Id, out var models))
        {
            if (_activeElementId is not null) ResetActiveTarget();
            return;
        }
        if (_activeElementId != sel.Id) ResetActiveTarget(sel);

        // Мультивыделение: каждый выделенный элемент подсвечивается материалом и контуром.
        foreach (var item in _scene.SelectedElements.Count > 0 ? _scene.SelectedElements.ToList() : new List<ArchitecturalElement> { sel })
        {
            if (!_modelsById.TryGetValue(item.Id, out var itemModels) || !_geometryById.TryGetValue(item.Id, out var itemGeometry)) continue;
            foreach (var model in itemModels.OfType<MeshGeometryModel3D>())
                if (_partKinds.TryGetValue(model, out var kind) && !_stress.ContainsKey(model))
                    model.Material = SelectedMaterialFor(kind, accent);
            var itemBox = itemGeometry.LocalBounds.ToDx();
            _overlayGroup.Children.Add(new LineGeometryModel3D
            {
                Geometry = LineBuilder.GenerateBoundingBox(new BoundingBox(itemBox.Minimum - new Vector3(0.03f), itemBox.Maximum + new Vector3(0.03f))),
                Color = _theme.Current.Selection,
                Thickness = ReferenceEquals(item, sel) ? 1.6 : 1.2,
                HxTransform3D = itemGeometry.Transform.ToDx(),
                IsHitTestVisible = false
            });
        }

        var geometry = _geometryById[sel.Id];
        var box = geometry.LocalBounds.ToDx();

        // Gizmo: три стрелки X/Y/Z из центра элемента (выступают за габарит, чтобы их можно было схватить).
        var center = new Vector3((float)sel.X, (float)sel.Y, (float)sel.Z);
        float extent = (box.Maximum - box.Minimum).Length() / 2;
        float length = Math.Clamp(extent + 1.2f, 1.6f, 14f);
        float diameter = Math.Clamp(length * 0.045f, 0.06f, 0.35f);
        AddGizmoArrow(center, Vector3.UnitX, length, diameter, new Color4(1.0f, 0.27f, 0.23f, 1f));
        AddGizmoArrow(center, Vector3.UnitY, length, diameter, new Color4(0.19f, 0.82f, 0.35f, 1f));
        AddGizmoArrow(center, Vector3.UnitZ, length, diameter, new Color4(0.04f, 0.52f, 1.0f, 1f));

        // Торцевые ручки Push/Pull и пины вершин контура Polygon2D — только при одиночном выделении.
        if (!_scene.IsMultiSelection)
        {
            AddPushPullHandles(sel, extent);
            AddVertexHandles(sel);
        }
    }

    private void AddGizmoArrow(Vector3 origin, Vector3 axis, float length, float diameter, Color4 color)
    {
        var builder = new MeshBuilder();
        builder.AddArrow(origin, origin + axis * length, diameter, diameter * 3.2, 16);
        builder.AddSphere(origin, diameter * 0.9, 12, 12);
        var model = new MeshGeometryModel3D
        {
            Geometry = builder.ToMeshGeometry3D(),
            Material = new PhongMaterial
            {
                DiffuseColor = color,
                AmbientColor = color,
                EmissiveColor = new Color4(color.Red * 0.55f, color.Green * 0.55f, color.Blue * 0.55f, 1f),
                SpecularColor = new Color4(0.3f, 0.3f, 0.3f, 1f),
                SpecularShininess = 30
            },
            CullMode = CullMode.None
        };
        _overlayGroup.Children.Add(model);
        _gizmoAxes[model] = axis;
        _gizmoTips[axis] = (origin, origin + axis * length);
    }

    // ───────────────────────────── Мышь: выбор и перетаскивание ─────────────────────────────

    private void OnMouse3DDown(object? sender, MouseDown3DEventArgs e)
    {
        if (_viewport is null || _scene is null) return;
        // В режиме прогулки кнопки мыши управляют обзором, выбор элементов отключён.
        if (_walkActive) return;
        // Только левая кнопка; e.Handled не трогаем, чтобы ViewCube и Trackball-вращение продолжали работать.
        var point = e.OriginalInputEventArgs?.GetCurrentPoint(_viewport);
        if (point is not null && !point.Properties.IsLeftButtonPressed) return;

        // Рулетка: клики ставят точки обмера (P1, P2) вместо выбора элементов.
        if (_vm?.IsMeasureMode == true)
        {
            HandleMeasureClick(e);
            return;
        }

        // Волна 6: пины поворотных точек участка и «+» сторон (Среда 1) — приоритетнее ручек элементов.
        if (TryBeginSiteEdit(e.Position, e.OriginalInputEventArgs?.Pointer)) return;

        // Пины вершин и маркеры рёбер контура — приоритетнее всех ручек (лежат на верхней грани плиты).
        if (_scene.SelectedElement is { } contourTarget && !_scene.IsMultiSelection && TryBeginVertexEdit(contourTarget, e.Position, e.OriginalInputEventArgs?.Pointer))
            return;

        // Торцевые ручки Push/Pull — приоритетнее стрелок Gizmo (стрелка проходит через центр торца).
        if (_scene.SelectedElement is { } pullTarget && !_scene.IsMultiSelection && HandleAt(e.Position) is { } pullFace)
        {
            BeginPushPull(pullTarget, pullFace, e.Position, e.OriginalInputEventArgs?.Pointer);
            return;
        }

        // Маркеры граней Section Box: перетаскивание плоскости разреза вдоль её нормали.
        if (_sectionHandles.Count > 0)
        {
            foreach (var hit in _viewport.FindHits(e.Position))
            {
                if (hit.ModelHit is Element3D handle && _sectionHandles.TryGetValue(handle, out var face))
                {
                    BeginFaceDrag(face, e.Position, e.OriginalInputEventArgs?.Pointer);
                    return;
                }
            }
        }

        // Gizmo рисуется поверх сцены, поэтому имеет приоритет: стрелка может проходить внутри стены,
        // и ближайшим попаданием луча окажется сама стена — проверяем все попадания вдоль луча.
        if (_scene.SelectedElement is { } selected && _gizmoAxes.Count > 0)
        {
            foreach (var hit in _viewport.FindHits(e.Position))
            {
                if (hit.ModelHit is Element3D gizmo && _gizmoAxes.TryGetValue(gizmo, out var axis))
                {
                    BeginDrag(selected, axis, e.Position, e.OriginalInputEventArgs?.Pointer);
                    return;
                }
            }
        }

        var hitModel = e.HitTestResult?.ModelHit as Element3D;

        if (hitModel is not null && _hitMap.TryGetValue(hitModel, out var element))
        {
            // Клик по окну/двери выбирает сам проём; повторный клик по стене — стену; Shift + клик — мультивыделение.
            _vm?.SelectUnderlay(null, default);
            if (IsKeyDown(Windows.System.VirtualKey.Shift)) _scene.ToggleSelection(element);
            else _scene.SelectedElement = element;
            return;
        }

        // Режим Б: клик по линии DXF-подложки — контур выделяется, на пилюле — кнопки быстрого создания.
        if (e.HitTestResult is null && TryPickUnderlay(e.Position))
            return;

        // Пустое место (не ViewCube и не служебные элементы) — снять выделение (с Shift — сохранить набор).
        if (e.HitTestResult is null && !IsKeyDown(Windows.System.VirtualKey.Shift))
        {
            _scene.SelectedElement = null;
            _vm?.SelectUnderlay(null, default);
        }
    }

    /// <summary>Ближайший к клику контур подложки (допуск — 10 пикселей экрана на плоскости подложки).</summary>
    private bool TryPickUnderlay(Windows.Foundation.Point position)
    {
        if (_viewport is null || _vm is not { HasUnderlay: true, IsUnderlayVisible: true, Underlay: { } drawing }) return false;
        float y = (float)_vm.UnderlayBaseElevation + 0.01f;
        var planePoint = new Vector3(0, y, 0);
        if (_viewport.UnProjectOnPlane(position, planePoint, Vector3.UnitY) is not { } hit) return false;
        double tolerance = 0.08;
        if (_viewport.UnProjectOnPlane(new Windows.Foundation.Point(position.X + 10, position.Y), planePoint, Vector3.UnitY) is { } side)
            tolerance = Math.Max(0.02, (side - hit).Length());
        var click = new DxfPoint(hit.X, -hit.Z, y);
        DxfContour? best = null;
        double bestDistance = tolerance;
        foreach (var c in drawing.Contours)
        {
            var (d, _, _) = c.Nearest(click);
            // При равной удалённости предпочтение — замкнутым контурам (колонны, плиты).
            if (d < bestDistance - 1e-6 || best is not null && Math.Abs(d - bestDistance) < 1e-6 && c.Closed && !best.Closed)
            {
                best = c;
                bestDistance = d;
            }
        }
        if (best is null) return false;
        _vm.SelectUnderlay(best, click);
        return true;
    }

    private void BeginDrag(ArchitecturalElement element, Vector3 axis, Windows.Foundation.Point position, Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        if (_viewport?.Camera is not ProjectionCamera camera) return;

        var look = Vector3.Normalize(camera.LookDirection);
        // Плоскость перетаскивания содержит ось и максимально обращена к камере.
        var normal = look - axis * Vector3.Dot(look, axis);
        if (normal.LengthSquared() < 1e-6f) return;
        normal = Vector3.Normalize(normal);

        var planePoint = new Vector3((float)element.X, (float)element.Y, (float)element.Z);
        if (_viewport.UnProjectOnPlane(position, planePoint, normal) is not { } hit) return;

        _dragElement = element;
        _dragAxis = axis;
        _dragPlanePoint = planePoint;
        _dragPlaneNormal = normal;
        _dragStartHit = hit;
        _dragStartPosition = [element.X, element.Y, element.Z];
        _dragUndoPushed = false;
        // Группа мультивыделения двигается синхронно; привязка — по характерным точкам главного элемента.
        _dragGroup = _scene!.IsMultiSelection ? _scene.SelectedElements.ToList() : [element];
        _dragStarts = _dragGroup.ToDictionary(g => g.Id, g => new[] { g.X, g.Y, g.Z });
        _dragKeyPoints = SmartSnapService.KeyPoints(element);
        _snapTargets = BuildSnapTargets(_dragGroup);
        _dragDelta = 0;
        SetActiveAxis(element, axis);
        _viewport.IsPanEnabled = false;
        _viewport.IsRotationEnabled = false;
        // Захват указателя: движения приходят даже за пределами модели Gizmo.
        if (pointer is not null) _viewport.CapturePointer(pointer);
    }

    /// <summary>Сырые события указателя: Helix не шлёт OnMouse3DMove при зажатой кнопке, поэтому Gizmo ведём сами.</summary>
    private void OnViewportPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_siteDrag is not null && _viewport is not null)
        {
            DragSiteVertexTo(e.GetCurrentPoint(_viewport).Position);
            return;
        }
        if (_vertexElement is not null && _viewport is not null)
        {
            DragVertexTo(e.GetCurrentPoint(_viewport).Position);
            return;
        }
        if (_dragFace is { } face && _viewport is not null && _vm is not null)
        {
            if (_viewport.UnProjectOnPlane(e.GetCurrentPoint(_viewport).Position, _dragPlanePoint, _dragPlaneNormal) is not { } faceHit) return;
            double moved = SceneStateService.Snap(_dragFaceStart + Vector3.Dot(faceHit - _dragStartHit, _dragAxis), 0.05);
            if (Math.Abs(moved - _vm.SectionBox.Value(face)) > 1e-6) _vm.SetSectionFace(face, moved);
            return;
        }
        if (_pullFace is not null && _viewport is not null)
        {
            if (_viewport.UnProjectOnPlane(e.GetCurrentPoint(_viewport).Position, _dragPlanePoint, _dragPlaneNormal) is { } pullHit)
                PushPullTo(Vector3.Dot(pullHit - _dragStartHit, _dragAxis));
            return;
        }
        if (_lookPointer is not null)
        {
            UpdateLook(e.GetCurrentPoint(_viewport).Position);
            return;
        }
        if (_dragElement is null || _viewport is null || _scene is null)
        {
            if (_viewport is null) return;
            if (_vm?.IsMeasureMode == true) UpdateMeasureHover(e.GetCurrentPoint(_viewport).Position);
            else if (!UpdateSiteHover(e.GetCurrentPoint(_viewport).Position)) UpdateHover(e.GetCurrentPoint(_viewport).Position);
            return;
        }
        var position = e.GetCurrentPoint(_viewport).Position;
        if (_viewport.UnProjectOnPlane(position, _dragPlanePoint, _dragPlaneNormal) is not { } hit) return;
        MoveTo(Vector3.Dot(hit - _dragStartHit, _dragAxis));
    }

    /// <summary>
    /// Смещение перетаскиваемой группы вдоль оси Gizmo: магнитная привязка OSnap к осям и геометрии (Alt — без привязки,
    /// шаг 10 мм), иначе — шаг 0.1 м; живая линейка показывает сдвиг в миллиметрах.
    /// </summary>
    private void MoveTo(double raw)
    {
        if (_dragElement is null || _scene is null) return;
        var (delta, snap) = ResolveSnap(_dragKeyPoints, _dragAxis, raw);
        ShowSnap(snap);
        _dragDelta = delta;
        var el = _dragElement;
        double nx = _dragStartPosition[0] + _dragAxis.X * delta;
        double ny = _dragStartPosition[1] + _dragAxis.Y * delta;
        double nz = _dragStartPosition[2] + _dragAxis.Z * delta;
        if (Math.Abs(nx - el.X) < 1e-6 && Math.Abs(ny - el.Y) < 1e-6 && Math.Abs(nz - el.Z) < 1e-6)
        {
            ShowHud();
            return;
        }

        if (!_dragUndoPushed)
        {
            _scene.PushUndo();
            _dragUndoPushed = true;
        }
        if (_dragGroup.Count > 1)
            _scene.MoveGroup(_dragGroup, _dragStarts, _dragAxis.X * delta, _dragAxis.Y * delta, _dragAxis.Z * delta);
        else
            _scene.SetTransform(el, nx, ny, nz, el.Width, el.Height, el.Depth, el.RotationY, el.LevelIndex, recordUndo: false);
        ShowHud();
    }

    private void OnViewportPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_lookPointer is not null)
        {
            // Волна 6: клик без поворота взгляда по двери — открыть / закрыть.
            var up = e.GetCurrentPoint(_viewport).Position;
            bool click = Math.Abs(up.X - _walkPressPosition.X) + Math.Abs(up.Y - _walkPressPosition.Y) < 5;
            EndLook(e.Pointer);
            if (click) TryToggleDoorAt(up);
            return;
        }
        EndDrag(e.Pointer);
        // Камеру могли повернуть: засечки размеров — в плоскости, обращённой к наблюдателю.
        RefreshDimensionOrientation();
    }

    /// <summary>Начало перетаскивания грани Section Box: плоскость содержит нормаль грани и обращена к камере.</summary>
    private void BeginFaceDrag(SectionFace face, Windows.Foundation.Point position, Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        if (_viewport?.Camera is not ProjectionCamera camera || _vm is null) return;
        var n = SectionBox.Normal(face).ToDx();
        var axis = new Vector3(Math.Abs(n.X), Math.Abs(n.Y), Math.Abs(n.Z));
        var look = Vector3.Normalize(camera.LookDirection);
        var normal = look - axis * Vector3.Dot(look, axis);
        if (normal.LengthSquared() < 1e-6f) return;
        normal = Vector3.Normalize(normal);
        var box = _vm.SectionBox;
        var planePoint = box.FaceCenter(face).ToDx();
        if (_viewport.UnProjectOnPlane(position, planePoint, normal) is not { } hit) return;

        _dragFace = face;
        _dragFaceStart = box.Value(face);
        _dragAxis = axis;
        _dragPlanePoint = planePoint;
        _dragPlaneNormal = normal;
        _dragStartHit = hit;
        _vm.ActiveSectionFace = face;
        _viewport.IsPanEnabled = false;
        _viewport.IsRotationEnabled = false;
        if (pointer is not null) _viewport.CapturePointer(pointer);
    }

    private void EndDrag(Microsoft.UI.Xaml.Input.Pointer? pointer = null)
    {
        if (_siteDrag is not null)
        {
            EndSiteDrag();
            return;
        }
        if (_vertexElement is not null)
        {
            EndVertexDrag(pointer);
            return;
        }
        if (_dragFace is not null)
        {
            _dragFace = null;
            if (_viewport is null) return;
            if (pointer is not null) _viewport.ReleasePointerCapture(pointer);
            _viewport.IsPanEnabled = true;
            _viewport.IsRotationEnabled = _vm?.ViewMode == ViewportMode.Perspective3D && _sectionCameraFace is null;
            RebuildSectionBox();
            return;
        }
        if (_pullFace is not null)
        {
            EndPushPull(pointer);
            return;
        }
        if (_dragElement is null) return;
        var moved = _dragUndoPushed && _dragGroup.Count <= 1 ? _dragElement : null;
        _dragElement = null;
        _dragDelta = 0;
        // Окно или дверь после перетаскивания: в стену, с отступом от колонн и поперечных стен (иначе шаг откатывается).
        if (moved is not null) _vm?.EnforceOpeningAfterMove(moved);
        ShowSnap(null);
        ShowHud();
        if (_viewport is null) return;
        if (pointer is not null) _viewport.ReleasePointerCapture(pointer);
        _viewport.IsPanEnabled = true;
        _viewport.IsRotationEnabled = _vm?.ViewMode == ViewportMode.Perspective3D;
    }

    // ───────────────────────────── Камеры 3D / План / Фасад ─────────────────────────────

    // «Безопасная зона» холста между парящими островами (DIP): сцена вписывается в неё, а не во всё окно.
    private const double SafeLeft = 24, SafeTop = 66, SafeBottom = 150;
    private double SafeRight => _vm?.IsInspectorCollapsed == true ? 24 : 330;

    /// <summary>Ставит камеру текущего режима и вписывает видимую сцену в зону между панелями.</summary>
    private void ResetCamera()
    {
        if (_viewport is null || _vm is null || _scene is null) return;
        // «Показать всё» или новая сцена во время прогулки — выход из режима прогулки к обзорной камере.
        if (_walkActive)
        {
            _vm.IsWalkthrough = false;
            return;
        }
        if (_sectionCameraFace is not null)
        {
            // Выход из камеры по разрезу: обычные оси этажей.
            _sectionCameraFace = null;
            RebuildAxes();
        }

        var (center, size) = SceneExtents();
        double fullW = Math.Max(ActualWidth, 400), fullH = Math.Max(ActualHeight, 300);
        double safeW = Math.Max(200, fullW - SafeLeft - SafeRight), safeH = Math.Max(150, fullH - SafeTop - SafeBottom);
        // Смещение центра безопасной зоны от центра окна: вправо и вверх, в пикселях.
        double offX = (SafeLeft - SafeRight) / 2, offY = (SafeBottom - SafeTop) / 2;
        float axesMargin = _vm.ShowGridAxes ? 9f : 2f;

        switch (_vm.ViewMode)
        {
            case ViewportMode.Plan2D:
            {
                double wpp = Math.Max((size.X + axesMargin) / safeW, (size.Z + axesMargin) / safeH) * 1.06;
                var target = Shift(center, Vector3.UnitX, -Vector3.UnitZ, offX, offY, wpp);
                float height = size.Y + 200;
                _viewport.Camera = new OrthographicCamera
                {
                    Position = target + new Vector3(0, height, 0),
                    LookDirection = new Vector3(0, -height, 0),
                    UpDirection = new Vector3(0, 0, -1),
                    Width = wpp * fullW,
                    NearPlaneDistance = 0.1,
                    FarPlaneDistance = 5000
                };
                Set2DInteraction(true);
                break;
            }
            case ViewportMode.Section2D:
            {
                double wpp = Math.Max((size.X + axesMargin) / safeW, (size.Y + 2) / safeH) * 1.06;
                var target = Shift(center, Vector3.UnitX, Vector3.UnitY, offX, offY, wpp);
                float distance = size.Z + 200;
                _viewport.Camera = new OrthographicCamera
                {
                    Position = target + new Vector3(0, 0, distance),
                    LookDirection = new Vector3(0, 0, -distance),
                    UpDirection = new Vector3(0, 1, 0),
                    Width = wpp * fullW,
                    NearPlaneDistance = 0.1,
                    FarPlaneDistance = 5000
                };
                Set2DInteraction(true);
                break;
            }
            default:
            {
                const double fov = 40;
                double tanHalf = Math.Tan(fov / 2 * Math.PI / 180);
                double aspect = fullW / fullH;
                var dir = Vector3.Normalize(new Vector3(-0.85f, 0.62f, 1.25f));
                var look = -dir;
                var right = Vector3.Normalize(Vector3.Cross(look, Vector3.UnitY));
                var up = Vector3.Normalize(Vector3.Cross(right, look));

                // Точное вписывание: каждый угол габарита должен попасть в безопасную зону с учётом своей глубины
                // (высокая башня и плоский участок вписываются одинаково плотно, без запаса «габаритной сферы»).
                double halfV = tanHalf * safeH / fullH, halfH = tanHalf * aspect * safeW / fullW;
                double distance = 8;
                var half = size / 2;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
                    double x = Math.Abs(Vector3.Dot(corner, right)), y = Math.Abs(Vector3.Dot(corner, up)), z = Vector3.Dot(corner, look);
                    distance = Math.Max(distance, Math.Max(x / halfH, y / halfV) - z);
                }
                distance *= 1.04;
                double wpp = 2 * distance * tanHalf / fullH;

                var target = Shift(center, right, up, offX, offY, wpp);
                var position = target + dir * (float)distance;
                _viewport.Camera = new PerspectiveCamera
                {
                    Position = position,
                    LookDirection = target - position,
                    UpDirection = new Vector3(0, 1, 0),
                    FieldOfView = fov,
                    NearPlaneDistance = 0.3,
                    FarPlaneDistance = 8000
                };
                Set2DInteraction(false);
                break;
            }
        }
        // Размер ручек Push/Pull зависит от масштаба экрана — после смены камеры пересобираем их.
        UpdateSelectionVisuals();
        RebuildDimensions();
    }

    /// <summary>
    /// «Камера по разрезу»: ортогональный вид строго перпендикулярно активной плоскости Section Box (для горизонтального
    /// среза — сверху), кадр — по габариту коробки в плоскости разреза; на виде — отметки уровней и оси.
    /// </summary>
    private void ApplySectionCamera(SectionFace face)
    {
        if (_viewport is null || _vm is null) return;
        var box = _vm.SectionBox;
        var n = SectionBox.Normal(face).ToDx();
        bool horizontal = face is SectionFace.MinY or SectionFace.MaxY;
        var up = horizontal ? new Vector3(0, 0, -1) : Vector3.UnitY;
        var look = -n;
        var right = Vector3.Normalize(Vector3.Cross(look, up));
        var size = (box.Max - box.Min).ToDx();
        float extentU = Math.Abs(Vector3.Dot(size, right)), extentV = Math.Abs(Vector3.Dot(size, up));

        double fullW = Math.Max(ActualWidth, 400), fullH = Math.Max(ActualHeight, 300);
        // Справа парит ещё и панель «Секущий бокс» (268 px + отступы): отметки уровней не должны уйти под неё.
        double safeRight = SafeRight + 290;
        double safeW = Math.Max(200, fullW - SafeLeft - safeRight), safeH = Math.Max(150, fullH - SafeTop - SafeBottom);
        double offX = (SafeLeft - safeRight) / 2, offY = (SafeBottom - SafeTop) / 2;
        double wpp = Math.Max((extentU + 7) / safeW, (extentV + 4) / safeH) * 1.06;
        // Центр кадра — на плоскости разреза.
        var axis = new Vector3(Math.Abs(n.X), Math.Abs(n.Y), Math.Abs(n.Z));
        var center = box.Center.ToDx();
        center += axis * ((float)box.Value(face) - Vector3.Dot(center, axis));
        var target = Shift(center, right, up, offX, offY, wpp);
        float distance = size.Length() + 200;
        _viewport.Camera = new OrthographicCamera
        {
            Position = target + n * distance,
            LookDirection = look * distance,
            UpDirection = up,
            Width = wpp * fullW,
            NearPlaneDistance = 0.1,
            FarPlaneDistance = 5000
        };
        Set2DInteraction(true);
        _sectionCameraFace = face;
        RebuildAxes();
        RebuildDimensions();
    }

    /// <summary>Линии DXF-подложки на отметке активного этажа и выделенный контур (режим Б).</summary>
    private void RebuildUnderlay()
    {
        if (_viewport is null || _vm is null || _theme is null) return;
        _underlayGroup.Children.Clear();
        if (_vm.Underlay is not { } drawing || !_vm.IsUnderlayVisible) return;

        float y = (float)_vm.UnderlayBaseElevation + 0.01f;
        var lines = new LineBuilder();
        int count = 0;
        foreach (var c in drawing.Contours)
            foreach (var (a, b) in c.Segments())
            {
                lines.AddLine(new Vector3((float)a.X, y, (float)-a.Y), new Vector3((float)b.X, y, (float)-b.Y));
                count++;
            }
        var theme = _theme.Current;
        if (count > 0)
            _underlayGroup.Children.Add(new LineGeometryModel3D
            {
                Geometry = lines.ToLineGeometry3D(),
                Color = Blend(theme.TextPrimary, theme.ViewportBackground, 0.35),
                Thickness = 0.8,
                DepthBias = -60,
                IsHitTestVisible = false
            });

        if (_vm.UnderlaySelection is { } selected)
        {
            var hl = new LineBuilder();
            foreach (var (a, b) in selected.Segments())
                hl.AddLine(new Vector3((float)a.X, y + 0.01f, (float)-a.Y), new Vector3((float)b.X, y + 0.01f, (float)-b.Y));
            _underlayGroup.Children.Add(new LineGeometryModel3D
            {
                Geometry = hl.ToLineGeometry3D(),
                Color = theme.Selection,
                Thickness = 2.6,
                DepthBias = -120,
                IsHitTestVisible = false
            });
        }
    }

    /// <summary>Тонкий каркас Section Box и 6 маркеров-стрелок на центрах граней (активная грань — тёплым акцентом).</summary>
    private void RebuildSectionBox()
    {
        if (_viewport is null || _vm is null || _theme is null) return;
        _sectionGroup.Children.Clear();
        _sectionHandles.Clear();
        if (!_vm.IsSectionBoxEnabled) return;

        var box = _vm.SectionBox;
        var theme = _theme.Current;
        _sectionGroup.Children.Add(new LineGeometryModel3D
        {
            Geometry = LineBuilder.GenerateBoundingBox(new BoundingBox(box.Min.ToDx(), box.Max.ToDx())),
            Color = theme.Selection,
            Thickness = 1.1,
            DepthBias = -50,
            IsHitTestVisible = false
        });

        float diag = (box.Max - box.Min).ToDx().Length();
        float length = Math.Clamp(diag * 0.045f, 0.7f, 3.5f);
        float diameter = Math.Clamp(length * 0.22f, 0.12f, 0.6f);
        var accent = ToColor4(theme.Selection);
        var active = new Color4(1.0f, 0.62f, 0.04f, 1f);
        foreach (var face in Enum.GetValues<SectionFace>())
        {
            var n = SectionBox.Normal(face).ToDx();
            var origin = box.FaceCenter(face).ToDx();
            var builder = new MeshBuilder();
            builder.AddSphere(origin, diameter * 0.75, 14, 14);
            builder.AddArrow(origin, origin + n * length, diameter * 0.55, diameter * 2.4, 14);
            var color = face == _vm.ActiveSectionFace ? active : accent;
            var model = new MeshGeometryModel3D
            {
                Geometry = builder.ToMeshGeometry3D(),
                Material = new PhongMaterial
                {
                    DiffuseColor = color,
                    AmbientColor = color,
                    EmissiveColor = new Color4(color.Red * 0.5f, color.Green * 0.5f, color.Blue * 0.5f, 1f),
                    SpecularColor = new Color4(0.25f, 0.25f, 0.25f, 1f),
                    SpecularShininess = 30
                },
                CullMode = CullMode.None
            };
            _sectionGroup.Children.Add(model);
            _sectionHandles[model] = face;
        }
    }

    /// <summary>Точка, которую должна видеть камера, чтобы центр сцены оказался в центре безопасной зоны.</summary>
    private static Vector3 Shift(Vector3 center, Vector3 right, Vector3 up, double offX, double offY, double worldPerPixel) =>
        center - right * (float)(offX * worldPerPixel) - up * (float)(offY * worldPerPixel);

    private void Set2DInteraction(bool is2D)
    {
        if (_viewport is null) return;
        _viewport.IsRotationEnabled = !is2D;
        _viewport.ShowViewCube = !is2D;
        // Легенда карты напряжений занимает левый нижний угол — индикатор осей координат на это время скрыт.
        _viewport.ShowCoordinateSystem = !is2D && _vm?.IsStressMapEnabled != true;
    }

    // ───────────────────────────── Карта напряжений (КЭ-расчёт) ─────────────────────────────

    /// <summary>Цвет части элемента в режиме карты: шкала K_исп, f / f_ult или F / F_b,ult; ненесущее — полупрозрачный нейтральный.</summary>
    private Color4 StressTarget(ArchitecturalElement el, MaterialKind kind, StructuralAnalysisResult analysis)
    {
        if (kind == MaterialKind.Void || _vm is null) return StressPalette.Ghost;
        if (StructuralBodyColor(el) is { } structural) return structural;
        var r = analysis.For(el.Id);
        double? k = _vm.StressMode switch
        {
            StressMapMode.Deflection => r is { DeflectionLimitMm: > 0 } ? r.DeflectionRatio : null,
            StressMapMode.Punching => r is { Punching: not PunchingStatus.None } ? r.KPunching : null,
            _ => r?.Utilization
        };
        return k is { } v ? StressPalette.ColorFor(v) : StressPalette.Ghost;
    }

    /// <summary>
    /// Включение карты: у каждой модели — собственный материал, цвет плавно (≈0.4 с) переходит от архитектурного к цвету шкалы;
    /// пересчёт (перемещение элемента, смена режима) перекрашивает мгновенно. Выключение возвращает архитектурные материалы.
    /// </summary>
    private void ApplyStress(bool animate)
    {
        if (_viewport is null || _vm is null) return;
        bool on = StressColoring;
        _viewport.ShowCoordinateSystem = !on && _vm.ViewMode == ViewportMode.Perspective3D && _sectionCameraFace is null;
        if (!on)
        {
            _stressTimer?.Stop();
            foreach (var (model, kind) in _partKinds) model.IsTransparent = ArchitecturalMaterials.IsTransparent(kind);
            _stress.Clear();
            _stressGroup.Children.Clear();
            UpdateSelectionVisuals();
            return;
        }

        var analysis = _vm.Analysis;
        foreach (var (model, kind) in _partKinds)
        {
            if (!_hitMap.TryGetValue(model, out var el)) continue;
            var target = StressTarget(el, kind, analysis);
            if (_stress.TryGetValue(model, out var entry))
                _stress[model] = (entry.Material, entry.Material.DiffuseColor, target);
            else
            {
                var from = animate ? ArchitecturalMaterials.BaseColor(kind) : target;
                var material = ArchitecturalMaterials.CreateStress(from);
                model.Material = material;
                _stress[model] = (material, from, target);
            }
            model.IsTransparent = target.Alpha < 0.99f || ArchitecturalMaterials.IsTransparent(kind);
        }
        RebuildPunchingOverlay(analysis);

        if (!animate)
        {
            _stressTimer?.Stop();
            foreach (var (model, (material, _, to)) in _stress.ToList())
            {
                ArchitecturalMaterials.SetStressColor(material, to);
                _stress[model] = (material, to, to);
            }
            return;
        }
        _stressStart = DateTime.UtcNow;
        _stressTimer ??= CreateStressTimer();
        _stressTimer.Start();
    }

    private DispatcherTimer CreateStressTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Clamp((DateTime.UtcNow - _stressStart).TotalMilliseconds / 420.0, 0, 1);
            float eased = (float)(1 - Math.Pow(1 - t, 3));
            foreach (var (_, (material, from, to)) in _stress)
                ArchitecturalMaterials.SetStressColor(material, Color4.Lerp(from, to, eased));
            if (t >= 1)
            {
                timer.Stop();
                foreach (var (model, (material, _, to)) in _stress.ToList()) _stress[model] = (material, to, to);
            }
        };
        return timer;
    }

    /// <summary>Расчётные контуры продавливания (h0/2 от граней колонн): в режиме «Продавливание» — все узлы, иначе — проблемные.</summary>
    private void RebuildPunchingOverlay(StructuralAnalysisResult analysis)
    {
        _stressGroup.Children.Clear();
        if (_vm is null || _scene is null) return;
        bool all = _vm.StressMode == StressMapMode.Punching;
        foreach (var group in analysis.PunchingNodes.Where(n => all || n.Status != PunchingStatus.Ok).GroupBy(n => n.Status))
        {
            var lines = new LineBuilder();
            int count = 0;
            foreach (var node in group)
            {
                if (_scene.FindById(node.SlabId) is { } slab && !_scene.IsElementVisible(slab) || node.Contour.Count < 3) continue;
                float y = (float)node.Location[1] + 0.04f;
                for (int i = 0; i < node.Contour.Count; i++)
                {
                    var a = node.Contour[i];
                    var b = node.Contour[(i + 1) % node.Contour.Count];
                    lines.AddLine(new Vector3((float)a.X, y, (float)a.Z), new Vector3((float)b.X, y, (float)b.Z));
                }
                count++;
            }
            if (count == 0) continue;
            var color = group.Key switch
            {
                PunchingStatus.Critical => StressPalette.Red,
                PunchingStatus.NeedsReinforcement => StressPalette.Amber,
                _ => StressPalette.Green
            };
            _stressGroup.Children.Add(new LineGeometryModel3D
            {
                Geometry = lines.ToLineGeometry3D(),
                Color = StressPalette.ToUi(color),
                Thickness = group.Key == PunchingStatus.Ok ? 1.2 : 2.4,
                DepthBias = -200,
                IsHitTestVisible = false
            });
        }
    }

    // ───────────────────────────── Коллизии: подсветка и фокус камеры ─────────────────────────────

    /// <summary>Контрастные контуры пары конфликтующих элементов (оранжевый и малиновый) и маркер узла пересечения.</summary>
    private void RebuildClashHighlight()
    {
        _clashGroup.Children.Clear();
        // Спорные узлы карточки согласования Совета КБ: критичные — алым, предупреждения — янтарным.
        foreach (var (id, critical) in _vm?.CouncilHighlights ?? [])
            Outline(id, critical ? Windows.UI.Color.FromArgb(255, 0xFF, 0x45, 0x3A) : Windows.UI.Color.FromArgb(255, 0xFF, 0x9F, 0x0A));
        if (_vm?.FocusedClash is not { } clash) return;
        void Outline(string id, Windows.UI.Color color)
        {
            if (!_geometryById.TryGetValue(id, out var g) || g.IsEmpty) return;
            var box = g.LocalBounds.ToDx();
            _clashGroup.Children.Add(new LineGeometryModel3D
            {
                Geometry = LineBuilder.GenerateBoundingBox(new BoundingBox(box.Minimum - new Vector3(0.06f), box.Maximum + new Vector3(0.06f))),
                Color = color,
                Thickness = 2.8,
                HxTransform3D = g.Transform.ToDx(),
                IsHitTestVisible = false
            });
        }
        Outline(clash.ElementAId, Windows.UI.Color.FromArgb(255, 0xFF, 0x9F, 0x0A));
        Outline(clash.ElementBId, Windows.UI.Color.FromArgb(255, 0xFF, 0x2D, 0x55));

        var center = new Vector3((float)clash.LocationPoint[0], (float)clash.LocationPoint[1], (float)clash.LocationPoint[2]);
        float r = (float)Math.Clamp(clash.Extent * 0.035, 0.08, 0.45);
        var builder = new MeshBuilder();
        builder.AddSphere(center, r, 16, 16);
        var marker = new Color4(1f, 0.18f, 0.33f, 1f);
        _clashGroup.Children.Add(new MeshGeometryModel3D
        {
            Geometry = builder.ToMeshGeometry3D(),
            Material = new PhongMaterial
            {
                DiffuseColor = marker,
                AmbientColor = marker,
                EmissiveColor = new Color4(0.6f, 0.1f, 0.2f, 1f),
                SpecularColor = new Color4(0.3f, 0.3f, 0.3f, 1f),
                SpecularShininess = 30
            },
            CullMode = CullMode.None,
            IsHitTestVisible = false
        });
    }

    /// <summary>Плавный (≈0.5 с, ease-in-out) перелёт камеры к узлу с сохранением направления взгляда.</summary>
    private void AnimateCameraTo(ClashFocus focus)
    {
        if (_viewport?.Camera is not ProjectionCamera camera) return;
        var target = new Vector3((float)focus.X, (float)focus.Y, (float)focus.Z);
        var p0 = camera.Position;
        var l0 = camera.LookDirection;
        var dir = Vector3.Normalize(l0);
        float distance = camera is OrthographicCamera ? l0.Length() : (float)Math.Clamp(focus.Extent * 3.2, 5, 400);
        var p1 = target - dir * distance;
        var l1 = dir * distance;
        double w0 = (camera as OrthographicCamera)?.Width ?? 0, w1 = Math.Max(6, focus.Extent * 5);
        var start = DateTime.UtcNow;
        _cameraTimer?.Stop();
        _cameraTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _cameraTimer.Tick += (s, _) =>
        {
            double t = Math.Clamp((DateTime.UtcNow - start).TotalMilliseconds / 520.0, 0, 1);
            float e = (float)(t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2);
            camera.Position = Vector3.Lerp(p0, p1, e);
            camera.LookDirection = Vector3.Lerp(l0, l1, e);
            if (camera is OrthographicCamera ortho) ortho.Width = w0 + (w1 - w0) * e;
            if (t >= 1) (s as DispatcherTimer)?.Stop();
        };
        _cameraTimer.Start();
    }

    /// <summary>Центр и размеры видимой части сцены (по умолчанию — участок 24×24 м).</summary>
    private (Vector3 Center, Vector3 Size) SceneExtents()
    {
        var boxes = _geometryById.Values
            .Where(g => !g.IsEmpty)
            .SelectMany(g => g.WorldCorners().Select(c => c.ToDx()))
            .ToList();
        // Видимая DXF-подложка тоже попадает в кадр.
        if (_vm is { IsUnderlayVisible: true, Underlay: { } drawing })
        {
            float y = (float)_vm.UnderlayBaseElevation;
            boxes.Add(new Vector3((float)drawing.MinX, y, (float)-drawing.MaxY));
            boxes.Add(new Vector3((float)drawing.MaxX, y, (float)-drawing.MinY));
        }
        if (boxes.Count == 0) return (new Vector3(0, 2, 0), new Vector3(24, 6, 24));
        var bb = BoundingBox.FromPoints(boxes.ToArray());
        var size = bb.Maximum - bb.Minimum;
        return (bb.Center, new Vector3(Math.Max(size.X, 4), Math.Max(size.Y, 3), Math.Max(size.Z, 4)));
    }

    private static Color4 ToColor4(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

#if DEBUG
    /// <summary>Только Debug (AIA_DEBUG_CAMERA): камера в заданной точке, взгляд на цель — для съёмки деталей.</summary>
    public void DebugSetCamera(Vector3 position, Vector3 target)
    {
        if (_viewport?.Camera is not ProjectionCamera camera) return;
        camera.Position = position;
        camera.LookDirection = target - position;
        camera.UpDirection = new Vector3(0, 1, 0);
    }
#endif
}
