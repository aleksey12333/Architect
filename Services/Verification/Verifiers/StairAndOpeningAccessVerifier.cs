using AIArchitectStudio.Models;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification.Verifiers;

/// <summary>
/// Проёмы и эвакуация (СП 1.13130): сквозное отверстие slab_opening в плите над каждым маршем, высота прохода в свету
/// над маршем ≥ 2.0 м, ширина марша ≥ 0.9 м, эвакуационные двери ≥ 0.8 × 1.9 м, окна и двери привязаны к стене
/// (HostElementId) и не выходят за её габарит. Коллизии марш/плита и марш/балка берутся из ClashDetectionService.
/// </summary>
public sealed class StairAndOpeningAccessVerifier : IBimDesignVerifier
{
    public string Id => "ACCESS";
    public string Title => "Лестницы, проёмы и эвакуация";
    public int Order => 40;

    public const double MinHeadroom = 2.0, MinStairWidth = 0.9, MinDoorWidth = 0.8, MinDoorHeight = 1.9;

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        foreach (var c in ctx.Clashes)
        {
            switch (c.Category)
            {
                case ClashCategory.StairsWithoutOpening when ctx.Find(c.ElementAId) is { } stairs && ctx.Find(c.ElementBId) is { } slab:
                    yield return new VerificationFinding
                    {
                        Code = "ACCESS-STAIR-OPENING",
                        Severity = VerificationSeverity.Critical,
                        Category = "Эвакуация · лестницы",
                        Title = $"Над маршем «{stairs.DisplayTitle}» нет проёма в плите «{slab.DisplayTitle}»",
                        DetailedExplanation = c.Description + ". Лестница упирается в перекрытие — путь эвакуации между этажами перекрыт.",
                        AffectedElementIds = [stairs.Id, slab.Id],
                        Location = c.LocationPoint,
                        Extent = c.Extent,
                        ProposedFixOptions =
                        [
                            new ProposedFixOption
                            {
                                Author = FixAuthor.Structural,
                                Title = "Вырезать проём в плите над маршем (+100 мм по контуру)",
                                Description = "Отверстие slab_opening по габариту марша в его осях; частичные отверстия объединяются в одно",
                                Actions = [new FixAction(FixActionKind.CreateStairsOpening, slab.Id) { SecondaryId = stairs.Id }],
                                Priority = 1
                            }
                        ]
                    };
                    break;
                case ClashCategory.StairsHeadroom when ctx.Find(c.ElementAId) is { } st && ctx.Find(c.ElementBId) is { } beam:
                    yield return new VerificationFinding
                    {
                        Code = "ACCESS-HEADROOM",
                        Severity = VerificationSeverity.Critical,
                        Category = "Эвакуация · лестницы",
                        Title = $"Над маршем «{st.DisplayTitle}» высота в свету меньше {M(MinHeadroom)} м (балка «{beam.DisplayTitle}»)",
                        DetailedExplanation = c.Description,
                        AffectedElementIds = [st.Id, beam.Id],
                        Location = c.LocationPoint,
                        Extent = c.Extent,
                        ProposedFixOptions =
                        [
                            new ProposedFixOption
                            {
                                Author = FixAuthor.Structural,
                                Title = "Перенести балку за пределы марша",
                                Description = "Балка смещается перпендикулярно своей оси до края лестничной клетки (+100 мм)",
                                Actions = [new FixAction(FixActionKind.ShiftBeamOffStairs, beam.Id) { SecondaryId = st.Id }],
                                Priority = 1
                            }
                        ]
                    };
                    break;
                case ClashCategory.WallOpeningOutsideWall when ctx.Find(c.ElementAId) is { } o && ctx.Find(c.ElementBId) is { } wall:
                    yield return new VerificationFinding
                    {
                        Code = "ACCESS-OPENING-OUT",
                        Severity = VerificationSeverity.Warning,
                        Category = "Проёмы",
                        Title = $"Проём «{o.DisplayTitle}» выходит за габарит стены «{wall.DisplayTitle}»",
                        DetailedExplanation = c.Description,
                        AffectedElementIds = [o.Id, wall.Id],
                        Location = c.LocationPoint,
                        Extent = c.Extent,
                        ProposedFixOptions =
                        [
                            new ProposedFixOption
                            {
                                Author = FixAuthor.Architect,
                                Title = "Вписать проём в стену",
                                Description = "Ширина и высота не больше стены, центр — в её пределах",
                                Actions = [new FixAction(FixActionKind.ClampWallOpening, o.Id) { SecondaryId = wall.Id }],
                                Priority = 1,
                                PreservesArchitecture = true
                            }
                        ]
                    };
                    break;
            }
        }

