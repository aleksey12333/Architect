using System.Globalization;
using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.WinUI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SharpDX;
using Windows.System;
using Windows.UI.Core;
using Color = Windows.UI.Color;
using Matrix = SharpDX.Matrix;
using CullMode = SharpDX.Direct3D11.CullMode;
using PerspectiveCamera = HelixToolkit.WinUI.PerspectiveCamera;
using OrthographicCamera = HelixToolkit.WinUI.OrthographicCamera;

namespace AIArchitectStudio.Views;

/// <summary>
/// Тактильное моделирование во вьюпорте (Shapr3D): торцевые ручки Push/Pull с живой линейкой и вводом размера с клавиатуры,
/// магнитные привязки OSnap с пунктирными направляющими и бейджем, мультивыделение, режим прогулки от первого лица
/// (рост 1.70 м, автоподъём по лестницам и плитам) и солнечная инсоляция с падающими тенями на участке.
/// </summary>
public sealed partial class Viewport3DControl
{
    private const float EyeHeight = 1.70f;
    private const double StepUp = 0.45;
    private const double HandlePickPixels = 7;

    private static readonly Vector3 DefaultKeyDirection = new(0.45f, -1f, -0.65f);
    private static readonly Vector3 DefaultFillDirection = new(-0.6f, -0.35f, 0.75f);
    private static readonly Color AxisGuideColor = Color.FromArgb(255, 0x0A, 0x84, 0xFF);
    private static readonly Color GeometryGuideColor = Color.FromArgb(255, 0xFF, 0x9F, 0x0A);

    // Направляющие привязки и падающие тени.
    private readonly TopMostGroup3D _snapGroup = new() { EnableTopMost = true };
    private readonly GroupModel3D _shadowGroup = new();

    /// <summary>Матовая плоскость участка под тенями (тёмные темы) — в прозрачном рендере 4K не рисуется.</summary>
    private MeshGeometryModel3D? _siteGroundModel;
    private SnapHit? _snapHit;
    private List<SnapTarget>? _snapTargets;
    private bool _shadowQueued;

    // Ручки Push/Pull и стрелки Gizmo (для экранного хит-теста наведения).
    private readonly Dictionary<Element3D, PushPullFace> _handleModels = [];
    private readonly Dictionary<PushPullFace, (Vector3 Center, float Radius, PhongMaterial Pin, PhongMaterial Arrow)> _handleCenters = [];
    private readonly Dictionary<Vector3, (Vector3 From, Vector3 To)> _gizmoTips = [];

    // Перетаскивание группы за Gizmo.
    private List<ArchitecturalElement> _dragGroup = [];
    private Dictionary<string, double[]> _dragStarts = [];
    private List<(double X, double Y, double Z)> _dragKeyPoints = [];
    private double _dragDelta;

    // Перетаскивание грани Push/Pull.
    private PushPullFace? _pullFace;
    private ArchitecturalElement? _pullElement;
    private double _pullStartLength;
    private (double X, double Y, double Z) _pullStartFace;
    private bool _hudFrozen;

    // Активная цель ввода с клавиатуры: грань или ось Gizmo; наведение и набираемое значение (мм).
    private string? _activeElementId;
    private PushPullFace? _activeFace;
    private Vector3? _activeAxis;
    private PushPullFace? _hoverFace;
    private Vector3? _hoverAxis;
    private string? _typing;
    private bool _overlayTracking;

    // Режим прогулки.
    private bool _walkActive;
    private DispatcherTimer? _walkTimer;
    private DateTime _walkLast;
    private double _walkYaw, _walkPitch;
    private double _walkX, _walkZ, _walkEye, _walkFloor;
    private List<WalkSurface>? _walkSurfaces;
    private List<WalkObstacle>? _walkObstacles;
    private Pointer? _lookPointer;
    private Windows.Foundation.Point _lookLast;

