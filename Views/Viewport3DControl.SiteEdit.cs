using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.Gpzu;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.WinUI;
using Microsoft.UI.Xaml;
using SharpDX;
using Windows.System;
using Color = Windows.UI.Color;
using CullMode = SharpDX.Direct3D11.CullMode;

namespace AIArchitectStudio.Views;

/// <summary>
/// Волна 6 — участок как живая земля во вьюпорте. Платформа участка: рельеф по отметкам (проектная поверхность с площадками
/// зданий и откосами 1 : 2), прилегающая территория (тротуары и проезды по периметру, 25 м), грунтовое основание толщиной
/// SoilThickness с полупрозрачными стенками и подземные сети ИГДИ в толще грунта (трубы на глубине заложения по виду сети).
/// В Среде 1 поворотные точки 1…N и вершины зон — интерактивные пины: перетаскивание (шаг 0,05 м, Alt — 0,01 м), «+» в середине
/// стороны вставляет точку, Delete / правая кнопка удаляют; каждое действие — один шаг журнала (MainViewModel.MoveSiteVertex…).
/// </summary>
public sealed partial class Viewport3DControl
{
    private SiteTerrain? _siteTerrain;
    private readonly List<(SiteVertexRef Ref, Vector3 Center, float Radius, bool IsEdge, PhongMaterial Material)> _siteHandles = [];
    private readonly GroupModel3D _sitePreviewGroup = new();
    private SiteVertexRef? _siteDrag, _siteHover;
    private bool _siteHoverEdge;
    private float _siteDragPlaneY;
    private SitePoint _siteDragStart, _siteDragCurrent;
    private Vector3 _siteDragStartHit;
    private Microsoft.UI.Xaml.Input.Pointer? _siteDragPointer;

    /// <summary>Глубина заложения сетей по виду (м от поверхности до оси).</summary>
    private static double NetworkDepth(GpzuZoneKind kind) => kind switch
    {
        GpzuZoneKind.GasPipeline => 1.2,
        GpzuZoneKind.HeatNetwork => 1.5,
        GpzuZoneKind.PowerCable => 0.7,
        GpzuZoneKind.WaterSupply => 2.2,
        GpzuZoneKind.Sewer => 2.6,
        _ => 0.8
    };

    private double TerrainH(double x, double z) => _siteTerrain?.HeightAt(x, z) ?? 0;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _siteTerrainTimer;

    /// <summary>Площадки зданий изменились (перенос, новое строение): платформа перестраивается через 0,4 с после последней правки.</summary>
    private void QueueSiteTerrainRebuild()
    {
        if (_siteDrag is not null) return;
        _siteTerrainTimer ??= DispatcherQueue.CreateTimer();
        _siteTerrainTimer.Interval = TimeSpan.FromMilliseconds(400);
        _siteTerrainTimer.IsRepeating = false;
        _siteTerrainTimer.Tick -= OnSiteTerrainTick;
        _siteTerrainTimer.Tick += OnSiteTerrainTick;
        _siteTerrainTimer.Stop();
        _siteTerrainTimer.Start();
    }

    private void OnSiteTerrainTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => QueueSiteRebuild();

    // ═════════════════════════════ Платформа и грунт ═════════════════════════════

    private void AddTerrainPlatform(GpzuSitePlan plan, SiteTerrain terrain)
    {
        if (_theme is null) return;
        var (minX, maxX, minZ, maxZ) = plan.Bounds;
        const double apron = 25;
        double x0 = minX - apron, x1 = maxX + apron, z0 = minZ - apron, z1 = maxZ + apron;
        double step = Math.Clamp(Math.Max(x1 - x0, z1 - z0) / 90, 1.0, 4.0);
        int nx = Math.Max(2, (int)Math.Ceiling((x1 - x0) / step)), nz = Math.Max(2, (int)Math.Ceiling((z1 - z0) / step));
        var heights = new double[nx + 1, nz + 1];
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
                heights[i, j] = terrain.HeightAt(x0 + i * step, z0 + j * step) - 0.02;
        bool light = _theme.Current.IsLight;
        var inside = new MeshBuilderParts();
        var outside = new MeshBuilderParts();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                double cx = x0 + (i + 0.5) * step, cz = z0 + (j + 0.5) * step;
                var target = SiteBoundaryBuilder.Contains(plan.Boundary, cx, cz) ? inside : outside;
                var a = new Vector3((float)(x0 + i * step), (float)heights[i, j], (float)(z0 + j * step));
                var b = new Vector3((float)(x0 + (i + 1) * step), (float)heights[i + 1, j], (float)(z0 + j * step));
                var c = new Vector3((float)(x0 + (i + 1) * step), (float)heights[i + 1, j + 1], (float)(z0 + (j + 1) * step));
                var d = new Vector3((float)(x0 + i * step), (float)heights[i, j + 1], (float)(z0 + (j + 1) * step));
                target.Quad(a, d, c, b);
            }
        // Земля участка — травяно-песчаный тон, прилегающая территория (тротуары, проезды) — асфальт.
        AddSurface(inside, light ? Color.FromArgb(255, 196, 214, 168) : Color.FromArgb(255, 74, 92, 64), 1f);
        AddSurface(outside, light ? Color.FromArgb(255, 206, 208, 212) : Color.FromArgb(255, 58, 60, 66), 1f);

