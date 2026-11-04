using System.Globalization;
using System.Text;

namespace AIArchitectStudio.Models;

/// <summary>Состояние вызова инструмента Инженерного агента.</summary>
public enum AgentToolStatus
{
    Pending,
    Succeeded,
    Skipped,
    Failed
}

/// <summary>
/// Вызов детерминированного инструмента программы Инженерным агентом (<see cref="Services.BimEngineeringAgentService"/>): имя
/// инструмента из реестра, аргументы, итог одной строкой, подробные строки и показатели, время выполнения.
/// </summary>
public sealed class AgentToolCall
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Инженерная подпись инструмента («Аудит расчётной схемы», «Подбор типоразмера ГОСТ»…).</summary>
    public string Title { get; set; } = string.Empty;

    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public AgentToolStatus Status { get; set; } = AgentToolStatus.Pending;
    public string Summary { get; set; } = string.Empty;
    public List<string> Details { get; } = [];

    /// <summary>Численные итоги инструмента для карточки (ΣF = ΣR, K_исп max, V_нетто…).</summary>
    public List<(string Name, string Value)> Results { get; } = [];

    /// <summary>Изменённые или созданные элементы (для выделения после выполнения).</summary>
    public List<string> AffectedIds { get; } = [];

    public double ElapsedMs { get; set; }

    /// <summary>Инструмент меняет модель (входит в транзакцию агента), иначе — только читает.</summary>
    public bool Mutates { get; set; }

    public string Glyph { get; set; } = "\uE90F";

    public string Argument(string key, string fallback = "") => Arguments.TryGetValue(key, out var v) ? v : fallback;

    public double? Number(string key) =>
        Arguments.TryGetValue(key, out var v) && double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public string ArgumentsText => Arguments.Count == 0 ? string.Empty : string.Join(", ", Arguments.Select(a => $"{a.Key} = {a.Value}"));

    public string StatusColorHex => Status switch
    {
        AgentToolStatus.Succeeded => "#30D158",
        AgentToolStatus.Failed => "#FF453A",
        AgentToolStatus.Skipped => "#8E8E93",
        _ => "#0A84FF"
    };
}

/// <summary>Итог задания Инженерного агента: цепочка рассуждений, вызовы инструментов, отчёт и шаг журнала.</summary>
public sealed class AgentRunResult
{
    public string Task { get; set; } = string.Empty;
    public ReasoningTrace Trace { get; set; } = new();
    public List<AgentToolCall> ToolCalls { get; } = [];
    public bool Success { get; set; }

    /// <summary>Модель изменилась: ровно один шаг журнала (отмена — одним Ctrl+Z).</summary>
    public bool Changed { get; set; }

    public string TransactionName { get; set; } = string.Empty;

    /// <summary>План инструментов составлен основной локальной моделью (иначе — разбором задачи ядром программы).</summary>
    public bool PlannedByModel { get; set; }

    public string PlannerName { get; set; } = string.Empty;
    public List<string> Report { get; } = [];
    public double ElapsedMs { get; set; }

    /// <summary>Отложенный шаг, требующий решения пользователя (окно подтверждения расценок).</summary>
    public object? PendingConfirmation { get; set; }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Инженерный агент · {Task}");
        foreach (var call in ToolCalls)
            sb.AppendLine($"  [{call.Status}] {call.Title} ({call.Name}): {call.Summary}");
        foreach (var line in Report) sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.Append(Trace.ToText());
        return sb.ToString();
    }
}
