#if DEBUG
using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.Services.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace AIArchitectStudio.Services;

/// <summary>
/// Автотест Мультиагентного конструкторского бюро: плагины-проверщики на сцене с намеренными ошибками (колонна на тонкой
/// консоли, плита 200 мм на пролёте 10.5 м с K_исп &gt; 1.2, марш без проёма, окно на колонне), цикл согласования
/// User-in-the-Loop (Вариант 1 и 2, текстовое уточнение ГИПа, ровно один шаг Undo и откат), маршрутизация ролей
/// Multi-LLM и полный прогон Совета через сценарный шлюз моделей (без сети).
/// </summary>
public static partial class CadSelfTestService
{
    public const string CouncilGroup = "Корпус КБ-тест";

    /// <summary>
    /// Двухэтажный каркас по осям 1-2-3 (0 / 6 / 16.5 м) × А-Б-В (0 / 6 / 12 м): пролёт 10.5 м с плитой 200 мм, консоль
    /// 2.5 м с колонной второго этажа на ней (вместо оси 3/Б), марш без проёма в плите, окно ОК-1 на колонне 2/В.
    /// </summary>
    public static (List<ArchitecturalElement> Elements, List<BuildingGroup> Groups, List<StructuralGridAxis> Axes) CreateCouncilTestScene()
    {
        const string g = CouncilGroup;
        var list = new List<ArchitecturalElement>();
        int n = 0;
        ArchitecturalElement Add(string type, string name, double x, double baseY, double z, double w, double h, double d, int level = 0, string material = "concrete", double rot = 0)
        {
            var e = new ArchitecturalElement { Id = $"el_kb{++n:D3}", Type = type, Name = name, BuildingGroupId = g, LevelIndex = level, Material = material };
            e.SetSize(w, h, d);
            e.SetPosition(x, baseY + h / 2, z);
            e.SetRotationY(rot);
            list.Add(e);
            return e;
        }
        double[] xs = [0, 6, 16.5];
        double[] zs = [0, 6, 12];
        Add(ElementTypes.Foundation, "Фундаментная плита", 8.25, -0.5, 6, 18.0, 0.5, 13.2);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Add(ElementTypes.Column, $"Колонна 1-{i + 1}{StructuralAxesBuilderLetter(j)}", xs[i], 0, zs[j], 0.4, 2.8, 0.4);
        Add(ElementTypes.Slab, "Плита перекрытия 1", 9.35, 2.8, 6, 19.3, 0.2, 12.6);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                bool shifted = i == 2 && j == 1;
                Add(ElementTypes.Column, shifted ? "Колонна 2-3Б на консоли" : $"Колонна 2-{i + 1}{StructuralAxesBuilderLetter(j)}", shifted ? 18.6 : xs[i], 3.0, zs[j], 0.4, 2.8, 0.4, 1);
            }
        var roof = Add(ElementTypes.Roof, "Плоская кровля", 9.35, 5.8, 6, 19.3, 0.2, 12.6, 1);
        roof.RoofShape = RoofShapes.Flat;
        Add(ElementTypes.Stairs, "Лестница Л-1", 1.6, 0, 3.0, 1.2, 3.0, 3.0);
        var wall = Add(ElementTypes.Wall, "Стена по оси В", 8.25, 0, 12, 16.9, 2.8, 0.3, 0, "brick");
        var window = Add(ElementTypes.Window, "Окно ОК-1", 6.6, 0.9, 12, 1.5, 1.5, 0.36, 0, "glass");
        window.HostElementId = wall.Id;
        var axes = new List<StructuralGridAxis>();
        for (int i = 0; i < 3; i++) axes.Add(new StructuralGridAxis((i + 1).ToString(Inv), StructuralGridAxis.AlongZ, xs[i], g));
        for (int j = 0; j < 3; j++) axes.Add(new StructuralGridAxis(StructuralAxesBuilderLetter(j), StructuralGridAxis.AlongX, zs[j], g));
        return (list, [new BuildingGroup(g, "office", "#FF9F0A")], axes);
    }

    private static string StructuralAxesBuilderLetter(int j) => AIArchitectStudio.Rendering.StructuralAxesBuilder.Letter(j);

    private static SceneStateService CouncilSceneState()
    {
        var (elements, groups, axes) = CreateCouncilTestScene();
        var scene = new SceneStateService();
        scene.LoadScene(elements, groups, recordUndo: false, clearHistory: true, axes: axes);
        return scene;
    }

    // ═════════════════════════════ 1. Плагины-проверщики ═════════════════════════════

    private static int CheckVerifierPlugins(StringBuilder report)
    {
        int failures = 0;
        var pipeline = BimVerificationPipeline.CreateDefault();
        string[] required = ["KE", "SPAN", "VLP", "ACCESS", "SEIS", "XSECT", "CLASH", "AUDIT", "SUN"];
        Check(report, ref failures, required.All(id => pipeline.Verifiers.Any(v => v.Id == id)),
            $"плагины обнаружены автоматически ({pipeline.Verifiers.Count}): {string.Join(", ", pipeline.Verifiers.Select(v => v.Id))}");

        var scene = CouncilSceneState();
        var ctx = VerificationContext.FromScene(scene, new StructuralLoadSettings());
        var result = pipeline.Run(ctx);
        report.AppendLine($"  Сцена с намеренными ошибками: {result.Summary}");
        foreach (var t in result.Timings) report.AppendLine($"    {t.Id,-7} {t.Title}: {t.Findings} замеч., {t.Milliseconds:0.0} мс");
        foreach (var f in result.Findings)
            report.AppendLine($"    [{f.Code}] {f.SeverityTitle}: {f.Title} → {string.Join(" | ", f.ProposedFixOptions.Select(o => $"{VerificationText.AuthorTitle(o.Author)}: {o.Title}{(o.PredictedUtilization is { } k ? $" (K→{k:0.00})" : string.Empty)}"))}");

        var column = scene.Elements.First(e => e.Name == "Колонна 2-3Б на консоли");
        var slab = scene.Elements.First(e => e.Name == "Плита перекрытия 1");
        var stairs = scene.Elements.First(e => e.Name == "Лестница Л-1");
        var window = scene.Elements.First(e => e.Name == "Окно ОК-1");
        var hanging = result.Findings.FirstOrDefault(f => f.Code.StartsWith("VLP-", StringComparison.Ordinal) && f.IsCritical && f.AffectedElementIds.Contains(column.Id));
        var overload = result.Findings.FirstOrDefault(f => f.Code == "KE-SLAB" && f.AffectedElementIds.Contains(slab.Id));
        var stair = result.Findings.FirstOrDefault(f => f.Code == "ACCESS-STAIR-OPENING" && f.AffectedElementIds.Contains(stairs.Id));
        var opening = result.Findings.FirstOrDefault(f => f.Code == "CLASH-OPENING-COLUMN" && f.AffectedElementIds.Contains(window.Id));
        var ratio = result.Findings.FirstOrDefault(f => f.Code == "SPAN-SLAB" && f.AffectedElementIds.Contains(slab.Id));
        Check(report, ref failures, hanging is not null && hanging.HasFixes && hanging.ProposedFixOptions.Count(o => !o.IsKeep) >= 2,
            $"VerticalLoadPathVerifier: колонна второго этажа на консоли без опоры — {hanging?.Code ?? "не найдена"}, вариантов {hanging?.ProposedFixOptions.Count(o => !o.IsKeep) ?? 0}");
        var slabFindings = result.Findings.Where(f => f.Code == "KE-SLAB").ToList();
        Check(report, ref failures, overload is { Utilization: > 1.2 } && overload.ProposedFixOptions.Count(o => !o.IsKeep) >= 2 && overload.ProposedFixOptions.Any(o => o.Author == FixAuthor.Structural) &&
                                    slabFindings.Any(f => f.ProposedFixOptions.Any(o => o.Author == FixAuthor.Architect)) &&
                                    slabFindings.SelectMany(f => f.ProposedFixOptions).Where(o => !o.IsKeep).All(o => o.PredictedUtilization <= DesignFixEngine.TargetUtilization),
            $"StructuralCapacityVerifier: плита 200 мм на пролёте 10.5 м — K_исп {overload?.Utilization:0.00} > 1.2; варианты утолщения, балочной клетки (Конструктор) и промежуточных колонн (Архитектор) с прогнозом K ≤ 0.95 по моделированию");
        Check(report, ref failures, ratio is not null && ratio.HasFixes,
            $"SpanToDepthRatioVerifier: {ratio?.Title ?? "пропорция плиты не проверена"}");
        Check(report, ref failures, stair is not null && stair.HasFixes && opening is not null && opening.ProposedFixOptions.Count(o => !o.IsKeep) >= 2,
            $"StairAndOpeningAccessVerifier и ClashVerifier: марш без проёма ({stair?.Code ?? "нет"}), окно на колонне ({opening?.Code ?? "нет"}, вариантов {opening?.ProposedFixOptions.Count(o => !o.IsKeep) ?? 0})");
        Check(report, ref failures, result.Findings.All(f => f.ProposedFixOptions.Any(o => o.IsKeep)) && result.Findings.Where(f => f.IsCritical).All(f => f.HasFixes),
            "каждое замечание — VerificationFinding с ProposedFixOptions (у критичных есть исправление, у всех — «Оставить как есть»)");

        // Устойчивость плагинов на реальных сценах: пресеты и тестовый каркас — без сбоев плагинов, быстро.
        var scenes = new List<(string Title, IReadOnlyList<ArchitecturalElement> Elements, IReadOnlyList<BuildingGroup> Groups)>();
        foreach (var id in new[] { PresetScenesService.Cottage, PresetScenesService.Tower, PresetScenesService.Pavilion })
        {
            var demo = PresetScenesService.Create(id, LocalizationService.Russian);
            scenes.Add((demo.Title, demo.Elements, demo.Groups));
        }
        var (frameElements, frameGroups, _) = CreateFrameScene();
        scenes.Add(("Каркасный корпус", frameElements, frameGroups));
        bool stable = true;
        foreach (var (title, elements, groups) in scenes)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = pipeline.Run(new VerificationContext(elements, groups, [], new StructuralLoadSettings(), SolarLocations.Resolve("moscow"), includeInsolation: true));
            sw.Stop();
            bool ok = r.Findings.All(f => !f.Code.EndsWith("-FAIL", StringComparison.Ordinal)) && sw.Elapsed.TotalMilliseconds < 15000;
            stable &= ok;
            report.AppendLine($"    «{title}»: {r.Summary}; {sw.Elapsed.TotalMilliseconds:0} мс" +
                              (r.Findings.Count > 0 ? " — " + string.Join(", ", r.Findings.GroupBy(f => f.Code).Select(g => $"{g.Key} ×{g.Count()}")) : string.Empty));
        }
        Check(report, ref failures, stable, $"конвейер на {scenes.Count} сценах (пресеты и каркас, с инсоляцией): без сбоев плагинов");

        // Сейсмика: 10 этажей без ядра при 8 баллах и несимметричная диафрагма — ядро и эксцентриситет.
        var tall = CreateTallFrame();
        var seismic = new StructuralLoadSettings { Seismicity = 8 };
        var tallReport = pipeline.Run(new VerificationContext(tall, [new BuildingGroup("Башня-тест")], [], seismic));
        var core = tallReport.Findings.FirstOrDefault(f => f.Code == "SEIS-CORE");
        var ecc = tallReport.Findings.FirstOrDefault(f => f.Code.StartsWith("SEIS-ECC", StringComparison.Ordinal));
        var tallScene = DesignFixEngine.Sandbox(tall, [new BuildingGroup("Башня-тест")], []);
        if (core is not null) DesignFixEngine.Apply(tallScene, CouncilPlanner.Pick(core, FixAuthor.Structural)!.Actions);
        var fixedReport = pipeline.Run(VerificationContext.FromScene(tallScene, seismic));
        Check(report, ref failures, core is { IsCritical: true } && ecc is not null && fixedReport.Findings.All(f => f.Code != "SEIS-CORE"),
            $"SeismicAndStabilityVerifier: {core?.Title ?? "ядро не проверено"}; {ecc?.Title ?? "эксцентриситет не найден"}; после «ядро жёсткости» замечание снято");
        return failures;
    }

    /// <summary>10-этажный рамный каркас 3 × 3 пролёта по 6 м без ядра, с одной ж/б стеной у грани (асимметрия жёсткости).</summary>
    private static List<ArchitecturalElement> CreateTallFrame()
    {
        var list = new List<ArchitecturalElement>();
        int n = 0;
        for (int level = 0; level < 10; level++)
        {
            double b = level * 3.3;
            for (int i = 0; i <= 3; i++)
                for (int j = 0; j <= 3; j++)
                {
                    var c = new ArchitecturalElement { Id = $"el_tall{++n:D4}", Type = ElementTypes.Column, Name = $"Колонна {level + 1}-{i}{j}", BuildingGroupId = "Башня-тест", LevelIndex = level };
                    c.SetSize(0.6, 3.1, 0.6);
                    c.SetPosition(i * 6, b + 1.55, j * 6);
                    list.Add(c);
                }
            var s = new ArchitecturalElement { Id = $"el_tall{++n:D4}", Type = ElementTypes.Slab, Name = $"Плита {level + 1}", BuildingGroupId = "Башня-тест", LevelIndex = level };
            s.SetSize(18.8, 0.2, 18.8);
            s.SetPosition(9, b + 3.2, 9);
            list.Add(s);
            var w = new ArchitecturalElement { Id = $"el_tall{++n:D4}", Type = ElementTypes.Wall, Name = $"Диафрагма {level + 1}", BuildingGroupId = "Башня-тест", LevelIndex = level };
            w.SetSize(6, 3.1, 0.3);
            w.SetPosition(3, b + 1.55, 0);
            list.Add(w);
        }
        return list;
    }

    // ═════════════════════════════ 2. Согласование User-in-the-Loop ═════════════════════════════

    private static async Task<int> CheckCouncilConsultationAsync(StringBuilder report)
    {
        int failures = 0;
        var scene = CouncilSceneState();
        var council = new CouncilSettings { ConsultUser = true };
        var models = TestModels();
        var gateway = new ScriptedGateway();
        var swarm = new AgentSwarmOrchestrator(scene, App.Services.GetRequiredService<AIOrchestrator>(), gateway, () => council,
            () => new StructuralLoadSettings(), () => models);
        var timeline = new List<AgentDebateEntry>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = await swarm.RunAuditAsync(models[0], consult: true, timeline.Add, CancellationToken.None);
        sw.Stop();
        var c = run.Consultation;
        report.AppendLine($"  Аудит Совета: {sw.Elapsed.TotalMilliseconds:0} мс, реплик {timeline.Count}, вариантов {c?.Variants.Count ?? 0}");
        foreach (var e in timeline) report.AppendLine($"    {e.RoleTitle} [{e.ModelLabel}] {e.ElapsedText}: {e.Text}");
        foreach (var v in c?.Variants ?? []) report.AppendLine($"    {v.Title}\n      {v.Description}\n      прогноз: {v.MetricsText}, критичных после {v.CriticalAfter}, новых элементов {v.ElementsAdded}");
        Check(report, ref failures, c is not null && c.Variants.Count >= 3 && c.Variants.Any(v => v.Author == FixAuthor.Structural) && c.Variants.Any(v => v.Author == FixAuthor.Architect) &&
                                    c.Variants.All(v => v.KAfter <= 1.0 && v.ClashesAfter == 0) && scene.UndoDepth == 0,
            $"карточка согласования: {c?.Issues.Count ?? 0} спорных узлов, варианты Конструктора, Архитектора и КБ с прогнозом K_исп ≤ 1.0 и 0 коллизий; сцена не изменена до выбора (Undo {scene.UndoDepth})");

        if (c is null) return failures + 1;
        long original = StructuralTopology.Fingerprint(scene.Elements);
        double kOriginal = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), new StructuralLoadSettings()).MaxUtilization;
        foreach (var variant in c.Variants.Where(v => v.Author is FixAuthor.Structural or FixAuthor.Architect))
        {
            int depth = scene.UndoDepth;
            var applied = await swarm.ApplyDecisionAsync(c, new CouncilDecision(variant.Id, CouncilModifiers.None), CancellationToken.None);
            int clashes = ClashDetectionService.Detect(scene.Elements.ToList()).Count;
            double k = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), new StructuralLoadSettings()).MaxUtilization;
            Check(report, ref failures, applied.Applied && k <= 1.0 && applied.KAfter <= 1.0 && clashes == 0 && applied.ClashesAfter == 0 && scene.UndoDepth == depth + 1 && applied.UndoSteps == 1,
                $"выбор «{variant.Title}»: K_исп {applied.KBefore:0.00} → {k:0.00}, коллизий {applied.ClashesBefore} → {clashes}, действий {applied.ActionsApplied}, новых элементов {applied.ElementsAdded}, " +
                $"шагов Undo {scene.UndoDepth - depth}, {applied.ElapsedMilliseconds:0} мс");
            foreach (var line in applied.Log.Take(10)) report.AppendLine("      " + line);
            scene.Undo();
            double kUndo = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), new StructuralLoadSettings()).MaxUtilization;
            Check(report, ref failures, StructuralTopology.Fingerprint(scene.Elements) == original && Math.Abs(kUndo - kOriginal) < 1e-9 && scene.UndoDepth == depth,
                $"Undo откатил вариант {variant.Number} целиком: элементов {scene.Elements.Count}, K_исп снова {kUndo:0.00}");
        }

        // Ответ ГИПа текстом: вариант 1, но балки — двутавровые стальные.
        const string reply = "Сделайте вариант 1, но балки сделайте двутавровыми стальными";
        var decision = await swarm.InterpretReplyAsync(c, reply, models[0], CancellationToken.None);
        bool parsed = decision is { VariantId: "v1", Modifiers.BeamProfile: ProfileTypes.IBeam, Modifiers.BeamMaterial: "steel" };
        var withBeams = parsed ? await swarm.ApplyDecisionAsync(c, decision!, CancellationToken.None) : null;
        var newBeams = scene.Elements.Where(e => e.Type == ElementTypes.Beam && e.Name.StartsWith("Балка КБ", StringComparison.Ordinal)).ToList();
        double kText = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), new StructuralLoadSettings()).MaxUtilization;
        bool steelBeams = newBeams.Count > 0 && newBeams.All(b => b.EffectiveProfile == ProfileTypes.IBeam && b.Material == "steel");
        Check(report, ref failures, parsed && withBeams is { Applied: true, UndoSteps: 1 } && kText <= 1.0 && (steelBeams || c.Variants[0].Choices.All(ch => ch.Option.Actions.All(a => a.Kind != FixActionKind.AddBeamsUnderSlab))),
            $"ответ «{reply}» → {decision?.VariantId} ({decision?.Modifiers.Describe()}); новых балок {newBeams.Count} (двутавр, сталь: {(steelBeams ? "да" : "нет")}), K_исп → {kText:0.00}");
        scene.Undo();
        Check(report, ref failures, StructuralTopology.Fingerprint(scene.Elements) == original, "Undo после уточнённого варианта вернул исходную модель");

        // «Оставить как есть» не меняет сцену и не пишет Undo.
        int keepDepth = scene.UndoDepth;
        var keep = await swarm.ApplyDecisionAsync(c, new CouncilDecision(CouncilDecision.Keep, CouncilModifiers.None), CancellationToken.None);
        Check(report, ref failures, !keep.Applied && scene.UndoDepth == keepDepth && StructuralTopology.Fingerprint(scene.Elements) == original,
            "«Оставить как есть»: модель и стек Undo не изменены");

        // Автопилот: аудит исправляет всё сам одним шагом Undo.
        council.ConsultUser = false;
        var auto = await swarm.RunAuditAsync(models[0], consult: false, _ => { }, CancellationToken.None);
        double kAuto = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), new StructuralLoadSettings()).MaxUtilization;
        Check(report, ref failures, auto.AutoApplied is { Applied: true, UndoSteps: 1, ClashesAfter: 0 } && kAuto <= 1.0 && auto.Consultation is null,
            $"полный автопилот: {auto.AutoApplied?.Summary}");
        scene.Undo();
        return failures;
    }

    // ═════════════════════════════ 3. Маршрутизация Multi-LLM ═════════════════════════════

    private static List<AiModelOption> TestModels() =>
    [
        AiModelCatalog.Cloud(AiProviderKind.Gemini, "gemini-2.5-pro"),
        AiModelCatalog.Cloud(AiProviderKind.Anthropic, "claude-sonnet-5-5"),
        AiModelCatalog.Cloud(AiProviderKind.OpenAI, "gpt-4o"),
        AiModelCatalog.LocalQwen3B,
        AiModelCatalog.LocalQwen7B
    ];

    private static async Task<int> CheckCouncilRoutingAsync(StringBuilder report)
    {
        int failures = 0;
        var models = TestModels();
        var auto = AgentModelRouter.Resolve(new CouncilSettings(), models, models[0]);
        string Show(IEnumerable<AgentAssignment> list) => string.Join("; ", list.Select(a => $"{AgentRoles.Info(a.Role).ShortRu} → {a.Model.DisplayName} (t = {a.Temperature:0.##})"));
        report.AppendLine("  Автоподбор: " + Show(auto));
        Check(report, ref failures, auto[0].Model.Provider == AiProviderKind.Gemini && auto[1].Model.Provider == AiProviderKind.Anthropic &&
                                    auto[2].Model.Provider == AiProviderKind.OpenAI && auto.Select(a => a.Model.Provider).Distinct().Count() >= 3 && !AgentModelRouter.IsSingleProvider(auto),
            "автоподбор лучших моделей: Архитектор — Gemini (зрение), Конструктор — Claude (сложный JSON), Нормоконтроль — ChatGPT; не меньше трёх разных провайдеров");

        var local = AgentModelRouter.Resolve(new CouncilSettings(), [AiModelCatalog.LocalQwen3B, AiModelCatalog.LocalQwen7B], AiModelCatalog.LocalQwen3B);
        Check(report, ref failures, local.All(a => a.Model.IsLocal) && AgentModelRouter.IsSingleProvider(local) && local.Select(a => a.Temperature).Distinct().Count() >= 3,
            "только локальный Qwen: все 4 роли — последовательно через локальный провайдер, температуры ролей " + string.Join(" / ", local.Select(a => a.Temperature.ToString("0.##", Inv))));

        var single = AgentModelRouter.Resolve(new CouncilSettings(), [models[1]], models[1]);
        var bound = new CouncilSettings { Bindings = { ["architect"] = "openai:gpt-4o", ["structural"] = "openai:o3-pro", ["norms"] = AiModelCatalog.LocalQwen7BId } };
        var explicitRoutes = AgentModelRouter.Resolve(bound, models, models[0]);
        Check(report, ref failures, single.All(a => a.Model.Id == models[1].Id) && explicitRoutes[0].Model.Id == "openai:gpt-4o" && !explicitRoutes[0].IsFallback &&
                                    explicitRoutes[1].IsFallback && explicitRoutes[1].Model.Provider == AiProviderKind.Anthropic && explicitRoutes[2].Model.Id == AiModelCatalog.LocalQwen7BId,
            $"один API-ключ → все роли через {models[1].DisplayName}; явные назначения: {Show(explicitRoutes)} (недоступная o3-pro → откат на автоподбор)");

        // Полный прогон Совета через сценарный шлюз: каждая роль вызывает свою модель со своей температурой.
        var scene = new SceneStateService();
        var council = new CouncilSettings { ConsultUser = false };
        var gateway = new ScriptedGateway();
        var ai = App.Services.GetRequiredService<AIOrchestrator>();
        var swarm = new AgentSwarmOrchestrator(scene, ai, gateway, () => council, () => new StructuralLoadSettings(), () => models);
        var context = new AiRequestContext { Task = "Двухэтажный офис 16.5 × 12 м с большим пролётом 10.5 м и лестницей", Model = models[0], Mode = SolverMode.NewScene };
        var timeline = new List<AgentDebateEntry>();
        var run = await swarm.RunDesignAsync(context, consult: false, timeline.Add, CancellationToken.None);
        foreach (var e in timeline) report.AppendLine($"    {e.RoleTitle} [{e.ModelLabel}] {e.ElapsedText}: {e.Text}");
        var calls = gateway.Calls;
        report.AppendLine("  Вызовы моделей: " + string.Join("; ", calls.Select(cl => $"{AgentRoles.Info(cl.Role).ShortRu} → {cl.ModelId} t={cl.Temperature:0.##}")));
        double k = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), new StructuralLoadSettings()).MaxUtilization;
        bool rolesOk = calls.Any(cl => cl.Role == AiAgentRole.ChiefArchitect && cl.ModelId.StartsWith("gemini", StringComparison.Ordinal)) &&
                       calls.Any(cl => cl.Role == AiAgentRole.StructuralEngineer && cl.ModelId.StartsWith("anthropic", StringComparison.Ordinal) && cl.SawArchitectJson) &&
                       calls.Any(cl => cl.Role == AiAgentRole.NormsAndSafetyVerifier) && calls.Any(cl => cl.Role == AiAgentRole.MasterSynthesizer);
        Check(report, ref failures, rolesOk && run.AutoApplied is { Applied: true } && k <= 1.0 && ClashDetectionService.Detect(scene.Elements.ToList()).Count == 0 && scene.Elements.Count > 20,
            $"Совет (Multi-LLM, автопилот): Архитектор → Конструктор (получил JSON ГИПа) → Нормоконтроль → Координатор; элементов {scene.Elements.Count}, " +
            $"K_исп → {k:0.00}, {run.AutoApplied?.Summary}");

        // Один локальный провайдер и сбой Архитектора: роли идут последовательно через Qwen, Конструктор строит здание сам.
        var scene2 = new SceneStateService();
        var gateway2 = new ScriptedGateway { FailArchitect = true };
        var localOnly = new List<AiModelOption> { AiModelCatalog.LocalQwen7B };
        var swarm2 = new AgentSwarmOrchestrator(scene2, ai, gateway2, () => new CouncilSettings { ConsultUser = true }, () => new StructuralLoadSettings(), () => localOnly);
        var run2 = await swarm2.RunDesignAsync(new AiRequestContext { Task = context.Task, Model = AiModelCatalog.LocalQwen7B, Mode = SolverMode.NewScene },
            consult: true, _ => { }, CancellationToken.None);
        bool sequential = gateway2.Calls.All(cl => cl.ModelId == AiModelCatalog.LocalQwen7BId) && gateway2.Calls.Select(cl => cl.Temperature).Distinct().Count() >= 2;
        Check(report, ref failures, sequential && run2.Consultation is { Variants.Count: >= 1 } && scene2.Elements.Count > 20 && scene2.UndoDepth == 0,
            $"откат на одиночный локальный провайдер: {gateway2.Calls.Count} вызова(ов) Qwen 7B со сменой ролей и температур; Архитектор недоступен — Конструктор построил здание; " +
            $"черновик выпущен, карточка с {run2.Consultation?.Variants.Count ?? 0} вариантами ждёт ГИПа");
        return failures;
    }

    /// <summary>Сценарный шлюз автотеста (общий класс <see cref="CouncilScenarioGateway"/>).</summary>
    private sealed class ScriptedGateway : CouncilScenarioGateway;
}

