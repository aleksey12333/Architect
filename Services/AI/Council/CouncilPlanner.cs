using System.Globalization;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.Verification;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.AI;

/// <summary>Итог применения варианта к сцене (живой или песочнице).</summary>
public sealed class VariantOutcome
{
    public int Applied { get; set; }
    public int Added { get; set; }
    public int StabilizationPasses { get; set; }
    public List<string> Log { get; } = [];
    public List<string> Failed { get; } = [];
}

/// <summary>
/// Планировщик Совета КБ: из замечаний конвейера собирает глобальные варианты (позиция Конструктора, позиция Архитектора,
/// автоматическое решение КБ), моделирует каждый на копии сцены (K_исп, коллизии, бетон) и применяет выбранный со
/// стабилизацией: остаточные критичные замечания и коллизии исправляются автоматическими вариантами до K_исп ≤ 1.0.
/// </summary>
public static class CouncilPlanner
{
    private const int MaxStabilizationPasses = 3;

    /// <summary>Вариант исправления замечания, который выбирает данная позиция (Конструктор / Архитектор / КБ).</summary>
    public static ProposedFixOption? Pick(VerificationFinding finding, FixAuthor author)
    {
        var options = finding.ProposedFixOptions.Where(o => !o.IsKeep && o.Actions.Count > 0).ToList();
        if (options.Count == 0) return null;
        int Rank(ProposedFixOption o) => author switch
        {
            FixAuthor.Structural => o.Author == FixAuthor.Structural ? 0 : o.Author == FixAuthor.Automatic ? 1 : 2,
            FixAuthor.Architect => o.Author == FixAuthor.Architect ? 0 : o.Author == FixAuthor.Automatic ? 1 : 2,
            _ => 0
        };
        return options.OrderBy(Rank).ThenBy(o => o.Priority).First();
    }

    /// <summary>Замечания, которые закрывает стабилизация: все критичные и коллизии/проёмы с исправлением.</summary>
    private static bool NeedsStabilization(VerificationFinding f) =>
        f.HasFixes && (f.Severity == VerificationSeverity.Critical ||
                       f.Severity == VerificationSeverity.Warning && (f.Code.StartsWith("CLASH", StringComparison.Ordinal) || f.Code.StartsWith("ACCESS", StringComparison.Ordinal)));

    /// <summary>Глобальные варианты для карточки согласования с прогнозом по моделированию на песочнице.</summary>
    public static List<CouncilVariant> BuildVariants(SceneStateService scene, VerificationReport report, StructuralLoadSettings loads, BimVerificationPipeline pipeline)
    {
        var actionable = report.Actionable.ToList();
        var variants = new List<CouncilVariant>();
        if (actionable.Count == 0) return variants;
        double kBefore = report.MaxUtilization;
        double concreteBefore = DesignFixEngine.Concrete(scene);
        foreach (var author in new[] { FixAuthor.Structural, FixAuthor.Architect, FixAuthor.Automatic })
        {
            var v = new CouncilVariant { Author = author, KBefore = kBefore, ClashesBefore = report.ClashCount, ConcreteBefore = concreteBefore };
            foreach (var f in actionable)
                if (Pick(f, author) is { } option) v.Choices.Add((f, option));
            if (v.Choices.Count == 0 || variants.Any(o => o.Signature == v.Signature)) continue;
            var sandbox = DesignFixEngine.Sandbox(scene);
            int before = sandbox.Elements.Count;
            var outcome = ApplyVariant(sandbox, v.Choices, loads, CouncilModifiers.None, pipeline);
            var analysis = DesignFixEngine.Analyze(sandbox, loads);
            var after = pipeline.Run(new VerificationContext(sandbox.Elements.ToList(), sandbox.BuildingGroups.ToList(), sandbox.GridAxes.ToList(), loads));
            v.KAfter = analysis.MaxUtilization;
            v.ClashesAfter = ClashDetectionService.Detect(sandbox.Elements.ToList()).Count;
            v.CriticalAfter = after.CriticalCount;
            v.ConcreteAfter = DesignFixEngine.Concrete(sandbox);
            v.ElementsAdded = sandbox.Elements.Count - before;
            v.Log.AddRange(outcome.Log);
            variants.Add(v);
        }
        int n = 1;
        foreach (var v in variants)
        {
            v.Number = n++;
            v.Id = "v" + v.Number;
            string who = v.Author switch
            {
                FixAuthor.Structural => "Конструктор",
                FixAuthor.Architect => "Архитектор",
                _ => "КБ"
            };
            v.Title = v.Author == FixAuthor.Automatic
                ? $"Вариант {v.Number}: исправить все замечания автоматически на усмотрение КБ"
                : $"Вариант {v.Number} ({who}): {Lead(v)}";
            v.Description = Describe(v);
        }
        return variants;
    }

