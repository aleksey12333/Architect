using System.Runtime.InteropServices;
using AIArchitectStudio.Services.AI;
using Windows.Security.Credentials;

namespace AIArchitectStudio.Services;

/// <summary>
/// Хранение секретов (API-ключей) в диспетчере учётных данных Windows (Credential Locker / PasswordVault).
/// Ключ шифруется системой под учётной записью пользователя и никогда не пишется в settings.json.
/// </summary>
public sealed class SecretStore
{
    private const string Resource = "AIArchitectStudio";
    public const string GeminiApiKeyName = "GeminiApiKey";
    public const string OpenAiApiKeyName = "OpenAiApiKey";
    public const string AnthropicApiKeyName = "AnthropicApiKey";
    public const string OpenRouterApiKeyName = "OpenRouterApiKey";

    /// <summary>Имя записи PasswordVault для облачного провайдера.</summary>
    public static string KeyNameFor(AiProviderKind provider) => provider switch
    {
        AiProviderKind.Gemini => GeminiApiKeyName,
        AiProviderKind.OpenAI => OpenAiApiKeyName,
        AiProviderKind.Anthropic => AnthropicApiKeyName,
        AiProviderKind.OpenRouter => OpenRouterApiKeyName,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "У провайдера нет API-ключа")
    };

    /// <summary>Возвращает секрет или пустую строку, если он не сохранён.</summary>
    public string Read(string name)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, name);
            credential.RetrievePassword();
            return credential.Password ?? string.Empty;
        }
        catch (COMException)
        {
            // 0x80070490 (ELEMENT_NOT_FOUND) — секрет ещё не сохранён.
            return string.Empty;
        }
    }

    /// <summary>Сохраняет секрет; пустое значение удаляет его из хранилища.</summary>
    public void Write(string name, string? value)
    {
        var vault = new PasswordVault();
        Delete(vault, name);
        if (!string.IsNullOrWhiteSpace(value))
            vault.Add(new PasswordCredential(Resource, name, value.Trim()));
    }

    private static void Delete(PasswordVault vault, string name)
    {
        try
        {
            vault.Remove(vault.Retrieve(Resource, name));
        }
        catch (COMException)
        {
            // Удалять нечего.
        }
    }
}
