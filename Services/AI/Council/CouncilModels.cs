using AIArchitectStudio.Models;
using AIArchitectStudio.Services.Verification;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIArchitectStudio.Services.AI;

/// <summary>Настройки Совета КБ в settings.json: режим дока, согласование, назначение моделей на роли.</summary>
public sealed class CouncilSettings
{
    public const string AutoModelId = "auto";

    /// <summary>Режим дока «Совет КБ (Мульти-ИИ)» вместо «1 Агент».</summary>
    public bool Enabled { get; set; }

    /// <summary>Согласовывать важные правки с пользователем (иначе — полный автопилот).</summary>
    public bool ConsultUser { get; set; } = true;

    /// <summary>Роль → ключ модели (<c>auto</c> — автоподбор лучшей доступной модели).</summary>
    public Dictionary<string, string> Bindings { get; set; } = [];

    /// <summary>ИИ-комментарии Нормоконтроля и Координатора при аудите открытой сцены (C#-проверщики работают всегда).</summary>
    public bool AiCommentaryInAudit { get; set; } = true;

    /// <summary>
    /// Лимит раундов самоисправления «ИИ ↔ C#-движок» при проектировании: черновик Конструктора проверяется КЭ-расчётом и
    /// коллизиями, при дефектах Конструктор получает их точный список и рецепт Аудитора и дорабатывает модель (0 — без доработки).
    /// </summary>
    public int MaxReworkIterations { get; set; } = 3;

    public string BindingFor(AiAgentRole role) =>
        Bindings.TryGetValue(AgentRoles.Info(role).Key, out var id) && !string.IsNullOrWhiteSpace(id) ? id : AutoModelId;

    public CouncilSettings Clone() => new()
    {
        Enabled = Enabled,
        ConsultUser = ConsultUser,
        AiCommentaryInAudit = AiCommentaryInAudit,
        MaxReworkIterations = MaxReworkIterations,
        Bindings = new Dictionary<string, string>(Bindings)
    };
}

/// <summary>Раунд цикла самоисправления: что нашёл C#-движок в черновике и сколько дефектов осталось.</summary>
public sealed record DesignIteration(int Number, int Critical, int Clashes, double MaxUtilization, int Elements, IReadOnlyList<string> Codes, bool Reworked)
{
    /// <summary>Дефекты, требующие доработки моделью: критичные замечания, коллизии, перегрузка K_исп &gt; 1.0.</summary>
    public bool HasDefects => Critical > 0 || Clashes > 0 || MaxUtilization > 1.0;

    /// <summary>Сравнение качества черновиков: меньше критичных, затем коллизий, затем перегрузки.</summary>
    public (int, int, double) Rank => (Critical, Clashes, Math.Max(0, MaxUtilization - 1.0));
}

/// <summary>Назначение модели на роль после маршрутизации.</summary>
public sealed record AgentAssignment(AiAgentRole Role, AiModelOption Model, double Temperature, bool IsAuto, bool IsFallback, string Reason)
{
    public AgentRoleInfo Info => AgentRoles.Info(Role);
}

/// <summary>Состояние реплики агента в ленте «Ход мысли Совета».</summary>
public enum AgentEntryStatus
{
    Working,
    Done,
    Warning,
    Failed,
    Decision
}

/// <summary>Реплика агента в ленте дебатов: роль, модель, время ответа, текст и найденные/исправленные узлы.</summary>
public sealed partial class AgentDebateEntry : ObservableObject
{
    public AgentDebateEntry(AiAgentRole role, string modelLabel, bool russian = true)
    {
        Role = role;
        var info = AgentRoles.Info(role);
        RoleTitle = info.Short(russian);
        Glyph = info.Glyph;
        ColorHex = info.ColorHex;
        ModelLabel = modelLabel;
        Time = DateTime.Now.ToString("HH:mm:ss");
    }

    public AiAgentRole Role { get; }
    public string RoleTitle { get; }
    public string Glyph { get; }
    public string ColorHex { get; }
    public string Time { get; }