    /// <summary>Главное действие варианта: решение его автора по самому тяжёлому расчётному замечанию.</summary>
    public static string LeadOption(CouncilVariant v) =>
        v.Choices.OrderBy(c => c.Option.Author == v.Author ? 0 : 1)
            .ThenByDescending(c => c.Finding.Severity)
            .ThenBy(c => c.Finding.Code.StartsWith("KE", StringComparison.Ordinal) ? 0 : c.Finding.Code.StartsWith("SPAN", StringComparison.Ordinal) ? 1 : 2)
            .ThenByDescending(c => c.Finding.Utilization ?? 0)
            .Select(c => c.Option.Title).First();

    private static string Lead(CouncilVariant v)
    {
        string main = LeadOption(v);
        return v.Choices.Count > 1 ? $"{main} + ещё {v.Choices.Count - 1}" : main;
    }

    private static string Describe(CouncilVariant v)
    {
        string concrete = v.ConcreteAfter - v.ConcreteBefore is var d && Math.Abs(d) > 0.05
            ? $"бетон {(d > 0 ? "+" : "−")}{Math.Abs(d).ToString("0.0", Ru)} м³"
            : "расход бетона почти не меняется";
        bool keepsPlan = v.Choices.All(c => c.Option.PreservesArchitecture);
        string plan = v.Author == FixAuthor.Structural && keepsPlan ? " — сохранит свободный план" : v.Author == FixAuthor.Architect ? " — меньше вмешательства в перекрытия" : string.Empty;
        return string.Join("; ", v.Choices.Select(c => c.Option.Title).Distinct().Take(4)) + (v.Choices.Count > 4 ? "; …" : string.Empty) + $"{plan}; {concrete}";
    }

    /// <summary>Сравнение расхода бетона двух вариантов для реплики Архитектора («снизит расход бетона на 18 %»).</summary>
    public static string ConcreteComparison(CouncilVariant a, CouncilVariant b)
    {
        double da = a.ConcreteAfter - a.ConcreteBefore, db = b.ConcreteAfter - b.ConcreteBefore;
        if (da <= 0.05 || Math.Abs(da - db) < 0.05) return string.Empty;
        double pct = (da - db) / Math.Max(0.1, a.ConcreteAfter) * 100;
        return pct > 0.5 ? $"снизит расход бетона на {pct:0} % относительно варианта {a.Number}" : string.Empty;
    }

    /// <summary>
    /// Применяет выбранные исправления к сцене и стабилизирует модель: остаточные критичные замечания и коллизии
    /// закрываются автоматическими вариантами (до трёх проходов). Undo не записывается — транзакция у вызывающего.
    /// </summary>
    public static VariantOutcome ApplyVariant(SceneStateService scene, IReadOnlyList<(VerificationFinding Finding, ProposedFixOption Option)> choices,
        StructuralLoadSettings loads, CouncilModifiers modifiers, BimVerificationPipeline pipeline)
    {
        var outcome = new VariantOutcome();
        int before = scene.Elements.Count;
        var actions = choices.SelectMany(c => c.Option.Actions).Select(a => Modify(a, modifiers)).ToList();
        var first = DesignFixEngine.Apply(scene, actions);
        outcome.Applied += first.Applied;
        outcome.Log.AddRange(first.Log);
        outcome.Failed.AddRange(first.Failed);

        for (int pass = 0; pass < MaxStabilizationPasses; pass++)
        {
            var report = pipeline.Run(new VerificationContext(scene.Elements.Select(e => e.Clone()).ToList(), scene.BuildingGroups.ToList(), scene.GridAxes.ToList(), loads));
            var residual = report.Findings.Where(NeedsStabilization).ToList();
            if (residual.Count == 0) break;
            var fix = residual.Select(f => Pick(f, FixAuthor.Automatic)).OfType<ProposedFixOption>().SelectMany(o => o.Actions).Select(a => Modify(a, modifiers)).ToList();
            if (fix.Count == 0) break;
            var r = DesignFixEngine.Apply(scene, fix);
            outcome.StabilizationPasses++;
            outcome.Applied += r.Applied;
            outcome.Log.AddRange(r.Log.Select(l => "Стабилизация: " + l));
            if (r.Applied == 0) break;
        }
        outcome.Added = scene.Elements.Count - before;
        return outcome;
    }

