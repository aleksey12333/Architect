using System.Globalization;
using System.Text;

namespace AIArchitectStudio.Models;

/// <summary>Пять обязательных фаз инженерной цепочки рассуждений (в этом порядке).</summary>
public enum ReasoningPhase
{
    /// <summary>«1. Декомпозиция задачи и исходные данные»: элементы, оси, этажи, нагрузки, ограничения.</summary>
    Decomposition,

    /// <summary>«2. Инженерные гипотезы и нормативы»: применимые пункты СП / ГОСТ и рассматриваемые варианты.</summary>
    Hypotheses,

    /// <summary>«3. Численная проверка в песочнице»: прогон вариантов через расчётное ядро (K_исп, σ, f, стоимость).</summary>
    Sandbox,

    /// <summary>«4. Самопроверка и отсев ошибок»: коллизии, логика проёмов, висящие узлы, перегрузки; корректировка шага.</summary>
    SelfCheck,

    /// <summary>«5. Итоговое инженерное решение»: вывод и список атомарных действий.</summary>
    Decision
}

/// <summary>Итог шага цепочки: пройден, скорректирован автоматически или не прошёл проверку.</summary>
public enum ReasoningStepStatus
{
    Passed,
    Corrected,
    Failed
}

/// <summary>Численный показатель шага «было → стало» (K_исп, σ_max МПа, f мм, стоимость ₽).</summary>
public sealed record ReasoningMetric(string Name, double Before, double After, string Unit, string Format = "0.00")
{
    public double Delta => After - Before;

    public string ToText(CultureInfo culture) =>
        $"{Name}: {Before.ToString(Format, culture)} → {After.ToString(Format, culture)}{(Unit.Length > 0 ? " " + Unit : string.Empty)}";
}

/// <summary>
/// Гипотеза (вариант решения), проверенная в песочнице: заголовок, норматив, показатели расчётного ядра и вердикт.
/// Отклонённая гипотеза хранит причину — её видит пользователь в карточке «Цепочка рассуждений».
/// </summary>
public sealed class ReasoningHypothesis
{
    public string Title { get; set; } = string.Empty;
    public string Norm { get; set; } = string.Empty;
    public double KBefore { get; set; }
    public double KAfter { get; set; }
    public double SigmaMaxMPa { get; set; }
    public double DeflectionMm { get; set; }
    public double CostRub { get; set; }
    public int Clashes { get; set; }
    public bool Passed { get; set; }
    public bool Chosen { get; set; }
    public string RejectReason { get; set; } = string.Empty;
}

/// <summary>Шаг цепочки рассуждений: фаза, строки выкладок, численные показатели и итог.</summary>
public sealed class ReasoningStep
{
    public ReasoningPhase Phase { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> Lines { get; } = [];
    public List<ReasoningMetric> Metrics { get; } = [];
    public ReasoningStepStatus Status { get; set; } = ReasoningStepStatus.Passed;

    /// <summary>Шаг подтверждён расчётом (а не только перечислением данных).</summary>
    public bool IsComputed { get; set; }

    public int Number => (int)Phase + 1;

    public string StatusGlyph => Status switch
    {
        ReasoningStepStatus.Corrected => "\uE7BA",
        ReasoningStepStatus.Failed => "\uE711",
        _ => "\uE73E"
    };

    public string StatusColorHex => Status switch
    {
        ReasoningStepStatus.Corrected => "#FF9F0A",
        ReasoningStepStatus.Failed => "#FF453A",
        _ => "#30D158"
    };
}

/// <summary>
/// Структурированная верифицируемая цепочка инженерных рассуждений (<see cref="Services.EngineeringReasoningService"/>): ровно пять
/// фаз <see cref="ReasoningPhase"/>, проверенные в песочнице гипотезы с цифрами расчётного ядра, отсев ошибок и итоговые атомарные действия.
/// </summary>
public sealed class ReasoningTrace
{
    public static readonly string[] PhaseTitles =
    [
        "1. Декомпозиция задачи и исходные данные",
        "2. Инженерные гипотезы и нормативы",
        "3. Численная проверка в песочнице",
        "4. Самопроверка и отсев ошибок",
        "5. Итоговое инженерное решение"
    ];

