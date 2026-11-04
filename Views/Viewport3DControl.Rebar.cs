using AIArchitectStudio.Models;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.Rendering;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.WinUI;
using Color = Windows.UI.Color;

namespace AIArchitectStudio.Views;

/// <summary>
/// Визуализация 3D-армирования (КЖ, модуль «Модули и Плагины»): каркасы колонн и балок, сетки плит и фундаментов поверх модели
/// (TopMostGroup3D — «рентген» сквозь бетон). Цели — выделенные элементы, без выделения — каркасы всех видимых колонн и балок
/// (не больше <see cref="RebarElementLimit"/>). Перестройка — не чаще раза за кадр UI.
/// </summary>
public sealed partial class Viewport3DControl
{
    private const int RebarElementLimit = 600;

    private readonly TopMostGroup3D _rebarGroup = new() { EnableTopMost = true };
    private bool _rebarQueued;

    private void QueueRebarRebuild()
    {
        if (_rebarQueued) return;
        _rebarQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _rebarQueued = false;
            RebuildRebar();
        });
    }

    private void RebuildRebar()
    {
        _rebarGroup.Children.Clear();
        if (_vm is null || _scene is null || _theme is null) return;
        if (!_vm.IsRebarView || !_vm.IsRebarModule || _highResRendering)
        {
            _vm.RebarInfoText = string.Empty;
            return;
        }

        var selected = _scene.SelectedElements.Where(RebarCageBuilder.Supports).ToList();
        // Без выделения — каркасы колонн и балок (сетки плит всего здания закрыли бы модель), с выделением — всё выделенное.
        var targets = selected.Count > 0
            ? selected
            : _scene.Elements.Where(e => e.Type is ElementTypes.Column or ElementTypes.Beam && RebarCageBuilder.Supports(e) && _geometryById.ContainsKey(e.Id))
                .Take(RebarElementLimit).ToList();

        var analysis = _vm.Analysis;
        var utilization = analysis.UtilizationById;
        var punching = BimAnalyticsService.PunchingReinforcedIds(analysis);
        bool light = _theme.Current.IsLight || _vm.IsClayMode;
        var workingColor = light ? Color.FromArgb(255, 0xD9, 0x48, 0x0F) : Color.FromArgb(255, 0xFF, 0x7A, 0x45);
        var stirrupColor = light ? Color.FromArgb(255, 0xE8, 0x8A, 0x4A) : Color.FromArgb(255, 0xFF, 0xC2, 0x8F);
        // Каркасы продавливания Кр — зелёным: отличаются от оранжевых сеток плиты и синей подсветки выделения.
        var punchingColor = light ? Color.FromArgb(255, 0x24, 0x8A, 0x3D) : Color.FromArgb(255, 0x32, 0xD7, 0x4B);
        int punchingZones = 0;

        RebarCage? single = null;
        int drawn = 0, bars = 0;
        foreach (var e in targets)
        {
            if (!_geometryById.TryGetValue(e.Id, out var g) || g.IsEmpty) continue;
            var columns = StructuralDesign.HasPunchingRebar(e) ? PunchingColumnsOf(e, analysis) : null;
            var cage = RebarCageBuilder.Build(e, g.LocalBounds, utilization.GetValueOrDefault(e.Id), punching.Contains(e.Id), columns);
            if (cage is null || cage.Working.Count == 0) continue;
            AddRebarLines(cage.Working, workingColor, 1.4, g.Transform.ToDx());
            if (cage.Stirrups.Count > 0) AddRebarLines(cage.Stirrups, stirrupColor, 0.7, g.Transform.ToDx());
            if (cage.Punching.Count > 0) AddRebarLines(cage.Punching, punchingColor, 1.6, g.Transform.ToDx());
            punchingZones += cage.PunchingZoneCount;
            single = cage;
            drawn++;
            bars += cage.BarCount;
        }

        var loc = _vm.Loc;
        string working = StructuralNorms.Title(_vm.Settings.Current.StructuralLoads.Rebar == RebarClass.A240 ? RebarClass.A400 : _vm.Settings.Current.StructuralLoads.Rebar);
        _vm.RebarInfoText = drawn == 0
            ? loc.Get("RebarNone")
            : drawn == 1 && single is not null && selected.Count == 1
                ? single.IsMesh
                    ? loc.Format("RebarMeshInfo", selected[0].DisplayTitle, working, single.WorkingDiameter, single.Step * 1000)
                    : loc.Format("RebarFrameInfo", selected[0].DisplayTitle, working, single.BarCount, single.WorkingDiameter, single.StirrupDiameter, single.Step * 1000)
                : loc.Format("RebarManyInfo", drawn, bars);
        if (punchingZones > 0) _vm.RebarInfoText += " · " + loc.Format("W3RebarPunchingInfo", punchingZones);
    }

    /// <summary>
    /// Колонны узлов продавливания плиты по результату расчёта (опоры и стоящие на плите); без расчётных узлов — колонны той же
    /// постройки, чьё сечение в плане попадает в контур плиты, а верх или низ — у её нижней или верхней грани.
    /// </summary>
    private List<PunchingColumn> PunchingColumnsOf(ArchitecturalElement slab, StructuralAnalysisResult analysis)
    {
        var ids = analysis.PunchingNodes.Where(n => n.SlabId == slab.Id).Select(n => n.ColumnId).ToHashSet();
        var bottom = slab.Y - slab.Height / 2;
        var top = slab.Y + slab.Height / 2;
        var columns = _scene!.Elements.Where(c => c.Category == BimCategory.Column && (ids.Count > 0
            ? ids.Contains(c.Id)
            : c.BuildingGroupId == slab.BuildingGroupId && Math.Abs(slab.X - c.X) <= slab.Width / 2 + 0.01 && Math.Abs(slab.Z - c.Z) <= slab.Depth / 2 + 0.01 &&
              (Math.Abs(c.Y + c.Height / 2 - bottom) < 0.1 || Math.Abs(c.Y - c.Height / 2 - top) < 0.1)));
        return RebarCageBuilder.LocalColumns(slab, columns);
    }

    private void AddRebarLines(List<System.Numerics.Vector3> segments, Color color, double thickness, SharpDX.Matrix transform)
    {
        _rebarGroup.Children.Add(new LineGeometryModel3D
        {
            Geometry = HelixMeshAdapter.ToLineGeometry(segments),
            Color = color,
            Thickness = thickness,
            HxTransform3D = transform,
            IsHitTestVisible = false
        });
    }
}