    /// <summary>Уточнения пользователя к действиям: профиль и материал новых балок/колонн, минимальная толщина плиты.</summary>
    public static FixAction Modify(FixAction a, CouncilModifiers m)
    {
        if (m.IsEmpty) return a;
        return a.Kind switch
        {
            FixActionKind.AddBeamsUnderSlab => a with
            {
                Profile = m.BeamProfile ?? a.Profile,
                Material = m.BeamMaterial ?? (m.BeamProfile == ProfileTypes.IBeam ? "steel" : a.Material),
                Value = m.BeamHeightMm is { } h ? h / 1000 : a.Value,
                Value2 = m.BeamWidthMm is { } b ? b / 1000 : m.BeamProfile == ProfileTypes.IBeam ? Math.Min(a.Value2, 0.2) : a.Value2
            },
            FixActionKind.DeepenBeam when m.BeamHeightMm is { } bh => a with { Value = Math.Max(a.Value, bh / 1000) },
            FixActionKind.AddIntermediateColumns or FixActionKind.AddSupportColumnBelow => a with
            {
                Profile = m.ColumnProfile ?? a.Profile,
                Material = m.ColumnMaterial ?? a.Material,
                Value = m.ColumnSizeMm is { } s ? s / 1000 : a.Value,
                Value2 = m.ColumnSizeMm is { } s2 ? s2 / 1000 : a.Value2
            },
            FixActionKind.EnlargeColumn when m.ColumnSizeMm is { } cs => a with { Value = Math.Max(a.Value, cs / 1000), Value2 = Math.Max(a.Value2, cs / 1000) },
            FixActionKind.ThickenSlab when m.SlabThicknessMm is { } t => a with { Value = Math.Max(a.Value, t / 1000) },
            _ => a
        };
    }

    // ───────────────────────────── Ответ пользователя текстом ─────────────────────────────

