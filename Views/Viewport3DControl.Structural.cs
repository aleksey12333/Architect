using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Analytical;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.Rendering;
using AIArchitectStudio.ViewModels;
using HelixToolkit.WinUI;
using SharpDX;
using Color = Windows.UI.Color;

namespace AIArchitectStudio.Views;

/// <summary>
/// Аналитический оверлей режима «Конструкции (КЖ / КМ)»: тела элементов — полупрозрачные в цвете шкалы фактора (как карта напряжений),
/// поверх модели («рентген», TopMostGroup3D) — оси стержней схемы толстыми линиями цвета шкалы, контуры пластин и грузовых площадей
/// плит (выпуклая оболочка точек сетки, отнесённых к каждой опоре), узлы — крестики, опоры на грунте — квадраты. Перестройка — не чаще
/// раза за кадр UI; во время рендера 4K скрыт.
/// </summary>
public sealed partial class Viewport3DControl
{
    private readonly TopMostGroup3D _structuralGroup = new() { EnableTopMost = true };
    private bool _structuralQueued;

    /// <summary>Карта цветов тел: карта напряжений или режим «Конструкции».</summary>
    private bool StressColoring => _vm is not null && (_vm.IsStressMapEnabled || _vm.IsStructuralMode);

    private void OnStructuralOverlayChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        ApplyStress(animate: false);
        QueueStructuralRebuild();
    });

    private void QueueStructuralRebuild()
    {
        if (_structuralQueued) return;
        _structuralQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _structuralQueued = false;
            RebuildStructural();
        });
    }

    /// <summary>Цвет тела в режиме «Конструкции»: шкала выбранного фактора, полупрозрачный (видны оси схемы внутри).</summary>
    private Color4? StructuralBodyColor(ArchitecturalElement el)
    {
        if (_vm is null || !_vm.IsStructuralMode || _vm.IsStressMapEnabled) return null;
        if (_vm.StructuralValue(el.Id) is not { } v) return StressPalette.Ghost;
        var c = StressPalette.ColorFor(v.Ratio);
        return new Color4(c.Red, c.Green, c.Blue, 0.3f);
    }

    private void RebuildStructural()
    {
        _structuralGroup.Children.Clear();
        if (_vm is null || _theme is null || !_vm.IsStructuralMode || _highResRendering || _vm.StructuralModel is not { } model) return;

        // Стержни — по корзинам цвета шкалы (одна модель линий на корзину).
        var buckets = new Dictionary<int, List<System.Numerics.Vector3>>();
        void Segment(int bucket, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            if (!buckets.TryGetValue(bucket, out var list)) buckets[bucket] = list = [];
            list.Add(new System.Numerics.Vector3((float)x0, (float)y0, (float)z0));
            list.Add(new System.Numerics.Vector3((float)x1, (float)y1, (float)z1));
        }
        int BucketOf(string id) => _vm.StructuralValue(id) is { } v ? Math.Clamp((int)Math.Round(v.Ratio * 20), 0, 30) : -1;

        foreach (var m in model.Members)
        {
            int bucket = BucketOf(m.Source.Id);
            var path = m.Path.Count >= 2 ? m.Path : [m.StartNode, m.EndNode];
            for (int i = 0; i + 1 < path.Count; i++)
                Segment(bucket, path[i].X, path[i].Y, path[i].Z, path[i + 1].X, path[i + 1].Y, path[i + 1].Z);
            if (m.AxisClosed && path.Count > 2)
                Segment(bucket, path[^1].X, path[^1].Y, path[^1].Z, path[0].X, path[0].Y, path[0].Z);
        }
        foreach (var (bucket, segments) in buckets)
        {
            var color = bucket < 0 ? Color.FromArgb(255, 0x8E, 0x8E, 0x93) : StressPalette.ToUi(StressPalette.ColorFor(bucket / 20.0));
            AddStructuralLines(segments, Color.FromArgb(255, color.R, color.G, color.B), 3.2);
        }

        // Пластины: контур по верху (стены — срединная плоскость) и грузовые площади плит.
        bool light = _theme.Current.IsLight;
        var outline = new List<System.Numerics.Vector3>();
        var tributary = new List<System.Numerics.Vector3>();
        foreach (var p in model.Panels)
        {
            if (p.IsVertical)
            {
                var c = p.Contour;
                for (int i = 0; i < c.Count; i++) AddPair(outline, c[i], c[(i + 1) % c.Count]);
                continue;
            }
            float y = (float)p.Top + 0.03f;
            for (int i = 0; i < p.Outline.Count; i++)
            {
                var a = p.Outline[i];
                var b = p.Outline[(i + 1) % p.Outline.Count];
                outline.Add(new System.Numerics.Vector3((float)a.X, y, (float)a.Z));
                outline.Add(new System.Numerics.Vector3((float)b.X, y, (float)b.Z));
            }
            if (p.Tributary is { Supports.Count: > 1 } plan) AddTributaryHulls(tributary, plan, y);
        }
        AddStructuralLines(outline, light ? Color.FromArgb(255, 0x3A, 0x3A, 0x3C) : Color.FromArgb(255, 0xD1, 0xD1, 0xD6), 1.2);
        AddStructuralLines(tributary, light ? Color.FromArgb(255, 0x00, 0x7A, 0xFF) : Color.FromArgb(255, 0x64, 0xD2, 0xFF), 0.9);

        // Узлы — крестики, опоры на грунте — квадраты.
        var nodes = new List<System.Numerics.Vector3>();
        var supports = new List<System.Numerics.Vector3>();
        const float n = 0.12f, s = 0.22f;
        foreach (var node in model.Nodes)
        {
            float x = (float)node.X, y = (float)node.Y, z = (float)node.Z;
            if (node.IsFixedSupport)
            {
                supports.AddRange([new(x - s, y, z - s), new(x + s, y, z - s), new(x + s, y, z - s), new(x + s, y, z + s),
                    new(x + s, y, z + s), new(x - s, y, z + s), new(x - s, y, z + s), new(x - s, y, z - s), new(x - s, y, z - s), new(x + s, y, z + s)]);
                continue;
            }
            nodes.AddRange([new(x - n, y, z), new(x + n, y, z), new(x, y - n, z), new(x, y + n, z), new(x, y, z - n), new(x, y, z + n)]);
        }
        AddStructuralLines(nodes, light ? Color.FromArgb(255, 0x1C, 0x1C, 0x1E) : Color.FromArgb(255, 0xF5, 0xF5, 0xF7), 1.4);
        AddStructuralLines(supports, Color.FromArgb(255, 0xFF, 0x9F, 0x0A), 2.0);
    }

    private static void AddPair(List<System.Numerics.Vector3> list, AnalyticalNode a, AnalyticalNode b)
    {
        list.Add(new System.Numerics.Vector3((float)a.X, (float)a.Y, (float)a.Z));
        list.Add(new System.Numerics.Vector3((float)b.X, (float)b.Y, (float)b.Z));
    }

    /// <summary>Грузовые площади: точки сетки плиты — к ближайшей опоре (как в расчёте), контур — выпуклая оболочка точек опоры.</summary>
    private static void AddTributaryHulls(List<System.Numerics.Vector3> target, PanelTributary plan, float y)
    {
        var groups = new Dictionary<int, List<(double X, double Z)>>();
        foreach (var (x, z, _) in plan.Samples)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < plan.Supports.Count; i++)
            {
                double d = plan.Supports[i].DistanceTo(x, z);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (!groups.TryGetValue(best, out var list)) groups[best] = list = [];
            list.Add((x, z));
        }
        foreach (var points in groups.Values)
        {
            if (points.Count < 3) continue;
            var hull = Services.Geometry2D.ConvexHull(points);
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % hull.Count];
                target.Add(new System.Numerics.Vector3((float)a.X, y, (float)a.Z));
                target.Add(new System.Numerics.Vector3((float)b.X, y, (float)b.Z));
            }
        }
    }

    private void AddStructuralLines(List<System.Numerics.Vector3> segments, Color color, double thickness)
    {
        if (segments.Count < 2) return;
        _structuralGroup.Children.Add(new LineGeometryModel3D
        {
            Geometry = HelixMeshAdapter.ToLineGeometry(segments),
            Color = color,
            Thickness = thickness,
            IsHitTestVisible = false
        });
    }
}
