namespace AIArchitectStudio.Services.AI;

/// <summary>Промежуточное состояние потоковой генерации: накопленные рассуждения и ответ (для ленты «Коллоквиум ИИ»).</summary>
public sealed record AiStreamProgress(string Reasoning, string Answer);

/// <summary>
/// Отделение «внутренних рассуждений» рассуждающих моделей (deepseek-r1, QwQ, gpt-oss, Qwen 3 и т.п.) от основного ответа.
/// Понимает блоки <c>&lt;think&gt;…&lt;/think&gt;</c> (а также <c>&lt;thinking&gt;</c>, <c>&lt;reasoning&gt;</c>) в любом месте,
/// несколько блоков подряд, незакрытый блок (ответ обрезан или ещё генерируется — хвост считается рассуждением) и шаблон
/// deepseek-r1, в котором открывающий тег уже стоит в промпте и ответ начинается сразу с рассуждения до <c>&lt;/think&gt;</c>.
/// JSON-десериализатор получает только ответ — фигурные скобки внутри рассуждений его не сбивают.
/// </summary>
public static class ReasoningText
{
    private static readonly (string Open, string Close)[] Tags =
    [
        ("<think>", "</think>"),
        ("<thinking>", "</thinking>"),
        ("<reasoning>", "</reasoning>")
    ];

    /// <summary>Есть ли в тексте теги рассуждений.</summary>
    public static bool HasReasoningTags(string? text) =>
        !string.IsNullOrEmpty(text) && Tags.Any(t => text.Contains(t.Open, StringComparison.OrdinalIgnoreCase) || text.Contains(t.Close, StringComparison.OrdinalIgnoreCase));

    /// <summary>Разделяет текст модели на ответ и рассуждения (без тегов, с обрезанными пробелами).</summary>
    public static (string Answer, string Reasoning) Separate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (string.Empty, string.Empty);
        if (!HasReasoningTags(text)) return (text.Trim(), string.Empty);

        var answer = new System.Text.StringBuilder(text.Length);
        var reasoning = new System.Text.StringBuilder();
        int pos = 0;

        // Шаблон deepseek-r1: «рассуждение…</think>ответ» без открывающего тега — всё до первого закрывающего тега.
        int firstOpen = IndexOfAny(text, 0, open: true, out _);
        int firstClose = IndexOfAny(text, 0, open: false, out int firstCloseLength);
        if (firstClose >= 0 && (firstOpen < 0 || firstClose < firstOpen))
        {
            Append(reasoning, text[..firstClose]);
            pos = firstClose + firstCloseLength;
        }

        while (pos < text.Length)
        {
            int open = IndexOfAny(text, pos, open: true, out int openLength);
            if (open < 0)
            {
                answer.Append(text, pos, text.Length - pos);
                break;
            }
            answer.Append(text, pos, open - pos);
            int bodyStart = open + openLength;
            int close = IndexOfAny(text, bodyStart, open: false, out int closeLength);
            if (close < 0)
            {
                // Незакрытый блок: генерация обрезана или ещё идёт — всё до конца считается рассуждением.
                Append(reasoning, text[bodyStart..]);
                break;
            }
            Append(reasoning, text[bodyStart..close]);
            pos = close + closeLength;
        }

        // Одиночные закрывающие теги, оставшиеся в ответе (модель закрыла блок дважды), не должны мешать разбору.
        string a = answer.ToString();
        foreach (var (_, closeTag) in Tags)
            a = a.Replace(closeTag, string.Empty, StringComparison.OrdinalIgnoreCase);
        return (a.Trim(), reasoning.ToString().Trim());
    }

    /// <summary>Ответ без рассуждений (если тегов нет — исходный текст).</summary>
    public static string AnswerOnly(string? text) => HasReasoningTags(text) ? Separate(text).Answer : text ?? string.Empty;

    /// <summary>
    /// Модель известного рассуждающего семейства: deepseek-r1, QwQ, gpt-oss, Qwen 3, Magistral, Phi-4-reasoning и т.п.
    /// (для них парсеры обязательно отделяют рассуждения, даже если сервер не сообщил о возможности «thinking»).
    /// </summary>
    public static bool IsReasoningFamily(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return false;
        string id = modelId.ToLowerInvariant();
        return id.Contains("deepseek-r1") || id.Contains("-r1") || id.StartsWith("r1", StringComparison.Ordinal) || id.Contains("qwq") ||
               id.Contains("gpt-oss") || id.Contains("qwen3") || id.Contains("magistral") || id.Contains("reasoning") || id.Contains("thinking");
    }

    private static void Append(System.Text.StringBuilder sb, string part)
    {
        part = part.Trim();
        if (part.Length == 0) return;
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append(part);
    }

    private static int IndexOfAny(string text, int start, bool open, out int length)
    {
        int best = -1;
        length = 0;
        foreach (var (o, c) in Tags)
        {
            string tag = open ? o : c;
            int i = text.IndexOf(tag, start, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best))
            {
                best = i;
                length = tag.Length;
            }
        }
        return best;
    }
}
