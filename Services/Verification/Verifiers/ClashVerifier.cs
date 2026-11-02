using AIArchitectStudio.Models;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification.Verifiers;

/// <summary>
/// Геометрические коллизии (ClashDetectionService): отверстие плиты режет балку или зону опирания колонны, проём стены
/// врезается в колонну, дубликаты конструкций. Лестничные коллизии и проёмы вне стены — в
/// <see cref="StairAndOpeningAccessVerifier"/>. Варианты: сдвиг проёма (Архитектор) или сужение до свободного участка
/// стены (Конструктор: колонна несущая и не двигается).
/// </summary>
public sealed class ClashVerifier : IBimDesignVerifier
{
    public string Id => "CLASH";
    public string Title => "Коллизии конструкций (Clash Detection)";
    public int Order => 60;

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        foreach (var c in ctx.Clashes)
        {
            if (ctx.Find(c.ElementAId) is not { } a) continue;
            var b = ctx.Find(c.ElementBId);
            var f = new VerificationFinding
            {
                Severity = c.IsCritical ? VerificationSeverity.Critical : VerificationSeverity.Warning,
                Category = "Коллизии",
                Title = c.CategoryTitle + ": " + c.Description,
                DetailedExplanation = c.Description,
                AffectedElementIds = b is null ? [a.Id] : [a.Id, b.Id],
                Location = c.LocationPoint,
                Extent = c.Extent
            };
            switch (c.Category)
            {
                case ClashCategory.WallOpeningHitsColumn:
                    f.Code = "CLASH-OPENING-COLUMN";
                    f.Title = $"{(a.Type == ElementTypes.Window ? "Окно" : a.Type == ElementTypes.Door ? "Дверь" : "Проём")} «{a.DisplayTitle}» задевает колонну «{b?.DisplayTitle}»";
                    f.DetailedExplanation = c.Description + ". Проём не может проходить сквозь несущую колонну — нарушена её целостность.";
                    if (a.HostElementId is not null)
                    {
                        f.ProposedFixOptions.Add(new ProposedFixOption
                        {
                            Author = FixAuthor.Architect,
                            Title = $"Сдвинуть «{a.DisplayTitle}» вдоль стены на свободный участок",
                            Description = "Размер проёма сохраняется, ближайшая к исходной позиция без колонн и других проёмов",
                            Actions = [new FixAction(FixActionKind.MoveWallOpening, a.Id) { SecondaryId = b?.Id }],
                            Priority = 1,
                            PreservesArchitecture = true
                        });
                        var narrow = new List<FixAction> { new(FixActionKind.NarrowWallOpening, a.Id) };
                        var sandbox = DesignFixEngine.Sandbox(ctx);
                        var outcome = DesignFixEngine.Apply(sandbox, narrow);
                        if (outcome.Applied > 0 && sandbox.FindById(a.Id) is { } narrowed)
                            f.ProposedFixOptions.Add(new ProposedFixOption
                            {
                                Author = FixAuthor.Structural,
                                Title = $"Сузить «{a.DisplayTitle}» с {Mm(a.Width)} до {Mm(narrowed.Width)} мм у колонны",
                                Description = "Колонна несущая и остаётся на оси; внешний край проёма на месте, край у колонны отодвинут от её грани",
                                Actions = narrow,
                                Priority = 2
                            });
                    }
                    break;
                case ClashCategory.SlabOpeningCutsFrame:
                    f.Code = "CLASH-SLAB-OPENING";
                    f.ProposedFixOptions.Add(new ProposedFixOption
                    {
                        Author = FixAuthor.Structural,
                        Title = $"Сдвинуть отверстие «{a.DisplayTitle}» с балок и зон опирания колонн",
                        Description = "Минимальный сдвиг (8 направлений, шаг 50 мм) внутри плиты без наложения на другие отверстия",
                        Actions = [new FixAction(FixActionKind.MoveSlabOpening, a.Id) { SecondaryId = b?.Id }],
                        Priority = 1
                    });
                    break;
                case ClashCategory.Duplicate when b is not null:
                    f.Code = "CLASH-DUPLICATE";
                    f.ProposedFixOptions.Add(new ProposedFixOption
                    {
                        Author = FixAuthor.Automatic,
                        Title = $"Удалить дубликат «{b.DisplayTitle}»",
                        Description = $"Остаётся «{a.DisplayTitle}»",
                        Actions = [new FixAction(FixActionKind.RemoveElement, b.Id)],
                        Priority = 1,
                        PreservesArchitecture = true
                    });
                    break;
                default:
                    continue;
            }
            yield return f;
        }
    }
}