    [ObservableProperty]
    public partial string ModelLabel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(StatusGlyph), nameof(StatusColorHex))]
    public partial AgentEntryStatus Status { get; set; } = AgentEntryStatus.Working;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>«Внутренние рассуждения агента» рассуждающих моделей (deepseek-r1 и т.п.) — мелкий полупрозрачный курсив в ленте.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning), nameof(ReasoningTitle))]
    public partial string Reasoning { get; set; } = string.Empty;

    /// <summary>Рассуждения развёрнуты полностью (иначе — последние строки).</summary>
    [ObservableProperty]
    public partial bool IsReasoningExpanded { get; set; }

    public bool HasReasoning => Reasoning.Length > 0;

    public string ReasoningTitle => Reasoning.Length < 1000
        ? $"Внутренние рассуждения агента · {Reasoning.Length} симв."
        : $"Внутренние рассуждения агента · {Reasoning.Length / 1000.0:0.0} тыс. симв.";

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetricsText), nameof(HasMetrics))]
    public partial int Found { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetricsText), nameof(HasMetrics))]
    public partial int Fixed { get; set; }

    public bool IsWorking => Status == AgentEntryStatus.Working;

    public bool HasMetrics => Found > 0 || Fixed > 0;

    public string MetricsText => Fixed > 0 ? $"найдено {Found} · исправлено {Fixed}" : $"найдено {Found}";

    public string StatusGlyph => Status switch
    {
        AgentEntryStatus.Done => "",
        AgentEntryStatus.Warning => "",
        AgentEntryStatus.Failed => "",
        AgentEntryStatus.Decision => "",
        _ => ""
    };

    public string StatusColorHex => Status switch
    {
        AgentEntryStatus.Done => "#30D158",
        AgentEntryStatus.Warning => "#FF9F0A",
        AgentEntryStatus.Failed => "#FF453A",
        AgentEntryStatus.Decision => "#0A84FF",
        _ => "#8E8E93"
    };

    public void Complete(AgentEntryStatus status, string text, TimeSpan elapsed, string? reasoning = null)
    {
        Text = text;
        if (!string.IsNullOrWhiteSpace(reasoning)) Reasoning = reasoning.Trim();
        ElapsedText = elapsed.TotalSeconds < 1 ? $"{elapsed.TotalMilliseconds:0} мс" : $"{elapsed.TotalSeconds:0.0} с";
        Status = status;
    }

    /// <summary>
    /// Потоковое обновление реплики во время генерации (вызывается в UI-потоке через Progress&lt;T&gt;): рассуждения — по
    /// мере поступления, текст — фрагмент поля «message» реплики или счётчик символов JSON сцены. После завершения
    /// реплики запоздавшие обновления игнорируются.
    /// </summary>
    public void Stream(AiStreamProgress progress, bool sceneJson)
    {
        if (Status != AgentEntryStatus.Working) return;
        if (progress.Reasoning.Length > 0) Reasoning = progress.Reasoning;
        if (sceneJson)
        {
            if (progress.Answer.Length > 0) Text = $"Формирует JSON модели · {progress.Answer.Length:N0} симв.";
            else if (progress.Reasoning.Length > 0) Text = "Рассуждает…";
            return;
        }
        if (PartialMessage(progress.Answer) is { Length: > 0 } message) Text = message;
        else if (progress.Reasoning.Length > 0) Text = "Рассуждает…";
    }

    /// <summary>Незавершённое значение поля «message» из потока JSON: {"message": "Плита пере…</summary>
    public static string? PartialMessage(string json)
    {
        int key = json.IndexOf("\"message\"", StringComparison.Ordinal);
        if (key < 0) return null;
        int colon = json.IndexOf(':', key + 9);
        if (colon < 0) return null;
        int quote = json.IndexOf('"', colon + 1);
        if (quote < 0) return null;
        var sb = new System.Text.StringBuilder();
        for (int i = quote + 1; i < json.Length; i++)
        {
            char ch = json[i];
            if (ch == '"') break;
            if (ch == '\\' && i + 1 < json.Length)
            {
                char next = json[++i];
                sb.Append(next switch { 'n' => '\n', 't' => ' ', '"' => '"', '\\' => '\\', '/' => '/', _ => next });
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>Вариант решения для карточки согласования: выбор исправления по каждому замечанию и прогноз на копии сцены.</summary>
public sealed class CouncilVariant
{
    public string Id { get; set; } = string.Empty;
    public int Number { get; set; }
    public FixAuthor Author { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<(VerificationFinding Finding, ProposedFixOption Option)> Choices { get; } = [];

    public double KBefore { get; set; }
    public double KAfter { get; set; }
    public int ClashesBefore { get; set; }
    public int ClashesAfter { get; set; }
    public int CriticalAfter { get; set; }
    public double ConcreteBefore { get; set; }
    public double ConcreteAfter { get; set; }
    public int ElementsAdded { get; set; }
    public List<string> Log { get; } = [];

    public string Signature => string.Join("|", Choices.Select(c => c.Option.Id));

    public string MetricsText =>
        $"K_исп {VerificationText.K(KBefore)} → {VerificationText.K(KAfter)} · коллизий {ClashesBefore} → {ClashesAfter} · " +
        $"бетон {(ConcreteAfter - ConcreteBefore >= 0 ? "+" : "−")}{Math.Abs(ConcreteAfter - ConcreteBefore).ToString("0.0", VerificationText.Ru)} м³";
}

/// <summary>Карточка «Согласование решения с главным инженером»: найденные проблемы и варианты исправления.</summary>
public sealed class CouncilConsultation
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public required VerificationReport Report { get; init; }
    public List<VerificationFinding> Issues { get; } = [];
    public List<CouncilVariant> Variants { get; } = [];
    public required StructuralLoadSettings Loads { get; init; }

    /// <summary>Отпечаток сцены на момент планирования: если сцена изменилась, варианты пересчитываются.</summary>
    public long Fingerprint { get; set; }

    public bool FromDesign { get; set; }
    public DateTime CreatedAt { get; } = DateTime.Now;
}

/// <summary>Уточнения пользователя к варианту («балки двутавровые стальные», «плита 250 мм»).</summary>
public sealed record CouncilModifiers(string? BeamProfile = null, string? BeamMaterial = null, string? ColumnProfile = null, string? ColumnMaterial = null,
    double? SlabThicknessMm = null, double? BeamHeightMm = null, double? BeamWidthMm = null, double? ColumnSizeMm = null)
{
    public static readonly CouncilModifiers None = new();

    public bool IsEmpty => this == None;

    public string Describe()
    {
        var parts = new List<string>();
        if (BeamProfile is not null || BeamMaterial is not null)
            parts.Add($"балки — {(BeamProfile == ProfileTypes.IBeam ? "двутавр" : BeamProfile == ProfileTypes.Pipe ? "труба" : "прямоугольные")}" +
                      $"{(BeamMaterial is { } m ? m.Contains("steel", StringComparison.Ordinal) ? ", сталь" : ", ж/б" : string.Empty)}");
        if (BeamHeightMm is { } bh) parts.Add($"высота балок {bh:0} мм");
        if (ColumnProfile is not null || ColumnMaterial is not null)
            parts.Add($"колонны — {(ColumnProfile == ProfileTypes.Circle ? "круглые" : ColumnProfile == ProfileTypes.Pipe ? "трубы" : ColumnProfile == ProfileTypes.IBeam ? "двутавр" : "прямоугольные")}" +
                      $"{(ColumnMaterial is { } cm ? cm.Contains("steel", StringComparison.Ordinal) ? ", сталь" : ", ж/б" : string.Empty)}");
        if (ColumnSizeMm is { } cs) parts.Add($"сечение колонн {cs:0} мм");
        if (SlabThicknessMm is { } t) parts.Add($"плита не тоньше {t:0} мм");
        return string.Join("; ", parts);
    }
}

/// <summary>Решение пользователя по карточке: вариант (v1…v3 или keep) и уточнения.</summary>
public sealed record CouncilDecision(string VariantId, CouncilModifiers Modifiers, string? UserText = null, string? Rationale = null)
{
    public const string Keep = "keep";
    public bool IsKeep => VariantId == Keep;
}

/// <summary>Итог применения решения к сцене: K_исп и коллизии до/после, шаги Undo, журнал действий.</summary>
public sealed class CouncilApplyResult
{
    public bool Applied { get; set; }
    public string VariantTitle { get; set; } = string.Empty;
    public double KBefore { get; set; }
    public double KAfter { get; set; }
    public int ClashesBefore { get; set; }
    public int ClashesAfter { get; set; }
    public int CriticalAfter { get; set; }
    public double ConcreteDelta { get; set; }
    public int UndoSteps { get; set; }
    public int ElementsAdded { get; set; }
    public int ActionsApplied { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public List<string> Log { get; } = [];

    public string Summary =>
        Applied
            ? $"{VariantTitle}: K_исп {VerificationText.K(KBefore)} → {VerificationText.K(KAfter)}, коллизий {ClashesBefore} → {ClashesAfter}, " +
              $"действий {ActionsApplied}, новых элементов {ElementsAdded}, {ElapsedMilliseconds:0} мс, шагов Undo {UndoSteps}"
            : VariantTitle;
}

/// <summary>Итог работы Совета: черновик построен и проверен; карточка согласования или применённое автопилотом решение.</summary>
public sealed class CouncilRunResult
{
    public CouncilConsultation? Consultation { get; set; }
    public CouncilApplyResult? AutoApplied { get; set; }
    public VerificationReport? Report { get; set; }
    public AiRunResult? Draft { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<AgentAssignment> Assignments { get; } = [];

    /// <summary>Раунды самоисправления «ИИ ↔ C#-движок» (первый — черновик Конструктора).</summary>
    public List<DesignIteration> Iterations { get; } = [];

    /// <summary>Номер раунда, чья модель выпущена (лучший по критичным замечаниям, коллизиям и перегрузке).</summary>
    public int ReleasedIteration { get; set; }
}
