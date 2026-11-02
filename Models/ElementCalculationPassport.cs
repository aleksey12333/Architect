using System.Globalization;
using System.Text;

namespace AIArchitectStudio.Models;

/// <summary>Нормативный документ и пункт, по которому выполнена проверка или взята величина.</summary>
public sealed record NormReference(string Document, string Title, string Clause, string Subject);

/// <summary>Характеристика материала: обозначение, значение, единица и источник (таблица СП или сертификат).</summary>
public sealed record MaterialProperty(string Symbol, string Title, double Value, string Unit, string Source);

/// <summary>Составляющая грузовой цепочки: откуда пришла сила (собственный вес, плита по грузовой площади, этажи выше).</summary>
public sealed record LoadContribution(string Title, double Value, string Unit, string Detail, string Clause);

/// <summary>
/// Шаг расчёта: формула с пунктом СП, подстановка чисел элемента и результат. У проверки (<see cref="IsCheck"/>) значение — K,
/// пересчитанный по формуле паспорта, <see cref="SolverValue"/> — K экспресс-расчёта; <see cref="Verified"/> — совпали до бита.
/// </summary>
public sealed record CalculationStep(
    string Title, string Clause, string Formula, string Substitution, double Value, string Unit,
    bool IsCheck = false, bool Verified = false, double SolverValue = double.NaN, bool Recomputed = true);

/// <summary>
/// Паспорт расчёта несущего элемента: сводка и вердикт, нормативная база, паспорт материала с источником характеристик,
/// сбор нагрузок и граничные условия по расчётной схеме, пошаговый расчёт с формулами СП и подстановкой. Строится по запросу
/// (<c>CalculationJustificationService</c>) из расчётной схемы и результата расчёта — не в цикле расчёта.
/// </summary>
public sealed class ElementCalculationPassport
{
    public string ElementId { get; init; } = string.Empty;
    public string ElementTitle { get; init; } = string.Empty;
    public string CategoryTitle { get; init; } = string.Empty;
    public BimCategory Category { get; init; }
    public int Floor { get; init; }
    public string SectionText { get; init; } = string.Empty;

    /// <summary>K_исп экспресс-расчёта (StructuralAnalysisService).</summary>
    public double Utilization { get; init; }

    /// <summary>K_исп, собранный из шагов паспорта (пересчитанные проверки и принятые по схеме).</summary>
    public double RecomputedUtilization { get; init; }

    /// <summary>Паспорт воспроизводит K_исп расчёта до бита.</summary>
    public bool MatchesSolver => BitConverter.DoubleToInt64Bits(RecomputedUtilization) == BitConverter.DoubleToInt64Bits(Utilization);

    public string Governing { get; init; } = string.Empty;
    public bool IsSafe => Utilization <= 1.0;
    public double GammaN { get; init; } = 1;

    public string MaterialTitle { get; init; } = string.Empty;

    /// <summary>Таблицы СП или пользовательский сертификат материала.</summary>
    public bool MaterialFromCertificate { get; init; }
    public string MaterialSource { get; init; } = string.Empty;

    public List<MaterialProperty> MaterialProperties { get; } = [];
    public List<NormReference> Norms { get; } = [];
    public List<LoadContribution> Loads { get; } = [];
    public List<string> Boundary { get; } = [];
    public List<CalculationStep> Steps { get; } = [];
    public List<string> Notes { get; } = [];

    public IEnumerable<CalculationStep> Checks => Steps.Where(s => s.IsCheck);
    public int VerifiedChecks => Steps.Count(s => s.IsCheck && s.Verified);
    public int RecomputedChecks => Steps.Count(s => s.IsCheck && s.Recomputed);

    public string Verdict => IsSafe ? "Несущая способность обеспечена" : "Перегрузка — несущая способность не обеспечена";

    /// <summary>Расчётная записка для пояснительной записки ПД (простой текст с нумерацией разделов).</summary>
    public string ToReportText(CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.GetCultureInfo("ru-RU");
        var sb = new StringBuilder();
        sb.AppendLine("РАСЧЁТНОЕ ОБОСНОВАНИЕ НЕСУЩЕЙ СПОСОБНОСТИ ЭЛЕМЕНТА");
        sb.AppendLine($"Элемент: {CategoryTitle} «{ElementTitle}» ({ElementId}){(Floor > 0 ? $", этаж +{Floor}" : string.Empty)}");
        if (SectionText.Length > 0) sb.AppendLine($"Сечение: {SectionText}");
        sb.AppendLine($"Результат: K_исп = {Utilization.ToString("0.000", c)} {(IsSafe ? "≤" : ">")} 1,0 — {Verdict.ToLowerInvariant()}. Определяющая проверка: {Governing}.");
        if (GammaN != 1) sb.AppendLine($"Коэффициент надёжности по ответственности γn = {GammaN.ToString("0.0", c)} (ГОСТ 27751-2014, п. 10.1).");
        sb.AppendLine();
        sb.AppendLine("1. Нормативная база");
        foreach (var n in Norms) sb.AppendLine($"   {n.Document} «{n.Title}», {n.Clause} — {n.Subject}");
        sb.AppendLine();
        sb.AppendLine($"2. Материал: {MaterialTitle}");
        sb.AppendLine($"   Источник характеристик: {MaterialSource}");
        foreach (var p in MaterialProperties) sb.AppendLine($"   {p.Symbol} = {Format(p.Value, c)} {p.Unit} — {p.Title} ({p.Source})");
        sb.AppendLine();
        sb.AppendLine("3. Сбор нагрузок и граничные условия");
        foreach (var l in Loads) sb.AppendLine($"   {l.Title}: {Format(l.Value, c)} {l.Unit}{(l.Detail.Length > 0 ? " — " + l.Detail : string.Empty)}{(l.Clause.Length > 0 ? $" [{l.Clause}]" : string.Empty)}");
        foreach (var b in Boundary) sb.AppendLine($"   {b}");
        sb.AppendLine();
        sb.AppendLine("4. Расчёт");
        int k = 1;
        foreach (var s in Steps)
        {
            sb.AppendLine($"   4.{k++}. {s.Title}{(s.Clause.Length > 0 ? $" ({s.Clause})" : string.Empty)}");
            if (s.Formula.Length > 0) sb.AppendLine($"        {s.Formula}");
            if (s.Substitution.Length > 0) sb.AppendLine($"        {s.Substitution}");
            if (s.IsCheck) sb.AppendLine($"        K = {s.Value.ToString("0.000", c)}{(s.Recomputed ? s.Verified ? " (пересчёт совпал с расчётной схемой)" : $" (по схеме {s.SolverValue.ToString("0.000", c)})" : " (по результату расчётной схемы)")}");
        }
        if (Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Примечания");
            foreach (var n in Notes) sb.AppendLine("   " + n);
        }
        return sb.ToString();
    }

    public static string Format(double value, CultureInfo c)
    {
        double a = Math.Abs(value);
        return a == 0 ? "0" : a >= 1000 ? value.ToString("N0", c) : a >= 10 ? value.ToString("0.0", c) : a >= 0.01 ? value.ToString("0.###", c)
            : a >= 1e-5 ? value.ToString("0.#####", c) : value.ToString("0.###E+0", c);
    }
}
