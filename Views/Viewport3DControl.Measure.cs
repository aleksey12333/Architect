using System.Diagnostics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Rendering;
using AIArchitectStudio.ViewModels;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.WinUI;
using Microsoft.UI.Xaml;
using SharpDX;
using Windows.Graphics.Imaging;
using Windows.System;
using Color = Windows.UI.Color;
using CullMode = SharpDX.Direct3D11.CullMode;

namespace AIArchitectStudio.Views;

/// <summary>
/// Интерактивная 3D-рулетка (клавиша M): точки P1/P2 по поверхности модели с привязкой OSnap (углы габарита элемента —
/// экранный радиус 12 px, оси/торцы/грани/края плит — SmartSnapService), живая размерная линия с засечками 45°, стеклянная
/// капсула L / ΔX / ΔY / ΔZ / наклон и закрепление размера на чертеже; закреплённые размеры видны поверх модели.
/// Здесь же — рендер текущего вида в буфер 3840 × 2160 и запись PNG (фон неба или прозрачный, Alpha = 0).
/// </summary>
public sealed partial class Viewport3DControl
{
    private const double MeasureSnapPixels = 12;
    private const double DimensionPickPixels = 6;

    // Закреплённые размеры (всегда поверх модели) и живой обмер.
    private readonly TopMostGroup3D _measureGroup = new() { EnableTopMost = true };
    private readonly TopMostGroup3D _measureLiveGroup = new() { EnableTopMost = true };
    private Vector3? _measureA, _measureB, _measureCursor;
    private string? _measureSelectedId;
    private List<SnapTarget>? _measureTargets;
    private Vector3 _measureLook = new(0, -1, 0);
    private Windows.Foundation.Point _measureLastPointer = new(-1, -1);

    /// <summary>Идёт рендер высокого разрешения: перестройка теней откладывается до его завершения.</summary>
    private bool _highResRendering;

