using System.Text;
using System.Text.Json;
using AIArchitectStudio.Services.Verification;

namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Ролевые промпты Совета КБ. Архитектор и Конструктор отвечают JSON сцены (общая схема SystemPrompts), Нормоконтроль и
/// Координатор — коротким JSON-объектом реплики. Для локальных моделей с контекстом 2048 — компактные английские версии.
/// </summary>
public static class AgentPrompts
{
    private const string ArchitectRole = """

РОЛЬ: Главный архитектор (ГИП) Мультиагентного конструкторского бюро.
Ты отвечаешь за градостроительную посадку, сетку координационных осей (1..N, А..Я), высотные отметки уровней,
объёмно-планировочную структуру, наружные стены и витражи, окна и двери, лестницы, форму кровли.
Несущий каркас детально не проектируй — по твоей модели его выполнит Главный конструктор: задай сетку осей
(блок blueprint.gridAxes или колонны по осям) и контуры этажей. В "message" — 2–3 предложения об архитектурном замысле.
""";

    private const string StructuralRole = """

РОЛЬ: Главный конструктор КЖ/КМ Мультиагентного конструкторского бюро.
Тебе передана архитектурная модель ГИПа (JSON). Верни ПОЛНУЮ сцену одним JSON: все элементы архитектора без изменения
их положения и формы (те же id) + несущий каркас: фундаментная плита, колонны по сетке осей (шаг 6–7.2 м), балки
высотой h ≥ L/12, плиты перекрытий толщиной h ≥ L/30, консоли h ≥ L/10, ядро жёсткости (core) при 9 и более этажах,
slab_opening над каждой лестницей и шахтой, колонны верхних этажей строго над колоннами нижних.
Назначь пироги: "layers": "floor" | "flat_roof" | "foundation" | "exterior_wall" (или массив слоёв
{"name","thickness" (мм),"role": structural|insulation|screed|waterproofing|finish}). В "message" — 2–3 предложения о схеме каркаса.
""";

    private const string NormsSystem = """
Ты — инженер нормоконтроля и КЭ-аудита Мультиагентного конструкторского бюро (СП 20.13330, СП 63.13330, СП 14.13330,
СП 1.13130). На вход — отчёт C#-конвейера проверщиков: коды замечаний, K_исп, прогибы, продавливание, коллизии и
готовые варианты исправления. Ответь ОДНИМ JSON-объектом без пояснений:
{"message": "критика 2–4 предложения по-русски: главные риски и рецепт усиления с числами",
 "priorities": ["код замечания", ...],
 "recipes": [{"code": "код замечания", "fix": "точное исправление для Конструктора: какой элемент, что и на сколько изменить (мм, м)"}]}
Не используй эмодзи.
""";

    private const string ReworkRole = """

РЕЖИМ: ДОРАБОТКА МОДЕЛИ ПО ЗАМЕЧАНИЯМ C#-ДВИЖКА.
Твой черновик прогнан через КЭ-экспресс-расчёт (СП 63.13330, СП 20.13330) и Clash Detection. Ниже — точный список
дефектов с кодами, элементами (имя, тип, этаж, координаты центра в метрах) и рецептом Инженера-аудитора.
Верни ПОЛНУЮ исправленную сцену одним JSON (все элементы, те же id у неизменённых), устранив КАЖДЫЙ дефект:
перегрузка K_исп > 1.0 — увеличь толщину плиты/сечение или добавь балки по осям колонн или промежуточные колонны,
сквозные до фундамента; колонна или стена без опоры — продолжи опору вниз до фундамента или перенеси на ось нижней
колонны; проём на колонне — сдвинь проём; марш без проёма — добавь slab_opening над лестницей. Архитектурный замысел
(контуры, отметки, остекление) не меняй без необходимости. В "message" — что исправлено (1–3 предложения).
""";

    private const string SynthesizerSystem = """
Ты — координатор BIM-проекта Мультиагентного конструкторского бюро. Тебе даны варианты решения спорных узлов
(v1 — позиция Конструктора, v2 — позиция Архитектора, v3 — автоматическое решение КБ) с прогнозом K_исп, коллизий и
расхода бетона по моделированию. Разреши спор «красота — прочность»: выбери вариант, который обеспечивает K_исп ≤ 1.0
и 0 коллизий при наименьшем вмешательстве в архитектуру. Ответь ОДНИМ JSON-объектом:
{"message": "обоснование 1–2 предложения по-русски", "variant": "v1" | "v2" | "v3"}
Не используй эмодзи.
""";

