namespace AIArchitectStudio.Models.Gpzu;

/// <summary>Трасса сети из топосъёмки: вид, слой чертежа, ломаная в МСК (X — север, Y — восток), длина.</summary>
public sealed record TopoNetworkLine(GpzuZoneKind Kind, string Layer, IReadOnlyList<GeoPoint> Points)
{
    public double Length
    {
        get
        {
            double s = 0;
            for (int i = 1; i < Points.Count; i++) s += Geodesy.Length(Points[i - 1], Points[i]);
            return s;
        }
    }
}

/// <summary>Контур из топосъёмки (здание, кадастровый участок, ЗОУИТ кадастрового слоя) с подписью, если она рядом.</summary>
public sealed record TopoContour(string Layer, IReadOnlyList<GeoPoint> Points, string Label);

/// <summary>Высотная отметка топосъёмки (м БС).</summary>
public readonly record struct TopoElevation(double X, double Y, double Z);

/// <summary>
/// Инженерно-геодезическая топосъёмка (ИГДИ) в границах интереса: сети по видам, здания, кадастровые контуры, отметки.
/// Координаты — в системе участка (МСК), копия чертежа в местной СК отбрасывается.
/// </summary>
public sealed class TopoSurvey
{
    public string SourceFile { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Units { get; set; } = string.Empty;
    public int EntityCount { get; set; }
    public int FlattenedCount { get; set; }
    public List<TopoNetworkLine> Networks { get; } = [];
    public List<TopoContour> Buildings { get; } = [];
    public List<TopoContour> CadastralZones { get; } = [];
    public List<TopoContour> Parcels { get; } = [];

    /// <summary>Контуры ОКС кадастрового слоя с кадастровым номером (сопоставляются с разделом 3.1 ГПЗУ).</summary>
    public List<TopoContour> CapitalContours { get; } = [];
    public List<TopoElevation> Elevations { get; } = [];
    public List<string> Layers { get; } = [];
    public List<string> Warnings { get; } = [];
    public double ElapsedMs { get; set; }

    /// <summary>Медиана отметок внутри контура (м БС), null — отметок нет.</summary>
    public double? GroundElevation(IReadOnlyList<GeoPoint> boundary, double margin = 25)
    {
        var inside = Elevations.Where(e => Geodesy.Contains(boundary, e.X, e.Y)).Select(e => e.Z).OrderBy(z => z).ToList();
        if (inside.Count == 0 && boundary.Count > 0)
        {
            var (cx, cy) = Geodesy.Centroid(boundary);
            double r = boundary.Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy))) + margin;
            inside = Elevations.Where(e => (e.X - cx) * (e.X - cx) + (e.Y - cy) * (e.Y - cy) <= r * r).Select(e => e.Z).OrderBy(z => z).ToList();
        }
        return inside.Count == 0 ? null : inside[inside.Count / 2];
    }
}
