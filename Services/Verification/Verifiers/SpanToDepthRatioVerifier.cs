using AIArchitectStudio.Models;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification.Verifiers;

/// <summary>
/// Геометрические пропорции КЖ/КМ: высота балки h ≥ L/15 (сталь — L/20), толщина плиты h ≥ L/30, консоль — h ≥ L/10
/// (вылет не больше 10 толщин), гибкость колонн l0/h ≤ 25 (ж/б) и λ ≤ 120 (сталь), минимум три опоры у плиты.
/// Пролёт берётся из экспресс-расчёта (поле «расстояние до опоры»), иначе — по длине элемента.
/// </summary>
public sealed class SpanToDepthRatioVerifier : IBimDesignVerifier
{
    public string Id => "SPAN";
    public string Title => "Пропорции пролёт / высота сечения";
    public int Order => 20;

    private static double Ceil(double v, double step) => Math.Ceiling(v / step - 1e-9) * step;

    private static bool IsSteel(ArchitecturalElement e) =>
        e.Material.Contains("steel", StringComparison.OrdinalIgnoreCase) || e.Material.Contains("стал", StringComparison.OrdinalIgnoreCase) ||
        e.Material.Contains("timber", StringComparison.OrdinalIgnoreCase) || e.Material.Contains("wood", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        foreach (var e in ctx.Elements)
        {
            if (e.RepeatLevels > 1 || !e.Position.Concat(e.Size).All(double.IsFinite)) continue;
            var r = ctx.Analysis.For(e.Id);
            if (StructuralTopology.IsLineMember(e) && e.Type == ElementTypes.Beam)
            {
                if (BeamRatio(ctx, e, r) is { } f) yield return f;
            }
            else if (StructuralTopology.IsSpanningSlab(e))
            {
                if (SlabRatio(ctx, e, r) is { } f) yield return f;
                if (SupportCount(ctx, e) is { } s) yield return s;
            }
            else if (e.Type == ElementTypes.Column && e.Polygon2D.Count < 3)
            {
                if (ColumnSlenderness(ctx, e, r) is { } f) yield return f;
            }
        }
    }

    private static VerificationFinding? BeamRatio(VerificationContext ctx, ArchitecturalElement beam, ElementStructuralResult? r)
    {
        double l = r is { Span: > 0 } ? r.Span : beam.Width;
        if (l < 2.5) return null;
        double ratio = IsSteel(beam) ? 20 : 15;
        double need = l / ratio;
        if (beam.Height >= need - 1e-3) return null;
        double h = Ceil(need, 0.05), b = Math.Max(beam.Depth, Ceil(h * 0.4, 0.05));
        var deepen = new List<FixAction> { new(FixActionKind.DeepenBeam, beam.Id) { Value = h, Value2 = b } };
        var f = new VerificationFinding
        {
            Code = "SPAN-BEAM",
            Severity = beam.Height < l / (ratio * 1.35) ? VerificationSeverity.Critical : VerificationSeverity.Warning,
            Category = "КЖ/КМ · пропорции",
            Title = $"Балка «{beam.DisplayTitle}»: h = {Mm(beam.Height)} мм < L/{ratio:0} = {Mm(need)} мм (L = {M(l)} м)",
            DetailedExplanation = $"Высота сечения меньше рекомендуемой для пролёта {M(l)} м — балка будет гибкой (прогибы, трещины, колебания). " +
                                  $"Рекомендуемое отношение пролёта к высоте для {(IsSteel(beam) ? "стальной" : "железобетонной")} балки — не более {ratio:0}.",
            AffectedElementIds = [beam.Id],
            Location = [beam.X, beam.Y, beam.Z],
            Extent = Math.Max(4, beam.Width),
            Utilization = r?.Utilization
        };
        var (k, _) = StructuralCapacityVerifier.Simulate(ctx, deepen, beam.Id);
        f.ProposedFixOptions.Add(new ProposedFixOption
        {
            Author = FixAuthor.Structural,
            Title = $"Увеличить сечение балки до {Mm(b)} × {Mm(h)} мм (L/{l / h:0})",
            Description = $"Верх балки сохраняется, низ опускается на {Mm(h - beam.Height)} мм; K_исп → {K(k)}",
            Actions = deepen,
            PredictedUtilization = k,
            Priority = 1
        });
        if (l > 6.5)
        {
            var (cw, cd, profile, material) = StructuralTopology.TypicalColumn(ctx.Elements.Where(e => e.BuildingGroupId == beam.BuildingGroupId));
            var column = new List<FixAction> { new(FixActionKind.AddSupportColumnBelow, beam.Id) { Value = cw, Value2 = cd, Profile = profile, Material = material } };
            var (kc, outcome) = StructuralCapacityVerifier.Simulate(ctx, column, beam.Id);
            if (outcome.CreatedIds.Count > 0)
                f.ProposedFixOptions.Add(new ProposedFixOption
                {
                    Author = FixAuthor.Architect,
                    Title = $"Колонна {Mm(cw)} × {Mm(cd)} мм под серединой балки — пролёт {M(l / 2)} м",
                    Description = $"Сечение балки сохраняется: L/h = {l / 2 / beam.Height:0}; K_исп → {K(kc)}",
                    Actions = column,
                    PredictedUtilization = kc,
                    Priority = 2
                });
        }
        return f;
    }