    private const string ReplySystem = """
Ты — координатор BIM-проекта. Главный инженер (пользователь) ответил текстом на карточку согласования вариантов
(v1 Конструктор, v2 Архитектор, v3 автоматически, keep — оставить как есть). Определи выбранный вариант и уточнения.
Ответь ОДНИМ JSON-объектом:
{"variant": "v1|v2|v3|keep", "beamProfile": "rectangle|i_beam|pipe|null", "beamMaterial": "concrete|steel|null",
 "columnProfile": "rectangle|circle|pipe|i_beam|null", "columnMaterial": "concrete|steel|null", "slabThicknessMm": число или null,
 "beamHeightMm": число или null, "columnSizeMm": число или null, "message": "1 предложение: как понято решение"}
""";

    private const string CompactArchitect = " ROLE: chief architect. Define grid axes, levels, outer walls, curtain walls, windows, doors, stairs and roof. Keep the frame minimal: the structural engineer adds it.";
    private const string CompactStructural = " ROLE: structural engineer. Return the FULL scene: architect elements (same ids) + frame: foundation, columns on grid (6-7.2 m), beams h>=L/12, slabs h>=L/30, slab_opening over stairs, core if >=9 floors. Optional \"layers\":\"floor|flat_roof|foundation|exterior_wall\".";

    public static (string System, string User) Architect(AIOrchestrator ai, AiRequestContext ctx)
    {
        var (system, user) = ai.BuildPrompts(ctx);
        return (system + (ctx.Model.IsLocal ? CompactArchitect : ArchitectRole), user);
    }

    public static (string System, string User) Structural(AIOrchestrator ai, AiRequestContext ctx, string? architectJson, string? architectMessage)
    {
        var (system, user) = ai.BuildPrompts(ctx);
        system += ctx.Model.IsLocal ? CompactStructural : StructuralRole;
        if (string.IsNullOrWhiteSpace(architectJson)) return (system, user + (ctx.Model.IsLocal ? "\nArchitect unavailable: design the whole building." : "\n\nМодель ГИПа недоступна — спроектируй здание целиком (архитектура + каркас)."));
        int limit = ctx.Model.IsLocal ? 2200 : 60000;
        string json = architectJson.Length > limit ? architectJson[..limit] + "…" : architectJson;
        var sb = new StringBuilder(user);
        sb.AppendLine().AppendLine();
        sb.AppendLine(ctx.Model.IsLocal ? "ARCHITECT MODEL:" : "АРХИТЕКТУРНАЯ МОДЕЛЬ ГИПа (JSON):");
        sb.AppendLine(json);
        if (!string.IsNullOrWhiteSpace(architectMessage)) sb.AppendLine((ctx.Model.IsLocal ? "ARCHITECT NOTE: " : "ЗАМЫСЕЛ ГИПа: ") + architectMessage);
        return (system, sb.ToString());
    }

    public static (string System, string User) Norms(VerificationReport report, bool compact, IReadOnlyList<Models.ArchitecturalElement>? elements = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Сводка: {report.Summary}.");
        sb.Append(DefectList(report, compact ? 6 : 14, compact ? 1 : 3, elements));
        return (NormsSystem, sb.ToString());
    }

