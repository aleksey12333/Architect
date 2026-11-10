#if DEBUG
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.Services.Pdf;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест Волны 5 (ГПЗУ и охранные зоны) на реальных документах DemoKit/GPZU: разбор ГПЗУ № РФ-66-3-02-0-00-2024-2185-0
/// (19 точек, площадь Гаусса, реквизиты, регламент подзоны 45.4, 7 ЗОУИТ 66:00-6.*, 14 таблиц подзон ПАТ, 6 ОКС), склеенные ячейки
/// номеров на синтетической таблице, ориентация «Север вверх / Восток вправо», топосъёмка 938-01.24-ИГДИ-Г.3.dwg и коридоры сетей,
/// перенос участка в сцену одним шагом журнала, проверка посадки (граница, сеть, высота ПАТ, жилая площадь), лист ГПЗУ PDF/DXF,
/// форматы XML/JSON/CSV, экспертиза через модель (заглушка шлюза; живой вызов — если ключ доступен), строки.
/// </summary>
public static partial class CadSelfTestService
{
    private static readonly CultureInfo W5Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static async Task<string> RunWave5OnlyAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        int failures = await CheckWave5Async(directory, report);
        report.Insert(0, failures == 0 ? "ИТОГ: УСПЕХ\n" : $"ИТОГ: ОШИБОК {failures}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "wave5.txt"), report.ToString(), new UTF8Encoding(true));
        return report.ToString();
    }

    private static readonly (string N, double X, double Y)[] GpzuTruth =
    [
        ("1", 387509.92, 1532936.30), ("2", 387526.19, 1532947.55), ("3", 387513.41, 1532965.08), ("4", 387504.18, 1532978.36), ("5", 387502.16, 1532976.86),
        ("6", 387495.35, 1532986.84), ("7", 387488.86, 1532996.37), ("8", 387491.86, 1532998.06), ("9", 387486.05, 1533006.52), ("10", 387419.18, 1532961.28),
        ("11", 387425.45, 1532952.06), ("12", 387428.19, 1532954.00), ("13", 387439.38, 1532937.56), ("14", 387450.61, 1532919.74), ("15", 387461.34, 1532903.68),
        ("16", 387461.96, 1532904.10), ("17", 387462.12, 1532903.87), ("18", 387476.43, 1532913.58), ("19", 387492.73, 1532924.64)
    ];

