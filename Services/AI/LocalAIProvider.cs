using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;

namespace AIArchitectStudio.Services.AI;

/// <summary>
/// Локальный ИИ на LLamaSharp: бэкенд Vulkan с автоматическим откатом на CPU, модели Qwen 2.5 Coder 3B/7B (GGUF)
/// из папки LocalModels. Веса хранятся в единственном экземпляре; при смене модели — Dispose() + GC.Collect().
/// </summary>
public sealed class LocalAIProvider(SettingsService settings) : IAiProvider, IDisposable
{
    public const uint ContextSize = 2048;
    public const int GpuLayers = 99;
    public const float Temperature = 0.05f;

    private static readonly object NativeSync = new();
    private static bool _nativeConfigured;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private ModelParams? _params;
    private string? _loadedPath;

    /// <summary>Фактический бэкенд после загрузки: «Vulkan» или «CPU».</summary>
    public string BackendName { get; private set; } = "—";

    public string? LoadedModelFile => _loadedPath is null ? null : Path.GetFileName(_loadedPath);

    public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken)
    {
        if (request.HasImage)
            throw new AiProviderException("AIA-LOCAL-VISION",
                "Локальная модель Qwen 2.5 Coder работает только с текстом. Для режимов «Из картинки» выберите Gemini, ChatGPT, Claude или веб-аккаунт.");

        var path = ResolveModelPath(request.Model.ModelId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            await Task.Run(() => EnsureLoaded(path), cancellationToken).ConfigureAwait(false);

            var prompt = BuildChatMl(request.SystemPrompt, request.UserPrompt);
            int promptTokens = _weights!.Tokenize(prompt, true, true, Encoding.UTF8).Length;
            int available = (int)ContextSize - promptTokens - 8;
            if (available < 256)
                throw new AiProviderException("AIA-LOCAL-CONTEXT",
                    $"Запрос слишком длинный для контекста локальной модели ({promptTokens} из {ContextSize} токенов). Сократите описание или выберите облачную модель.");

            var inference = new InferenceParams
            {
                MaxTokens = available,
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    // Роли Совета КБ задают свою температуру (архитектор — выше), одиночный режим — детерминированные 0.05.
                    Temperature = request.Temperature > 0.1 ? (float)Math.Min(1.0, request.Temperature) : Temperature,
                    // Грамматика GBNF: только валидный компактный JSON, генерация завершается сразу после закрытия объекта.
                    Grammar = new Grammar(JsonGrammar, "root")
                }
            };

            var executor = new StatelessExecutor(_weights, _params!);
            var text = new StringBuilder();
            await Task.Run(async () =>
            {
                await foreach (var token in executor.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
                    text.Append(token);
            }, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            var result = text.ToString().Replace("<|im_end|>", string.Empty).Replace("<|endoftext|>", string.Empty).Trim();
            if (result.Length == 0)
                throw new AiProviderException("AIA-EMPTY", "Локальная модель вернула пустой ответ. Повторите запрос или выберите модель 7B.");
            return new AiResponse(result, $"{request.Model.DisplayName} · {BackendName}", sw.Elapsed, BackendName, promptTokens, null);
        }
        catch (Exception ex) when (ex is not (AiProviderException or OperationCanceledException))
        {
            throw new AiProviderException("AIA-LOCAL-002",
                $"Сбой локальной модели ({BackendName}): {ex.Message}", ex.ToString(), ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Выгружает веса из памяти (смена модели или переход на облачного провайдера).</summary>
    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Выход приложения. Если окно закрыли во время генерации, веса не освобождаются под работающим llama.cpp
    /// (освобождение памяти модели во время инференса — обращение нативного кода к освобождённой памяти); их вернёт
    /// система при завершении процесса. Семафор не освобождается: незавершённая генерация ещё вызовет Release.
    /// </summary>
    public void Dispose()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            Unload();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveModelPath(string fileName)
    {
        var dir = settings.ResolveLocalModelsDirectory() ?? AiModelCatalog.DefaultLocalModelsDirectory;
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
            throw new AiProviderException("AIA-LOCAL-001",
                $"Файл локальной модели не найден: {path}. Проверьте папку локальных моделей в «Настройках».");
        return path;
    }

    private void EnsureLoaded(string path)
    {
        ConfigureNativeLibrary();
        if (_weights is not null && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase)) return;

        Unload();
        _params = new ModelParams(path) { ContextSize = ContextSize, GpuLayerCount = GpuLayers };
        try
        {
            _weights = LLamaWeights.LoadFromFile(_params);
        }
        catch (Exception ex) when (ex is LoadWeightsFailedException or RuntimeError)
        {
            // Видеопамяти не хватило или Vulkan-устройство не поддерживает выгрузку слоёв — полностью на CPU.
            _params = new ModelParams(path) { ContextSize = ContextSize, GpuLayerCount = 0 };
            _weights = LLamaWeights.LoadFromFile(_params);
        }
        _loadedPath = path;
        BackendName = _params.GpuLayerCount > 0 && NativeApi.llama_supports_gpu_offload() ? "Vulkan" : "CPU";
    }

    private void Unload()
    {
        if (_weights is null) return;
        _weights.Dispose();
        _weights = null;
        _params = null;
        _loadedPath = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Однократная настройка нативной библиотеки до первого вызова llama.cpp: Vulkan, затем CPU.</summary>
    private static void ConfigureNativeLibrary()
    {
        lock (NativeSync)
        {
            if (_nativeConfigured) return;
            _nativeConfigured = true;
            if (NativeLibraryConfig.LLama.LibraryHasLoaded) return;
            NativeLibraryConfig.All
                .WithCuda(false)
                .WithVulkan(true)
                .WithAutoFallback(true)
                .WithLogCallback((_, _) => { });
        }
    }

    /// <summary>JSON-грамматика llama.cpp с компактными пробелами (экономит токены контекста 2048).</summary>
    private const string JsonGrammar = """
root   ::= object
value  ::= object | array | string | number | ("true" | "false" | "null") ws
object ::= "{" ws ( string ":" ws value ("," ws string ":" ws value)* )? "}" ws
array  ::= "[" ws ( value ("," ws value)* )? "]" ws
string ::= "\"" ( [^"\\\x7F\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]) )* "\"" ws
number ::= "-"? ([0-9] | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [-+]? [0-9]+)? ws
ws     ::= | " " | "\n"
""";

    /// <summary>Шаблон ChatML, которым обучены модели Qwen 2.5.</summary>
    private static string BuildChatMl(string system, string user) =>
        $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n";
}