/// <summary>
/// Сценарный шлюз моделей Совета КБ (только Debug): ответы ролей без сети и журнал вызовов (роль, модель, температура,
/// доработка). Рассуждающие модели (deepseek-r1, gpt-oss…) отвечают как настоящие — с блоком &lt;think&gt;, который шлюз
/// отделяет тем же <see cref="ReasoningText"/>, что и реальный провайдер; при заданной задержке рассуждения и ответ
/// «печатаются» потоком через Progress (живая лента «Коллоквиум ИИ» в Debug-съёмке). С <see cref="FlawedFirstDraft"/>
/// первый черновик Конструктора содержит дефекты (пролёт 10.5 м без промежуточных опор, марш без проёма), а запрос
/// доработки со списком дефектов C#-движка получает исправленную модель — проверка цикла самоисправления.
/// </summary>
public class CouncilScenarioGateway : IAgentModelGateway
{
    public sealed record Call(AiAgentRole Role, string ModelId, double Temperature, bool SawArchitectJson, bool IsRework, string UserPrompt);

    public List<Call> Calls { get; } = [];
    public bool FailArchitect { get; init; }

    /// <summary>Первый черновик Конструктора с дефектами, доработка — исправленная модель.</summary>
    public bool FlawedFirstDraft { get; init; }