        foreach (var e in ctx.Elements)
        {
            if (e.Type == ElementTypes.Stairs && Math.Min(e.Width, e.Depth) < MinStairWidth - 1e-3)
                yield return new VerificationFinding
                {
                    Code = "ACCESS-STAIR-WIDTH",
                    Severity = VerificationSeverity.Warning,
                    Category = "Эвакуация · лестницы",
                    Title = $"Ширина марша «{e.DisplayTitle}» {Mm(Math.Min(e.Width, e.Depth))} мм < {Mm(MinStairWidth)} мм",
                    DetailedExplanation = "СП 1.13130.2020, п. 4.4.1: ширина марша лестницы на путях эвакуации — не менее 0.9 м (в многоквартирных домах — 1.05 м).",
                    AffectedElementIds = [e.Id],
                    Location = [e.X, e.Y, e.Z],
                    Extent = Math.Max(3, e.Depth)
                };

            if (!ElementTypes.IsHostedType(e.Type)) continue;
            if (e.HostElementId is null || ctx.Find(e.HostElementId) is not { } host || !ElementTypes.IsHostType(host.Type))
            {
                yield return new VerificationFinding
                {
                    Code = "ACCESS-ORPHAN",
                    Severity = VerificationSeverity.Critical,
                    Category = "Проёмы",
                    Title = $"{(e.Type == ElementTypes.Door ? "Дверь" : e.Type == ElementTypes.Window ? "Окно" : "Проём")} «{e.DisplayTitle}» не привязан(а) к стене",
                    DetailedExplanation = "У проёма нет стены-хоста (HostElementId): вырез не делается, при перемещении стены проём останется на месте, в IFC не будет IfcRelVoidsElement.",
                    AffectedElementIds = [e.Id],
                    Location = [e.X, e.Y, e.Z],
                    Extent = 3,
                    ProposedFixOptions =
                    [
                        new ProposedFixOption
                        {
                            Author = FixAuthor.Architect,
                            Title = "Привязать к ближайшей стене этажа",
                            Description = "Проём встаёт на ось стены, глубина выреза — толщина стены + 60 мм",
                            Actions = [new FixAction(FixActionKind.AttachToWall, e.Id)],
                            Priority = 1,
                            PreservesArchitecture = true
                        }
                    ]
                };
                continue;
            }
            if (e.Type == ElementTypes.Door && (e.Width < MinDoorWidth - 1e-3 || e.Height < MinDoorHeight - 1e-3))
            {
                double w = Math.Max(e.Width, 0.9), h = Math.Max(e.Height, 2.1);
                yield return new VerificationFinding
                {
                    Code = "ACCESS-DOOR",
                    Severity = VerificationSeverity.Warning,
                    Category = "Эвакуация · выходы",
                    Title = $"Дверь «{e.DisplayTitle}» {Mm(e.Width)} × {Mm(e.Height)} мм меньше эвакуационного выхода 800 × 1900 мм",
                    DetailedExplanation = "СП 1.13130.2020, п. 4.2.5: ширина эвакуационного выхода в свету — не менее 0.8 м, высота — не менее 1.9 м.",
                    AffectedElementIds = [e.Id, host.Id],
                    Location = [e.X, e.Y, e.Z],
                    Extent = 3,
                    ProposedFixOptions =
                    [
                        new ProposedFixOption
                        {
                            Author = FixAuthor.Architect,
                            Title = $"Расширить дверной проём до {Mm(w)} × {Mm(h)} мм",
                            Description = "Проём остаётся в своей стене, центр сдвигается только при упоре в торец",
                            Actions = [new FixAction(FixActionKind.ResizeOpening, e.Id) { Value = w, Value2 = h }],
                            Priority = 1,
                            PreservesArchitecture = true
                        }
                    ]
                };
            }
        }
    }
}
