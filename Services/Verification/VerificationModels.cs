using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services.Verification;

/// <summary>Важность замечания проверщика.</summary>
public enum VerificationSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>
/// Автор варианта исправления: позиция Главного конструктора (прочность), Главного архитектора (сохранение замысла),
/// автоматическое решение КБ или «оставить как есть».
/// </summary>
public enum FixAuthor
{
    Structural,
    Architect,
    Automatic,
    Keep
}

/// <summary>Элементарное действие исправления, которое DesignFixEngine применяет к сцене (по Id элементов).</summary>
public enum FixActionKind
{
    /// <summary>Утолщить плиту до Value, м (низ сохраняется, отверстия режут новую толщину).</summary>
    ThickenSlab,

    /// <summary>Балки Value2 × Value (ширина × высота, м) по линиям опорных колонн под плитой TargetId.</summary>
    AddBeamsUnderSlab,

    /// <summary>Промежуточные колонны под плитой TargetId (не больше Count) в точках наибольшего удаления от опор — сквозные до фундамента.</summary>
    AddIntermediateColumns,

    /// <summary>Сечение колонны W × D = Value × Value2, м.</summary>
    EnlargeColumn,

    /// <summary>Высота сечения балки Value, м (верх сохраняется).</summary>
    DeepenBeam,

    /// <summary>Колонна под элементом TargetId до ближайшей опоры (через все перекрытия — до фундамента).</summary>
    AddSupportColumnBelow,

    /// <summary>Перенос колонны на ось нижележащей колонны (точка X, Z).</summary>
    MoveOntoSupport,

    /// <summary>Сдвиг проёма стены вдоль стены от колонны (автоисправление коллизии).</summary>
    MoveWallOpening,

    /// <summary>Сужение проёма до свободного участка стены между колоннами.</summary>
    NarrowWallOpening,

    /// <summary>Ширина (Value) и высота (Value2) проёма в пределах стены, м; 0 — не менять.</summary>
    ResizeOpening,

    /// <summary>Сдвиг отверстия плиты с каркаса.</summary>
    MoveSlabOpening,

    /// <summary>Проём в плите TargetId над лестницей SecondaryId.</summary>
    CreateStairsOpening,

    /// <summary>Перенос балки перпендикулярно оси за пределы марша SecondaryId.</summary>
    ShiftBeamOffStairs,

    /// <summary>Вписать проём в габарит стены SecondaryId.</summary>
    ClampWallOpening,

    /// <summary>Привязать проём к ближайшей стене.</summary>
    AttachToWall,

    /// <summary>Удалить элемент (дубликат, лишняя конструкция).</summary>
    RemoveElement,

    /// <summary>Ядро жёсткости в центре плана постройки GroupId (Value × Value2, м) на всю высоту.</summary>
    AddCore,

    /// <summary>Диафрагма жёсткости: стены толщиной 0.2 м длиной Value на всех этажах постройки GroupId у грани (X, Z), вдоль оси (Value2 = 0 — X, 90 — Z).</summary>
    AddDiaphragm,

    /// <summary>Толщина стены Value, м (ось сохраняется, несущий слой пирога принимает прирост, проёмы режут новую толщину).</summary>
    ThickenWall
}

/// <summary>Действие исправления с параметрами.</summary>
public sealed record FixAction(FixActionKind Kind, string TargetId)
{
    public string? SecondaryId { get; init; }
    public string? GroupId { get; init; }
    public double Value { get; init; }
    public double Value2 { get; init; }
    public double X { get; init; }
    public double Z { get; init; }
    public int Count { get; init; }

    /// <summary>Профиль новых балок/колонн (rectangle, i_beam, pipe, circle) — правка пользователя «двутавровые стальные».</summary>
    public string? Profile { get; init; }
    public string? Material { get; init; }
}

/// <summary>Готовый вариант исправления замечания: автор, описание, действия и прогноз (K_исп, бетон) по моделированию на копии сцены.</summary>
public sealed class ProposedFixOption
{
    public string Id { get; set; } = string.Empty;
    public FixAuthor Author { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<FixAction> Actions { get; set; } = [];

    /// <summary>Порядок выбора автопилотом: меньше — предпочтительнее.</summary>
    public int Priority { get; set; } = 10;

    /// <summary>Сохраняет архитектурный замысел (свободный план, положение проёмов).</summary>
    public bool PreservesArchitecture { get; set; }

    /// <summary>Прогноз K_исп затронутых элементов после исправления (null — не моделировалось).</summary>
    public double? PredictedUtilization { get; set; }

    public bool IsKeep => Author == FixAuthor.Keep;
}

/// <summary>Структурированное замечание проверщика: код, важность, объяснение, затронутые элементы и варианты исправления.</summary>
public sealed class VerificationFinding
{
    public string VerifierId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public VerificationSeverity Severity { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DetailedExplanation { get; set; } = string.Empty;
    public List<string> AffectedElementIds { get; set; } = [];
    public List<ProposedFixOption> ProposedFixOptions { get; set; } = [];

    /// <summary>Точка узла [X, Y, Z], м — для фокуса камеры.</summary>
    public double[]? Location { get; set; }

    public double Extent { get; set; } = 3;

    /// <summary>K_исп элемента (для замечаний расчёта).</summary>
    public double? Utilization { get; set; }

    public bool IsCritical => Severity == VerificationSeverity.Critical;

    /// <summary>Есть исправления, кроме «оставить как есть».</summary>
    public bool HasFixes => ProposedFixOptions.Any(o => !o.IsKeep && o.Actions.Count > 0);

    public string SeverityTitle => Severity switch
    {
        VerificationSeverity.Critical => "критично",
        VerificationSeverity.Warning => "предупреждение",
        _ => "к сведению"
    };

    public override string ToString() => $"[{Code}] {Title}";
}

/// <summary>Время и число замечаний одного проверщика.</summary>
public sealed record VerifierTiming(string Id, string Title, double Milliseconds, int Findings);

/// <summary>Итог конвейера: замечания всех проверщиков, расчёт и коллизии.</summary>
public sealed class VerificationReport
{
    public List<VerificationFinding> Findings { get; } = [];
    public List<VerifierTiming> Timings { get; } = [];
    public StructuralAnalysisResult Analysis { get; set; } = StructuralAnalysisResult.Empty;
    public int ClashCount { get; set; }
    public double ElapsedMilliseconds { get; set; }

    public int CriticalCount => Findings.Count(f => f.Severity == VerificationSeverity.Critical);
    public int WarningCount => Findings.Count(f => f.Severity == VerificationSeverity.Warning);
    public double MaxUtilization => Analysis.MaxUtilization;

    /// <summary>Замечания, требующие решения (критичные и предупреждения с вариантами исправления).</summary>
    public IEnumerable<VerificationFinding> Actionable => Findings.Where(f => f.Severity != VerificationSeverity.Info && f.HasFixes);

    public string Summary =>
        $"проверщиков {Timings.Count}, {ElapsedMilliseconds:0} мс: критичных {CriticalCount}, предупреждений {WarningCount}, " +
        $"K_исп max {MaxUtilization:0.00}, коллизий {ClashCount}";
}
