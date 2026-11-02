using System.Text.Json;

namespace AIArchitectStudio.Services.AI;

/// <summary>Описание веб-чата: адрес и CSS-селекторы поля ввода, кнопок отправки/остановки и ответов модели.</summary>
public sealed record WebAiSite(
    AiProviderKind Kind,
    string Title,
    string Url,
    string Host,
    string[] InputSelectors,
    string[] SendSelectors,
    string[] StopSelectors,
    string[] ResponseSelectors);

/// <summary>Ожидающий ответа запрос, отправленный в веб-чат.</summary>
public sealed class WebBridgeSession
{
    public required AiRequestContext Context { get; init; }
    public required string Prompt { get; init; }
    public required WebAiSite Site { get; init; }
    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>Ожидание ответа агентом Совета КБ: перехваченный текст передаётся сюда, а не в солвер.</summary>
    public TaskCompletionSource<string>? Completion { get; init; }

    /// <summary>Критерий приёма ответа (по умолчанию — JSON сцены).</summary>
    public Func<string, bool>? Accept { get; init; }
}

/// <summary>Перехваченный ответ веб-чата с JSON сцены.</summary>
public sealed record WebBridgeCapture(string Text, string Source, WebAiSite Site, AiRequestContext? Context);

/// <summary>Снимок состояния страницы, возвращаемый DOM-поллингом.</summary>
public sealed record WebPageSnapshot(bool Busy, int Count, string Code, string Text, double SinceMutationMs)
{
    public string Candidate => Code.Length > 0 ? Code : Text;
}

/// <summary>
/// Мост к обычным веб-аккаунтам ChatGPT / Claude / Gemini без API-ключа. Формирует полный промпт
/// (скрытое ТЗ SystemPrompts + задача), готовит JS для вставки и отправки в поле ввода сайта, DOM-поллинг
/// с MutationObserver для отслеживания завершения генерации и принимает перехваченный JSON
/// (со страницы или из буфера обмена после кнопки «Copy code»).
/// </summary>
public sealed class WebAccountBridgeProvider
{
    /// <summary>Постоянный профиль WebView2: куки и вход в аккаунт сохраняются между запусками.</summary>
    public static string ProfileDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIArchitectStudio", "WebAccountProfile");

    public static readonly IReadOnlyList<WebAiSite> Sites =
    [
        new(AiProviderKind.WebChatGpt, "ChatGPT", "https://chatgpt.com/", "chatgpt.com",
            ["#prompt-textarea", "div[contenteditable=\"true\"]", "textarea"],
            ["button[data-testid=\"send-button\"]", "#composer-submit-button", "button[aria-label*=\"Send\"]", "button[aria-label*=\"Отправ\"]"],
            ["button[data-testid=\"stop-button\"]", "button[aria-label*=\"Stop\"]", "button[aria-label*=\"Останов\"]"],
            ["[data-message-author-role=\"assistant\"]"]),
        new(AiProviderKind.WebClaude, "Claude", "https://claude.ai/new", "claude.ai",
            ["div[contenteditable=\"true\"].ProseMirror", "div[contenteditable=\"true\"]", "textarea"],
            ["button[aria-label=\"Send message\"]", "button[aria-label=\"Send Message\"]", "button[aria-label*=\"Send\"]", "button[aria-label*=\"Отправ\"]"],
            ["button[aria-label=\"Stop response\"]", "button[aria-label*=\"Stop\"]", "[data-is-streaming=\"true\"]"],
            [".font-claude-response", ".font-claude-message", "[data-is-streaming]"]),
        new(AiProviderKind.WebGemini, "Gemini", "https://gemini.google.com/app", "gemini.google.com",
            ["rich-textarea div[contenteditable=\"true\"]", ".ql-editor[contenteditable=\"true\"]", "div[contenteditable=\"true\"]", "textarea"],
            ["button.send-button", "button[aria-label=\"Send message\"]", "button[aria-label*=\"Send\"]", "button[aria-label*=\"Отправ\"]"],
            ["button[aria-label=\"Stop response\"]", "button[aria-label*=\"Stop\"]", "button[aria-label*=\"Останов\"]"],
            ["model-response", "message-content", ".model-response-text"])
    ];

