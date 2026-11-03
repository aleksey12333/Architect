using System.ComponentModel;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace AIArchitectStudio.Views;

/// <summary>
/// Плавающая стеклянная панель с WebView2 для работы через обычный веб-аккаунт ChatGPT / Claude / Gemini.
/// Сессия хранится в постоянном профиле. Двойной перехватчик ответа: DOM-поллинг с MutationObserver
/// и мониторинг буфера обмена (Clipboard.ContentChanged) + кнопка «Применить JSON из ответа».
/// </summary>
public sealed partial class WebAiAccountBridgePanel : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(6);

    private MainViewModel? _vm;
    private WebAccountBridgeProvider? _bridge;
    private Task? _initTask;
    private bool _webReady;
    private bool _syncingTabs;
    private WebAiSite _site = WebAccountBridgeProvider.Sites[0];
    private TaskCompletionSource<bool>? _navigation;

    private DispatcherQueueTimer? _pollTimer;
    private WebBridgeSession? _active;
    private int _baselineCount;
    private string _baselineCandidate = string.Empty;
    private string _lastCandidate = string.Empty;
    private int _stableTicks;
    private DateTime _waitStarted;
    private bool _polling;
    private string? _lastCopiedPrompt;

    private LocalizationService L => _vm!.Loc;

    public WebAiAccountBridgePanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    public void Initialize(MainViewModel vm)
    {
        _vm = vm;
        _bridge = vm.WebBridge;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Loc.PropertyChanged += OnLanguageChanged;
        _bridge.PromptPrepared += OnPromptPrepared;
        Clipboard.ContentChanged += OnClipboardChanged;

        _pollTimer = DispatcherQueue.CreateTimer();
        _pollTimer.Interval = PollInterval;
        _pollTimer.Tick += OnPollTick;

        _site = WebAccountBridgeProvider.Site(vm.WebBridgeSite);
        ApplyTexts();
        SyncTabs();
        SetStatus(StatusKind.Info, L.Get("WebStatusIdle"));
        if (vm.IsWebBridgeOpen) _ = OpenAsync();
    }

    private void Detach()
    {
        Clipboard.ContentChanged -= OnClipboardChanged;
        _pollTimer?.Stop();
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Loc.PropertyChanged -= OnLanguageChanged;
        }
        if (_bridge is not null) _bridge.PromptPrepared -= OnPromptPrepared;
    }

    /// <summary>
    /// Закрытие окна (Unloaded при этом может не наступить): отписка от статического Clipboard.ContentChanged, остановка
    /// поллинга и закрытие WebView2 (процессы браузера завершаются сразу, профиль освобождается).
    /// </summary>
    public void Shutdown()
    {
        Detach();
        if (!_webReady) return;
        _webReady = false;
        try
        {
            Web.Close();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            App.WriteCrashLog("webview2-close", ex);
        }
    }

    /// <summary>Тик DOM-поллинга: сбой разбора или применения ответа — в статус панели, а не в необработанное исключение.</summary>
    private async void OnPollTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            await PollAsync();
        }
        catch (Exception ex)
        {
            _pollTimer?.Stop();
            App.WriteCrashLog("web-bridge-poll", ex);
            SetStatus(StatusKind.Error, ex.Message);
        }
    }

    // ───────────────────────────── Состояние и тексты ─────────────────────────────

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationService.Language)) ApplyTexts();
    }

    private void ApplyTexts()
    {
        TitleText.Text = L.Get("WebPanelTitle");
        ApplyText.Text = L.Get("WebApplyJson");
        ToolTipService.SetToolTip(CopyPromptButton, L.Get("WebCopyPrompt"));
        ToolTipService.SetToolTip(ResendButton, L.Get("WebResend"));
        ToolTipService.SetToolTip(ReloadButton, L.Get("WebReload"));
        ToolTipService.SetToolTip(CloseButton, L.Get("Close"));
        ProfileText.Text = L.Format("WebProfileHint", WebAccountBridgeProvider.ProfileDirectory);
        PlaceholderText.Text = L.Format("WebStatusLoading", _site.Host);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(MainViewModel.IsWebBridgeOpen) && _vm.IsWebBridgeOpen) _ = OpenAsync();
        if (e.PropertyName == nameof(MainViewModel.WebBridgeSite))
        {
            SyncTabs();
            if (_vm.IsWebBridgeOpen) _ = NavigateAsync(WebAccountBridgeProvider.Site(_vm.WebBridgeSite));
        }
    }

    private void SyncTabs()
    {
        if (_vm is null) return;
        _syncingTabs = true;
        ChatGptTab.IsChecked = _vm.WebBridgeSite == AiProviderKind.WebChatGpt;
        ClaudeTab.IsChecked = _vm.WebBridgeSite == AiProviderKind.WebClaude;
        GeminiTab.IsChecked = _vm.WebBridgeSite == AiProviderKind.WebGemini;
        _syncingTabs = false;
    }

    private void OnSiteChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTabs || _vm is null || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<AiProviderKind>(tag, out var kind)) return;
        // Переключение сайта выбирает соответствующий веб-режим в ИИ-доке.
        _vm.SelectedAiModel = AiModelCatalog.WebOptions.First(o => o.Provider == kind).Id;
        _vm.WebBridgeSite = kind;
    }

    private enum StatusKind { Info, Busy, Success, Error }

    private void SetStatus(StatusKind kind, string text)
    {
        StatusText.Text = text;
        bool busy = kind == StatusKind.Busy;
        StatusRing.IsActive = busy;
        StatusRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StatusGlyph.Glyph = kind switch
        {
            StatusKind.Success => "",
            StatusKind.Error => "",
            _ => ""
        };
        StatusGlyph.Foreground = kind switch
        {
            StatusKind.Success => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x30, 0xD1, 0x58)),
            StatusKind.Error => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x45, 0x3A)),
            _ => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextSecondaryBrush"]
        };
    }

    // ───────────────────────────── WebView2 с постоянным профилем ─────────────────────────────

    private async Task OpenAsync()
    {
        if (_vm is null) return;
        await EnsureWebViewAsync();
        if (!_webReady) return;
        var site = WebAccountBridgeProvider.Site(_vm.WebBridgeSite);
        if (Web.Source is null || !Web.Source.Host.EndsWith(site.Host, StringComparison.OrdinalIgnoreCase))
            await NavigateAsync(site);
    }

    private Task EnsureWebViewAsync() => _initTask ??= InitializeWebViewAsync();

    /// <summary>Процесс браузера WebView2 запускался (панель открывали явно); при старте приложения — false.</summary>
    internal bool IsBrowserStarted => _initTask is not null;

    private async Task InitializeWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(WebAccountBridgeProvider.ProfileDirectory);
            var options = new CoreWebView2EnvironmentOptions { Language = L.IsRussian ? "ru-RU" : "en-US" };
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, WebAccountBridgeProvider.ProfileDirectory, options);
            await Web.EnsureCoreWebView2Async(environment);

            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebAccountBridgeProvider.ObserverScript);
            core.NavigationCompleted += (_, args) =>
            {
                Placeholder.Visibility = Visibility.Collapsed;
                _navigation?.TrySetResult(args.IsSuccess);
            };
            _webReady = true;
        }
        catch (Exception ex)
        {
            // Нет WebView2 Runtime, профиль заблокирован другим процессом и т.п. — показываем причину в статусе.
            _webReady = false;
            Placeholder.Visibility = Visibility.Collapsed;
            SetStatus(StatusKind.Error, L.Format("WebStatusRuntime", ex.Message));
            App.WriteCrashLog("webview2", ex);
        }
    }

    private async Task NavigateAsync(WebAiSite site)
    {
        _site = site;
        await EnsureWebViewAsync();
        if (!_webReady) return;
        PlaceholderText.Text = L.Format("WebStatusLoading", site.Host);
        Placeholder.Visibility = Visibility.Visible;
        if (_active is null) SetStatus(StatusKind.Busy, L.Format("WebStatusLoading", site.Host));

        _navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Web.CoreWebView2.Navigate(site.Url);
        await Task.WhenAny(_navigation.Task, Task.Delay(TimeSpan.FromSeconds(25)));
        if (_active is null) SetStatus(StatusKind.Info, L.Get("WebStatusIdle"));
    }

    private async Task<string> ExecAsync(string script)
    {
        if (!_webReady) return string.Empty;
        try
        {
            return await Web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private async Task<WebPageSnapshot?> SnapshotAsync() => WebAccountBridgeProvider.ParseSnapshot(await ExecAsync(WebAccountBridgeProvider.PollScript(_site)));

    // ───────────────────────────── Отправка промпта ─────────────────────────────

    private async void OnPromptPrepared(object? sender, WebBridgeSession session)
    {
        try
        {
            await SendSessionAsync(session);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("web-bridge", ex);
            SetStatus(StatusKind.Error, ex.Message);
        }
    }

    private async Task SendSessionAsync(WebBridgeSession session)
    {
        _active = session;
        _pollTimer?.Stop();

        // 1) Полный промпт — в буфер обмена (запасной путь: ручная вставка Ctrl+V).
        CopyText(session.Prompt);

        // 2) Сайт нужного провайдера.
        await EnsureWebViewAsync();
        if (!_webReady) return;
        if (Web.Source is null || !Web.Source.Host.EndsWith(session.Site.Host, StringComparison.OrdinalIgnoreCase))
        {
            await NavigateAsync(session.Site);
            await Task.Delay(1500);
        }
        _site = session.Site;

        // Базовая линия: ответ, который уже был на странице, не должен быть принят за новый.
        var baseline = await SnapshotAsync();
        _baselineCount = baseline?.Count ?? 0;
        _baselineCandidate = baseline?.Candidate ?? string.Empty;
        _lastCandidate = string.Empty;
        _stableTicks = 0;

        // 3) Вставка в textarea / [contenteditable="true"] и отправка.
        var insert = WebAccountBridgeProvider.ParseString(await ExecAsync(WebAccountBridgeProvider.InsertScript(session.Site, session.Prompt)));
        if (!insert.Contains("\"ok\":true", StringComparison.Ordinal))
        {
            SetStatus(StatusKind.Error, L.Get("WebStatusNoInput"));
            StartWaiting(keepStatus: true);
            return;
        }

        // 4) Изображения (авто-снимок вида, план, листы PDF) — программно в поле выбора файла сайта, без Ctrl+V.
        var images = session.Context.Images.Count > 0
            ? session.Context.Images
            : session.Context.Image is { Length: > 0 } single ? [new AiImage(single, "image/jpeg", session.Context.ImageName)] : (IReadOnlyList<AiImage>)[];
        if (images.Count > 0)
        {
            var attach = WebAccountBridgeProvider.ParseAttach(await ExecAsync(WebAccountBridgeProvider.AttachImagesScript(session.Site, images)));
            if (!attach.Ok)
            {
                // Сайт не принял файл программно (изменилась разметка) — запасной путь: изображение в буфере обмена.
                if (session.Context.Image is { Length: > 0 } image) await CopyImageAsync(image);
                else await CopyImageAsync(images[0].Bytes);
                SetStatus(StatusKind.Busy, L.Get("WebStatusPasteImage"));
                StartWaiting(keepStatus: true);
                return;
            }
            SetStatus(StatusKind.Busy, L.Format("WebStatusAttached", attach.Count));
            // Загрузка вложения на сервер сайта: кнопка отправки активируется после неё.
            await Task.Delay(1500);
        }

        SetStatus(StatusKind.Busy, L.Get("WebStatusInserted"));
        string result = string.Empty;
        for (int i = 0; i < (images.Count > 0 ? 60 : 30); i++)
        {
            await Task.Delay(300);
            result = WebAccountBridgeProvider.ParseString(await ExecAsync(WebAccountBridgeProvider.SendScript(session.Site)));
            if (result == "sent") break;
        }
        if (result != "sent") SetStatus(StatusKind.Error, L.Get("WebStatusNoInput"));
        StartWaiting(keepStatus: result != "sent");
    }

    private void StartWaiting(bool keepStatus = false)
    {
        _waitStarted = DateTime.Now;
        if (!keepStatus) SetStatus(StatusKind.Busy, L.Format("WebStatusWaiting", _site.Title));
        _pollTimer?.Start();
    }

    // ───────────────────────────── Перехватчик 1: DOM-поллинг + MutationObserver ─────────────────────────────

    private async Task PollAsync()
    {
        if (_polling || _bridge is null) return;
        _polling = true;
        try
        {
            if (_bridge.Pending is null)
            {
                _pollTimer?.Stop();
                return;
            }
            if (DateTime.Now - _waitStarted > WaitTimeout)
            {
                _pollTimer?.Stop();
                SetStatus(StatusKind.Error, L.Get("WebStatusTimeout"));
                return;
            }

            var snapshot = await SnapshotAsync();
            if (snapshot is null) return;
            var candidate = snapshot.Candidate;
            bool fresh = snapshot.Count > _baselineCount || !string.Equals(candidate, _baselineCandidate, StringComparison.Ordinal);
            if (snapshot.Busy || !fresh || snapshot.SinceMutationMs < 1500 || candidate.Length == 0)
            {
                _stableTicks = 0;
                _lastCandidate = candidate;
                return;
            }

            _stableTicks = string.Equals(candidate, _lastCandidate, StringComparison.Ordinal) ? _stableTicks + 1 : 0;
            _lastCandidate = candidate;
            if (_stableTicks < 1) return;

            if (Capture(candidate, "DOM")) return;
            // Код-блок без JSON сцены: пробуем весь текст ответа.
            if (snapshot.Code.Length > 0 && Capture(snapshot.Text, "DOM")) return;
        }
        finally
        {
            _polling = false;
        }
    }

    private bool Capture(string text, string source, bool force = false)
    {
        if (_bridge is null || !_bridge.TryCapture(text, source, _site, force)) return false;
        _pollTimer?.Stop();
        _active = null;
        SetStatus(StatusKind.Success, L.Format("WebStatusCaptured", source));
        return true;
    }

    // ───────────────────────────── Перехватчик 2: буфер обмена ─────────────────────────────

    /// <summary>Пользователь нажал «Copy code» в веб-чате — JSON из буфера обмена применяется мгновенно.</summary>
    private async void OnClipboardChanged(object? sender, object e)
    {
        if (_bridge?.Pending is null) return;
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;
            var text = await view.GetTextAsync();
            if (string.Equals(text, _lastCopiedPrompt, StringComparison.Ordinal)) return;
            DispatcherQueue.TryEnqueue(() => Capture(text, L.IsRussian ? "буфер обмена" : "clipboard"));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Буфер занят другим приложением — следующий ContentChanged придёт позже.
        }
    }

    // ───────────────────────────── Кнопки ─────────────────────────────

    /// <summary>«Применить JSON из ответа»: последний ответ со страницы, иначе — содержимое буфера обмена.</summary>
    private async void OnApplyJsonClick(object sender, RoutedEventArgs e)
    {
        var snapshot = await SnapshotAsync();
        if (snapshot is not null && (Capture(snapshot.Code, "DOM", force: true) || Capture(snapshot.Text, "DOM", force: true))) return;

        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                var text = await view.GetTextAsync();
                if (!string.Equals(text, _lastCopiedPrompt, StringComparison.Ordinal) &&
                    Capture(text, L.IsRussian ? "буфер обмена" : "clipboard", force: true)) return;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Буфер обмена недоступен — сообщаем, что JSON не найден.
        }
        SetStatus(StatusKind.Error, L.Get("WebStatusNoJson"));
    }

    private void OnCopyPromptClick(object sender, RoutedEventArgs e)
    {
        var prompt = _bridge?.Pending?.Prompt ?? _active?.Prompt;
        if (prompt is not null) CopyText(prompt);
    }

    private async void OnResendClick(object sender, RoutedEventArgs e)
    {
        var session = _bridge?.Pending ?? _active;
        if (session is null) return;
        try
        {
            await SendSessionAsync(session);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("web-bridge-resend", ex);
            SetStatus(StatusKind.Error, ex.Message);
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (_webReady) Web.CoreWebView2.Reload();
        else _ = OpenAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.IsWebBridgeOpen = false;
    }

    // ───────────────────────────── Буфер обмена ─────────────────────────────

    private void CopyText(string text)
    {
        try
        {
            _lastCopiedPrompt = text;
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            App.WriteCrashLog("clipboard", ex);
        }
    }

    private static async Task CopyImageAsync(byte[] image)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(image);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            App.WriteCrashLog("clipboard-image", ex);
        }
    }
}