    /// <summary>Задержка «печати» потока (мс на фрагмент) — для Debug-съёмки; 0 — мгновенно.</summary>
    public int StreamDelayMs { get; init; }

    public async Task<AiResponse> InvokeAsync(AgentInvocation invocation, CancellationToken cancellationToken)
    {
        var a = invocation.Assignment;
        bool rework = invocation.UserPrompt.Contains("ДОРАБОТКА ", StringComparison.Ordinal);
        lock (Calls)
            Calls.Add(new Call(a.Role, a.Model.Id, a.Temperature, invocation.UserPrompt.Contains("el_arch_marker", StringComparison.Ordinal), rework, invocation.UserPrompt));
        if (a.Role == AiAgentRole.ChiefArchitect && FailArchitect)
            throw new AiProviderException("AIA-LOCAL-CONTEXT", "Тест: модель Архитектора недоступна");
        string answer = a.Role switch
        {
            AiAgentRole.ChiefArchitect => ArchitectJson,
            AiAgentRole.StructuralEngineer => FlawedFirstDraft && rework ? FixedStructuralJson : StructuralJson,
            AiAgentRole.NormsAndSafetyVerifier => NormsReply,
            _ => "{\"message\": \"Вариант КБ закрывает все замечания при наименьшем вмешательстве в архитектуру; если важен свободный план — вариант Конструктора с балочной клеткой.\", \"variant\": \"v3\"}"
        };
        string raw = a.Model.IsReasoning ? $"<think>\n{ThinkingFor(a.Role, rework)}\n</think>\n\n{answer}" : answer;

        // Тот же разбор, что у реального шлюза: рассуждения отдельно, JSON отдельно.
        var (text, reasoning) = ReasoningText.Separate(raw);
        if (invocation.Progress is { } progress)
        {
            int step = StreamDelayMs > 0 ? 48 : Math.Max(64, raw.Length / 4);
            for (int i = step; i < raw.Length; i += step)
            {
                var (partAnswer, partReasoning) = ReasoningText.Separate(raw[..i]);
                progress.Report(new AiStreamProgress(partReasoning, partAnswer));
                if (StreamDelayMs > 0) await Task.Delay(StreamDelayMs, cancellationToken);
            }
            progress.Report(new AiStreamProgress(reasoning, text));
        }
        else if (StreamDelayMs > 0) await Task.Delay(StreamDelayMs * 10, cancellationToken);
        return new AiResponse(text, a.Model.DisplayName, TimeSpan.FromMilliseconds(5), Reasoning: reasoning.Length > 0 ? reasoning : null);
    }

