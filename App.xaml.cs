using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using AIArchitectStudio.ViewModels;
using AIArchitectStudio.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace AIArchitectStudio;

public partial class App : Application
{
    private Window? _window;

    /// <summary>DI-контейнер приложения.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static Window? MainWindowInstance => (Current as App)?._window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        UnhandledException += OnUnhandledException;
        // Фоновые сбои не теряются: исключения задач «запустил и забыл» и потоков пула — в crash.log.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("unobserved-task", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog("fatal", e.ExceptionObject as Exception);
    }

    private static readonly object CrashLogSync = new();

    /// <summary>Предел журнала: больше — текущий переименовывается в crash.old.log (журнал не растёт бесконечно).</summary>
    private const long CrashLogLimit = 2 * 1024 * 1024;

    /// <summary>
    /// Журнал необработанных ошибок: %LOCALAPPDATA%\AIArchitectStudio\crash.log. Пишут и фоновые потоки (расчёт, тени,
    /// Ollama) — запись под блокировкой, иначе одновременные записи теряются из-за совместного доступа к файлу.
    /// </summary>
    public static void WriteCrashLog(string kind, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIArchitectStudio");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "crash.log");
            lock (CrashLogSync)
            {
                if (File.Exists(path) && new FileInfo(path).Length > CrashLogLimit)
                    File.Move(path, Path.Combine(dir, "crash.old.log"), overwrite: true);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {kind}: {ex}\n\n");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Журнал — вспомогательная функция, её сбой не должен ронять приложение.
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SecretStore>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<SceneStateService>();

        // BIM: экспресс-расчёт конструктива (КЭ/карта напряжений), смета с ведомостью стали, проектные файлы .aia и экспорт IFC / DXF / OBJ.
        services.AddSingleton<StructuralAnalysisService>();
        services.AddSingleton<BimAnalyticsService>();
        services.AddSingleton<ProjectFileService>();
        services.AddSingleton<CadExportService>();

        // Двусторонний обмен: импорт DXF (подложка / 3D) и IFC (Renga / Revit / ArchiCAD), листы ГОСТ в PDF.
        services.AddSingleton<CadDxfImporterService>();
        services.AddSingleton<CadIfcImporterService>();
        services.AddSingleton<GostDrawingPdfExporter>();

        // ИИ: реестр моделей, анализатор ключей, провайдеры, C#-солвер и веб-мост.
        services.AddSingleton<AiModelRegistry>();
        services.AddSingleton<AiKeyInspectorService>();
        services.AddSingleton<ParametricSolverService>();
        services.AddSingleton<LocalAIProvider>();
        services.AddSingleton<GeminiAIProvider>();
        services.AddSingleton<OpenAiCompatibleProvider>();
        services.AddSingleton<ClaudeAIProvider>();
        // Локальный ИИ-сервер Ollama (Zero-Config): автозапуск ollama serve и потоковый провайдер /api/chat.
        services.AddSingleton<OllamaProcessManager>();
        services.AddSingleton<OllamaAIProvider>();
        services.AddSingleton<AIOrchestrator>();
        services.AddSingleton<WebAccountBridgeProvider>();

        // Мультиагентное КБ: шлюз моделей ролей (облако, локальный Qwen, веб-аккаунты); оркестратор Совета создаёт ViewModel.
        services.AddSingleton<AgentModelGateway>();

        services.AddSingleton<AppStartupOptions>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Запуск: тема по системе (первый запуск) → заставка с фоновой загрузкой → стартовое окно (шаблоны, онбординг) →
    /// главное окно с выбранной сценой. Debug-сценарии AIA_DEBUG_* (автотест, съёмка) идут сразу в главное окно с Пресетом 1.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = Services.GetRequiredService<SettingsService>();
        var theme = Services.GetRequiredService<ThemeService>();
        var loc = Services.GetRequiredService<LocalizationService>();
        loc.Language = settings.Current.Language;
        bool firstRun = settings.IsFirstRun;
#if DEBUG
        // AIA_DEBUG_FIRSTRUN=1 — как первый запуск (тема по системе без записи в settings.json, онбординг).
        bool simulateFirstRun = DebugEnv("FIRSTRUN") == "1";
        firstRun |= simulateFirstRun;
        if (DebugEnv("LANG") is LocalizationService.English or LocalizationService.Russian) loc.Language = DebugEnv("LANG")!;
#else
        const bool simulateFirstRun = false;
#endif
        PrepareTheme(settings, theme, firstRun, persist: !simulateFirstRun);

        if (!UseStartupShell())
        {
            OpenMainWindow(null);
            _ = RefreshApiKeysAsync();
            _ = StartOllamaAsync();
            return;
        }
        Services.GetRequiredService<AppStartupOptions>().StartupPresetId = null;
        _ = RunStartupShellSafeAsync(settings, theme, loc, forceOnboarding: firstRun);
    }

    /// <summary>
    /// Сбой оболочки (заставка, стартовое окно) не должен оставлять процесс без видимого окна: причина — в crash.log,
    /// дальше — главное окно с чистым проектом.
    /// </summary>
    private async Task RunStartupShellSafeAsync(SettingsService settings, ThemeService theme, LocalizationService loc, bool forceOnboarding)
    {
        try
        {
            await RunStartupShellAsync(settings, theme, loc, forceOnboarding);
        }
        catch (Exception ex)
        {
            WriteCrashLog("startup-shell", ex);
            if (_window is not null) return;
            await OpenEditorAsync(StartupChoice.Empty, null);
            // Недосозданное окно оболочки (сбой в конструкторе) не закрыть — выход вместе с главным окном.
            if (_window is not null) _window.Closed += (_, _) => Exit();
        }
    }

    /// <summary>
    /// Первый запуск (или «Следовать теме Windows»): Cupertino Light / Dark по ключу AppsUseLightTheme; затем тема применяется
    /// к кистям приложения до появления первого окна.
    /// </summary>
    private static void PrepareTheme(SettingsService settings, ThemeService theme, bool firstRun, bool persist)
    {
        if (firstRun || settings.Current.FollowSystemTheme)
        {
            string name = SystemThemeDetector.PreferredThemeName();
            if (!persist) settings.Current.ThemeName = name;
            else if (firstRun || settings.Current.ThemeName != name)
            {
                try
                {
                    settings.Update(s =>
                    {
                        s.ThemeName = name;
                        s.FollowSystemTheme = true;
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteCrashLog("theme-detect", ex);
                }
            }
        }
        theme.Apply(settings.Current.ThemeName);
    }

    /// <summary>Заставка и стартовое окно показываются всегда, кроме Debug-сценариев AIA_DEBUG_* (кроме переменных самой оболочки).</summary>
    private static bool UseStartupShell()
    {
#if DEBUG
        string[] shellKeys = ["AIA_DEBUG_FIRSTRUN", "AIA_DEBUG_ONBOARDING", "AIA_DEBUG_SPLASHHOLD", "AIA_DEBUG_WELCOMEPICK", "AIA_DEBUG_LANG", "AIA_DEBUG_PROFILE", "AIA_DEBUG_SHELL"];
        if (DebugEnv("SHELL") == "1") return true;
        foreach (var key in Environment.GetEnvironmentVariables().Keys.OfType<string>())
            if (key.StartsWith("AIA_DEBUG_", StringComparison.OrdinalIgnoreCase) && !shellKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                return false;
#endif
        return true;
    }

#if DEBUG
    private static string? DebugEnv(string name) => Environment.GetEnvironmentVariable("AIA_DEBUG_" + name);
#endif

    private async Task RunStartupShellAsync(SettingsService settings, ThemeService theme, LocalizationService loc, bool forceOnboarding)
    {
        var splash = new SplashWindow(theme, loc);
        splash.Activate();
        try
        {
            await LoadWithSplashAsync(splash);
        }
        catch (Exception ex)
        {
            WriteCrashLog("startup", ex);
        }

        var viewModel = Services.GetRequiredService<MainViewModel>();
#if DEBUG
        // ONBOARDING=1 — мастер с шага 1, =2 — сразу шаг 2 «Выберите необходимые модули» (съёмка).
        forceOnboarding |= DebugEnv("ONBOARDING") is "1" or "2";
        if (DebugEnv("PROFILE") is { Length: > 0 } profile)
            viewModel.SelectProfile(UserExperienceProfiles.Parse(profile, viewModel.ExperienceProfile));
#endif
        await splash.FadeOutAsync();
        if (!settings.Current.ShowWelcomeScreen && settings.Current.OnboardingCompleted && !forceOnboarding)
        {
            // Стартовое окно отключено в настройках: сразу чистый проект.
            await OpenEditorAsync(StartupChoice.Empty, null);
            splash.Close();
            return;
        }

        var welcome = new WelcomeWindow(settings, theme, loc, viewModel, forceOnboarding);
        welcome.Completed += async (_, choice) => await OpenEditorAsync(choice, welcome);
        welcome.Closed += (_, _) =>
        {
            // Окно закрыто без выбора — приложение завершается, запущенные им фоновые службы останавливаются.
            if (_window is null) ShutdownServices();
        };
        welcome.Activate();
        splash.Close();
    }

    /// <summary>
    /// Шаги заставки: каждая строка статуса держится не меньше заданного времени, тяжёлые службы создаются в UI-потоке
    /// (они запоминают его диспетчер), расчётные прогревы идут в пуле.
    /// </summary>
    private static async Task LoadWithSplashAsync(SplashWindow splash)
    {
        int hold = 0;
#if DEBUG
        // AIA_DEBUG_SPLASHHOLD=мс — задержать каждый шаг (съёмка анимации заставки).
        _ = int.TryParse(DebugEnv("SPLASHHOLD"), out hold);
#endif
        async Task Step(string key, Func<Task> work, int minimumMs)
        {
            var started = Environment.TickCount64;
            await splash.ShowStatusAsync(key);
            await work();
            int rest = minimumMs + hold - (int)(Environment.TickCount64 - started);
            if (rest > 0) await Task.Delay(rest);
        }

        // Запуск локального ИИ идёт в фоне дальше: ждём его не дольше шага заставки.
        var ollama = StartOllamaAsync();
        _ = RefreshApiKeysAsync();
        await Task.Delay(450);
        await Step("SplashStartingAi", () => Task.WhenAny(ollama, Task.Delay(900)), 750);
        await Step("SplashInit3D", () =>
        {
            Services.GetRequiredService<StructuralAnalysisService>();
            Services.GetRequiredService<BimAnalyticsService>();
            Services.GetRequiredService<MainViewModel>();
            return Task.CompletedTask;
        }, 650);
        await Step("SplashModules", () => Task.Run(() => global::AIArchitectStudio.Services.Verification.BimVerificationPipeline.CreateDefault()), 550);
        await Step("SplashWorkspace", () => Task.CompletedTask, 400);
    }

    /// <summary>Главное окно с выбранной в стартовом окне сценой; стартовое окно закрывается после появления редактора.</summary>
    private async Task OpenEditorAsync(StartupChoice choice, Window? welcome)
    {
        var viewModel = Services.GetRequiredService<MainViewModel>();
        if (choice.Kind == StartupChoiceKind.Preset && choice.PresetId is { } presetId) viewModel.LoadStartupScene(presetId);
        else if (choice.Kind == StartupChoiceKind.Empty) viewModel.StartEmptyProject();

        var window = OpenMainWindow(choice);
        welcome?.Close();
        if (choice is { Kind: StartupChoiceKind.OpenFile, FilePath: { } path } && window.Content is FrameworkElement root)
        {
            // Открытие файла — после загрузки окна: DXF может показать окно верификации, ему нужен XamlRoot.
            if (root.IsLoaded) await viewModel.OpenAnyAsync(path);
            else root.Loaded += async (_, _) => await viewModel.OpenAnyAsync(path);
        }
    }

    private MainWindow OpenMainWindow(StartupChoice? choice)
    {
        var window = Services.GetRequiredService<MainWindow>();
        _window = window;
        window.Closed += (_, _) => ShutdownServices();
        if (choice is not null) window.PrepareEntranceAnimation();
        window.Activate();
        return window;
    }

    private static void ShutdownServices()
    {
        Services.GetService<LocalAIProvider>()?.Dispose();
        // Сервер Ollama останавливается, только если его запустило приложение.
        Services.GetService<OllamaProcessManager>()?.Dispose();
    }

    /// <summary>Zero-Config: подключение к Ollama или скрытый запуск ollama serve в фоне (статус — в ИИ-доке).</summary>
    private static async Task StartOllamaAsync()
    {
        try
        {
            await Services.GetRequiredService<OllamaProcessManager>().EnsureRunningAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog("ollama-start", ex);
        }
    }

    /// <summary>При старте проверяет сохранённые ключи и подгружает облачные модели в ИИ-док (в фоне, без блокировки UI).</summary>
    private static async Task RefreshApiKeysAsync()
    {
        try
        {
            await Task.Run(() => Services.GetRequiredService<AiKeyInspectorService>().RefreshAllAsync());
        }
        catch (Exception ex)
        {
            WriteCrashLog("key-refresh", ex);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Ошибка UI-потока показывается в ErrorInfoBar вместо аварийного завершения.
        e.Handled = true;
        WriteCrashLog("unhandled", e.Exception);
        var vm = Services.GetService<MainViewModel>();
        vm?.ShowError("Необработанная ошибка", e.Message, $"AIA-UNHANDLED-{e.Exception?.HResult ?? 0:X8}", e.Exception?.ToString());
    }
}
