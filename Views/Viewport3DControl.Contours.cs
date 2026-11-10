using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.WinUI;
using Microsoft.UI.Xaml;
using SharpDX;
using Windows.System;
using CullMode = SharpDX.Direct3D11.CullMode;
using ProjectionCamera = HelixToolkit.WinUI.ProjectionCamera;

namespace AIArchitectStudio.Views;

/// <summary>
/// Редактирование вершин контура Polygon2D во вьюпорте (Shapr3D): белые пины в вершинах полигональной плиты, фундамента,
/// террасы или плоской кровли и акцентные маркеры «+» в серединах рёбер. Перетаскивание вершины — в горизонтальной плоскости
/// верха элемента с магнитной привязкой OSnap к узлам и линиям осей, граням и краям (Alt — шаг 10 мм), протягивание маркера
/// ребра вставляет новую вершину, Delete или правая кнопка на пине удаляют вершину. Каждое действие — один шаг Undo.
/// </summary>
public sealed partial class Viewport3DControl
{
    private readonly List<(int Index, Vector3 Center, float Radius, PhongMaterial Material)> _vertexHandles = [];
    private readonly List<(int Edge, Vector3 Center, float Radius, PhongMaterial Material)> _edgeHandles = [];
    private int? _hoverVertex, _hoverEdge;

    // Перетаскивание вершины.
    private ArchitecturalElement? _vertexElement;
    private int _vertexIndex = -1;
    private float _vertexPlaneY;
    private (double X, double Z) _vertexStart;
    private Vector3 _vertexStartHit;

    /// <summary>Вершина, к которой относятся Delete и капсула координат: перетаскиваемая, иначе наведённая.</summary>
    private int? ActiveVertex => _vertexElement is not null && _vertexIndex >= 0 ? _vertexIndex : _hoverVertex;

    /// <summary>Пины вершин и маркеры середин рёбер выбранного полигонального элемента (только при одиночном выделении).</summary>
    private void AddVertexHandles(ArchitecturalElement sel)
    {
        _vertexHandles.Clear();
        _edgeHandles.Clear();
        if (_walkActive || _theme is null || !SceneStateService.IsContourEditable(sel)) return;
        var vertices = SceneStateService.WorldVertices(sel);
        float y = (float)sel.TopY;
        var accent = ToColor4(_theme.Current.Selection);
        for (int i = 0; i < vertices.Count; i++)
        {
            var center = new Vector3((float)vertices[i].X, y, (float)vertices[i].Z);
            float radius = Math.Clamp(WorldPerPixel(center) * 7f, 0.05f, 1.2f);
            center.Y += radius * 0.4f;
            var material = HandleMaterial(new Color4(0.97f, 0.97f, 0.98f, 1f));
            var builder = new MeshBuilder();
            builder.AddSphere(center, radius, 16, 16);
            _overlayGroup.Children.Add(new MeshGeometryModel3D { Geometry = builder.ToMeshGeometry3D(), Material = material, CullMode = CullMode.None, IsHitTestVisible = false });
            _vertexHandles.Add((i, center, radius, material));

            // Маркер «+» в середине ребра i → i + 1 (протянуть — новая вершина).
            var next = vertices[(i + 1) % vertices.Count];
            var mid = new Vector3((float)((vertices[i].X + next.X) / 2), y, (float)((vertices[i].Z + next.Z) / 2));
            float r = Math.Clamp(WorldPerPixel(mid) * 4.5f, 0.035f, 0.8f);
            mid.Y += r * 0.4f;
            var midMaterial = HandleMaterial(accent);
            var midBuilder = new MeshBuilder();
            midBuilder.AddBox(mid, r * 2.6f, r * 0.7f, r * 0.7f);
            midBuilder.AddBox(mid, r * 0.7f, r * 0.7f, r * 2.6f);
            _overlayGroup.Children.Add(new MeshGeometryModel3D { Geometry = midBuilder.ToMeshGeometry3D(), Material = midMaterial, CullMode = CullMode.None, IsHitTestVisible = false });
            _edgeHandles.Add((i, mid, r * 1.3f, midMaterial));
        }
        RefreshVertexColors();
    }

