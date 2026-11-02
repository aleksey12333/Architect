using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Локальный ИИ-движок Ollama в ViewModel: строка статуса «Подключение локального ИИ-движка… → Движок готов» над
/// ИИ-доком, статус в «Составе КБ», обновление списка моделей и переход со встроенных весов LLamaSharp на модели сервера.
/// </summary>
public sealed partial class MainViewModel
{
    private OllamaProcessManager? _ollama;
    private bool _ollamaSwitchDone;
    private int _ollamaToastVersion;

    public OllamaProcessManager? Ollama => _ollama;

    private void InitializeOllama(OllamaProcessManager ollama)
    {
        _ollama = ollama;
        ollama.StateChanged += (_, _) => RunOnUi(OnOllamaStateChanged);
        OnOllamaStateChanged();
    }

    /// <summary>Подключение или запуск идёт (индикатор в пилюле статуса).</summary>
    [ObservableProperty] public partial bool IsOllamaBusy { get; set; }

    [ObservableProperty] public partial bool IsOllamaReady { get; set; }

    /// <summary>Пилюля статуса над доком видна во время подключения и несколько секунд после готовности или сбоя.</summary>
    [ObservableProperty] public partial bool IsOllamaToastVisible { get; set; }

    [ObservableProperty] public partial string OllamaStatusText { get; set; } = string.Empty;

    [ObservableProperty] public partial string OllamaStatusDetail { get; set; } = string.Empty;

    /// <summary>Цвет точки статуса: серый — неизвестно, синий — подключение, зелёный — готов, янтарный — нет установки, алый — сбой.</summary>
    [ObservableProperty] public partial string OllamaStatusColorHex { get; set; } = "#8E8E93";

    /// <summary>Короткая подпись для «Состава КБ»: «Ollama 0.40.5 · моделей 7».</summary>
    [ObservableProperty] public partial string OllamaShortText { get; set; } = "Ollama";

    private void OnOllamaStateChanged()
    {
        if (_ollama is not { } o) return;
        RefreshOllamaTexts();
        var state = o.State;
        int version = ++_ollamaToastVersion;
        IsOllamaToastVisible = state != OllamaEngineState.Unknown;
        if (!IsOllamaBusy) _ = HideOllamaToastAsync(version, state == OllamaEngineState.Ready ? 4500 : 9000);
        if (state == OllamaEngineState.Ready) PreferOllamaModel();
    }

    /// <summary>Подписи статуса на текущем языке (без показа пилюли).</summary>
    private void RefreshOllamaTexts()
    {
        if (_ollama is not { } o) return;
        var state = o.State;
        IsOllamaBusy = state is OllamaEngineState.Connecting or OllamaEngineState.Starting;
        IsOllamaReady = state == OllamaEngineState.Ready;
        OllamaStatusText = state switch
        {
            OllamaEngineState.Connecting => Loc.Get("OllamaConnecting"),
            OllamaEngineState.Starting => Loc.Get("OllamaStarting"),
            OllamaEngineState.Ready => Loc.Format("OllamaReady", o.Models.Count),
            OllamaEngineState.NotInstalled => Loc.Get("OllamaNotInstalled"),
            OllamaEngineState.Failed => Loc.Get("OllamaFailed"),
            _ => Loc.Get("OllamaUnknown")
        };
        OllamaStatusDetail = o.Detail;
        OllamaStatusColorHex = state switch
        {
            OllamaEngineState.Ready => "#30D158",
            OllamaEngineState.Connecting or OllamaEngineState.Starting => "#0A84FF",
            OllamaEngineState.NotInstalled => "#FF9F0A",
            OllamaEngineState.Failed => "#FF453A",
            _ => "#8E8E93"
        };
        OllamaShortText = state == OllamaEngineState.Ready
            ? $"Ollama {o.Version ?? string.Empty} · {Loc.Format("OllamaModelsCount", o.Models.Count)}".Replace("  ", " ")
            : OllamaStatusText;
    }

    private async Task HideOllamaToastAsync(int version, int milliseconds)
    {
        await Task.Delay(milliseconds);
        if (version == _ollamaToastVersion) IsOllamaToastVisible = false;
    }

    /// <summary>
    /// Встроенные веса LLamaSharp заменяются моделью сервера Ollama: при первой готовности движка, если в доке выбран
    /// встроенный Qwen, выбирается лучшая установленная модель для JSON сцены (coder → не рассуждающая → любая, по размеру,
    /// помещающаяся в память). Выбор пользователя облачной или веб-модели не трогается.
    /// </summary>
    private void PreferOllamaModel()
    {
        if (_ollamaSwitchDone || _ollama is not { Models.Count: > 0 } o) return;
        _ollamaSwitchDone = true;
        if (!SelectedModel.IsLocal) return;
        double memory = OllamaProcessManager.TotalMemoryGb;
        var best = o.Models
            .OrderByDescending(m => memory <= 0 || m.SizeGb <= memory * 0.75)
            .ThenByDescending(m => m.ModelId.Contains("coder", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => !m.IsReasoning)
            .ThenByDescending(m => m.ParameterBillions)
            .First();
        SelectedAiModel = best.Id;
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("OllamaSwitched", best.ModelId)));
    }

    /// <summary>«Обновить» в «Составе КБ» и меню моделей: переподключение (с автозапуском) и перечитывание /api/tags.</summary>
    [RelayCommand]
    private async Task RefreshOllamaAsync()
    {
        if (_ollama is not { } o) return;
        try
        {
            if (o.IsReady) await o.RefreshModelsAsync();
            else await o.EnsureRunningAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            App.WriteCrashLog("ollama-refresh", ex);
        }
    }
}