    public string Task { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<ReasoningStep> Steps { get; } = [];
    public List<ReasoningHypothesis> Hypotheses { get; } = [];

    /// <summary>Атомарные действия итогового решения (в порядке выполнения).</summary>
    public List<string> Actions { get; } = [];

    /// <summary>Итоговое решение прошло численную проверку: показатели посчитаны ядром, критичных замечаний нет.</summary>
    public bool IsVerified { get; set; }

    /// <summary>Сколько раз шаг скорректирован автоматически (отсеянные гипотезы, повторный подбор).</summary>
    public int Corrections { get; set; }

    /// <summary>Пояснение основной локальной модели (если она была доступна) — только справочно, решения принимает расчёт.</summary>
    public string ModelNarrative { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public double ElapsedMs { get; set; }

    public ReasoningStep Step(ReasoningPhase phase)
    {
        var step = Steps.FirstOrDefault(s => s.Phase == phase);
        if (step is not null) return step;
        step = new ReasoningStep { Phase = phase, Title = PhaseTitles[(int)phase] };
        Steps.Add(step);
        Steps.Sort((a, b) => a.Phase.CompareTo(b.Phase));
        return step;
    }

    /// <summary>«Цепочка рассуждений (5 шагов · расчёт проверен)».</summary>
    public string Summary => $"Цепочка рассуждений ({Steps.Count} {StepsWord(Steps.Count)} · {(IsVerified ? "расчёт проверен" : "требует внимания")})";

    private static string StepsWord(int n) => (n % 100) is >= 11 and <= 14 ? "шагов" : (n % 10) switch { 1 => "шаг", 2 or 3 or 4 => "шага", _ => "шагов" };

    /// <summary>
    /// Проверка структуры: все пять фаз по порядку и непустые, песочница содержит численные показатели расчётного ядра, итог
    /// содержит действия (или явный вывод «изменений не требуется»). Пустой список — цепочка корректна.
    /// </summary>
    public List<string> Validate()
    {
        var issues = new List<string>();
        if (Steps.Count != 5) issues.Add($"фаз {Steps.Count} вместо 5");
        for (int i = 0; i < Steps.Count; i++)
        {
            if ((int)Steps[i].Phase != i) issues.Add($"фаза {i + 1} не на своём месте");
            if (Steps[i].Lines.Count == 0) issues.Add($"фаза «{Steps[i].Title}» пуста");
        }
        var sandbox = Steps.FirstOrDefault(s => s.Phase == ReasoningPhase.Sandbox);
        if (sandbox is null || !sandbox.IsComputed || sandbox.Metrics.Count == 0) issues.Add("нет численной проверки в песочнице");
        else if (sandbox.Metrics.Any(m => !double.IsFinite(m.Before) || !double.IsFinite(m.After))) issues.Add("в песочнице нечисловые показатели");
        var decision = Steps.FirstOrDefault(s => s.Phase == ReasoningPhase.Decision);
        if (decision is null || Actions.Count == 0 && decision.Lines.Count == 0) issues.Add("нет итогового решения");
        return issues;
    }

    public string ToText(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.GetCultureInfo("ru-RU");
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        if (Task.Length > 0) sb.AppendLine("Задача: " + Task);
        foreach (var step in Steps)
        {
            sb.AppendLine();
            sb.AppendLine(step.Title + (step.Status == ReasoningStepStatus.Corrected ? " (скорректировано)" : step.Status == ReasoningStepStatus.Failed ? " (не пройдено)" : string.Empty));
            foreach (var line in step.Lines) sb.AppendLine("  • " + line);
            foreach (var m in step.Metrics) sb.AppendLine("  = " + m.ToText(culture));
        }
        if (Actions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Действия:");
            for (int i = 0; i < Actions.Count; i++) sb.AppendLine($"  {i + 1}) {Actions[i]}");
        }
        if (ModelNarrative.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Комментарий локальной модели{(ModelName.Length > 0 ? " (" + ModelName + ")" : string.Empty)}: {ModelNarrative}");
        }
        return sb.ToString().TrimEnd();
    }
}