    /// <summary>Наведённая или перетаскиваемая вершина и наведённый маркер ребра — оранжевым.</summary>
    private void RefreshVertexColors()
    {
        if (_theme is null) return;
        var orange = new Color4(1.0f, 0.62f, 0.04f, 1f);
        var white = new Color4(0.97f, 0.97f, 0.98f, 1f);
        var accent = ToColor4(_theme.Current.Selection);
        foreach (var (index, _, _, material) in _vertexHandles) Paint(material, index == ActiveVertex ? orange : white);
        foreach (var (edge, _, _, material) in _edgeHandles) Paint(material, edge == _hoverEdge ? orange : accent);

        static void Paint(PhongMaterial m, Color4 c)
        {
            m.DiffuseColor = c;
            m.AmbientColor = c;
            m.EmissiveColor = new Color4(c.Red * 0.55f, c.Green * 0.55f, c.Blue * 0.55f, 1f);
        }
    }

    /// <summary>Пин вершины под курсором (экранный радиус + 4 px; ближайший к камере).</summary>
    private int? VertexAt(Windows.Foundation.Point position) => PickScreen(_vertexHandles.Select(h => (h.Index, h.Center, h.Radius)), position, 4);

    /// <summary>Маркер середины ребра под курсором.</summary>
    private int? EdgeAt(Windows.Foundation.Point position) => PickScreen(_edgeHandles.Select(h => (h.Edge, h.Center, h.Radius)), position, 3);

    private int? PickScreen(IEnumerable<(int Id, Vector3 Center, float Radius)> handles, Windows.Foundation.Point position, double extra)
    {
        if (_viewport?.Camera is not ProjectionCamera camera) return null;
        var right = Vector3.Normalize(Vector3.Cross(camera.LookDirection, camera.UpDirection));
        int? best = null;
        double bestDepth = double.MaxValue;
        foreach (var (id, center, radius) in handles)
        {
            if (!TryProject(center, out var c) || !TryProject(center + right * radius, out var edge)) continue;
            if (Distance(c, position) > Math.Max(HandlePickPixels, Distance(c, edge)) + extra) continue;
            double depth = Vector3.Dot(center - camera.Position, Vector3.Normalize(camera.LookDirection));
            if (depth < bestDepth)
            {
                bestDepth = depth;
                best = id;
            }
        }
        return best;
    }

    /// <summary>Нажатие на пин вершины (перетаскивание) или маркер ребра (новая вершина + перетаскивание). true — обработано.</summary>
    private bool TryBeginVertexEdit(ArchitecturalElement element, Windows.Foundation.Point position, Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        if (_scene is null || !SceneStateService.IsContourEditable(element)) return false;
        if (VertexAt(position) is { } vertex)
        {
            BeginVertexDrag(element, vertex, position, pointer, undoPushed: false);
            return true;
        }
        if (EdgeAt(position) is { } edge)
        {
            // Вставка вершины и её перетаскивание — один шаг Undo.
            _scene.PushUndo();
            int index = _scene.InsertVertex(element, edge, recordUndo: false);
            if (index < 0) return true;
            _hoverEdge = null;
            BeginVertexDrag(element, index, position, pointer, undoPushed: true);
            return true;
        }
        return false;
    }

    private void BeginVertexDrag(ArchitecturalElement element, int index, Windows.Foundation.Point position, Microsoft.UI.Xaml.Input.Pointer? pointer, bool undoPushed)
    {
        if (_viewport is null) return;
        _vertexPlaneY = (float)element.TopY;
        if (_viewport.UnProjectOnPlane(position, new Vector3(0, _vertexPlaneY, 0), Vector3.UnitY) is not { } hit) return;
        _vertexElement = element;
        _vertexIndex = index;
        _vertexStart = SceneStateService.WorldVertices(element)[index];
        _vertexStartHit = hit;
        _dragUndoPushed = undoPushed;
        _snapTargets = BuildSnapTargets([element]);
        _viewport.IsPanEnabled = false;
        _viewport.IsRotationEnabled = false;
        if (pointer is not null) _viewport.CapturePointer(pointer);
        RefreshVertexColors();
        ShowHud();
    }

