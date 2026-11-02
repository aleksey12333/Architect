using AIArchitectStudio.Models;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification.Verifiers;

/// <summary>
/// Пересечения несущих элементов с проёмами и друг с другом, которых нет в Clash Detection: балка проходит сквозь окно,
/// дверь или проём стены (низ балки ниже верха проёма), проём врезается в перекрытие над ним, колонны одного этажа
/// частично наложены (без признаков дубликата). Варианты: опустить верх проёма под балку/перекрытие (Архитектор),
/// уменьшить высоту балки до перемычки с проверкой K_исп моделированием (Конструктор), убрать меньшую из наложенных колонн.
/// </summary>
public sealed class MemberIntersectionVerifier : IBimDesignVerifier
{
    /// <summary>Минимальная высота проёма после понижения верха: окно 0.6 м, дверь и проём — 1.9 м (СП 1.13130).</summary>
    private const double MinWindowHeight = 0.6;
    private const double MinDoorHeight = 1.9;

    public string Id => "XSECT";
    public string Title => "Пересечения стен, колонн, проёмов и балок";
    public int Order => 55;

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        var elements = ctx.Elements.Where(e => e.RepeatLevels <= 1 && e.Position.Concat(e.Size).All(double.IsFinite)).ToList();
        var openings = elements.Where(e => ElementTypes.IsHostedType(e.Type)).ToList();
        var beams = elements.Where(StructuralTopology.IsLineMember).ToList();
        var slabs = elements.Where(e => e.Type is ElementTypes.Slab or ElementTypes.CantileverSlab || e.Type == ElementTypes.Roof && e.RoofShape == RoofShapes.Flat).ToList();
        var columns = elements.Where(e => e.Type == ElementTypes.Column).ToList();
        var outlines = new Dictionary<string, List<(double X, double Z)>>(StringComparer.Ordinal);
        List<(double X, double Z)> Outline(ArchitecturalElement e) =>
            outlines.TryGetValue(e.Id, out var o) ? o : outlines[e.Id] = StructuralTopology.Outline(e);

        foreach (var o in openings)
        {
            // 1. Балка сквозь проём: перекрытие в плане и низ балки ниже верха проёма.
            foreach (var b in beams)
            {
                if (b.BaseY >= o.TopY - 0.02 || b.TopY <= o.BaseY + 0.02) continue;
                if (Geometry2D.IntersectionArea(Outline(o), Outline(b)) < 0.005) continue;
                yield return BeamThroughOpening(ctx, o, b);
                break;
            }

            // 2. Проём врезается в перекрытие над ним (верх проёма выше низа плиты).
            foreach (var s in slabs)
            {
                if (s.BaseY >= o.TopY - 0.02 || s.BaseY <= o.BaseY + 0.3 || s.TopY <= o.BaseY) continue;
                if (Geometry2D.IntersectionArea(Outline(o), Outline(s)) < 0.005) continue;
                yield return OpeningIntoSlab(o, s);
                break;
            }
        }