    private void OnDimensionsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_measureSelectedId is not null && _scene?.FindDimension(_measureSelectedId) is null) _measureSelectedId = null;
        RebuildDimensions();
        UpdateMeasureCapsule();
    });

    // ───────────────────────────── Состояние режима ─────────────────────────────

    private void OnMeasureModeChanged()
    {
        ResetMeasurement();
        _measureTargets = null;
        if (_vm?.IsMeasureMode != true)
        {
            _measureCursor = null;
            ShowSnap(null);
        }
        RebuildMeasureLive();
        UpdateMeasureCapsule();
    }

    private void ResetMeasurement()
    {
        _measureA = null;
        _measureB = null;
        _measureSelectedId = null;
        if (_vm is not null && _vm.IsMeasureMode) _vm.MeasureHintText = _vm.Loc.Get("MeasureHintStart");
        RebuildDimensions();
    }

    /// <summary>Esc в режиме рулетки: сброс текущего замера, повторный Esc — выход из режима.</summary>
    private bool HandleMeasureEscape()
    {
        if (_vm?.IsMeasureMode != true) return false;
        if (_measureA is not null || _measureSelectedId is not null)
        {
            ResetMeasurement();
            RebuildMeasureLive();
            UpdateMeasureCapsule();
        }
        else _vm.IsMeasureMode = false;
        return true;
    }

    /// <summary>Delete при выбранном закреплённом размере (режим рулетки) — удалить размер; true — обработано.</summary>
    public bool HandleMeasureDelete()
    {
        if (_vm?.IsMeasureMode != true || _measureSelectedId is not { } id) return false;
        _vm.RemoveDimension(id);
        ResetMeasurement();
        UpdateMeasureCapsule();
        return true;
    }

    // ───────────────────────────── Клики и наведение ─────────────────────────────

    /// <summary>
    /// Клик в режиме рулетки: по закреплённому размеру — выбрать его (капсула с «Удалить размер»); иначе первая точка P1,
    /// вторая — P2 (размер фиксируется, на капсуле — «Закрепить размер на чертеже»); после фиксации клик начинает новый замер.
    /// </summary>
    private void HandleMeasureClick(MouseDown3DEventArgs e)
    {
        if (_viewport is null || _vm is null) return;
        // ViewCube и служебные модели (не элементы сцены) — клик не ставит точку.
        if (e.HitTestResult?.ModelHit is Element3D model && !_hitMap.ContainsKey(model)) return;

        bool restart = _measureB is not null || _measureSelectedId is not null;
        if (_measureA is null || restart)
        {
            if (DimensionAt(e.Position) is { } picked)
            {
                _measureA = null;
                _measureB = null;
                _measureSelectedId = picked.Id;
                ShowSnap(null);
                RebuildDimensions();
                RebuildMeasureLive();
                UpdateMeasureCapsule();
                return;
            }
        }
        if (MeasurePointAt(e.Position) is not { } hit) return;
        if (restart)
        {
            _measureB = null;
            _measureSelectedId = null;
            RebuildDimensions();
        }
        if (_measureA is null || restart)
        {
            _measureA = hit.Point;
            _vm.MeasureHintText = _vm.Loc.Get("MeasureHintSecond");
        }
        else
        {
            if ((hit.Point - _measureA.Value).Length() < MeasurementService.MinLength) return;
            _measureB = hit.Point;
            _vm.MeasureHintText = _vm.Loc.Get("MeasureFixed");
            ShowSnap(null);
        }
        _measureCursor = hit.Point;
        RebuildMeasureLive();
        UpdateMeasureCapsule();
    }

    /// <summary>Движение мыши: привязка и живая линия от P1 до курсора (после фиксации размер не меняется).</summary>
    private void UpdateMeasureHover(Windows.Foundation.Point position)
    {
        if (Math.Abs(position.X - _measureLastPointer.X) < 0.5 && Math.Abs(position.Y - _measureLastPointer.Y) < 0.5) return;
        _measureLastPointer = position;
        if (_measureB is not null || _measureSelectedId is not null) return;
        var hit = MeasurePointAt(position);
        ShowSnap(hit?.Snap);
        _measureCursor = hit?.Point;
        RebuildMeasureLive();
        UpdateMeasureCapsule();
    }

    /// <summary>
    /// Точка обмера под курсором: поверхность ближайшего элемента (иначе горизонтальная плоскость через P1 или земля);
    /// при OSnap (без Alt) — угол или середина ребра габарита элемента в радиусе 12 px экрана, затем плановые цели
    /// OSnap (узлы и линии осей, торцы, углы и грани стен, центры колонн, края плит) на отметке точки.
    /// </summary>
    private (Vector3 Point, SnapHit? Snap)? MeasurePointAt(Windows.Foundation.Point position)
    {
        if (_viewport is null || _vm is null) return null;
        Vector3? point = null;
        ArchitecturalElement? element = null;
        foreach (var h in _viewport.FindHits(position))
        {
            if (h.ModelHit is not Element3D m || !_hitMap.TryGetValue(m, out var el) || ElementTypes.IsVoidType(el.Type)) continue;
            point = h.PointHit;
            element = el;
            break;
        }
        if (point is null)
        {
            float y = _measureA?.Y ?? 0f;
            point = _viewport.UnProjectOnPlane(position, new Vector3(0, y, 0), Vector3.UnitY);
        }
        if (point is not { } p) return null;
        if (_vm.IsOSnapEnabled != true || IsKeyDown(VirtualKey.Menu)) return (p, null);

        if (element is not null && _geometryById.TryGetValue(element.Id, out var geometry) && !geometry.IsEmpty)
        {
            // Углы и середины рёбер габарита — из независимой геометрии ядра; здесь — только экранная проекция.
            var candidates = MeasurementService.BoxSnapCandidates(geometry);
            if (MeasurementService.PickSnap(candidates, c => TryProject(c.ToDx(), out var s) ? Distance(s, position) : null, MeasureSnapPixels) is { } pick)
            {
                var v = pick.Candidate.Point.ToDx();
                bool english = !_vm.Loc.IsRussian;
                string label = pick.Candidate.IsMidpoint ? english ? "Edge midpoint" : "Середина ребра" : _vm.Loc.Get("MeasureVertex");
                var target = new SnapTarget(SnapKind.Vertex, SnapShape.Point, label, v.X, v.Z, Y: v.Y);
                var at = (v.X, (double)v.Y, (double)v.Z);
                return (v, new SnapHit(target, 0, at, at, pick.Distance));
            }
        }
        _measureTargets ??= BuildSnapTargets([]);
        if (_measureTargets is { Count: > 0 } targets)
        {
            double radius = Math.Max(SmartSnapService.Radius, WorldPerPixel(p) * MeasureSnapPixels);
            if (SmartSnapService.SnapPoint(p.X, p.Y, p.Z, targets, radius) is { } snap)
                return (new Vector3((float)snap.Contact.X, p.Y, (float)snap.Contact.Z), snap);
        }
        return (p, null);
    }

    /// <summary>Закреплённый размер под курсором (≤ 6 px до проекции размерной линии).</summary>
    private PinnedDimension? DimensionAt(Windows.Foundation.Point position)
    {
        if (_scene is null) return null;
        PinnedDimension? best = null;
        double bestDistance = DimensionPickPixels;
        foreach (var d in _scene.PinnedDimensions)
        {
            if (!TryProject(V(d.A), out var a) || !TryProject(V(d.B), out var b)) continue;
            double dist = DistanceToSegment(position, a, b);
            if (dist <= bestDistance)
            {
                bestDistance = dist;
                best = d;
            }
        }
        return best;
    }

    private static Vector3 V((double X, double Y, double Z) p) => new((float)p.X, (float)p.Y, (float)p.Z);

    private static (double X, double Y, double Z) T(Vector3 v) => (v.X, v.Y, v.Z);

    // ───────────────────────────── Геометрия размеров ─────────────────────────────

    /// <summary>Цвет закреплённых размеров: графит в светлой теме и в макете, светлый текст темы — в тёмных темах.</summary>
    private Color DimensionColor => _vm?.IsClayMode == true || _theme?.Current.IsLight == true
        ? ArchitecturalMaterials.ClayEdgeColor
        : _theme?.Current.TextPrimary ?? Color.FromArgb(255, 245, 245, 247);

    /// <summary>После поворота камеры засечки перестраиваются в плоскости, обращённой к наблюдателю.</summary>
    private void RefreshDimensionOrientation()
    {
        if (_scene is not { PinnedDimensions.Count: > 0 } || _viewport?.Camera is not ProjectionCamera camera) return;
        var look = Vector3.Normalize(camera.LookDirection);
        if (Vector3.Dot(look, _measureLook) < 0.995f) RebuildDimensions();
    }

    /// <summary>Закреплённые размеры: размерная линия с выступом, засечки 45°, точки концов и подпись-билборд значения в мм.</summary>
    private void RebuildDimensions()
    {
        _measureGroup.Children.Clear();
        if (_viewport is null || _scene is null || _theme is null) return;
        _measureLook = _viewport.Camera is ProjectionCamera camera ? Vector3.Normalize(camera.LookDirection) : new Vector3(0, -1, 0);
        if (_scene.PinnedDimensions.Count == 0) return;

        var color = DimensionColor;
        var accent = _theme.Current.Selection;
        var normal = new BimLineSet();
        var selected = new BimLineSet();
        bool anyNormal = false, anySelected = false;
        foreach (var d in _scene.PinnedDimensions)
        {
            bool isSelected = d.Id == _measureSelectedId;
            var target = isSelected ? selected : normal;
            if (isSelected) anySelected = true;
            else anyNormal = true;
            var labelAt = AddDimensionStrokes(target, V(d.A), V(d.B));
            var text = isSelected ? ToColor4(_theme.Current.OnAccent) : ToColor4(_theme.Current.IsLight || _vm?.IsClayMode == true ? Color.FromArgb(255, 0x1C, 0x1C, 0x1E) : _theme.Current.TextPrimary);
            var background = isSelected ? ToColor4(accent) : WithAlpha(ToColor4(_theme.Current.IsLight || _vm?.IsClayMode == true ? Color.FromArgb(255, 255, 255, 255) : _theme.Current.Card), 0.86f);
            AddDimensionLabel(_measureGroup, MeasurementService.LabelOf(d), labelAt, text, background);
        }
        if (anyNormal) _measureGroup.Children.Add(DimensionLines(normal, color, 1.4));
        if (anySelected) _measureGroup.Children.Add(DimensionLines(selected, accent, 2.0));
    }

    /// <summary>Живой обмер: акцентная линия P1 → курсор (или P2) с засечками и маркеры точек.</summary>
    private void RebuildMeasureLive()
    {
        _measureLiveGroup.Children.Clear();
        if (_vm?.IsMeasureMode != true || _theme is null) return;
        var accent = _theme.Current.Selection;
        var markers = new MeshBuilder();
        bool anyMarker = false;
        void Marker(Vector3 p)
        {
            markers.AddSphere(p, Math.Clamp(WorldPerPixel(p) * 4.5f, 0.015f, 0.6f), 12, 12);
            anyMarker = true;
        }
        if (_measureA is { } a)
        {
            Marker(a);
            var end = _measureB ?? _measureCursor;
            if (end is { } b && (b - a).Length() > 1e-4f)
            {
                var lines = new BimLineSet();
                AddDimensionStrokes(lines, a, b);
                _measureLiveGroup.Children.Add(DimensionLines(lines, accent, _measureB is null ? 1.6 : 2.0));
                Marker(b);
            }
        }
        else if (_measureCursor is { } c && _measureSelectedId is null) Marker(c);
        if (anyMarker)
            _measureLiveGroup.Children.Add(new MeshGeometryModel3D
            {
                Geometry = markers.ToMeshGeometry3D(),
                Material = HandleMaterial(ToColor4(accent)),
                CullMode = CullMode.None,
                IsHitTestVisible = false
            });
    }

    /// <summary>Отрезки размера a–b (линия с выступом и засечки — геометрия ядра); возвращает точку подписи у середины.</summary>
    private Vector3 AddDimensionStrokes(BimLineSet lines, Vector3 a, Vector3 b) =>
        MeasurementService.AddDimensionStrokes(lines, a.ToBim(), b.ToBim(), _measureLook.ToBim(), WorldPerPixel((a + b) / 2)).ToDx();

    private static LineGeometryModel3D DimensionLines(BimLineSet lines, Color color, double thickness) => new()
    {
        Geometry = lines.ToLineGeometry(),
        Color = color,
        Thickness = thickness,
        Smoothness = 0.5,
        IsHitTestVisible = false
    };
    private static void AddDimensionLabel(GroupElement3D group, string text, Vector3 at, Color4 foreground, Color4 background)
    {
        var label = new BillboardSingleText3D
        {
            TextInfo = new TextInfo(text, at),
            FontColor = foreground,
            BackgroundColor = background,
            FontSize = 13,
            FontFamily = "Segoe UI",
            FontWeight = SharpDX.DirectWrite.FontWeight.SemiBold,
            Padding = new HelixToolkit.SharpDX.Core.Model.Scene2D.Thickness(7, 3, 7, 3)
        };
        group.Children.Add(new BillboardTextModel3D { Geometry = label, FixedSize = true, IsHitTestVisible = false });
    }

    private static Color4 WithAlpha(Color4 c, float alpha) => new(c.Red, c.Green, c.Blue, alpha);

    // ───────────────────────────── Стеклянная капсула ─────────────────────────────

    /// <summary>Содержимое капсулы: живой обмер, зафиксированный размер (кнопка «Закрепить») или выбранный закреплённый размер.</summary>
    private void UpdateMeasureCapsule()
    {
        if (_vm is null || _scene is null)
        {
            MeasureCapsule.Visibility = Visibility.Collapsed;
            return;
        }
        var loc = _vm.Loc;
        TapeMeasurement? m = null;
        string title;
        bool fixedMeasure = false, pinned = false;
        if (_vm.IsMeasureMode && _measureSelectedId is { } id && _scene.FindDimension(id) is { } d)
        {
            m = MeasurementService.Measure(d);
            title = loc.Get("MeasurePinnedTitle");
            pinned = true;
        }
        else if (_vm.IsMeasureMode && _measureA is { } a && (_measureB ?? _measureCursor) is { } b && (b - a).Length() > 1e-4f)
        {
            m = MeasurementService.Measure(T(a), T(b));
            fixedMeasure = _measureB is not null;
            title = loc.Get(fixedMeasure ? "MeasureFixed" : "MeasureLive");
        }
        else title = string.Empty;

        if (m is null)
        {
            MeasureCapsule.Visibility = Visibility.Collapsed;
            UpdateOverlayTracking();
            return;
        }
        var (length, projections, slope) = MeasurementService.CapsuleLines(m, loc.Get("Mm"), !loc.IsRussian);
        MeasureTitle.Text = title;
        MeasureLength.Text = length;
        MeasureProjections.Text = projections;
        MeasureSlope.Text = slope ?? string.Empty;
        MeasureSlope.Visibility = slope is null ? Visibility.Collapsed : Visibility.Visible;
        MeasurePinText.Text = loc.Get("MeasurePin");
        MeasureRemoveText.Text = loc.Get("MeasureRemove");
        MeasureNewText.Text = loc.Get("MeasureNew");
        MeasurePinButton.Visibility = fixedMeasure ? Visibility.Visible : Visibility.Collapsed;
        MeasureRemoveButton.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        MeasureNewButton.Visibility = fixedMeasure || pinned ? Visibility.Visible : Visibility.Collapsed;
        MeasureActions.Visibility = fixedMeasure || pinned ? Visibility.Visible : Visibility.Collapsed;
        // Во время ведения линии капсула не перехватывает клики; кнопки активны только после фиксации.
        MeasureCapsule.IsHitTestVisible = fixedMeasure || pinned;
        MeasureCapsule.Visibility = Visibility.Visible;
        UpdateOverlayTracking();
        UpdateMeasureCapsulePosition();
    }

    /// <summary>Капсула у середины размерной линии (справа-сверху), в пределах вьюпорта.</summary>
    private void UpdateMeasureCapsulePosition()
    {
        if (MeasureCapsule.Visibility != Visibility.Visible || _scene is null) return;
        Vector3? anchor = null;
        if (_measureSelectedId is { } id && _scene.FindDimension(id) is { } d) anchor = (V(d.A) + V(d.B)) / 2;
        else if (_measureA is { } a && (_measureB ?? _measureCursor) is { } b) anchor = (a + b) / 2;
        if (anchor is not { } p3 || !TryProject(p3, out var p))
        {
            MeasureCapsule.Opacity = 0;
            return;
        }
        MeasureCapsule.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = MeasureCapsule.DesiredSize.Width, h = MeasureCapsule.DesiredSize.Height;
        double x = Math.Clamp(p.X + 18, 8, Math.Max(8, ActualWidth - w - 8));
        double y = Math.Clamp(p.Y - h - 18, 58, Math.Max(58, ActualHeight - h - 8));
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(MeasureCapsule, Math.Round(x));
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(MeasureCapsule, Math.Round(y));
        MeasureCapsule.Opacity = 1;
    }

    private void OnMeasurePinClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _measureA is not { } a || _measureB is not { } b) return;
        _vm.PinDimension(T(a), T(b));
        ResetMeasurement();
        _measureCursor = null;
        RebuildMeasureLive();
        UpdateMeasureCapsule();
    }

    private void OnMeasureRemoveClick(object sender, RoutedEventArgs e) => HandleMeasureDelete();

    private void OnMeasureNewClick(object sender, RoutedEventArgs e)
    {
        ResetMeasurement();
        _measureCursor = null;
        RebuildMeasureLive();
        UpdateMeasureCapsule();
    }

    // ───────────────────────────── Рендер 4K PNG ─────────────────────────────

    /// <summary>
    /// Рендер текущего вида в буфер width × height (по умолчанию 3840 × 2160) без интерфейса: Gizmo, ручки, выделение,
    /// привязки, живой обмер, Section Box, ViewCube и индикатор осей скрываются, толщина линий масштабируется под кадр.
    /// Прозрачный фон — разностное извлечение альфы по двум кадрам (на чёрном и белом фоне): полупрозрачные стёкла и
    /// тени сохраняют Alpha, пустое небо — Alpha = 0; сетка участка в прозрачном кадре не рисуется.
    /// </summary>
    public async Task<HighResRenderResult> RenderHighResolutionAsync(string path, int width, int height, bool transparent)
    {
        var sw = Stopwatch.StartNew();
        var pixels = await RenderPixelsAsync(width, height, transparent);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        await using (var file = File.Create(path))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        sw.Stop();
        return new HighResRenderResult(path, width, height, transparent, new FileInfo(path).Length, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Авто-снимок вида для запроса проектировщику: тот же чистый кадр, что у рендера 4K (без Gizmo, привязок, ViewCube),
    /// с пропорциями вьюпорта и длинной стороной <paramref name="longSide"/> px. null — вьюпорт ещё не готов.
    /// </summary>
    public async Task<(byte[] Bgra, int Width, int Height)?> CaptureViewSnapshotAsync(int longSide)
    {
        if (_viewport?.RenderHost is not { } host || !host.IsRendering || _highResRendering) return null;
        double w0 = Math.Max(1, host.ActualWidth), h0 = Math.Max(1, host.ActualHeight);
        double k = longSide / Math.Max(w0, h0);
        int width = Math.Max(64, (int)Math.Round(w0 * k)), height = Math.Max(64, (int)Math.Round(h0 * k));
        return (await RenderPixelsAsync(width, height, transparent: false), width, height);
    }

    private async Task<byte[]> RenderPixelsAsync(int width, int height, bool transparent)
    {
        if (_viewport?.RenderHost is not { } host || !host.IsRendering) throw new InvalidOperationException("3D-вьюпорт ещё не готов к рендеру.");
        int oldW = Math.Max(1, (int)Math.Round(host.ActualWidth)), oldH = Math.Max(1, (int)Math.Round(host.ActualHeight));
        float lineScale = (float)Math.Clamp(Math.Min(width / (double)oldW, height / (double)oldH), 1, 4);

        var hidden = new List<(Element3D Model, Visibility Visibility)>();
        void Hide(Element3D model)
        {
            hidden.Add((model, model.Visibility));
            model.Visibility = Visibility.Collapsed;
        }
        foreach (var g in new Element3D[] { _overlayGroup, _snapGroup, _sectionGroup, _measureLiveGroup, _clashGroup }) Hide(g);
        // Пока идёт рендер (между кадрами есть await), тени не пересобираются — иначе плоскость участка вернётся в кадр.
        _highResRendering = true;
        if (transparent)
        {
            Hide(_gridMinor);
            Hide(_gridMajor);
            if (_siteGroundModel is not null) _shadowGroup.Children.Remove(_siteGroundModel);
        }
        var thick = new List<(LineGeometryModel3D Model, double Thickness)>();
        foreach (var group in new GroupElement3D[] { _elementsGroup, _axesGroup, _measureGroup, _stressGroup, _underlayGroup })
            foreach (var line in group.Children.OfType<LineGeometryModel3D>())
            {
                thick.Add((line, line.Thickness));
                line.Thickness *= lineScale;
            }
        bool cube = _viewport.ShowViewCube, coordinates = _viewport.ShowCoordinateSystem;
        var background = _viewport.BackgroundColor;
        _viewport.ShowViewCube = false;
        _viewport.ShowCoordinateSystem = false;
        SnapBadge.Visibility = Visibility.Collapsed;

        byte[] pixels;
        try
        {
            host.Resize(width, height);
            if (!transparent) pixels = await CaptureAsync(width, height);
            else
            {
                _viewport.BackgroundColor = Color.FromArgb(255, 0, 0, 0);
                var black = await CaptureAsync(width, height);
                _viewport.BackgroundColor = Color.FromArgb(255, 255, 255, 255);
                var white = await CaptureAsync(width, height);
                // 8,3 млн пикселей — разностная альфа в пуле потоков, UI-поток не замирает.
                pixels = await Task.Run(() => ExtractAlpha(black, white));
            }
        }
        finally
        {
            _viewport.BackgroundColor = background;
            host.Resize(oldW, oldH);
            foreach (var (model, thickness) in thick) model.Thickness = thickness;
            foreach (var (model, visibility) in hidden) model.Visibility = visibility;
            _viewport.ShowViewCube = cube;
            _viewport.ShowCoordinateSystem = coordinates;
            _highResRendering = false;
            if (transparent) RebuildShadows();
            host.UpdateAndRender();
        }
        return pixels;
    }

    /// <summary>
    /// Синхронный кадр Helix в увеличенный буфер и чтение пикселей BGRA8 (straight alpha). Цепочка обмена SwapChainPanel —
    /// flip-модель: после Present текущий задний буфер хранит один из предыдущих кадров, поэтому то же состояние сцены
    /// сначала рисуется дважды (первый кадр после Resize пуст, смена фона применяется со сдвигом), затем захватывается.
    /// </summary>
    private async Task<byte[]> CaptureAsync(int width, int height)
    {
        var host = _viewport!.RenderHost;
        for (int i = 0; i < 2; i++)
        {
            host.InvalidateRender();
            host.UpdateAndRender();
        }
        host.InvalidateRender();
        using var stream = _viewport.RenderToBitmapStream() ?? throw new InvalidOperationException("Helix не вернул кадр рендера.");
        stream.Position = 0;
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        if (decoder.PixelWidth != width || decoder.PixelHeight != height)
        {
            var transform = new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height, InterpolationMode = BitmapInterpolationMode.Fant };
            var scaled = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return ForceOpaque(scaled.DetachPixelData());
        }
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return ForceOpaque(data.DetachPixelData());
    }

    /// <summary>Кадр с фоном неба — непрозрачный (альфа буфера цепочки обмена не несёт смысла).</summary>
    private static byte[] ForceOpaque(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return bgra;
    }

    /// <summary>
    /// Альфа по двум кадрам: на чёрном фоне C_b = α·C, на белом C_w = α·C + (1 − α)·255 ⇒ α = 1 − (C_w − C_b) / 255,
    /// цвет C = C_b / α (среднее по трём каналам — устойчиво к сглаживанию FXAA).
    /// </summary>
    public static byte[] ExtractAlpha(byte[] black, byte[] white)
    {
        var result = new byte[black.Length];
        for (int i = 0; i + 3 < black.Length; i += 4)
        {
            double diff = (Math.Max(0, white[i] - black[i]) + Math.Max(0, white[i + 1] - black[i + 1]) + Math.Max(0, white[i + 2] - black[i + 2])) / 3.0;
            double alpha = Math.Clamp(1 - diff / 255.0, 0, 1);
            if (alpha < 1.5 / 255)
            {
                result[i] = result[i + 1] = result[i + 2] = result[i + 3] = 0;
                continue;
            }
            for (int c = 0; c < 3; c++) result[i + c] = (byte)Math.Clamp(Math.Round(black[i + c] / alpha), 0, 255);
            result[i + 3] = (byte)Math.Round(alpha * 255);
        }
        return result;
    }

#if DEBUG
    /// <summary>
    /// Только Debug (AIA_DEBUG_MEASURE): режим рулетки и обмер a → b тем же путём, что и клики; pin — закрепить размер
    /// и выбрать его (капсула закреплённого размера), иначе капсула остаётся с кнопкой «Закрепить размер на чертеже».
    /// </summary>
    public void DebugMeasure(Vector3 a, Vector3 b, bool pin, bool select, bool snap = false)
    {
        if (_vm is null) return;
        if (!_vm.IsMeasureMode) _vm.IsMeasureMode = true;
        if (snap)
        {
            // Путь клика: экранная точка → хит-тест поверхности → привязка OSnap (как HandleMeasureClick).
            SnapHit? last = null;
            if (TryProject(a, out var sa) && MeasurePointAt(sa) is { } ha) a = ha.Point;
            if (TryProject(b, out var sb) && MeasurePointAt(sb) is { } hb) { b = hb.Point; last = hb.Snap; }
            ShowSnap(last);
        }
        _measureA = a;
        _measureB = b;
        _measureCursor = b;
        if (pin)
        {
            var d = _vm.PinDimension(T(a), T(b));
            _measureA = null;
            _measureB = null;
            _measureSelectedId = select ? d?.Id : null;
            _vm.MeasureHintText = _vm.Loc.Get("MeasureHintStart");
        }
        else _vm.MeasureHintText = _vm.Loc.Get("MeasureFixed");
        RebuildDimensions();
        RebuildMeasureLive();
        UpdateMeasureCapsule();
    }
#endif
}