    /// <summary>
    /// Новое положение вершины: смещение курсора в плоскости верха элемента, затем OSnap (узлы осей, торцы, углы, линии осей,
    /// грани и края в радиусе 0.25 м); без привязки — шаг 0.1 м, Alt — 0.01 м. Недопустимый контур не применяется.
    /// </summary>
    private void DragVertexTo(Windows.Foundation.Point position)
    {
        if (_viewport is null || _scene is null || _vertexElement is not { } el) return;
        if (_viewport.UnProjectOnPlane(position, new Vector3(0, _vertexPlaneY, 0), Vector3.UnitY) is not { } hit) return;
        double x = _vertexStart.X + (hit.X - _vertexStartHit.X), z = _vertexStart.Z + (hit.Z - _vertexStartHit.Z);
        bool alt = IsKeyDown(VirtualKey.Menu);
        SnapHit? snap = null;
        if (_vm?.IsOSnapEnabled == true && !alt && _snapTargets is { Count: > 0 } targets)
            snap = SmartSnapService.SnapPoint(x, _vertexPlaneY, z, targets);
        if (snap is not null) (x, z) = (snap.Contact.X, snap.Contact.Z);
        else
        {
            double step = alt ? SmartSnapService.FineStep : SnapStep;
            x = SceneStateService.Snap(x, step);
            z = SceneStateService.Snap(z, step);
        }
        ShowSnap(snap);
        var current = SceneStateService.WorldVertices(el)[_vertexIndex];
        if (Math.Abs(current.X - x) > 1e-6 || Math.Abs(current.Z - z) > 1e-6)
        {
            bool pushed = !_dragUndoPushed;
            if (pushed) _scene.PushUndo();
            if (_scene.MoveVertex(el, _vertexIndex, x, z, recordUndo: false)) _dragUndoPushed = true;
            else if (pushed) _scene.Undo();
        }
        ShowHud();
    }

    private void EndVertexDrag(Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        _vertexElement = null;
        _vertexIndex = -1;
        ShowSnap(null);
        RefreshVertexColors();
        ShowHud();
        if (_viewport is null) return;
        if (pointer is not null) _viewport.ReleasePointerCapture(pointer);
        _viewport.IsPanEnabled = true;
        _viewport.IsRotationEnabled = _vm?.ViewMode == ViewModels.ViewportMode.Perspective3D;
    }

    /// <summary>Наведение на пины вершин и маркеры рёбер (подсветка и капсула координат вершины).</summary>
    private bool UpdateVertexHover(Windows.Foundation.Point position)
    {
        int? vertex = _vertexHandles.Count > 0 ? VertexAt(position) : null;
        int? edge = vertex is null && _edgeHandles.Count > 0 ? EdgeAt(position) : null;
        if (vertex == _hoverVertex && edge == _hoverEdge) return vertex is not null || edge is not null;
        _hoverVertex = vertex;
        _hoverEdge = edge;
        RefreshVertexColors();
        ShowHud();
        return vertex is not null || edge is not null;
    }

    /// <summary>Delete (MainWindow) при наведённой или перетаскиваемой вершине: удалить вершину, а не весь элемент.</summary>
    public bool HandleDelete()
    {
        if (HandleSiteDelete()) return true;
        if (_scene?.SelectedElement is not { } el || ActiveVertex is not { } index || !SceneStateService.IsContourEditable(el)) return false;
        if (!_scene.RemoveVertex(el, index))
        {
            _vm?.ShowNotice(_vm.Loc.Get("VertexTitle"), _vm.Loc.Get("VertexMinimum"), "AIA-VERTEX-MIN");
            return true;
        }
        _hoverVertex = null;
        ShowHud();
        return true;
    }

    /// <summary>Правая кнопка на пине вершины — удаление вершины.</summary>
    private bool TryRemoveVertexAt(Windows.Foundation.Point position)
    {
        if (_scene?.SelectedElement is not { } el || _scene.IsMultiSelection || !SceneStateService.IsContourEditable(el) || VertexAt(position) is not { } index) return false;
        _hoverVertex = index;
        return HandleDelete();
    }