    /// <summary>
    /// Список дефектов на чистом русском: код, важность, заголовок, затронутые элементы (имя, тип, этаж, центр X/Y/Z, габарит),
    /// объяснение и готовые варианты исправления с прогнозом K_исп — для Аудитора и для доработки Конструктором.
    /// </summary>
    public static string DefectList(VerificationReport report, int maxFindings, int maxOptions, IReadOnlyList<Models.ArchitecturalElement>? elements)
    {
        var byId = elements?.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var sb = new StringBuilder();
        foreach (var f in report.Findings.Where(f => f.Severity != VerificationSeverity.Info).Take(maxFindings))
        {
            sb.AppendLine($"- [{f.Code}] {f.SeverityTitle}: {f.Title}");
            if (byId is not null)
                foreach (var id in f.AffectedElementIds.Take(3))
                    if (byId.TryGetValue(id, out var e))
                        sb.AppendLine(FormattableString.Invariant(
                            $"    элемент «{e.DisplayTitle}» (id {e.Id}, {e.Type}, этаж {e.LevelIndex + 1}): центр X {e.X:0.##}, Y {e.Y:0.##}, Z {e.Z:0.##} м; W×H×D {e.Width:0.###}×{e.Height:0.###}×{e.Depth:0.###} м"));
            if (f.DetailedExplanation.Length > 0) sb.AppendLine("    почему: " + AiHttp.Truncate(f.DetailedExplanation, 260));
            foreach (var o in f.ProposedFixOptions.Where(o => !o.IsKeep).Take(maxOptions))
                sb.AppendLine($"    вариант ({VerificationText.AuthorTitle(o.Author)}): {o.Title}{(o.PredictedUtilization is { } k ? $", K → {VerificationText.K(k)}" : string.Empty)}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Доработка Конструктором: исходная задача + текущий JSON модели + дефекты C#-движка + рецепт Аудитора. Ответ — полная
    /// исправленная сцена (тот же формат, что у черновика).
    /// </summary>
    public static (string System, string User) Rework(AIOrchestrator ai, AiRequestContext ctx, string currentJson, VerificationReport report,
        IReadOnlyList<Models.ArchitecturalElement> elements, string? auditorMessage, IReadOnlyList<string> recipes, int round, int maxRounds)
    {
        var (system, user) = ai.BuildPrompts(ctx);
        system += (ctx.Model.IsLocal ? CompactStructural : StructuralRole) + ReworkRole;
        int limit = ctx.Model.IsLocal ? 1800 : 60000;
        var sb = new StringBuilder(user);
        sb.AppendLine().AppendLine();
        sb.AppendLine($"ДОРАБОТКА {round}/{maxRounds}. ТЕКУЩАЯ МОДЕЛЬ (JSON):");
        sb.AppendLine(currentJson.Length > limit ? currentJson[..limit] + "…" : currentJson);
        sb.AppendLine();
        sb.AppendLine($"ДЕФЕКТЫ C#-ДВИЖКА: {report.Summary}.");
        sb.Append(DefectList(report, ctx.Model.IsLocal ? 5 : 16, ctx.Model.IsLocal ? 1 : 2, elements));
        if (!string.IsNullOrWhiteSpace(auditorMessage)) sb.AppendLine().AppendLine("РЕЦЕПТ ИНЖЕНЕРА-АУДИТОРА: " + auditorMessage.Trim());
        foreach (var r in recipes.Take(12)) sb.AppendLine("  - " + r);
        return (system, sb.ToString());
    }

    /// <summary>Рецепты Аудитора из ответа: [{"code","fix"}] → «[код] исправление».</summary>
    public static List<string> ReadRecipes(JsonElement root)
    {
        var list = new List<string>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("recipes", out var recipes) || recipes.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in recipes.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } s) list.Add(s.Trim());
            else if (ReadString(r, "fix") is { Length: > 0 } fix) list.Add((ReadString(r, "code") is { Length: > 0 } code ? $"[{code}] " : string.Empty) + fix.Trim());
        }
        return list;
    }

    public static (string System, string User) Synthesizer(CouncilConsultation consultation)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Замечаний к решению: {consultation.Issues.Count}; {consultation.Report.Summary}.");
        foreach (var v in consultation.Variants)
            sb.AppendLine($"{v.Id}: {v.Title} — {v.Description}. Прогноз: {v.MetricsText}, критичных после — {v.CriticalAfter}.");
        return (SynthesizerSystem, sb.ToString());
    }

    public static (string System, string User) Reply(CouncilConsultation consultation, string text)
    {
        var sb = new StringBuilder();
        foreach (var v in consultation.Variants) sb.AppendLine($"{v.Id}: {v.Title} — {v.Description}");
        sb.AppendLine().AppendLine("ОТВЕТ ГИПа: " + text.Trim());
        return (ReplySystem, sb.ToString());
    }

    /// <summary>Ответ-реплика агента: JSON-объект с полем message (для веб-моста и разбора).</summary>
    public static bool IsReplyJson(string text) => TryReadObject(text, out var root) && root.TryGetProperty("message", out _);

    /// <summary>Извлекает первый JSON-объект из ответа модели.</summary>
    public static bool TryReadObject(string text, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (ReasoningText.HasReasoningTags(text) && ReasoningText.Separate(text).Answer is { Length: > 0 } answer) text = answer;
        int start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char ch = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (ch == '\\') escape = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(text[start..(i + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                        root = doc.RootElement.Clone();
                        return root.ValueKind == JsonValueKind.Object;
                    }
                    catch (JsonException)
                    {
                        break;
                    }
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return false;
    }

    public static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double? ReadNumber(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
