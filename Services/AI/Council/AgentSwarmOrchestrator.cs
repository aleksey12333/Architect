using System.Diagnostics;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.Verification;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Мультиагентное конструкторское бюро (Multi-Agent BIM Council). Главный архитектор формирует объёмно-планировочное
/// решение, Главный конструктор — несущий каркас по нему, Нормоконтроль прогоняет черновик через конвейер C#-проверщиков
/// (на песочнице, живая сцена не трогается) и даёт критику, Координатор сводит позиции и выпускает модель. Каждая роль
/// может выполняться своей моделью (Claude, Gemini, ChatGPT, OpenRouter, Qwen, веб-аккаунт). Спорные узлы выносятся на
/// согласование с пользователем (карточка с вариантами), в режиме автопилота исправляются сразу. Применение решения —
/// ровно один шаг Undo; прогноз K_исп и коллизий получен моделированием того же варианта на копии сцены.
/// </summary>
public sealed class AgentSwarmOrchestrator(
    SceneStateService scene,
    AIOrchestrator ai,
    IAgentModelGateway gateway,
    Func<CouncilSettings> council,
    Func<StructuralLoadSettings> loads,
    Func<IReadOnlyList<AiModelOption>> availableModels,
    Func<SolarLocation?>? location = null)
{
    /// <summary>Таймаут реплик Нормоконтроля и Координатора (комментарии необязательны — C#-решение готово без них).</summary>
    private static readonly TimeSpan CommentTimeout = TimeSpan.FromSeconds(90);

    public BimVerificationPipeline Pipeline { get; } = BimVerificationPipeline.CreateDefault();

    public SceneStateService Scene => scene;

    public IReadOnlyList<AgentAssignment> ResolveAssignments(AiModelOption dockModel) =>
        AgentModelRouter.Resolve(council(), availableModels(), dockModel, OllamaProcessManager.TotalMemoryGb);

    public static string ModelLabel(AiModelOption m) =>
        m.IsLocal ? $"{m.ShortName} · локально" : m.IsOllama ? $"{m.ShortName} · локальный интеллект" : m.IsWeb ? $"Веб · {AiModelCatalog.ProviderTitle(m.Provider)}" : $"{AiModelCatalog.ProviderTitle(m.Provider)} · {m.ShortName}";

    private static AgentDebateEntry Entry(AgentAssignment a, string? suffix = null) => new(a.Role, ModelLabel(a.Model) + (suffix ?? string.Empty));

    /// <summary>
    /// Живое обновление реплики при потоковой генерации (Ollama): Progress&lt;T&gt; захватывает контекст UI-потока, поэтому
    /// рассуждения и фрагменты ответа попадают в ленту «Коллоквиум ИИ» без межпоточных обращений к привязкам.
    /// </summary>
    private static IProgress<AiStreamProgress> Live(AgentDebateEntry entry, bool sceneJson) =>
        new Progress<AiStreamProgress>(p => entry.Stream(p, sceneJson));

    private StructuralLoadSettings Loads => loads().Clone();

    /// <summary>Итог проверки Нормоконтролем: отчёт конвейера, реплика модели-Аудитора и её рецепты исправления.</summary>
    private sealed record AuditOutcome(VerificationReport Report, string? Message, IReadOnlyList<string> Recipes);

    /// <summary>Реплика модели: статус, текст (поле message), рассуждения и рецепты Аудитора.</summary>
    private sealed record AgentReply(AgentEntryStatus Status, string Text, string? Reasoning, IReadOnlyList<string> Recipes);

    // ═════════════════════════════ Проектирование Советом ═════════════════════════════

    /// <summary>
    /// Архитектор → Конструктор → цикл самоисправления «ИИ ↔ C#-движок» (солвер на песочнице, КЭ-расчёт и коллизии, при
    /// дефектах Аудитор формулирует рецепт и Конструктор дорабатывает модель — до <see cref="CouncilSettings.MaxReworkIterations"/>
    /// раундов, в работу идёт лучший черновик) → варианты → выпуск: при спорных узлах в режиме согласования черновик
    /// выпускается в сцену (шаг Undo) и возвращается карточка; в автопилоте черновик и исправления — одним шагом Undo.
    /// </summary>
    public async Task<CouncilRunResult> RunDesignAsync(AiRequestContext ctx, bool consult, Action<AgentDebateEntry> post, CancellationToken ct)
    {
        var run = new CouncilRunResult();
        var assignments = ResolveAssignments(ctx.Model);
        run.Assignments.AddRange(assignments);
        AgentAssignment Of(AiAgentRole r) => assignments.First(a => a.Role == r);
        bool single = AgentModelRouter.IsSingleProvider(assignments);

        // ── 1. Главный архитектор.
        var arch = Of(AiAgentRole.ChiefArchitect);
        var archEntry = Entry(arch);
        post(archEntry);
        string? archJson = null, archMessage = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var actx = ctx.WithModel(arch.Model);
            var (system, user) = AgentPrompts.Architect(ai, actx);
            var response = await gateway.InvokeAsync(new AgentInvocation(arch, system, user, ctx.Images, actx, Progress: Live(archEntry, sceneJson: true)), ct);
            if (AiJsonExtractor.TryExtract(response.Text, out var doc))
            {
                archJson = doc.RawJson;
                archMessage = doc.Message?.Trim();
                int axes = doc.GridAxes.Count, levels = doc.Elements.Select(e => e.Level ?? 0).DefaultIfEmpty(0).Max() + 1;
                archEntry.Found = 0;
                archEntry.Complete(AgentEntryStatus.Done,
                    $"{(string.IsNullOrWhiteSpace(archMessage) ? "Объёмно-планировочное решение готово." : archMessage)} · элементов {doc.Elements.Count}, этажей {levels}" +
                    (axes > 0 ? $", осей {axes}" : string.Empty) + (single ? " · единый провайдер, роль ГИПа" : string.Empty), sw.Elapsed, response.Reasoning);
            }
            else archEntry.Complete(AgentEntryStatus.Failed, "В ответе нет JSON сцены — Конструктор спроектирует здание целиком.", sw.Elapsed, response.Reasoning);
        }
        catch (AiProviderException ex)
        {
            archEntry.Complete(AgentEntryStatus.Failed, $"{ex.Code}: {ex.Message} — Конструктор спроектирует здание целиком.", sw.Elapsed);
        }
        ct.ThrowIfCancellationRequested();

        // ── 2. Главный конструктор КЖ/КМ.
        var str = Of(AiAgentRole.StructuralEngineer);
        var strEntry = Entry(str);
        post(strEntry);
        string? finalJson = archJson;
        sw.Restart();
        try
        {
            var sctx = ctx.WithModel(str.Model);
            var (system, user) = AgentPrompts.Structural(ai, sctx, archJson, archMessage);
            var images = archJson is null ? ctx.Images : [];
            var response = await gateway.InvokeAsync(new AgentInvocation(str, system, user, images, sctx, Progress: Live(strEntry, sceneJson: true)), ct);
            if (AiJsonExtractor.TryExtract(response.Text, out var doc) && doc.Elements.Count > 0)
            {
                finalJson = doc.RawJson;
                strEntry.Complete(AgentEntryStatus.Done, StructuralSummary(doc, "Каркас запроектирован."), sw.Elapsed, response.Reasoning);
            }
            else strEntry.Complete(AgentEntryStatus.Warning, "Ответ без JSON сцены — в работу идёт модель ГИПа, каркас проверит и дополнит нормоконтроль.", sw.Elapsed, response.Reasoning);
        }
        catch (AiProviderException ex)
        {
            strEntry.Complete(AgentEntryStatus.Warning, $"{ex.Code}: {ex.Message} — в работу идёт модель ГИПа.", sw.Elapsed);
        }
        ct.ThrowIfCancellationRequested();
        if (finalJson is null)
            throw new AiProviderException("AIA-COUNCIL-EMPTY", "Ни Главный архитектор, ни Главный конструктор не вернули JSON сцены. Проверьте модели в «Составе КБ».");

        // ── 3–4. Цикл самоисправления «ИИ ↔ C#-движок»: солвер на песочнице (живая сцена не меняется до выпуска),
        // конвейер проверщиков и рецепт Аудитора; при дефектах (K_исп > 1.0, коллизии, критичные) — доработка Конструктором.
        var norms = Of(AiAgentRole.NormsAndSafetyVerifier);
        int maxRounds = Math.Clamp(council().MaxReworkIterations, 0, 5);
        string json = finalJson;
        var pending = await SolveOnSandboxAsync(json, ctx, ct);
        (SceneStateService Sandbox, SolvedResponse Solved, VerificationReport Report, DesignIteration Iteration)? best = null;
        for (int round = 0; ; round++)
        {
            var (candidate, candidateSolved) = pending;
            var audit = await VerifyAsync(candidate, norms, post, includeInsolation: false, ask: true, ct, round, maxRounds);
            var r = audit.Report;
            var iteration = new DesignIteration(round + 1, r.CriticalCount, r.ClashCount, r.MaxUtilization, candidate.Elements.Count,
                r.Findings.Where(f => f.Severity != VerificationSeverity.Info).Select(f => f.Code).Distinct().ToList(), round > 0);
            run.Iterations.Add(iteration);
            if (best is null || iteration.Rank.CompareTo(best.Value.Iteration.Rank) <= 0) best = (candidate, candidateSolved, r, iteration);
            if (!iteration.HasDefects || round >= maxRounds) break;

            // Доработка: Конструктор получает точный список дефектов C#-движка и рецепт Аудитора.
            var reworkEntry = Entry(str, $" · доработка {round + 1}/{maxRounds}");
            post(reworkEntry);
            reworkEntry.Found = iteration.Critical + iteration.Clashes;
            sw.Restart();
            try
            {
                var sctx = ctx.WithModel(str.Model);
                var (system, user) = AgentPrompts.Rework(ai, sctx, json, r, candidate.Elements.ToList(), audit.Message, audit.Recipes, round + 1, maxRounds);
                var response = await gateway.InvokeAsync(new AgentInvocation(str, system, user, [], sctx, Progress: Live(reworkEntry, sceneJson: true)), ct);
                if (!AiJsonExtractor.TryExtract(response.Text, out var doc) || doc.Elements.Count == 0)
                {
                    reworkEntry.Complete(AgentEntryStatus.Warning, "Ответ без JSON сцены — доработка остановлена, в работу идёт лучший черновик.", sw.Elapsed, response.Reasoning);
                    break;
                }
                pending = await SolveOnSandboxAsync(doc.RawJson, ctx, ct);
                json = doc.RawJson;
                reworkEntry.Complete(AgentEntryStatus.Done, StructuralSummary(doc, "Модель доработана по замечаниям."), sw.Elapsed, response.Reasoning);
            }
            catch (AiProviderException ex)
            {
                reworkEntry.Complete(AgentEntryStatus.Warning, $"{ex.Code}: {ex.Message} — доработка остановлена, в работу идёт лучший черновик.", sw.Elapsed);
                break;
            }
            ct.ThrowIfCancellationRequested();
        }
        var (sandbox, solved, report, released) = best!.Value;
        run.ReleasedIteration = released.Number;
        run.Draft = solved.ToRunResult($"Совет КБ · {ModelLabel(arch.Model)} + {ModelLabel(str.Model)}");
        if (run.Iterations.Count > 1)
            run.Draft.Report.Actions.Insert(0, "Самоисправление «Конструктор ↔ ядро расчётов»: " + string.Join(" → ", run.Iterations.Select(i =>
                $"раунд {i.Number}: критичных {i.Critical}, коллизий {i.Clashes}, K_исп {K(i.MaxUtilization)}")) + $"; выпущен раунд {released.Number}");
        run.Report = report;

        // ── 5. Варианты и позиции Конструктора и Архитектора.
        sw.Restart();
        var variants = await Task.Run(() => CouncilPlanner.BuildVariants(sandbox, report, Loads, Pipeline), ct);
        PostPositions(variants, assignments, post, sw.Elapsed);

        // ── 6. Координатор: выпуск модели.
        var syn = Of(AiAgentRole.MasterSynthesizer);
        var synEntry = Entry(syn);
        post(synEntry);
        sw.Restart();
        string loop = SelfCorrectionText(run);
        if (variants.Count == 0)
        {
            CommitToLive(sandbox, solved);
            synEntry.Complete(AgentEntryStatus.Done, $"{loop}Модель выпущена: замечаний, требующих решения, нет. K_исп max {K(report.MaxUtilization)}, коллизий {report.ClashCount}.", sw.Elapsed);
            run.Message = run.Draft.Message;
            return run;
        }
        var consultation = NewConsultation(report, variants, fromDesign: true);
        if (consult)
        {
            CommitToLive(sandbox, solved);
            consultation.Fingerprint = StructuralTopology.Fingerprint(scene.Elements);
            run.Consultation = consultation;
            synEntry.Complete(AgentEntryStatus.Decision,
                $"{loop}Черновик выпущен в сцену. Остались спорные узлы ({consultation.Issues.Count}) — решение за ГИПом: выберите вариант в карточке «Запрос ГИПа к Вам».", sw.Elapsed);
            run.Message = run.Draft.Message;
            return run;
        }
        var choice = await ChooseVariantAsync(consultation, syn, ct, synEntry);
        var target = consultation.Variants.First(v => v.Id == choice.VariantId);
        var outcome = await Task.Run(() => CouncilPlanner.ApplyVariant(sandbox, target.Choices, Loads, choice.Modifiers, Pipeline), ct);
        int depth = scene.UndoDepth;
        CommitToLive(sandbox, solved);
        var applied = await MeasureAsync(target, outcome, depth, report, ct);
        run.AutoApplied = applied;
        synEntry.Fixed = target.Choices.Count;
        synEntry.Found = consultation.Issues.Count;
        synEntry.Complete(AgentEntryStatus.Done,
            $"{loop}Автопилот: {target.Title}. {choice.Rationale ?? string.Empty} K_исп {K(applied.KBefore)} → {K(applied.KAfter)}, коллизий {applied.ClashesBefore} → {applied.ClashesAfter}; модель выпущена одним шагом Undo.".Replace("  ", " "),
            sw.Elapsed);
        run.Message = run.Draft.Message;
        return run;
    }

    /// <summary>«Самоисправление: 3 раунда, критичных 4 → 0 · » — для реплики Координатора.</summary>
    private static string SelfCorrectionText(CouncilRunResult run)
    {
        if (run.Iterations.Count < 2) return string.Empty;
        var first = run.Iterations[0];
        var released = run.Iterations.First(i => i.Number == run.ReleasedIteration);
        return $"Самоисправление «Конструктор ↔ ядро расчётов»: раундов {run.Iterations.Count}, критичных {first.Critical} → {released.Critical}, коллизий {first.Clashes} → {released.Clashes}, " +
               $"K_исп {K(first.MaxUtilization)} → {K(released.MaxUtilization)}. ";
    }

    /// <summary>
    /// Разбор и прогон JSON модели через C#-солвер на новой песочнице: снимок живой сцены — в UI-потоке, солвер (статический,
    /// без общего состояния) — в пуле потоков, чтобы большой ответ ИИ не замораживал интерфейс.
    /// </summary>
    private async Task<(SceneStateService Sandbox, SolvedResponse Solved)> SolveOnSandboxAsync(string json, AiRequestContext ctx, CancellationToken ct)
    {
        var sandbox = DesignFixEngine.Sandbox(scene);
        return await Task.Run(() =>
        {
            var solved = ai.Solve(json, ctx, sandbox);
            AIOrchestrator.Commit(solved, sandbox);
            return (sandbox, solved);
        }, ct);
    }

    private static string StructuralSummary(AiSceneDocument doc, string fallback)
    {
        int columns = doc.Elements.Count(e => string.Equals(e.Type, ElementTypes.Column, StringComparison.OrdinalIgnoreCase));
        int beams = doc.Elements.Count(e => string.Equals(e.Type, ElementTypes.Beam, StringComparison.OrdinalIgnoreCase));
        int layers = doc.Elements.Count(e => e.Layers is not null || e.LayersPreset is not null);
        return $"{(string.IsNullOrWhiteSpace(doc.Message) ? fallback : doc.Message.Trim())} · элементов {doc.Elements.Count}: колонн {columns}, балок {beams}, пирогов {layers}";
    }

    // ═════════════════════════════ Аудит открытой сцены ═════════════════════════════

    /// <summary>
    /// «Аудит текущей сцены»: все проверщики на снимке живой сцены (в том числе инсоляция), варианты Конструктора и
    /// Архитектора, карточка согласования или (автопилот) немедленное исправление одним шагом Undo.
    /// </summary>
    public async Task<CouncilRunResult> RunAuditAsync(AiModelOption dockModel, bool consult, Action<AgentDebateEntry> post, CancellationToken ct)
    {
        var run = new CouncilRunResult();
        var assignments = ResolveAssignments(dockModel);
        run.Assignments.AddRange(assignments);
        AgentAssignment Of(AiAgentRole r) => assignments.First(a => a.Role == r);

        long fingerprint = StructuralTopology.Fingerprint(scene.Elements);
        var snapshot = DesignFixEngine.Sandbox(scene);
        var report = (await VerifyAsync(snapshot, Of(AiAgentRole.NormsAndSafetyVerifier), post, includeInsolation: true, ask: false, ct)).Report;
        run.Report = report;
        var planning = Stopwatch.StartNew();
        var variants = await Task.Run(() => CouncilPlanner.BuildVariants(snapshot, report, Loads, Pipeline), ct);
        PostPositions(variants, assignments, post, planning.Elapsed);

        var syn = Of(AiAgentRole.MasterSynthesizer);
        var synEntry = Entry(syn, " · расчётная модель");
        post(synEntry);
        var sw = Stopwatch.StartNew();
        if (variants.Count == 0)
        {
            synEntry.Complete(AgentEntryStatus.Done, $"Замечаний, требующих решения, нет. K_исп max {K(report.MaxUtilization)}, коллизий {report.ClashCount}.", sw.Elapsed);
            run.Message = $"Аудит Совета: {report.Summary}.";
            return run;
        }
        var consultation = NewConsultation(report, variants, fromDesign: false);
        consultation.Fingerprint = fingerprint;
        run.Consultation = consultation;
        if (consult)
        {
            synEntry.Complete(AgentEntryStatus.Decision, $"Найдены спорные узлы ({consultation.Issues.Count}) — нужен выбор ГИПа. Вариантов решения: {variants.Count}.", sw.Elapsed);
            run.Message = $"Аудит Совета: {report.Summary}.";
            return run;
        }
        var auto = variants.FirstOrDefault(v => v.Author == FixAuthor.Automatic) ?? variants[^1];
        var applied = await ApplyDecisionAsync(consultation, new CouncilDecision(auto.Id, CouncilModifiers.None), ct);
        run.AutoApplied = applied;
        run.Consultation = null;
        synEntry.Found = consultation.Issues.Count;
        synEntry.Fixed = auto.Choices.Count;
        synEntry.Complete(AgentEntryStatus.Done, $"Автопилот: {applied.Summary}.", sw.Elapsed);
        run.Message = $"Аудит Совета: {report.Summary}.";
        return run;
    }

    /// <summary>
    /// ИИ-комментарии аудита (после карточки, не блокируя её): критика Нормоконтроля и рекомендация Координатора их
    /// моделями. Сбой модели не влияет на решение — оно уже готово на C#-моделировании.
    /// </summary>
    public async Task CommentAsync(CouncilRunResult run, Action<AgentDebateEntry> post, CancellationToken ct)
    {
        if (run.Report is null || !council().AiCommentaryInAudit) return;
        var norms = run.Assignments.First(a => a.Role == AiAgentRole.NormsAndSafetyVerifier);
        // Фоновые комментарии не отправляются в веб-аккаунт пользователя сами по себе — только API и локальные модели.
        if (norms.Model.IsWeb) return;
        var entry = Entry(norms);
        post(entry);
        var sw = Stopwatch.StartNew();
        var reply = await AskReplyAsync(norms, AgentPrompts.Norms(run.Report, norms.Model.IsLocal, scene.Elements.ToList()), ct, entry);
        entry.Found = run.Report.Findings.Count(f => f.Severity != VerificationSeverity.Info);
        entry.Complete(reply.Status, reply.Text + RecipesText(reply.Recipes), sw.Elapsed, reply.Reasoning);
        if (run.Consultation is not { } consultation) return;
        var syn = run.Assignments.First(a => a.Role == AiAgentRole.MasterSynthesizer);
        if (syn.Model.IsWeb) return;
        var synEntry = Entry(syn);
        post(synEntry);
        sw.Restart();
        var choice = await ChooseVariantAsync(consultation, syn, ct, synEntry);
        var v = consultation.Variants.FirstOrDefault(x => x.Id == choice.VariantId);
        synEntry.Complete(choice.Rationale is null ? AgentEntryStatus.Warning : AgentEntryStatus.Decision,
            $"Рекомендую {v?.Title ?? choice.VariantId}. {choice.Rationale ?? "(модель недоступна — рекомендация по прогнозу C#)"}", sw.Elapsed);
    }

    // ═════════════════════════════ Согласование ═════════════════════════════

    /// <summary>
    /// Применяет решение ГИПа: вариант моделируется на копии текущей сцены (если сцена изменилась после планирования —
    /// позиция того же автора пересчитывается) и выпускается в сцену ровно одним шагом Undo с отчётом K_исп до/после.
    /// </summary>
    public async Task<CouncilApplyResult> ApplyDecisionAsync(CouncilConsultation consultation, CouncilDecision decision, CancellationToken ct)
    {
        var result = new CouncilApplyResult();
        if (decision.IsKeep)
        {
            result.VariantTitle = "Оставить как есть — модель не изменена, замечания приняты под ответственность ГИПа";
            return result;
        }
        var variant = consultation.Variants.FirstOrDefault(v => v.Id == decision.VariantId)
                      ?? throw new InvalidOperationException($"Вариант «{decision.VariantId}» не найден в карточке согласования.");
        var sw = Stopwatch.StartNew();
        var choices = variant.Choices.ToList();
        if (StructuralTopology.Fingerprint(scene.Elements) != consultation.Fingerprint)
        {
            var current = VerificationContext.FromScene(scene, consultation.Loads);
            var fresh = await Task.Run(() => Pipeline.Run(current), ct);
            choices = fresh.Actionable.Select(f => (f, CouncilPlanner.Pick(f, variant.Author))).Where(p => p.Item2 is not null)
                .Select(p => (p.f, p.Item2!)).ToList();
            result.Log.Add("Сцена изменилась после согласования — позиция пересчитана на текущей модели");
        }
        var sandbox = DesignFixEngine.Sandbox(scene);
        long fingerprint = StructuralTopology.Fingerprint(scene.Elements);
        var before = await Task.Run(() => (K: DesignFixEngine.Analyze(sandbox, consultation.Loads).MaxUtilization,
            Clashes: ClashDetectionService.Detect(sandbox.Elements.ToList()).Count, Concrete: DesignFixEngine.Concrete(sandbox), Count: sandbox.Elements.Count), ct);
        var outcome = await Task.Run(() => CouncilPlanner.ApplyVariant(sandbox, choices, consultation.Loads, decision.Modifiers, Pipeline), ct);
        if (StructuralTopology.Fingerprint(scene.Elements) != fingerprint)
            throw new InvalidOperationException("Сцена изменилась во время применения решения — выберите вариант ещё раз.");

        int depth = scene.UndoDepth;
        var selected = scene.SelectedElements.Select(e => e.Id).ToList();
        var primary = scene.SelectedElement?.Id;
        scene.LoadScene(sandbox.Elements.ToList(), sandbox.BuildingGroups.ToList(), recordUndo: true, axes: sandbox.GridAxes.ToList());
        scene.SetSelection(selected.Select(scene.FindById).OfType<ArchitecturalElement>(), scene.FindById(primary));

        // Снимок живой сцены — в UI-потоке: перечисление ObservableCollection из пула гонялось бы с правками пользователя.
        var copy = scene.Elements.Select(e => e.Clone()).ToList();
        var groups = scene.BuildingGroups.Select(g => g.Clone()).ToList();
        var after = await Task.Run(() =>
        {
            var ctx = new VerificationContext(copy, groups, [], consultation.Loads);
            var rep = Pipeline.Run(ctx);
            return (K: rep.MaxUtilization, Clashes: rep.ClashCount, Critical: rep.CriticalCount,
                Concrete: BimAnalyticsService.Calculate(copy, groups, 1, 1, k => k).TotalConcrete);
        }, ct);

        result.Applied = true;
        result.VariantTitle = variant.Title + (decision.Modifiers.IsEmpty ? string.Empty : $" (уточнения ГИПа: {decision.Modifiers.Describe()})");
        result.KBefore = before.K;
        result.KAfter = after.K;
        result.ClashesBefore = before.Clashes;
        result.ClashesAfter = after.Clashes;
        result.CriticalAfter = after.Critical;
        result.ConcreteDelta = after.Concrete - before.Concrete;
        result.UndoSteps = scene.UndoDepth - depth;
        result.ElementsAdded = scene.Elements.Count - before.Count;
        result.ActionsApplied = outcome.Applied;
        result.Log.AddRange(outcome.Log);
        result.Log.AddRange(outcome.Failed.Select(f => "Не применено: " + f));
        result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>Текстовый ответ ГИПа: сначала C#-разбор («вариант 1, балки двутавровые стальные»), иначе — модель Координатора.</summary>
    public async Task<CouncilDecision?> InterpretReplyAsync(CouncilConsultation consultation, string text, AiModelOption dockModel, CancellationToken ct)
    {
        if (CouncilPlanner.Interpret(consultation, text) is { } decision) return decision;
        var syn = ResolveAssignments(dockModel).First(a => a.Role == AiAgentRole.MasterSynthesizer);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReplyTimeout(syn.Model));
            var (system, user) = AgentPrompts.Reply(consultation, text);
            var context = new AiRequestContext { Task = text, Model = syn.Model };
            var response = await gateway.InvokeAsync(new AgentInvocation(syn, system, user, [], context, ReplyTokens(syn.Model, 1200), AgentPrompts.IsReplyJson), cts.Token);
            return CouncilPlanner.FromReplyJson(consultation, text, response.Text);
        }
        catch (Exception ex) when (ex is AiProviderException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // ═════════════════════════════ Внутреннее ═════════════════════════════

    /// <summary>
    /// Нормоконтроль: конвейер C#-проверщиков на песочнице и (при замечаниях) реплика модели-Аудитора с рецептами исправления
    /// на чистом русском — по точному списку дефектов (коды, элементы с координатами, варианты с прогнозом K_исп).
    /// </summary>
    private async Task<AuditOutcome> VerifyAsync(SceneStateService target, AgentAssignment norms, Action<AgentDebateEntry> post, bool includeInsolation, bool ask,
        CancellationToken ct, int round = 0, int maxRounds = 0)
    {
        var entry = Entry(norms, !ask ? " · проверки ядра расчётов" : round > 0 ? $" · проверка доработки {round}/{maxRounds}" : null);
        post(entry);
        var sw = Stopwatch.StartNew();
        var context = VerificationContext.FromScene(target, Loads, location?.Invoke(), includeInsolation);
        var report = await Task.Run(() => Pipeline.Run(context), ct);
        entry.Found = report.Findings.Count(f => f.Severity != VerificationSeverity.Info);
        string top = string.Join("; ", report.Findings.Where(f => f.Severity != VerificationSeverity.Info).Take(3).Select(f => f.Title));
        string text = $"Конвейер из {Pipeline.Verifiers.Count} проверщиков ({report.ElapsedMilliseconds:0} мс): критичных {report.CriticalCount}, предупреждений {report.WarningCount}; " +
                      $"K_исп max {K(report.MaxUtilization)}, коллизий {report.ClashCount}." + (top.Length > 0 ? " " + top + "." : string.Empty);
        string? message = null, reasoning = null;
        IReadOnlyList<string> recipes = [];
        if (ask && report.Findings.Any(f => f.Severity != VerificationSeverity.Info))
        {
            entry.Text = text;
            var reply = await AskReplyAsync(norms, AgentPrompts.Norms(report, norms.Model.IsLocal, target.Elements.ToList()), ct, entry);
            text += reply.Status == AgentEntryStatus.Done ? " Рецепт Аудитора: " + reply.Text + RecipesText(reply.Recipes) : " " + reply.Text;
            if (reply.Status == AgentEntryStatus.Done) message = reply.Text;
            recipes = reply.Recipes;
            reasoning = reply.Reasoning;
        }
        entry.Complete(report.CriticalCount > 0 ? AgentEntryStatus.Warning : AgentEntryStatus.Done, text, sw.Elapsed, reasoning);
        return new AuditOutcome(report, message, recipes);
    }

    private static string RecipesText(IReadOnlyList<string> recipes) =>
        recipes.Count == 0 ? string.Empty : " Рецепты: " + string.Join("; ", recipes.Take(4)) + (recipes.Count > 4 ? $" и ещё {recipes.Count - 4}" : string.Empty) + ".";

    /// <summary>
    /// Короткая реплика модели (JSON с полем message, у Аудитора — ещё recipes); рассуждения рассуждающих моделей идут в
    /// ленту живым потоком. При сбое — пометка о резервном C#-решении.
    /// </summary>
    private async Task<AgentReply> AskReplyAsync(AgentAssignment who, (string System, string User) prompt, CancellationToken ct, AgentDebateEntry? entry = null)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReplyTimeout(who.Model));
            var context = new AiRequestContext { Task = prompt.User, Model = who.Model };
            var response = await gateway.InvokeAsync(new AgentInvocation(who, prompt.System, prompt.User, [], context, ReplyTokens(who.Model, 1500), AgentPrompts.IsReplyJson,
                entry is null ? null : Live(entry, sceneJson: false)), cts.Token);
            if (AgentPrompts.TryReadObject(response.Text, out var root) && AgentPrompts.ReadString(root, "message") is { Length: > 0 } message)
                return new AgentReply(AgentEntryStatus.Done, message.Trim(), response.Reasoning, AgentPrompts.ReadRecipes(root));
            return new AgentReply(AgentEntryStatus.Warning, "(ответ модели не распознан — использован отчёт ядра расчётов)", response.Reasoning, []);
        }
        catch (Exception ex) when (ex is AiProviderException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            string code = ex is AiProviderException p ? p.Code : "AIA-TIMEOUT";
            return new AgentReply(AgentEntryStatus.Warning, $"(модель недоступна: {code} — решение по расчётной модели)", null, []);
        }
    }

    /// <summary>Локальные модели (Ollama) на встроенной графике отвечают медленнее, рассуждающие — ещё и думают: таймаут больше.</summary>
    private static TimeSpan ReplyTimeout(AiModelOption model) =>
        model.IsOllama ? TimeSpan.FromMinutes(model.IsReasoning ? 8 : 5) : CommentTimeout;

    /// <summary>
    /// Рассуждения расходуют тот же лимит токенов, что и ответ: провайдер Ollama добавляет бюджет рассуждений сам,
    /// облачным рассуждающим моделям запас добавляется здесь.
    /// </summary>
    private static int ReplyTokens(AiModelOption model, int tokens) => model.IsReasoning && !model.IsOllama ? tokens + 6000 : tokens;

    /// <summary>Выбор Координатора: модель (JSON с полем variant), иначе — автоматический вариант КБ.</summary>
    private async Task<CouncilDecision> ChooseVariantAsync(CouncilConsultation consultation, AgentAssignment syn, CancellationToken ct, AgentDebateEntry? entry = null)
    {
        var fallback = consultation.Variants.FirstOrDefault(v => v.Author == FixAuthor.Automatic && v.CriticalAfter == 0)
                       ?? consultation.Variants.OrderBy(v => v.CriticalAfter).ThenBy(v => v.KAfter).First();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReplyTimeout(syn.Model));
            var (system, user) = AgentPrompts.Synthesizer(consultation);
            var context = new AiRequestContext { Task = user, Model = syn.Model };
            var response = await gateway.InvokeAsync(new AgentInvocation(syn, system, user, [], context, ReplyTokens(syn.Model, 800), AgentPrompts.IsReplyJson,
                entry is null ? null : Live(entry, sceneJson: false)), cts.Token);
            if (entry is not null && response.HasReasoning) entry.Reasoning = response.Reasoning!.Trim();
            if (AgentPrompts.TryReadObject(response.Text, out var root) && AgentPrompts.ReadString(root, "variant") is { } id &&
                consultation.Variants.FirstOrDefault(v => v.Id == id.Trim().ToLowerInvariant()) is { } chosen && chosen.CriticalAfter <= fallback.CriticalAfter)
                return new CouncilDecision(chosen.Id, CouncilModifiers.None, Rationale: AgentPrompts.ReadString(root, "message")?.Trim());
        }
        catch (Exception ex) when (ex is AiProviderException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Модель Координатора недоступна — решает прогноз C#-моделирования.
        }
        return new CouncilDecision(fallback.Id, CouncilModifiers.None);
    }

    private static CouncilConsultation NewConsultation(VerificationReport report, List<CouncilVariant> variants, bool fromDesign)
    {
        var c = new CouncilConsultation { Report = report, Loads = report.Analysis.Settings.Clone(), FromDesign = fromDesign };
        c.Issues.AddRange(report.Actionable);
        c.Variants.AddRange(variants);
        var lead = c.Issues.Take(2).Select(i => i.Title.TrimEnd('.')).ToList();
        c.Title = "Согласование решения с главным инженером (Вами)";
        c.Summary = $"Агенты нашли {c.Issues.Count} {Plural(c.Issues.Count, "спорный узел", "спорных узла", "спорных узлов")}: " +
                    string.Join("; ", lead) + (c.Issues.Count > 2 ? $" и ещё {c.Issues.Count - 2}" : string.Empty) +
                    ". Модель не исправлена — выберите решение или опишите своё.";
        return c;
    }

    private static string Plural(int n, string one, string few, string many) =>
        n % 10 == 1 && n % 100 != 11 ? one : n % 10 is >= 2 and <= 4 && (n % 100 < 10 || n % 100 >= 20) ? few : many;

    /// <summary>Позиции Конструктора и Архитектора по вариантам (дебаты «прочность — замысел»).</summary>
    /// <param name="planning">Время моделирования вариантов на копии сцены (у позиций — общее).</param>
    private static void PostPositions(IReadOnlyList<CouncilVariant> variants, IReadOnlyList<AgentAssignment> assignments, Action<AgentDebateEntry> post, TimeSpan planning)
    {
        var structural = variants.FirstOrDefault(v => v.Author == FixAuthor.Structural);
        var architect = variants.FirstOrDefault(v => v.Author == FixAuthor.Architect);
        if (structural is not null)
        {
            var e = Entry(assignments.First(a => a.Role == AiAgentRole.StructuralEngineer), " · расчётная модель");
            post(e);
            e.Found = structural.Choices.Count;
            e.Complete(AgentEntryStatus.Done,
                $"Предлагаю вариант {structural.Number}: {CouncilPlanner.LeadOption(structural)}{(structural.Choices.Count > 1 ? $" и ещё {structural.Choices.Count - 1} исправл." : string.Empty)}. " +
                $"Прогноз K_исп {K(structural.KBefore)} → {K(structural.KAfter)}, коллизий {structural.ClashesBefore} → {structural.ClashesAfter}.", planning);
        }
        if (architect is not null)
        {
            var e = Entry(assignments.First(a => a.Role == AiAgentRole.ChiefArchitect), " · расчётная модель");
            post(e);
            e.Found = architect.Choices.Count;
            string concrete = structural is null ? string.Empty : CouncilPlanner.ConcreteComparison(structural, architect);
            e.Complete(AgentEntryStatus.Done,
                (structural is null ? string.Empty : "Возражаю против лишних балок под перекрытием. ") +
                $"Вариант {architect.Number}: {CouncilPlanner.LeadOption(architect)}" +
                (concrete.Length > 0 ? $" — {concrete}" : string.Empty) + $". Прогноз K_исп → {K(architect.KAfter)}, коллизий → {architect.ClashesAfter}.", planning);
        }
    }

    private void CommitToLive(SceneStateService sandbox, SolvedResponse solved)
    {
        scene.LoadScene(sandbox.Elements.ToList(), sandbox.BuildingGroups.ToList(), recordUndo: scene.Elements.Count > 0, axes: sandbox.GridAxes.ToList());
        if (solved.Mode == SolverMode.Modify) scene.SelectById(solved.Result.SelectId);
    }

    /// <summary>Итог выпуска: снимок живой сцены — в UI-потоке, конвейер проверщиков — в пуле (не замораживает интерфейс).</summary>
    private async Task<CouncilApplyResult> MeasureAsync(CouncilVariant v, VariantOutcome outcome, int depth, VerificationReport before, CancellationToken ct)
    {
        var copy = scene.Elements.Select(e => e.Clone()).ToList();
        var groups = scene.BuildingGroups.Select(g => g.Clone()).ToList();
        var loads = Loads;
        var rep = await Task.Run(() => Pipeline.Run(new VerificationContext(copy, groups, [], loads)), ct);
        var r = new CouncilApplyResult
        {
            Applied = true,
            VariantTitle = v.Title,
            KBefore = before.MaxUtilization,
            KAfter = rep.MaxUtilization,
            ClashesBefore = before.ClashCount,
            ClashesAfter = rep.ClashCount,
            CriticalAfter = rep.CriticalCount,
            UndoSteps = scene.UndoDepth - depth,
            ElementsAdded = outcome.Added,
            ActionsApplied = outcome.Applied
        };
        r.Log.AddRange(outcome.Log);
        return r;
    }
}