    private static VerificationFinding? SlabRatio(VerificationContext ctx, ArchitecturalElement slab, ElementStructuralResult? r)
    {
        if (r is not { Span: > 0 }) return null;
        double t = MaterialLayers.StructuralThickness(slab);
        bool cantilever = r.IsCantilever;
        double ratio = cantilever ? 10 : 30;
        double need = r.Span / ratio;
        if (t >= need - 1e-3) return null;
        double target = Ceil(need + (slab.Height - t), 0.01);
        var thicken = new List<FixAction> { new(FixActionKind.ThickenSlab, slab.Id) { Value = target } };
        var f = new VerificationFinding
        {
            Code = cantilever ? "SPAN-CANT" : "SPAN-SLAB",
            Severity = t < need / 1.35 ? VerificationSeverity.Critical : VerificationSeverity.Warning,
            Category = "КЖ/КМ · пропорции",
            Title = cantilever
                ? $"Консоль «{slab.DisplayTitle}»: вылет {M(r.Span)} м больше 10 толщин плиты ({Mm(t)} мм)"
                : $"Плита «{slab.DisplayTitle}»: h = {Mm(t)} мм < L/30 = {Mm(need)} мм (пролёт {M(r.Span)} м)",
            DetailedExplanation = cantilever
                ? $"Для консольной плиты толщина должна быть не меньше L/10 = {Mm(need)} мм — иначе недопустимый прогиб края и раскрытие трещин у заделки."
                : $"Для монолитной плиты пролётом {M(r.Span)} м рекомендуемая толщина — не меньше L/30 = {Mm(need)} мм (ограничение прогиба и колебаний).",
            AffectedElementIds = [slab.Id],
            Location = [slab.X, slab.Y, slab.Z],
            Extent = Math.Max(6, Math.Max(slab.Width, slab.Depth) * 0.6),
            Utilization = r.Utilization
        };
        var (k, _) = StructuralCapacityVerifier.Simulate(ctx, thicken, slab.Id);
        f.ProposedFixOptions.Add(new ProposedFixOption
        {
            Author = FixAuthor.Structural,
            Title = $"Утолщить плиту с {Mm(slab.Height)} до {Mm(target)} мм (L/{r.Span / (target - (slab.Height - t)):0})",
            Description = $"План не меняется, низ плиты на опорах; K_исп → {K(k)}",
            Actions = thicken,
            PredictedUtilization = k,
            Priority = 1,
            PreservesArchitecture = true
        });
        if (!cantilever)
        {
            var supports = StructuralTopology.SupportsUnder(slab, ctx.Elements);
            var (cw, cd, profile, material) = StructuralTopology.TypicalColumn(supports);
            for (int n = 1; n <= 6; n++)
            {
                var columns = new List<FixAction> { new(FixActionKind.AddIntermediateColumns, slab.Id) { Count = n, Value = cw, Value2 = cd, Profile = profile, Material = material } };
                var sandbox = DesignFixEngine.Sandbox(ctx);
                var outcome = DesignFixEngine.Apply(sandbox, columns);
                if (outcome.CreatedIds.Count == 0) break;
                var a = DesignFixEngine.Analyze(sandbox, ctx.Loads);
                if (a.For(slab.Id) is not { } rs || rs.Span / t > ratio) continue;
                f.ProposedFixOptions.Add(new ProposedFixOption
                {
                    Author = FixAuthor.Architect,
                    Title = $"Промежуточные колонны {Mm(cw)} × {Mm(cd)} мм ({n} шт.) — пролёт {M(rs.Span)} м",
                    Description = $"Толщина плиты {Mm(slab.Height)} мм сохраняется (L/{rs.Span / t:0}); K_исп → {K(StructuralCapacityVerifier.MemberK(rs))}",
                    Actions = columns,
                    PredictedUtilization = StructuralCapacityVerifier.MemberK(rs),
                    Priority = 2
                });
                break;
            }
        }
        return f;
    }

