using System.Text.Json.Serialization;
using AIArchitectStudio.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIArchitectStudio.Models;

public enum ChatRole
{
    User,
    Assistant,
    System,
    Error
}

/// <summary>Сообщение чата ИИ-Проектировщика.</summary>
public sealed partial class ChatMessageModel : ObservableObject
{
    /// <summary>Язык интерфейса для подписей новых сообщений (MainViewModel синхронизирует его с LocalizationService).</summary>
    public static string CurrentLanguage { get; set; } = LocalizationService.Russian;

    public ChatRole Role { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string? ModelName { get; set; }
    public SolverReport? Report { get; set; }

    [JsonIgnore] public bool IsUser => Role == ChatRole.User;
    [JsonIgnore] public bool IsError => Role == ChatRole.Error;
    [JsonIgnore] public bool HasReport => Report is not null;
    [JsonIgnore] public string ReportSummary => Report?.Summary ?? string.Empty;
    [JsonIgnore] public string ReportDetails => Report?.DetailsText ?? string.Empty;

    /// <summary>Язык подписи «Вы / Система / Ошибка» (RU/EN) — переключается вместе с языком интерфейса.</summary>
    [JsonIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(RoleTitle))]
    public partial string Language { get; set; } = CurrentLanguage;

    [JsonIgnore]
    public string Header => Role switch
    {
        ChatRole.User => LocalizationService.Lookup("ChatRoleUser", Language),
        ChatRole.Assistant => string.IsNullOrEmpty(ModelName) ? LocalizationService.Lookup("ChatRoleAssistant", Language) : ModelName!,
        ChatRole.Error => LocalizationService.Lookup("ChatRoleError", Language),
        _ => LocalizationService.Lookup("ChatRoleSystem", Language)
    } + " · " + Timestamp.ToString("HH:mm");

    /// <summary>Подпись роли без времени (бейдж карточки сообщения).</summary>
    [JsonIgnore]
    public string RoleTitle => Role switch
    {
        ChatRole.User => LocalizationService.Lookup("ChatRoleUser", Language),
        ChatRole.Assistant => string.IsNullOrEmpty(ModelName) ? LocalizationService.Lookup("ChatRoleAssistant", Language) : ModelName!,
        ChatRole.Error => LocalizationService.Lookup("ChatRoleError", Language),
        _ => LocalizationService.Lookup("ChatRoleSystem", Language)
    };

    [JsonIgnore] public string TimeText => Timestamp.ToString("HH:mm");

    [JsonIgnore]
    public string RoleGlyph => Role switch
    {
        ChatRole.User => "",
        ChatRole.Assistant => "",
        ChatRole.Error => "",
        _ => ""
    };

    [JsonIgnore]
    public string RoleColorHex => Role switch
    {
        ChatRole.User => "#0A84FF",
        ChatRole.Assistant => "#30D158",
        ChatRole.Error => "#FF453A",
        _ => "#8E8E93"
    };

    /// <summary>Миниатюры изображений, приложенных к сообщению (авто-снимок вида, план) — только в сессии, в .aia не пишутся.</summary>
    [JsonIgnore] public IReadOnlyList<byte[]> Thumbnails { get; set; } = [];

    [JsonIgnore] public bool HasThumbnails => Thumbnails.Count > 0;

    /// <summary>Индикатор достоверности ответа: «Проверено по СП 63 · 0 коллизий».</summary>
    [JsonIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial string Badge { get; set; } = string.Empty;

    [JsonIgnore]
    [ObservableProperty]
    public partial string BadgeColorHex { get; set; } = "#30D158";

    [JsonIgnore] public bool HasBadge => Badge.Length > 0;

    /// <summary>Цепочка рассуждений Инженерного агента (только в сессии, в .aia не пишется).</summary>
    [JsonIgnore] public ReasoningTrace? Trace { get; set; }

    [JsonIgnore] public bool HasTrace => Trace is not null;
    [JsonIgnore] public string TraceSummary => Trace?.Summary ?? string.Empty;
    [JsonIgnore] public string TraceDetails => Trace?.ToText() ?? string.Empty;

    /// <summary>Вызванные инструменты агента: «Аудит расчётной схемы — критериев 5 из 5…».</summary>
    [JsonIgnore] public IReadOnlyList<AgentToolCall> ToolCalls { get; set; } = [];

    [JsonIgnore] public bool HasToolCalls => ToolCalls.Count > 0;
    [JsonIgnore] public string ToolCallsSummary => $"Вызванные инструменты · {ToolCalls.Count}";

    [JsonIgnore]
    public string ToolCallsText => string.Join("\n", ToolCalls.Select(c => $"{c.Title} ({c.Name}) — {(c.Status == AgentToolStatus.Succeeded ? "выполнено" : c.Status == AgentToolStatus.Failed ? "ошибка" : "пропущено")}: {c.Summary}"));

    public static ChatMessageModel User(string text) => new() { Role = ChatRole.User, Text = text };
    public static ChatMessageModel System(string text) => new() { Role = ChatRole.System, Text = text };

    public static ChatMessageModel Assistant(string text, string? model, SolverReport? report = null) =>
        new() { Role = ChatRole.Assistant, Text = text, ModelName = model, Report = report };
}
