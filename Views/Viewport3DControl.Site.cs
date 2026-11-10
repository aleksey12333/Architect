using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Gpzu;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.WinUI;
using Microsoft.UI.Xaml;
using SharpDX;
using Color = Windows.UI.Color;
using CullMode = SharpDX.Direct3D11.CullMode;

namespace AIArchitectStudio.Views;

/// <summary>
/// Волна 5 — участок ГПЗУ во вьюпорте: кобальтовый поребрик границы с пинами характерных точек 1…N и длинами сторон,
/// изумрудное пятно допустимого размещения с диагональной штриховкой, цветные контуры ЗОУИТ и подзон ПАТ со смещённым штрихом
/// (зоны часто совпадают с участком — каждая вложена внутрь на 0,6 м и начинает пунктир со своей фазой), полупрозрачные
/// коридоры сетей топосъёмки с осью трассы, терракотовые объёмы существующих ОКС и стеклянная плоскость потолка высоты ПАТ.
/// Слои включаются тумблерами студии (MainViewModel.IsSiteLayerVisible). В расчёт и экспорт элементов не входит.
/// </summary>
public sealed partial class Viewport3DControl
{
    private static readonly CultureInfo SiteRu = CultureInfo.GetCultureInfo("ru-RU");
    private readonly GroupModel3D _siteGroup = new();
    private bool _siteRebuildQueued;

