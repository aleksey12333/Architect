namespace AIArchitectStudio.Services.AI;

/// <summary>Роли Мультиагентного конструкторского бюро (Multi-Agent BIM Council).</summary>
public enum AiAgentRole
{
    /// <summary>Главный архитектор / ГИП: посадка, сетка осей, отметки, объёмно-планировочное решение, остекление, кровля.</summary>
    ChiefArchitect,

    /// <summary>Главный конструктор КЖ/КМ: несущий каркас, перекрытия с проёмами, пироги.</summary>
    StructuralEngineer,

    /// <summary>Инженер нормоконтроля и КЭ-аудита: конвейер C#-проверщиков и критика с рецептами усиления.</summary>
    NormsAndSafetyVerifier,

    /// <summary>Координатор BIM-проекта: сводит правки, разрешает спор «красота — прочность», выпускает модель.</summary>
    MasterSynthesizer
}

/// <summary>Описание роли: подписи RU/EN, глиф Segoe Fluent Icons, цвет метки, температура и ролевой промпт.</summary>
public sealed record AgentRoleInfo(AiAgentRole Role, string Key, string TitleRu, string TitleEn, string ShortRu, string ShortEn,
    string DescriptionRu, string DescriptionEn, string Glyph, string ColorHex, double Temperature)
{
    public string Title(bool ru) => ru ? TitleRu : TitleEn;
    public string Short(bool ru) => ru ? ShortRu : ShortEn;
    public string Description(bool ru) => ru ? DescriptionRu : DescriptionEn;
}

public static class AgentRoles
{
    public static readonly IReadOnlyList<AgentRoleInfo> All =
    [
        new(AiAgentRole.ChiefArchitect, "architect", "Главный архитектор (ГИП)", "Chief architect", "Архитектор", "Architect",
            "Посадка, сетка осей, отметки, объёмы, остекление, кровля", "Siting, grid, levels, massing, glazing, roof",
            "", "#5AC8FA", 0.35),
        new(AiAgentRole.StructuralEngineer, "structural", "Главный конструктор КЖ/КМ", "Chief structural engineer", "Конструктор КЖ", "Structural",
            "Каркас: колонны, балки, ядра, плиты с проёмами, пироги", "Frame: columns, beams, cores, slabs, layers",
            "", "#FF9F0A", 0.1),
        new(AiAgentRole.NormsAndSafetyVerifier, "norms", "Инженер нормоконтроля и КЭ-аудита", "Code compliance and FEA auditor", "Нормоконтроль", "Compliance",
            "Конвейер проверщиков: N, M, Q, прогибы, продавливание, коллизии", "Verifier pipeline: N, M, Q, deflection, punching, clashes",
            "", "#FF453A", 0.0),
        new(AiAgentRole.MasterSynthesizer, "synthesizer", "Координатор BIM-проекта", "BIM coordinator", "Координатор", "Coordinator",
            "Сводит правки, выбирает решение, выпускает модель", "Merges proposals, decides, releases the model",
            "", "#30D158", 0.2)
    ];

    public static AgentRoleInfo Info(AiAgentRole role) => All.First(r => r.Role == role);

    public static AiAgentRole? FromKey(string? key) => All.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))?.Role;
}
