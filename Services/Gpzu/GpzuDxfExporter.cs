using System.Globalization;
using System.Text;
using AIArchitectStudio.Models.Gpzu;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>
/// DXF R12 (AC1009) чертежа ГПЗУ: координаты МСК в осях чертежа (X — восток = Y_геод, Y — север = X_геод), поэтому файл ложится
/// поверх топосъёмки ИГДИ без сдвига. Слои: GPZU_BOUNDARY (граница, синий), GPZU_POINTS (номера точек), GPZU_LENGTHS, GPZU_ALLOWED
/// (пятно допустимого размещения), GPZU_ZOUIT (ЗОУИТ и подзоны ПАТ), NET_* (оси трасс и контуры коридоров по видам сетей),
/// GPZU_OKS (существующие ОКС). Кириллица — \U+XXXX, числа — InvariantCulture.
/// </summary>
public static class GpzuDxfExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly (string Layer, int Color)[] Layers =
    [
        ("GPZU_BOUNDARY", 5), ("GPZU_POINTS", 5), ("GPZU_LENGTHS", 8), ("GPZU_ALLOWED", 3), ("GPZU_ZOUIT", 6), ("GPZU_OKS", 32),
        ("NET_HEAT", 1), ("NET_GAS", 1), ("NET_POWER", 30), ("NET_WATER", 4), ("NET_SEWER", 4), ("NET_COMM", 200)
    ];

    public static string LayerOf(GpzuZoneKind kind) => kind switch
    {
        GpzuZoneKind.HeatNetwork => "NET_HEAT", GpzuZoneKind.GasPipeline => "NET_GAS", GpzuZoneKind.PowerCable => "NET_POWER",
        GpzuZoneKind.WaterSupply => "NET_WATER", GpzuZoneKind.Sewer => "NET_SEWER", GpzuZoneKind.Communication => "NET_COMM",
        GpzuZoneKind.AllowedArea => "GPZU_ALLOWED", _ => "GPZU_ZOUIT"
    };

    /// <summary>Пишет файл и возвращает сводку (слоёв, полилиний, надписей).</summary>
    public static string Export(GpzuSitePlan plan, string path)
    {
        var sb = new StringBuilder();
        int polylines = 0, texts = 0;
        void G(int code, string value) => sb.Append(code).Append('\n').Append(value).Append('\n');
        void Poly(string layer, IEnumerable<(double X, double Y)> pts, bool closed)
        {
            G(0, "POLYLINE"); G(8, layer); G(66, "1"); G(70, closed ? "1" : "0");
            foreach (var (x, y) in pts) { G(0, "VERTEX"); G(8, layer); G(10, F(x)); G(20, F(y)); G(30, "0"); }
            G(0, "SEQEND"); G(8, layer);
            polylines++;
        }
        void Text(string layer, double x, double y, double height, string text)
        {
            G(0, "TEXT"); G(8, layer); G(10, F(x)); G(20, F(y)); G(30, "0"); G(40, F(height)); G(1, Encode(text));
            texts++;
        }
        (double X, double Y) D(SitePoint p)
        {
            var (gx, gy) = plan.ToGeo(p.X, p.Z);
            return (gy, gx);
        }

        G(999, "AI Architect Studio GpzuDxfExporter");
        G(0, "SECTION"); G(2, "HEADER"); G(9, "$ACADVER"); G(1, "AC1009"); G(9, "$INSUNITS"); G(70, "6"); G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "TABLES");
        G(0, "TABLE"); G(2, "LAYER"); G(70, Layers.Length.ToString(Inv));
        foreach (var (layer, color) in Layers) { G(0, "LAYER"); G(2, layer); G(70, "0"); G(62, color.ToString(Inv)); G(6, "CONTINUOUS"); }
        G(0, "ENDTAB"); G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "ENTITIES");

        Poly("GPZU_BOUNDARY", plan.Boundary.Select(D), true);
        for (int i = 0; i < plan.Boundary.Count; i++)
        {
            var (x, y) = D(plan.Boundary[i]);
            Text("GPZU_POINTS", x + 0.6, y + 0.6, 1.2, plan.PointNumbers[i]);
        }
        foreach (var s in plan.Segments)
        {
            var (x, y) = D(s.Mid);
            Text("GPZU_LENGTHS", x, y, 0.8, s.Length.ToString("0.00", Inv));
        }
        foreach (var z in plan.Zones)
        {
            string layer = LayerOf(z.Zone.Kind);
            if (z.Polygon.Count >= 3) Poly(layer, z.Polygon.Select(D), true);
            foreach (var l in z.Lines) Poly(layer, l.Select(D), false);
            foreach (var c in z.Corridor.Where(c => c.Count == 4)) Poly(layer, c.Select(D), true);
        }
        foreach (var o in plan.Existing)
        {
            Poly("GPZU_OKS", o.Footprint.Select(D), true);
            var (x, y) = D(new SitePoint(o.Footprint.Average(p => p.X), o.Footprint.Average(p => p.Z)));
            Text("GPZU_OKS", x, y, 1.5, o.Number > 0 ? "№ " + o.Number : o.Label);
        }
        var (tx, ty) = D(new SitePoint(plan.Bounds.MinX, plan.Bounds.MinZ - 6));
        Text("GPZU_POINTS", tx, ty, 2.0, $"Участок {plan.Document.Cadastral}, S = {plan.Area.ToString("0.00", Inv)} м²");
        G(0, "ENDSEC"); G(0, "EOF");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return $"DXF R12: слоёв {Layers.Length}, полилиний {polylines}, надписей {texts}";
    }

    private static string F(double v) => v.ToString("0.####", Inv);

    /// <summary>Не-ASCII символы — \U+XXXX (как в экспорте сцены).</summary>
    private static string Encode(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (char c in s) sb.Append(c < 128 ? c.ToString() : $"\\U+{(int)c:X4}");
        return sb.ToString();
    }
}