/// <summary>
/// Замечания C#-солвера (StructuralAuditor): недопустимые координаты, элементы ниже земли, тонкие стены, перекрывающиеся
/// проёмы, большие вылеты консолей, постройки без перекрытия. Висящие элементы и непривязанные проёмы проверяют
/// специализированные плагины. Аудит выполняется на копии (флаги HasPhysicsError живой сцены не меняются).
/// </summary>
public sealed class SolverAuditVerifier : IBimDesignVerifier
{
    public string Id => "AUDIT";
    public string Title => "Аудит C#-солвера (StructuralAuditor)";
    public int Order => 70;

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        var copy = ctx.Elements.Select(e => e.Clone()).ToList();
        var report = new SolverReport();
        StructuralAuditor.Audit(copy, report);
        foreach (var (text, severity) in report.Errors.Select(t => (t, VerificationSeverity.Critical)).Concat(report.Warnings.Select(t => (t, VerificationSeverity.Warning))))
        {
            if (text.Contains("висит в воздухе", StringComparison.Ordinal) || text.Contains("не привязан", StringComparison.Ordinal) ||
                text.Contains("без ядра", StringComparison.Ordinal)) continue;
            var ids = copy.Where(e => text.Contains($"«{e.DisplayTitle}»", StringComparison.Ordinal)).Select(e => e.Id).Take(4).ToList();
            var first = ids.Count > 0 ? ctx.Find(ids[0]) : null;
            yield return new VerificationFinding
            {
                Code = "AUDIT-" + (text.Contains("консол", StringComparison.Ordinal) ? "CANTILEVER" : text.Contains("гибкость", StringComparison.Ordinal) ? "SLENDER" :
                    text.Contains("перекрываются", StringComparison.Ordinal) ? "OVERLAP" : text.Contains("ниже уровня", StringComparison.Ordinal) ? "BELOW" : "MISC"),
                Severity = severity == VerificationSeverity.Critical ? VerificationSeverity.Critical : VerificationSeverity.Info,
                Category = "Аудит солвера",
                Title = text,
                DetailedExplanation = "Замечание StructuralAuditor после развёртки сцены C#-солвером.",
                AffectedElementIds = ids,
                Location = first is null ? null : [first.X, first.Y, first.Z],
                Extent = first is null ? 4 : Math.Max(3, Math.Max(first.Width, first.Depth))
            };
        }
    }
}

/// <summary>
/// Инсоляция окон по СанПиН 1.2.3685-21 (InsolationService) — только когда включена в контексте (аудит Совета):
/// окна с непрерывной инсоляцией ниже нормы. Вариант Архитектора — расширить проём в 1.4 раза в пределах стены.
/// </summary>
public sealed class InsolationVerifier : IBimDesignVerifier
{
    public const int MaxWindows = 40;

    public string Id => "SUN";
    public string Title => "Инсоляция окон (СанПиН 1.2.3685-21)";
    public int Order => 80;

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        if (!ctx.IncludeInsolation || ctx.Location is not { } location) yield break;
        var windows = ctx.Elements.Where(e => e.Type == ElementTypes.Window && e.HostElementId is not null).Take(MaxWindows).ToList();
        if (windows.Count == 0) yield break;
        var result = InsolationService.Analyze(ctx.Elements.ToList(), windows, location);
        foreach (var w in result.Windows.Where(w => !w.Passes))
        {
            if (ctx.Find(w.Window.Id) is not { } window) continue;
            double width = Math.Round(window.Width * 1.4, 2);
            yield return new VerificationFinding
            {
                Code = "SUN-WINDOW",
                Severity = VerificationSeverity.Warning,
                Category = "Инсоляция",
                Title = $"Окно «{window.DisplayTitle}»: непрерывная инсоляция {InsolationService.HoursText(w.ContinuousHours, true)} при норме {InsolationService.HoursText(result.Norm.RequiredHours, true)}",
                DetailedExplanation = $"{location.NameRu}, {result.Norm.ZoneTitle(true)}, расчётная дата {result.Norm.DateTitle(true)}: окно затенено или ориентировано на север.",
                AffectedElementIds = [window.Id],
                Location = [window.X, window.Y, window.Z],
                Extent = 4,
                ProposedFixOptions =
                [
                    new ProposedFixOption
                    {
                        Author = FixAuthor.Architect,
                        Title = $"Расширить окно до {Mm(width)} мм",
                        Description = "Больший световой проём увеличивает телесный угол неба и продолжительность облучения",
                        Actions = [new FixAction(FixActionKind.ResizeOpening, window.Id) { Value = width }],
                        Priority = 5,
                        PreservesArchitecture = true
                    }
                ]
            };
        }
    }
}