    /// <summary>Капсула координат вершины: «Вершина 3: X 1 200 · Z −3 400 мм»; true — капсула показана.</summary>
    private bool ShowVertexHud(ArchitecturalElement el)
    {
        if (_vm is null || ActiveVertex is null && _hoverEdge is null) return false;
        var loc = _vm.Loc;
        string mm = loc.Get("Mm");
        var vertices = SceneStateService.WorldVertices(el);
        if (ActiveVertex is { } v && v < vertices.Count)
        {
            var (x, z) = vertices[v];
            HudGlyph.Glyph = "";
            HudLabel.Text = loc.Format("VertexN", v + 1) + ":";
            HudValue.Text = $"X {PushPullService.Millimeters(x, unit: string.Empty).TrimEnd()} · Z {PushPullService.Millimeters(z, unit: mm)}";
            double dx = x - _vertexStart.X, dz = z - _vertexStart.Z;
            HudDelta.Text = _vertexElement is not null && Math.Abs(dx) + Math.Abs(dz) > 5e-4
                ? $"(Δ {PushPullService.Millimeters(Math.Sqrt(dx * dx + dz * dz), unit: mm)})"
                : string.Empty;
            HudHint.Text = _vertexElement is not null ? (_vm.IsOSnapEnabled ? "OSnap · Alt — 10 " + mm : string.Empty) : loc.Get("VertexHint");
        }
        else
        {
            HudGlyph.Glyph = "";
            HudLabel.Text = loc.Get("VertexInsert");
            HudValue.Text = loc.Format("VertexCount", vertices.Count);
            HudDelta.Text = string.Empty;
            HudHint.Text = loc.Get("VertexInsertHint");
        }
        HudDelta.Visibility = HudDelta.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HudValueBox.Background = null;
        HudValueBox.Padding = new Thickness(0);
        HudHint.Visibility = HudHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HudBorder.Visibility = Visibility.Visible;
        UpdateOverlayTracking();
        UpdateOverlayPositions();
        return true;
    }

    /// <summary>Точка привязки капсулы — активная вершина или наведённый маркер ребра.</summary>
    private Vector3? VertexHudAnchor()
    {
        if (ActiveVertex is { } v && _vertexHandles.FirstOrDefault(h => h.Index == v) is { Radius: > 0 } vh) return vh.Center;
        if (_hoverEdge is { } e && _edgeHandles.FirstOrDefault(h => h.Edge == e) is { Radius: > 0 } eh) return eh.Center;
        return null;
    }

#if DEBUG
    /// <summary>
    /// Только Debug (AIA_DEBUG_VERTEX=имя|индекс|x,z): перетаскивание вершины контура тем же путём, что мышь (OSnap к осям),
    /// капсула координат и направляющая привязки остаются на экране.
    /// </summary>
    public void DebugDragVertex(ArchitecturalElement element, int index, double x, double z)
    {
        if (_scene is null || !SceneStateService.IsContourEditable(element)) return;
        _scene.SelectedElement = element;
        UpdateSelectionVisuals();
        index = Math.Clamp(index, 0, element.Polygon2D.Count - 1);
        _vertexElement = element;
        _vertexIndex = index;
        _vertexPlaneY = (float)element.TopY;
        _vertexStart = SceneStateService.WorldVertices(element)[index];
        _vertexStartHit = new Vector3((float)_vertexStart.X, _vertexPlaneY, (float)_vertexStart.Z);
        _dragUndoPushed = false;
        _snapTargets = BuildSnapTargets([element]);
        var target = new Vector3((float)x, _vertexPlaneY, (float)z);
        if (TryProject(target, out var screen)) DragVertexTo(screen);
        var snap = _snapHit;
        _vertexElement = null;
        _hoverVertex = index;
        _vertexIndex = -1;
        ShowSnap(snap);
        RefreshVertexColors();
        ShowHud();
    }
#endif
}