        // 3. Частично наложенные колонны одного яруса (дубликаты > 35 % объёма — в ClashVerifier).
        var duplicates = ctx.Clashes.Where(c => c.Category == ClashCategory.Duplicate)
            .Select(c => string.CompareOrdinal(c.ElementAId, c.ElementBId) < 0 ? (c.ElementAId, c.ElementBId) : (c.ElementBId, c.ElementAId)).ToHashSet();
        for (int i = 0; i < columns.Count; i++)
            for (int j = i + 1; j < columns.Count; j++)
            {
                var a = columns[i];
                var c = columns[j];
                if (Math.Min(a.TopY, c.TopY) - Math.Max(a.BaseY, c.BaseY) < 0.1) continue;
                double area = Geometry2D.IntersectionArea(Outline(a), Outline(c));
                if (area < 0.002) continue;
                var key = string.CompareOrdinal(a.Id, c.Id) < 0 ? (a.Id, c.Id) : (c.Id, a.Id);
                if (duplicates.Contains(key)) continue;
                var smaller = a.Width * a.Depth <= c.Width * c.Depth ? a : c;
                var larger = ReferenceEquals(smaller, a) ? c : a;
                yield return new VerificationFinding
                {
                    Code = "XSECT-COLUMNS",
                    Severity = VerificationSeverity.Warning,
                    Category = "Коллизии",
                    Title = $"Колонны «{a.DisplayTitle}» и «{c.DisplayTitle}» наложены друг на друга ({area.ToString("0.000", Ru)} м²)",
                    DetailedExplanation = "Два несущих элемента занимают одно место в плане: нагрузка распределяется неопределённо, бетон учитывается дважды. " +
                                          "Обычно это ошибка расстановки по осям — одну из колонн нужно убрать или сдвинуть.",
                    AffectedElementIds = [smaller.Id, larger.Id],
                    Location = [smaller.X, smaller.Y, smaller.Z],
                    Extent = 3,
                    ProposedFixOptions =
                    [
                        new ProposedFixOption
                        {
                            Author = FixAuthor.Automatic,
                            Title = $"Удалить меньшую колонну «{smaller.DisplayTitle}»",
                            Description = $"Остаётся «{larger.DisplayTitle}» {Mm(larger.Width)} × {Mm(larger.Depth)} мм",
                            Actions = [new FixAction(FixActionKind.RemoveElement, smaller.Id)],
                            Priority = 1,
                            PreservesArchitecture = true
                        }
                    ]
                };
            }
    }

    private static string Kind(ArchitecturalElement o) => o.Type switch
    {
        ElementTypes.Window => "Окно",
        ElementTypes.Door => "Дверь",
        _ => "Проём"
    };

    private static double MinHeight(ArchitecturalElement o) => o.Type == ElementTypes.Window ? MinWindowHeight : MinDoorHeight;

    private static VerificationFinding BeamThroughOpening(VerificationContext ctx, ArchitecturalElement o, ArchitecturalElement b)
    {
        double overlap = o.TopY - b.BaseY;
        var f = new VerificationFinding
        {
            Code = "XSECT-BEAM-OPENING",
            Severity = VerificationSeverity.Critical,
            Category = "Коллизии",
            Title = $"Балка «{b.DisplayTitle}» проходит сквозь {Kind(o).ToLowerInvariant()} «{o.DisplayTitle}» (на {Mm(overlap)} мм ниже верха проёма)",
            DetailedExplanation = $"Низ балки на отметке {M(b.BaseY)} м, верх проёма — {M(o.TopY)} м: балка перекрывает световой проём или проход, " +
                                  "столярный блок не может быть установлен. Нужно опустить верх проёма под балку (перемычка) или уменьшить высоту балки.",
            AffectedElementIds = [o.Id, b.Id],
            Location = [o.X, o.TopY, o.Z],
            Extent = Math.Max(3, o.Width * 2)
        };
        double height = Math.Round(b.BaseY - 0.05 - o.BaseY, 2);
        if (height >= MinHeight(o) && o.HostElementId is not null)
            f.ProposedFixOptions.Add(new ProposedFixOption
            {
                Author = FixAuthor.Architect,
                Title = $"Опустить верх «{o.DisplayTitle}» под балку: высота {Mm(o.Height)} → {Mm(height)} мм",
                Description = "Низ проёма и ширина сохраняются, над проёмом остаётся перемычка 50 мм до низа балки",
                Actions = [new FixAction(FixActionKind.ResizeOpening, o.Id) { Value2 = height }],
                Priority = 1,
                PreservesArchitecture = true
            });
        double beamHeight = Math.Round(b.TopY - (o.TopY + 0.05), 2);
        if (beamHeight >= Math.Max(0.25, b.Width / 20))
        {
            var actions = new List<FixAction> { new(FixActionKind.DeepenBeam, b.Id) { Value = beamHeight, Value2 = b.Depth } };
            var (k, _) = StructuralCapacityVerifier.Simulate(ctx, actions, b.Id);
            if (k <= DesignFixEngine.TargetUtilization)
                f.ProposedFixOptions.Add(new ProposedFixOption
                {
                    Author = FixAuthor.Structural,
                    Title = $"Уменьшить высоту балки «{b.DisplayTitle}» до {Mm(beamHeight)} мм",
                    Description = $"Верх балки сохраняется, низ поднимается над проёмом на 50 мм; несущая способность проверена моделированием: K_исп → {K(k)}",
                    Actions = actions,
                    PredictedUtilization = k,
                    Priority = 2
                });
        }
        return f;
    }

    private static VerificationFinding OpeningIntoSlab(ArchitecturalElement o, ArchitecturalElement s)
    {
        double height = Math.Round(s.BaseY - 0.05 - o.BaseY, 2);
        var f = new VerificationFinding
        {
            Code = "XSECT-OPENING-SLAB",
            Severity = VerificationSeverity.Critical,
            Category = "Коллизии",
            Title = $"{Kind(o)} «{o.DisplayTitle}» врезается в перекрытие «{s.DisplayTitle}» на {Mm(o.TopY - s.BaseY)} мм",
            DetailedExplanation = $"Верх проёма {M(o.TopY)} м выше низа плиты {M(s.BaseY)} м: проём прорезает перекрытие, а над ним нет перемычки.",
            AffectedElementIds = [o.Id, s.Id],
            Location = [o.X, o.TopY, o.Z],
            Extent = Math.Max(3, o.Width * 2)
        };
        if (height >= MinHeight(o) && o.HostElementId is not null)
            f.ProposedFixOptions.Add(new ProposedFixOption
            {
                Author = FixAuthor.Architect,
                Title = $"Опустить верх «{o.DisplayTitle}» под перекрытие: высота {Mm(o.Height)} → {Mm(height)} мм",
                Description = "Низ проёма и ширина сохраняются, перемычка 50 мм под плитой",
                Actions = [new FixAction(FixActionKind.ResizeOpening, o.Id) { Value2 = height }],
                Priority = 1,
                PreservesArchitecture = true
            });
        return f;
    }
}
