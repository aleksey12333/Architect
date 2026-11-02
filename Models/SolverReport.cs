using System.Text;

namespace AIArchitectStudio.Models;

/// <summary>Отчёт C#-солвера о развёртке и проверке конструкций.</summary>
public sealed class SolverReport
{
    public bool Success { get; set; } = true;
    public double ElapsedMilliseconds { get; set; }
    public int InputElements { get; set; }
    public int GeneratedElements { get; set; }
    public int CorrectedElements { get; set; }
    public int HostedOpenings { get; set; }
    public int PhysicsErrors { get; set; }
    public string? ErrorCode { get; set; }
    public List<string> Actions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];

    public bool HasIssues => Warnings.Count > 0 || Errors.Count > 0 || PhysicsErrors > 0;

    public string Summary =>
        $"{(Success ? "OK" : "Сбой")} · {InputElements} → {GeneratedElements} эл. · {ElapsedMilliseconds:0.##} мс" +
        (HasIssues ? $" · замечаний: {Warnings.Count + Errors.Count + PhysicsErrors}" : string.Empty);

    public string DetailsText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Входных элементов: {InputElements}");
            sb.AppendLine($"Сгенерировано: {GeneratedElements}");
            sb.AppendLine($"Исправлено солвером: {CorrectedElements}");
            sb.AppendLine($"Привязано проёмов (HostElementId): {HostedOpenings}");
            sb.AppendLine($"Физических ошибок: {PhysicsErrors}");
            sb.AppendLine($"Время: {ElapsedMilliseconds:0.###} мс");
            foreach (var a in Actions) sb.AppendLine("• " + a);
            foreach (var w in Warnings) sb.AppendLine("Предупреждение: " + w);
            foreach (var e in Errors) sb.AppendLine("Ошибка: " + e);
            if (!string.IsNullOrEmpty(ErrorCode)) sb.AppendLine($"Код: {ErrorCode}");
            return sb.ToString().TrimEnd();
        }
    }
}