    private static async Task<int> CheckWave5Async(string directory, StringBuilder report)
    {
        int failures = 0;
        string kit = Path.Combine(AppContext.BaseDirectory, "DemoKit", "GPZU");
        string? pdfPath = Directory.Exists(kit) ? Directory.GetFiles(kit, "*.pdf").FirstOrDefault() : null;
        string? dwgPath = Directory.Exists(kit) ? Directory.GetFiles(kit, "*.dwg").FirstOrDefault() : null;
        if (pdfPath is null || dwgPath is null)
        {
            Check(report, ref failures, false, $"DemoKit/GPZU рядом с программой: ГПЗУ PDF и топосъёмка DWG ({kit})");
            return failures;
        }

        // 1. Реквизиты, 19 точек и площадь Гаусса.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var doc = await Task.Run(() => GpzuParser.Load(pdfPath));
        double parseMs = sw.Elapsed.TotalMilliseconds;
        bool points = doc.Boundary.Count == 19 && doc.Boundary.Zip(GpzuTruth).All(t => t.First.Number == t.Second.N && Math.Abs(t.First.X - t.Second.X) < 0.005 && Math.Abs(t.First.Y - t.Second.Y) < 0.005);
        double gauss = doc.GaussArea;
        bool area = doc.DeclaredArea == 5498 && Math.Abs(gauss - 5497.84) < 0.01 && Math.Abs(Math.Round(gauss) - doc.DeclaredArea) < 0.5;
        bool req = doc.Number == "РФ-66-3-02-0-00-2024-2185-0" && doc.IssueDate == "13.08.2024" && doc.Cadastral == "66:41:0403044:148" &&
                   doc.Applicant.Contains("Свобода Мысли", StringComparison.Ordinal) && doc.Application.Contains("12299/003/21/001", StringComparison.Ordinal) &&
                   doc.Application.Contains("30.07.2024", StringComparison.Ordinal) && doc.Address.Contains("Депутатская", StringComparison.Ordinal);
        var reg = doc.Regulation;
        bool regulation = reg.ZoneCode == "Ж-5" && reg.Subzone == "45.4" && reg.MaxLivingArea == 20810 && reg.MinParkingPlaces == 130 && reg.MinParcelArea == 3000 &&
                          reg.PermittedUses.Contains("Многоэтажная жилая застройка (высотная застройка)");
        Check(report, ref failures, points && area && req && regulation,
            string.Create(W5Ru, $"ГПЗУ PDF ({doc.PageCount} стр., текстовый слой, {parseMs:0} мс): № {doc.Number} от {doc.IssueDate}, {doc.Cadastral}, заявитель «{doc.Applicant}» {doc.Application}; ") +
            string.Create(W5Ru, $"19 характерных точек МСК-66 = Приложение № 1 до 0,01 м [{points}]; площадь Гаусса {gauss:0.00} м² ≈ паспорт {doc.DeclaredArea:0} м² (Δ {gauss - doc.DeclaredArea:+0.00;-0.00}) [{area}]; ") +
            $"реквизиты [{req}]; регламент {reg.ZoneCode}, подзона {reg.Subzone}: жилья ≤ {reg.MaxLivingArea:N0} м², машино-мест ≥ {reg.MinParkingPlaces}, участок ≥ {reg.MinParcelArea:N0} м², ВРИ {reg.PermittedUses.Count} [{regulation}]");

        // 2. Таблицы Приложения № 1: граница + 14 таблиц подзон, все по 19 точек, переносы со страницы на страницу.
        var zoneTables = doc.Tables.Skip(1).ToList();
        int split = doc.Tables.Count(t => t.LastPage > t.FirstPage);
        bool tables = doc.Tables.Count == 15 && zoneTables.All(t => t.PointCount == 19 && t.SameAsBoundary) && split >= 4;
        Check(report, ref failures, tables,
            $"таблицы координат: {doc.Tables.Count} (граница + {zoneTables.Count} подзон ПАТ), у каждой 19 точек = граница [{zoneTables.All(t => t.PointCount == 19 && t.SameAsBoundary)}], " +
            $"продолжение на следующей странице — {split} таблиц (номера 13…19 вверху страницы приклеены к своей зоне)");

        // 3. Склеенные ячейки номеров («4/5», «13/14» в одной ячейке между строками) на синтетической таблице.
        var tokens = new List<GpzuToken>();
        var lines = new List<GpzuTextLine>();
        const double h = 9, step = 12.1;
        for (int i = 0; i < 19; i++)
        {
            double y = 700 - i * step;
            var t = GpzuTruth[i];
            tokens.Add(new GpzuToken(0, 300, y, h, t.X.ToString("0.00", W5Ru)));
            tokens.Add(new GpzuToken(0, 380, y, h, t.Y.ToString("0.00", W5Ru)));
            // Склейка: «4» и «5», «13» и «14» — в одной ячейке, по вертикали между своими строками (смещение 0,6 кегля).
            double shift = t.N is "4" or "13" ? -h * 0.6 : t.N is "5" or "14" ? h * 0.6 : 0;
            tokens.Add(new GpzuToken(0, 252, y + shift, h, t.N));
            lines.Add(new GpzuTextLine(0, y, 252, $"{t.N} {t.X.ToString("0.00", W5Ru)} {t.Y.ToString("0.00", W5Ru)}"));
        }
        var merged = GpzuParser.FromLines(lines, tokens);
        bool mergedOk = merged.Boundary.Count == 19 && merged.Boundary.Select(p => p.Number).SequenceEqual(GpzuTruth.Select(t => t.N)) && merged.Tables[0].HadMergedCells;
        Check(report, ref failures, mergedOk,
            $"склеенные ячейки номеров «4/5» и «13/14» (номер не на базовой линии строки): {merged.Boundary.Count} точек, порядок 1…19 сохранён [{mergedOk}], признак склейки {merged.Tables.FirstOrDefault()?.HadMergedCells}");

        // 4. Топосъёмка DWG: копия в МСК у участка, сети по видам, отметки земли, коридоры.
        sw.Restart();
        var topo = await Task.Run(() => TopoSurveyReader.Read(dwgPath, doc.Boundary));
        double dwgMs = sw.Elapsed.TotalMilliseconds;
        SiteBoundaryBuilder.Merge(doc, topo);
        var kinds = topo.Networks.Select(n => n.Kind).Distinct().ToList();
        var corridorZones = doc.Zones.Where(z => z.IsNetwork).ToList();
        bool corridorWidths = corridorZones.All(z => z.CorridorHalfWidth == SiteBoundaryBuilder.StyleOf(z.Kind).HalfWidth && z.CorridorHalfWidth is >= 1 and <= 5 && z.Lines.Count > 0);
        bool topoOk = topo.Version == "AC1032" && kinds.Count >= 5 && topo.Networks.Count(n => n.Kind == GpzuZoneKind.GasPipeline) > 0 &&
                      doc.GroundElevation is > 255 and < 270 && corridorZones.Count >= 3 && corridorWidths && topo.Buildings.Count > 0;
        Check(report, ref failures, topoOk,
            string.Create(W5Ru, $"топосъёмка {Path.GetFileName(dwgPath)} ({topo.Version}, {topo.EntityCount} примитивов, развёрнуто вставок {topo.FlattenedCount}, {dwgMs:0} мс): ") +
            string.Join(", ", topo.Networks.GroupBy(n => n.Kind).Select(g => string.Create(W5Ru, $"{SiteBoundaryBuilder.StyleOf(g.Key).Title} {g.Count()} тр. {g.Sum(n => n.Length):N0} м"))) +
            string.Create(W5Ru, $"; зданий {topo.Buildings.Count}, отметок {topo.Elevations.Count}, земля {doc.GroundElevation:0.00} м БС; коридоров у участка {corridorZones.Count} (± {string.Join("/", corridorZones.Select(z => z.CorridorHalfWidth.ToString("0.#", W5Ru)))} м) [{corridorWidths}]"));

        // 5. ЗОУИТ 66:00-6.* (раздел 5.1 ↔ таблицы ЕГРН ↔ экспликация 14…20) и подзоны ПАТ с ограничением высоты.
        var expected = new Dictionary<string, int> { ["66:00-6.1909"] = 14, ["66:00-6.2651"] = 15, ["66:00-6.1907"] = 16, ["66:00-6.1908"] = 17, ["66:00-6.1915"] = 18, ["66:00-6.1910"] = 19, ["66:00-6.2648"] = 20 };
        var transport = doc.Zones.Where(z => z.Kind == GpzuZoneKind.TransportZone).ToList();
        bool zouit = transport.Count == 7 && expected.All(e => transport.Any(z => z.Code == e.Key && z.ExplicationNo == e.Value && z.Polygon.Count == 19 && z.Restriction.Length > 0));
        var subzones = doc.Zones.Where(z => z.Kind == GpzuZoneKind.AirfieldSubzone).ToList();
        bool heights = subzones.Any(z => z.HeightLimitMin == 383.50) && subzones.Any(z => z.HeightLimitMin == 365.00 && z.HeightLimitMax == 367.40) &&
                       subzones.Any(z => z.HeightLimitMin == 423.58) && doc.CeilingAbs == 365.00;
        Check(report, ref failures, zouit && heights,
            $"ЗОУИТ транспорта: {transport.Count} — {string.Join(", ", transport.Select(z => $"{z.Code} (п. {z.ExplicationNo})"))}, у каждой контур 19 точек и ограничения [{zouit}]; " +
            string.Create(W5Ru, $"подзоны ПАТ: {subzones.Count}, высоты {string.Join(" / ", subzones.Where(z => z.HeightLimitMin.HasValue).Select(z => z.HeightText(W5Ru)))}; потолок {doc.CeilingAbs:0.00} м БС → над землёй {doc.CeilingAbs - doc.GroundElevation:0.0} м [{heights}]"));

        // 6. ОКС раздела 3.1.
        bool oks = doc.Objects.Select(o => o.Number).SequenceEqual([9, 10, 11, 12, 13, 21]) && doc.Objects[0].Cadastral == "66:41:0000000:86963";
        Check(report, ref failures, oks, $"ОКС раздела 3.1: {string.Join(", ", doc.Objects.Select(o => "№ " + o.Number + (o.Cadastral.Length > 0 ? " " + o.Cadastral : string.Empty)))} [{oks}]");

        // 7. Ориентация МСК → сцена: восток +X, север −Z; обратный перевод; площадь в сцене.
        var plan = SiteBoundaryBuilder.Build(doc, topo);
        int Idx(string n) => doc.Boundary.FindIndex(p => p.Number == n);
        var b = plan.Boundary;
        bool north = b.All(p => p.Z >= b[Idx("2")].Z - 1e-9), east = b.All(p => p.X <= b[Idx("9")].X + 1e-9), south = b.All(p => p.Z <= b[Idx("10")].Z + 1e-9);
        bool west = new[] { "15", "16", "17" }.Contains(doc.Boundary[b.Select((p, i) => (p, i)).OrderBy(t => t.p.X).First().i].Number);
        var back = plan.ToGeo(b[0].X, b[0].Z);
        double sceneArea = GpzuComplianceVerifier.Area(b.Select(p => (p.X, p.Z)).ToList());
        bool orient = north && east && south && west && Math.Abs(back.X - doc.Boundary[0].X) < 1e-6 && Math.Abs(back.Y - doc.Boundary[0].Y) < 1e-6 && Math.Abs(sceneArea - gauss) < 0.01;
        Check(report, ref failures, orient,
            string.Create(W5Ru, $"ориентация: точка 2 — север (Z min) [{north}], 9 — восток (X max) [{east}], 10 — юг (Z max) [{south}], 15–17 — запад [{west}]; начало (X₀ {plan.OriginX:0.00}; Y₀ {plan.OriginY:0.00}), ") +
            string.Create(W5Ru, $"обратный перевод точки 1 до 1e-6 м, площадь в сцене {sceneArea:0.00} м² = Гаусс; сторон {plan.Segments.Count}, сторона 9–10 = {plan.Segments[8].Length:0.00} м"));

        // 8. Перенос участка в сцену — один шаг журнала, Undo/Redo; посадка здания подбором сдвига.
        var scene = new SceneStateService();
        var demo = PresetScenesService.Create("frame");
        var placement = GpzuComplianceVerifier.FindPlacement(plan, demo.Elements);
        if (placement is { } off) foreach (var e in demo.Elements) e.Position = [e.X + off.Dx, e.Y, e.Z + off.Dz];
        scene.LoadScene(demo.Elements, demo.Groups, recordUndo: false, clearHistory: true, axes: []);
        int depth = scene.UndoDepth;
        int elementsBefore = scene.Elements.Count;
        scene.ApplySitePlan(plan);
        bool oneStep = scene.UndoDepth == depth + 1 && ReferenceEquals(scene.SitePlan, plan) && scene.Elements.Count == elementsBefore;
        scene.Undo();
        bool undone = scene.SitePlan is null && scene.Elements.Count == elementsBefore;
        scene.Redo();
        bool redone = ReferenceEquals(scene.SitePlan, plan);
        var ok = GpzuComplianceVerifier.Verify(plan, scene.Elements.ToList());
        // Сети топосъёмки проходят по участку (ГПЗУ, п. 5.1: «размещение возможно при условии переустройства (выноса) сетей») —
        // замечания по коридорам здесь ожидаемы; граница, высота ПАТ и жилая площадь должны соблюдаться.
        var netHits = ok.Findings.Where(f => f.Code.StartsWith("GPZU-NET", StringComparison.Ordinal) && f.Severity == GpzuSeverity.Critical).Select(f => f.Title).ToList();
        bool placed = placement is not null && ok.Findings.Single(f => f.Code == "GPZU-BOUNDARY").Severity == GpzuSeverity.Ok &&
                      ok.Findings.Single(f => f.Code == "GPZU-HEIGHT").Severity == GpzuSeverity.Ok && ok.Findings.Single(f => f.Code == "GPZU-LIVING").Severity == GpzuSeverity.Ok &&
                      ok.Findings.Count(f => f.Code.StartsWith("GPZU-NET", StringComparison.Ordinal)) == plan.Zones.Count(z => z.Zone.IsNetwork && z.Lines.Count > 0);
        Check(report, ref failures, oneStep && undone && redone && placed,
            $"участок в сцене: «{scene.Transactions.PeekUndo()?.Name ?? "шаг"}» — ровно 1 шаг журнала [{oneStep}], Undo → без участка [{undone}], Redo → тот же план [{redone}]; " +
            string.Create(W5Ru, $"каркасный корпус посажен сдвигом ({placement?.Dx:0.0}; {placement?.Dz:0.0}) м с запасом до коридоров {placement?.Clearance:0.0} м: граница, сети, высота {ok.MaxHeight:0.0} м (верх {ok.TopAbs:0.00} м БС), ") +
            string.Create(W5Ru, $"жильё {ok.LivingAreaEstimate:N0} м² — в пределах; коридоры сетей под пятном: {(netHits.Count == 0 ? "нет" : string.Join(", ", netHits))} [{placed}]"));

        // 9. Нарушения: выход за границу, коридор сети, превышение потолка ПАТ, лимит жилой площади.
        var moved = demo.Elements.Select(e => e.Clone()).ToList();
        foreach (var e in moved) e.Position = [e.X + 70, e.Y, e.Z];
        var outRep = GpzuComplianceVerifier.Verify(plan, moved);
        var net = plan.Zones.First(z => z.Zone.IsNetwork && z.Lines.Count > 0);
        var onNet = demo.Elements.Select(e => e.Clone()).ToList();
        var lp = net.Lines[0][0];
        double cx0 = onNet.Average(e => e.X), cz0 = onNet.Average(e => e.Z);
        foreach (var e in onNet) e.Position = [e.X - cx0 + lp.X, e.Y, e.Z - cz0 + lp.Z];
        var netRep = GpzuComplianceVerifier.Verify(plan, onNet);
        var tall = demo.Elements.Select(e => e.Clone()).ToList();
        var column = tall.First(e => e.Category == BimCategory.Column);
        column.SetSize(column.Width, 140, column.Depth);
        column.Position = [column.X, 70, column.Z];
        var tallRep = GpzuComplianceVerifier.Verify(plan, tall);
        var heavy = demo.Elements.Select(e => e.Clone()).ToList();
        foreach (var slab in heavy.Where(e => e.Category == BimCategory.Slab)) slab.RepeatLevels = 40;
        var heavyRep = GpzuComplianceVerifier.Verify(plan, heavy);
        bool violations = outRep.Findings.Single(f => f.Code == "GPZU-BOUNDARY").Severity == GpzuSeverity.Critical &&
                          netRep.Findings.Any(f => f.Code == "GPZU-NET-" + net.Zone.Kind && f.Severity == GpzuSeverity.Critical) &&
                          tallRep.Findings.Single(f => f.Code == "GPZU-HEIGHT").Severity == GpzuSeverity.Critical &&
                          heavyRep.Findings.Single(f => f.Code == "GPZU-LIVING").Severity == GpzuSeverity.Critical;
        Check(report, ref failures, violations,
            $"нарушения ловятся: сдвиг на 70 м — «{outRep.Findings.Single(f => f.Code == "GPZU-BOUNDARY").Detail}»; на трассе ({SiteBoundaryBuilder.StyleOf(net.Zone.Kind).Title}) — критично; " +
            string.Create(W5Ru, $"колонна до {tallRep.MaxHeight:0} м — верх {tallRep.TopAbs:0.00} > {plan.CeilingAbs:0.00} м БС; перекрытия ×40 — жильё {heavyRep.LivingAreaEstimate:N0} > {reg.MaxLivingArea:N0} м² [{violations}]"));

        // 10. Лист ГПЗУ / ПЗУ: PDF (векторный, цветной) и DXF R12 — читаются обратно.
        string sheet = Path.Combine(directory, "wave5_gpzu_sheet.pdf");
        var sheetRes = await Task.Run(() => GostDrawingPdfExporter.ExportGpzuSheet(plan, ok, sheet, scene.Elements.ToList()));
        var read = PdfNativeContentExtractor.Read(sheet);
        string sheetText = string.Join(" ", read.Pages.SelectMany(p => p.Runs).Select(r => r.Text));
        bool sheetNumbers = GpzuTruth.All(t => sheetText.Contains(t.X.ToString("0.00", W5Ru), StringComparison.Ordinal) && sheetText.Contains(t.Y.ToString("0.00", W5Ru), StringComparison.Ordinal));
        bool sheetOk = sheetNumbers && sheetText.Contains("Условные обозначения", StringComparison.Ordinal) && sheetText.Contains("Ведомость координат", StringComparison.Ordinal) &&
                       sheetText.Contains("66:00-6.1909", StringComparison.Ordinal) && sheetRes.Info.Bytes < 400_000;
        string dxf = Path.Combine(directory, "wave5_gpzu.dxf");
        string dxfInfo = GpzuDxfExporter.Export(plan, dxf);
        var dxfDoc = await Task.Run(() => TopoSurveyReader.Read(dxf));
        string dxfText = await File.ReadAllTextAsync(dxf);
        int boundaryVertices = Regex.Matches(dxfText, @"VERTEX\n8\nGPZU_BOUNDARY\n").Count;
        bool dxfOk = boundaryVertices == 19 && dxfDoc.Networks.Count > 0 && dxfText.Contains("NET_GAS", StringComparison.Ordinal) && dxfText.Contains("1532936.3", StringComparison.Ordinal);
        Check(report, ref failures, sheetOk && dxfOk,
            $"лист ГПЗУ / ПЗУ: {sheetRes.Summary}; обратное чтение PDF — все 19 пар X/Y, «Ведомость координат», «Условные обозначения», ЗОУИТ [{sheetOk}]; {dxfInfo}, граница — 19 вершин в осях МСК, при обратном чтении слои NET_* распознаны как сети ({dxfDoc.Networks.Count} трасс) [{dxfOk}]");

        // 11. Универсальные форматы: CSV/TXT, XML (ordinate), JSON (собственный формат) — тот же контур и площадь.
        string csv = string.Join("\n", GpzuTruth.Select(t => $"{t.N};{t.X.ToString("0.00", W5Ru)};{t.Y.ToString("0.00", W5Ru)}")) + "\n1;387509,92;1532936,30\n";
        var fromCsv = GpzuParser.FromPlainText("Номер;X;Y\n" + csv);
        string xml = "<?xml version=\"1.0\"?><extract><parcel cad_number=\"66:41:0403044:148\"><area><value>5498</value></area><contour>" +
                     string.Concat(GpzuTruth.Select(t => $"<ordinate num_geopoint=\"{t.N}\" x=\"{t.X.ToString(CultureInfo.InvariantCulture)}\" y=\"{t.Y.ToString(CultureInfo.InvariantCulture)}\"/>")) +
                     "</contour></parcel></extract>";
        var fromXml = GpzuParser.FromXml(xml);
        var fromJson = GpzuParser.FromJson(GpzuParser.Serialize(doc));
        bool formats = Geodesy.SameRing(fromCsv.Boundary, doc.Boundary) && Geodesy.SameRing(fromXml.Boundary, doc.Boundary) && fromXml.Cadastral == doc.Cadastral && fromXml.DeclaredArea == 5498 &&
                       Geodesy.SameRing(fromJson.Boundary, doc.Boundary) && fromJson.Zones.Count == doc.Zones.Count && fromJson.Regulation.MaxLivingArea == 20810;
        Check(report, ref failures, formats,
            $"форматы: CSV «№;X;Y» с запятыми — {fromCsv.Boundary.Count} т., XML (ordinate x/y, num_geopoint) — {fromXml.Boundary.Count} т. и {fromXml.Cadastral}, JSON (документ целиком) — {fromJson.Zones.Count} зон; контур и площадь совпадают [{formats}]");

        // 12. Экспертиза через модель: заглушка шлюза (запрос несёт регламент, высоты и итоги проверки), офлайн-заключение; живой вызов — при ключе.
        string? seenPrompt = null;
        var stub = new GatewayStub((model, root, n) =>
        {
            seenPrompt = root.GetProperty("messages")[1].GetProperty("content").GetString()?.Replace(' ', ' ');
            return (HttpStatusCode.OK, ChatReply("• Высота ограничена 60 Сектором IV подзоны: Hабс 365,00 м БС.\n• Газопровод: вынос или ТУ.", 2100, 90));
        });
        using var client = new UnifiedCloudAiClient(new UnifiedGatewayConfig("https://gateway.test/api", W41Key), stub);
        var expert = await GpzuExpertiseService.AskAsync(client, UnifiedCloudAiClient.Sonnet55.Alias, doc, topo, ok, TimeSpan.FromSeconds(20));
        var offline = GpzuExpertiseService.AskAsync(null, "x", doc, topo, ok, TimeSpan.FromSeconds(1)).Result;
        bool promptOk = seenPrompt is not null && seenPrompt.Contains("66:41:0403044:148", StringComparison.Ordinal) && seenPrompt.Contains("365,00", StringComparison.Ordinal) &&
                        seenPrompt.Contains("20 810", StringComparison.Ordinal) && seenPrompt.Contains("ПРОВЕРКА ПОСАДКИ", StringComparison.Ordinal);
        bool expertOk = promptOk && !expert.Offline && expert.Text.Contains("365,00", StringComparison.Ordinal) && offline.Offline && offline.Text.Contains("365,00", StringComparison.Ordinal);
        string live = "живой вызов: ключ шлюза не найден (офлайн)";
        if (UnifiedGatewayConfig.Discover() is { } cfg && Environment.GetEnvironmentVariable("AIA_DEBUG_W5LIVE") != "0")
        {
            using var real = new UnifiedCloudAiClient(cfg);
            var r = await GpzuExpertiseService.AskAsync(real, UnifiedCloudAiClient.Sonnet55.Alias, doc, topo, ok, TimeSpan.FromSeconds(150));
            live = r.Offline
                ? $"живой вызов не удался ({r.Error}) — офлайн-заключение ядра"
                : string.Create(W5Ru, $"живой вызов {cfg.Source}: {r.Model}, {r.Seconds:0} с, токенов {r.InputTokens:N0} → {r.OutputTokens:N0}, ответ {r.Text.Length} симв. («{Regex.Replace(r.Text, @"\s+", " ")[..Math.Min(90, r.Text.Length)]}…»)");
            if (live.Contains(cfg.ApiKey, StringComparison.Ordinal)) live = "живой вызов: в тексте обнаружен ключ — скрыт";
        }
        Check(report, ref failures, expertOk,
            $"экспертиза ГПЗУ через модель: промпт несёт кадастровый номер, Hабс 365,00, лимит 20 810 м² и итоги проверки [{promptOk}], ответ заглушки шлюза разобран; без шлюза — офлайн-заключение ядра [{offline.Offline}]; {live}");

        // 13. Строки: ключи W5 на двух языках, без эмодзи.
        var dict = typeof(LocalizationService).GetField("Wave5Strings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) as Dictionary<string, (string Ru, string En)>;
        bool strings = dict is { Count: > 50 } && dict.All(kv => kv.Value.Ru.Length > 0 && kv.Value.En.Length > 0 && !kv.Value.Ru.Any(c => char.IsSurrogate(c)) && !kv.Value.En.Any(c => char.IsSurrogate(c)));
        Check(report, ref failures, strings, $"строки Волны 5: {dict?.Count} ключей W5*, RU/EN заполнены, без эмодзи [{strings}]");
        return failures;
    }
}
#endif
