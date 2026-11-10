using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using AIArchitectStudio.Models.Gpzu;
using CSMath;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>
/// Чтение топосъёмки ИГДИ (DWG R2000…R2018 и DXF) через ACadSharp: развёртка вставок блоков с матрицами (примитивы слоя «0»
/// наследуют слой вставки — как в AutoCAD), выбор копии чертежа в системе участка (у многих топопланов в одном файле лежат
/// местная СК и МСК), классификация слоёв сетей по названию (RU/EN), здания, кадастровые ЗОУИТ с подписями, высотные отметки.
/// Сети берутся только с собственного слоя примитива: элементы слоя «0» внутри блока, вставленного на слой сети, — это общая
/// топооснова, а не трасса. Ось чертежа X — восток, Y — север (в МСК: Y_геод = X_dwg, X_геод = Y_dwg).
/// </summary>
public static partial class TopoSurveyReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Чтение файла; boundary — граница участка (окно интереса = габарит + margin), null — весь чертёж.</summary>
    public static TopoSurvey Read(string path, IReadOnlyList<GeoPoint>? boundary = null, double margin = 120)
    {
        var sw = Stopwatch.StartNew();
        CadDocument doc;
        bool dwg = path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (dwg)
            {
                using var reader = new DwgReader(path);
                doc = reader.Read();
            }
            else
            {
                using var reader = new DxfReader(path);
                doc = reader.Read();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"Не удалось прочитать чертёж топосъёмки «{Path.GetFileName(path)}»: {ex.Message}", ex);
        }
        var survey = new TopoSurvey
        {
            SourceFile = path,
            Version = doc.Header.VersionString,
            Units = doc.Header.InsUnits.ToString(),
            EntityCount = doc.Entities.Count()
        };
        survey.Layers.AddRange(doc.Layers.Select(l => l.Name));

        // Окно интереса в координатах чертежа (X — восток, Y — север).
        double minE = double.MinValue, maxE = double.MaxValue, minN = double.MinValue, maxN = double.MaxValue;
        if (boundary is { Count: > 2 })
        {
            minE = boundary.Min(p => p.Y) - margin;
            maxE = boundary.Max(p => p.Y) + margin;
            minN = boundary.Min(p => p.X) - margin;
            maxN = boundary.Max(p => p.X) + margin;
        }
        bool InWindow(XYZ p) => p.X >= minE && p.X <= maxE && p.Y >= minN && p.Y <= maxN;

        var polys = new List<(string Layer, bool Own, List<XYZ> Pts, bool Closed)>();
        var texts = new List<(string Layer, XYZ At, string Text)>();
        var points = new List<(string Layer, XYZ At)>();
        int flattened = 0;
        void Walk(IEnumerable<Entity> entities, Transform t, string parentLayer, int depth)
        {
            foreach (var e in entities)
            {
                flattened++;
                bool own = e.Layer is not null && e.Layer.Name != "0";
                string layer = own ? e.Layer!.Name : parentLayer;
                switch (e)
                {
                    case Insert ins when depth < 8 && ins.Block is not null:
                        Walk(ins.Block.Entities, new Transform(t.Matrix * ins.GetTransform().Matrix), layer, depth + 1);
                        foreach (var a in ins.Attributes) texts.Add((layer, t.ApplyTransform(a.InsertPoint), a.Value ?? string.Empty));
                        break;
                    case LwPolyline lw when lw.Vertices.Count >= 2:
                        polys.Add((layer, own, lw.Vertices.Select(v => t.ApplyTransform(new XYZ(v.Location.X, v.Location.Y, lw.Elevation))).ToList(), lw.IsClosed));
                        break;
                    case Polyline2D p2:
                        { var v = p2.Vertices.Select(q => t.ApplyTransform(q.Location)).ToList(); if (v.Count >= 2) polys.Add((layer, own, v, p2.IsClosed)); }
                        break;
                    case Polyline3D p3:
                        { var v = p3.Vertices.Select(q => t.ApplyTransform(q.Location)).ToList(); if (v.Count >= 2) polys.Add((layer, own, v, p3.IsClosed)); }
                        break;
                    case Line ln:
                        polys.Add((layer, own, [t.ApplyTransform(ln.StartPoint), t.ApplyTransform(ln.EndPoint)], false));
                        break;
                    case TextEntity te:
                        texts.Add((layer, t.ApplyTransform(te.InsertPoint), te.Value ?? string.Empty));
                        break;
                    case MText mt:
                        texts.Add((layer, t.ApplyTransform(mt.InsertPoint), mt.Value ?? string.Empty));
                        break;
                    case Point pt:
                        points.Add((layer, t.ApplyTransform(pt.Location)));
                        break;
                }
            }
        }
        Walk(doc.Entities, new Transform(Matrix4.Identity), "0", 0);
        survey.FlattenedCount = flattened;

        static GeoPoint Geo(XYZ p, int i) => new((i + 1).ToString(Inv), p.Y, p.X);
        static List<GeoPoint> Ring(List<XYZ> pts)
        {
            var list = pts.Select(Geo).ToList();
            if (list.Count > 2 && Geodesy.Length(list[0], list[^1]) < 0.01) list.RemoveAt(list.Count - 1);
            return list;
        }

        // Сети: собственный слой, ломаные в окне интереса, длиной не меньше 2 м.
        foreach (var (layer, own, pts, _) in polys)
        {
            if (!own || !pts.Any(InWindow)) continue;
            if (Classify(layer) is not { } kind) continue;
            var line = new TopoNetworkLine(kind, layer, pts.Select(Geo).ToList());
            if (line.Length >= (kind == GpzuZoneKind.PowerCable && layer.Contains("объект", StringComparison.OrdinalIgnoreCase) ? 5 : 2)) survey.Networks.Add(line);
        }

        // Подписи кадастрового слоя (реестровые номера ЗОУИТ и участков).
        var labels = texts.Where(t => InWindow(t.At) && ZoneLabel().IsMatch(t.Text)).ToList();
        foreach (var (layer, own, pts, closed) in polys)
        {
            if (!closed || pts.Count < 3 || !pts.Any(InWindow)) continue;
            var ring = Ring(pts);
            if (ring.Count < 3 || Geodesy.Area(ring) < 4) continue;
            string l = layer.Trim();
            if (IsBuildingLayer(l) && own)
            {
                // Подпись здания топоплана: «5Ж», «2Н», «1КЖ» — этажность и материал.
                string label = texts.Where(t => FloorLabel().IsMatch(t.Text.Trim()) && Geodesy.Contains(ring, t.At.Y, t.At.X)).Select(t => t.Text.Trim()).FirstOrDefault() ?? string.Empty;
                survey.Buildings.Add(new TopoContour(layer, ring, label));
            }
            else if (l == "Z" || l.Contains("зоуит", StringComparison.OrdinalIgnoreCase) || l.Contains("охранн", StringComparison.OrdinalIgnoreCase))
            {
                string label = labels.Where(t => ZouitLabel().IsMatch(t.Text) && Geodesy.Contains(ring, t.At.Y, t.At.X)).Select(t => ZouitLabel().Match(t.Text).Value).FirstOrDefault() ?? string.Empty;
                survey.CadastralZones.Add(new TopoContour(layer, ring, label));
            }
            else if (l == "C" || l.Contains("окс", StringComparison.OrdinalIgnoreCase))
            {
                string label = labels.Where(t => ParcelLabel().IsMatch(t.Text) && Geodesy.Contains(ring, t.At.Y, t.At.X)).Select(t => ParcelLabel().Match(t.Text).Value).FirstOrDefault() ?? string.Empty;
                survey.CapitalContours.Add(new TopoContour(layer, ring, label));
            }
            else if (l == "P" || l.Contains("кадастр", StringComparison.OrdinalIgnoreCase))
            {
                string label = labels.Where(t => ParcelLabel().IsMatch(t.Text) && Geodesy.Contains(ring, t.At.Y, t.At.X)).Select(t => ParcelLabel().Match(t.Text).Value).FirstOrDefault() ?? string.Empty;
                survey.Parcels.Add(new TopoContour(layer, ring, label));
            }
        }

        // Высотные отметки: числа 50…3000 на слоях «Отметка/Высот/Elev», точки пикетов с Z.
        foreach (var (layer, at, text) in texts)
        {
            if (!InWindow(at)) continue;
            if (!(layer.Contains("отмет", StringComparison.OrdinalIgnoreCase) || layer.Contains("высот", StringComparison.OrdinalIgnoreCase) ||
                  layer.Contains("elev", StringComparison.OrdinalIgnoreCase))) continue;
            string s = text.Trim().Replace(',', '.');
            if (double.TryParse(s, NumberStyles.Float, Inv, out var z) && z is > 50 and < 3000) survey.Elevations.Add(new TopoElevation(at.Y, at.X, z));
        }
        foreach (var (layer, at) in points)
            if (InWindow(at) && at.Z is > 50 and < 3000) survey.Elevations.Add(new TopoElevation(at.Y, at.X, at.Z));

        if (survey.Networks.Count == 0) survey.Warnings.Add("В окне участка не найдено трасс инженерных сетей на собственных слоях.");
        survey.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return survey;
    }

    /// <summary>Вид сети по названию слоя (RU/EN); null — не сеть.</summary>
    public static GpzuZoneKind? Classify(string layer)
    {
        string l = layer.ToLowerInvariant();
        if (l.Contains("тепл") || (l.Contains("канал") && !l.Contains("канализ")) || l.Contains("футляр") || l.Contains("heat")) return GpzuZoneKind.HeatNetwork;
        if (l.Contains("газ") || l.Contains("gas")) return GpzuZoneKind.GasPipeline;
        if (l.Contains("кабел") && (l.Contains("связ") || l.Contains("телеф"))) return GpzuZoneKind.Communication;
        if (l.Contains("связ") || l.Contains("слаботоч") || l.Contains("telecom") || l.Contains("comm")) return GpzuZoneKind.Communication;
        if (l.Contains("водопр") || l.Contains("водовод") || l.Contains("water")) return GpzuZoneKind.WaterSupply;
        if (l.Contains("канализ") || l.Contains("дренаж") || l.Contains("ливнев") || l.Contains("sewer") || l.Contains("drain")) return GpzuZoneKind.Sewer;
        if (l.Contains("кабел") || l.Contains("электропередач") || l.Contains("лэп") || l.Contains("power") || l.Contains("elec") || l.Contains("cable"))
            return GpzuZoneKind.PowerCable;
        return null;
    }

    private static bool IsBuildingLayer(string layer)
    {
        string l = layer.ToLowerInvariant();
        return (l.Contains("здани") || l.Contains("строени") || l.Contains("building")) && !l.Contains("проектир");
    }

    [GeneratedRegex(@"^\d{1,2}\s*[НнЖжКкМмДд][а-яА-Я]{0,2}$")]
    private static partial Regex FloorLabel();

    [GeneratedRegex(@"\d{2}:\d{2}[-:][\d.:()]+")]
    private static partial Regex ZoneLabel();

    [GeneratedRegex(@"\d{2}:\d{2}-6\.\d+")]
    private static partial Regex ZouitLabel();

    [GeneratedRegex(@"\d{2}:\d{2}:\d{6,7}:\d+")]
    private static partial Regex ParcelLabel();
}
