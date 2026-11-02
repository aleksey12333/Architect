using System.Diagnostics;
using System.Reflection;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services.Verification;

/// <summary>
/// Расширяемый конвейер инженерной верификации. Плагины <see cref="IBimDesignVerifier"/> обнаруживаются в сборке
/// автоматически и запускаются по порядку на одном снимке сцены; экспресс-расчёт и коллизии считаются один раз и
/// разделяются всеми плагинами. Сбой отдельного плагина не прерывает конвейер — он попадает в отчёт как замечание.
/// Одинаковые замечания разных плагинов (тот же код и набор элементов) объединяются.
/// </summary>
public sealed class BimVerificationPipeline
{
    private readonly List<IBimDesignVerifier> _verifiers = [];

    public IReadOnlyList<IBimDesignVerifier> Verifiers => _verifiers;

    /// <summary>Конвейер со всеми плагинами сборки (классы с конструктором без параметров).</summary>
    public static BimVerificationPipeline CreateDefault()
    {
        var pipeline = new BimVerificationPipeline();
        foreach (var type in typeof(BimVerificationPipeline).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IBimDesignVerifier).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is null) continue;
            pipeline.Register((IBimDesignVerifier)Activator.CreateInstance(type)!);
        }
        return pipeline;
    }

    /// <summary>Подключает плагин (повторная регистрация того же Id заменяет прежний).</summary>
    public void Register(IBimDesignVerifier verifier)
    {
        _verifiers.RemoveAll(v => v.Id == verifier.Id);
        _verifiers.Add(verifier);
        _verifiers.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
    }

    public VerificationReport Run(VerificationContext context)
    {
        var total = Stopwatch.StartNew();
        var report = new VerificationReport();
        var seen = new Dictionary<string, VerificationFinding>(StringComparer.Ordinal);
        foreach (var verifier in _verifiers)
        {
            var sw = Stopwatch.StartNew();
            int count = 0;
            List<VerificationFinding> found;
            try
            {
                found = verifier.Verify(context).ToList();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Плагины могут быть внешними (Register): любой сбой одного проверщика — замечание, конвейер продолжает работу.
                found =
                [
                    new VerificationFinding
                    {
                        VerifierId = verifier.Id,
                        Code = verifier.Id + "-FAIL",
                        Severity = VerificationSeverity.Info,
                        Category = "Конвейер",
                        Title = $"Плагин «{verifier.Title}» завершился с ошибкой",
                        DetailedExplanation = ex.Message
                    }
                ];
            }
            foreach (var f in found)
            {
                if (string.IsNullOrEmpty(f.VerifierId)) f.VerifierId = verifier.Id;
                string key = f.Code + "|" + string.Join(",", f.AffectedElementIds.Order(StringComparer.Ordinal));
                if (seen.TryGetValue(key, out var existing))
                {
                    if (f.Severity > existing.Severity) existing.Severity = f.Severity;
                    continue;
                }
                for (int i = 0; i < f.ProposedFixOptions.Count; i++)
                    if (string.IsNullOrEmpty(f.ProposedFixOptions[i].Id)) f.ProposedFixOptions[i].Id = $"{f.Code}-{i + 1}";
                if (!f.ProposedFixOptions.Any(o => o.IsKeep)) f.ProposedFixOptions.Add(VerificationText.Keep());
                seen[key] = f;
                report.Findings.Add(f);
                count++;
            }
            sw.Stop();
            report.Timings.Add(new VerifierTiming(verifier.Id, verifier.Title, sw.Elapsed.TotalMilliseconds, count));
        }
        report.Findings.Sort((a, b) => b.Severity != a.Severity ? b.Severity.CompareTo(a.Severity) : string.CompareOrdinal(a.Code, b.Code));
        // Сбой расчёта или поиска коллизий уже отражён замечаниями плагинов (ленивое значение хранит исключение) —
        // отчёт выпускается с пустыми итогами, а не прерывает Совет.
        try
        {
            report.Analysis = context.Analysis;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report.Analysis = StructuralAnalysisResult.Empty;
        }
        try
        {
            report.ClashCount = context.Clashes.Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report.ClashCount = 0;
        }
        total.Stop();
        report.ElapsedMilliseconds = total.Elapsed.TotalMilliseconds;
        return report;
    }
}