    public WebBridgeSession? Pending { get; private set; }

    /// <summary>Подготовлен новый промпт — панель WebView2 вставляет и отправляет его.</summary>
    public event EventHandler<WebBridgeSession>? PromptPrepared;

    /// <summary>Перехвачен валидный JSON — ViewModel прогоняет его через солвер.</summary>
    public event EventHandler<WebBridgeCapture>? ResponseCaptured;

    /// <summary>Сессия сброшена (ответ применён или отменён).</summary>
    public event EventHandler? PendingChanged;

    public static WebAiSite Site(AiProviderKind kind) => Sites.FirstOrDefault(s => s.Kind == kind) ?? Sites[0];

    public WebBridgeSession Prepare(AiRequestContext context, string prompt)
    {
        var session = new WebBridgeSession { Context = context, Prompt = prompt, Site = Site(context.Model.Provider) };
        Pending = session;
        PendingChanged?.Invoke(this, EventArgs.Empty);
        PromptPrepared?.Invoke(this, session);
        return session;
    }

    public void Cancel()
    {
        if (Pending is null) return;
        Pending.Completion?.TrySetCanceled();
        Pending = null;
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Запрос агента Совета КБ через веб-аккаунт: промпт вставляется и отправляется панелью WebView2, ответ (по критерию
    /// accept) возвращается задачей. Вызывать из UI-потока (панель подписана на PromptPrepared).
    /// </summary>
    public Task<string> AskAsync(AiRequestContext context, string prompt, Func<string, bool>? accept, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new WebBridgeSession { Context = context, Prompt = prompt, Site = Site(context.Model.Provider), Completion = tcs, Accept = accept };
        Pending?.Completion?.TrySetCanceled();
        Pending = session;
        PendingChanged?.Invoke(this, EventArgs.Empty);
        PromptPrepared?.Invoke(this, session);
        var registration = cancellationToken.Register(() =>
        {
            if (ReferenceEquals(Pending, session)) Cancel();
            tcs.TrySetCanceled(cancellationToken);
        });
        // Регистрация живёт до ответа, а не до конца жизни источника отмены (иначе каждая реплика агента удерживает сессию).
        _ = tcs.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>
    /// Принимает текст ответа (со страницы, из буфера обмена или по кнопке «Применить JSON из ответа»),
    /// если в нём есть валидный JSON сцены и это не наш собственный промпт.
    /// </summary>
    public bool TryCapture(string? text, string source, WebAiSite site, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (Pending is { } p && string.Equals(text.Trim(), p.Prompt.Trim(), StringComparison.Ordinal)) return false;
        if (Pending is null && !force) return false;
        if (Pending is { Completion: { } completion } awaiting)
        {
            // Ответ агенту Совета: критерий приёма свой (JSON сцены или JSON реплики), в солвер не идёт.
            if (!(awaiting.Accept ?? AiJsonExtractor.ContainsSceneJson)(text)) return false;
            Pending = null;
            PendingChanged?.Invoke(this, EventArgs.Empty);
            completion.TrySetResult(text);
            return true;
        }
        if (!AiJsonExtractor.ContainsSceneJson(text)) return false;

        var context = Pending?.Context;
        Pending = null;
        PendingChanged?.Invoke(this, EventArgs.Empty);
        ResponseCaptured?.Invoke(this, new WebBridgeCapture(text, source, site, context));
        return true;
    }

    // ───────────────────────────── JavaScript для CoreWebView2.ExecuteScriptAsync ─────────────────────────────

    /// <summary>Вешается на каждую страницу: MutationObserver отмечает время последнего изменения DOM.</summary>
    public const string ObserverScript = """
(() => {
  if (window.__aiaObserverInstalled) return;
  window.__aiaObserverInstalled = true;
  window.__aiaLastMutation = Date.now();
  const start = () => {
    const observer = new MutationObserver(() => { window.__aiaLastMutation = Date.now(); });
    observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true });
  };
  if (document.documentElement) start(); else document.addEventListener('DOMContentLoaded', start);
})();
""";

    /// <summary>Вставляет промпт в textarea / [contenteditable="true"] сайта. Возвращает JSON {ok, reason}.</summary>
    public static string InsertScript(WebAiSite site, string prompt) => $$"""
(() => {
  const prompt = {{JsonSerializer.Serialize(prompt)}};
  const selectors = {{JsonSerializer.Serialize(site.InputSelectors)}};
  const visible = e => !!(e && (e.offsetParent !== null || e.getClientRects().length));
  let input = null;
  for (const s of selectors) {
    const list = [...document.querySelectorAll(s)].filter(visible);
    if (list.length) { input = list[list.length - 1]; break; }
  }
  if (!input) return JSON.stringify({ ok: false, reason: 'input-not-found' });
  input.focus();
  if (input.tagName === 'TEXTAREA' || input.tagName === 'INPUT') {
    const proto = input.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(input, prompt);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  } else {
    const selection = window.getSelection();
    const range = document.createRange();
    range.selectNodeContents(input);
    selection.removeAllRanges();
    selection.addRange(range);
    let ok = false;
    try { ok = document.execCommand('insertText', false, prompt); } catch (e) { ok = false; }
    if (!ok || !(input.innerText || '').trim()) {
      const data = new DataTransfer();
      data.setData('text/plain', prompt);
      input.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
    }
    input.dispatchEvent(new InputEvent('input', { bubbles: true }));
  }
  const length = (input.value || input.innerText || '').length;
  return JSON.stringify({ ok: length > 0, reason: length > 0 ? 'inserted' : 'empty-after-insert' });
})()
""";

    /// <summary>
    /// Программное прикрепление изображений к сообщению сайта без буфера обмена: файлы собираются из base64 в DataTransfer и
    /// передаются полю выбора файла (input[type=file] → change), иначе — событием вставки (paste) или перетаскивания (drop)
    /// в поле ввода. Возвращает JSON {ok, via, count}.
    /// </summary>
    public static string AttachImagesScript(WebAiSite site, IReadOnlyList<AiImage> images)
    {
        var files = images.Select((image, i) => new
        {
            name = image.Label is { Length: > 0 } label ? SafeName(label, i, image.MimeType) : $"architect-studio-{i + 1}{(image.MimeType == "image/png" ? ".png" : ".jpg")}",
            mime = image.MimeType,
            b64 = image.Base64
        }).ToList();
        return $$"""
(() => {
  const files = {{JsonSerializer.Serialize(files)}};
  const selectors = {{JsonSerializer.Serialize(site.InputSelectors)}};
  const data = new DataTransfer();
  for (const f of files) {
    const raw = atob(f.b64);
    const bytes = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
    data.items.add(new File([bytes], f.name, { type: f.mime }));
  }
  const inputs = [...document.querySelectorAll('input[type="file"]')];
  const target = inputs.find(i => (i.accept || '').includes('image')) || inputs[0];
  if (target) {
    try {
      target.files = data.files;
      target.dispatchEvent(new Event('input', { bubbles: true }));
      target.dispatchEvent(new Event('change', { bubbles: true }));
      return JSON.stringify({ ok: true, via: 'file-input', count: data.files.length });
    } catch (e) { }
  }
  const visible = e => !!(e && (e.offsetParent !== null || e.getClientRects().length));
  let editor = null;
  for (const s of selectors) {
    const list = [...document.querySelectorAll(s)].filter(visible);
    if (list.length) { editor = list[list.length - 1]; break; }
  }
  if (!editor) return JSON.stringify({ ok: false, via: 'none', count: 0 });
  editor.focus();
  const paste = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
  editor.dispatchEvent(paste);
  if (paste.defaultPrevented) return JSON.stringify({ ok: true, via: 'paste', count: data.files.length });
  const drop = new DragEvent('drop', { dataTransfer: data, bubbles: true, cancelable: true });
  editor.dispatchEvent(drop);
  return JSON.stringify({ ok: drop.defaultPrevented, via: 'drop', count: data.files.length });
})()
""";
    }

    private static string SafeName(string label, int index, string mime)
    {
        var name = new string(Path.GetFileNameWithoutExtension(label).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return (name.Length == 0 ? $"architect-studio-{index + 1}" : name) + (mime == "image/png" ? ".png" : ".jpg");
    }

    /// <summary>Результат скрипта прикрепления: удалось ли и каким путём (file-input / paste / drop).</summary>
    public static (bool Ok, string Via, int Count) ParseAttach(string scriptResult)
    {
        try
        {
            using var doc = JsonDocument.Parse(ParseString(scriptResult));
            var r = doc.RootElement;
            return (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                r.TryGetProperty("via", out var via) ? via.GetString() ?? string.Empty : string.Empty,
                r.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 0);
        }
        catch (JsonException)
        {
            return (false, string.Empty, 0);
        }
    }

    /// <summary>Нажимает кнопку отправки, когда она стала активной. Возвращает "sent" / "waiting" / "no-button".</summary>
    public static string SendScript(WebAiSite site) => $$"""
(() => {
  const selectors = {{JsonSerializer.Serialize(site.SendSelectors)}};
  for (const s of selectors) {
    const list = [...document.querySelectorAll(s)].filter(e => e.offsetParent !== null || e.getClientRects().length);
    if (!list.length) continue;
    const button = list[list.length - 1];
    if (button.disabled || button.getAttribute('aria-disabled') === 'true') return 'waiting';
    button.click();
    return 'sent';
  }
  return 'no-button';
})()
""";

    /// <summary>DOM-поллинг: идёт ли генерация, число ответов, последний блок кода и текст последнего ответа.</summary>
    public static string PollScript(WebAiSite site) => $$"""
(() => {
  const stopSelectors = {{JsonSerializer.Serialize(site.StopSelectors)}};
  const responseSelectors = {{JsonSerializer.Serialize(site.ResponseSelectors)}};
  const visible = e => !!(e && (e.offsetParent !== null || e.getClientRects().length));
  let busy = false;
  for (const s of stopSelectors) { if ([...document.querySelectorAll(s)].some(visible)) { busy = true; break; } }
  let responses = [];
  for (const s of responseSelectors) { const list = [...document.querySelectorAll(s)]; if (list.length) { responses = list; break; } }
  const last = responses.length ? responses[responses.length - 1] : null;
  let code = '';
  const scope = last || document;
  const blocks = [...scope.querySelectorAll('pre code, pre, code-block code, code')];
  for (const b of blocks) {
    const t = (b.innerText || b.textContent || '').trim();
    if (t.length > code.length && (t.startsWith('{') || t.startsWith('['))) code = t;
  }
  const text = last ? (last.innerText || last.textContent || '') : '';
  return JSON.stringify({
    busy: busy,
    count: responses.length,
    code: code.slice(-200000),
    text: text.slice(-200000),
    since: Date.now() - (window.__aiaLastMutation || 0)
  });
})()
""";

    /// <summary>Разбирает результат ExecuteScriptAsync (JSON-строка со строкой JSON внутри).</summary>
    public static WebPageSnapshot? ParseSnapshot(string scriptResult)
    {
        try
        {
            var inner = JsonSerializer.Deserialize<string>(scriptResult);
            if (string.IsNullOrEmpty(inner)) return null;
            using var doc = JsonDocument.Parse(inner);
            var r = doc.RootElement;
            return new WebPageSnapshot(
                r.TryGetProperty("busy", out var b) && b.ValueKind == JsonValueKind.True,
                r.TryGetProperty("count", out var c) && c.TryGetInt32(out var ci) ? ci : 0,
                r.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty,
                r.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                r.TryGetProperty("since", out var s) && s.TryGetDouble(out var sd) ? sd : 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Строковый результат простого скрипта ("sent", JSON вставки и т.п.).</summary>
    public static string ParseString(string scriptResult)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(scriptResult) ?? string.Empty;
        }
        catch (JsonException)
        {
            return scriptResult;
        }
    }
}