    /// <summary>«Внутренние рассуждения» рассуждающей модели по роли (сценарий deepseek-r1).</summary>
    private static string ThinkingFor(AiAgentRole role, bool rework) => role switch
    {
        AiAgentRole.NormsAndSafetyVerifier =>
            "Итак, мне передан отчёт C#-конвейера. Самое тяжёлое — KE-SLAB: плита 200 мм на пролёте 10.5 м, K_исп больше 1.2. " +
            "Проверю пропорцию: L/30 = 10500/30 = 350 мм, значит 200 мм заведомо мало, прогиб f/f_ult выше единицы. " +
            "Варианты: утолщать до ~350 мм — это +30 м³ бетона и лишний вес на колонны; балки по осям колонн h ≥ L/12 ≈ 900 мм; " +
            "или промежуточная колонна в середине пролёта — пролёт станет 5.25 м и 200 мм хватит. " +
            "Колонна 2-3Б стоит на консоли 200 мм без балки — это VLP-CANT, надо продолжить стойку до фундамента. " +
            "Марш Л-1 без проёма в плите — ACCESS, нужен slab_opening примерно 1.4 × 3.2 м. Расставлю приоритеты: сначала прочность, потом доступ.",
        AiAgentRole.StructuralEngineer when rework =>
            "Аудитор просит убрать перегрузку плиты и дать проём над маршем. Добавлю колонны 400×400 по оси x = 11.25 на обоих этажах, " +
            "строго одна над другой, и slab_opening над лестницей. Остальные элементы оставлю с теми же id.",
        AiAgentRole.StructuralEngineer =>
            "Сетка 0 / 6 / 16.5 × 0 / 6 / 12. Колонны 400×400 по узлам, плиты 200 мм, фундаментная плита 500 мм.",
        AiAgentRole.MasterSynthesizer =>
            "Сравниваю прогнозы: у всех вариантов K_исп ≤ 0.95 и 0 коллизий. Вариант КБ меняет меньше всего в архитектуре, " +
            "балочная клетка Конструктора даёт свободный план, но опускает потолок. Выберу v3.",
        _ => "Сначала определю сетку осей и отметки уровней, затем контур этажа и лестницу у оси 1."
    };

