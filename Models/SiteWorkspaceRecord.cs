using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.Gpzu;

namespace AIArchitectStudio.Models;

/// <summary>Существующий объект участка в файле: номер, подпись, контур сцены (x, z), высота.</summary>
public sealed class SiteObjectRecord
{
    public int Number { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<double[]> Footprint { get; set; } = [];
    public double Height { get; set; }
}

/// <summary>
/// Волна 6 — блок «site» файла .aia (пишется только при участке, ограничениях или выбранной Среде; у проектов без участка
/// блока нет — формат и эталон ядра прежние): документ ГПЗУ целиком (собственный JSON, вместе с коридорами сетей топосъёмки),
/// начало координат сцены, отметки поворотных точек, рельеф, существующие объекты, подтверждённые ограничения «НО»,
/// толщина грунтового основания и активная Среда девелопера.
/// </summary>
public sealed class SiteWorkspaceRecord
{
    public string? DocumentJson { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public List<double>? VertexElevations { get; set; }
    public List<double[]>? Relief { get; set; }
    public List<SiteObjectRecord>? Existing { get; set; }
    public List<ProjectConstraintItem>? Constraints { get; set; }
    public double SoilThickness { get; set; } = 3.0;
    public string? Environment { get; set; }

    public static SiteWorkspaceRecord? From(GpzuSitePlan? plan, string? environment)
    {
        if (plan is null && environment is null) return null;
        var r = new SiteWorkspaceRecord { Environment = environment };
        if (plan is null) return r;
        r.DocumentJson = GpzuParser.Serialize(plan.Document);
        r.OriginX = plan.OriginX;
        r.OriginY = plan.OriginY;
        r.VertexElevations = plan.VertexElevations.Count > 0 ? [.. plan.VertexElevations] : null;
        r.Relief = plan.Relief.Count > 0 ? plan.Relief.Select(s => new[] { s.X, s.Z, s.ElevationBs }).ToList() : null;
        r.Existing = plan.Existing.Count > 0
            ? plan.Existing.Select(o => new SiteObjectRecord { Number = o.Number, Label = o.Label, Height = o.Height, Footprint = o.Footprint.Select(p => new[] { p.X, p.Z }).ToList() }).ToList()
            : null;
        r.Constraints = plan.Constraints.Count > 0 ? [.. plan.Constraints] : null;
        r.SoilThickness = plan.SoilThickness;
        return r;
    }

    /// <summary>План участка из блока файла (null — блок без участка).</summary>
    public GpzuSitePlan? ToPlan()
    {
        if (string.IsNullOrWhiteSpace(DocumentJson)) return null;
        var doc = GpzuParser.FromJson(DocumentJson);
        if (doc.Boundary.Count < 3) return null;
        var plan = SiteBoundaryBuilder.Build(doc, null, (OriginX, OriginY));
        return plan.With(
            existing: Existing?.Select(o => new SiteObject(o.Number, o.Label, o.Footprint.Where(p => p.Length >= 2).Select(p => new SitePoint(p[0], p[1])).ToList(), o.Height)).ToList(),
            vertexElevations: VertexElevations,
            constraints: Constraints,
            relief: Relief?.Where(p => p.Length >= 3).Select(p => new TerrainSample(p[0], p[1], p[2])).ToList());
    }
}