    /// <summary>Пол для прогулки: плита/фундамент/терраса/плоская кровля (контур, отверстия, повторы этажей) или лестничный марш.</summary>
    private sealed record WalkSurface(ArchitecturalElement Element, List<(double X, double Z)> Outline, List<List<(double X, double Z)>> Holes, bool IsStairs);

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnSelectionChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateSelectionVisuals();
        QueueRebarRebuild();
    });

    // ───────────────────────────── Ручки Push/Pull ─────────────────────────────

    /// <summary>Контрастные пины на центрах граней (белый шар + короткая стрелка наружу); активная грань — акцентом темы.</summary>
    private void AddPushPullHandles(ArchitecturalElement sel, float extent)
    {
        if (_walkActive || _theme is null) return;
        var faces = PushPullService.FacesFor(sel);
        if (faces.Count == 0) return;
        // Пин ~9 px на экране (но не меньше 4 % габарита): хорошо виден и на целом здании, и при крупном плане.
        float pixel = WorldPerPixel(new Vector3((float)sel.X, (float)sel.Y, (float)sel.Z));
        float radius = Math.Clamp(Math.Max(extent * 0.04f, pixel * 9f), 0.06f, Math.Max(0.15f, extent * 0.08f));
        foreach (var face in faces)
        {
            var (dx, dy, dz) = PushPullService.Direction(sel, face);
            var dir = new Vector3((float)dx, (float)dy, (float)dz);
            var (fx, fy, fz) = PushPullService.FaceCenter(sel, face);
            var center = new Vector3((float)fx, (float)fy, (float)fz) + dir * (radius * 1.25f);

            var pinBuilder = new MeshBuilder();
            pinBuilder.AddSphere(center, radius, 18, 18);
            var arrowBuilder = new MeshBuilder();
            arrowBuilder.AddArrow(center + dir * (radius * 0.9f), center + dir * (radius * 3.2f), radius * 0.42f, radius * 1.3f, 14);

            var pinMaterial = HandleMaterial(new Color4(0.97f, 0.97f, 0.98f, 1f));
            var arrowMaterial = HandleMaterial(ToColor4(_theme.Current.Selection));
            var pin = new MeshGeometryModel3D { Geometry = pinBuilder.ToMeshGeometry3D(), Material = pinMaterial, CullMode = CullMode.None };
            var arrow = new MeshGeometryModel3D { Geometry = arrowBuilder.ToMeshGeometry3D(), Material = arrowMaterial, CullMode = CullMode.None };
            _overlayGroup.Children.Add(pin);
            _overlayGroup.Children.Add(arrow);
            _handleModels[pin] = face;
            _handleModels[arrow] = face;
            _handleCenters[face] = (center, radius, pinMaterial, arrowMaterial);
        }
        RefreshHandleColors();
    }

    private static PhongMaterial HandleMaterial(Color4 color) => new()
    {
        DiffuseColor = color,
        AmbientColor = color,
        EmissiveColor = new Color4(color.Red * 0.55f, color.Green * 0.55f, color.Blue * 0.55f, 1f),
        SpecularColor = new Color4(0.35f, 0.35f, 0.35f, 1f),
        SpecularShininess = 40
    };

    /// <summary>Активная (наведённая, перетаскиваемая или выбранная для ввода) грань — заливка акцентом, остальные — белые пины.</summary>
    private void RefreshHandleColors()
    {
        if (_theme is null) return;
        var accent = ToColor4(_theme.Current.Selection);
        var white = new Color4(0.97f, 0.97f, 0.98f, 1f);
        var active = _pullFace ?? _hoverFace ?? (_typing is not null || _hudFrozen ? _activeFace : null);
        foreach (var (face, h) in _handleCenters)
        {
            var c = face == active ? new Color4(1.0f, 0.62f, 0.04f, 1f) : white;
            h.Pin.DiffuseColor = c;
            h.Pin.AmbientColor = c;
            h.Pin.EmissiveColor = new Color4(c.Red * 0.55f, c.Green * 0.55f, c.Blue * 0.55f, 1f);
            var a = face == active ? new Color4(1.0f, 0.62f, 0.04f, 1f) : accent;
            h.Arrow.DiffuseColor = a;
            h.Arrow.AmbientColor = a;
            h.Arrow.EmissiveColor = new Color4(a.Red * 0.55f, a.Green * 0.55f, a.Blue * 0.55f, 1f);
        }
    }

    /// <summary>Ручка под курсором (экранный радиус пина + 3 px): ближайшая к камере при перекрытии.</summary>
    private PushPullFace? HandleAt(Windows.Foundation.Point position)
    {
        if (_viewport?.Camera is not ProjectionCamera camera || _handleCenters.Count == 0) return null;
        PushPullFace? best = null;
        double bestDepth = double.MaxValue;
        var right = Vector3.Normalize(Vector3.Cross(camera.LookDirection, camera.UpDirection));
        foreach (var (face, h) in _handleCenters)
        {
            if (!TryProject(h.Center, out var c) || !TryProject(h.Center + right * h.Radius, out var edge)) continue;
            double r = Math.Max(HandlePickPixels, Distance(c, edge)) + 3;
            if (Distance(c, position) > r) continue;
            double depth = Vector3.Dot(h.Center - camera.Position, Vector3.Normalize(camera.LookDirection));
            if (depth < bestDepth)
            {
                bestDepth = depth;
                best = face;
            }
        }
        return best;
    }

    /// <summary>Стрелка Gizmo под курсором (расстояние до проекции стрелки ≤ 7 px).</summary>
    private Vector3? GizmoAxisAt(Windows.Foundation.Point position)
    {
        Vector3? best = null;
        double bestDistance = HandlePickPixels;
        foreach (var (axis, (from, to)) in _gizmoTips)
        {
            if (!TryProject(from + (to - from) * 0.15f, out var a) || !TryProject(to, out var b)) continue;
            double d = DistanceToSegment(position, a, b);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = axis;
            }
        }
        return best;
    }

    private void BeginPushPull(ArchitecturalElement element, PushPullFace face, Windows.Foundation.Point position, Pointer? pointer)
    {
        if (_viewport?.Camera is not ProjectionCamera camera || _scene is null) return;
        var (dx, dy, dz) = PushPullService.Direction(element, face);
        var axis = new Vector3((float)dx, (float)dy, (float)dz);
        var look = Vector3.Normalize(camera.LookDirection);
        var normal = look - axis * Vector3.Dot(look, axis);
        if (normal.LengthSquared() < 1e-6f) return;
        normal = Vector3.Normalize(normal);
        var fc = PushPullService.FaceCenter(element, face);
        var planePoint = new Vector3((float)fc.X, (float)fc.Y, (float)fc.Z);
        if (_viewport.UnProjectOnPlane(position, planePoint, normal) is not { } hit) return;

        StartPull(element, face);
        _dragAxis = axis;
        _dragPlanePoint = planePoint;
        _dragPlaneNormal = normal;
        _dragStartHit = hit;
        _viewport.IsPanEnabled = false;
        _viewport.IsRotationEnabled = false;
        if (pointer is not null) _viewport.CapturePointer(pointer);
        ShowHud();
    }

    private void StartPull(ArchitecturalElement element, PushPullFace face)
    {
        _hudFrozen = false;
        _pullFace = face;
        _pullElement = element;
        _pullStartLength = PushPullService.Length(element, face);
        _pullStartFace = PushPullService.FaceCenter(element, face);
        var (dx, dy, dz) = PushPullService.Direction(element, face);
        _dragAxis = new Vector3((float)dx, (float)dy, (float)dz);
        _dragUndoPushed = false;
        _snapTargets = BuildSnapTargets([element]);
        _typing = null;
        SetActiveFace(element, face);
    }

    /// <summary>
    /// Вытягивание грани на raw метров вдоль её нормали: привязка OSnap по центру грани (Alt — шаг 10 мм), новый размер
    /// L' = max(0.1, L₀ + Δ), противоположная грань неподвижна (SceneStateService.PushPull).
    /// </summary>
    private void PushPullTo(double raw)
    {
        if (_pullFace is not { } face || _pullElement is not { } el || _scene is null) return;
        var (delta, snap) = ResolveSnap([_pullStartFace], _dragAxis, raw);
        double length = Math.Max(PushPullService.MinSize, _pullStartLength + delta);
        ShowSnap(snap);
        if (Math.Abs(length - PushPullService.Length(el, face)) > 1e-6)
        {
            if (!_dragUndoPushed)
            {
                _scene.PushUndo();
                _dragUndoPushed = true;
            }
            _scene.PushPull(el, face, length, recordUndo: false);
        }
        ShowHud();
    }

    private void EndPushPull(Pointer? pointer)
    {
        var el = _pullElement;
        bool changed = _dragUndoPushed;
        _pullFace = null;
        _pullElement = null;
        ShowSnap(null);
        if (changed && el is not null) _vm?.CompletePushPull(el);
        RefreshHandleColors();
        ShowHud();
        if (_viewport is null) return;
        if (pointer is not null) _viewport.ReleasePointerCapture(pointer);
        _viewport.IsPanEnabled = true;
        _viewport.IsRotationEnabled = _vm?.ViewMode == ViewportMode.Perspective3D;
    }

    // ───────────────────────────── OSnap ─────────────────────────────

    /// <summary>Цели привязки: видимые элементы и оси сцены, кроме перетаскиваемых элементов и их проёмов.</summary>
    private List<SnapTarget>? BuildSnapTargets(IReadOnlyList<ArchitecturalElement> moving)
    {
        if (_scene is null || _vm is null) return null;
        var exclude = new HashSet<string>();
        foreach (var m in moving)
        {
            exclude.Add(m.Id);
            foreach (var child in _scene.GetHostedElements(m.Id)) exclude.Add(child.Id);
        }
        var visible = _scene.VisibleElements.ToList();
        var axes = _scene.GridAxes.Where(a => _scene.FindGroup(a.BuildingGroupId) is not { IsVisible: false }).ToList();
        return SmartSnapService.BuildTargets(visible, axes, exclude, english: !_vm.Loc.IsRussian);
    }

    /// <summary>Смещение с учётом привязки: OSnap включён и Alt не зажат — магнит в радиусе 0.25 м, иначе шаг 0.1 м (Alt — 0.01 м).</summary>
    private (double Delta, SnapHit? Hit) ResolveSnap(IReadOnlyList<(double X, double Y, double Z)> points, Vector3 axis, double raw)
    {
        bool alt = IsKeyDown(VirtualKey.Menu);
        if (_vm?.IsOSnapEnabled == true && !alt && _snapTargets is { Count: > 0 } targets &&
            SmartSnapService.SnapAlongAxis(points, (axis.X, axis.Y, axis.Z), raw, targets) is { } hit)
            return (hit.Delta, hit);
        return (SceneStateService.Snap(raw, alt ? SmartSnapService.FineStep : SnapStep), null);
    }

    /// <summary>
    /// Тонкие пунктирные направляющие привязки (#0A84FF — оси, #FF9F0A — геометрия): вдоль линий цели через точку касания,
    /// от точки элемента до цели и квадратный маркер узла; у точки касания — стеклянный бейдж с подписью.
    /// </summary>
    private void ShowSnap(SnapHit? hit)
    {
        _snapHit = hit;
        _snapGroup.Children.Clear();
        if (hit is null)
        {
            SnapBadge.Visibility = Visibility.Collapsed;
            UpdateOverlayTracking();
            return;
        }
        var color = hit.IsAxis ? AxisGuideColor : GeometryGuideColor;
        var lines = new LineBuilder();
        var contact = new Vector3((float)hit.Contact.X, (float)hit.Contact.Y + 0.02f, (float)hit.Contact.Z);
        var anchor = new Vector3((float)hit.Anchor.X, (float)hit.Anchor.Y + 0.02f, (float)hit.Anchor.Z);
        var t = hit.Target;
        switch (t.Shape)
        {
            case SnapShape.Line:
            {
                var a = new Vector3((float)t.AX, contact.Y, (float)t.AZ);
                var b = new Vector3((float)t.BX, contact.Y, (float)t.BZ);
                var u = Vector3.Normalize(b - a);
                AddDashed(lines, a - u * 0.6f, b + u * 0.6f);
                break;
            }
            case SnapShape.Point:
                foreach (var (gx, gz) in t.GuideDirections)
                {
                    var u = new Vector3((float)gx, 0, (float)gz);
                    AddDashed(lines, contact - u * 4.5f, contact + u * 4.5f);
                }
                break;
            case SnapShape.Elevation:
                AddDashed(lines, contact - Vector3.UnitX * 3f, contact + Vector3.UnitX * 3f);
                AddDashed(lines, contact - Vector3.UnitZ * 3f, contact + Vector3.UnitZ * 3f);
                break;
        }
        if ((anchor - contact).Length() > 0.015f) AddDashed(lines, anchor, contact);
        // Маркер узла: квадрат 0.24 м в плане вокруг точки касания.
        const float m = 0.12f;
        var c0 = contact + new Vector3(-m, 0, -m);
        var c1 = contact + new Vector3(m, 0, -m);
        var c2 = contact + new Vector3(m, 0, m);
        var c3 = contact + new Vector3(-m, 0, m);
        lines.AddLine(c0, c1);
        lines.AddLine(c1, c2);
        lines.AddLine(c2, c3);
        lines.AddLine(c3, c0);
        _snapGroup.Children.Add(new LineGeometryModel3D
        {
            Geometry = lines.ToLineGeometry3D(),
            Color = color,
            Thickness = 1.6,
            Smoothness = 0.5,
            IsHitTestVisible = false
        });

        SnapText.Text = hit.Label;
        SnapDot.Fill = new SolidColorBrush(color);
        SnapBadge.Visibility = Visibility.Visible;
        UpdateOverlayTracking();
        UpdateOverlayPositions();
    }

    private static void AddDashed(LineBuilder lines, Vector3 a, Vector3 b, float dash = 0.24f, float gap = 0.14f)
    {
        float length = (b - a).Length();
        if (length < 1e-4f) return;
        var u = (b - a) / length;
        for (float s = 0; s < length; s += dash + gap)
            lines.AddLine(a + u * s, a + u * Math.Min(length, s + dash));
    }

    // ───────────────────────────── Живая линейка (LiveDimensionHud) ─────────────────────────────

    private void SetActiveFace(ArchitecturalElement element, PushPullFace face)
    {
        _activeElementId = element.Id;
        _activeFace = face;
        _activeAxis = null;
        RefreshHandleColors();
    }

    private void SetActiveAxis(ArchitecturalElement element, Vector3 axis)
    {
        _hudFrozen = false;
        _activeElementId = element.Id;
        _activeAxis = axis;
        _activeFace = null;
        RefreshHandleColors();
    }

    /// <summary>Новый выбор: активна грань по умолчанию (длина стены/балки, высота колонны) или ось X Gizmo.</summary>
    private void ResetActiveTarget(ArchitecturalElement? element = null)
    {
        _activeElementId = element?.Id;
        _activeFace = element is not null && PushPullService.FacesFor(element).Count > 0 ? PushPullService.DefaultFace(element) : null;
        _activeAxis = _activeFace is null && element is not null ? Vector3.UnitX : null;
        _hoverFace = null;
        _hoverAxis = null;
        _hoverVertex = null;
        _hoverEdge = null;
        _typing = null;
        _hudFrozen = false;
        ShowHud();
    }

    /// <summary>Наведение на ручку или стрелку: цель становится активной для ввода с клавиатуры, рядом — живая линейка.</summary>
    private void UpdateHover(Windows.Foundation.Point position)
    {
        if (_walkActive || _scene?.SelectedElement is not { } el) return;
        // Пины вершин контура приоритетнее ручек граней и стрелок Gizmo.
        if (!_scene.IsMultiSelection && _typing is null && UpdateVertexHover(position))
        {
            if (_hoverFace is not null || _hoverAxis is not null)
            {
                _hoverFace = null;
                _hoverAxis = null;
                RefreshHandleColors();
                ShowHud();
            }
            return;
        }
        var face = _scene.IsMultiSelection ? null : HandleAt(position);
        var axis = face is null ? GizmoAxisAt(position) : null;
        if (face == _hoverFace && axis == _hoverAxis) return;
        _hoverFace = face;
        _hoverAxis = axis;
        if (_typing is null)
        {
            _hudFrozen = false;
            if (face is { } f) SetActiveFace(el, f);
            else if (axis is { } a) SetActiveAxis(el, a);
        }
        RefreshHandleColors();
        ShowHud();
    }

    private void OnViewportPointerExited()
    {
        if (_pullFace is not null || _dragElement is not null || _vertexElement is not null) return;
        _hoverFace = null;
        _hoverAxis = null;
        _hoverVertex = null;
        _hoverEdge = null;
        RefreshHandleColors();
        RefreshVertexColors();
        ShowHud();
    }

    private static string AxisName(Vector3 axis) => Math.Abs(axis.X) > 0.5f ? "X" : Math.Abs(axis.Y) > 0.5f ? "Y" : "Z";

    /// <summary>
    /// Капсула размера у активного ребра: при перетаскивании ручки — «Длина 6 000 мм (+2 000 мм)», при сдвиге Gizmo —
    /// «Сдвиг X 1 200 мм», при наборе с клавиатуры — поле ввода в мм с подсказкой Enter/Esc.
    /// </summary>
    private void ShowHud()
    {
        // Волна 6: капсула точки участка (наведение / перетаскивание пина) — вместо капсулы элемента.
        if ((_siteDrag is not null || _siteHover is not null) && !_walkActive && ShowSiteHud()) return;
        if (_vm is null || _scene?.SelectedElement is not { } el || _walkActive)
        {
            HudBorder.Visibility = Visibility.Collapsed;
            UpdateOverlayTracking();
            return;
        }
        // Вершина контура (наведение / перетаскивание) или маркер ребра — капсула координат вершины.
        if (_typing is null && _pullFace is null && _dragElement is null && !_scene.IsMultiSelection && ShowVertexHud(el)) return;
        var loc = _vm.Loc;
        string mm = loc.Get("Mm");
        PushPullFace? face = null;
        Vector3? axis = null;
        bool dragging = false;
        if (_pullFace is { } pf && _pullElement == el) { face = pf; dragging = true; }
        else if (_dragElement == el) { axis = _dragAxis; dragging = true; }
        else if (_typing is not null || _hudFrozen) { face = _activeFace; axis = _activeAxis; }
        else if (_hoverFace is { } hf) face = hf;
        else if (_hoverAxis is { } ha) axis = ha;

        if (face is null && axis is null)
        {
            HudBorder.Visibility = Visibility.Collapsed;
            UpdateOverlayTracking();
            return;
        }

        bool typing = _typing is not null;
        if (face is { } f)
        {
            double length = PushPullService.Length(el, f);
            HudGlyph.Glyph = "";
            HudLabel.Text = loc.Get(PushPullService.DimensionKey(el, f)) + ":";
            HudValue.Text = typing ? $"{_typing}▏ {mm}" : PushPullService.Millimeters(length, unit: mm);
            HudDelta.Text = !typing && (dragging || _hudFrozen) && Math.Abs(length - _pullStartLength) > 5e-4
                ? $"({PushPullService.Millimeters(length - _pullStartLength, signed: true, unit: mm)})"
                : string.Empty;
        }
        else if (axis is { } a)
        {
            HudGlyph.Glyph = "";
            HudLabel.Text = loc.Format("ShiftAxis", AxisName(a)) + ":";
            HudValue.Text = typing ? $"{_typing}▏ {mm}" : PushPullService.Millimeters(dragging ? _dragDelta : 0, unit: mm);
            HudDelta.Text = string.Empty;
        }
        HudDelta.Visibility = HudDelta.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HudValueBox.Background = typing ? (Brush)Application.Current.Resources["AccentSoftBrush"] : null;
        HudValueBox.Padding = typing ? new Thickness(5, 0, 5, 1) : new Thickness(0);
        HudHint.Text = typing ? loc.Get("HudTypeHint") : dragging ? (_vm.IsOSnapEnabled ? "OSnap · Alt — 10 " + mm : string.Empty) : loc.Get("HudKeysHint");
        HudHint.Visibility = HudHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HudBorder.Visibility = Visibility.Visible;
        UpdateOverlayTracking();
        UpdateOverlayPositions();
    }

    /// <summary>3D-точка, к которой привязана капсула: центр активной грани или конец стрелки Gizmo.</summary>
    private Vector3? HudAnchor()
    {
        if (SiteHudAnchor() is { } siteAnchor) return siteAnchor;
        if (_scene?.SelectedElement is not { } el) return null;
        if (_typing is null && _pullFace is null && _dragElement is null && VertexHudAnchor() is { } vertexAnchor) return vertexAnchor;
        PushPullFace? face = null;
        Vector3? axis = null;
        if (_pullFace is { } pf) face = pf;
        else if (_dragElement is not null) axis = _dragAxis;
        else if (_typing is not null || _hudFrozen) { face = _activeFace; axis = _activeAxis ?? Vector3.UnitX; }
        else if (_hoverFace is { } hf) face = hf;
        else axis = _hoverAxis ?? Vector3.UnitX;

        if (face is { } f && PushPullService.FacesFor(el).Contains(f))
        {
            var (x, y, z) = PushPullService.FaceCenter(el, f);
            return new Vector3((float)x, (float)y, (float)z);
        }
        if (axis is { } a && _gizmoTips.TryGetValue(a, out var tip)) return tip.To;
        return new Vector3((float)el.X, (float)el.Y, (float)el.Z);
    }

    private void UpdateOverlayTracking() =>
        SetOverlayTracking(HudBorder.Visibility == Visibility.Visible || SnapBadge.Visibility == Visibility.Visible || MeasureCapsule.Visibility == Visibility.Visible);

    /// <summary>Пока видны капсулы, их экранные позиции пересчитываются каждый кадр (камера может двигаться).</summary>
    private void SetOverlayTracking(bool on)
    {
        if (on == _overlayTracking) return;
        _overlayTracking = on;
        if (on) CompositionTarget.Rendering += OnOverlayRendering;
        else CompositionTarget.Rendering -= OnOverlayRendering;
    }

    private void OnOverlayRendering(object? sender, object e) => UpdateOverlayPositions();

    private void UpdateOverlayPositions()
    {
        UpdateMeasureCapsulePosition();
        if (HudBorder.Visibility == Visibility.Visible)
        {
            if (HudAnchor() is { } anchor && TryProject(anchor, out var p))
            {
                HudBorder.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double w = HudBorder.DesiredSize.Width, h = HudBorder.DesiredSize.Height;
                double x = Math.Clamp(p.X + 18, 8, Math.Max(8, ActualWidth - w - 8));
                double y = Math.Clamp(p.Y - h - 14, 8, Math.Max(8, ActualHeight - h - 8));
                Canvas.SetLeft(HudBorder, Math.Round(x));
                Canvas.SetTop(HudBorder, Math.Round(y));
                HudBorder.Opacity = 1;
            }
            else HudBorder.Opacity = 0;
        }
        if (SnapBadge.Visibility == Visibility.Visible && _snapHit is { } hit)
        {
            var contact = new Vector3((float)hit.Contact.X, (float)hit.Contact.Y, (float)hit.Contact.Z);
            if (TryProject(contact, out var p))
            {
                SnapBadge.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double w = SnapBadge.DesiredSize.Width, h = SnapBadge.DesiredSize.Height;
                Canvas.SetLeft(SnapBadge, Math.Round(Math.Clamp(p.X - w / 2, 8, Math.Max(8, ActualWidth - w - 8))));
                Canvas.SetTop(SnapBadge, Math.Round(Math.Clamp(p.Y + 14, 8, Math.Max(8, ActualHeight - h - 8))));
                SnapBadge.Opacity = 1;
            }
            else SnapBadge.Opacity = 0;
        }
    }

    /// <summary>Проекция мировой точки на экран вьюпорта (DIP); false — точка позади камеры.</summary>
    private bool TryProject(Vector3 point, out Windows.Foundation.Point screen)
    {
        screen = default;
        if (_viewport?.Camera is not ProjectionCamera camera) return false;
        double w = _viewport.ActualWidth, h = _viewport.ActualHeight;
        if (w < 1 || h < 1) return false;
        float aspect = (float)(w / h);
        var view = Matrix.LookAtRH(camera.Position, camera.Position + camera.LookDirection, camera.UpDirection);
        var projection = camera is OrthographicCamera ortho
            ? Matrix.OrthoRH((float)ortho.Width, (float)(ortho.Width / aspect), (float)ortho.NearPlaneDistance, (float)ortho.FarPlaneDistance)
            : Matrix.PerspectiveFovRH((float)(((PerspectiveCamera)camera).FieldOfView * Math.PI / 180), aspect, (float)camera.NearPlaneDistance, (float)camera.FarPlaneDistance);
        var v = Vector4.Transform(new Vector4(point, 1), view * projection);
        if (v.W <= 1e-6f) return false;
        screen = new Windows.Foundation.Point((v.X / v.W + 1) / 2 * w, (1 - v.Y / v.W) / 2 * h);
        return true;
    }

    /// <summary>Размер экранного пикселя (м) на глубине точки: перспектива — 2·d·tg(FOV/2) / H, орто — Width / W.</summary>
    private float WorldPerPixel(Vector3 at)
    {
        if (_viewport?.Camera is not ProjectionCamera camera) return 0.01f;
        double w = Math.Max(1, _viewport.ActualWidth), h = Math.Max(1, _viewport.ActualHeight);
        if (camera is OrthographicCamera ortho) return (float)(ortho.Width / w);
        double depth = Math.Max(0.1, Vector3.Dot(at - camera.Position, Vector3.Normalize(camera.LookDirection)));
        double fov = ((PerspectiveCamera)camera).FieldOfView * Math.PI / 180;
        return (float)(2 * depth * Math.Tan(fov / 2) / h);
    }

    private static double Distance(Windows.Foundation.Point a, Windows.Foundation.Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double DistanceToSegment(Windows.Foundation.Point p, Windows.Foundation.Point a, Windows.Foundation.Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double len2 = vx * vx + vy * vy;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
        return Distance(p, new Windows.Foundation.Point(a.X + vx * t, a.Y + vy * t));
    }

    // ───────────────────────────── Ввод с клавиатуры ─────────────────────────────

    /// <summary>
    /// Клавиши для вьюпорта (MainWindow передаёт их, если фокус не в поле ввода): цифры открывают в капсуле поле точного размера
    /// в мм, Enter применяет (неподвижный противоположный торец, шаг Undo), Backspace стирает; в прогулке — Q/E, PageUp/PageDown.
    /// </summary>
    public bool HandleKey(VirtualKey key)
    {
        // M — рулетка (вкл/выкл); в прогулке и при наборе размера не перехватывается.
        if (key == VirtualKey.M && _vm is not null && _typing is null && _dragElement is null && _pullFace is null)
        {
            _vm.IsMeasureMode = !_vm.IsMeasureMode;
            return true;
        }
        if (_walkActive) return HandleWalkKey(key);
        if (_scene?.SelectedElement is not { } el || _vm is null) return false;
        if (_dragElement is not null || _pullFace is not null) return false;

        char? ch = key switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => (char)('0' + (key - VirtualKey.Number0)),
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => (char)('0' + (key - VirtualKey.NumberPad0)),
            VirtualKey.Decimal or (VirtualKey)190 or (VirtualKey)188 => '.',
            VirtualKey.Subtract or (VirtualKey)189 => '-',
            _ => null
        };
        if (ch is { } c)
        {
            if (_typing is null)
            {
                // Ввод начинается с цифры (или «−» для сдвига по оси).
                if (c == '.') return false;
                if (_scene.IsMultiSelection && _activeAxis is null) SetActiveAxis(el, Vector3.UnitX);
                if (_activeFace is null && _activeAxis is null) ResetActiveTarget(el);
                if (c == '-' && _activeAxis is null) return false;
                _typing = string.Empty;
            }
            if (c == '-' && _typing.Length > 0) return true;
            if (c == '.' && _typing.Contains('.')) return true;
            if (_typing.Length < 9) _typing += c;
            RefreshHandleColors();
            ShowHud();
            return true;
        }
        if (_typing is null) return false;
        switch (key)
        {
            case VirtualKey.Back:
                _typing = _typing.Length > 0 ? _typing[..^1] : _typing;
                ShowHud();
                return true;
            case VirtualKey.Enter:
                CommitTyping(el);
                return true;
        }
        return false;
    }

    /// <summary>Идёт набор точного размера с клавиатуры (Delete и прочие горячие клавиши не должны срабатывать).</summary>
    public bool IsTypingDimension => _typing is not null;

    /// <summary>Esc: отмена набора размера, выход из прогулки. true — клавиша обработана вьюпортом.</summary>
    public bool HandleEscape()
    {
        if (_typing is not null)
        {
            _typing = null;
            RefreshHandleColors();
            ShowHud();
            return true;
        }
        if (HandleMeasureEscape()) return true;
        if (_walkActive && _vm is not null)
        {
            _vm.IsWalkthrough = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Применение набранного значения: для грани — новый габарит в мм (противоположный торец неподвижен),
    /// для оси Gizmo — сдвиг выделенной группы на заданное число мм; один шаг Undo.
    /// </summary>
    private void CommitTyping(ArchitecturalElement el)
    {
        string text = _typing ?? string.Empty;
        _typing = null;
        if (_scene is null || _vm is null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm) || !double.IsFinite(mm))
        {
            ShowHud();
            return;
        }
        if (_activeFace is { } face && !_scene.IsMultiSelection)
        {
            if (mm > 0)
            {
                _pullStartLength = PushPullService.Length(el, face);
                _vm.ApplyPushPull(el, face, mm / 1000);
                _hudFrozen = true;
            }
        }
        else
        {
            var axis = _activeAxis ?? Vector3.UnitX;
            double d = mm / 1000;
            var group = _scene.IsMultiSelection ? _scene.SelectedElements.ToList() : new List<ArchitecturalElement> { el };
            var starts = group.ToDictionary(g => g.Id, g => new[] { g.X, g.Y, g.Z });
            _scene.PushUndo();
            _scene.MoveGroup(group, starts, axis.X * d, axis.Y * d, axis.Z * d);
        }
        RefreshHandleColors();
        ShowHud();
    }

    // ───────────────────────────── Режим прогулки ─────────────────────────────

    /// <summary>
    /// Вход в прогулку: перспектива FOV 70°, глаза на 1.70 м над перекрытием активного этажа — внутри здания у входной двери
    /// (1.8 м от неё, взгляд внутрь) или в центре постройки; вращение/панорама Helix отключены.
    /// </summary>
    private void EnterWalk()
    {
        if (_viewport is null || _scene is null || _vm is null || _walkActive) return;
        if (_vm.IsSectionBoxEnabled) _vm.IsSectionBoxEnabled = false;
        int level = _scene.ActiveLevelFilter >= 0 ? _scene.ActiveLevelFilter : 0;
        var elements = _scene.VisibleElements.ToList();
        var door = elements.FirstOrDefault(e => e.Type == ElementTypes.Door && e.LevelIndex == level)
                   ?? elements.FirstOrDefault(e => e.Type == ElementTypes.Door);
        string groupId = door?.BuildingGroupId ?? elements.FirstOrDefault(e => !ElementTypes.IsVoidType(e.Type))?.BuildingGroupId ?? BuildingGroup.DefaultGroupId;
        var groupItems = elements.Where(e => e.BuildingGroupId == groupId && !ElementTypes.IsVoidType(e.Type)).ToList();
        var (minX, maxX, minZ, maxZ) = groupItems.Count > 0 ? SceneStateService.Bounds(groupItems) : (-5, 5, -5, 5);
        double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;

        _walkX = cx;
        _walkZ = cz;
        _walkYaw = 0;
        if (door is not null)
        {
            var (nx, nz) = ArchitecturalElement.RotateY(0, 1, door.RotationY);
            if (nx * (cx - door.X) + nz * (cz - door.Z) < 0) { nx = -nx; nz = -nz; }
            _walkX = door.X + nx * 1.8;
            _walkZ = door.Z + nz * 1.8;
            _walkYaw = Math.Atan2(nx, -nz);
        }
        _walkPitch = -4 * Math.PI / 180;
        _walkSurfaces = null;
        _walkObstacles = null; _walkWorld = null;
        var marks = StructuralAxesBuilder.LevelElevations(_scene.Elements.Where(e => e.BuildingGroupId == groupId).ToList());
        double probe = (marks.FirstOrDefault(m => m.Level == level)?.Elevation ?? 0) + 0.05;
        _walkWorld = null;
        _walkFloor = WalkPhysicsService.FloorAt(WalkWorld(), _walkX, _walkZ, probe, WalkPhysicsService.MaxStep);
        // Старт не внутри стены или колонны: капсулу выталкивает на ближайшее свободное место; двери у входа открыты.
        if (door is not null) WalkWorld().OpenDoors.Add(door.Id);
        WalkWorld().RefreshDoors();
        _walkOpenDoors.Clear();
        foreach (var id in WalkWorld().OpenDoors) _walkOpenDoors.Add(id);
        ApplyDoorVisuals();
        (_walkX, _walkZ) = WalkCollisionService.Resolve(WalkWorld().ActiveObstacles, _walkX, _walkZ, WalkPhysicsService.Radius);
        _walkFloor = WalkPhysicsService.FloorAt(WalkWorld(), _walkX, _walkZ, probe, WalkPhysicsService.MaxStep);
        _walkVy = 0;
        _walkStatusFloor = _walkFloor;
        _walkEye = _walkFloor + EyeHeight;

        _walkActive = true;
        _viewport.Camera = new PerspectiveCamera
        {
            Position = new Vector3((float)_walkX, (float)_walkEye, (float)_walkZ),
            LookDirection = WalkLook(),
            UpDirection = Vector3.UnitY,
            FieldOfView = 70,
            NearPlaneDistance = 0.05,
            FarPlaneDistance = 4000
        };
        _viewport.IsRotationEnabled = false;
        _viewport.IsPanEnabled = false;
        _viewport.IsZoomEnabled = false;
        _viewport.IsInertiaEnabled = false;
        _viewport.ShowViewCube = false;
        _viewport.ShowCoordinateSystem = false;
        ApplySun();
        ShowHud();
        ShowSnap(null);
        UpdateWalkStatus();

        _walkLast = DateTime.UtcNow;
        _walkTimer ??= CreateWalkTimer();
        _walkTimer.Start();
        Focus(FocusState.Programmatic);
    }

    private void ExitWalk()
    {
        if (!_walkActive || _viewport is null) return;
        _walkActive = false;
        // Двери возвращаются в закрытое положение (вид модели — как до прогулки).
        _walkOpenDoors.Clear();
        ApplyDoorVisuals();
        _walkTimer?.Stop();
        EndLook();
        _viewport.IsZoomEnabled = true;
        _viewport.IsPanEnabled = true;
        _viewport.IsInertiaEnabled = true;
        if (_vm is not null) _vm.WalkStatusText = string.Empty;
        ApplySun();
        ResetCamera();
        UpdateSelectionVisuals();
    }

    private DispatcherTimer CreateWalkTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => WalkTick();
        return timer;
    }

    private Vector3 WalkLook()
    {
        double cp = Math.Cos(_walkPitch);
        return new Vector3((float)(Math.Sin(_walkYaw) * cp), (float)Math.Sin(_walkPitch), (float)(-Math.Cos(_walkYaw) * cp));
    }

    /// <summary>
    /// Кадр прогулки: WASD/стрелки (Shift — быстрее) в плоскости взгляда, пол под ногами — по плитам и лестничным маршам
    /// (подъём до 0.45 м за шаг), высота глаз плавно догоняет «пол + 1.70 м».
    /// </summary>
    private void WalkTick()
    {
        if (!_walkActive || _viewport?.Camera is not ProjectionCamera camera) return;
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _walkLast).TotalSeconds, 0, 0.1);
        _walkLast = now;

        bool textFocused = XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox or NumberBox or PasswordBox;
        if (!textFocused)
        {
            double f = (IsKeyDown(VirtualKey.W) || IsKeyDown(VirtualKey.Up) ? 1 : 0) - (IsKeyDown(VirtualKey.S) || IsKeyDown(VirtualKey.Down) ? 1 : 0);
            double r = (IsKeyDown(VirtualKey.D) || IsKeyDown(VirtualKey.Right) ? 1 : 0) - (IsKeyDown(VirtualKey.A) || IsKeyDown(VirtualKey.Left) ? 1 : 0);
            if (f != 0 || r != 0)
            {
                double speed = IsKeyDown(VirtualKey.Shift) ? 4.5 : 1.6;
                double fx = Math.Sin(_walkYaw), fz = -Math.Cos(_walkYaw);
                double rx = Math.Cos(_walkYaw), rz = Math.Sin(_walkYaw);
                double mx = f * fx + r * rx, mz = f * fz + r * rz;
                double len = Math.Sqrt(mx * mx + mz * mz);
                // Волна 6: капсула R = 0,28 м — упор в стены, колонны, парапеты и закрытые двери со скольжением вдоль
                // поверхности; ступени до 0,22 м — автоматически; ниже — падение по гравитации.
                WalkPhysicsStep(mx / len * speed * dt, mz / len * speed * dt, dt);
            }
            else WalkPhysicsStep(0, 0, dt);
        }
        double floor = _walkFloor;
        bool floorChanged = Math.Abs(floor - _walkStatusFloor) > 0.05;
        if (floorChanged) _walkStatusFloor = floor;
        _walkEye += (_walkFloor + EyeHeight - _walkEye) * (1 - Math.Exp(-dt * 9));
        camera.Position = new Vector3((float)_walkX, (float)_walkEye, (float)_walkZ);
        camera.LookDirection = WalkLook();
        camera.UpDirection = Vector3.UnitY;
        if (floorChanged) UpdateWalkStatus();
    }

    private bool HandleWalkKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.E:
                // Волна 6: E — открыть / закрыть дверь перед собой; без двери рядом — этаж выше (как прежде).
                if (!ToggleDoorAhead()) ChangeWalkLevel(+1);
                return true;
            case VirtualKey.PageUp:
                ChangeWalkLevel(+1);
                return true;
            case VirtualKey.Q or VirtualKey.PageDown:
                ChangeWalkLevel(-1);
                return true;
            case VirtualKey.W or VirtualKey.A or VirtualKey.S or VirtualKey.D or VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right:
                return true;
        }
        return false;
    }

    /// <summary>Q/E: пол соседнего этажа постройки под камерой (по отметкам этажей); высота глаз плавно поднимается/опускается.</summary>
    private void ChangeWalkLevel(int direction)
    {
        var marks = WalkLevels();
        if (marks.Count == 0) return;
        int current = CurrentWalkLevel(marks);
        int target = Math.Clamp(current + direction, 0, marks.Count - 1);
        if (target == current) return;
        _walkFloor = WalkPhysicsService.FloorAt(WalkWorld(), _walkX, _walkZ, marks[target].Elevation + 0.05, WalkPhysicsService.MaxStep);
        _walkVy = 0;
        // На другом этаже под ногами может оказаться стена — выталкиваем на свободное место.
        (_walkX, _walkZ) = WalkCollisionService.Resolve(WalkWorld().ActiveObstacles, _walkX, _walkZ, WalkPhysicsService.Radius);
        UpdateWalkStatus();
    }

    private List<LevelMark> WalkLevels()
    {
        if (_scene is null) return [];
        var groupItems = _scene.VisibleElements.Where(e => !ElementTypes.IsVoidType(e.Type)).ToList();
        string? group = groupItems.FirstOrDefault(e =>
        {
            var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds([e]);
            return e.Type is ElementTypes.Foundation or ElementTypes.Slab && _walkX >= minX && _walkX <= maxX && _walkZ >= minZ && _walkZ <= maxZ;
        })?.BuildingGroupId ?? groupItems.FirstOrDefault()?.BuildingGroupId;
        return group is null ? [] : StructuralAxesBuilder.LevelElevations(groupItems.Where(e => e.BuildingGroupId == group).ToList());
    }

    private int CurrentWalkLevel(List<LevelMark> marks)
    {
        int index = 0;
        for (int i = 0; i < marks.Count; i++)
            if (marks[i].Elevation <= _walkFloor + 0.3) index = i;
        return index;
    }

    private void UpdateWalkStatus()
    {
        if (_vm is null) return;
        var marks = WalkLevels();
        int level = marks.Count > 0 ? marks[CurrentWalkLevel(marks)].Level + 1 : 1;
        _vm.WalkStatusText = _vm.Loc.Format("WalkStatus", level, GostDrawingPdfExporter.Elevation(_walkFloor)) + WalkPhysicsStatus();
    }

    /// <summary>
    /// Отметка пола под точкой (x, z): наивысшая из поверхностей не выше feet + 0.45 м — верх плит, фундамента, террас и плоской
    /// кровли (кроме отверстий slab_opening), рампа лестничного марша (подъём вдоль локальной +Z, с площадками по 0.3 м); иначе земля.
    /// </summary>
    private double SurfaceAt(double x, double z, double feet)
    {
        _walkSurfaces ??= BuildWalkSurfaces();
        double best = 0;
        foreach (var s in _walkSurfaces)
        {
            var e = s.Element;
            if (s.IsStairs)
            {
                var (lx, lz) = ArchitecturalElement.RotateY(x - e.X, z - e.Z, -e.RotationY);
                if (Math.Abs(lx) > e.Width / 2 || Math.Abs(lz) > e.Depth / 2 + 0.3) continue;
                double t = Math.Clamp((lz + e.Depth / 2) / Math.Max(0.01, e.Depth), 0, 1);
                double top = e.BaseY + e.Height * t;
                if (top <= feet + StepUp && top > best) best = top;
                continue;
            }
            if (!SceneStateService.PointInPolygon(x, z, s.Outline) || s.Holes.Any(h => SceneStateService.PointInPolygon(x, z, h))) continue;
            int repeat = Math.Max(1, e.RepeatLevels);
            for (int i = 0; i < repeat; i++)
            {
                double top = e.TopY + e.LevelHeight * i;
                if (top > feet + StepUp) break;
                if (top > best) best = top;
            }
        }
        return best;
    }

    /// <summary>Препятствия прогулки по видимым элементам (кэш до изменения сцены).</summary>
    private List<WalkObstacle> WalkObstacles() =>
        _walkObstacles ??= _scene is null ? [] : WalkCollisionService.BuildObstacles(_scene.VisibleElements.ToList());

    private List<WalkSurface> BuildWalkSurfaces()
    {
        var list = new List<WalkSurface>();
        if (_scene is null) return list;
        foreach (var e in _scene.VisibleElements)
        {
            if (e.Type == ElementTypes.Stairs)
            {
                list.Add(new WalkSurface(e, [], [], true));
                continue;
            }
            if (e.Type is not (ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Foundation or ElementTypes.Terrace) && !e.IsFlatRoof) continue;
            var holes = _scene.GetOpeningsFor(e).Where(o => o.Type == ElementTypes.SlabOpening).Select(ElementGeometryBuilder.WorldOutline).ToList();
            list.Add(new WalkSurface(e, ElementGeometryBuilder.WorldOutline(e), holes, false));
        }
        return list;
    }

    /// <summary>Нажатие кнопки мыши в прогулке — начало обзора (yaw/pitch) с захватом указателя.</summary>
    private void OnViewportPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_walkActive && _viewport is not null)
        {
            // Правая кнопка на пине вершины контура — удалить вершину.
            var point = e.GetCurrentPoint(_viewport);
            if (point.Properties.IsRightButtonPressed && (TryRemoveSiteVertexAt(point.Position) || TryRemoveVertexAt(point.Position))) e.Handled = true;
            return;
        }
        if (!_walkActive || _viewport is null) return;
        _lookPointer = e.Pointer;
        _lookLast = e.GetCurrentPoint(_viewport).Position;
        _walkPressPosition = _lookLast;
        _viewport.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void UpdateLook(Windows.Foundation.Point position)
    {
        const double sensitivity = 0.22 * Math.PI / 180;
        _walkYaw += (position.X - _lookLast.X) * sensitivity;
        _walkPitch = Math.Clamp(_walkPitch - (position.Y - _lookLast.Y) * sensitivity, -80 * Math.PI / 180, 80 * Math.PI / 180);
        _lookLast = position;
        if (_viewport?.Camera is ProjectionCamera camera) camera.LookDirection = WalkLook();
    }

    private void EndLook(Pointer? pointer = null)
    {
        if (_lookPointer is null) return;
        if (_viewport is not null) _viewport.ReleasePointerCapture(pointer ?? _lookPointer);
        _lookPointer = null;
    }

    // ───────────────────────────── Инсоляция и падающие тени ─────────────────────────────

    private void OnSolarChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        ApplySun();
        QueueShadowRebuild();
    });

    /// <summary>Перестройка теней — не чаще одного раза за кадр UI (ползунок времени, перемещение элементов).</summary>
    private void QueueShadowRebuild()
    {
        if (_shadowQueued) return;
        if (_vm?.IsSunLighting != true)
        {
            _shadowGroup.Children.Clear();
            return;
        }
        _shadowQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _shadowQueued = false;
            if (_highResRendering) QueueShadowRebuild();
            else RebuildShadows();
        });
    }

    /// <summary>Фоновый расчёт теней идёт; за время расчёта сцена или Солнце изменились — нужен ещё один.</summary>
    private bool _shadowRunning, _shadowRerun;

    /// <summary>
    /// Силуэты и их объединение считаются в фоне по снимку видимых элементов (на 30-этажной башне — миллисекунды на кадр),
    /// меш собирается в UI-потоке; прежние тени остаются на экране до готовности новых (без мерцания). Изменения во время
    /// расчёта схлопываются в один повторный расчёт.
    /// </summary>
    private void RebuildShadows()
    {
        if (_viewport is null || _scene is null || _vm is not { IsSunLighting: true } || _theme is null)
        {
            _shadowGroup.Children.Clear();
            _siteGroundModel = null;
            return;
        }
        if (_shadowRunning)
        {
            _shadowRerun = true;
            return;
        }
        _shadowRunning = true;
        var visible = _scene.VisibleElements.Select(e => e.Clone()).ToList();
        var sun = _vm.Sun;
        var queue = DispatcherQueue;
        Task.Run(() => SolarShadowService.Build(visible, sun))
            .ContinueWith(task => queue.TryEnqueue(() => OnShadowsBuilt(task, visible)), TaskScheduler.Default);
    }

    private void OnShadowsBuilt(Task<GroundShadow> task, List<ArchitecturalElement> visible)
    {
        _shadowRunning = false;
        if (_shadowRerun || _highResRendering)
        {
            // Результат устарел (сцена или время изменились) или идёт рендер 4K — считаем заново по свежему снимку.
            _shadowRerun = false;
            QueueShadowRebuild();
            return;
        }
        if (task.Exception?.InnerException is { } ex)
        {
            App.WriteCrashLog("shadows", ex);
            return;
        }
        // Инсоляцию выключили, пока считались тени, — не возвращаем их на экран.
        if (!task.IsCompletedSuccessfully || _vm is not { IsSunLighting: true } || _theme is null) return;
        _shadowGroup.Children.Clear();
        _siteGroundModel = null;
        var shadow = task.Result;
        AddSiteGround(visible, shadow);
        if (shadow.IsEmpty) return;
        var positions = new Vector3Collection(shadow.Union.Count * 4);
        var normals = new Vector3Collection(shadow.Union.Count * 4);
        var indices = new IntCollection(shadow.Union.Count * 6);
        const float y = (float)SolarShadowService.GroundY;
        foreach (var t in shadow.Union)
        {
            int i = positions.Count;
            positions.Add(new Vector3((float)t.XL0, y, (float)t.Z0));
            positions.Add(new Vector3((float)t.XR0, y, (float)t.Z0));
            positions.Add(new Vector3((float)t.XR1, y, (float)t.Z1));
            positions.Add(new Vector3((float)t.XL1, y, (float)t.Z1));
            for (int k = 0; k < 4; k++) normals.Add(Vector3.UnitY);
            indices.Add(i); indices.Add(i + 2); indices.Add(i + 1);
            indices.Add(i); indices.Add(i + 3); indices.Add(i + 2);
        }
        float alpha = _theme.Current.IsLight ? 0x40 / 255f : 0x55 / 255f;
        _shadowGroup.Children.Add(new MeshGeometryModel3D
        {
            Geometry = new MeshGeometry3D { Positions = positions, Normals = normals, Indices = indices },
            Material = new PhongMaterial
            {
                DiffuseColor = new Color4(0, 0, 0, alpha),
                AmbientColor = new Color4(0, 0, 0, 1),
                EmissiveColor = new Color4(0, 0, 0, 1),
                SpecularColor = new Color4(0, 0, 0, 1)
            },
            IsTransparent = true,
            CullMode = CullMode.None,
            // Тень на 4 мм над плоскостью участка (1.5 мм): без сдвига глубины на дальних дистанциях и скользящих углах
            // буфер глубины их не различает — тень «рябит» зерном (z-fighting). Сдвиг к камере — постоянный и по наклону.
            DepthBias = -120,
            SlopeScaledDepthBias = -1.5,
            IsHitTestVisible = false,
            IsThrowingShadow = false
        });
    }

    /// <summary>
    /// Плоскость участка под тенями (только тёмные темы): почти чёрный фон вьюпорта не даёт прочитать полупрозрачную тень
    /// #55000000, поэтому на время инсоляции под постройками и их тенями лежит матовая плоскость чуть светлее фона (отступ 6 м).
    /// </summary>
    private void AddSiteGround(IReadOnlyList<ArchitecturalElement> visible, GroundShadow shadow)
    {
        if (_theme is null || _theme.Current.IsLight) return;
        var casters = visible.Where(SolarShadowService.CastsShadow).ToList();
        if (casters.Count == 0) return;
        var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(casters);
        foreach (var t in shadow.Union)
        {
            minX = Math.Min(minX, Math.Min(t.XL0, t.XL1));
            maxX = Math.Max(maxX, Math.Max(t.XR0, t.XR1));
            minZ = Math.Min(minZ, t.Z0);
            maxZ = Math.Max(maxZ, t.Z1);
        }
        const float margin = 6f, y = 0.0015f;
        // Внешний контур — против часовой, чаши бассейнов (утоплены в грунт) — вырезы по часовой; заливка ненулевой обмоткой.
        float x0 = (float)minX - margin, x1 = (float)maxX + margin, z0 = (float)minZ - margin, z1 = (float)maxZ + margin;
        var loops = new List<IReadOnlyList<System.Numerics.Vector2>> { new List<System.Numerics.Vector2> { new(x0, z0), new(x1, z0), new(x1, z1), new(x0, z1) } };
        foreach (var pool in visible.Where(e => e.Type == ElementTypes.Pool))
        {
            var outline = ElementGeometryBuilder.WorldOutline(pool);
            if (SolarShadowService.SignedArea(outline) > 0) outline.Reverse();
            loops.Add(outline.Select(p => new System.Numerics.Vector2((float)p.X, (float)p.Z)).ToList());
        }
        var builder = new MeshBuilder();
        foreach (var (a, b, c, d) in MeshAccumulator.FillTrapezoids(loops, [], alongX: false))
            builder.AddQuad(new Vector3(a.X, y, a.Y), new Vector3(b.X, y, b.Y), new Vector3(c.X, y, c.Y), new Vector3(d.X, y, d.Y));
        var ground = ToColor4(Blend(_theme.Current.ViewportBackground, Color.FromArgb(255, 255, 255, 255), 0.2));
        _shadowGroup.Children.Add(_siteGroundModel = new MeshGeometryModel3D
        {
            Geometry = builder.ToMeshGeometry3D(),
            Material = new PhongMaterial
            {
                DiffuseColor = ground,
                AmbientColor = ground,
                EmissiveColor = new Color4(ground.Red * 0.35f, ground.Green * 0.35f, ground.Blue * 0.35f, 1f),
                SpecularColor = new Color4(0, 0, 0, 1)
            },
            CullMode = CullMode.None,
            // Плоскость на 1.5 мм над сеткой участка: небольшой постоянный сдвиг глубины к камере, чтобы линии сетки не
            // «просвечивали» пунктиром (меньше, чем у теневого меша и у марок осей — они остаются поверх плоскости).
            DepthBias = -12,
            IsHitTestVisible = false,
            IsThrowingShadow = false
        });
    }

    /// <summary>
    /// Солнечный свет: направление ключевого света — вдоль солнечного луча, цветовая температура — от золотистой у горизонта
    /// до нейтральной в полдень; заполняющий свет приглушается. Без инсоляции — свет темы и исходные направления.
    /// </summary>
    private void ApplySun()
    {
        if (_viewport is null || _theme is null) return;
        var theme = _theme.Current;
        // В прогулке заполняющий свет идёт снизу вверх — потолки и нижние грани перекрытий не проваливаются в темноту.
        var interiorFill = new Vector3(0.3f, 0.85f, 0.4f);
        if (_vm?.IsSunLighting != true)
        {
            _keyLight.Direction = DefaultKeyDirection;
            _fillLight.Direction = _walkActive ? interiorFill : DefaultFillDirection;
            _keyLight.Color = theme.KeyLight;
            _fillLight.Color = theme.FillLight;
            _ambient.Color = theme.AmbientLight;
            _shadowGroup.Children.Clear();
            return;
        }
        var sun = _vm.Sun;
        var (r, g, b) = SolarShadowService.LightColor(sun);
        _keyLight.Color = Color.FromArgb(255, ToByte(r), ToByte(g), ToByte(b));
        _keyLight.Direction = sun.IsAboveHorizon
            ? new Vector3(-(float)sun.DirX, -(float)sun.DirY, -(float)sun.DirZ)
            : new Vector3(0.2f, -1f, 0.3f);
        // Небесный (заполняющий) свет — с противоположной Солнцу стороны, слабее и холоднее.
        _fillLight.Direction = _walkActive ? interiorFill : new Vector3((float)sun.DirX, -0.45f, (float)sun.DirZ);
        _fillLight.Color = Blend(theme.FillLight, Color.FromArgb(255, 0, 0, 0), 0.45);
        double w = sun.IsAboveHorizon ? sun.Warmth : 0;
        var warm = Color.FromArgb(255, 0xC8, 0x8A, 0x52);
        _ambient.Color = Blend(theme.AmbientLight, warm, 0.3 * w);
        QueueShadowRebuild();
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

#if DEBUG
    /// <summary>
    /// Только Debug (AIA_DEBUG_PUSHPULL): вытягивание грани элемента до «сырой» длины rawLength (м) тем же путём, что и мышь
    /// (с привязкой OSnap), капсула размера и направляющая привязки остаются на экране для съёмки.
    /// </summary>
    public void DebugPushPull(ArchitecturalElement element, PushPullFace face, double rawLength)
    {
        if (_scene is null) return;
        _scene.SelectedElement = element;
        UpdateSelectionVisuals();
        StartPull(element, face);
        PushPullTo(rawLength - _pullStartLength);
        var snap = _snapHit;
        bool changed = _dragUndoPushed;
        _pullFace = null;
        _pullElement = null;
        if (changed) _vm?.CompletePushPull(element);
        _hudFrozen = true;
        ShowSnap(snap);
        RefreshHandleColors();
        ShowHud();
    }

    /// <summary>Только Debug (AIA_DEBUG_WALKAT): переход прогулки в точку (x, z) с курсом yaw° (0 — на север плана, −Z).</summary>
    public void DebugWalkTo(double x, double z, double yawDegrees, double forward = 0)
    {
        if (!_walkActive) return;
        _walkX = x;
        _walkZ = z;
        _walkYaw = yawDegrees * Math.PI / 180;
        _walkFloor = SurfaceAt(x, z, _walkFloor);
        // Шаги вперёд по 5 см тем же поиском пола и теми же коллизиями, что и WASD (подъём по лестничному маршу, упор в стены).
        for (double s = 0; s < forward; s += 0.05)
        {
            (_walkX, _walkZ, _) = WalkCollisionService.Move(WalkObstacles(), _walkX, _walkZ, Math.Sin(_walkYaw) * 0.05, -Math.Cos(_walkYaw) * 0.05, _walkFloor, EyeHeight);
            _walkFloor = SurfaceAt(_walkX, _walkZ, _walkFloor);
        }
        _walkEye = _walkFloor + EyeHeight;
        UpdateWalkStatus();
    }
#endif
}
