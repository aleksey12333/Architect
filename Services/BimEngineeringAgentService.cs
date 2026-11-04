using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.Verification;

namespace AIArchitectStudio.Services;

/// <summary>
/// Окружение задания агента: сцена, нагрузки, расценки, выделение, постройка и связь с интерфейсом (окно подтверждения расценок,
/// составитель плана основной локальной моделью). Окно подтверждения цен — всегда у пользователя: агент ничего не применяет сам.
/// </summary>
public sealed class AgentContext
{
    public required SceneStateService Scene { get; init; }
    public required StructuralLoadSettings Loads { get; init; }
    public GateCostRates Rates { get; init; } = GateCostRates.Default;
    public IReadOnlyList<ArchitecturalElement> Selection { get; init; } = [];

    /// <summary>
    /// Инструмент pull_supplier_prices: интерфейс извлекает расценки и открывает окно «Проверка подтянутых расценок — всё ли верно?».
    /// Возвращает итог для карточки инструмента (null — пользователь отменил).
    /// </summary>
    public Func<AgentToolCall, CancellationToken, Task<string?>>? PriceHost { get; init; }

    /// <summary>
    /// Составитель плана основной локальной моделью (system, user → текст JSON); вызывается, только если разбор задачи ядром не нашёл
    /// инструментов. null — план только по разбору ядра.
    /// </summary>
    public Func<string, string, CancellationToken, Task<string>>? ModelPlanner { get; init; }

    public string PlannerName { get; init; } = string.Empty;
}

/// <summary>Описание инструмента в реестре: имя, инженерная подпись, глиф, меняет ли модель, аргументы (для плана модели).</summary>
public sealed record AgentToolInfo(string Name, string Title, string Glyph, bool Mutates, string Description, string ArgumentsHint);

/// <summary>
/// Реестр детерминированных инструментов Инженерного агента. Каждый инструмент — обёртка над сервисом программы (расчётное ядро,
/// аудит, доводка, мастер размеров, библиотека ГОСТ/СП, конструирование КЖ, ручное моделирование с логикой проёмов, веб-импорт
/// прайса, показатели Renga); изменяющие инструменты работают с любой сценой (живой или песочницей) по Id элементов.
/// </summary>
public static class AgentToolRegistry
{
    public const string InspectProject = "inspect_project";
    public const string RunFeaAudit = "run_fea_audit";
    public const string AutoFixStructure = "auto_fix_structure";
    public const string BatchResizeBuilding = "batch_resize_building";
    public const string AssignGostStandardType = "assign_gost_standard_type";
    public const string ConfigureRebarAndSupports = "configure_rebar_and_supports";
    public const string AddOrModifyElement = "add_or_modify_element";
    public const string PullSupplierPrices = "pull_supplier_prices";
    public const string ComputeRengaQuantities = "compute_renga_quantities";

    public static readonly IReadOnlyList<AgentToolInfo> Tools =
    [
        new(InspectProject, "Сводка проекта", "\uE946", false, "чтение сводки здания: оси, этажи, элементы, K_исп max, стоимость каркаса", "{}"),
        new(RunFeaAudit, "Аудит расчётной схемы", "\uE9F5", false, "логический аудит КЭ-расчёта: баланс сил, перегруженные элементы, σ и τ", "{}"),
        new(AutoFixStructure, "Двухпроходная доводка каркаса", "\uE90F", true, "доводка каркаса до K_исп ≤ 1,0 (утолщение плит, сечения, стойки)", "{}"),
        new(BatchResizeBuilding, "Мастер размеров", "\uE740", true, "изменение высот этажей и шагов осей",
            "{\"storey\":\"1|all\",\"height_mm\":3600} или {\"axis\":\"x|z\",\"from\":\"2\",\"to\":\"3\",\"length_mm\":7200}"),
        new(AssignGostStandardType, "Типоразмер по ГОСТ/СП", "\uE8F1", true, "назначение или подбор типоразмера из библиотеки ГОСТ/СП",
            "{\"category\":\"column|beam|slab\",\"mark\":\"auto|К 500×500\",\"target\":\"selected|most_loaded\"}"),
        new(ConfigureRebarAndSupports, "Армирование и закрепления", "\uE9D9", true, "закрепление μ, диаметр арматуры, шаг хомутов, защитный слой, каркасы продавливания",
            "{\"fixity\":\"fixed|pinned_fixed|pinned|cantilever\",\"bar_mm\":20,\"stirrup_mm\":150,\"cover_mm\":30,\"punching\":\"true\"}"),
        new(AddOrModifyElement, "Элемент модели", "\uE710", true, "создание колонны, стены, балки, плиты, проёма (с логикой проёмов), полой кровли, марша",
            "{\"kind\":\"column|wall|beam|slab|window|door|roof|stair\",\"shape\":\"gable_hollow|hip_hollow|flat_parapet|mansard|barrel_vault\"}"),
        new(PullSupplierPrices, "Веб-импорт прайса по ссылке", "\uE774", false, "расценки по ссылке на магазин или из прошлой сметы — с подтверждением пользователя",
            "{\"url\":\"https://…\",\"path\":\"C:\\\\…\\\\смета.aia\",\"index_percent\":5}"),
        new(ComputeRengaQuantities, "Показатели Renga", "\uE8EF", false, "V_нетто, V_брутто, S_опал, масса, арматура, R0", "{\"target\":\"selected|all\"}")
    ];

    public static AgentToolInfo? Find(string name) => Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public static AgentToolCall Call(string name, params (string Key, string Value)[] args)
    {
        var info = Find(name) ?? throw new ArgumentException($"Инструмент «{name}» не найден в реестре");
        var call = new AgentToolCall { Name = info.Name, Title = info.Title, Glyph = info.Glyph, Mutates = info.Mutates };
        foreach (var (k, v) in args) call.Arguments[k] = v;
        return call;
    }
}

