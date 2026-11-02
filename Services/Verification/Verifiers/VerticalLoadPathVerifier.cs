using AIArchitectStudio.Models;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification.Verifiers;

/// <summary>
/// Непрерывность вертикальных несущих осей: колонны, стены и ядра верхних этажей должны стоять на нижележащей
/// вертикальной конструкции, балке-ригеле или (для колонн) на плите в зоне её опор. Проверяются: опора в пустоту,
/// опора на тонкую консоль (h &lt; 250 мм) или неармированную плиту без балки/капители и колонна над пролётом плиты
/// без колонны под ней. Варианты: стойка до фундамента (Конструктор), перенос на ось нижней колонны (Архитектор).
/// </summary>
public sealed class VerticalLoadPathVerifier : IBimDesignVerifier
{
    public string Id => "VLP";
    public string Title => "Непрерывность вертикальных несущих осей";
    public int Order => 30;

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        var all = ctx.Elements;
        foreach (var e in all)
        {
            if (!StructuralTopology.IsVerticalBearing(e) || e.RepeatLevels > 1 || e.BaseY < 0.3) continue;
            if (!e.Position.Concat(e.Size).All(double.IsFinite)) continue;

            // 1. Вертикальная опора или балка-ригель под элементом.
            if (HasVerticalSupport(e, all)) continue;
            if (HasBeamSupport(e, all)) continue;

            var surface = StructuralTopology.SurfaceBelow(e.X, e.Z, e.BaseY, all, tolerance: 0.12, exclude: e);
            bool onSurface = surface is not null && surface.TopY >= e.BaseY - 0.15;
            if (onSurface && surface!.Type is ElementTypes.Foundation or ElementTypes.Terrace) continue;

            if (!onSurface)
            {
                yield return Finding(ctx, e, "VLP-VOID", VerificationSeverity.Critical,
                    $"«{e.DisplayTitle}» висит в пустоте на отметке {M(e.BaseY)} м",
                    "Под элементом нет ни перекрытия, ни колонны, ни балки — вертикальная нагрузка не передаётся на фундамент. " +
                    "Это разрыв несущей оси: элемент нужно опереть или перенести на ось нижележащей конструкции.", null);
                continue;
            }

            var slab = surface!;
            var supports = StructuralTopology.SupportsUnder(slab, all);
            var hull = StructuralTopology.SupportHull(supports);
            bool outside = hull.Count < 3 || Geometry2D.DistanceToPolygon(hull, e.X, e.Z) > 0.3;
            bool cantilever = slab.Type == ElementTypes.CantileverSlab || outside;
            double t = MaterialLayers.StructuralThickness(slab);
            bool plain = !slab.Material.Contains("concrete", StringComparison.OrdinalIgnoreCase) && !slab.Layers.Any(l => l.IsReinforcedConcrete);
            if (cantilever && (t < 0.25 || plain))
            {
                yield return Finding(ctx, e, "VLP-CANT", VerificationSeverity.Critical,
                    $"«{e.DisplayTitle}» опирается на тонкую консоль «{slab.DisplayTitle}» ({Mm(t)} мм) без балки и капители",
                    $"Колонна стоит на консольной части плиты за пределами её опор: сосредоточенная сила на вылете {Mm(t)}-мм плиты без балки " +
                    "вызывает недопустимый момент в заделке и продавливание. Нужна стойка под колонной, балка-ригель или перенос колонны на ось.", slab);
                continue;
            }
            if (e.Type is ElementTypes.Column or ElementTypes.Core)
                yield return Finding(ctx, e, "VLP-SLAB", VerificationSeverity.Warning,
                    $"Колонна «{e.DisplayTitle}» стоит на пролёте плиты «{slab.DisplayTitle}» — под ней нет колонны",
                    "Нагрузка колонны передаётся через изгиб и продавливание плиты (смещение несущей оси). Допустимо только с балкой-ригелем " +
                    "или капителью; надёжнее продолжить колонну вниз до фундамента.", slab);
        }
    }

    private static bool HasVerticalSupport(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> all)
    {
        var outline = StructuralTopology.Outline(e);
        foreach (var v in all)
        {
            if (ReferenceEquals(v, e) || !StructuralTopology.IsVerticalBearing(v)) continue;
            double top = StructuralTopology.Top(v);
            if (top > e.BaseY + 0.05 || top < e.BaseY - StructuralTopology.SlabGap - 0.1 || v.BaseY >= e.BaseY - 0.3) continue;
            var vo = StructuralTopology.Outline(v);
            if (Geometry2D.IntersectionArea(outline, vo) > 1e-4 || Geometry2D.DistanceToPolygon(vo, e.X, e.Z) < 0.05) return true;
        }
        return false;
    }

    private static bool HasBeamSupport(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> all)
    {
        foreach (var b in all)
        {
            if (!StructuralTopology.IsLineMember(b)) continue;
            if (b.TopY > e.BaseY + 0.05 || b.TopY < e.BaseY - StructuralTopology.SlabGap) continue;
            if (Geometry2D.DistanceToPolygon(StructuralTopology.Outline(b), e.X, e.Z) < 0.05) return true;
        }
        return false;
    }

    private static VerificationFinding Finding(VerificationContext ctx, ArchitecturalElement e, string code, VerificationSeverity severity, string title, string text,
        ArchitecturalElement? slab)
    {
        var f = new VerificationFinding
        {
            Code = code,
            Severity = severity,
            Category = "КЖ/КМ · несущие оси",
            Title = title,
            DetailedExplanation = text,
            AffectedElementIds = slab is null ? [e.Id] : [e.Id, slab.Id],
            Location = [e.X, e.BaseY, e.Z],
            Extent = Math.Max(4, e.Height * 1.5),
            Utilization = ctx.Analysis.For(e.Id)?.Utilization
        };

        // Конструктор: непрерывная стойка до фундамента (или ригель со стойками под стеной).
        var support = new List<FixAction> { new(FixActionKind.AddSupportColumnBelow, e.Id) };
        var (k, outcome) = StructuralCapacityVerifier.Simulate(ctx, support, e.Id);
        if (outcome.CreatedIds.Count > 0)
            f.ProposedFixOptions.Add(new ProposedFixOption
            {
                Author = FixAuthor.Structural,
                Title = e.Type == ElementTypes.Wall
                    ? "Подвести ригель под стену и стойки до фундамента"
                    : $"Продолжить колонну вниз до фундамента ({outcome.CreatedIds.Count} яр.)",
                Description = $"Восстанавливается непрерывная вертикальная несущая ось: стойка того же сечения на всех нижних этажах; K_исп → {K(k)}",
                Actions = support,
                PredictedUtilization = k,
                Priority = 1
            });

        // Архитектор: перенос колонны на ось ближайшей нижележащей колонны (если место свободно).
        if (e.Type == ElementTypes.Column && NearestLowerColumn(ctx, e) is { } target)
        {
            double shift = Math.Sqrt((target.X - e.X) * (target.X - e.X) + (target.Z - e.Z) * (target.Z - e.Z));
            bool occupied = ctx.Elements.Any(o => o.Type == ElementTypes.Column && !ReferenceEquals(o, e) && Math.Abs(o.BaseY - e.BaseY) < 0.3 &&
                                                 Geometry2D.DistanceToPolygon(StructuralTopology.Outline(o), target.X, target.Z) < 0.05);
            if (occupied)
                f.ProposedFixOptions.Add(new ProposedFixOption
                {
                    Author = FixAuthor.Architect,
                    Title = "Удалить колонну: на оси нижней колонны уже есть стойка этого этажа",
                    Description = $"Колонна смещена с оси на {M(shift)} м и дублирует соседнюю — её нагрузку воспримет существующая стойка",
                    Actions = [new FixAction(FixActionKind.RemoveElement, e.Id)],
                    Priority = 3
                });
            else
                f.ProposedFixOptions.Add(new ProposedFixOption
                {
                    Author = FixAuthor.Architect,
                    Title = $"Перенести колонну на ось нижележащей колонны «{target.DisplayTitle}» (сдвиг {M(shift)} м)",
                    Description = "Несущая ось становится непрерывной без новых конструкций на нижних этажах",
                    Actions = [new FixAction(FixActionKind.MoveOntoSupport, e.Id) { X = target.X, Z = target.Z, SecondaryId = target.Id }],
                    Priority = 2,
                    PreservesArchitecture = shift < 1.0
                });
        }
        return f;
    }

    /// <summary>Ближайшая колонна нижнего этажа (верх под низом элемента), не дальше 3 м.</summary>
    private static ArchitecturalElement? NearestLowerColumn(VerificationContext ctx, ArchitecturalElement e) =>
        ctx.Elements.Where(v => v.Type == ElementTypes.Column && !ReferenceEquals(v, e) && v.RepeatLevels <= 1 &&
                                StructuralTopology.Top(v) <= e.BaseY + 0.05 && StructuralTopology.Top(v) >= e.BaseY - StructuralTopology.SlabGap - 0.1)
            .Select(v => (v, d: Math.Sqrt((v.X - e.X) * (v.X - e.X) + (v.Z - e.Z) * (v.Z - e.Z))))
            .Where(p => p.d < 3.0).OrderBy(p => p.d).Select(p => p.v).FirstOrDefault();
}
