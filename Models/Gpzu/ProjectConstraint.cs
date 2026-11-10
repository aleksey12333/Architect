using System.Globalization;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models.Gpzu;

/// <summary>Категория ограничения («НО») документа или нормы.</summary>
public enum ConstraintCategory
{
    HeightLimit,
    NetworkRelocation,
    SanitaryZone,
    FireAccess,
    LivingAreaLimit,
    Parking,
    CulturalHeritage,
    HazardousFacility,
    Setback,
    ProtectionZone,
    SpecialConditions,
    Approval,
    Other
}

/// <summary>Как ограничение влияет на проект: блокирует посадку, требует ТУ/согласования или справочно.</summary>
public enum ConstraintImpact { Blocking, RequiresApproval, Informational }

/// <summary>Решение пользователя по пункту окна подтверждения.</summary>
public enum ConstraintStatus { Pending, Confirmed, Rejected, Manual }

/// <summary>Откуда пункт: текст документа (ядро или модель), веб-поиск, база нормативов ядра, ручной ввод.</summary>
public enum ConstraintOrigin { Document, Web, Normative, Manual }

/// <summary>
/// Ограничение, особое условие или «НО» документа: категория, точная цитата и место (страница/пункт), числовые параметры для
/// геометрического ядра и влияние на проект. Неизменяемое: правка пользователем — новая запись (with), подтверждённый набор
/// хранится в плане участка (<see cref="GpzuSitePlan.Constraints"/>) и входит в журнал Undo вместе с участком.
/// </summary>
public sealed record ProjectConstraintItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public ConstraintCategory Category { get; init; } = ConstraintCategory.Other;
    public string Title { get; init; } = string.Empty;

    /// <summary>Точная цитата из документа (или фрагмент найденного источника).</summary>
    public string Quote { get; init; } = string.Empty;

    /// <summary>Страница (с 1; 0 — неизвестна).</summary>
    public int Page { get; init; }

    /// <summary>Пункт / раздел документа («п. 5.1», «сноска *», «табл. 12.6»).</summary>
    public string Clause { get; init; } = string.Empty;

    /// <summary>Источник: имя файла документа, URL страницы, норматив.</summary>
    public string Source { get; init; } = string.Empty;

    public ConstraintOrigin Origin { get; init; } = ConstraintOrigin.Document;

    /// <summary>Кто извлёк пункт: «Ядро расчётов» или подпись модели.</summary>
    public string ExtractedBy { get; init; } = string.Empty;

    // ── Числовые параметры для геометрического ядра (null — не задан) ──
    /// <summary>Предельная высота над землёй, м.</summary>
    public double? MaxHeightM { get; init; }
    /// <summary>Предельная абсолютная отметка, м БС.</summary>
    public double? MaxHeightAbsM { get; init; }
    /// <summary>Минимальный отступ от границ участка, м.</summary>
    public double? MinSetbackM { get; init; }
    /// <summary>Нужен вынос (переустройство) сетей / ТУ балансодержателя.</summary>
    public bool RequiresNetworkRelocation { get; init; }
    /// <summary>Минимум машино-мест.</summary>
    public int? MinParkingSpots { get; init; }
    /// <summary>Радиус / полуширина буферной (охранной, санитарной) зоны, м.</summary>
    public double? BufferRadiusM { get; init; }
    /// <summary>Предельная общая площадь жилых помещений, м².</summary>
    public double? MaxLivingAreaM2 { get; init; }
    /// <summary>Вид сети, к которой относится буфер или вынос (null — все).</summary>
    public GpzuZoneKind? NetworkKind { get; init; }

    public ConstraintImpact Impact { get; init; } = ConstraintImpact.Informational;

    /// <summary>Пояснение влияния на проект.</summary>
    public string ImpactText { get; init; } = string.Empty;

    public ConstraintStatus Status { get; init; } = ConstraintStatus.Pending;

    /// <summary>Комментарий пользователя при отклонении («ИИ перепутал …»).</summary>
    public string UserComment { get; init; } = string.Empty;

    /// <summary>Цитата найдена в тексте документа дословно (защита от вымысла модели).</summary>
    public bool QuoteVerified { get; init; }

    /// <summary>Достоверность 0…1.</summary>
    public double Confidence { get; init; } = 0.7;

    /// <summary>Пункт сноски со звёздочкой («*», «**»).</summary>
    public bool IsFootnote { get; init; }

    [JsonIgnore] public bool IsActive => Status is ConstraintStatus.Confirmed or ConstraintStatus.Manual;

    [JsonIgnore] public bool HasNumbers => MaxHeightM.HasValue || MaxHeightAbsM.HasValue || MinSetbackM.HasValue || MinParkingSpots.HasValue || BufferRadiusM.HasValue || MaxLivingAreaM2.HasValue;

    /// <summary>Значение параметра для ядра одной строкой («Hmax 102,0 м · отступ 3,0 м»).</summary>
    public string ValueText(CultureInfo c)
    {
        var parts = new List<string>();
        if (MaxHeightM is { } h) parts.Add(string.Create(c, $"H ≤ {h:0.0#} м"));
        if (MaxHeightAbsM is { } ha) parts.Add(string.Create(c, $"Hабс ≤ {ha:0.00} м БС"));
        if (MinSetbackM is { } s) parts.Add(string.Create(c, $"отступ ≥ {s:0.0#} м"));
        if (BufferRadiusM is { } b) parts.Add(string.Create(c, $"зона ± {b:0.0#} м"));
        if (MinParkingSpots is { } p) parts.Add(string.Create(c, $"машино-мест ≥ {p}"));
        if (MaxLivingAreaM2 is { } l) parts.Add(string.Create(c, $"жильё ≤ {l:N0} м²"));
        if (RequiresNetworkRelocation) parts.Add("вынос сетей / ТУ");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    /// <summary>Ключ для объединения дублей: категория + сеть + значения.</summary>
    [JsonIgnore]
    public string MergeKey => string.Create(CultureInfo.InvariantCulture,
        $"{Category}|{NetworkKind}|{MaxHeightM:0.#}|{MaxHeightAbsM:0.##}|{MinSetbackM:0.#}|{BufferRadiusM:0.#}|{MinParkingSpots}|{MaxLivingAreaM2:0}|{RequiresNetworkRelocation}");
}