    private const string NormsReply = """
{"message": "Главный риск — плита 200 мм на пролёте 10.5 м: K_исп выше 1.2 и прогиб сверх предельного. Колонну 2-3Б на консоли нужно опереть до фундамента, над маршем Л-1 вырезать проём 1.4 × 3.2 м.",
 "priorities": ["KE-SLAB", "VLP-CANT", "ACCESS-STAIR-OPENING"],
 "recipes": [{"code": "KE-SLAB", "fix": "добавить колонны 400×400 по оси x = 11.25 м (z = 0, 6, 12) на этажах 1 и 2 — пролёт 5.25 м"},
             {"code": "ACCESS-STAIR-OPENING", "fix": "slab_opening 1.4 × 3.2 м в плите этажа 1 над маршем, центр (1.6; 3.0)"}]}
""";

    private const string ArchitectJson = """
{"mode":"new","message":"Офис 16.5 × 12 м: сетка 1-3 / А-В, два этажа по 3.0 м, плоская кровля, лестница у оси 1.","elements":[
{"id":"el_arch_marker","type":"wall","name":"Стена по оси В","start":[0,12],"end":[16.5,12],"height":2.8,"thickness":0.3,"material":"brick"},
{"id":"st1","type":"stairs","name":"Лестница Л-1","level":0,"position":[1.6,3.0],"size":[1.2,3.0,3.0]},
{"id":"rf","type":"roof","roofType":"flat","level":1,"position":[8.25,6],"size":[17.0,0.2,12.6]}]}
""";

