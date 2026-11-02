using AIArchitectStudio.Models;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification.Verifiers;

/// <summary>
/// Несущая способность по КЭ-экспресс-расчёту (StructuralAnalysisService): элементы с K_исп &gt; 1.0 по прочности,
/// устойчивости, срезу, прогибу или грунту (продавливание — в <see cref="SeismicAndStabilityVerifier"/> как перегрузка узла).
/// Варианты подбираются моделированием на копии сцены до K_исп ≤ 0.95: утолщение плиты, балки по осям колонн + толщина
/// (Конструктор), промежуточные колонны (Архитектор), увеличение сечения колонн и высоты балок.
/// </summary>
public sealed class StructuralCapacityVerifier : IBimDesignVerifier
{
    public string Id => "KE";
    public string Title => "Несущая способность (КЭ-экспресс-расчёт)";
    public int Order => 10;

    /// <summary>K_исп без продавливания: прочность, устойчивость, срез, прогиб, грунт.</summary>
    public static double MemberK(ElementStructuralResult r) =>
        Math.Max(Math.Max(Math.Max(r.KStrength, r.KStability), Math.Max(r.KShear, r.KDeflection)), r.KSoil);

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        foreach (var (id, r) in ctx.Analysis.Elements.OrderByDescending(p => MemberK(p.Value)))
        {
            double k = MemberK(r);
            if (k <= 1.0) continue;
            if (ctx.Find(id) is not { } e) continue;
            var f = new VerificationFinding
            {
                Code = e.Type switch
                {
                    ElementTypes.Column or ElementTypes.Core => "KE-COL",
                    ElementTypes.Beam or ElementTypes.Truss => "KE-BEAM",
                    ElementTypes.Foundation => "KE-FND",
                    ElementTypes.Wall => "KE-WALL",
                    _ => "KE-SLAB"
                },
                Severity = VerificationSeverity.Critical,
                Category = "КЖ/КМ · прочность",
                AffectedElementIds = [id],
                Utilization = k,
                Location = [e.X, e.Y, e.Z],
                Extent = Math.Max(4, Math.Max(e.Width, e.Depth))
            };
            string governing = r.Governing;
            string span = r.Span > 0 ? $", {(r.IsCantilever ? "вылет" : "пролёт")} {M(r.Span)} м" : string.Empty;
            string deflection = r.DeflectionLimitMm > 0
                ? $", прогиб f/f_ult = {r.DeflectionMm.ToString("0", Ru)} / {r.DeflectionLimitMm.ToString("0", Ru)} мм = {K(r.DeflectionMm / r.DeflectionLimitMm)}"
                : string.Empty;
            f.Title = $"«{e.DisplayTitle}»: K_исп = {K(k)} ({governing}{span}{deflection})";
            f.DetailedExplanation = $"Экспресс-расчёт по СП 63.13330/СП 20.13330: определяющая проверка — {governing}; " +
                                    $"N = {r.AxialForce.ToString("N0", Ru)} кН, M = {r.Moment.ToString("N1", Ru)} кН·м, Q = {r.Shear.ToString("N1", Ru)} кН. " +
                                    "Элемент не обеспечивает несущую способность — нужно изменить сечение или расчётную схему.";
            switch (e.Type)
            {
                case ElementTypes.Slab or ElementTypes.CantileverSlab or ElementTypes.Roof or ElementTypes.Foundation:
                    f.ProposedFixOptions.AddRange(SlabOptions(ctx, e, r));
                    break;
                case ElementTypes.Beam or ElementTypes.Truss:
                    f.ProposedFixOptions.AddRange(BeamOptions(ctx, e, r));
                    break;
                case ElementTypes.Column:
                    f.ProposedFixOptions.AddRange(ColumnOptions(ctx, e));
                    break;
            }
            yield return f;
        }
    }

    // ───────────────────────────── Моделирование ─────────────────────────────

    /// <summary>K_исп цели и созданных элементов после применения действий на копии сцены.</summary>
    internal static (double K, FixOutcome Outcome) Simulate(VerificationContext ctx, IReadOnlyList<FixAction> actions, params string[] ids)
    {
        var sandbox = DesignFixEngine.Sandbox(ctx);
        var outcome = DesignFixEngine.Apply(sandbox, actions);
        var analysis = DesignFixEngine.Analyze(sandbox, ctx.Loads);
        double k = ids.Concat(outcome.CreatedIds).Select(analysis.For).Where(r => r is not null).Select(r => MemberK(r!)).DefaultIfEmpty(0).Max();
        return (k, outcome);
    }

    private static double Ceil(double v, double step) => Math.Ceiling(v / step - 1e-9) * step;

    private static IEnumerable<ProposedFixOption> SlabOptions(VerificationContext ctx, ArchitecturalElement slab, ElementStructuralResult r)
    {
        double t0 = slab.Height;
        bool foundation = slab.Type == ElementTypes.Foundation;

        // 1. Только толщина (автоматическое решение КБ): шаг 10 мм, не толще 450 мм (фундамент — 1200 мм).
        double max = foundation ? 1.2 : Math.Max(0.45, t0 + 0.05);
        for (double t = Ceil(t0 + 0.01, 0.01); t <= max + 1e-9; t += t < 0.3 ? 0.01 : 0.02)
        {
            var actions = new List<FixAction> { new(FixActionKind.ThickenSlab, slab.Id) { Value = Math.Round(t, 3) } };
            var (k, _) = Simulate(ctx, actions, slab.Id);
            if (k > DesignFixEngine.TargetUtilization) continue;
            yield return new ProposedFixOption
            {
                Author = FixAuthor.Automatic,
                Title = $"Утолщить {(foundation ? "фундаментную плиту" : "плиту")} с {Mm(t0)} до {Mm(t)} мм",
                Description = $"Минимальная толщина по расчёту: K_исп {K(r.Utilization)} → {K(k)}; план и отметки пола не меняются",
                Actions = actions,
                PredictedUtilization = k,
                Priority = 1,
                PreservesArchitecture = true
            };
            break;
        }
        if (foundation) yield break;

        // 2. Конструктор: балки по осям колонн + доводка толщины плиты и высоты балок.
        var supports = StructuralTopology.SupportsUnder(slab, ctx.Elements);
        var segments = StructuralTopology.GridSegments(slab, supports);
        if (segments.Count > 0)
        {
            double l = segments.Max(s => Math.Sqrt((s.A.X - s.B.X) * (s.A.X - s.B.X) + (s.A.Z - s.B.Z) * (s.A.Z - s.B.Z)));
            double h = Math.Max(0.4, Ceil(l / 12, 0.05)), b = Math.Max(0.25, Ceil(h * 0.45, 0.05)), t = t0;
            (double K, FixOutcome Outcome) best = (double.MaxValue, new FixOutcome());
            for (int i = 0; i < 24; i++)
            {
                var actions = BeamActions(slab, t, h, b, t0);
                var sandbox = DesignFixEngine.Sandbox(ctx);
                var outcome = DesignFixEngine.Apply(sandbox, actions);
                var analysis = DesignFixEngine.Analyze(sandbox, ctx.Loads);
                double ks = analysis.For(slab.Id) is { } rs ? MemberK(rs) : 0;
                double kb = outcome.CreatedIds.Select(analysis.For).Where(x => x is not null).Select(x => MemberK(x!)).DefaultIfEmpty(0).Max();
                best = (Math.Max(ks, kb), outcome);
                if (ks <= DesignFixEngine.TargetUtilization && kb <= DesignFixEngine.TargetUtilization)
                {
                    string thick = t > t0 + 1e-6 ? $"Увеличить толщину плиты с {Mm(t0)} до {Mm(t)} мм и добавить" : "Добавить";
                    yield return new ProposedFixOption
                    {
                        Author = FixAuthor.Structural,
                        Title = $"{thick} балки {Mm(b)} × {Mm(h)} мм по осям колонн",
                        Description = $"Балочная клетка сохраняет свободный план без новых колонн: {outcome.CreatedIds.Count} балок, K_исп {K(r.Utilization)} → {K(best.K)}",
                        Actions = actions,
                        PredictedUtilization = best.K,
                        Priority = 2,
                        PreservesArchitecture = true
                    };
                    break;
                }
                if (ks > DesignFixEngine.TargetUtilization && t < 0.35) t = Math.Round(t + 0.01, 3);
                else if (kb > DesignFixEngine.TargetUtilization && h < l / 6) { h = Math.Round(h + 0.05, 3); b = Math.Max(b, Ceil(h * 0.45, 0.05)); }
                else if (ks > DesignFixEngine.TargetUtilization) t = Math.Round(t + 0.02, 3);
                else break;
            }
        }

        // 3. Архитектор: промежуточные колонны в центрах пролётов (сквозные до фундамента) — меньше бетона в перекрытии.
        var (cw, cd, profile, material) = StructuralTopology.TypicalColumn(supports);
        for (int n = 1; n <= 8; n++)
        {
            var actions = new List<FixAction>
            {
                new(FixActionKind.AddIntermediateColumns, slab.Id) { Count = n, Value = cw, Value2 = cd, Profile = profile, Material = material }
            };
            var (k, outcome) = Simulate(ctx, actions, slab.Id);
            if (outcome.CreatedIds.Count == 0) break;
            if (k > DesignFixEngine.TargetUtilization) continue;
            yield return new ProposedFixOption
            {
                Author = FixAuthor.Architect,
                Title = $"Поставить промежуточные колонны {Mm(cw)} × {Mm(cd)} мм в центре пролёта ({n} шт. на этаже)",
                Description = $"Пролёт сокращается вдвое, плита остаётся {Mm(t0)} мм: K_исп {K(r.Utilization)} → {K(k)}; колонны сквозные до фундамента",
                Actions = actions,
                PredictedUtilization = k,
                Priority = 3
            };
            break;
        }
    }

    private static List<FixAction> BeamActions(ArchitecturalElement slab, double t, double h, double b, double t0)
    {
        var list = new List<FixAction>();
        if (t > t0 + 1e-6) list.Add(new FixAction(FixActionKind.ThickenSlab, slab.Id) { Value = Math.Round(t, 3) });
        list.Add(new FixAction(FixActionKind.AddBeamsUnderSlab, slab.Id) { Value = Math.Round(h, 3), Value2 = Math.Round(b, 3) });
        return list;
    }

    private static IEnumerable<ProposedFixOption> BeamOptions(VerificationContext ctx, ArchitecturalElement beam, ElementStructuralResult r)
    {
        double h0 = beam.Height;
        for (double h = Ceil(h0 + 0.05, 0.05); h <= Math.Max(h0 + 0.1, beam.Width / 5) + 1e-9; h += 0.05)
        {
            double b = Math.Max(beam.Depth, Ceil(h * 0.4, 0.05));
            var actions = new List<FixAction> { new(FixActionKind.DeepenBeam, beam.Id) { Value = Math.Round(h, 3), Value2 = Math.Round(b, 3) } };
            var (k, _) = Simulate(ctx, actions, beam.Id);
            if (k > DesignFixEngine.TargetUtilization) continue;
            yield return new ProposedFixOption
            {
                Author = FixAuthor.Structural,
                Title = $"Увеличить сечение балки до {Mm(b)} × {Mm(h)} мм",
                Description = $"Верх балки сохраняется, низ опускается на {Mm(h - h0)} мм: K_исп {K(r.Utilization)} → {K(k)}",
                Actions = actions,
                PredictedUtilization = k,
                Priority = 1
            };
            break;
        }
        var (cw, cd, profile, material) = StructuralTopology.TypicalColumn(ctx.Elements.Where(e => e.BuildingGroupId == beam.BuildingGroupId));
        var support = new List<FixAction> { new(FixActionKind.AddSupportColumnBelow, beam.Id) { Value = cw, Value2 = cd, Profile = profile, Material = material } };
        var (ks, outcome) = Simulate(ctx, support, beam.Id);
        if (outcome.CreatedIds.Count > 0 && ks <= DesignFixEngine.TargetUtilization)
            yield return new ProposedFixOption
            {
                Author = FixAuthor.Architect,
                Title = $"Поставить колонну {Mm(cw)} × {Mm(cd)} мм под серединой пролёта балки",
                Description = $"Пролёт балки сокращается вдвое без увеличения сечения: K_исп {K(r.Utilization)} → {K(ks)}",
                Actions = support,
                PredictedUtilization = ks,
                Priority = 2
            };
    }

    private static IEnumerable<ProposedFixOption> ColumnOptions(VerificationContext ctx, ArchitecturalElement column)
    {
        double s0 = Math.Max(column.Width, column.Depth);
        for (double s = Ceil(s0 + 0.05, 0.05); s <= 1.2 + 1e-9; s += 0.05)
        {
            double w = Math.Max(column.Width, s), d = Math.Max(column.Depth, s);
            var actions = new List<FixAction> { new(FixActionKind.EnlargeColumn, column.Id) { Value = w, Value2 = d } };
            var (k, _) = Simulate(ctx, actions, column.Id);
            if (k > DesignFixEngine.TargetUtilization) continue;
            yield return new ProposedFixOption
            {
                Author = FixAuthor.Structural,
                Title = $"Увеличить сечение колонны до {Mm(w)} × {Mm(d)} мм",
                Description = $"Колонна остаётся на своей оси: K_исп → {K(k)}",
                Actions = actions,
                PredictedUtilization = k,
                Priority = 1
            };
            break;
        }
    }
}