    private void QueueSiteRebuild()
    {
        if (_siteRebuildQueued) return;
        _siteRebuildQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _siteRebuildQueued = false;
            RebuildSite();
        });
    }

    private void RebuildSite()
    {
        if (_viewport is null || _scene is null || _theme is null) return;
        _siteGroup.Children.Clear();
        _siteHandles.Clear();
        if (_scene.SitePlan is not { } plan)
        {
            // Без участка — прежняя абстрактная сетка земли.
            _siteTerrain = null;
            _gridMinor.Visibility = Visibility.Visible;
            _gridMajor.Visibility = Visibility.Visible;
            return;
        }
        // Волна 6: участок — главная земля проекта: сетка заменяется платформой с рельефом, грунтом и подземными сетями.
        _siteTerrain = plan.Terrain(EditableSiteTerrainService.BuildingPads(_scene.Elements.ToList()));
        _gridMinor.Visibility = Visibility.Collapsed;
        _gridMajor.Visibility = Visibility.Collapsed;
        AddTerrainPlatform(plan, _siteTerrain);
        AddUnderground(plan, _siteTerrain);
        bool Visible(string id) => _vm?.IsSiteLayerVisible(id) ?? true;
        var theme = _theme.Current;
        var lines = new Dictionary<(Color Color, double Thickness), LineBuilder>();
        LineBuilder Lines(Color c, double thickness)
        {
            if (!lines.TryGetValue((c, thickness), out var lb)) lines[(c, thickness)] = lb = new LineBuilder();
            return lb;
        }
        Vector3 V(SitePoint p, double y) => new((float)p.X, (float)(y + TerrainH(p.X, p.Z)), (float)p.Z);

        // 1. Коридоры сетей: полупрозрачные полосы ± полуширина и ось трассы.
        foreach (var zone in plan.Zones.Where(z => z.Zone.IsNetwork && Visible(z.Id)))
        {
            var color = Hex(zone.Zone.ColorHex);
            AddFill(zone.Corridor, color, 0.30f, 0.035);
            var axis = Lines(color, 2.2);
            foreach (var l in zone.Lines)
                for (int i = 1; i < l.Count; i++) axis.AddLine(V(l[i - 1], 0.07), V(l[i], 0.07));
            var edge = Lines(Blend(color, theme.ViewportBackground, 0.35), 0.8);
            foreach (var piece in zone.Corridor.Where(c => c.Count == 4))
            {
                edge.AddLine(V(piece[0], 0.045), V(piece[1], 0.045));
                edge.AddLine(V(piece[2], 0.045), V(piece[3], 0.045));
            }
        }

        // 2. Пятно допустимого размещения: заливка и диагональная градостроительная штриховка.
        if (plan.Zones.FirstOrDefault(z => z.Zone.Kind == GpzuZoneKind.AllowedArea) is { } allowed && Visible(allowed.Id))
        {
            var green = Hex(SiteBoundaryBuilder.AllowedColor);
            AddFill([allowed.Polygon], green, 0.16f, 0.02);
            var hatch = Lines(green, 1.0);
            foreach (var (a, b) in SiteBoundaryBuilder.HatchSegments(allowed.Polygon, 3.0, 45)) hatch.AddLine(V(a, 0.03), V(b, 0.03));
        }

        // 3. ЗОУИТ и подзоны: контур, вложенный на 0,6 м на каждую зону, пунктир со своей фазой.
        int ring = 0;
        foreach (var zone in plan.Zones.Where(z => z.Polygon.Count >= 3 && z.Zone.Kind is GpzuZoneKind.TransportZone or GpzuZoneKind.AirfieldSubzone or GpzuZoneKind.LocalZone))
        {
            if (!Visible(zone.Id)) { ring++; continue; }
            var color = Hex(zone.Zone.ColorHex);
            var poly = zone.Zone.CoversWholeParcel ? SiteBoundaryBuilder.Offset(zone.Polygon, 0.32 * (++ring)) : zone.Polygon.ToList();
            var lb = Lines(color, 1.1);
            foreach (var (a, b) in SiteBoundaryBuilder.Dashes(poly, 3.0, 2.2, ring * 0.37)) lb.AddLine(V(a, 0.06), V(b, 0.06));
            if (zone.Zone.Kind == GpzuZoneKind.LocalZone) AddFill([zone.Polygon], color, 0.10f, 0.025);
        }

        // 4. Граница участка: поребрик 0,25 × 0,35 м, линия, пины точек и длины сторон.
        if (Visible("boundary"))
        {
            var cobalt = Hex(SiteBoundaryBuilder.BoundaryColor);
            AddCurb(plan.Boundary, cobalt);
            var edge = Lines(cobalt, 3.0);
            for (int i = 0; i < plan.Boundary.Count; i++) edge.AddLine(V(plan.Boundary[i], 0.36), V(plan.Boundary[(i + 1) % plan.Boundary.Count], 0.36));
            var pins = Lines(cobalt, 1.4);
            var bounds = plan.Bounds;
            double cx = (bounds.MinX + bounds.MaxX) / 2, cz = (bounds.MinZ + bounds.MaxZ) / 2;
            for (int i = 0; i < plan.Boundary.Count; i++)
            {
                var p = plan.Boundary[i];
                pins.AddLine(V(p, 0.36), V(p, 2.2));
                SiteLabel(plan.PointNumbers[i], V(p, 2.6), new Color4(1, 1, 1, 1), ToColor4(cobalt), 12, true);
            }
            foreach (var s in plan.Segments)
            {
                // Подпись длины — снаружи контура, на 1,6 м от середины стороны.
                double ox = s.Mid.X - cx, oz = s.Mid.Z - cz, ol = Math.Max(1e-6, Math.Sqrt(ox * ox + oz * oz));
                var at = V(new SitePoint(s.Mid.X + ox / ol * 1.6, s.Mid.Z + oz / ol * 1.6), 0.5);
                if (s.Length >= 1.0)
                    SiteLabel(s.Length.ToString("0.00", SiteRu), at, ToColor4(theme.TextPrimary), WithAlpha(ToColor4(theme.Panel), 0.78f), 10, false);
            }
        }

        // 5. Существующие ОКС — терракотово-серые объёмы с номером.
        if (Visible("existing"))
        {
            var terracotta = Hex(SiteBoundaryBuilder.ExistingColor);
            foreach (var o in plan.Existing.Where(o => o.Footprint.Count >= 3))
            {
                AddPrism(o.Footprint, o.Height, terracotta, 0.92f);
                var lb = Lines(Blend(terracotta, Color.FromArgb(255, 30, 30, 30), 0.45), 1.0);
                for (int i = 0; i < o.Footprint.Count; i++)
                {
                    lb.AddLine(V(o.Footprint[i], o.Height + 0.01), V(o.Footprint[(i + 1) % o.Footprint.Count], o.Height + 0.01));
                    lb.AddLine(V(o.Footprint[i], 0.01), V(o.Footprint[i], o.Height));
                }
                double ox = o.Footprint.Average(p => p.X), oz = o.Footprint.Average(p => p.Z);
                SiteLabel(o.Number > 0 ? "№ " + o.Number.ToString(SiteRu) : o.Label, new Vector3((float)ox, (float)o.Height + 1.2f, (float)oz),
                    new Color4(1, 1, 1, 1), ToColor4(terracotta), 11, true);
            }
        }

        // 6. Потолок высоты ПАТ: стеклянная плоскость над участком и подпись Hабс.
        if (plan.CeilingHeight is { } h && h > 1 && Visible("ceiling"))
        {
            var blue = Hex(SiteBoundaryBuilder.CeilingColor);
            AddFill([plan.Boundary], blue, 0.10f, h);
            var lb = Lines(blue, 1.4);
            for (int i = 0; i < plan.Boundary.Count; i++)
            {
                lb.AddLine(V(plan.Boundary[i], h), V(plan.Boundary[(i + 1) % plan.Boundary.Count], h));
            }
            var top = plan.Boundary.OrderBy(p => p.Z).First();
            SiteLabel(string.Create(SiteRu, $"Hабс {plan.CeilingAbs:0.00} м БС · +{h:0.0} м"), new Vector3((float)top.X, (float)h + 1.5f, (float)top.Z),
                new Color4(1, 1, 1, 1), ToColor4(blue), 12, true);
        }

        AddSiteHandles(plan);
        foreach (var ((color, thickness), lb) in lines)
            _siteGroup.Children.Add(new LineGeometryModel3D
            {
                Geometry = lb.ToLineGeometry3D(),
                Color = color,
                Thickness = thickness,
                DepthBias = -60,
                IsHitTestVisible = false,
                IsThrowingShadow = false
            });
    }

    private void SiteLabel(string text, Vector3 at, Color4 foreground, Color4 background, int size, bool bold)
    {
        var label = new BillboardSingleText3D
        {
            TextInfo = new HelixToolkit.SharpDX.Core.TextInfo(text, at),
            FontColor = foreground,
            BackgroundColor = background,
            FontSize = size,
            FontFamily = "Segoe UI",
            FontWeight = bold ? SharpDX.DirectWrite.FontWeight.SemiBold : SharpDX.DirectWrite.FontWeight.Normal,
            Padding = new HelixToolkit.SharpDX.Core.Model.Scene2D.Thickness(5, 2, 5, 2)
        };
        _siteGroup.Children.Add(new BillboardTextModel3D { Geometry = label, FixedSize = true, IsHitTestVisible = false });
    }

    /// <summary>Полупрозрачная заливка набора контуров на высоте y (отсечение ушей ядра; контур приводится к обходу против часовой).</summary>
    private void AddFill(IEnumerable<IReadOnlyList<SitePoint>> rings, Color color, float alpha, double y)
    {
        var positions = new Vector3Collection();
        var normals = new Vector3Collection();
        var indices = new IntCollection();
        foreach (var ring in rings)
        {
            if (ring.Count < 3) continue;
            foreach (var (a, b, c) in TriangulateRing(ring))
            {
                int i = positions.Count;
                positions.Add(new Vector3(a.X, (float)(y + TerrainH(a.X, a.Y)), a.Y));
                positions.Add(new Vector3(b.X, (float)(y + TerrainH(b.X, b.Y)), b.Y));
                positions.Add(new Vector3(c.X, (float)(y + TerrainH(c.X, c.Y)), c.Y));
                for (int k = 0; k < 3; k++) normals.Add(Vector3.UnitY);
                indices.Add(i); indices.Add(i + 1); indices.Add(i + 2);
            }
        }
        if (indices.Count == 0) return;
        _siteGroup.Children.Add(new MeshGeometryModel3D
        {
            Geometry = new MeshGeometry3D { Positions = positions, Normals = normals, Indices = indices },
            Material = new PhongMaterial
            {
                DiffuseColor = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, alpha),
                AmbientColor = new Color4(color.R / 255f * 0.6f, color.G / 255f * 0.6f, color.B / 255f * 0.6f, 1),
                EmissiveColor = new Color4(color.R / 255f * 0.35f, color.G / 255f * 0.35f, color.B / 255f * 0.35f, 1),
                SpecularColor = new Color4(0, 0, 0, 1)
            },
            IsTransparent = true,
            CullMode = CullMode.None,
            DepthBias = -40,
            IsHitTestVisible = false,
            IsThrowingShadow = false
        });
    }

    private static IEnumerable<(System.Numerics.Vector2 A, System.Numerics.Vector2 B, System.Numerics.Vector2 C)> TriangulateRing(IReadOnlyList<SitePoint> ring)
    {
        var pts = ring.Select(p => new System.Numerics.Vector2((float)p.X, (float)p.Z)).ToList();
        double area = 0;
        for (int i = 0; i < pts.Count; i++) area += pts[i].X * pts[(i + 1) % pts.Count].Y - pts[(i + 1) % pts.Count].X * pts[i].Y;
        if (area < 0) pts.Reverse();
        var tris = MeshAccumulator.Triangulate(pts).ToList();
        if (tris.Count < pts.Count - 2)
        {
            pts.Reverse();
            var other = MeshAccumulator.Triangulate(pts).ToList();
            if (other.Count > tris.Count) tris = other;
        }
        return tris;
    }

    /// <summary>Поребрик границы: стенка 0,25 м шириной и 0,35 м высотой вдоль каждой стороны (центр — по линии границы).</summary>
    private void AddCurb(IReadOnlyList<SitePoint> ring, Color color)
    {
        var mb = new MeshBuilder();
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double dx = b.X - a.X, dz = b.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 0.05) continue;
            var center = new Vector3((float)((a.X + b.X) / 2), (float)(0.175 + TerrainH((a.X + b.X) / 2, (a.Z + b.Z) / 2)), (float)((a.Z + b.Z) / 2));
            var dir = new Vector3((float)(dx / len), 0, (float)(dz / len));
            mb.AddBox(center, dir, Vector3.UnitY, (float)(len + 0.25), 0.35f, 0.25f);
        }
        _siteGroup.Children.Add(new MeshGeometryModel3D
        {
            Geometry = mb.ToMeshGeometry3D(),
            Material = new PhongMaterial
            {
                DiffuseColor = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, 1),
                AmbientColor = new Color4(color.R / 255f * 0.5f, color.G / 255f * 0.5f, color.B / 255f * 0.5f, 1),
                EmissiveColor = new Color4(color.R / 255f * 0.25f, color.G / 255f * 0.25f, color.B / 255f * 0.25f, 1),
                SpecularColor = new Color4(0.2f, 0.2f, 0.2f, 1),
                SpecularShininess = 30
            },
            IsHitTestVisible = false,
            IsThrowingShadow = false
        });
    }

    /// <summary>Призма существующего ОКС: стены по контуру и крыша (заливка отсечением ушей).</summary>
    private void AddPrism(IReadOnlyList<SitePoint> ring, double height, Color color, float alpha)
    {
        var positions = new Vector3Collection();
        var normals = new Vector3Collection();
        var indices = new IntCollection();
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var n = Vector3.Normalize(new Vector3((float)(b.Z - a.Z), 0, (float)-(b.X - a.X)));
            int k = positions.Count;
            positions.Add(new Vector3((float)a.X, 0, (float)a.Z));
            positions.Add(new Vector3((float)b.X, 0, (float)b.Z));
            positions.Add(new Vector3((float)b.X, (float)height, (float)b.Z));
            positions.Add(new Vector3((float)a.X, (float)height, (float)a.Z));
            for (int j = 0; j < 4; j++) normals.Add(n);
            indices.Add(k); indices.Add(k + 1); indices.Add(k + 2);
            indices.Add(k); indices.Add(k + 2); indices.Add(k + 3);
        }
        foreach (var (a, b, c) in TriangulateRing(ring))
        {
            int k = positions.Count;
            positions.Add(new Vector3(a.X, (float)height, a.Y));
            positions.Add(new Vector3(b.X, (float)height, b.Y));
            positions.Add(new Vector3(c.X, (float)height, c.Y));
            for (int j = 0; j < 3; j++) normals.Add(Vector3.UnitY);
            indices.Add(k); indices.Add(k + 1); indices.Add(k + 2);
        }
        _siteGroup.Children.Add(new MeshGeometryModel3D
        {
            Geometry = new MeshGeometry3D { Positions = positions, Normals = normals, Indices = indices },
            Material = new PhongMaterial
            {
                DiffuseColor = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, alpha),
                AmbientColor = new Color4(color.R / 255f * 0.45f, color.G / 255f * 0.45f, color.B / 255f * 0.45f, 1),
                SpecularColor = new Color4(0.05f, 0.05f, 0.05f, 1)
            },
            IsTransparent = alpha < 1,
            CullMode = CullMode.None,
            IsHitTestVisible = false
        });
    }

    private static Color Hex(string hex)
    {
        string h = hex.TrimStart('#');
        return Color.FromArgb(255, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }
}