        private const string StructuralJson = """
{"mode":"new","message":"Рамный ж/б каркас: колонны 400×400 по осям, плиты 200 мм, фундаментная плита 500 мм.","elements":[
{"id":"f","type":"foundation","name":"Фундаментная плита","position":[8.25,6],"size":[18.0,0.5,13.2],"layers":"foundation"},
{"id":"c11","type":"column","level":0,"position":[0,0],"size":[0.4,2.8,0.4]},{"id":"c12","type":"column","level":0,"position":[0,6],"size":[0.4,2.8,0.4]},
{"id":"c13","type":"column","level":0,"position":[0,12],"size":[0.4,2.8,0.4]},{"id":"c21","type":"column","level":0,"position":[6,0],"size":[0.4,2.8,0.4]},
{"id":"c22","type":"column","level":0,"position":[6,6],"size":[0.4,2.8,0.4]},{"id":"c23","type":"column","level":0,"position":[6,12],"size":[0.4,2.8,0.4]},
{"id":"c31","type":"column","level":0,"position":[16.5,0],"size":[0.4,2.8,0.4]},{"id":"c32","type":"column","level":0,"position":[16.5,6],"size":[0.4,2.8,0.4]},
{"id":"c33","type":"column","level":0,"position":[16.5,12],"size":[0.4,2.8,0.4]},
{"id":"s1","type":"slab","name":"Плита перекрытия 1","level":0,"position":[8.25,6],"size":[17.0,0.2,12.6]},
{"id":"d11","type":"column","level":1,"position":[0,0],"size":[0.4,2.8,0.4]},{"id":"d12","type":"column","level":1,"position":[0,6],"size":[0.4,2.8,0.4]},
{"id":"d13","type":"column","level":1,"position":[0,12],"size":[0.4,2.8,0.4]},{"id":"d21","type":"column","level":1,"position":[6,0],"size":[0.4,2.8,0.4]},
{"id":"d22","type":"column","level":1,"position":[6,6],"size":[0.4,2.8,0.4]},{"id":"d23","type":"column","level":1,"position":[6,12],"size":[0.4,2.8,0.4]},
{"id":"d31","type":"column","level":1,"position":[16.5,0],"size":[0.4,2.8,0.4]},{"id":"d32","type":"column","level":1,"position":[16.5,6],"size":[0.4,2.8,0.4]},
{"id":"d33","type":"column","level":1,"position":[16.5,12],"size":[0.4,2.8,0.4]},
{"id":"el_arch_marker","type":"wall","name":"Стена по оси В","start":[0,12],"end":[16.5,12],"height":2.8,"thickness":0.3,"material":"brick"},
{"id":"st1","type":"stairs","name":"Лестница Л-1","level":0,"position":[1.6,3.0],"size":[1.2,3.0,3.0]},
{"id":"rf","type":"roof","roofType":"flat","name":"Плоская кровля","level":1,"position":[8.25,6],"size":[17.0,0.2,12.6],"layers":"flat_roof"}]}
""";