        // Грунтовое основание: стенки по периметру платформы до −SoilThickness и дно (полупрозрачно — видны трубы сетей).
        double bottom = -Math.Max(1.0, plan.SoilThickness);
        var soil = new MeshBuilderParts();
        void Wall(double ax, double az, double bx, double bz, double ha, double hb)
        {
            soil.Quad(new Vector3((float)ax, (float)bottom, (float)az), new Vector3((float)bx, (float)bottom, (float)bz),
                new Vector3((float)bx, (float)hb, (float)bz), new Vector3((float)ax, (float)ha, (float)az));
        }
        for (int i = 0; i < nx; i++)
        {
            Wall(x0 + i * step, z0, x0 + (i + 1) * step, z0, heights[i, 0], heights[i + 1, 0]);
            Wall(x0 + (i + 1) * step, z1, x0 + i * step, z1, heights[i + 1, nz], heights[i, nz]);
        }
        for (int j = 0; j < nz; j++)
        {
            Wall(x0, z0 + (j + 1) * step, x0, z0 + j * step, heights[0, j + 1], heights[0, j]);
            Wall(x1, z0 + j * step, x1, z0 + (j + 1) * step, heights[nx, j], heights[nx, j + 1]);
        }
        soil.Quad(new Vector3((float)x0, (float)bottom, (float)z0), new Vector3((float)x1, (float)bottom, (float)z0),
            new Vector3((float)x1, (float)bottom, (float)z1), new Vector3((float)x0, (float)bottom, (float)z1));
        AddSurface(soil, light ? Color.FromArgb(255, 150, 112, 78) : Color.FromArgb(255, 110, 80, 56), 0.55f);