/// <summary>
/// Внутрипрограммный Инженерный агент: объединяет основную локальную модель (план, если разбор ядром не справился), цепочку
/// рассуждений <see cref="EngineeringReasoningService"/> (пять фаз, численная проверка плана в песочнице) и инструменты программы.
/// Порядок: разбор задачи → план вызовов → рассуждения (варианты — например, типоразмеры ГОСТ по возрастанию) → выполнение
/// выбранного плана на живой сцене одной транзакцией журнала (один Ctrl+Z) → окно подтверждения расценок (если в плане есть
/// веб-импорт) → карточки инструментов и инженерный отчёт.
/// </summary>
public static class BimEngineeringAgentService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ───────────────────────────── План ─────────────────────────────

    /// <summary>
    /// Разбор задачи ядром программы (без модели): ключевые слова и числа русского текста → вызовы инструментов в порядке упоминания.
    /// </summary>
    public static List<AgentToolCall> Plan(string task, AgentContext? ctx = null)
    {
        string t = task.ToLowerInvariant().Replace('ё', 'е');
        var found = new List<(int Index, AgentToolCall Call)>();

        void Add(string pattern, Func<Match, AgentToolCall?> make)
        {
            var m = Regex.Match(t, pattern);
            if (m.Success && make(m) is { } call && found.All(f => f.Call.Name != call.Name)) found.Add((m.Index, call));
        }

        Add(@"сводк|состав проекта|осмотр|что в проекте|обзор модели", _ => AgentToolRegistry.Call(AgentToolRegistry.InspectProject));
        Add(@"аудит|проверь (?:расчет|схем|каркас|напряж)|кэ-|напряжени|расчетн\w* схем", _ => AgentToolRegistry.Call(AgentToolRegistry.RunFeaAudit));
        Add(@"довед|доводк|исправь каркас|усиль|усилени|k\s*(?:исп)?\s*(?:≤|<=|до)\s*1", _ => AgentToolRegistry.Call(AgentToolRegistry.AutoFixStructure));
        Add(@"высот\w*\s+(?:\d+\s*-?\s*(?:го|й|ого)?\s+)?этаж|этаж\w*\s+высот", m => StoreyCall(t));
        Add(@"(?:шаг|пролет)\w*\s+(?:между\s+)?ос", m => SpanCall(t));
        Add(@"(?:подбер|подбор|назнач|примени|замени)\w*\s+(?:\w+\s+){0,3}?(?:сечени|типоразмер|профил|гост)|сечени\w*\s+(?:\w+\s+){0,2}?по\s+гост|типоразмер",
            m => GostCall(t));
        Add(@"армирован|хомут|защитн\w*\s+сло|диаметр\w*\s+(?:рабочей\s+)?арматур|продавлив|закреплен|шарнир|защемлен", m => RebarCall(t));
        Add(@"(?:добав|созда|постав|построй|сделай)\w*\s+(?:\w+\s+){0,2}?(?:колонн|стен|балк|плит|окн|двер|кровл|крыш|лестниц|марш)", m => ElementCall(t));
        Add(@"https?://|прайс|расценк|цен\w*\s+(?:по\s+ссылк|из\s+смет|поставщ|магазин)|подтян\w*\s+цен|прошл\w*\s+смет", m => PriceCall(task));
        Add(@"renga|опалубк|объем\w*\s+(?:бетона\s+)?нетто|v_?нетто|показател\w*\s+renga|теплотехн|\br0\b|ведомост\w*\s+объем|посчитай\s+объем", _ =>
            AgentToolRegistry.Call(AgentToolRegistry.ComputeRengaQuantities, ("target", ctx is { Selection.Count: > 0 } ? "selected" : "all")));
        return found.OrderBy(f => f.Index).Select(f => f.Call).ToList();
    }

    private static readonly Regex LengthRx = new(@"(\d+(?:[.,]\d+)?)\s*(мм|м)(?![а-я])", RegexOptions.Compiled);

    private static double? LengthMm(string t, int from = 0)
    {
        foreach (Match m in LengthRx.Matches(t, Math.Clamp(from, 0, t.Length)))
            if (double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, Inv, out var v))
                return m.Groups[2].Value == "мм" ? v : v * 1000;
        return null;
    }

    private static AgentToolCall? StoreyCall(string t)
    {
        int at = Regex.Match(t, @"высот\w*|этаж").Index;
        if (LengthMm(t, at) is not { } mm) return null;
        var n = Regex.Match(t, @"(\d+)\s*-?\s*(?:го|й|ого)?\s+этаж|этаж\w*\s+(?:№\s*)?(\d+)(?!\s*(?:мм|м))");
        string storey = n.Success ? (n.Groups[1].Success ? n.Groups[1].Value : n.Groups[2].Value)
            : t.Contains("первого этаж") ? "1" : t.Contains("второго этаж") ? "2" : t.Contains("третьего этаж") ? "3" : "all";
        return AgentToolRegistry.Call(AgentToolRegistry.BatchResizeBuilding, ("storey", storey), ("height_mm", mm.ToString("0", Inv)));
    }

    private static AgentToolCall? SpanCall(string t)
    {
        var m = Regex.Match(t, @"ос\w*\s+(\d+|[а-я])\s*[-–—/]\s*(\d+|[а-я])");
        int at = Regex.Match(t, @"(?:шаг|пролет)\w*").Index;
        if (LengthMm(t, at) is not { } mm) return null;
        bool numeric = !m.Success || char.IsDigit(m.Groups[1].Value[0]);
        var call = AgentToolRegistry.Call(AgentToolRegistry.BatchResizeBuilding, ("axis", numeric ? "x" : "z"), ("length_mm", mm.ToString("0", Inv)));
        if (m.Success)
        {
            call.Arguments["from"] = m.Groups[1].Value.ToUpperInvariant();
            call.Arguments["to"] = m.Groups[2].Value.ToUpperInvariant();
        }
        else call.Arguments["span"] = "all";
        return call;
    }

    private static AgentToolCall GostCall(string t)
    {
        string category = Regex.IsMatch(t, @"балк|ригел") ? "beam" : Regex.IsMatch(t, @"плит|перекрыти") ? "slab" : "column";
        var mark = Regex.Match(t, @"(\d{3})\s*[x×х*]\s*(\d{3})");
        var call = AgentToolRegistry.Call(AgentToolRegistry.AssignGostStandardType, ("category", category),
            ("mark", mark.Success ? $"{mark.Groups[1].Value}×{mark.Groups[2].Value}" : "auto"));
        call.Arguments["target"] = Regex.IsMatch(t, @"выделен|выбран") ? "selected" : "most_loaded";
        return call;
    }

    private static AgentToolCall RebarCall(string t)
    {
        var call = AgentToolRegistry.Call(AgentToolRegistry.ConfigureRebarAndSupports);
        if (Regex.Match(t, @"(?:ø|⌀|диаметр\w*\s+(?:рабочей\s+)?(?:арматур\w*\s+)?)\s*(\d{2})") is { Success: true } d) call.Arguments["bar_mm"] = d.Groups[1].Value;
        if (Regex.Match(t, @"шаг\w*\s+хомут\w*\s*(?:до\s+|=\s*)?(\d{2,3})") is { Success: true } s) call.Arguments["stirrup_mm"] = s.Groups[1].Value;
        if (Regex.Match(t, @"защитн\w*\s+сло\w*\s*(?:до\s+|=\s*)?(\d{2})") is { Success: true } c) call.Arguments["cover_mm"] = c.Groups[1].Value;
        if (t.Contains("продавлив")) call.Arguments["punching"] = Regex.IsMatch(t, @"(?:убер|сним|выключ|без)\w*\s+(?:\w+\s+){0,3}?продавлив") ? "false" : "true";
        if (t.Contains("консол")) call.Arguments["fixity"] = "cantilever";
        else if (Regex.IsMatch(t, @"шарнир\w*\s*[-–—]\s*защемл|шарнир\w*\s+и\s+защемл")) call.Arguments["fixity"] = "pinned_fixed";
        else if (t.Contains("шарнир")) call.Arguments["fixity"] = "pinned";
        else if (t.Contains("защемлен")) call.Arguments["fixity"] = "fixed";
        call.Arguments["target"] = Regex.IsMatch(t, @"выделен|выбран") ? "selected" : t.Contains("продавлив") && !Regex.IsMatch(t, @"колонн") ? "slabs" : "columns";
        return call;
    }

    private static AgentToolCall ElementCall(string t)
    {
        string kind = Regex.IsMatch(t, @"кровл|крыш") ? "roof" : Regex.IsMatch(t, @"лестниц|марш") ? "stair" : Regex.IsMatch(t, @"окн") ? "window"
            : Regex.IsMatch(t, @"двер") ? "door" : Regex.IsMatch(t, @"стен") ? "wall" : Regex.IsMatch(t, @"балк") ? "beam" : Regex.IsMatch(t, @"плит") ? "slab" : "column";
        var call = AgentToolRegistry.Call(AgentToolRegistry.AddOrModifyElement, ("kind", kind));
        if (kind == "roof")
            call.Arguments["shape"] = t.Contains("вальм") ? RoofShapes.HipHollow : t.Contains("односкат") ? RoofShapes.ShedHollow : t.Contains("парапет") || t.Contains("плоск") ? RoofShapes.FlatParapet
                : t.Contains("мансард") ? RoofShapes.Mansard : t.Contains("свод") ? RoofShapes.BarrelVault : RoofShapes.GableHollow;
        if (Regex.Match(t, @"(\d{2})\s*°|уклон\w*\s*(\d{2})") is { Success: true } a) call.Arguments["angle"] = a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Value;
        return call;
    }

    private static AgentToolCall PriceCall(string task)
    {
        var call = AgentToolRegistry.Call(AgentToolRegistry.PullSupplierPrices);
        var urls = Regex.Matches(task, @"https?://[^\s""'<>]+").Select(m => m.Value.TrimEnd('.', ',', ';', ')')).ToList();
        if (urls.Count > 0) call.Arguments["url"] = string.Join(" ", urls);
        var paths = Regex.Matches(task, @"[A-Za-z]:\\[^""<>|*?\r\n]+?\.(?:aia|csv|json|txt)", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
        if (paths.Count > 0) call.Arguments["path"] = string.Join("|", paths);
        if (Regex.Match(task, @"индекс\w*\s*(?:цен\s*)?(?:на\s*)?([+−-]?\d+(?:[.,]\d+)?)\s*%", RegexOptions.IgnoreCase) is { Success: true } idx)
            call.Arguments["index_percent"] = idx.Groups[1].Value.Replace('−', '-').Replace(',', '.');
        return call;
    }

    /// <summary>Системный промпт плана для основной локальной модели: только вызовы инструментов реестра в JSON.</summary>
    public static string PlannerSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты — планировщик инструментов инженерной BIM-программы. Верни ТОЛЬКО JSON вида {\"tool_calls\":[{\"name\":\"…\",\"arguments\":{…}}]}.");
        sb.AppendLine("Доступные инструменты (name — описание — аргументы):");
        foreach (var tool in AgentToolRegistry.Tools) sb.AppendLine($"- {tool.Name} — {tool.Description} — {tool.ArgumentsHint}");
        sb.AppendLine("Порядок вызовов — порядок выполнения. Не придумывай инструменты. Числа — в миллиметрах.");
        return sb.ToString();
    }

    /// <summary>Разбор ответа модели-планировщика: вызовы известных инструментов с аргументами-строками.</summary>
    public static List<AgentToolCall> ParseModelPlan(string text)
    {
        var calls = new List<AgentToolCall>();
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return calls;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("tool_calls", out var list) || list.ValueKind != JsonValueKind.Array) return calls;
            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var n) || AgentToolRegistry.Find(n.GetString() ?? string.Empty) is not { } info) continue;
                var call = AgentToolRegistry.Call(info.Name);
                if (item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
                    foreach (var p in a.EnumerateObject()) call.Arguments[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.ToString();
                calls.Add(call);
            }
        }
        catch (JsonException)
        {
            // Модель вернула не JSON — план пустой, агент сообщит об этом.
        }
        return calls;
    }

    // ───────────────────────────── Выполнение ─────────────────────────────

    /// <summary>
    /// Задание агента: план (разбор ядром, иначе основная локальная модель) → цепочка рассуждений с проверкой плана в песочнице →
    /// выполнение на живой сцене одной транзакцией журнала → веб-импорт расценок с подтверждением пользователя → отчёт.
    /// </summary>
    public static async Task<AgentRunResult> RunAsync(string task, AgentContext ctx, IReadOnlyList<AgentToolCall>? plan = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new AgentRunResult { Task = task };
        var calls = plan?.ToList() ?? Plan(task, ctx);
        result.PlannerName = "разбор задачи ядром программы";
        if (calls.Count == 0 && ctx.ModelPlanner is not null)
        {
            try
            {
                calls = ParseModelPlan(await ctx.ModelPlanner(PlannerSystemPrompt(), "Задача инженера: " + task, ct).ConfigureAwait(true));
                result.PlannedByModel = calls.Count > 0;
                if (calls.Count > 0) result.PlannerName = ctx.PlannerName.Length > 0 ? ctx.PlannerName : "основная локальная модель";
            }
            catch (Exception ex) when (ex is AI.AiProviderException or HttpRequestException or IOException)
            {
                result.Report.Add("Основная локальная модель не ответила: " + ex.Message);
            }
        }
        if (calls.Count == 0)
        {
            // Задача не распознана: агент осматривает проект и проверяет схему (безопасные инструменты только чтения).
            calls = [AgentToolRegistry.Call(AgentToolRegistry.InspectProject), AgentToolRegistry.Call(AgentToolRegistry.RunFeaAudit)];
            result.Report.Add("Задача не содержит распознанных действий — выполнены сводка проекта и аудит расчётной схемы");
        }
        var scene = ctx.Scene;
        string group = MainGroup(scene, ctx.Selection);
        foreach (var call in calls) ResolveTargets(call, ctx, group);

        // ── Рассуждения: варианты плана в песочнице ──
        var sceneCalls = calls.Where(c => c.Name != AgentToolRegistry.PullSupplierPrices).ToList();
        var mutating = sceneCalls.Where(c => c.Mutates).ToList();
        var variants = VariantsOf(mutating, ctx);
        var hypotheses = variants.Select(v => new EngineeringHypothesis
        {
            Title = v.Title,
            Norm = v.Norm,
            Actions = v.Calls.Select(c => $"{c.Title}: {Describe(c)}").ToList(),
            Apply = s =>
            {
                foreach (var c in v.Calls)
                {
                    var copy = Clone(c);
                    if (Execute(copy, ctx, s, group) is { } error) return error;
                }
                return null;
            },
            Tag = v
        }).ToList();
        var focus = mutating.SelectMany(c => c.Argument("ids").Split(',', StringSplitOptions.RemoveEmptyEntries)).Distinct().ToList();
        var request = new ReasoningRequest
        {
            Task = task,
            Scene = scene,
            Loads = ctx.Loads,
            Rates = ctx.Rates,
            Intents =
            [
                $"План ({result.PlannerName}): {string.Join(" → ", calls.Select(c => c.Name))}",
                .. calls.Where(c => Describe(c).Length > 0).Select(c => $"{c.Title}: {Describe(c)}")
            ],
            Affected = focus.Select(scene.FindById).OfType<ArchitecturalElement>().ToList(),
            Norms = calls.SelectMany(NormsOf).Distinct().ToList(),
            Hypotheses = hypotheses,
            FocusIds = focus
        };
        var trace = EngineeringReasoningService.Reason(request, out var chosen);
        result.Trace = trace;
        var chosenCalls = (chosen?.Tag as PlanVariant)?.Calls;

        // ── Выполнение на живой сцене: одна транзакция журнала ──
        int depth = scene.UndoDepth;
        string step = "Инженерный агент: " + Short(task);
        result.TransactionName = step;
        // Корректирующие вызовы выбранного варианта (доводка каркаса) — после последнего изменяющего вызова плана.
        var execution = new List<AgentToolCall>(sceneCalls);
        if (chosenCalls is not null)
        {
            var extra = chosenCalls.Where(c => c.Argument("_slot") == CorrectionSlot).Select(Clone).ToList();
            int last = execution.FindLastIndex(c => c.Mutates);
            execution.InsertRange(last < 0 ? execution.Count : last + 1, extra);
            int at = calls.FindLastIndex(c => c.Mutates);
            calls.InsertRange(at < 0 ? calls.Count : at + 1, extra);
        }
        // Транзакция — только если в плане есть изменяющие вызовы (чтение не открывает шаг и не помечает проект изменённым).
        using (var transaction = execution.Any(c => c.Mutates) ? scene.BeginUndoTransaction(step) : null)
        {
            foreach (var call in execution)
            {
                if (call.Mutates)
                {
                    if (chosenCalls is null)
                    {
                        call.Status = AgentToolStatus.Skipped;
                        call.Summary = mutating.Count > 0 && hypotheses.Count > 0 ? "не выполнено: ни один вариант не прошёл проверку в песочнице" : "пропущено";
                        result.ToolCalls.Add(call);
                        continue;
                    }
                    var picked = chosenCalls.FirstOrDefault(c => c.Name == call.Name && c.Argument("_slot") == call.Argument("_slot")) ?? call;
                    foreach (var (k, v) in picked.Arguments) call.Arguments[k] = v;
                }
                var swTool = Stopwatch.StartNew();
                string? error = Execute(call, ctx, scene, group);
                call.ElapsedMs = swTool.Elapsed.TotalMilliseconds;
                if (error is not null)
                {
                    call.Status = AgentToolStatus.Failed;
                    call.Summary = error;
                }
                else if (call.Status == AgentToolStatus.Pending) call.Status = AgentToolStatus.Succeeded;
                result.ToolCalls.Add(call);
            }
            if (!result.ToolCalls.Any(c => c.Mutates && c.Status == AgentToolStatus.Succeeded)) transaction?.Discard();
        }
        result.Changed = scene.UndoDepth == depth + 1;

        // ── Расценки поставщика: окно подтверждения у пользователя ──
        foreach (var call in calls.Where(c => c.Name == AgentToolRegistry.PullSupplierPrices))
        {
            var swTool = Stopwatch.StartNew();
            if (ctx.PriceHost is null)
            {
                call.Status = AgentToolStatus.Skipped;
                call.Summary = "окно подтверждения расценок недоступно";
            }
            else
            {
                try
                {
                    string? summary = await ctx.PriceHost(call, ct).ConfigureAwait(true);
                    call.Status = summary is null ? AgentToolStatus.Skipped : AgentToolStatus.Succeeded;
                    call.Summary = summary ?? "пользователь отменил применение расценок";
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or JsonException)
                {
                    call.Status = AgentToolStatus.Failed;
                    call.Summary = ex.Message;
                }
            }
            call.ElapsedMs = swTool.Elapsed.TotalMilliseconds;
            result.ToolCalls.Add(call);
        }
        // Порядок карточек — порядок плана.
        result.ToolCalls.Sort((a, b) => calls.IndexOf(a).CompareTo(calls.IndexOf(b)));
        result.Success = result.ToolCalls.All(c => c.Status != AgentToolStatus.Failed) && (mutating.Count == 0 || result.Changed || chosenCalls is not null);
        Summarize(result, depth, scene);
        result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    private static void Summarize(AgentRunResult result, int depth, SceneStateService scene)
    {
        int ok = result.ToolCalls.Count(c => c.Status == AgentToolStatus.Succeeded);
        result.Report.Insert(0, $"Инструментов выполнено: {ok} из {result.ToolCalls.Count} · {result.Trace.Summary}");
        if (result.Changed) result.Report.Add($"Изменения модели — один шаг журнала «{result.TransactionName}» (отмена — Ctrl+Z)");
        else if (result.ToolCalls.Any(c => c.Mutates)) result.Report.Add("Модель не изменена");
        foreach (var call in result.ToolCalls.Where(c => c.Status == AgentToolStatus.Failed)) result.Report.Add($"Ошибка «{call.Title}»: {call.Summary}");
    }

    /// <summary>Метка корректирующего вызова, добавленного рассуждениями (а не пользователем).</summary>
    private const string CorrectionSlot = "fix";

    /// <summary>Вариант плана: заголовок, норматив и вызовы изменяющих инструментов с подставленными аргументами.</summary>
    public sealed record PlanVariant(string Title, string Norm, IReadOnlyList<AgentToolCall> Calls);

    /// <summary>
    /// Варианты плана для песочницы: первый вызов с выбором (подбор типоразмера «auto») раскрывается в кандидатов библиотеки по
    /// возрастанию сечения, остальные вызовы — как есть. Без изменяющих вызовов вариантов нет (оценивается текущее состояние).
    /// </summary>
    private static List<PlanVariant> VariantsOf(List<AgentToolCall> mutating, AgentContext ctx)
    {
        if (mutating.Count == 0) return [];
        for (int i = 0; i < mutating.Count; i++) mutating[i].Arguments["_slot"] = i.ToString(Inv);
        int choice = mutating.FindIndex(c => c.Name == AgentToolRegistry.AssignGostStandardType && c.Argument("mark", "auto") == "auto");
        if (choice < 0)
        {
            string norm = string.Join("; ", mutating.SelectMany(NormsOf).Distinct().Take(2));
            var list = new List<PlanVariant> { new(string.Join(" + ", mutating.Select(c => c.Title)), norm, mutating.Select(Clone).ToList()) };
            // Корректирующий вариант: тот же план с двухпроходной доводкой каркаса (если исходный перегрузит конструкции).
            if (mutating.All(c => c.Name != AgentToolRegistry.AutoFixStructure))
            {
                var fix = AgentToolRegistry.Call(AgentToolRegistry.AutoFixStructure);
                fix.Arguments["_slot"] = CorrectionSlot;
                list.Add(new PlanVariant(list[0].Title + " + доводка каркаса до K_исп ≤ 1,0", norm + "; СП 63.13330.2018, п. 8.1", mutating.Select(Clone).Append(fix).ToList()));
            }
            return list;
        }
        var gost = mutating[choice];
        var variants = new List<PlanVariant>();
        foreach (var entry in Candidates(gost.Argument("category", "column"), ctx).Take(6))
        {
            var calls = mutating.Select(Clone).ToList();
            calls[choice].Arguments["mark"] = entry.Id;
            calls[choice].Arguments["entry"] = entry.Id;
            // Варианты отличаются только типоразмером — он и есть заголовок гипотезы (остальной план — в фазе 2).
            variants.Add(new PlanVariant($"Типоразмер {entry.Caption}", entry.Standard, calls));
        }
        return variants;
    }

    /// <summary>Кандидаты библиотеки ГОСТ/СП для категории (ж/б — по возрастанию площади сечения / толщины).</summary>
    public static List<StandardLibraryEntry> Candidates(string category, AgentContext ctx)
    {
        string type = category switch { "beam" => ElementTypes.Beam, "slab" => ElementTypes.Slab, _ => ElementTypes.Column };
        return StandardLibraryService.BuiltIn.Entries
            .Where(e => e.ElementType == type && (e.Material ?? string.Empty).Contains("concrete", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => type == ElementTypes.Slab ? e.ThicknessMm : e.WidthMm * e.DepthMm).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> NormsOf(AgentToolCall c) => c.Name switch
    {
        AgentToolRegistry.RunFeaAudit => ["СП 20.13330.2016, п. 6 (сочетания нагрузок)", "СП 63.13330.2018, п. 8.1 (прочность сечений)"],
        AgentToolRegistry.AutoFixStructure => ["СП 63.13330.2018, п. 8.1.4, 8.1.46 (прочность, продавливание)", "ГОСТ 27751-2014 (γn)"],
        AgentToolRegistry.BatchResizeBuilding => ["СП 54.13330 / СП 118.13330 (высота этажа)", "СП 63.13330.2018, п. 8.1.17 (гибкость стоек l0 = μ·H)"],
        AgentToolRegistry.AssignGostStandardType => ["ГОСТ 25628 / Серия 1.020 (колонны)", "ГОСТ 18980 (ригели)", "СП 63.13330.2018, п. 8.1.16 (внецентренное сжатие)"],
        AgentToolRegistry.ConfigureRebarAndSupports => ["СП 63.13330.2018, п. 10.3 (защитный слой, шаг хомутов)", "п. 8.1.48 (поперечная арматура продавливания)"],
        AgentToolRegistry.AddOrModifyElement => ["СП 1.13130 / СП 54.13330 (проёмы и простенки)"],
        AgentToolRegistry.ComputeRengaQuantities => ["ГОСТ 21.501 (ведомость объёмов)", "СП 50.13330.2012 (R0)"],
        AgentToolRegistry.PullSupplierPrices => ["МДС 81-35.2004 (сметная стоимость по прайсам поставщиков)"],
        _ => []
    };

    private static AgentToolCall Clone(AgentToolCall c)
    {
        var copy = new AgentToolCall { Name = c.Name, Title = c.Title, Glyph = c.Glyph, Mutates = c.Mutates };
        foreach (var (k, v) in c.Arguments) copy.Arguments[k] = v;
        return copy;
    }

    private static string Describe(AgentToolCall c) =>
        string.Join(", ", c.Arguments.Where(a => !a.Key.StartsWith('_') && a.Key is not ("ids" or "entry")).Select(a => $"{a.Key} = {a.Value}"));

    private static string Short(string task)
    {
        string t = Regex.Replace(task.Trim(), @"\s+", " ");
        return t.Length <= 60 ? t : t[..57] + "…";
    }

    /// <summary>Постройка задания: выделенного элемента, иначе самая населённая.</summary>
    private static string MainGroup(SceneStateService scene, IReadOnlyList<ArchitecturalElement> selection) =>
        selection.FirstOrDefault()?.BuildingGroupId ?? scene.Elements.GroupBy(e => e.BuildingGroupId).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? string.Empty;

    /// <summary>Цели вызова (Id элементов) — по живой сцене до песочницы, чтобы все варианты меняли одни и те же элементы.</summary>
    private static void ResolveTargets(AgentToolCall call, AgentContext ctx, string group)
    {
        var scene = ctx.Scene;
        List<ArchitecturalElement> targets;
        switch (call.Name)
        {
            case AgentToolRegistry.AssignGostStandardType:
            {
                var type = call.Argument("category", "column") switch { "beam" => ElementTypes.Beam, "slab" => ElementTypes.Slab, _ => ElementTypes.Column };
                if (call.Argument("target") == "selected" && ctx.Selection.Any(e => e.Type == type)) targets = ctx.Selection.Where(e => e.Type == type).ToList();
                else
                {
                    // Самый нагруженный элемент категории и все однотипные на его этаже той же постройки.
                    var analysis = StructuralAnalysisService.Analyze(scene.Elements.ToList(), scene.BuildingGroups.ToList(), ctx.Loads);
                    var pool = scene.Elements.Where(e => e.Type == type && e.BuildingGroupId == group && ReinforcementCalculator.IsReinforced(e)).ToList();
                    var worst = pool.OrderByDescending(e => analysis.For(e.Id)?.Utilization ?? 0).FirstOrDefault();
                    targets = worst is null ? [] : pool.Where(e => e.LevelIndex == worst.LevelIndex).ToList();
                }
                call.Arguments["ids"] = string.Join(",", targets.Select(e => e.Id));
                break;
            }
            case AgentToolRegistry.ConfigureRebarAndSupports:
            {
                string target = call.Argument("target", "columns");
                targets = target == "selected" && ctx.Selection.Count > 0 ? ctx.Selection.ToList()
                    : target == "slabs" ? scene.Elements.Where(e => e.Category is BimCategory.Slab or BimCategory.Foundation && e.BuildingGroupId == group).ToList()
                    : scene.Elements.Where(e => e.Category is BimCategory.Column && e.BuildingGroupId == group).ToList();
                call.Arguments["ids"] = string.Join(",", targets.Where(ReinforcementCalculator.IsReinforced).Select(e => e.Id));
                break;
            }
            case AgentToolRegistry.AddOrModifyElement when call.Argument("kind") is "window" or "door":
            {
                var wall = ctx.Selection.FirstOrDefault(e => e.Category == BimCategory.Wall)
                           ?? scene.Elements.Where(e => e.Category == BimCategory.Wall && e.BuildingGroupId == group && e.LevelIndex == Math.Max(0, scene.ActiveLevelFilter))
                               .OrderByDescending(e => Math.Max(e.Width, e.Depth)).FirstOrDefault();
                if (wall is not null) call.Arguments["wall"] = wall.Id;
                break;
            }
            case AgentToolRegistry.AddOrModifyElement when call.Argument("kind") == "roof":
            {
                var target = ctx.Selection.FirstOrDefault(e => e.Type is ElementTypes.Roof or ElementTypes.Slab)
                             ?? scene.Elements.Where(e => e.BuildingGroupId == group && e.Type is ElementTypes.Roof or ElementTypes.Slab).OrderByDescending(e => e.TopY).FirstOrDefault();
                if (target is not null) call.Arguments["host"] = target.Id;
                break;
            }
            case AgentToolRegistry.ComputeRengaQuantities:
                if (call.Argument("target") == "selected" && ctx.Selection.Count > 0) call.Arguments["ids"] = string.Join(",", ctx.Selection.Select(e => e.Id));
                break;
        }
    }

    /// <summary>Выполнение вызова на сцене (живой или песочнице). null — успешно (итог — в Summary/Results вызова), иначе причина отказа.</summary>
    public static string? Execute(AgentToolCall call, AgentContext ctx, SceneStateService scene, string group)
    {
        call.Details.Clear();
        call.Results.Clear();
        return call.Name switch
        {
            AgentToolRegistry.InspectProject => InspectProjectTool(call, ctx, scene, group),
            AgentToolRegistry.RunFeaAudit => FeaAuditTool(call, ctx, scene),
            AgentToolRegistry.AutoFixStructure => AutoFixTool(call, ctx, scene),
            AgentToolRegistry.BatchResizeBuilding => ResizeTool(call, scene, group),
            AgentToolRegistry.AssignGostStandardType => GostTool(call, scene),
            AgentToolRegistry.ConfigureRebarAndSupports => RebarTool(call, scene),
            AgentToolRegistry.AddOrModifyElement => ElementTool(call, scene),
            AgentToolRegistry.ComputeRengaQuantities => RengaTool(call, ctx, scene, group),
            _ => $"инструмент «{call.Name}» не выполняется на сцене"
        };
    }

    private static string? InspectProjectTool(AgentToolCall call, AgentContext ctx, SceneStateService scene, string group)
    {
        var snapshot = EngineeringReasoningService.Measure(scene, ctx.Loads, ctx.Rates);
        var info = BatchDimensionService.Describe(scene, group);
        double concrete = QuantityScheduleService.Calculate(scene.Elements.ToList()).ConcreteTotal;
        call.Details.Add(EngineeringReasoningService.DescribeScene(scene));
        if (info is not null)
        {
            call.Details.Add($"Габариты: {Mm(info.LengthX)} × {Mm(info.WidthZ)} × {Mm(info.HeightY)} мм, пролёты X: {string.Join(" · ", info.SpansX.Select(s => Mm(s.Length)))}, Z: {string.Join(" · ", info.SpansZ.Select(s => Mm(s.Length)))}");
            call.Details.Add("Этажи: " + string.Join(", ", info.Storeys.Select(s => $"{s.Level + 1}-й {Mm(s.Height)} мм")));
        }
        call.Results.Add(("K_исп max", snapshot.KMax.ToString("0.00", Ru)));
        call.Results.Add(("Бетон", concrete.ToString("0.0", Ru) + " м³"));
        call.Results.Add(("Каркас", snapshot.CostRub.ToString("N0", Ru) + " ₽"));
        call.Summary = $"{scene.Elements.Count} элементов, этажей {info?.Storeys.Count ?? 0}, K_исп max {snapshot.KMax.ToString("0.00", Ru)} («{snapshot.KMaxElement}»), каркас {snapshot.CostRub.ToString("N0", Ru)} ₽";
        return null;
    }

    private static string? FeaAuditTool(AgentToolCall call, AgentContext ctx, SceneStateService scene)
    {
        var report = FeaLogicalAuditService.Audit(scene.Elements.ToList(), scene.BuildingGroups.ToList(), ctx.Loads, monotonicity: false);
        int passed = report.Criteria.Count(c => c.Passed);
        foreach (var c in report.Criteria) call.Details.Add($"{c.Number}. {c.Title}: {(c.Passed ? "выполнен" : "НЕ выполнен")} — {c.Detail}");
        var worst = report.Stresses.Where(s => !s.Reference).OrderByDescending(s => s.Utilization).Take(3).ToList();
        foreach (var s in worst)
            call.Details.Add($"«{s.Title}»: σ_max = {s.SigmaMax.ToString("0.00", Ru)} МПа, τ = {s.Tau.ToString("0.000", Ru)} МПа, η = {s.StressRatio.ToString("0.00", Ru)}, K_исп = {s.Utilization.ToString("0.00", Ru)}");
        int overloaded = report.Stresses.Count(s => !s.Reference && s.Utilization > 1);
        call.Results.Add(("Критерии", $"{passed} / {report.Criteria.Count}"));
        call.Results.Add(("ΣF = ΣR_y", $"{report.AppliedLoad.ToString("N1", Ru)} = {report.GroundReaction.ToString("N1", Ru)} кН"));
        call.Results.Add(("Перегружено", overloaded.ToString(Ru)));
        call.Summary = $"критериев {passed} из {report.Criteria.Count}, невязка {report.ImbalancePercent.ToString("0.000", Ru)} %, перегружено {overloaded}" +
                       (worst.Count > 0 ? $", σ_max {worst[0].SigmaMax.ToString("0.00", Ru)} МПа у «{worst[0].Title}»" : string.Empty);
        return null;
    }

    private static string? AutoFixTool(AgentToolCall call, AgentContext ctx, SceneStateService scene)
    {
        var gate = VerificationGateService.Inspect(scene, ctx.Loads, "Инженерный агент", ctx.Rates, undoDepth: scene.UndoDepth);
        if (gate.Fixes.Count == 0)
        {
            call.Summary = $"правок не требуется: K_исп max {gate.KBefore.ToString("0.00", Ru)}";
            call.Status = AgentToolStatus.Succeeded;
            return null;
        }
        foreach (var f in gate.Fixes) f.IsAccepted = true;
        var applied = VerificationGateService.ApplyAccepted(scene, gate, ctx.Loads, ctx.Rates);
        call.Details.AddRange(applied.Log.Take(12));
        call.Results.Add(("Правок", applied.Applied.ToString(Ru)));
        call.Results.Add(("K_исп", $"{gate.KBefore.ToString("0.00", Ru)} → {applied.KAfter.ToString("0.00", Ru)}"));
        call.Summary = $"применено правок {applied.Applied}, K_исп {gate.KBefore.ToString("0.00", Ru)} → {applied.KAfter.ToString("0.00", Ru)}, каркас {applied.CostAfterRub.ToString("N0", Ru)} ₽";
        return applied.Applied == 0 ? "правки не применились" : null;
    }

    private static string? ResizeTool(AgentToolCall call, SceneStateService scene, string group)
    {
        var info = BatchDimensionService.Describe(scene, group);
        if (info is null) return "в постройке нет элементов";
        BatchDimensionResult r;
        if (call.Number("height_mm") is { } h)
        {
            string storey = call.Argument("storey", "all");
            var heights = new Dictionary<int, double>();
            if (storey == "all")
                for (int i = 0; i < info.Storeys.Count; i++) heights[i] = h / 1000;
            else if (int.TryParse(storey, NumberStyles.Integer, Inv, out int n) && n >= 1 && n <= info.Storeys.Count) heights[n - 1] = h / 1000;
            else return $"этажа {storey} нет (этажей {info.Storeys.Count})";
            var before = heights.Keys.Select(i => Mm(info.Storeys[i].Height)).Distinct().ToList();
            r = BatchDimensionService.SetStoreyHeights(scene, group, heights);
            call.Results.Add(("Высота этажа", $"{string.Join("/", before)} → {Mm(h / 1000)} мм"));
        }
        else if (call.Number("length_mm") is { } l)
        {
            bool alongX = call.Argument("axis", "x") != "z";
            var spans = alongX ? info.SpansX : info.SpansZ;
            var lengths = new Dictionary<int, double>();
            if (call.Argument("span") == "all") foreach (var s in spans) lengths[s.Index] = l / 1000;
            else if (spans.FirstOrDefault(s => string.Equals(s.From, call.Argument("from"), StringComparison.OrdinalIgnoreCase) && string.Equals(s.To, call.Argument("to"), StringComparison.OrdinalIgnoreCase)) is { } span)
                lengths[span.Index] = l / 1000;
            else return $"пролёта {call.Argument("from")}–{call.Argument("to")} нет";
            r = BatchDimensionService.SetSpans(scene, group, alongX, lengths);
            call.Results.Add(("Пролёт", $"→ {Mm(l / 1000)} мм"));
        }
        else return "не задан размер (height_mm или length_mm)";
        call.Summary = r.Message;
        call.Results.Add(("Изменено", r.Changed.ToString(Ru)));
        return r.Success ? null : r.Message;
    }

    private static string? GostTool(AgentToolCall call, SceneStateService scene)
    {
        var ids = call.Argument("ids").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var targets = ids.Select(scene.FindById).OfType<ArchitecturalElement>().ToList();
        if (targets.Count == 0) return "нет элементов для назначения типоразмера";
        string mark = call.Argument("entry", call.Argument("mark"));
        var library = StandardLibraryService.BuiltIn;
        var entry = library.Find(mark) ?? library.Entries.FirstOrDefault(e => e.ElementType == targets[0].Type &&
                        (e.Mark.Replace(" ", string.Empty).Contains(mark.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase) || e.SizeText.Replace(" ", string.Empty).Contains(mark.Replace(" ", string.Empty))));
        if (entry is null) return $"типоразмер «{mark}» не найден в библиотеке ГОСТ/СП";
        var result = StandardLibraryService.Apply(scene, entry, targets);
        call.AffectedIds.AddRange(result.Applied.Select(e => e.Id));
        call.Results.Add(("Типоразмер", entry.Caption));
        call.Results.Add(("Элементов", result.Applied.Count.ToString(Ru)));
        call.Summary = $"{entry.Caption} назначен {result.Applied.Count} эл." + (result.Skipped.Count > 0 ? $", несовместимых {result.Skipped.Count}" : string.Empty);
        return result.Applied.Count == 0 ? "типоразмер не подходит к выбранным элементам" : null;
    }

    private static string? RebarTool(AgentToolCall call, SceneStateService scene)
    {
        var targets = call.Argument("ids").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(scene.FindById).OfType<ArchitecturalElement>().ToList();
        if (targets.Count == 0) return "нет армируемых элементов";
        int changed = 0;
        scene.PushUndo("Армирование и закрепления");
        foreach (var e in targets)
        {
            bool touched = false;
            if (call.Argument("fixity") is { Length: > 0 } f && e.Category is BimCategory.Column or BimCategory.Wall or BimCategory.Core)
            {
                StructuralDesign.WriteFixity(e, f switch { "fixed" => EndFixity.Fixed, "pinned_fixed" => EndFixity.PinnedFixed, "pinned" => EndFixity.Pinned, "cantilever" => EndFixity.Cantilever, _ => EndFixity.Default });
                touched = true;
            }
            if (call.Number("bar_mm") is not null || call.Number("stirrup_mm") is not null || call.Number("cover_mm") is not null)
            {
                double d = (call.Number("bar_mm") ?? (StructuralDesign.BarDiameter(e) ?? 0.016) * 1000) / 1000;
                double s = (call.Number("stirrup_mm") ?? (StructuralDesign.StirrupSpacing(e) ?? 0.2) * 1000) / 1000;
                double c = (call.Number("cover_mm") ?? (StructuralDesign.Cover(e) ?? 0.04) * 1000) / 1000;
                StructuralDesign.WriteRebar(e, d, s, c);
                touched = true;
            }
            if (call.Argument("punching") is { Length: > 0 } p && e.Category is BimCategory.Slab or BimCategory.Foundation)
            {
                StructuralDesign.WritePunchingRebar(e, p == "true");
                touched = true;
            }
            if (!touched) continue;
            changed++;
            call.AffectedIds.Add(e.Id);
        }
        if (changed > 0) scene.NotifyElementsChanged(targets.Where(e => call.AffectedIds.Contains(e.Id)).ToList());
        call.Results.Add(("Элементов", changed.ToString(Ru)));
        call.Summary = $"параметры КЖ заданы {changed} эл.: {Describe(call)}";
        return changed == 0 ? "параметры не заданы (нет Ø, шага, слоя, закрепления или продавливания)" : null;
    }

    private static string? ElementTool(AgentToolCall call, SceneStateService scene)
    {
        string kind = call.Argument("kind", "column");
        switch (kind)
        {
            case "window" or "door":
            {
                if (scene.FindById(call.Argument("wall")) is not { } wall) return "нет стены для проёма (выделите стену)";
                var r = ManualModelingService.InsertOpening(scene, wall, door: kind == "door");
                call.Summary = r.Message;
                if (r.Primary is { } p) call.AffectedIds.Add(p.Id);
                return r.Success ? null : r.Message;
            }
            case "roof":
            {
                if (scene.FindById(call.Argument("host")) is not { } host) return "нет плиты или кровли для полой кровли";
                double angle = call.Number("angle") ?? 30;
                var request = new RoofDesignRequest(RoofShapes.Normalize(call.Argument("shape", RoofShapes.GableHollow), RoofShapes.GableHollow), angle);
                var result = RoofGeneratorService.Apply(scene.Elements.ToList(), host, request);
                if (result is null) return "кровля не строится по этому элементу";
                scene.PushUndo("Кровля агента");
                if (result.Created) scene.AddElement(result.Roof, recordUndo: false);
                else
                {
                    if (result.Removed.Count > 0) scene.RemoveElements(result.Removed, recordUndo: false);
                    scene.NotifyElementsChanged([result.Roof]);
                }
                call.AffectedIds.Add(result.Roof.Id);
                call.Summary = $"{RoofGeneratorService.ShapeTitle(request.Shape)}: уклон {result.AngleDeg.ToString("0.#", Ru)}°, пролёт {Mm(result.Span)} мм";
                return null;
            }
            default:
            {
                var manual = kind switch
                {
                    "wall" => ManualElementKind.Wall,
                    "beam" => ManualElementKind.Beam,
                    "slab" => ManualElementKind.Slab,
                    "stair" => ManualElementKind.Stairs,
                    _ => ManualElementKind.Column
                };
                var r = ManualModelingService.AddElement(scene, manual);
                call.Summary = r.Message;
                if (r.Primary is { } p) call.AffectedIds.Add(p.Id);
                return r.Success ? null : r.Message;
            }
        }
    }

    private static string? RengaTool(AgentToolCall call, AgentContext ctx, SceneStateService scene, string group)
    {
        var ids = call.Argument("ids").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var elements = scene.Elements.ToList();
        var targets = ids.Count > 0 ? elements.Where(e => ids.Contains(e.Id)).ToList()
            : elements.Where(e => e.BuildingGroupId == group && e.Category is BimCategory.Column or BimCategory.Beam or BimCategory.Slab or BimCategory.Wall or BimCategory.Foundation or BimCategory.Roof).ToList();
        if (targets.Count == 0) return "нет элементов для расчёта показателей";
        var analysis = StructuralAnalysisService.Analyze(elements, scene.BuildingGroups.ToList(), ctx.Loads);
        var punching = BimAnalyticsService.PunchingReinforcedIds(analysis);
        double net = 0, gross = 0, formwork = 0, mass = 0, rebar = 0;
        double? r0Min = null, r0Max = null;
        foreach (var e in targets)
        {
            var p = RengaPropertyService.Compute(e, elements, analysis.For(e.Id)?.Utilization ?? 0, punching.Contains(e.Id));
            net += p.NetVolume;
            gross += p.GrossVolume;
            formwork += p.FormworkArea;
            mass += p.MassTonnes;
            rebar += p.RebarKg;
            if (p.ThermalResistance is { } r0)
            {
                r0Min = Math.Min(r0Min ?? r0, r0);
                r0Max = Math.Max(r0Max ?? r0, r0);
            }
        }
        call.Results.Add(("V_нетто", net.ToString("0.00", Ru) + " м³"));
        call.Results.Add(("V_брутто", gross.ToString("0.00", Ru) + " м³"));
        call.Results.Add(("S_опал", formwork.ToString("0.0", Ru) + " м²"));
        call.Results.Add(("Масса", mass.ToString("0.0", Ru) + " т"));
        call.Results.Add(("Арматура", rebar.ToString("N0", Ru) + " кг"));
        if (r0Min is not null) call.Results.Add(("R0", $"{r0Min.Value.ToString("0.00", Ru)}…{r0Max!.Value.ToString("0.00", Ru)} м²·°С/Вт"));
        call.Summary = $"{targets.Count} эл.: V_нетто {net.ToString("0.00", Ru)} м³, S_опал {formwork.ToString("0.0", Ru)} м², арматура {rebar.ToString("N0", Ru)} кг";
        foreach (var g in targets.GroupBy(e => EngineeringReasoningService.CategoryTitle(e.Category)))
            call.Details.Add($"{g.Key}: {g.Count()} эл.");
        return null;
    }

    private static string Mm(double meters) => (meters * 1000).ToString("N0", Ru);
}