    /// <summary>Доработка по замечаниям: промежуточные колонны по оси x = 11.25 на обоих этажах (пролёт 5.25 м) и проём над маршем.</summary>
    private const string FixedStructuralJson = """
{"mode":"new","message":"Исправлено по замечаниям Аудитора: колонны 400×400 по оси x = 11.25 м на обоих этажах (пролёт 5.25 м), проём 1.4 × 3.2 м над маршем Л-1.","elements":[
{"id":"f","type":"foundation","name":"Фундаментная плита","position":[8.25,6],"size":[18.0,0.5,13.2],"layers":"foundation"},
{"id":"c11","type":"column","level":0,"position":[0,0],"size":[0.4,2.8,0.4]},{"id":"c12","type":"column","level":0,"position":[0,6],"size":[0.4,2.8,0.4]},
{"id":"c13","type":"column","level":0,"position":[0,12],"size":[0.4,2.8,0.4]},{"id":"c21","type":"column","level":0,"position":[6,0],"size":[0.4,2.8,0.4]},
{"id":"c22","type":"column","level":0,"position":[6,6],"size":[0.4,2.8,0.4]},{"id":"c23","type":"column","level":0,"position":[6,12],"size":[0.4,2.8,0.4]},
{"id":"m21","type":"column","level":0,"position":[11.25,0],"size":[0.4,2.8,0.4]},{"id":"m22","type":"column","level":0,"position":[11.25,6],"size":[0.4,2.8,0.4]},
{"id":"m23","type":"column","level":0,"position":[11.25,12],"size":[0.4,2.8,0.4]},
{"id":"c31","type":"column","level":0,"position":[16.5,0],"size":[0.4,2.8,0.4]},{"id":"c32","type":"column","level":0,"position":[16.5,6],"size":[0.4,2.8,0.4]},
{"id":"c33","type":"column","level":0,"position":[16.5,12],"size":[0.4,2.8,0.4]},
{"id":"s1","type":"slab","name":"Плита перекрытия 1","level":0,"position":[8.25,6],"size":[17.0,0.2,12.6]},
{"id":"so1","type":"slab_opening","name":"Проём над маршем Л-1","level":0,"position":[1.6,3.0],"size":[1.4,0,3.2]},
{"id":"d11","type":"column","level":1,"position":[0,0],"size":[0.4,2.8,0.4]},{"id":"d12","type":"column","level":1,"position":[0,6],"size":[0.4,2.8,0.4]},
{"id":"d13","type":"column","level":1,"position":[0,12],"size":[0.4,2.8,0.4]},{"id":"d21","type":"column","level":1,"position":[6,0],"size":[0.4,2.8,0.4]},
{"id":"d22","type":"column","level":1,"position":[6,6],"size":[0.4,2.8,0.4]},{"id":"d23","type":"column","level":1,"position":[6,12],"size":[0.4,2.8,0.4]},
{"id":"n21","type":"column","level":1,"position":[11.25,0],"size":[0.4,2.8,0.4]},{"id":"n22","type":"column","level":1,"position":[11.25,6],"size":[0.4,2.8,0.4]},
{"id":"n23","type":"column","level":1,"position":[11.25,12],"size":[0.4,2.8,0.4]},
{"id":"d31","type":"column","level":1,"position":[16.5,0],"size":[0.4,2.8,0.4]},{"id":"d32","type":"column","level":1,"position":[16.5,6],"size":[0.4,2.8,0.4]},
{"id":"d33","type":"column","level":1,"position":[16.5,12],"size":[0.4,2.8,0.4]},
{"id":"el_arch_marker","type":"wall","name":"Стена по оси В","start":[0,12],"end":[16.5,12],"height":2.8,"thickness":0.3,"material":"brick"},
{"id":"st1","type":"stairs","name":"Лестница Л-1","level":0,"position":[1.6,3.0],"size":[1.2,3.0,3.0]},
{"id":"rf","type":"roof","roofType":"flat","name":"Плоская кровля","level":1,"position":[8.25,6],"size":[17.0,0.2,12.6],"layers":"flat_roof"}]}
""";
}
#endif