    private static VerificationFinding? SupportCount(VerificationContext ctx, ArchitecturalElement slab)
    {
        if (slab.BaseY < 0.3) return null;
        var supports = StructuralTopology.SupportsUnder(slab, ctx.Elements);
        bool line = supports.Any(s => s.Type is ElementTypes.Wall or ElementTypes.Core or ElementTypes.Beam or ElementTypes.Truss);
        int points = supports.Count(s => s.Type == ElementTypes.Column);
        if (line || points >= 3) return null;
        var (cw, cd, profile, material) = StructuralTopology.TypicalColumn(ctx.Elements.Where(e => e.BuildingGroupId == slab.BuildingGroupId));
        int add = Math.Max(1, 3 - points);
        var columns = new List<FixAction> { new(FixActionKind.AddIntermediateColumns, slab.Id) { Count = add, Value = cw, Value2 = cd, Profile = profile, Material = material } };
        return new VerificationFinding
        {
            Code = "SPAN-SUPPORTS",
            Severity = VerificationSeverity.Critical,
            Category = "КЖ/КМ · опирание",
            Title = $"Плита «{slab.DisplayTitle}» опирается на {points} колонн(ы) — нужно не меньше трёх опор",
            DetailedExplanation = "Плита на одной-двух точечных опорах геометрически изменяема (опрокидывание, кручение). Нужны минимум три колонны вне одной прямой или линейная опора (стена, балка).",
            AffectedElementIds = [slab.Id, .. supports.Select(s => s.Id)],
            Location = [slab.X, slab.Y, slab.Z],
            Extent = Math.Max(6, Math.Max(slab.Width, slab.Depth)),
            ProposedFixOptions =
            [
                new ProposedFixOption
                {
                    Author = FixAuthor.Architect,
                    Title = $"Добавить колонны {Mm(cw)} × {Mm(cd)} мм в наиболее удалённых от опор точках ({add} шт.)",
                    Description = "Колонны сквозные до фундамента; плита становится геометрически неизменяемой",
                    Actions = columns,
                    Priority = 1
                }
            ]
        };
    }

    private static VerificationFinding? ColumnSlenderness(VerificationContext ctx, ArchitecturalElement c, ElementStructuralResult? r)
    {
        double h = Math.Min(c.Width, c.Depth);
        if (h <= 0) return null;
        bool steel = IsSteel(c);
        double l0 = c.Height;
        if (steel)
        {
            double lambda = r?.Slenderness ?? 0;
            if (lambda <= 120) return null;
            return Slender(ctx, c, $"гибкость λ = {lambda:0} > 120", Math.Max(c.Width, Ceil(c.Width * lambda / 110, 0.025)), lambda > 150);
        }
        double ratio = l0 / h;
        if (ratio <= 25) return null;
        return Slender(ctx, c, $"l0/h = {ratio:0} > 25 (l0 = {M(l0)} м, h = {Mm(h)} мм)", Ceil(l0 / 25, 0.05), ratio > 32);
    }

    private static VerificationFinding Slender(VerificationContext ctx, ArchitecturalElement c, string text, double size, bool critical)
    {
        double w = Math.Max(c.Width, size), d = Math.Max(c.Depth, size);
        var actions = new List<FixAction> { new(FixActionKind.EnlargeColumn, c.Id) { Value = w, Value2 = d } };
        var (k, _) = StructuralCapacityVerifier.Simulate(ctx, actions, c.Id);
        return new VerificationFinding
        {
            Code = "SPAN-COL",
            Severity = critical ? VerificationSeverity.Critical : VerificationSeverity.Warning,
            Category = "КЖ/КМ · устойчивость",
            Title = $"Колонна «{c.DisplayTitle}»: {text}",
            DetailedExplanation = "Гибкая колонна теряет устойчивость раньше исчерпания прочности сечения (продольный изгиб, φ < 0.7). Увеличьте сечение или раскрепите колонну.",
            AffectedElementIds = [c.Id],
            Location = [c.X, c.Y, c.Z],
            Extent = Math.Max(3, c.Height),
            ProposedFixOptions =
            [
                new ProposedFixOption
                {
                    Author = FixAuthor.Structural,
                    Title = $"Увеличить сечение колонны до {Mm(w)} × {Mm(d)} мм",
                    Description = $"Гибкость в норме, ось колонны сохраняется; K_исп → {K(k)}",
                    Actions = actions,
                    PredictedUtilization = k,
                    Priority = 1
                }
            ]
        };
    }
}
