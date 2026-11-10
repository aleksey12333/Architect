using System.Globalization;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services;

/// <summary>Итог выпуска листа ГПЗУ / ПЗУ.</summary>
public sealed record GpzuSheetResult(string Path, int Points, int Zones, int Networks, double Scale, PdfSaveInfo Info)
{
    public string Summary => string.Create(CultureInfo.GetCultureInfo("ru-RU"), $"А2, М 1:{Scale:0}, точек {Points}, зон {Zones}, сетей {Networks}, {Info.Bytes / 1024.0:0} КБ");
}

/// <summary>
/// Волна 5 — лист «Чертёж градостроительного плана земельного участка (ПЗУ)» на А2 по ГОСТ Р 21.101 / 21.508: рамка и основная
/// надпись формы 3, план участка М 1:500 (1:1000, если не помещается) с сеткой МСК, границей точек 1…N и длинами сторон, пятном
/// допустимого размещения со штриховкой, цветными контурами ЗОУИТ со смещённым штрихом, коридорами сетей топосъёмки и ОКС;
/// справа — ведомость координат характерных точек, экспликация зон, условные обозначения и итог проверки посадки.
/// </summary>
public sealed partial class GostDrawingPdfExporter
{
    public static GpzuSheetResult ExportGpzuSheet(GpzuSitePlan plan, GpzuComplianceReport? report, string path, IReadOnlyList<Models.ArchitecturalElement>? building = null)
    {
        var doc = plan.Document;
        var pdf = new PdfVectorDocument(LoadFont()) { Title = "ГПЗУ " + doc.Cadastral, Author = "Architect Studio Pro" };
        const double w = 594, h = 420;
        var page = pdf.AddPage(w, h);
        DrawFrame(page, w, h, GostSheetFormat.A2);

        // Поле плана: от рамки (20 мм) до таблиц (правая колонка 205 мм), над штампом — свободно по высоте.
        double fieldX0 = 26, fieldX1 = w - 5 - 212, fieldY0 = 12, fieldY1 = h - 22;
        var (minX, maxX, minZ, maxZ) = plan.Bounds;
        double scale = 500;
        double margin = 18;
        double Need(double sc) => Math.Max((maxX - minX + 2 * margin) * 1000 / sc / (fieldX1 - fieldX0), (maxZ - minZ + 2 * margin) * 1000 / sc / (fieldY1 - fieldY0));
        if (Need(500) > 1) scale = 1000;
        double k = 1000 / scale; // мм листа на метр
        double cxm = (fieldX0 + fieldX1) / 2, cym = (fieldY0 + fieldY1) / 2;
        double mx = (minX + maxX) / 2, mz = (minZ + maxZ) / 2;
        (double X, double Y) P(SitePoint p) => (cxm + (p.X - mx) * k, cym - (p.Z - mz) * k);
        static (byte R, byte G, byte B) Rgb(string hex)
        {
            string s = hex.TrimStart('#');
            return (Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
        }
        static (byte, byte, byte) Tint((byte R, byte G, byte B) c, double t) =>
            ((byte)(255 - (255 - c.R) * t), (byte)(255 - (255 - c.G) * t), (byte)(255 - (255 - c.B) * t));
        void Stroke(string hex) { var c = Rgb(hex); page.StrokeRgb(c.R, c.G, c.B); }
        void Fill((byte R, byte G, byte B) c) => page.FillRgb(c.R, c.G, c.B);

        page.Text($"Чертёж градостроительного плана земельного участка {doc.Cadastral}   М 1:{scale:0}", fieldX0 + 2, h - 15, 5);

        // Сетка МСК-66: кресты через 20 м (М 1:500) / 50 м (М 1:1000) с подписями координат.
        double gstep = scale <= 500 ? 20 : 50;
        var corners = new[] { plan.ToGeo(mx - (cxm - fieldX0) / k, mz + (cym - fieldY0) / k), plan.ToGeo(mx + (fieldX1 - cxm) / k, mz - (fieldY1 - cym) / k) };
        double gx0 = Math.Min(corners[0].X, corners[1].X), gx1 = Math.Max(corners[0].X, corners[1].X);
        double gy0 = Math.Min(corners[0].Y, corners[1].Y), gy1 = Math.Max(corners[0].Y, corners[1].Y);
        page.LineWidth(Thin);
        page.StrokeGray(0.45);
        for (double gx = Math.Ceiling(gx0 / gstep) * gstep; gx <= gx1; gx += gstep)
            for (double gy = Math.Ceiling(gy0 / gstep) * gstep; gy <= gy1; gy += gstep)
            {
                var (px, py) = P(plan.ToScene(gx, gy));
                if (px < fieldX0 || px > fieldX1 || py < fieldY0 || py > fieldY1) continue;
                page.Line(px - 2.5, py, px + 2.5, py);
                page.Line(px, py - 2.5, px, py + 2.5);
            }
        page.FillGray(0.35);
        for (double gx = Math.Ceiling(gx0 / gstep) * gstep; gx <= gx1; gx += gstep)
        {
            var (_, py) = P(plan.ToScene(gx, gy0));
            if (py > fieldY0 + 4 && py < fieldY1 - 4) page.Text(gx.ToString("0", Ru), fieldX0 + 1, py + 0.8, 1.8);
        }
        for (double gy = Math.Ceiling(gy0 / gstep) * gstep; gy <= gy1; gy += gstep)
        {
            var (px, _) = P(plan.ToScene(gx0, gy));
            if (px > fieldX0 + 8 && px < fieldX1 - 8) page.Text(gy.ToString("0", Ru), px, fieldY0 + 1.5, 1.8, PdfTextAlign.Center, 90);
        }
        page.FillGray(0);

        // Всё содержимое плана — в отсечении полем чертежа (линии за полем не прижимаются к рамке).
        page.Save();
        page.ClipRect(fieldX0, fieldY0, fieldX1 - fieldX0, fieldY1 - fieldY0);
        // Коридоры сетей (заливка светлым оттенком, ось трассы цветом).
        foreach (var z in plan.Zones.Where(z => z.Zone.IsNetwork))
        {
            var c = Rgb(z.Zone.ColorHex);
            Fill(Tint(c, 0.28));
            foreach (var piece in z.Corridor) page.Polygon(Clip(piece.Select(P).ToList()), fill: true, stroke: false);
            Stroke(z.Zone.ColorHex);
            page.LineWidth(0.35);
            page.Dash(4, 1.2, 0.6, 1.2);
            foreach (var l in z.Lines) page.Polyline(Clip(l.Select(P).ToList()), false);
            page.Dash();
        }
        // Существующие ОКС.
        foreach (var o in plan.Existing)
        {
            Fill(Tint(Rgb(SiteBoundaryBuilder.ExistingColor), 0.55));
            page.StrokeGray(0.25);
            page.LineWidth(0.3);
            page.Polygon(o.Footprint.Select(P).ToList(), fill: true, stroke: true);
            if (o.Number > 0)
            {
                var (ox, oy) = P(new SitePoint(o.Footprint.Average(p => p.X), o.Footprint.Average(p => p.Z)));
                page.FillGray(0);
                page.Text(o.Number.ToString(Ru), ox, oy - 1.2, 3, PdfTextAlign.Center);
            }
        }
        // Пятно допустимого размещения: штриховка 45° изумрудом.
        if (plan.Zones.FirstOrDefault(z => z.Zone.Kind == GpzuZoneKind.AllowedArea) is { } allowed)
        {
            Stroke(SiteBoundaryBuilder.AllowedColor);
            page.Hatch([allowed.Polygon.Select(P).ToList()], 3.0, 45, 0.18);
        }
        // ЗОУИТ и подзоны: вложенные контуры со смещённым штрихом своего цвета.
        int ring = 0;
        foreach (var z in plan.Zones.Where(z => z.Polygon.Count >= 3 && z.Zone.Kind is GpzuZoneKind.TransportZone or GpzuZoneKind.AirfieldSubzone or GpzuZoneKind.LocalZone))
        {
            ring++;
            var poly = z.Zone.CoversWholeParcel ? SiteBoundaryBuilder.Offset(z.Polygon, 0.55 * ring * 500 / scale) : z.Polygon.ToList();
            Stroke(z.Zone.ColorHex);
            page.LineWidth(0.25);
            page.Dash(3, 1.5);
            page.Polyline(Clip(poly.Select(P).ToList()), z.Zone.CoversWholeParcel);
            page.Dash();
        }
        // Проектируемое здание: пятно застройки (контуры несущих элементов) тёмно-серой заливкой с основной линией.
        if (building is { Count: > 0 })
        {
            page.FillGray(0.55);
            page.StrokeGray(0.1);
            page.LineWidth(0.35);
            foreach (var e in building.Where(GpzuComplianceVerifier.IsBuildingPart).Where(e => e.Category is Models.BimCategory.Foundation or Models.BimCategory.Slab or Models.BimCategory.Wall or Models.BimCategory.CurtainWall or Models.BimCategory.Column))
                if (e.Category is Models.BimCategory.Foundation || e.LevelIndex == building.Min(b => b.LevelIndex))
                    page.Polygon(AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(e).Select(p => P(new SitePoint(p.X, p.Z))).ToList(), fill: true, stroke: true);
            page.FillGray(0);
        }
        // Граница участка (основная линия кобальтом), точки, номера и длины сторон.
        Stroke(SiteBoundaryBuilder.BoundaryColor);
        page.LineWidth(0.7);
        page.Polyline(plan.Boundary.Select(P).ToList(), true);
        double ccx = plan.Boundary.Average(p => p.X), ccz = plan.Boundary.Average(p => p.Z);
        var cobalt = Rgb(SiteBoundaryBuilder.BoundaryColor);
        for (int i = 0; i < plan.Boundary.Count; i++)
        {
            var b = plan.Boundary[i];
            var (px, py) = P(b);
            Fill(cobalt);
            page.Circle(px, py, 0.7, fill: true, stroke: false);
            double dx = b.X - ccx, dz = b.Z - ccz, l = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
            page.Text(plan.PointNumbers[i], px + dx / l * 3.2, py - dz / l * 3.2 - 1.1, 2.5, PdfTextAlign.Center);
        }
        page.FillGray(0.15);
        foreach (var s in plan.Segments.Where(sg => sg.Length >= 2.5))
        {
            double dx = s.Mid.X - ccx, dz = s.Mid.Z - ccz, l = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
            var (px, py) = P(new SitePoint(s.Mid.X + dx / l * 3.5 * scale / 500, s.Mid.Z + dz / l * 3.5 * scale / 500));
            page.Text(s.Length.ToString("0.00", Ru), px, py - 0.9, 1.8, PdfTextAlign.Center);
        }
        page.FillGray(0);
        // «Север».
        double nx = fieldX1 - 14, ny = fieldY1 - 22;
        page.StrokeGray(0);
        page.LineWidth(Main);
        page.Polygon([(nx, ny + 14), (nx + 4, ny), (nx, ny + 3), (nx - 4, ny)], fill: true, stroke: true);
        page.Text("С", nx, ny + 16, 3.5, PdfTextAlign.Center);
        page.Restore();

        // ── Правая колонка: ведомость координат, экспликация зон, условные обозначения, проверка ──
        double tx = w - 5 - 207, ty = h - 12;
        page.StrokeGray(0);
        page.FillGray(0);
        page.Text($"Ведомость координат характерных точек ({doc.CoordinateSystem})", tx, ty, 3);
        ty -= 3;
        double[] cols = [16, 46, 46, 26];
        string[] heads = ["№", "X, м", "Y, м", "L, м"];
        double rowH = 5;
        TableRow(page, tx, ty, cols, heads, rowH, header: true);
        ty -= rowH;
        for (int i = 0; i < doc.Boundary.Count; i++)
        {
            var p = doc.Boundary[i];
            double len = Geodesy.Length(p, doc.Boundary[(i + 1) % doc.Boundary.Count]);
            TableRow(page, tx, ty, cols, [p.Number, p.X.ToString("0.00", Ru), p.Y.ToString("0.00", Ru), len.ToString("0.00", Ru)], rowH, header: false);
            ty -= rowH;
        }
        page.Text(string.Create(Ru, $"Площадь по координатам (Гаусс) {doc.GaussArea:0.00} м²; по ГПЗУ {doc.DeclaredArea:0} м²"), tx, ty - 3.5, 2.5);
        ty -= 10;

        page.Text("Экспликация зон с особыми условиями и сетей", tx, ty, 3);
        ty -= 3;
        double zoneRow = 5.4;
        page.LineWidth(Thin);
        int idx = 0;
        foreach (var z in plan.Zones)
        {
            if (ty < 70) break;
            idx++;
            var c = Rgb(z.Zone.ColorHex);
            page.FillRgb(c.R, c.G, c.B);
            page.Rect(tx + 1, ty - zoneRow + 1.4, 6, 2.6, fill: true, stroke: false);
            page.FillGray(0);
            page.Text(idx.ToString(Ru), tx + 10, ty - zoneRow + 1.6, 2.2, PdfTextAlign.Center);
            string label = z.Zone.Name + (z.Zone.HeightLimitMin.HasValue ? " · " + z.Zone.HeightText(Ru) : string.Empty);
            FitLeft(page, label, tx + 14, ty - zoneRow + 1.6, 2.1, 190);
            ty -= zoneRow;
        }
        if (idx < plan.Zones.Count) page.Text($"… ещё {plan.Zones.Count - idx} (см. студию ГПЗУ)", tx + 14, ty - 3, 2.1);
        ty -= 6;

        // Итог проверки посадки (над штампом).
        if (report is not null && ty > 68)
        {
            page.Text($"Проверка посадки: {(report.Passed ? "соответствует ГПЗУ" : $"нарушений {report.Critical}")}", tx, ty, 2.8);
            ty -= 4.5;
            foreach (var f in report.Findings.Where(f => f.Severity != GpzuSeverity.Info).Take(5))
            {
                if (ty < 64) break;
                FitLeft(page, (f.Severity == GpzuSeverity.Ok ? "+ " : "! ") + f.Title, tx + 2, ty, 2.1, 200);
                ty -= 3.8;
            }
        }

        // Условные обозначения — в левом нижнем углу поля плана.
        double lx = fieldX0 + 2, ly = fieldY0 + 6;
        var legend = new List<(string Hex, string Text, int Kind)> { (SiteBoundaryBuilder.BoundaryColor, "Граница земельного участка ГПЗУ", 0), (SiteBoundaryBuilder.AllowedColor, "Место допустимого размещения ОКС", 1) };
        foreach (var st in SiteBoundaryBuilder.NetworkStyles.Where(st => plan.Zones.Any(z => z.Zone.Kind == st.Kind)).DistinctBy(st => st.Title))
            legend.Add((st.ColorHex, string.Create(Ru, $"Охранная зона: {st.Title} ± {st.HalfWidth:0.#} м"), 2));
        legend.Add(("#EC4899", "ЗОУИТ / подзоны ПАТ (смещённый штрих)", 3));
        if (plan.Existing.Count > 0) legend.Add((SiteBoundaryBuilder.ExistingColor, "Существующие ОКС", 2));
        if (building is { Count: > 0 }) legend.Add(("#8C8C8C", "Проектируемое здание (пятно застройки)", 2));
        page.FillGray(1);
        page.StrokeGray(0);
        page.LineWidth(Thin);
        page.Rect(lx - 1, ly - 2, 92, legend.Count * 4.6 + 7, fill: true, stroke: true);
        page.FillGray(0);
        page.Text("Условные обозначения", lx + 1, ly + legend.Count * 4.6 + 1.2, 2.5);
        for (int i = 0; i < legend.Count; i++)
        {
            var (hex, text, kind) = legend[legend.Count - 1 - i];
            double y0 = ly + i * 4.6;
            var c = Rgb(hex);
            page.StrokeRgb(c.R, c.G, c.B);
            if (kind == 0) { page.LineWidth(0.7); page.Line(lx + 1, y0 + 1, lx + 11, y0 + 1); }
            else if (kind == 3) { page.LineWidth(0.3); page.Dash(2, 1); page.Line(lx + 1, y0 + 1, lx + 11, y0 + 1); page.Dash(); }
            else { Fill(Tint(c, kind == 1 ? 0.35 : 0.45)); page.Rect(lx + 1, y0 - 0.3, 10, 2.6, fill: true, stroke: true); }
            page.FillGray(0);
            page.Text(text, lx + 14, y0, 2.1);
        }

        page.StrokeGray(0);
        page.FillGray(0);
        DrawStamp(page, w - 5 - StampW, 5, "ГПЗУ " + doc.Number, $"Земельный участок {doc.Cadastral}, {doc.Address}",
            $"Зона {doc.Regulation.ZoneCode}, подзона {doc.Regulation.Subzone}", $"Чертёж градостроительного плана (ПЗУ) М 1:{scale:0}");
        var info = pdf.Save(path);
        return new GpzuSheetResult(path, doc.Boundary.Count, plan.Zones.Count, plan.Zones.Count(z => z.Zone.IsNetwork), scale, info);

        static List<(double X, double Y)> Clip(List<(double X, double Y)> pts) => pts;
    }

    private static void TableRow(PdfVectorPage page, double x, double top, double[] cols, string[] cells, double rowH, bool header)
    {
        page.LineWidth(header ? Main : Thin);
        page.Rect(x, top - rowH, cols.Sum(), rowH);
        double cx = x;
        for (int i = 0; i < cols.Length; i++)
        {
            if (i > 0) page.Line(cx, top - rowH, cx, top);
            // Числа — по правому краю (разряды в столбик), заголовки — по центру.
            if (header || i == 0) page.Text(cells[i], cx + cols[i] / 2, top - rowH + 1.6, 2.2, PdfTextAlign.Center);
            else page.Text(cells[i], cx + cols[i] - 1.5, top - rowH + 1.6, 2.2, PdfTextAlign.Right);
            cx += cols[i];
        }
    }
}
