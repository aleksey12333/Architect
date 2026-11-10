#if DEBUG
using System.Globalization;
using System.Net;
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.Gpzu;
using AIArchitectStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест Волны 6: живой участок (перенос поворотной точки № 2 на +5 м — площадь Гаусса, стороны, рельеф, проверка посадки,
/// ровно один шаг журнала и Undo/Redo), несколько строений на участке, глубокое чтение «НО» нестандартного текстового ГПЗУ
/// (правила ядра + модель-заглушка со сверкой цитат), отклонение ошибочного пункта, повторный уточнённый поиск и ручная правка,
/// применение подтверждённых правил к проверке посадки, веб-поиск (заглушка сети + база нормативов + синтез), три Среды над одним
/// проектом, автосохранение и восстановление сессии, защита выхода, физический проход (стена, дверь, лестница 0,00 → +3,00).
/// </summary>
public static partial class CadSelfTestService
{
    private static readonly CultureInfo W6Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static async Task<string> RunWave6OnlyAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        int failures = await CheckWave6Async(directory, report);
        report.Insert(0, failures == 0 ? "ИТОГ: УСПЕХ\n" : $"ИТОГ: ОШИБОК {failures}\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "wave6.txt"), report.ToString(), new UTF8Encoding(true));
        return report.ToString();
    }

    /// <summary>Нестандартный текстовый ГПЗУ со скрытыми условиями в тексте, сносках и примечаниях (страницы — маркерами).</summary>
    public const string W6HiddenConditionsText = """
        === СТРАНИЦА 1 ===
        ГРАДОСТРОИТЕЛЬНЫЙ ПЛАН ЗЕМЕЛЬНОГО УЧАСТКА № РФ-66-3-02-0-00-2026-0006
        Земельный участок 66:41:0403044:999, г. Екатеринбург.
        === СТРАНИЦА 3 ===
        2.2. Предельные параметры разрешённого строительства. Минимальный отступ от границ земельного участка — 3 м*.
        Предельная высота зданий не более 75 м. Общая площадь жилых помещений не более 18 500 кв. м.
        Количество машино-мест — не менее 140 машино-мест в границах участка.
        === СТРАНИЦА 5 ===
        5.1. Участок полностью расположен в приаэродромной территории аэродрома Кольцово. Размещение объектов выше Hабс = 365,00 м подлежит согласованию с оператором аэродрома и Росавиацией.
        По участку проходит газопровод среднего давления. Размещение зданий возможно при условии выноса (переустройства) газопровода по техническим условиям газораспределительной организации, охранная зона 2 м.
        Участок частично расположен в санитарно-защитной зоне котельной 50 м.
        * Отступ не распространяется на подземную часть; со стороны ул. Депутатской отступ составляет 6 м.
        ** Строительство выше 50 м возможно только при разработке специальных технических условий (СТУ).
        """;

    private static async Task<int> CheckWave6Async(string directory, StringBuilder report)
    {
        int failures = 0;
        string kit = Path.Combine(AppContext.BaseDirectory, "DemoKit", "GPZU");
        string? pdfPath = Directory.Exists(kit) ? Directory.GetFiles(kit, "*.pdf").FirstOrDefault() : null;
        string? dwgPath = Directory.Exists(kit) ? Directory.GetFiles(kit, "*.dwg").FirstOrDefault() : null;
        if (pdfPath is null || dwgPath is null)
        {
            Check(report, ref failures, false, $"DemoKit/GPZU рядом с программой ({kit})");
            return failures;
        }
        var doc = await Task.Run(() => GpzuParser.Load(pdfPath));
        var topo = await Task.Run(() => TopoSurveyReader.Read(dwgPath, doc.Boundary));
        SiteBoundaryBuilder.Merge(doc, topo);
        var plan = SiteBoundaryBuilder.Build(doc, topo);

        // ── 1. Живой участок: точка № 2 на +5 м к северу — площадь Гаусса, стороны, рельеф, посадка, один шаг журнала, Undo/Redo.
        var scene = new SceneStateService();
        var demo = PresetScenesService.Create("frame");
        if (GpzuComplianceVerifier.FindPlacement(plan, demo.Elements) is { } off) foreach (var e in demo.Elements) e.Position = [e.X + off.Dx, e.Y, e.Z + off.Dz];
        scene.LoadScene(demo.Elements, demo.Groups, recordUndo: false, clearHistory: true, axes: []);
        scene.ApplySitePlan(plan);
        var before = EditableSiteTerrainService.Analyze(plan, scene.Elements.ToList());
        int i2 = doc.Boundary.FindIndex(p => p.Number == "2");
        var p2 = doc.Boundary[i2];
        var expectedRing = doc.Boundary.ToList();
        expectedRing[i2] = p2 with { X = p2.X + 5 };
        double expectedArea = Geodesy.Area(expectedRing);
        int depth = scene.UndoDepth;
        var moved = EditableSiteTerrainService.MoveVertexGeo(plan, SiteVertexRef.Boundary(i2), p2.X + 5, p2.Y);
        bool movedOk = moved is not null;
        if (moved is not null) scene.ApplySitePlan(moved, "Участок: Точка 2 — перенос (5,00 м)");
        var after = moved is null ? before : EditableSiteTerrainService.Analyze(moved, scene.Elements.ToList());
        var newP2 = moved?.Boundary[i2] ?? default;
        bool originKept = moved is not null && moved.OriginX == plan.OriginX && moved.OriginY == plan.OriginY &&
                          Math.Abs(newP2.Z - (plan.Boundary[i2].Z - 5)) < 1e-6 && Math.Abs(newP2.X - plan.Boundary[i2].X) < 1e-6;
        bool areaOk = moved is not null && Math.Abs(moved.Area - expectedArea) < 1e-6 && Math.Abs(after.GaussArea - before.GaussArea) > 10 &&
                      Math.Abs(EditableSiteTerrainService.RingArea(moved.Boundary) - expectedArea) < 0.01;
        double side12 = moved?.Segments.First(s => s.From == "1").Length ?? 0, side23 = moved?.Segments.First(s => s.From == "2").Length ?? 0;
        bool sidesOk = Math.Abs(side12 - Geodesy.Length(expectedRing[i2 - 1], expectedRing[i2])) < 1e-6 && Math.Abs(side23 - Geodesy.Length(expectedRing[i2], expectedRing[i2 + 1])) < 1e-6;
        bool zonesFollow = moved is not null && moved.Zones.Where(z => z.Zone.CoversWholeParcel || z.Zone.Kind == GpzuZoneKind.AllowedArea)
            .All(z => Math.Abs(EditableSiteTerrainService.RingArea(z.Polygon) - expectedArea) < 0.05);
        // Рельеф: отметка точки № 2 — по топосъёмке в новом месте; заданная вручную отметка +1,5 м поднимает рельеф участка.
        double z2Before = EditableSiteTerrainService.EffectiveVertexElevation(plan, i2), z2After = moved is null ? 0 : EditableSiteTerrainService.EffectiveVertexElevation(moved, i2);
        var raised = moved is null ? null : EditableSiteTerrainService.SetVertexElevation(moved, i2, moved.GroundElevation + 4.0);
        var raisedMetrics = raised is null ? after : EditableSiteTerrainService.Analyze(raised, scene.Elements.ToList());
        bool reliefOk = plan.Relief.Count > 50 && z2After > 200 && raisedMetrics.MaxRelief > after.MaxRelief + 0.3 && Math.Abs(raised!.VertexElevation(i2) - (moved!.GroundElevation + 4.0)) < 0.006 && Math.Abs(raised.Terrain().NaturalAt(raised.Boundary[i2].X, raised.Boundary[i2].Z) - 4.0) < 0.006;
        bool oneStep = scene.UndoDepth == depth + 1 && ReferenceEquals(scene.SitePlan, moved);
        scene.Undo();
        bool undone = ReferenceEquals(scene.SitePlan, plan) && Math.Abs(scene.SitePlan!.Area - doc.GaussArea) < 1e-9;
        scene.Redo();
        bool redone = ReferenceEquals(scene.SitePlan, moved);
        var crossing = EditableSiteTerrainService.MoveVertex(plan, SiteVertexRef.Boundary(i2), plan.Boundary[i2 + 9].X, plan.Boundary[i2 + 9].Z + 3);
        bool invalidRejected = crossing is null;
        // Посадка пересчитывается: та же сцена, новый контур — замечания и площадь застройки на месте.
        bool complianceOk = after.Compliance.Findings.Any(f => f.Code == "GPZU-BOUNDARY") && Math.Abs(after.Compliance.CoveragePercent - before.Compliance.CoveragePercent) > 1e-4;
        Check(report, ref failures, movedOk && originKept && areaOk && sidesOk && zonesFollow && reliefOk && oneStep && undone && redone && invalidRejected && complianceOk,
            string.Create(W6Ru, $"живой участок: точка № 2 +5,00 м к северу (X {p2.X:0.00} → {p2.X + 5:0.00}), начало сцены прежнее [{originKept}]; Гаусс {before.GaussArea:N2} → {after.GaussArea:N2} м² = независимый расчёт {expectedArea:N2} [{areaOk}]; ") +
            string.Create(W6Ru, $"стороны 1–2 = {side12:0.00} м, 2–3 = {side23:0.00} м [{sidesOk}]; зоны на весь участок и пятно следуют за границей [{zonesFollow}]; рельеф: Z точки 2 {z2Before:0.00} → {z2After:0.00} м БС по {plan.Relief.Count} отметкам, ") +
            string.Create(W6Ru, $"отметка +4,00 м поднимает рельеф участка {after.MaxRelief:+0.00;-0.00} → {raisedMetrics.MaxRelief:+0.00;-0.00} м [{reliefOk}]; посадка: застройка {before.Compliance.CoveragePercent:0.00} → {after.Compliance.CoveragePercent:0.00} % [{complianceOk}]; ") +
            $"ровно 1 шаг «{scene.Transactions.PeekUndo()?.Name}» [{oneStep}], Undo → прежний план [{undone}], Redo [{redone}]; самопересечение отклонено [{invalidRejected}]");

        // ── 2. Несколько строений на участке: корпус + ТП + паркинг без наложений, в границах, разрыв ≥ 6 м.
        var site = moved ?? plan;
        var placed = new List<string>();
        foreach (var kind in new[] { SiteStructureKind.TransformerSubstation, SiteStructureKind.Parking })
        {
            string gid = "w6-" + kind;
            var s = EditableSiteTerrainService.CreateStructure(kind, gid);
            if (EditableSiteTerrainService.FindFreePlace(site, scene.Elements.ToList(), s) is { } place)
            {
                foreach (var e in s) e.Position = [e.X + place.Dx, e.Y, e.Z + place.Dz];
                using (scene.BeginUndoTransaction("Строение на участке: " + kind)) scene.AddElements(s, recordUndo: false);
                placed.Add(EditableSiteTerrainService.StructureTitle(kind));
            }
        }
        var pads = EditableSiteTerrainService.BuildingPads(scene.Elements.ToList());
        double minGap = double.MaxValue;
        for (int a = 0; a < pads.Count; a++)
            for (int b = a + 1; b < pads.Count; b++) minGap = Math.Min(minGap, GpzuComplianceVerifier.RingDistance(pads[a].Outline, pads[b].Outline));
        var multi = EditableSiteTerrainService.Analyze(site, scene.Elements.ToList());
        bool multiOk = placed.Count == 2 && pads.Count == 3 && minGap >= 6 - 1e-6 && multi.Compliance.Findings.Single(f => f.Code == "GPZU-BOUNDARY").Severity == GpzuSeverity.Ok;
        Check(report, ref failures, multiOk,
            string.Create(W6Ru, $"несколько строений на участке: корпус + {string.Join(" + ", placed)} — площадок {pads.Count}, наименьший разрыв {minGap:0.0} м ≥ 6, все в границах [{multiOk}]; ") +
            string.Create(W6Ru, $"земляные работы под площадками: выемка {multi.Earthworks.CutM3:N1} м³, насыпь {multi.Earthworks.FillM3:N1} м³ по рельефу {multi.MinRelief:+0.00;-0.00}…{multi.MaxRelief:+0.00;-0.00} м"));

        // ── 3. Глубокое чтение «НО» нестандартного документа: ядро по всем страницам и сноскам + модель со сверкой цитат.
        var text = DeepDocumentConstraintService.FromText(W6HiddenConditionsText, "ГПЗУ-2026-0006.txt");
        var core = DeepDocumentConstraintService.ExtractCore(text);
        bool Has(ConstraintCategory c, Func<ProjectConstraintItem, bool>? f = null) => core.Any(i => i.Category == c && (f?.Invoke(i) ?? true));
        bool coreOk = Has(ConstraintCategory.Setback, i => i.MinSetbackM == 3 && i.Page == 2) && Has(ConstraintCategory.HeightLimit, i => i.MaxHeightM == 75) &&
                      Has(ConstraintCategory.HeightLimit, i => i.MaxHeightAbsM == 365.00) && Has(ConstraintCategory.LivingAreaLimit, i => i.MaxLivingAreaM2 == 18500) &&
                      Has(ConstraintCategory.Parking, i => i.MinParkingSpots == 140) && Has(ConstraintCategory.Approval) &&
                      Has(ConstraintCategory.NetworkRelocation, i => i.RequiresNetworkRelocation && i.NetworkKind == GpzuZoneKind.GasPipeline && i.BufferRadiusM == 2) &&
                      Has(ConstraintCategory.SanitaryZone, i => i.BufferRadiusM == 50) && Has(ConstraintCategory.SpecialConditions, i => i.IsFootnote) &&
                      core.Any(i => i.IsFootnote && i.MinSetbackM == 6);
        int modelCalls = 0;
        string? sentChunk = null;
        var stubModel = new ConstraintModel("Облачный сервис · заглушка", (system, user, ct) =>
        {
            modelCalls++;
            sentChunk = user;
            return Task.FromResult("""
                <think>читаю сноски</think>
                {"constraints":[
                 {"category":"setback","title":"Отступ со стороны ул. Депутатской 6 м","quote":"со стороны ул. Депутатской отступ составляет 6 м","page":3,"clause":"сноска *","minSetbackM":6,"impact":"blocking","impactText":"Здание не ближе 6 м к улице"},
                 {"category":"setback","title":"Отступ 10 м от красной линии","quote":"отступ от красной линии 10 м","page":2,"clause":"п. 2.2","minSetbackM":10,"impact":"blocking"},
                 {"category":"special_conditions","title":"СТУ при высоте более 50 м","quote":"Строительство выше 50 м возможно только при разработке специальных технических условий (СТУ)","page":3,"clause":"сноска **","maxHeightM":50,"impact":"approval"}
                ]}
                """);
        });
        var extraction = await DeepDocumentConstraintService.ExtractAsync(text, stubModel);
        var wrong = extraction.Items.FirstOrDefault(i => i.MinSetbackM == 10);
        bool modelOk = modelCalls == 1 && sentChunk is not null && sentChunk.Contains("=== СТРАНИЦА 3 ===", StringComparison.Ordinal) && sentChunk.Contains("** Строительство", StringComparison.Ordinal) &&
                       wrong is { QuoteVerified: false } && extraction.Items.Any(i => i.MinSetbackM == 6 && i.QuoteVerified && i.ExtractedBy.Contains('+')) &&
                       extraction.Items.Any(i => i.MaxHeightM == 50 && i.QuoteVerified);
        Check(report, ref failures, coreOk && modelOk,
            $"глубокое чтение «НО»: {text.Pages.Count} стр., {text.Sentences.Count} предложений, сносок {text.Sentences.Count(s => s.IsFootnote)}; ядро — {core.Count} пунктов: отступ 3 м (стр. 2), H ≤ 75 м, Hабс 365,00, жильё 18 500 м², 140 машино-мест, согласование с Росавиацией, " +
            $"вынос газопровода по ТУ ± 2 м, СЗЗ 50 м, сноски «*» (отступ 6 м) и «**» (СТУ) [{coreOk}]; модель получила текст с маркерами страниц и сносками, её пункты сверены с документом: вымышленная цитата «10 м» — не найдена в тексте, " +
            $"«6 м» — подтверждена ядром и моделью, «СТУ > 50 м» — сверена; итого {extraction.Items.Count} пунктов [{modelOk}]");

        // ── 4. Отклонение ошибочного пункта → повторный уточнённый поиск (модель и ядро) → ручная правка → применение к участку.
        string? refinePrompt = null;
        var refineModel = new ConstraintModel("Облачный сервис · заглушка", (system, user, ct) =>
        {
            refinePrompt = user;
            return Task.FromResult("""{"constraints":[{"category":"setback","title":"Минимальный отступ от границ участка 3 м","quote":"Минимальный отступ от границ земельного участка — 3 м*","page":2,"clause":"п. 2.2","minSetbackM":3,"impact":"blocking"}]}""");
        });
        var rejected = (wrong ?? core[0]) with { Status = ConstraintStatus.Rejected };
        var refined = await DeepDocumentConstraintService.RefineAsync(rejected, "ИИ перепутал: не 10 м от красной линии, а 3 м от границ участка (п. 2.2)", text, refineModel);
        var offlineRefined = await DeepDocumentConstraintService.RefineAsync(rejected, "не 10, а 3 м", text, null);
        var manualItem = DeepDocumentConstraintService.Manual(core.First(i => i.Category == ConstraintCategory.Parking), 160);
        bool refineOk = refinePrompt is not null && refinePrompt.Contains("ОТКЛОНИЛ", StringComparison.Ordinal) && refinePrompt.Contains("а 3 м от границ", StringComparison.Ordinal) &&
                        refined.Count == 1 && refined[0].MinSetbackM == 3 && refined[0].QuoteVerified && refined[0].UserComment.Length > 0 &&
                        offlineRefined.Any(i => i.MinSetbackM == 3) && manualItem is { Status: ConstraintStatus.Manual, MinParkingSpots: 160, Origin: ConstraintOrigin.Manual };
        // Применение: подтверждены — уточнённый отступ 3 м, сноска 6 м (улица), H ≤ 75 м, вручную — 160 машино-мест; ошибочный — отклонён.
        var confirmed = new List<ProjectConstraintItem>
        {
            refined[0] with { Status = ConstraintStatus.Confirmed },
            core.First(i => i.IsFootnote && i.MinSetbackM == 6) with { Status = ConstraintStatus.Confirmed },
            core.First(i => i.MaxHeightM == 75) with { Status = ConstraintStatus.Confirmed },
            manualItem,
            rejected
        };
        int depthC = scene.UndoDepth;
        var withRules = EditableSiteTerrainService.WithConstraints(site, confirmed);
        scene.ApplySitePlan(withRules, "Регламент участка: утверждено 4");
        var ruled = GpzuComplianceVerifier.Verify(withRules, scene.Elements.ToList());
        var setbackFindings = ruled.Findings.Where(f => f.Code == "GPZU-CNS-SETBACK").ToList();
        double nearest = scene.Elements.Where(GpzuComplianceVerifier.IsBuildingPart).SelectMany(e => AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(e))
            .Min(p => SiteBoundaryBuilder.Contains(site.Boundary, p.X, p.Z) ? Enumerable.Range(0, site.Boundary.Count).Min(k => SiteBoundaryBuilder.DistanceToSegment(site.Boundary[k], site.Boundary[(k + 1) % site.Boundary.Count], p.X, p.Z)) : 0);
        bool SixMetres(GpzuFinding f) => f.Detail.Contains("≥ 6 м", StringComparison.Ordinal) || f.Detail.Contains("ближе 6 м", StringComparison.Ordinal);
        bool setbackLogic = setbackFindings.Count == 2 && setbackFindings.All(f => f.Severity == (nearest >= (SixMetres(f) ? 6 : 3) - 1e-6 ? GpzuSeverity.Ok : GpzuSeverity.Critical));
        bool applyOk = withRules.Constraints.Count == 4 && withRules.Constraints.All(c => c.IsActive) && scene.UndoDepth == depthC + 1 &&
                       ruled.Findings.Any(f => f.Code == "GPZU-CNS-HEIGHT" && f.Severity == GpzuSeverity.Ok) &&
                       ruled.Findings.Any(f => f.Code == "GPZU-CNS-PARKING") && setbackFindings.Count == 2 && setbackLogic &&
                       ruled.Findings.Any(f => f.Code == "GPZU-CNS-SETBACK" && f.Severity == (nearest >= 6 - 1e-6 ? GpzuSeverity.Ok : GpzuSeverity.Critical));
        scene.Undo();
        bool constraintsUndone = scene.SitePlan!.Constraints.Count == 0;
        scene.Redo();
        Check(report, ref failures, refineOk && applyOk && constraintsUndone,
            $"отклонение «ИИ перепутал»: повторный поиск с уточнением — запрос несёт отклонённый пункт и комментарий, модель вернула «отступ 3 м» со сверенной цитатой [{refineOk}]; без модели ядро берёт число из комментария — «3 м»; " +
            string.Create(W6Ru, $"«Задать вручную» — 160 машино-мест (статус «вручную»); применены 4 подтверждённых пункта одним шагом журнала (отклонённый — нет): H ≤ 75 м — в норме, отступы 3 и 6 м проверены по ближайшей точке здания {nearest:0.00} м, ") +
            $"машино-места — по площади паркинга [{applyOk}]; Undo снимает правила [{constraintsUndone}]");

        // ── 5. Поиск ограничений в сети: заглушка поисковой выдачи + база нормативов ядра + синтез моделью.
        string html = """
            <div class="result"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fpp160&amp;rut=1">Охранные зоны ЛЭП — ПП РФ № 160</a>
            <a class="result__snippet" href="#">Охранная зона ВЛ 110 кВ составляет 20 м от крайних проводов по обе стороны линии.</a></div>
            <div class="result"><a rel="nofollow" class="result__a" href="https://example.org/gas">Газопровод среднего давления: расстояния</a>
            <a class="result__snippet" href="#">Охранная зона газопровода 2 м с каждой стороны; до фундаментов зданий 4 м (СП 42.13330).</a></div>
            """;
        string? requested = null;
        var handler = new W6SearchStub(url => { requested = url; return html; });
        var synth = new ConstraintModel("Облачный сервис · заглушка", (system, user, ct) => Task.FromResult(
            """{"constraints":[{"category":"protection_zone","title":"ЛЭП 110 кВ: охранная зона 20 м","quote":"Охранная зона ВЛ 110 кВ составляет 20 м","source":"https://example.org/pp160","clause":"ПП № 160, прил.","bufferRadiusM":20,"network":"power","impact":"blocking"}]}"""));
        var web = await DeepDocumentConstraintService.SearchAsync("Найди действующие ограничения и охранные зоны для газопровода среднего давления и ЛЭП 110 кВ в жилой зоне", synth, handler);
        bool webOk = requested is not null && requested.Contains("duckduckgo", StringComparison.Ordinal) && web.WebHits.Count == 2 && web.WebHits[0].Url == "https://example.org/pp160" &&
                     web.Items.Any(i => i.Origin == ConstraintOrigin.Normative && i.NetworkKind == GpzuZoneKind.GasPipeline && i.BufferRadiusM == 4 && i.Source.StartsWith("СП 42", StringComparison.Ordinal)) &&
                     web.Items.Any(i => i.NetworkKind == GpzuZoneKind.PowerCable && i.BufferRadiusM == 20) &&
                     web.Items.Any(i => i.Source.Contains("https://example.org/pp160", StringComparison.Ordinal)) && web.Items.Any(i => i.Source.Contains("https://example.org/gas", StringComparison.Ordinal)) &&
                     web.Items.All(i => i.Status == ConstraintStatus.Pending);
        Check(report, ref failures, webOk,
            $"поиск ограничений «газопровод среднего давления и ЛЭП 110 кВ»: выдача — {web.WebHits.Count} источника (переадресация раскрыта), база нормативов ядра — газ: до фундаментов 4 м (СП 42.13330, табл. 12.6) и охранная зона ± 2 м (ПП № 878), " +
            $"ВЛ 110 кВ ± 20 м (ПП № 160); синтез модели с источником; всего {web.Items.Count} пунктов, все ждут подтверждения [{webOk}]");

        // ── 6. Три Среды над одним проектом + автосохранение и восстановление сессии + защита выхода.
        failures += await CheckWave6EnvironmentsAsync(directory, report, pdfPath, dwgPath);

        // ── 7. Физический проход: глухая стена, дверь (закрыта / открыта), лестница 0,00 → +3,00.
        failures += CheckWave6Walk(report);

        // ── 8. Строки.
        var dict = typeof(LocalizationService).GetField("Wave6Strings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) as Dictionary<string, (string Ru, string En)>;
        bool strings = dict is { Count: > 120 } && dict.All(kv => kv.Value.Ru.Length > 0 && kv.Value.En.Length > 0 && !kv.Value.Ru.Any(char.IsSurrogate) && !kv.Value.En.Any(char.IsSurrogate));
        Check(report, ref failures, strings, $"строки Волны 6: {dict?.Count} ключей W6*, RU/EN заполнены, без эмодзи [{strings}]");
        return failures;
    }

    /// <summary>Заглушка сети для поисковой выдачи.</summary>
    private sealed class W6SearchStub(Func<string, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request.RequestUri?.ToString() ?? string.Empty), Encoding.UTF8, "text/html") });
    }

    private static async Task<int> CheckWave6EnvironmentsAsync(string directory, StringBuilder report, string pdfPath, string dwgPath)
    {
        int failures = 0;
        var vm = App.Services.GetRequiredService<MainViewModel>();
        var envBefore = vm.ActiveEnvironment;
        // Среда 1: участок из ГПЗУ и топосъёмки в живой проект.
        vm.ActiveEnvironment = DeveloperEnvironment.Site;
        vm.StartEmptyProject();
        await vm.LoadGpzuAsync(pdfPath, deepRead: false);
        await vm.LoadTopoAsync(dwgPath);
        var plan = vm.BuildGpzuPlan()!;
        vm.BuildSiteInScene(plan);
        var sitePlan = vm.Scene.SitePlan;
        bool env1 = sitePlan is not null && vm.IsSiteEditing && vm.IsEnvSite;
        bool pointMoved = vm.MoveSiteVertex(SiteVertexRef.Boundary(1), sitePlan!.Boundary[1].X + 1.0, sitePlan.Boundary[1].Z);
        var site1 = vm.Scene.SitePlan;
        // Среда 2: тот же документ — здание вызывается на участок.
        vm.ActiveEnvironment = DeveloperEnvironment.Building;
        bool sameProject = ReferenceEquals(vm.Scene.SitePlan, site1) && !vm.IsSiteEditing;
        string? tower = vm.AddStructureToSite(SiteStructureKind.ResidentialSection, 9);
        bool env2 = tower is not null && vm.Scene.Elements.Any(e => e.BuildingGroupId == tower) && vm.SiteMetrics is { Buildings: >= 1 };
        // Среда 3: пакеты этажей, вводы сетей участка, проверки зон — по тому же зданию.
        vm.ActiveEnvironment = DeveloperEnvironment.Interiors;
        var interiors = vm.RefreshInteriors();
        bool env3 = ReferenceEquals(vm.Scene.SitePlan, site1) && interiors.Levels == 9 && interiors.Packages.Count == 2 && interiors.Packages[0] is { FromLevel: 1, ToLevel: 5 } &&
                    interiors.Entries.Count >= 3 && interiors.Checks.Any(c => c.Title.Contains("Газ", StringComparison.Ordinal));
        // Автосохранение: снимок с участком, ограничением и Средой → файл, сведения сессии, восстановление с того же места.
        vm.ApplyConfirmedConstraints([new ProjectConstraintItem { Category = ConstraintCategory.HeightLimit, Title = "Предельная высота 75 м", MaxHeightM = 75, Status = ConstraintStatus.Confirmed }]);
        string autoDir = Path.Combine(directory, "autosave");
        if (Directory.Exists(autoDir)) Directory.Delete(autoDir, true);
        using var autosave = new AutoSaveService(vm.Scene, () => (new ProjectSnapshot("Perspective3D", vm.Scene.ActiveLevelFilter, vm.CaptureProjectParameters(), vm.ChatMessages.ToList(), vm.EnvironmentForFile), vm.EnvironmentForFile),
            a => a(), autoDir);
        autosave.MarkChanged();
        bool pending = autosave.HasPendingChanges;
        var saved = await autosave.SaveNowAsync("автотест");
        var info = AutoSaveService.TryReadSession(autoDir);
        int elements = vm.Scene.Elements.Count;
        double area = vm.Scene.SitePlan!.Area;
        vm.StartEmptyProject();
        vm.ActiveEnvironment = DeveloperEnvironment.Building;
        bool cleared = vm.Scene.SitePlan is null && vm.Scene.Elements.Count == 0;
        bool restored = await vm.RestoreAutoSaveSessionAsync(autoDir);
        bool autoOk = pending && saved is not null && File.Exists(Path.Combine(autoDir, AutoSaveService.FileName)) && info is { HasSite: true, Environment: "interiors" } && cleared && restored &&
                      vm.Scene.Elements.Count == elements && vm.Scene.SitePlan is { } r && Math.Abs(r.Area - area) < 1e-6 && r.Constraints.Count == 1 && vm.ActiveEnvironment == DeveloperEnvironment.Interiors;
        // Защита выхода: с проектом закрытие перехватывается, «Отмена» оставляет окно, «Выйти без сохранения» снимает перехват.
        bool guard = false;
        if (App.MainWindowInstance is MainWindow window)
        {
            bool blocked = window.EvaluateCloseRequest();
            bool cancelled = !await window.ExecuteExitDecisionAsync(ExitDecision.Cancel, closeWindow: false) && window.EvaluateCloseRequest();
            bool released = await window.ExecuteExitDecisionAsync(ExitDecision.Discard, closeWindow: false) && !window.EvaluateCloseRequest();
            window.DebugResetExitGuard();
            guard = blocked && cancelled && released && window.EvaluateCloseRequest();
        }
        Check(report, ref failures, env1 && pointMoved && sameProject && env2 && env3,
            string.Create(W6Ru, $"три Среды над одним проектом: Среда 1 — участок {sitePlan.Document.Cadastral} в сцене, пины точек активны, точка сдвинута шагом журнала [{env1 && pointMoved}]; Среда 2 — тот же участок, ") +
            string.Create(W6Ru, $"корпус ЖК 9 эт. вызван на участок [{sameProject && env2}]; Среда 3 — этажей {interiors.Levels}, пакеты {string.Join(", ", interiors.Packages.Select(p => $"{p.FromLevel}–{p.ToLevel} {p.Layout}"))}, ") +
            $"вводы сетей: {string.Join(", ", interiors.Entries.Select(e => e.Title))}; проверки: {string.Join("; ", interiors.Checks.Select(c => c.Title))} [{env3}]");
        Check(report, ref failures, autoOk && guard,
            string.Create(W6Ru, $"автосохранение: {AutoSaveService.FileName} — {saved?.Bytes / 1024.0:N0} КБ, сведения сессии (Среда «{info?.Environment}», участок, {info?.Elements} эл.); после «нового проекта» восстановление в 1 клик: ") +
            $"элементов {vm.Scene.Elements.Count}, площадь участка и ограничение на месте, Среда 3 [{autoOk}]; защита выхода: закрытие перехвачено, «Отмена» — окно остаётся, «Выйти без сохранения» — перехват снят [{guard}]");
        vm.ActiveEnvironment = envBefore;
        vm.StartEmptyProject();
        return failures;
    }

    /// <summary>Сцена прохода: пол 0,00, стена с дверью, глухая стена, перекрытие +3,00 с проёмом и прямой марш.</summary>
    public static List<ArchitecturalElement> CreateWalkTestScene(StairShape shape = StairShape.Straight)
    {
        const string g = "w6-walk";
        ArchitecturalElement E(string type, string name, double x, double y, double z, double w, double h, double d, double rot = 0) => new()
        {
            Type = type, Name = name, BuildingGroupId = g, LevelIndex = y > 2 ? 1 : 0, Material = "concrete",
            Size = [w, h, d], Position = [x, y + h / 2, z], Rotation = [0, rot, 0]
        };
        var list = new List<ArchitecturalElement>
        {
            E(ElementTypes.Slab, "Пол 1 этажа", 0, -0.2, 0, 16, 0.2, 12),
            E(ElementTypes.Wall, "Глухая стена", 0, 0, -6, 16, 3.0, 0.3),
            E(ElementTypes.Slab, "Перекрытие +3,00", 0, 2.8, 0, 16, 0.2, 12)
        };
        var front = E(ElementTypes.Wall, "Стена с дверью", 0, 0, 6, 16, 3.0, 0.3);
        list.Add(front);
        var door = E(ElementTypes.Door, "Дверь Д-1", 4, 0, 6, 1.0, 2.1, 0.36);
        door.HostElementId = front.Id;
        list.Add(door);
        var stair = StairGeneratorService.Build(list, new StairDesignRequest(shape, StairConstruction.MonolithicSlab), g, 0, 0, 3.0, -3, 0, 0, "Л-1");
        foreach (var r in stair.Removed) list.Remove(r);
        list.AddRange(stair.Created);
        return list;
    }

    private static int CheckWave6Walk(StringBuilder report)
    {
        int failures = 0;
        var scene = CreateWalkTestScene();
        var world = WalkPhysicsService.Build(scene);
        // а) Глухая стена (грань z = −5,85): капсула останавливается перед гранью, скольжение вдоль стены сохраняет касательную.
        var s = new WalkState(0, -3.5, 0);
        bool anyBlocked = false;
        for (int i = 0; i < 120; i++)
        {
            var r = WalkPhysicsService.Step(world, s, 0, -0.05, 1.0 / 60);
            anyBlocked |= r.Blocked && r.BlockedType == ElementTypes.Wall;
            s = r.State;
        }
        double wallFace = -6 + 0.15;
        bool wallStop = anyBlocked && s.Z >= wallFace + WalkPhysicsService.Radius - 0.01 && s.Z <= wallFace + WalkPhysicsService.Radius + 0.05 && !WalkPhysicsService.Overlaps(world, s);
        var slide = WalkPhysicsService.Step(world, s, 0.5, -0.5, 1.0 / 60);
        bool sliding = slide.State.X > s.X + 0.45 && Math.Abs(slide.State.Z - s.Z) < 0.02;
        // б) Дверь: закрытая — упор; E — открыть (дверь перед собой); открытая — проход наружу.
        var d0 = new WalkState(4, 4.2, 0);
        var door = WalkPhysicsService.DoorAhead(world, d0, 0 /* взгляд −Z */) ?? WalkPhysicsService.DoorAhead(world, d0, Math.PI);
        var closed = Walk(world, d0, 0, 0.05, 80);
        bool closedBlocks = closed.Blocked && closed.State.Z < 6 - 0.05 && closed.BlockedType == ElementTypes.Door;
        bool opened = door is not null && WalkPhysicsService.ToggleDoor(world, door);
        var through = Walk(world, d0, 0, 0.05, 80);
        bool doorPass = opened && through.State.Z > 7.5;
        WalkPhysicsService.ToggleDoor(world, door!);
        // в) Лестница: от низа марша вдоль его оси — ноги поднимаются по ступеням с 0,00 на +3,00 (подступенок ≤ 0,22 м).
        var flight = scene.First(e => e.Category == BimCategory.Stairs);
        var (fx, fz) = ArchitecturalElement.RotateY(0, 1, flight.RotationY);
        double startOffset = flight.Depth / 2 + 0.8;
        var st = new WalkState(flight.X - fx * startOffset, flight.Z - fz * startOffset, 0);
        double maxRise = 0, prevFeet = 0;
        bool onStairs = false;
        double total = flight.Depth + 2.0;
        int steps = (int)Math.Ceiling(total / 0.05);
        for (int i = 0; i < steps; i++)
        {
            var r = WalkPhysicsService.Step(world, st, fx * 0.05, fz * 0.05, 1.0 / 60);
            st = r.State;
            onStairs |= r.OnStairs;
            maxRise = Math.Max(maxRise, st.Feet - prevFeet);
            prevFeet = st.Feet;
        }
        bool climbed = onStairs && Math.Abs(st.Feet - 3.0) < 0.01 && maxRise <= WalkPhysicsService.MaxStep + 1e-9 && Math.Abs(st.Eye - 4.70) < 0.011;
        // г) Спуск обратно и П-образная лестница: тем же путём через площадку на +1,50.
        var u = CreateWalkTestScene(StairShape.UShaped);
        var uw = WalkPhysicsService.Build(u);
        var flights = u.Where(e => e.Category == BimCategory.Stairs).OrderBy(e => e.BaseY).ToList();
        var us = new WalkState(0, 0, 0);
        bool uOk = flights.Count == 2;
        foreach (var f in flights)
        {
            var (dx, dz) = ArchitecturalElement.RotateY(0, 1, f.RotationY);
            double bx = f.X - dx * (f.Depth / 2 + 0.25), bz = f.Z - dz * (f.Depth / 2 + 0.25);
            us = us.X == 0 && us.Z == 0 ? new WalkState(bx, bz, f.BaseY) : WalkTo(uw, us, bx, bz);
            us = Walk(uw, us, dx * 0.05, dz * 0.05, (int)Math.Ceiling((f.Depth + 0.6) / 0.05)).State;
        }
        bool uClimb = uOk && Math.Abs(us.Feet - 3.0) < 0.01;
        Check(report, ref failures, wallStop && sliding && closedBlocks && doorPass && climbed && uClimb,
            string.Create(W6Ru, $"физический проход (капсула R = {WalkPhysicsService.Radius:0.00} м, глаза 1,70 м, макушка 1,85 м): глухая стена — остановка у грани (z = {s.Z:0.000}, грань {wallFace:0.00} + R), без пересечения [{wallStop}], ") +
            string.Create(W6Ru, $"скольжение вдоль стены Δx = {slide.State.X - s.X:0.00} м при упоре по нормали [{sliding}]; закрытая дверь — упор (z = {closed.State.Z:0.00}) [{closedBlocks}], E — открыта, проход наружу до z = {through.State.Z:0.00} [{doorPass}]; ") +
            string.Create(W6Ru, $"прямой марш {flight.StepCount} ступ.: ноги 0,00 → {st.Feet:+0.00} м, наибольший шаг вверх {maxRise:0.000} м ≤ 0,22, глаза {st.Eye:0.00} м [{climbed}]; П-образная через площадку: → {us.Feet:+0.00} м [{uClimb}]"));
        return failures;

        static WalkStepResult Walk(WalkPhysicsWorld w, WalkState from, double dx, double dz, int n)
        {
            WalkStepResult r = new(from, false, null, null, 0, 0, false);
            bool blocked = false;
            string? type = null;
            for (int i = 0; i < n; i++)
            {
                r = WalkPhysicsService.Step(w, r.State, dx, dz, 1.0 / 60);
                if (r.Blocked) { blocked = true; type ??= r.BlockedType; }
            }
            return r with { Blocked = blocked, BlockedType = type };
        }

        static WalkState WalkTo(WalkPhysicsWorld w, WalkState from, double x, double z)
        {
            double dx = x - from.X, dz = z - from.Z, len = Math.Sqrt(dx * dx + dz * dz);
            int n = Math.Max(1, (int)Math.Ceiling(len / 0.05));
            var s = from;
            for (int i = 0; i < n; i++) s = WalkPhysicsService.Step(w, s, dx / n, dz / n, 1.0 / 60).State;
            return s;
        }
    }
}
#endif
