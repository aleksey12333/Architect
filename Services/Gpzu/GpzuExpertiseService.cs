using System.Globalization;
using System.Text;
using AIArchitectStudio.Models.Gpzu;
using AIArchitectStudio.Services.AI;

namespace AIArchitectStudio.Services.Gpzu;

/// <summary>Итог экспертизы ГПЗУ моделью: текст, модель, токены, время, признак офлайн-заглушки.</summary>
public sealed record GpzuExpertiseResult(string Text, string Model, long InputTokens, long OutputTokens, double Seconds, bool Offline, string? Error = null);

/// <summary>
/// «Экспертиза ГПЗУ через модель»: сводка документа (реквизиты, регламент, ЗОУИТ с ограничениями высоты, сети топосъёмки,
/// итоги детерминированной проверки посадки) уходит модели облачного сервиса (Sonnet 5.5 / GPT 5.6 Luna / Opus 5.5) с просьбой
/// перечислить риски и требования к проекту со ссылками на пункты ГПЗУ. Без ключа — офлайн-заключение по тем же данным
/// (детерминированное, без модели), чтобы студия и автотест работали без сети.
/// </summary>
public static class GpzuExpertiseService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public const string SystemPrompt =
        "Ты — главный архитектор проекта и эксперт по градостроительному регулированию РФ (ГрК РФ ст. 57.3, приказ Минстроя № 400/пр, " +
        "Воздушный кодекс ст. 47, ПП РФ № 160, СП 42.13330). По сводке ГПЗУ и результатам проверки посадки дай краткое заключение на русском: " +
        "1) что можно строить (ВРИ, лимиты подзоны), 2) ограничения высоты по приаэродромной территории с расчётом допустимой высоты над землёй, " +
        "3) охранные зоны сетей и что делать (вынос / ТУ), 4) риски и что запросить дополнительно. Ссылайся на разделы ГПЗУ. Не выдумывай цифр, " +
        "которых нет в сводке. Ответ — до 220 слов, маркированный список, без JSON.";

    public static string BuildPrompt(GpzuDocument doc, TopoSurvey? topo, GpzuComplianceReport? report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("СВОДКА ГПЗУ");
        sb.AppendLine($"№ {doc.Number} от {doc.IssueDate}; заявитель {doc.Applicant}; участок {doc.Cadastral}, {doc.Address}");
        sb.AppendLine(string.Create(Ru, $"Площадь {doc.DeclaredArea:N0} м² (по координатам {doc.GaussArea:N2} м²), точек границы {doc.Boundary.Count}, {doc.CoordinateSystem}"));
        var r = doc.Regulation;
        sb.AppendLine(string.Create(Ru, $"Зона {r.ZoneCode} {r.ZoneName}, подзона {r.Subzone}: жилых помещений ≤ {r.MaxLivingArea:N0} м², машино-мест ≥ {r.MinParkingPlaces}, мин. площадь участка {r.MinParcelArea:N0} м²; отступы, этажность и процент застройки не установлены."));
        if (r.PermittedUses.Count > 0) sb.AppendLine("Основные ВРИ: " + string.Join("; ", r.PermittedUses.Take(12)));
        sb.AppendLine("ЗОУИТ и подзоны ПАТ (раздел 5 и Приложение № 1):");
        foreach (var z in doc.Zones.Where(z => z.Kind is GpzuZoneKind.TransportZone or GpzuZoneKind.AirfieldSubzone))
            sb.AppendLine($"- {z.Name}{(z.HeightLimitMin.HasValue ? " — " + z.HeightText(Ru) : string.Empty)}{(z.Restriction.Length > 0 ? ": " + Trim(z.Restriction, 220) : string.Empty)}");
        if (doc.GroundElevation is { } g) sb.AppendLine(string.Create(Ru, $"Отметка земли по топосъёмке {g:0.00} м БС; минимальный потолок {doc.CeilingAbs:0.00} м БС."));
        if (topo is not null)
        {
            sb.AppendLine("Сети топосъёмки рядом с участком (коридоры охранных зон):");
            foreach (var z in doc.Zones.Where(z => z.IsNetwork))
                sb.AppendLine($"- {z.Name}; {z.Normative}");
        }
        sb.AppendLine($"ОКС на участке: {string.Join(", ", doc.Objects.Select(o => "№ " + o.Number + (o.Cadastral.Length > 0 ? " (" + o.Cadastral + ")" : string.Empty)))}");
        if (report is not null)
        {
            sb.AppendLine(string.Create(Ru, $"ПРОВЕРКА ПОСАДКИ: элементов {report.BuildingElements}, застройка {report.FootprintArea:N0} м² ({report.CoveragePercent:0.0} %), высота {report.MaxHeight:0.0} м, жилая площадь (оценка) {report.LivingAreaEstimate:N0} м²"));
            foreach (var f in report.Findings) sb.AppendLine($"- [{f.Severity}] {f.Title}: {f.Detail}");
        }
        return sb.ToString();
    }

    public static async Task<GpzuExpertiseResult> AskAsync(UnifiedCloudAiClient? client, string model, GpzuDocument doc, TopoSurvey? topo, GpzuComplianceReport? report,
        TimeSpan timeout, CancellationToken ct = default)
    {
        string prompt = BuildPrompt(doc, topo, report);
        if (client is null) return Offline(doc, report, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var result = await client.ChatAsync(new UnifiedChatRequest
            {
                Model = model,
                System = SystemPrompt,
                Messages = [UnifiedMessage.User(prompt)],
                MaxTokens = 1800,
                Temperature = 0.2,
                Purpose = "экспертиза ГПЗУ",
                ReasoningEffort = "low"
            }, cts.Token).ConfigureAwait(false);
            return new GpzuExpertiseResult(result.Text.Trim(), UnifiedCloudAiClient.SpecOf(result.ModelUsed)?.Title ?? result.ModelUsed, result.InputTokens, result.OutputTokens,
                result.Elapsed.TotalSeconds, false);
        }
        catch (Exception ex) when (ex is AiProviderException or OperationCanceledException or HttpRequestException)
        {
            string reason = ex is OperationCanceledException ? $"нет ответа за {timeout.TotalSeconds:0} с" : client.Redact(ex.Message);
            return Offline(doc, report, reason);
        }
    }

    /// <summary>Офлайн-заключение ядра (без модели) — по тем же данным, что получила бы модель.</summary>
    public static GpzuExpertiseResult Offline(GpzuDocument doc, GpzuComplianceReport? report, string? error)
    {
        var sb = new StringBuilder();
        var r = doc.Regulation;
        sb.AppendLine(string.Create(Ru, $"• Разрешено: {string.Join(", ", r.PermittedUses.Take(3))}…; подзона {r.Subzone} — жилых помещений ≤ {r.MaxLivingArea:N0} м², машино-мест ≥ {r.MinParkingPlaces} (раздел 2.3)."));
        if (doc.CeilingZone is { } cz)
        {
            string allowed = doc.GroundElevation is { } g ? string.Create(Ru, $"; при отметке земли {g:0.00} м БС допустимая высота ≈ {cz.HeightLimitMin - g:0.0} м") : string.Empty;
            sb.AppendLine($"• Высота ограничена {cz.Name}: {cz.HeightText(Ru)}{allowed} (раздел 5.1, ФАП-262).");
        }
        foreach (var z in doc.Zones.Where(z => z.IsNetwork).Take(4))
            sb.AppendLine($"• {z.Name}: застройка в коридоре — после выноса сети или по ТУ балансодержателя ({z.Normative}).");
        if (report is not null)
            foreach (var f in report.Findings.Where(f => f.Severity is GpzuSeverity.Critical or GpzuSeverity.Warning))
                sb.AppendLine($"• Риск: {f.Title} — {f.Detail}");
        sb.AppendLine("• Запросить: ТУ на подключение (водоканал, электросети, теплосети, ливнёвка — в ГПЗУ отсутствуют), выписки ЕГРН на ЗОУИТ 66:00-6.*, инженерные изыскания (п. 5.1).");
        return new GpzuExpertiseResult(sb.ToString().TrimEnd(), "ядро расчётов (офлайн)", 0, 0, 0, true, error);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