    /// <summary>
    /// Разбор текстового ответа ГИПа без нейросети: «вариант 1/2/3», «оставить», «автоматически», уточнения
    /// «двутавр / сталь / круглые колонны / плита 250 мм / балки 300×600 / колонны 500». null — решение не распознано.
    /// </summary>
    public static CouncilDecision? Interpret(CouncilConsultation consultation, string text)
    {
        var t = (text ?? string.Empty).Trim().ToLowerInvariant().Replace('ё', 'е');
        if (t.Length == 0) return null;
        string? variant = null;
        var number = Regex.Match(t, @"(?:вариант|вар\.?|option|variant|v)\s*№?\s*(\d)");
        if (number.Success) variant = "v" + number.Groups[1].Value;
        else if (Regex.IsMatch(t, @"^\s*(\d)\b")) variant = "v" + Regex.Match(t, @"^\s*(\d)").Groups[1].Value;
        else if (Regex.IsMatch(t, @"остав|как есть|без изменен|не мен|keep|leave")) variant = CouncilDecision.Keep;
        else if (Regex.IsMatch(t, @"автомат|на усмотрение|сами|auto")) variant = consultation.Variants.FirstOrDefault(v => v.Author == FixAuthor.Automatic)?.Id;
        else if (Regex.IsMatch(t, @"конструктор|балк|утолщ")) variant = consultation.Variants.FirstOrDefault(v => v.Author == FixAuthor.Structural)?.Id;
        else if (Regex.IsMatch(t, @"архитектор|колонн")) variant = consultation.Variants.FirstOrDefault(v => v.Author == FixAuthor.Architect)?.Id;
        if (variant is null) return null;
        if (variant != CouncilDecision.Keep && consultation.Variants.All(v => v.Id != variant)) variant = consultation.Variants.LastOrDefault()?.Id;
        if (variant is null) return null;

        string? beamProfile = null, beamMaterial = null, columnProfile = null, columnMaterial = null;
        double? slab = null, beamH = null, beamB = null, colSize = null;
        bool beams = Regex.IsMatch(t, @"балк|ригел|beam");
        bool columns = Regex.IsMatch(t, @"колонн|стойк|column");
        bool steel = Regex.IsMatch(t, @"стал|металл|steel");
        bool concrete = Regex.IsMatch(t, @"ж/б|железобетон|монолит|бетон|concrete");
        if (Regex.IsMatch(t, @"двутавр|i-?beam|hea|heb|ipe")) { if (columns && !beams) columnProfile = ProfileTypes.IBeam; else beamProfile = ProfileTypes.IBeam; }
        if (Regex.IsMatch(t, @"труб|pipe")) { if (columns && !beams) columnProfile = ProfileTypes.Pipe; else beamProfile = ProfileTypes.Pipe; }
        if (Regex.IsMatch(t, @"кругл|round|circle")) columnProfile = ProfileTypes.Circle;
        if (steel) { if (beams || !columns) beamMaterial = "steel"; if (columns) columnMaterial = "steel"; }
        else if (concrete) { if (beams) beamMaterial = "concrete"; if (columns) columnMaterial = "concrete"; }

        var plate = Regex.Match(t, @"(?:плит\w*|перекрыт\w*|толщин\w*)[^\d]{0,24}(\d{2,3})\s*мм");
        if (plate.Success) slab = double.Parse(plate.Groups[1].Value, CultureInfo.InvariantCulture);
        var beamSize = Regex.Match(t, @"балк\w*[^\d]{0,20}(\d{3})\s*[xх×*]\s*(\d{3})");
        if (beamSize.Success)
        {
            double a = double.Parse(beamSize.Groups[1].Value, CultureInfo.InvariantCulture), b = double.Parse(beamSize.Groups[2].Value, CultureInfo.InvariantCulture);
            (beamB, beamH) = (Math.Min(a, b), Math.Max(a, b));
        }
        var colMatch = Regex.Match(t, @"колонн\w*[^\d]{0,20}(\d{3})(?:\s*[xх×*]\s*(\d{3}))?");
        if (colMatch.Success) colSize = double.Parse(colMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var modifiers = new CouncilModifiers(beamProfile, beamMaterial, columnProfile, columnMaterial, slab, beamH, beamB, colSize);
        return new CouncilDecision(variant, modifiers.Describe().Length == 0 ? CouncilModifiers.None : modifiers, text);
    }

    /// <summary>Решение из JSON-ответа Координатора (нейросеть) на текст ГИПа.</summary>
    public static CouncilDecision? FromReplyJson(CouncilConsultation consultation, string text, string json)
    {
        if (!AgentPrompts.TryReadObject(json, out var root)) return null;
        string? variant = AgentPrompts.ReadString(root, "variant")?.Trim().ToLowerInvariant();
        if (variant is null || (variant != CouncilDecision.Keep && consultation.Variants.All(v => v.Id != variant))) return null;
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) || s == "null" ? null : s;
        string? beamProfile = Clean(AgentPrompts.ReadString(root, "beamProfile"));
        string? columnProfile = Clean(AgentPrompts.ReadString(root, "columnProfile"));
        var modifiers = new CouncilModifiers(
            beamProfile is null ? null : ProfileTypes.Normalize(beamProfile), Clean(AgentPrompts.ReadString(root, "beamMaterial")),
            columnProfile is null ? null : ProfileTypes.Normalize(columnProfile), Clean(AgentPrompts.ReadString(root, "columnMaterial")),
            AgentPrompts.ReadNumber(root, "slabThicknessMm"), AgentPrompts.ReadNumber(root, "beamHeightMm"), null, AgentPrompts.ReadNumber(root, "columnSizeMm"));
        return new CouncilDecision(variant, modifiers.Describe().Length == 0 ? CouncilModifiers.None : modifiers, text, AgentPrompts.ReadString(root, "message"));
    }
}
