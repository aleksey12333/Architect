#if DEBUG
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Economics;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using AIArchitectStudio.ViewModels;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест волны 3: долги волны 2 по армированию (Ø, шаг хомутов и защитный слой в расчёте h0 = h − a, As = n·π·d²/4 и в 3D-каркасе;
/// крестообразные каркасы продавливания у колонн), директор локальных моделей (инженерный рейтинг, автовыбор и ручной выбор, замер
/// скорости, модель остаётся в Совете КБ), пятифазная цепочка рассуждений с проверкой в песочнице, веб-импорт расценок (страница
/// магазина в UTF-8 и Windows-1251: JSON-LD, microdata, таблица, текст; пересчёт п.м/кг → т, бетон без доставки; прошлые сметы .aia
/// и .csv с индексацией; смета не меняется до подтверждения) и Инженерный агент (аудит + высота этажа + подбор ГОСТ + Renga за
/// одну транзакцию, Undo ≡ исходному, Redo ≡ итогу), чистота видимых строк.
/// </summary>
public static partial class CadSelfTestService
{
    private static readonly CultureInfo W3Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static async Task<int> CheckWave3Async(string directory, StringBuilder report)
    {
        int failures = 0;
        failures += CheckRebarDesign(report);
        failures += CheckPunchingCages(report);
        failures += await CheckModelDirectorAsync(report);
        failures += CheckReasoning(report);
        failures += await CheckPriceExtractionAsync(directory, report);
        failures += CheckPastEstimates(directory, report);
        failures += CheckPriceConfirmation(report);
        failures += await CheckAgentAsync(report);
        failures += await CheckAgentInWindowAsync(report);
        failures += CheckWave3Strings(report);
        return failures;
    }

    // ═════════════════════════════ Задача 0: явное армирование ═════════════════════════════

    private static int CheckRebarDesign(StringBuilder report)
    {
        int failures = 0;
        var loads = new StructuralLoadSettings();
        // Колонна 400 × 400, H = 3 м: каркас без параметров, Ø16 / шаг 200 / слой 25 и Ø28 / шаг 100 / слой 50.
        RebarCage Cage(double? d, double s, double c, out ArchitecturalElement column)
        {
            column = W2(ElementTypes.Column, "К-армирование", 0, 0, 0, 0.4, 3, 0.4);
            if (d is { } dia) StructuralDesign.WriteRebar(column, dia, s, c);
            var g = ElementGeometryBuilder.Build(column, [], AllLevels);
            return RebarCageBuilder.Build(column, g.LocalBounds)!;
        }
        var plain = Cage(null, 0, 0, out _);
        var thin = Cage(0.016, 0.2, 0.025, out var c16);
        var thick = Cage(0.028, 0.1, 0.05, out var c28);
        int Hoops(RebarCage cage) => cage.Stirrups.Count / 8;
        // Ось рабочего стержня от грани: a = c + Ø8 хомута + d/2.
        static double Inset(RebarCage cage) => 0.2 - cage.Working.Max(p => p.X);
        bool geometry = thin.IsExplicit && thick.IsExplicit && !plain.IsExplicit && thin.WorkingDiameter == 16 && thick.WorkingDiameter == 28 &&
                        Math.Abs(Inset(thin) - (0.025 + 0.008 + 0.008)) < 1e-5 && Math.Abs(Inset(thick) - (0.05 + 0.008 + 0.014)) < 1e-5 &&
                        Hoops(thick) > 1.8 * Hoops(thin) && Hoops(plain) != Hoops(thin) &&
                        thin.BarCount == StructuralDesign.Layout(c16)!.Value.Bars && thick.BarCount == StructuralDesign.Layout(c28)!.Value.Bars;

        // Расчёт: балка рамы — K прочности от Ø (As) и защитного слоя (h0 = h − a); паспорт ≡ солверу и с параметрами.
        var (elements, groups, _) = CreateFrameScene();
        var beam = elements.Where(e => e.Category == BimCategory.Beam && ReinforcementCalculator.IsReinforced(e)).OrderByDescending(e => StructuralAnalysisService.Analyze(elements, groups, loads).For(e.Id)?.Utilization ?? 0).First();
        double KOf(double? d, double c, out bool passport, out double a, out double mu)
        {
            var copy = elements.Select(e => e.Clone()).ToList();
            var b = copy.First(e => e.Id == beam.Id);
            if (d is { } dia) StructuralDesign.WriteRebar(b, dia, 0.15, c);
            var model = AnalyticalModelBuilder.Build(copy, groups, loads);
            var result = StructuralAnalysisService.Analyze(model);
            var m = model.Elements.First(x => x.Source.Id == beam.Id);
            a = StructuralAnalysisService.RebarAxisDistance(m, 0.05);
            mu = StructuralAnalysisService.RebarRatio(m, 0.012);
            passport = CalculationJustificationService.Build(beam.Id, model, result)?.MatchesSolver == true;
            return result.For(beam.Id)!.KStrength;
        }
        double k0 = KOf(null, 0, out bool p0, out double a0, out double mu0);
        double k16 = KOf(0.016, 0.025, out bool p16, out double a16, out double mu16);
        double k28 = KOf(0.028, 0.025, out bool p28, out _, out double mu28);
        double k28c50 = KOf(0.028, 0.05, out bool p50, out double a50, out _);
        var layout = StructuralDesign.Layout(new Func<ArchitecturalElement>(() => { var b = beam.Clone(); StructuralDesign.WriteRebar(b, 0.028, 0.15, 0.025); return b; })())!.Value;
        bool analysis = a0 == 0.05 && mu0 == 0.012 && Math.Abs(a16 - 0.041) < 1e-12 && Math.Abs(a50 - 0.072) < 1e-12 &&
                        Math.Abs(mu28 - layout.Bars * Math.PI * 0.028 * 0.028 / 4 / (beam.Depth * (beam.Height - layout.AxisDistance))) < 1e-12 &&
                        k28 < k16 && k28c50 > k28 && p0 && p16 && p28 && p50 && mu28 > mu16;
        Check(report, ref failures, geometry && analysis,
            $"армирование по параметрам: колонна 400 × 400 — Ø16/шаг 200/слой 25: стержней {thin.BarCount}, хомутов {Hoops(thin)}, ось стержня {Inset(thin) * 1000:0.#} мм от грани; " +
            $"Ø28/шаг 100/слой 50: стержней {thick.BarCount}, хомутов {Hoops(thick)}, {Inset(thick) * 1000:0.#} мм (без параметров — {plain.BarCount} / {Hoops(plain)}); " +
            $"балка «{beam.DisplayTitle}»: K прочности {k0:0.000} (μ 1,2 %, a 50) → Ø16 {k16:0.000} (μ {mu16 * 100:0.00} %, a {a16 * 1000:0} мм) → Ø28 {k28:0.000} (μ {mu28 * 100:0.00} %) → Ø28 при слое 50 мм {k28c50:0.000} (a {a50 * 1000:0} мм), паспорт ≡ солверу");
        return failures;
    }

    /// <summary>Безбалочная плита 15,2 × 15,2 × 0,18 м на 9 колоннах 400 × 400 (шаг 7,2 м) над фундаментной плитой.</summary>
    internal static List<ArchitecturalElement> PunchingScene(bool rebar, double slabRotation = 0)
    {
        var list = new List<ArchitecturalElement> { W2(ElementTypes.Foundation, "Фундаментная плита", 0, -0.6, 0, 22, 0.6, 22, group: "Волна 3") };
        int i = 0;
        foreach (double x in new[] { -7.2, 0, 7.2 })
            foreach (double z in new[] { -7.2, 0, 7.2 })
                list.Add(W2(ElementTypes.Column, $"К{++i}", x, 0, z, 0.4, 3.0, 0.4, group: "Волна 3"));
        var slab = W2(ElementTypes.Slab, "Плита П-1 200", 0, 3.0, 0, 15.2, 0.2, 15.2, slabRotation, group: "Волна 3");
        if (rebar) StructuralDesign.WritePunchingRebar(slab, true);
        list.Add(slab);
        return list;
    }

    private static int CheckPunchingCages(StringBuilder report)
    {
        int failures = 0;
        var loads = new StructuralLoadSettings();
        var scene = PunchingScene(rebar: false);
        var slab = scene.Last();
        var columns = scene.Where(e => e.Category == BimCategory.Column).ToList();
        var g = ElementGeometryBuilder.Build(slab, [], AllLevels);
        var local = RebarCageBuilder.LocalColumns(slab, columns);
        var off = RebarCageBuilder.Build(slab, g.LocalBounds, 0.5, false, local)!;
        StructuralDesign.WritePunchingRebar(slab, true);
        var on = RebarCageBuilder.Build(slab, g.LocalBounds, 0.5, true, local)!;
        StructuralDesign.WriteRebar(slab, 0.012, 0.2, 0.03);
        var sparse = RebarCageBuilder.Build(slab, ElementGeometryBuilder.Build(slab, [], AllLevels).LocalBounds, 0.5, true, local)!;
        // Шпильки — в кольце h0/2…1,5·h0 от грани колонны по четырём лучам, внутри плиты.
        double h0 = 0.2 - on.Cover - on.WorkingDiameter / 2000.0;
        bool inZones = true;
        for (int k = 0; k + 1 < on.Punching.Count; k += 2)
        {
            var p = on.Punching[k];
            if (on.Punching[k + 1].X != p.X || on.Punching[k + 1].Z != p.Z) continue; // поясной стержень
            var col = local.OrderBy(cl => Math.Abs(cl.X - p.X) + Math.Abs(cl.Z - p.Z)).First();
            double dx = Math.Abs(p.X - col.X) - col.HalfX, dz = Math.Abs(p.Z - col.Z) - col.HalfZ;
            double along = Math.Max(dx, dz);
            inZones &= along >= 0.5 * h0 - 1e-4 && along <= 1.5 * h0 + 1e-4 && p.Y > g.LocalBounds.Minimum.Y && p.Y < g.LocalBounds.Maximum.Y;
        }
        // Повёрнутая плита: локальные центры колонн обратно в мир ≡ мировым (знак поворота как у WorldMatrix).
        var rotated = PunchingScene(rebar: true, slabRotation: 30).Last();
        var rotatedLocal = RebarCageBuilder.LocalColumns(rotated, columns);
        var world = ElementGeometryBuilder.WorldMatrix(rotated);
        double worst = columns.Zip(rotatedLocal).Max(p =>
        {
            var w = BimMath.TransformCoordinate(new Vector3((float)p.Second.X, 0, (float)p.Second.Z), world);
            return Math.Max(Math.Abs(w.X - p.First.X), Math.Abs(w.Z - p.First.Z));
        });
        // Узлы продавливания расчёта и поперечная арматура в ведомости стали (А240 Кр) — тот же признак.
        var analysis = StructuralAnalysisService.Analyze(scene, [new BuildingGroup("Волна 3")], loads);
        bool ok = off.Punching.Count == 0 && on.PunchingZoneCount == columns.Count && on.PunchingBarCount >= columns.Count * 8 && inZones &&
                  sparse.PunchingBarCount < on.PunchingBarCount && worst < 1e-4 && analysis.PunchingNodes.Any(n => n.SteelCapacity > 0);
        Check(report, ref failures, ok,
            $"каркасы продавливания в 3D: без тумблера шпилек 0; с тумблером — {on.PunchingBarCount} вертикальных стержней Ø8 у {on.PunchingZoneCount} колонн (крест по 4 граням, " +
            $"h0/2…1,5·h0 = {0.5 * h0 * 1000:0}…{1.5 * h0 * 1000:0} мм от грани, шаг 100 мм), при шаге 200 мм — {sparse.PunchingBarCount}; повёрнутая на 30° плита: отклонение центров {worst * 1000:0.000} мм; " +
            $"F_sw,ult в расчёте — {analysis.PunchingNodes.Count(n => n.SteelCapacity > 0)} узлов");
        return failures;
    }

    // ═════════════════════════════ Задача 1: директор локальных моделей ═════════════════════════════

    /// <summary>Набор моделей для автотеста и съёмки (как ответ /api/tags): размеры, возможности, один гигант не по памяти.</summary>
    internal static List<AiModelOption> DemoLocalModels() =>
    [
        AiModelCatalog.Ollama("qwen2.5-coder:1.5b", 1.5, ["completion", "tools"]) with { SizeGb = 1.0 },
        AiModelCatalog.Ollama("qwen2.5-coder:14b", 14.8, ["completion", "tools"]) with { SizeGb = 9.0 },
        AiModelCatalog.Ollama("deepseek-r1:8b", 8.2, ["completion", "thinking"]) with { SizeGb = 5.2 },
        AiModelCatalog.Ollama("llama3.1:8b", 8.0, ["completion", "tools"]) with { SizeGb = 4.9 },
        AiModelCatalog.Ollama("mistral:7b", 7.2, ["completion", "tools"]) with { SizeGb = 4.1 },
        AiModelCatalog.Ollama("gemma3:4b", 4.3, ["completion", "vision"]) with { SizeGb = 3.3 },
        AiModelCatalog.Ollama("llama3.3:70b", 70.6, ["completion", "tools"]) with { SizeGb = 42.5 }
    ];

    internal static Dictionary<string, ModelSpeedSample> DemoModelSpeed() => new()
    {
        ["ollama:qwen2.5-coder:14b"] = new ModelSpeedSample(38.6, 640, DateTime.Now)
    };

    /// <summary>Провайдер-заглушка замера скорости: ответ за 1,25 с с 60 токенами (48 ток/с).</summary>
    private sealed class SpeedStubProvider : IAiProvider
    {
        public Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AiResponse("{\"status\":\"ok\",\"steps\":[1,2,3,4,5]}", "заглушка", TimeSpan.FromSeconds(1.25), "Ollama", 20, 60));
    }

    private static async Task<int> CheckModelDirectorAsync(StringBuilder report)
    {
        int failures = 0;
        var models = DemoLocalModels();
        var ids = models.Select(m => m.Id).ToList();
        var director = new LocalModelDirectorService();
        director.Refresh(models, [(AiModelCatalog.LocalQwen3B, true), (AiModelCatalog.LocalQwen7B, false)], 32);
        var ranked = director.Candidates;
        var primary = director.Primary!;
        int Pos(string id) => ranked.ToList().FindIndex(c => c.Option.Id == id);
        bool order = primary.Option.Id == "ollama:qwen2.5-coder:14b" && primary.FitsMemory && primary.Available &&
                     Pos("ollama:qwen2.5-coder:14b") < Pos("ollama:qwen2.5-coder:1.5b") && Pos("ollama:llama3.3:70b") > Pos("ollama:mistral:7b") &&
                     !ranked.First(c => c.Option.Id == "ollama:llama3.3:70b").FitsMemory && ranked[^1].Option.Id == AiModelCatalog.LocalQwen7BId && !ranked[^1].Available;
        // Ручной выбор и автовыбор; скорость входит в индекс (замер на заглушке провайдера).
        director.SetManual("ollama:mistral:7b");
        bool manual = director.Primary?.Option.Id == "ollama:mistral:7b" && director.IsManual;
        var auto = director.AutoSelect();
        var sample = await director.MeasureAsync(new SpeedStubProvider(), primary.Option);
        var measured = director.Candidates.First(c => c.Option.Id == primary.Option.Id);
        bool speed = sample is { } s && Math.Abs(s.TokensPerSecond - 48) < 1e-9 && measured.SpeedScore == 10 && measured.EngineeringScore > primary.EngineeringScore - 1e-9 &&
                     director.Primary?.Option.Id == primary.Option.Id;
        // Не исключается из проектирования: список моделей не изменён, Совет КБ по тому же списку назначает её роли.
        var assignments = AgentModelRouter.Resolve(new CouncilSettings(), models, models[0], 32);
        bool council = models.Select(m => m.Id).SequenceEqual(ids) && assignments.Any(a => a.Model.Id == primary.Option.Id);
        Check(report, ref failures, order && manual && auto?.Option.Id == primary.Option.Id && speed && council,
            $"основная локальная модель: из {ranked.Count} моделей (32 ГБ ОЗУ) выбрана {primary.Option.ModelId} — {primary.EngineeringScore:0.#} / 100 ({string.Join("; ", primary.Reasons)}); " +
            $"далее {string.Join(", ", ranked.Skip(1).Take(4).Select(c => $"{c.Option.ModelId} {c.EngineeringScore:0.#}"))}; llama3.3:70b (42,5 ГБ) — {ranked.First(c => c.Option.Id == "ollama:llama3.3:70b").EngineeringScore:0.#}, " +
            $"Qwen 7B без файла — последней; ручной выбор mistral:7b и «Автовыбор» — обратно; замер {sample?.TokensPerSecond:0} ток/с → +{measured.SpeedScore:0} к индексу; " +
            $"в Совете КБ — роль {string.Join(", ", assignments.Where(a => a.Model.Id == primary.Option.Id).Select(a => AgentRoles.Info(a.Role).TitleRu))}");
        return failures;
    }

    // ═════════════════════════════ Задача 2: цепочка рассуждений ═════════════════════════════

    private static int CheckReasoning(StringBuilder report)
    {
        int failures = 0;
        var (elements, groups, axes) = CreateFrameScene();
        var scene = new SceneStateService();
        scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes, dimensions: []);
        var loads = new StructuralLoadSettings();
        var slab = scene.Elements.First(e => e.Category == BimCategory.Beam && ReinforcementCalculator.IsReinforced(e) && e.LevelIndex == 0);
        string before = string.Join("\n", scene.Elements.Select(e => string.Join(";", VerificationGateService.SnapshotOf(e).Select(p => p.Key + "=" + p.Value))));
        EngineeringHypothesis Thickness(double t) => new()
        {
            Title = $"Балка «{slab.DisplayTitle}» h = {t * 1000:0} мм",
            Norm = "СП 63.13330.2018, п. 8.1.8",
            Apply = s =>
            {
                var target = s.FindById(slab.Id)!;
                double top = target.TopY;
                target.SetSize(target.Width, t, target.Depth);
                target.PlaceOnBase(top - t);
                s.NotifyElementsChanged([target]);
                return null;
            },
            Actions = [$"Высота сечения балки «{slab.DisplayTitle}» → {t * 1000:0} мм (верх на месте)"]
        };
        var trace = EngineeringReasoningService.Reason(new ReasoningRequest
        {
            Task = "Подобрать минимальную высоту сечения балки",
            Scene = scene,
            Loads = loads,
            Intents = ["Цель: минимальная высота балки при K_исп ≤ 1,0"],
            Affected = [slab],
            Norms = ["СП 63.13330.2018, п. 8.1.8 (изгиб)", "СП 20.13330.2016, п. 15 (прогибы)"],
            Hypotheses = [Thickness(0.2), Thickness(0.6)],
            FocusIds = [slab.Id]
        }, out var chosen);
        string after = string.Join("\n", scene.Elements.Select(e => string.Join(";", VerificationGateService.SnapshotOf(e).Select(p => p.Key + "=" + p.Value))));
        var sandbox = trace.Steps[(int)ReasoningPhase.Sandbox];
        bool phases = trace.Steps.Select(s => s.Phase).SequenceEqual(Enum.GetValues<ReasoningPhase>()) && trace.Steps.Select(s => s.Title).SequenceEqual(ReasoningTrace.PhaseTitles);
        bool ok = phases && trace.Validate().Count == 0 && chosen?.Title == Thickness(0.6).Title && !trace.Hypotheses[0].Passed && trace.Hypotheses[0].RejectReason.Contains("K_исп") &&
                  trace.Hypotheses[1].Chosen && trace.Corrections == 1 && trace.IsVerified && sandbox.IsComputed && sandbox.Metrics.Count == 4 &&
                  trace.Steps[(int)ReasoningPhase.SelfCheck].Status == ReasoningStepStatus.Corrected && before == after && scene.UndoDepth == 0 &&
                  trace.Summary == "Цепочка рассуждений (5 шагов · расчёт проверен)";
        // Битая цепочка ловится проверкой.
        var broken = new ReasoningTrace();
        broken.Step(ReasoningPhase.Decomposition).Lines.Add("x");
        bool validation = broken.Validate().Count >= 3;
        Check(report, ref failures, ok && validation,
            $"цепочка рассуждений: 5 фаз по порядку ({string.Join(" → ", trace.Steps.Select(s => s.Number))}), песочница — {string.Join(", ", sandbox.Metrics.Select(m => m.ToText(W3Ru)))}; " +
            $"балка «{slab.DisplayTitle}»: гипотеза h = 200 мм отклонена ({trace.Hypotheses[0].RejectReason}), принята h = 600 мм (K сцены {trace.Hypotheses[1].KAfter:0.00}, балки ≤ 1), корректировок 1; живая сцена не изменена, шагов журнала 0; " +
            $"«{trace.Summary}», неполная цепочка — замечаний {broken.Validate().Count}, {trace.ElapsedMs:0} мс");
        return failures;
    }

    // ═════════════════════════════ Задача 3: веб-импорт расценок ═════════════════════════════

    internal const string DemoStoreUrl = "https://stroybaza-sever.example/catalog/beton-armatura";

    /// <summary>Демонстрационная страница магазина: JSON-LD (бетон с доставкой), microdata (двутавр за п.м), таблица (арматура за т, п.м, кг), текст.</summary>
    internal const string DemoStoreHtml = """
        <!DOCTYPE html>
        <html lang="ru"><head><meta charset="windows-1251">
        <title>Бетон и арматура с доставкой — купить в Мурманске | Стройбаза Север</title>
        <meta property="og:site_name" content="Стройбаза Север">
        <script type="application/ld+json">
        {"@context":"https://schema.org","@graph":[
          {"@type":"Product","name":"Бетон B25 (М350) П4 W6 F150 с доставкой","description":"Товарный бетон с доставкой миксером по городу",
           "offers":{"@type":"Offer","price":"7450","priceCurrency":"RUB","priceSpecification":{"@type":"UnitPriceSpecification","price":7450,"unitCode":"MTQ"}}}
        ]}
        </script></head>
        <body>
        <h1>Прайс-лист на 04.11.2026</h1>
        <p>Доставка бетона по городу: 500 руб/м&sup3; (миксер 7 м&sup3;).</p>
        <div itemscope itemtype="https://schema.org/Product">
          <span itemprop="name">Двутавр 20Б1 С245 ГОСТ Р 57837-2017</span>
          <div itemprop="offers" itemscope itemtype="https://schema.org/Offer"><meta itemprop="price" content="1450"><span>1 450 руб. за п.м</span></div>
        </div>
        <table class="price">
          <tr><th>Наименование</th><th>Ед. изм.</th><th>Цена, руб.</th></tr>
          <tr><td>Арматура А500С &Oslash;12 мм</td><td>т</td><td>72 900 руб.</td></tr>
          <tr><td>Арматура А500С &Oslash;16 мм</td><td>п.м</td><td>89 руб.</td></tr>
          <tr><td>Арматура А240 &Oslash;8 гладкая</td><td>кг</td><td>78 руб.</td></tr>
          <tr><td>Стеклопакет двухкамерный 4-10-4-10-4</td><td>м&sup2;</td><td>5 600 руб.</td></tr>
          <tr><td>Кирпич облицовочный М150</td><td>шт</td><td>32 руб.</td></tr>
        </table>
        <ul><li>Бетон В30 (М400) П4 — 7 300 руб./м3 без доставки</li></ul>
        </body></html>
        """;

    /// <summary>HTTP без сети: любая ссылка отдаёт демонстрационную страницу в Windows-1251 (charset — только в meta).</summary>
    internal sealed class DemoStoreHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var content = new ByteArrayContent(Encoding.GetEncoding(1251).GetBytes(DemoStoreHtml));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    private static async Task<int> CheckPriceExtractionAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        var handler = new DemoStoreHandler();
        var items = await SmartPriceExtractorService.ExtractFromUrlAsync(new Uri(DemoStoreUrl), handler: handler);
        var direct = SmartPriceExtractorService.ExtractFromHtml(DemoStoreHtml, new Uri(DemoStoreUrl));
        ExtractedPriceItem? Item(string code) => items.FirstOrDefault(i => i.MaterialCode == code);
        double perTonD16 = 89 / (0.00617 * 16 * 16) * 1000;
        double beamKg = (2 * 100 * 8.5 + (200 - 2 * 8.5) * 5.6) * 1e-6 * QuantityScheduleService.SteelDensity;
        // Все предложения до выбора лучшего: пересчёт Ø16 за п.м → т.
        var all = SmartPriceExtractorService.ParseHtml(DemoStoreHtml).Select(o => SmartPriceExtractorService.Normalize(o, "Стройбаза Север", "x", PriceSourceKind.WebPage)).OfType<ExtractedPriceItem>().ToList();
        var d16 = all.FirstOrDefault(i => i.MaterialCode == "A500C" && i.SourceUnit == "м");
        bool prices = Item("B25") is { NewPriceRub: 6950, Confidence: PriceConfidence.Medium } b25 && b25.Conversion.Contains("доставк") &&
                      Item("B30")?.NewPriceRub == 7300 && Item("A500C")?.NewPriceRub == 72900 && Item("A240")?.NewPriceRub == 78000 &&
                      Item("C245") is { } beam && Math.Abs(beam.NewPriceRub - Math.Round(1450 / beamKg * 1000, 2)) < 1e-6 && Item("GLAZING")?.NewPriceRub == 5600 &&
                      d16 is not null && Math.Abs(d16.NewPriceRub - Math.Round(perTonD16, 2)) < 1e-6 && Item("BRICK") is { InEstimate: false };
        bool meta = items.All(i => i.Supplier == "Стройбаза Север" && i.SourceDocument.Contains(DemoStoreUrl) && i.SourceDocument.Contains(DateTime.Today.ToString("dd.MM.yyyy", W3Ru))) &&
                    handler.Requests == 1 && items.Count == direct.Count && items.Zip(direct).All(p => p.First.MaterialCode == p.Second.MaterialCode && p.First.NewPriceRub == p.Second.NewPriceRub);
        // Кодировки: UTF-8 без указания и Windows-1251 по meta дают одинаковый текст.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string utf8Page = DemoStoreHtml.Replace("windows-1251", "utf-8"), noCharset = DemoStoreHtml.Replace("<meta charset=\"windows-1251\">", string.Empty);
        bool decode = SmartPriceExtractorService.Decode(Encoding.UTF8.GetBytes(utf8Page)) == utf8Page && SmartPriceExtractorService.Decode(Encoding.UTF8.GetBytes(noCharset)) == noCharset &&
                      SmartPriceExtractorService.Decode(Encoding.GetEncoding(1251).GetBytes(noCharset)) == noCharset &&
                      SmartPriceExtractorService.Decode(Encoding.GetEncoding(1251).GetBytes(DemoStoreHtml)) == DemoStoreHtml;
        Check(report, ref failures, prices && meta && decode,
            $"веб-импорт по ссылке: страница в Windows-1251 (charset в meta) — позиций {items.Count}: B25 {Item("B25")?.NewPriceRub:N0} ₽/м³ ({Item("B25")?.Conversion}), B30 {Item("B30")?.NewPriceRub:N0}, " +
            $"A500C {Item("A500C")?.NewPriceRub:N0} ₽/т (Ø16 за п.м → {d16?.NewPriceRub:N0} ₽/т: {d16?.Conversion}), A240 {Item("A240")?.NewPriceRub:N0} ₽/т (за кг), двутавр 20Б1 → {Item("C245")?.NewPriceRub:N0} ₽/т ({beamKg:0.##} кг/м), " +
            $"стеклопакет {Item("GLAZING")?.NewPriceRub:N0} ₽/м², кирпич — справочно; поставщик «{items.FirstOrDefault()?.Supplier}» (og:site_name), основание — URL и дата; " +
            "кодировка: meta windows-1251 / utf-8 и без указания (проверка UTF-8, иначе 1251) — текст без потерь");
        return failures;
    }

    /// <summary>Прошлый проект .aia со сметой (поставщик, основание, дата 12.03.2025) — для автотеста и съёмки окна проверки.</summary>
    internal static string WriteDemoPastProject(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Проект_ЖК_Северный_2025.aia");
        const string json = """
            {
              "formatVersion": 6,
              "parameters": {
                "costEstimate": {
                  "concretePricePerM3": 6200, "rebarPricePerTon": 68000, "savedAt": "2025-03-12T10:20:00", "catalogName": "ЖБИ-Комплект 2025", "catalogDate": "2025-03-01T00:00:00",
                  "lines": [
                    { "key": "concrete", "code": "B25", "title": "Бетон B25", "unit": "м³", "unitPriceRub": 6200, "supplier": "ООО «ЖБИ-Комплект»", "sourceDocument": "Счёт № 118 от 01.03.2025", "manual": false },
                    { "key": "rebar-working", "code": "A500C", "title": "Арматура А500С (рабочая)", "unit": "т", "unitPriceRub": 68000, "supplier": "ООО «ЖБИ-Комплект»", "sourceDocument": "Счёт № 118 от 01.03.2025", "manual": false },
                    { "key": "rebar-a240", "code": "A240", "title": "Арматура А240 (хомуты)", "unit": "т", "unitPriceRub": 71000, "supplier": "ООО «ЖБИ-Комплект»", "sourceDocument": "Счёт № 118 от 01.03.2025", "manual": false }
                  ]
                }
              }
            }
            """;
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static int CheckPastEstimates(string directory, StringBuilder report)
    {
        int failures = 0;
        string aia = WriteDemoPastProject(directory);
        var fromProject = SmartPriceExtractorService.ExtractFromProject(File.ReadAllText(aia), Path.GetFileName(aia));
        var csv = new MaterialPriceCatalog
        {
            Name = "Металлобаза 2024", UpdatedAt = new DateTime(2024, 11, 20),
            Items = [new SupplierPriceItem { MaterialCode = "C245", Category = "Стальной прокат", DisplayName = "Лист С245", Unit = "т", UnitPriceRub = 82000, SupplierName = "АО «Металлобаза»", SourceDocument = "Прайс от 20.11.2024" }]
        };
        string csvPath = Path.Combine(directory, "wave3_past_estimate.csv");
        File.WriteAllBytes(csvPath, csv.ToCsvBytes());
        var fromCsv = SmartPriceExtractorService.ExtractFromCatalog(MaterialPriceCatalog.Load(csvPath), Path.GetFileName(csvPath));
        // Кусок таблицы Excel (ячейки через табуляцию, без «руб»).
        var pasted = SmartPriceExtractorService.ExtractFromText("Наименование\tЕд.\tЦена\nБетон В20 П4\tм3\t5 900\nАрматура А500С d12\tт\t70 500", "Сметчик");
        var batch = SmartPriceExtractorService.BuildBatch(fromProject.Concat(fromCsv).Concat(pasted), code => code == "B25" ? 8500 : null, 5);
        ExtractedPriceItem? Find(string code, PriceSourceKind kind) => batch.Items.FirstOrDefault(i => i.MaterialCode == code && i.Kind == kind);
        bool ok = fromProject.Count == 3 && Find("B25", PriceSourceKind.PastProject) is { NewPriceRub: 6510, OldPriceRub: 8500 } b25 && b25.Conversion.Contains("индексация") &&
                  b25.Supplier == "ООО «ЖБИ-Комплект»" && b25.SourceDocument.Contains("01.03.2025") && Math.Abs(b25.DeltaPercent - (6510 - 8500) / 85.0) < 1e-9 &&
                  Find("A240", PriceSourceKind.PastProject)?.NewPriceRub == 74550 && Find("C245", PriceSourceKind.PastEstimate)?.NewPriceRub == 86100 &&
                  Find("B20", PriceSourceKind.PastedText)?.NewPriceRub == 5900 && Find("A500C", PriceSourceKind.PastedText)?.NewPriceRub == 70500;
        Check(report, ref failures, ok,
            $"прошлые сметы: .aia (смета от {fromProject.FirstOrDefault()?.SourceDate:dd.MM.yyyy}, {fromProject.Count} позиции, поставщик и счёт), .csv прайс 2024 и вставка из Excel; индексация +5 %: " +
            $"B25 6 200 → {Find("B25", PriceSourceKind.PastProject)?.NewPriceRub:N0} ₽/м³ (было в смете 8 500, Δ {Find("B25", PriceSourceKind.PastProject)?.DeltaPercent:0.0} %), A240 71 000 → {Find("A240", PriceSourceKind.PastProject)?.NewPriceRub:N0}, " +
            $"C245 82 000 → {Find("C245", PriceSourceKind.PastEstimate)?.NewPriceRub:N0}; вставка без индексации: B20 {Find("B20", PriceSourceKind.PastedText)?.NewPriceRub:N0}, A500C {Find("A500C", PriceSourceKind.PastedText)?.NewPriceRub:N0}");
        return failures;
    }

    private static int CheckPriceConfirmation(StringBuilder report)
    {
        int failures = 0;
        if (App.MainWindowInstance is not MainWindow window)
        {
            Check(report, ref failures, false, "главное окно недоступно для проверки подтверждения расценок");
            return failures;
        }
        var vm = window.ViewModel;
        var (elements, groups, axes) = CreateFrameScene();
        vm.Scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes, dimensions: []);
        var state = vm.SnapshotPricingState();
        int catalogs = vm.Settings.Current.PriceCatalogs.Count;
        string active = vm.Settings.Current.ActivePriceCatalog;
        double before = vm.FrameTotalWith(null);
        var beforeLines = vm.BuildPricedEstimate(vm.BuildQuantitySchedule()).Lines.ToDictionary(l => l.Key, l => l.UnitPriceRub);
        var items = SmartPriceExtractorService.ExtractFromHtml(DemoStoreHtml, new Uri(DemoStoreUrl));
        var batch = SmartPriceExtractorService.BuildBatch(items, vm.CurrentUnitPrice);
        // Пользователь правит цену арматуры в окне проверки и снимает отметку со стеклопакета.
        batch.Items.First(i => i.MaterialCode == "A500C").NewPriceRub = 71500;
        batch.Items.First(i => i.MaterialCode == "GLAZING").IsChecked = false;
        double preview = vm.FrameTotalAfter(batch);
        bool untouched = vm.FrameTotalWith(null) == before && !batch.Confirmed && vm.ActivePriceCatalog?.Name == state.Active?.Name;
        var catalog = vm.ApplyPriceBatch(batch);
        double after = vm.FrameTotalWith(null);
        // Ожидаемый итог — те же формулы сметы по ведомости с подтверждёнными ценами.
        var schedule = vm.BuildQuantitySchedule();
        var estimate = vm.BuildPricedEstimate(schedule);
        string working = MaterialLibrary.NormalizeCode(StructuralNorms.Title(schedule.WorkingClass == RebarClass.A240 ? RebarClass.A400 : schedule.WorkingClass));
        bool workingMatch = working != "A500C" || estimate.Line(CostEstimateService.RebarWorking)?.UnitPriceRub == 71500;
        // Бетон класса проекта: цена страницы, если класс на ней есть (B25 / B30), иначе прежняя цена строки.
        var concreteLine = estimate.Line(CostEstimateService.Concrete)!;
        var concreteItem = batch.Items.FirstOrDefault(i => i.IsChecked && i.MaterialCode == concreteLine.Code);
        bool concreteMatch = concreteItem is null ? concreteLine.UnitPriceRub == beforeLines[CostEstimateService.Concrete]
            : concreteLine.UnitPriceRub == concreteItem.NewPriceRub && concreteLine.Supplier == "Стройбаза Север" && concreteLine.Origin == PriceOrigin.SupplierCatalog;
        bool applied = batch.Confirmed && after == preview && after != before && concreteMatch &&
                       estimate.Line(CostEstimateService.RebarA240)?.UnitPriceRub == 78000 && workingMatch && catalog.Find("GLAZING") is null &&
                       Math.Abs(after - estimate.FrameTotal) < 1e-6 && vm.Settings.Current.PriceCatalogs.Count == catalogs && vm.Settings.Current.ActivePriceCatalog == active;
        vm.RestorePricingState(state);
        bool restored = vm.FrameTotalWith(null) == before;
        Check(report, ref failures, untouched && applied && restored,
            $"подтверждение расценок: до «Да, всё верно» смета каркаса {before:N0} ₽ не меняется (предпросмотр {preview:N0} ₽), после — {after:N0} ₽ ≡ сумме строк сметы " +
            $"(бетон {estimate.Line(CostEstimateService.Concrete)?.UnitPriceRub:N0} ₽/м³ «{estimate.Line(CostEstimateService.Concrete)?.Supplier}», А240 {estimate.Line(CostEstimateService.RebarA240)?.UnitPriceRub:N0} ₽/т, " +
            $"рабочая {working} {estimate.Line(CostEstimateService.RebarWorking)?.UnitPriceRub:N0} ₽/т — ручная правка в окне); снятая отметка не применена; база «{catalog.Name}» — на сессию, settings.json не изменён");
        return failures;
    }

    // ═════════════════════════════ Задача 4: Инженерный агент ═════════════════════════════

    internal const string AgentDemoTask = "Проведи аудит расчётной схемы, увеличь высоту 1-го этажа до 3600 мм, подбери сечение колонн по ГОСТ и посчитай показатели Renga (объёмы нетто и опалубку)";

    private static string SceneState(SceneStateService scene) =>
        string.Join("\n", scene.Elements.Select(e => e.Id + ":" + string.Join(";", VerificationGateService.SnapshotOf(e).Select(p => p.Key + "=" + p.Value)) +
                                                     ";" + string.Join(",", e.Size.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + ";" + string.Join(",", e.Position.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) +
                                                     ";" + StandardTypeMark.Read(e)));

    private static async Task<int> CheckAgentAsync(StringBuilder report)
    {
        int failures = 0;
        var (elements, groups, axes) = CreateFrameScene();
        var scene = new SceneStateService();
        scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes, dimensions: []);
        var loads = new StructuralLoadSettings();
        string group = scene.Elements.GroupBy(e => e.BuildingGroupId).OrderByDescending(g => g.Count()).First().Key;
        double h1 = BatchDimensionService.Describe(scene, group)!.Storeys[0].Height;
        string initial = SceneState(scene);
        var plan = BimEngineeringAgentService.Plan(AgentDemoTask);
        var result = await BimEngineeringAgentService.RunAsync(AgentDemoTask, new AgentContext { Scene = scene, Loads = loads });
        string final = SceneState(scene);
        double h1After = BatchDimensionService.Describe(scene, group)!.Storeys[0].Height;
        var gost = result.ToolCalls.First(c => c.Name == AgentToolRegistry.AssignGostStandardType);
        var targets = gost.Argument("ids").Split(',').Select(scene.FindById).OfType<ArchitecturalElement>().ToList();
        double kTargets = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), loads) is var r ? targets.Max(t => r.For(t.Id)?.Utilization ?? 0) : 0;
        bool planned = plan.Select(c => c.Name).SequenceEqual([AgentToolRegistry.RunFeaAudit, AgentToolRegistry.BatchResizeBuilding, AgentToolRegistry.AssignGostStandardType, AgentToolRegistry.ComputeRengaQuantities]) &&
                       plan[1].Argument("storey") == "1" && plan[1].Argument("height_mm") == "3600" && plan[2].Argument("mark") == "auto";
        bool executed = result.Success && result.Changed && scene.UndoDepth == 1 && result.ToolCalls.Count == 4 && result.ToolCalls.All(c => c.Status == AgentToolStatus.Succeeded) &&
                        Math.Abs(h1After - 3.6) < 1e-6 && targets.Count > 0 && targets.All(t => StandardTypeMark.Read(t) is not null) && kTargets <= 1.0 + 1e-9 &&
                        result.Trace.Validate().Count == 0 && result.Trace.IsVerified && result.Trace.Hypotheses.Count >= 2 &&
                        result.ToolCalls.Last().Results.Any(x => x.Name == "V_нетто");
        scene.Undo();
        bool undo = SceneState(scene) == initial && scene.UndoDepth == 0;
        scene.Redo();
        bool redo = SceneState(scene) == final;
        // Веб-импорт в задании агента: ссылка → окно подтверждения (здесь — заглушка «Да, всё верно»), вне транзакции модели.
        int asked = 0;
        var priced = await BimEngineeringAgentService.RunAsync($"Подтяни цены по ссылке {DemoStoreUrl} с индексацией 5 %", new AgentContext
        {
            Scene = scene, Loads = loads,
            PriceHost = (call, _) =>
            {
                asked++;
                return Task.FromResult<string?>(call.Argument("url") == DemoStoreUrl && call.Number("index_percent") == 5 ? "применено позиций 6" : null);
            }
        });
        bool price = asked == 1 && !priced.Changed && scene.UndoDepth == 1 && priced.ToolCalls.Single().Status == AgentToolStatus.Succeeded;
        // Нераспознанная задача: безопасные инструменты только чтения.
        var unknown = await BimEngineeringAgentService.RunAsync("Расскажи о проекте", new AgentContext { Scene = scene, Loads = loads });
        bool fallback = !unknown.Changed && unknown.ToolCalls.Select(c => c.Name).SequenceEqual([AgentToolRegistry.InspectProject, AgentToolRegistry.RunFeaAudit]);
        Check(report, ref failures, planned && executed && undo && redo && price && fallback,
            $"Инженерный агент: «{AgentDemoTask}» → план {string.Join(" → ", plan.Select(c => c.Name))}; рассуждения: гипотез {result.Trace.Hypotheses.Count} " +
            $"({string.Join(", ", result.Trace.Hypotheses.Select(h => $"{h.Title.Split("типоразмер ").Last()} K {h.KAfter:0.00}{(h.Chosen ? " — принята" : h.Passed ? string.Empty : " — отклонена")}"))}); " +
            $"выполнено за 1 шаг журнала «{result.TransactionName}»: 1-й этаж {h1 * 1000:0} → {h1After * 1000:0} мм, «{gost.Summary}», K колонн {kTargets:0.00}, {result.ToolCalls.Last().Summary}; " +
            $"Undo ≡ исходному, Redo ≡ итогу; веб-импорт — окно подтверждения 1 раз, модель не меняется; нераспознанная задача — сводка и аудит; {result.ElapsedMs:0} мс");
        return failures;
    }

    private static async Task<int> CheckAgentInWindowAsync(StringBuilder report)
    {
        int failures = 0;
        if (App.MainWindowInstance is not MainWindow window)
        {
            Check(report, ref failures, false, "главное окно недоступно для Инженерного агента");
            return failures;
        }
        var vm = window.ViewModel;
        var (elements, groups, axes) = CreateFrameScene();
        vm.Scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes, dimensions: []);
        bool council = vm.IsCouncilMode;
        vm.Review = null;
        vm.IsAgentMode = true;
        bool mode = vm.IsAgentMode && !vm.IsSingleAgentMode && !vm.IsCouncilMode && vm.IsAgentPanelVisible && vm.IsCouncilDockVisible;
        int chat = vm.ChatMessages.Count;
        var result = await vm.RunAgentAsync("Проведи аудит расчётной схемы и задай колоннам защитный слой 30 мм, диаметр арматуры 25 и шаг хомутов 150");
        var message = vm.ChatMessages.LastOrDefault(m => m.HasTrace);
        var columns = vm.Scene.Elements.Where(e => e.Category == BimCategory.Column && ReinforcementCalculator.IsReinforced(e)).ToList();
        bool applied = result is { Changed: true } && columns.All(c => StructuralDesign.Cover(c) == 0.03 && StructuralDesign.BarDiameter(c) == 0.025 && StructuralDesign.StirrupSpacing(c) == 0.15) &&
                       message is { HasToolCalls: true } && message.TraceSummary.StartsWith("Цепочка рассуждений (5 шагов") && vm.ChatMessages.Count > chat && vm.LastAgentRun == result;
        vm.UndoAgentCommand.Execute(null);
        bool undone = columns.All(c => StructuralDesign.BarDiameter(c) is null) && vm.Scene.UndoDepth == 0;
        vm.RestoreCouncilAfterAgent();
        vm.IsAgentPanelOpen = false;
        bool reset = !vm.IsAgentMode && vm.IsCouncilMode == council && !vm.IsAgentPanelVisible;
        Check(report, ref failures, mode && applied && undone && reset,
            $"агент в главном окне: режим «Инженерный агент» в сегменте дока (Совет КБ выключен, панель агента в боковом доке), задание из панели команд — " +
            $"{result?.ToolCalls.Count} инструмента, Ø25 / шаг 150 / слой 30 у {columns.Count} колонн, в ленте — «{message?.TraceSummary}» и «{message?.ToolCallsSummary}»; " +
            $"«Отменить действия агента» — 1 Undo, параметры сняты; возврат к «BIM-Координатор»");
        return failures;
    }

    private static int CheckWave3Strings(StringBuilder report)
    {
        int failures = 0;
        var texts = new List<string>();
        if (typeof(LocalizationService).GetField("Wave3Strings", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is Dictionary<string, (string Ru, string En)> strings)
            texts.AddRange(strings.Values.SelectMany(v => new[] { v.Ru, v.En }));
        int keys = texts.Count / 2;
        texts.AddRange(ReasoningTrace.PhaseTitles);
        texts.AddRange(AgentToolRegistry.Tools.SelectMany(t => new[] { t.Title, t.Description }));
        var banned = new Regex(@"(?<![\p{L}])(ИИ|AI|LLM)(?![\p{L}])", RegexOptions.CultureInvariant);
        var dirty = texts.Where(t => banned.IsMatch(t) || t.EnumerateRunes().Any(r => r.Value >= 0x1F300 && r.Value <= 0x1FAFF || r.Value is >= 0x2600 and <= 0x27BF)).ToList();
        bool terms = texts.Any(t => t.Contains("Локальное ядро")) && texts.Any(t => t.Contains("Локальная модель") || t.Contains("локальная модель")) && texts.Any(t => t.Contains("Инженерный агент")) &&
                     texts.Any(t => t.Contains("Цепочка рассуждений") || t.Contains("цепочка рассуждений")) && texts.Any(t => t.Contains("Веб-импорт прайса по ссылке"));
        Check(report, ref failures, keys > 80 && dirty.Count == 0 && terms,
            $"строки волны 3: ключей {keys} (RU/EN), фазы рассуждений и {AgentToolRegistry.Tools.Count} инструментов агента — без эмодзи и «ИИ / AI / LLM»; термины «Локальное ядро», «Локальная модель», " +
            "«Инженерный агент», «Цепочка рассуждений», «Веб-импорт прайса по ссылке»" + (dirty.Count > 0 ? " [" + string.Join(" | ", dirty.Take(3)) + "]" : string.Empty));
        return failures;
    }

    // ═════════════════════════════ Съёмка ═════════════════════════════

    /// <summary>
    /// Сцена съёмки 3D-армирования: безбалочная плита на 9 колоннах, у колонн — Ø25, шаг хомутов 100 мм, защитный слой 30 мм, у плиты —
    /// каркасы продавливания; выделены плита и колонны, включена схема армирования.
    /// </summary>
    internal static void PrepareRebarDemo(MainViewModel vm)
    {
        var scene = PunchingScene(rebar: true);
        foreach (var c in scene.Where(e => e.Category == BimCategory.Column)) StructuralDesign.WriteRebar(c, 0.025, 0.1, 0.03);
        vm.Scene.LoadScene(scene, [new BuildingGroup("Волна 3")], recordUndo: false, clearHistory: true, dimensions: []);
        var targets = vm.Scene.Elements.Where(e => e.Category is BimCategory.Column or BimCategory.Slab).ToList();
        // Главный выделенный — угловая колонна: Gizmo не заслоняет оголовок средней колонны в кадре.
        vm.Scene.SetSelection(targets, targets.First());
        if (!vm.IsRebarModule) vm.Modules = vm.Modules with { Reinforcement3D = true };
        vm.IsRebarView = true;
    }
}
#endif