        // Кромка платформы — тонкая линия по верху стенок.
        var edge = new LineBuilder();
        for (int i = 0; i < nx; i++)
        {
            edge.AddLine(new Vector3((float)(x0 + i * step), (float)heights[i, 0], (float)z0), new Vector3((float)(x0 + (i + 1) * step), (float)heights[i + 1, 0], (float)z0));
            edge.AddLine(new Vector3((float)(x0 + i * step), (float)heights[i, nz], (float)z1), new Vector3((float)(x0 + (i + 1) * step), (float)heights[i + 1, nz], (float)z1));
        }
        for (int j = 0; j < nz; j++)
        {
            edge.AddLine(new Vector3((float)x0, (float)heights[0, j], (float)(z0 + j * step)), new Vector3((float)x0, (float)heights[0, j + 1], (float)(z0 + (j + 1) * step)));
            edge.AddLine(new Vector3((float)x1, (float)heights[nx, j], (float)(z0 + j * step)), new Vector3((float)x1, (float)heights[nx, j + 1], (float)(z0 + (j + 1) * step)));
        }
        _siteGroup.Children.Add(new LineGeometryModel3D { Geometry = edge.ToLineGeometry3D(), Color = light ? Color.FromArgb(255, 120, 96, 72) : Color.FromArgb(255, 160, 130, 100), Thickness = 1.0, IsHitTestVisible = false });
    }

    /// <summary>Подземные сети топосъёмки: трубы по осям трасс на глубине заложения вида сети, в цвете коридора.</summary>
    private void AddUnderground(GpzuSitePlan plan, SiteTerrain terrain)
    {
        foreach (var zone in plan.Zones.Where(z => z.Zone.IsNetwork && z.Lines.Count > 0))
        {
            if (_vm?.IsSiteLayerVisible(zone.Id) == false) continue;
            double depth = NetworkDepth(zone.Zone.Kind);
            double diameter = zone.Zone.Kind is GpzuZoneKind.PowerCable or GpzuZoneKind.Communication ? 0.12 : zone.Zone.Kind == GpzuZoneKind.Sewer ? 0.45 : 0.32;
            var mb = new MeshBuilder();
            foreach (var line in zone.Lines)
            {
                if (line.Count < 2) continue;
                // Сгущение трассы шагом 2 м — труба повторяет рельеф.
                var path = new List<Vector3>();
                for (int i = 1; i < line.Count; i++)
                {
                    var a = line[i - 1];
                    var b = line[i];
                    double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
                    int n = Math.Max(1, (int)Math.Ceiling(len / 2));
                    for (int k = i == 1 ? 0 : 1; k <= n; k++)
                    {
                        double x = a.X + (b.X - a.X) * k / n, z = a.Z + (b.Z - a.Z) * k / n;
                        path.Add(new Vector3((float)x, (float)(terrain.HeightAt(x, z) - depth), (float)z));
                    }
                }
                if (path.Count >= 2) mb.AddTube(path, (float)diameter, 10, false);
            }
            var color = Hex(zone.Zone.ColorHex);
            _siteGroup.Children.Add(new MeshGeometryModel3D
            {
                Geometry = mb.ToMeshGeometry3D(),
                Material = new PhongMaterial
                {
                    DiffuseColor = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, 1),
                    AmbientColor = new Color4(color.R / 255f * 0.5f, color.G / 255f * 0.5f, color.B / 255f * 0.5f, 1),
                    EmissiveColor = new Color4(color.R / 255f * 0.45f, color.G / 255f * 0.45f, color.B / 255f * 0.45f, 1),
                    SpecularColor = new Color4(0.3f, 0.3f, 0.3f, 1),
                    SpecularShininess = 40
                },
                CullMode = CullMode.None,
                IsHitTestVisible = false,
                IsThrowingShadow = false
            });
        }
    }

    /// <summary>Накопитель треугольников с нормалями (поверхность рельефа, стенки грунта).</summary>
    private sealed class MeshBuilderParts
    {
        public readonly Vector3Collection Positions = [];
        public readonly Vector3Collection Normals = [];
        public readonly IntCollection Indices = [];

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var n = Vector3.Cross(b - a, d - a);
            if (n.LengthSquared() < 1e-12f) n = Vector3.Cross(c - b, a - b);
            n = n.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(n);
            if (n.Y < 0 && Math.Abs(n.Y) > 0.5f) n = -n;
            int i = Positions.Count;
            Positions.Add(a); Positions.Add(b); Positions.Add(c); Positions.Add(d);
            for (int k = 0; k < 4; k++) Normals.Add(n);
            Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
            Indices.Add(i); Indices.Add(i + 2); Indices.Add(i + 3);
        }
    }

    private void AddSurface(MeshBuilderParts parts, Color color, float alpha)
    {
        if (parts.Indices.Count == 0) return;
        _siteGroup.Children.Add(new MeshGeometryModel3D
        {
            Geometry = new MeshGeometry3D { Positions = parts.Positions, Normals = parts.Normals, Indices = parts.Indices },
            Material = new PhongMaterial
            {
                DiffuseColor = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, alpha),
                AmbientColor = new Color4(color.R / 255f * 0.55f, color.G / 255f * 0.55f, color.B / 255f * 0.55f, 1),
                SpecularColor = new Color4(0.02f, 0.02f, 0.02f, 1)
            },
            IsTransparent = alpha < 1,
            CullMode = CullMode.None,
            IsHitTestVisible = false,
            IsThrowingShadow = false
        });
    }

    // ═════════════════════════════ Пины точек и вершин ═════════════════════════════

    private void AddSiteHandles(GpzuSitePlan plan)
    {
        _siteHandles.Clear();
        if (_vm?.IsSiteEditing != true || _theme is null) return;
        var (minX, maxX, minZ, maxZ) = plan.Bounds;
        float r = (float)Math.Clamp(Math.Max(maxX - minX, maxZ - minZ) / 85, 0.35, 1.4);
        foreach (var (reference, p, _) in EditableSiteTerrainService.EditableVertices(plan))
        {
            bool boundary = reference.Layer == SiteVertexLayer.Boundary;
            var center = new Vector3((float)p.X, (float)(TerrainH(p.X, p.Z) + (boundary ? 1.0 : 0.6)), (float)p.Z);
            float radius = boundary ? r : r * 0.7f;
            var material = HandleMaterial(new Color4(0.97f, 0.97f, 0.98f, 1f));
            var mb = new MeshBuilder();
            mb.AddSphere(center, radius, 16, 16);
            _siteGroup.Children.Add(new MeshGeometryModel3D { Geometry = mb.ToMeshGeometry3D(), Material = material, CullMode = CullMode.None, IsHitTestVisible = false, IsThrowingShadow = false });
            _siteHandles.Add((reference, center, radius, false, material));
        }
        // «+» в серединах сторон границы — вставка поворотной точки.
        for (int i = 0; i < plan.Boundary.Count; i++)
        {
            var a = plan.Boundary[i];
            var b = plan.Boundary[(i + 1) % plan.Boundary.Count];
            if (Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z)) < 4 * r) continue;
            double mx = (a.X + b.X) / 2, mz = (a.Z + b.Z) / 2;
            var mid = new Vector3((float)mx, (float)(TerrainH(mx, mz) + 0.5), (float)mz);
            var material = HandleMaterial(ToColor4(_theme.Current.Selection));
            var mb = new MeshBuilder();
            float s = r * 0.55f;
            mb.AddBox(mid, s * 2.6f, s * 0.7f, s * 0.7f);
            mb.AddBox(mid, s * 0.7f, s * 0.7f, s * 2.6f);
            _siteGroup.Children.Add(new MeshGeometryModel3D { Geometry = mb.ToMeshGeometry3D(), Material = material, CullMode = CullMode.None, IsHitTestVisible = false, IsThrowingShadow = false });
            _siteHandles.Add((SiteVertexRef.Boundary(i), mid, s * 1.4f, true, material));
        }
        RefreshSiteHandleColors();
    }

    private void RefreshSiteHandleColors()
    {
        var orange = new Color4(1.0f, 0.62f, 0.04f, 1f);
        var white = new Color4(0.97f, 0.97f, 0.98f, 1f);
        var green = new Color4(0.06f, 0.73f, 0.51f, 1f);
        var accent = _theme is null ? orange : ToColor4(_theme.Current.Selection);
        var selected = _vm?.SelectedSiteVertex;
        foreach (var (reference, _, _, isEdge, material) in _siteHandles)
        {
            Color4 c = isEdge ? (_siteHoverEdge && _siteHover == reference ? orange : accent)
                : reference == _siteDrag || reference == selected || !_siteHoverEdge && reference == _siteHover ? orange
                : reference.Layer == SiteVertexLayer.Zone ? green : white;
            material.DiffuseColor = c;
            material.AmbientColor = c;
            material.EmissiveColor = new Color4(c.Red * 0.55f, c.Green * 0.55f, c.Blue * 0.55f, 1f);
        }
    }

    /// <summary>Пин или «+» под курсором (экранный радиус + 4 px).</summary>
    private (SiteVertexRef Ref, bool IsEdge)? SiteHandleAt(Windows.Foundation.Point position)
    {
        var vertices = _siteHandles.Where(h => !h.IsEdge).ToList();
        if (PickScreen(vertices.Select((h, i) => (i, h.Center, h.Radius)), position, 4) is { } vi) return (vertices[vi].Ref, false);
        var edges = _siteHandles.Where(h => h.IsEdge).ToList();
        if (PickScreen(edges.Select((h, i) => (i, h.Center, h.Radius)), position, 3) is { } ei) return (edges[ei].Ref, true);
        return null;
    }

    /// <summary>Нажатие на пин (перетаскивание) или «+» (вставка точки, один шаг журнала). true — обработано.</summary>
    private bool TryBeginSiteEdit(Windows.Foundation.Point position, Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        if (_viewport is null || _vm is not { IsSiteEditing: true } || _scene?.SitePlan is not { } plan || SiteHandleAt(position) is not { } hit) return false;
        if (hit.IsEdge)
        {
            _vm.InsertSiteVertex(hit.Ref);
            return true;
        }
        var handle = _siteHandles.First(h => h.Ref == hit.Ref && !h.IsEdge);
        _siteDragPlaneY = handle.Center.Y;
        if (_viewport.UnProjectOnPlane(position, new Vector3(0, _siteDragPlaneY, 0), Vector3.UnitY) is not { } start) return false;
        _siteDrag = hit.Ref;
        _siteDragStart = _siteDragCurrent = EditableSiteTerrainService.PointOf(plan, hit.Ref) ?? default;
        _siteDragStartHit = start;
        _siteDragPointer = pointer;
        _vm.SelectedSiteVertex = hit.Ref;
        _scene.SelectedElement = null;
        _viewport.IsPanEnabled = false;
        _viewport.IsRotationEnabled = false;
        if (pointer is not null) _viewport.CapturePointer(pointer);
        RefreshSiteHandleColors();
        ShowSiteHud();
        return true;
    }

    /// <summary>Перетаскивание: шаг 0,05 м (Alt — 0,01 м); резиновая линия к соседним точкам и капсула «Точка N · Δ · S».</summary>
    private void DragSiteVertexTo(Windows.Foundation.Point position)
    {
        if (_viewport is null || _siteDrag is not { } r || _scene?.SitePlan is not { } plan) return;
        if (_viewport.UnProjectOnPlane(position, new Vector3(0, _siteDragPlaneY, 0), Vector3.UnitY) is not { } hit) return;
        double step = IsKeyDown(VirtualKey.Menu) ? 0.01 : 0.05;
        double x = Math.Round((_siteDragStart.X + hit.X - _siteDragStartHit.X) / step) * step;
        double z = Math.Round((_siteDragStart.Z + hit.Z - _siteDragStartHit.Z) / step) * step;
        _siteDragCurrent = new SitePoint(x, z);
        var ring = EditableSiteTerrainService.Ring(plan, r);
        _sitePreviewGroup.Children.Clear();
        if (ring.Count >= 3)
        {
            var lb = new LineBuilder();
            var prev = ring[(r.Index + ring.Count - 1) % ring.Count];
            var next = ring[(r.Index + 1) % ring.Count];
            var p = new Vector3((float)x, (float)(TerrainH(x, z) + 0.4), (float)z);
            lb.AddLine(new Vector3((float)prev.X, (float)(TerrainH(prev.X, prev.Z) + 0.4), (float)prev.Z), p);
            lb.AddLine(p, new Vector3((float)next.X, (float)(TerrainH(next.X, next.Z) + 0.4), (float)next.Z));
            lb.AddLine(new Vector3((float)x, (float)TerrainH(x, z), (float)z), new Vector3((float)x, (float)(TerrainH(x, z) + 2.4), (float)z));
            _sitePreviewGroup.Children.Add(new LineGeometryModel3D { Geometry = lb.ToLineGeometry3D(), Color = Color.FromArgb(255, 255, 159, 10), Thickness = 2.6, DepthBias = -200, IsHitTestVisible = false });
        }
        ShowSiteHud();
    }

    private void EndSiteDrag()
    {
        if (_siteDrag is not { } r) return;
        _siteDrag = null;
        _sitePreviewGroup.Children.Clear();
        if (_viewport is not null)
        {
            if (_siteDragPointer is not null) _viewport.ReleasePointerCapture(_siteDragPointer);
            _viewport.IsPanEnabled = true;
            _viewport.IsRotationEnabled = _vm?.ViewMode == ViewModels.ViewportMode.Perspective3D;
        }
        _siteDragPointer = null;
        double moved = Math.Sqrt((_siteDragCurrent.X - _siteDragStart.X) * (_siteDragCurrent.X - _siteDragStart.X) + (_siteDragCurrent.Z - _siteDragStart.Z) * (_siteDragCurrent.Z - _siteDragStart.Z));
        // Отпускание — ровно один шаг журнала (контур пересчитывается целиком: площадь, стороны, зоны, рельеф, проверка посадки).
        if (moved > 0.009) _vm?.MoveSiteVertex(r, _siteDragCurrent.X, _siteDragCurrent.Z);
        RefreshSiteHandleColors();
        ShowSiteHud();
    }

    private bool UpdateSiteHover(Windows.Foundation.Point position)
    {
        if (_vm?.IsSiteEditing != true || _siteHandles.Count == 0) return false;
        var hit = SiteHandleAt(position);
        var reference = hit?.Ref;
        bool isEdge = hit?.IsEdge ?? false;
        if (reference == _siteHover && isEdge == _siteHoverEdge) return reference is not null;
        _siteHover = reference;
        _siteHoverEdge = isEdge;
        RefreshSiteHandleColors();
        ShowSiteHud();
        return reference is not null;
    }

    /// <summary>Delete при выбранной точке участка (Среда 1, без выделенного элемента) — удалить точку одним шагом.</summary>
    private bool HandleSiteDelete()
    {
        if (_vm is not { IsSiteEditing: true, SelectedSiteVertex: { } v } || _scene?.SelectedElement is not null) return false;
        if (!_vm.RemoveSiteVertex(v)) _vm.ShowNotice(_vm.Loc.Get("W6SiteTitle"), _vm.Loc.Get("W6SiteMinimum"), "AIA-W6-MIN");
        return true;
    }

    /// <summary>Правая кнопка на пине — удаление точки.</summary>
    private bool TryRemoveSiteVertexAt(Windows.Foundation.Point position)
    {
        if (_vm is not { IsSiteEditing: true } || SiteHandleAt(position) is not { IsEdge: false } hit) return false;
        _vm.RemoveSiteVertex(hit.Ref);
        return true;
    }

    /// <summary>Капсула точки участка: «Точка 2 · X … · Z …», при перетаскивании — Δ и площадь по Гауссу нового контура.</summary>
    private bool ShowSiteHud()
    {
        if (_vm is null || _scene?.SitePlan is not { } plan || _vm.IsSiteEditing != true) return false;
        var r = _siteDrag ?? _siteHover;
        if (r is not { } reference || EditableSiteTerrainService.PointOf(plan, reference) is not { } p)
        {
            if (_scene.SelectedElement is null) HudBorder.Visibility = Visibility.Collapsed;
            return false;
        }
        var c = _vm.Loc.Culture;
        bool edge = _siteDrag is null && _siteHoverEdge;
        HudGlyph.Glyph = "\uE707";
        HudLabel.Text = edge ? _vm.Loc.Get("W6InsertHere") : reference.Layer == SiteVertexLayer.Boundary ? _vm.Loc.Format("W6PointN", plan.PointNumbers[reference.Index]) + ":" : _vm.Loc.Format("W6ZoneVertexN", reference.Index + 1) + ":";
        var at = _siteDrag is not null ? _siteDragCurrent : p;
        var (gx, gy) = plan.ToGeo(at.X, at.Z);
        HudValue.Text = string.Create(c, $"X {gx:N2} · Y {gy:N2} м");
        if (_siteDrag is not null)
        {
            var ring = EditableSiteTerrainService.Ring(plan, reference).ToList();
            ring[reference.Index] = at;
            double area = EditableSiteTerrainService.RingArea(ring);
            double d = Math.Sqrt((at.X - _siteDragStart.X) * (at.X - _siteDragStart.X) + (at.Z - _siteDragStart.Z) * (at.Z - _siteDragStart.Z));
            HudDelta.Text = string.Create(c, $"(Δ {d:0.00} м · S {area:N2} м²)");
            HudHint.Text = _vm.Loc.Get("W6DragHint");
        }
        else
        {
            HudDelta.Text = reference.Layer == SiteVertexLayer.Boundary
                ? string.Create(c, $"(Z {EditableSiteTerrainService.EffectiveVertexElevation(plan, reference.Index):0.00} м БС)")
                : string.Empty;
            HudHint.Text = edge ? string.Empty : _vm.Loc.Get("W6PinHint");
        }
        HudDelta.Visibility = HudDelta.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HudHint.Visibility = HudHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HudValueBox.Background = null;
        HudValueBox.Padding = new Thickness(0);
        HudBorder.Visibility = Visibility.Visible;
        UpdateOverlayTracking();
        UpdateOverlayPositions();
        return true;
    }


    private Vector3? SiteHudAnchor()
    {
        if (_vm?.IsSiteEditing != true) return null;
        if (_siteDrag is { } && _scene?.SitePlan is not null)
            return new Vector3((float)_siteDragCurrent.X, (float)(TerrainH(_siteDragCurrent.X, _siteDragCurrent.Z) + 0.8), (float)_siteDragCurrent.Z);
        if (_siteHover is { } r && _siteHandles.FirstOrDefault(h => h.Ref == r && h.IsEdge == _siteHoverEdge) is { Radius: > 0 } handle) return handle.Center;
        return null;
    }

#if DEBUG
    /// <summary>Съёмка: перетаскивание точки участка тем же путём, что мышь; капсула остаётся на экране.</summary>
    public void DebugDragSiteVertex(SiteVertexRef r, double x, double z)
    {
        if (_scene?.SitePlan is not { } plan || EditableSiteTerrainService.PointOf(plan, r) is not { } p) return;
        _siteDrag = r;
        _siteDragStart = p;
        _siteDragCurrent = new SitePoint(x, z);
        _siteDragPlaneY = (float)(TerrainH(p.X, p.Z) + 0.6);
        EndSiteDrag();
        _siteHover = r;
        _siteHoverEdge = false;
        RefreshSiteHandleColors();
        ShowSiteHud();
    }
#endif
}
