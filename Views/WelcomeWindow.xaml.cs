using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AIArchitectStudio.Views;

/// <summary>Недавний проект в сайдбаре стартового окна.</summary>
public sealed record RecentProjectItem(string Path, string Name, string Folder);

/// <summary>Плитка шаблона: пресет <see cref="PresetScenesService"/>, подписи, бейдж, глиф и градиент превью.</summary>
public sealed record WelcomeTemplate(string PresetId, string TitleKey, string HintKey, string BadgeKey, string Glyph, string FromHex, string ToHex);

/// <summary>Карточка-чекбокс модуля на шаге 2 онбординга: подписи, глиф и цвет (как в «Настройки → Модули и Плагины»), чтение и запись флага.</summary>
public sealed record OnboardingModule(string TitleKey, string HintKey, string Glyph, string ColorHex,
    Func<WorkspaceModules, bool> Get, Func<WorkspaceModules, bool, WorkspaceModules> Set);

/// <summary>
/// Стартовое окно 960 × 620 без рамки: «Новый проект», «Открыть», «Настройки», недавние проекты, шаблоны-плитки
/// (коттедж, каркасный корпус, спиральная башня, купольный павильон) и онбординг «Укажите ваш опыт в проектировании». Выбор возвращается в App
/// событием <see cref="Completed"/>; App открывает главное окно и закрывает это.
/// </summary>
public sealed partial class WelcomeWindow : Window
{
    public static readonly IReadOnlyList<WelcomeTemplate> Templates =
    [
        new(PresetScenesService.Cottage, "TemplateCottage", "TemplateCottageHint", "TemplateCottageBadge", "", "#34C759", "#0E8C7A"),
        new(PresetScenesService.Frame, "TemplateFrame", "TemplateFrameHint", "TemplateFrameBadge", "", "#0A84FF", "#5E5CE6"),
        new(PresetScenesService.Tower, "TemplateTower", "TemplateTowerHint", "TemplateTowerBadge", "", "#64D2FF", "#2C4BFF"),
        new(PresetScenesService.Pavilion, "TemplatePavilion", "TemplatePavilionHint", "TemplatePavilionBadge", "", "#FF9F0A", "#FF375F")
    ];

    /// <summary>Модули шага 2 мастера — в порядке «Настройки → Модули и Плагины».</summary>
    public static readonly IReadOnlyList<OnboardingModule> Modules =
    [
        new("ModuleInsolation", "ModuleInsolationHint", "", "#FF9F0A", m => m.Insolation, (m, v) => m with { Insolation = v }),
        new("ModuleAnalysis", "ModuleAnalysisHint", "", "#FF375F", m => m.StructuralAnalysis, (m, v) => m with { StructuralAnalysis = v }),
        new("ModuleRebar", "ModuleRebarHint", "", "#FF7A45", m => m.Reinforcement3D, (m, v) => m with { Reinforcement3D = v }),
        new("ModuleQuantities", "ModuleQuantitiesHint", "", "#30D158", m => m.QuantitySchedule, (m, v) => m with { QuantitySchedule = v }),
        new("ModuleClash", "ModuleClashHint", "", "#5E5CE6", m => m.ClashInspector, (m, v) => m with { ClashInspector = v })
    ];

    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly MainViewModel _viewModel;
    private readonly bool _forceOnboarding;
    private UserExperienceProfile _pendingProfile;
    private WorkspaceModules _pendingModules = new();
    private int _onboardingStep = 1;
    private bool _stepAnimating;
    private bool _completed;
    private bool _dialogOpen;
    private bool _syncingLanguage;

    public LocalizationService L { get; }

    /// <summary>Пользователь выбрал, с чего начать (окно гаснет до вызова).</summary>
    public event EventHandler<StartupChoice>? Completed;

    public WelcomeWindow(SettingsService settings, ThemeService theme, LocalizationService loc, MainViewModel viewModel, bool forceOnboarding)
    {
        _settings = settings;
        _theme = theme;
        _viewModel = viewModel;
        _forceOnboarding = forceOnboarding;
        L = loc;
        InitializeComponent();
        Title = AppInfo.ProductName;
        WindowChrome.ConfigureBorderless(this, 960, 620, minimizable: true);
        WindowChrome.ApplyTheme(this, theme);
        SetTitleBar(DragArea);

        VersionText.Text = loc.Format("VersionLabel", AppInfo.VersionText);
        _syncingLanguage = true;
        (loc.IsRussian ? LangRu : LangEn).IsChecked = true;
        _syncingLanguage = false;
        RefreshTexts();
        RefreshRecent();
        BuildTemplateCards();
        InitializeWave6();
        Root.Loaded += OnRootLoaded;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        var cards = TemplateGrid.Children.OfType<UIElement>().ToList();
        foreach (var card in cards) card.Opacity = 0;
        // Начальный фокус — программный: без рамки клавиатурного фокуса на первой кнопке.
        FocusSink.Focus(FocusState.Programmatic);
        WindowChrome.Fade(Root, 0, 1, 0.97, 1, 520).Begin();
        bool onboarding = _forceOnboarding || !_settings.Current.OnboardingCompleted;
#if DEBUG
        // AIA_DEBUG_ONBOARDING=0 — стартовое окно без онбординга (съёмка шаблонов), =1 — онбординг принудительно (App).
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_ONBOARDING") == "0") onboarding = false;
#endif
        if (onboarding)
        {
            // Онбординг поверх: основной слой ждёт выбора профиля.
            MainLayer.Opacity = 0;
            foreach (var card in cards) card.Opacity = 1;
            ShowOnboarding();
        }
        else
        {
            for (int i = 0; i < cards.Count; i++)
                WindowChrome.Fade(cards[i], 0, 1, 0.94, 1, 560, 140 + i * 70).Begin();
        }
#if DEBUG
        DebugAutoPick();
#endif
    }

#if DEBUG
    /// <summary>
    /// AIA_DEBUG_WELCOMEPICK=cottage|frame|tower|pavilion|empty — выбрать плитку без клика (проверка перехода в редактор);
    /// AIA_DEBUG_ONBOARDING=2 — мастер сразу на шаге 2: уровень «Архитектор» и дополнительно отмеченное 3D-армирование.
    /// </summary>
    private async void DebugAutoPick()
    {
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_ONBOARDING") == "2")
        {
            await Task.Delay(700);
            _pendingProfile = UserExperienceProfile.Architect;
            UpdateProfileCards();
            await ShowModulesStepAsync();
            var rebar = Modules.First(m => m.TitleKey == "ModuleRebar");
            _pendingModules = rebar.Set(_pendingModules, true);
            UpdateModuleCards();
        }
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_WELCOMEPICK") is not { Length: > 0 } pick) return;
        await Task.Delay(1600);
        await CompleteAsync(pick == "empty" ? StartupChoice.Empty : StartupChoice.Preset(pick));
    }
#endif

    // ───────────────────────────── Тексты, шаблоны, недавние ─────────────────────────────

    private void RefreshTexts()
    {
        int hour = DateTime.Now.Hour;
        GreetingText.Text = L.Get(hour switch
        {
            >= 5 and < 12 => "GreetingMorning",
            >= 12 and < 18 => "GreetingDay",
            >= 18 and < 23 => "GreetingEvening",
            _ => "GreetingNight"
        });
        var profile = _settings.Current.ExperienceProfile;
        ProfileGlyph.Glyph = UserExperienceProfiles.Glyph(profile);
        ProfileGlyph.Foreground = XamlHelpers.HexBrush(UserExperienceProfiles.ColorHex(profile));
        ProfileTitleText.Text = L.Get(UserExperienceProfiles.TitleKey(profile));
    }

    private void RefreshRecent()
    {
        var items = _settings.Current.RecentProjects
            .Where(File.Exists)
            .Select(p => new RecentProjectItem(p, Path.GetFileNameWithoutExtension(p), Path.GetDirectoryName(p) ?? string.Empty))
            .ToList();
        RecentList.ItemsSource = items;
        RecentEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Плитки шаблонов 2 × 2: превью (снимок «Белого макета» из Assets\Templates, иначе градиент с глифом), бейдж, название, описание.</summary>
    private void BuildTemplateCards()
    {
        TemplateGrid.Children.Clear();
        for (int i = 0; i < Templates.Count; i++)
        {
            var template = Templates[i];
            var card = new Button
            {
                Style = (Style)Root.Resources["TemplateCardStyle"],
                Tag = template.PresetId,
                Content = BuildCardContent(template)
            };
            ToolTipService.SetToolTip(card, L.Get(template.HintKey));
            card.Click += OnTemplateClick;
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            TemplateGrid.Children.Add(card);
        }
    }

    private Grid BuildCardContent(WelcomeTemplate template)
    {
        var root = new Grid { RowSpacing = 0 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var preview = new Grid { CornerRadius = new CornerRadius(12, 12, 0, 0), MinHeight = 108 };
        preview.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = WithAlpha(ThemeService.FromHex(template.FromHex), 0x55) },
                new GradientStop { Offset = 1, Color = WithAlpha(ThemeService.FromHex(template.ToHex), 0x30) }
            }
        };
        string thumbnail = Path.Combine(AppContext.BaseDirectory, "Assets", "Templates", template.PresetId + ".png");
        if (File.Exists(thumbnail))
            preview.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(thumbnail)),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(10, 8, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        else
            preview.Children.Add(new FontIcon { Glyph = template.Glyph, FontSize = 40, Foreground = XamlHelpers.HexBrush(template.FromHex), Opacity = 0.9 });

        var badge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)Application.Current.Resources["IslandBrush"],
            Child = new TextBlock { FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Text = L.Get(template.BadgeKey) }
        };
        preview.Children.Add(badge);
        root.Children.Add(preview);

        var text = new StackPanel { Padding = new Thickness(14, 10, 14, 12), Spacing = 2 };
        text.Children.Add(new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Text = L.Get(template.TitleKey) });
        text.Children.Add(new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            Text = L.Get(template.HintKey)
        });
        Grid.SetRow(text, 1);
        root.Children.Add(text);
        return root;
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    // ───────────────────────────── Действия ─────────────────────────────

    private async void OnTemplateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string presetId }) await CompleteAsync(StartupChoice.Preset(presetId));
    }

    private async void OnNewProjectClick(object sender, RoutedEventArgs e) => await CompleteAsync(StartupChoice.Empty);

    private async void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && File.Exists(path)) await CompleteAsync(StartupChoice.Open(path));
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        foreach (var ext in new[] { SceneProjectFile.Extension, ".ifc", ".dxf" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is { } file) await CompleteAsync(StartupChoice.Open(file.Path));
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || Root.XamlRoot is null) return;
        _dialogOpen = true;
        try
        {
            var dialog = new SettingsDialog(_settings, L, _viewModel.ModelRegistry,
                App.Services.GetRequiredService<Services.AI.AiKeyInspectorService>(), m => _viewModel.ModelLabel(m))
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = _theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.SaveError is null)
            {
                _viewModel.ApplySettingsFromDialog();
                WindowChrome.ApplyTheme(this, _theme);
                SyncLanguageButtons();
                RefreshTexts();
                BuildTemplateCards();
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingLanguage) return;
        string language = ReferenceEquals(sender, LangEn) ? LocalizationService.English : LocalizationService.Russian;
        if (L.Language == language) return;
        L.Language = language;
        try
        {
            _settings.Update(s => s.Language = language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.WriteCrashLog("welcome-language", ex);
        }
        RefreshTexts();
        BuildTemplateCards();
    }

    private void SyncLanguageButtons()
    {
        _syncingLanguage = true;
        (L.IsRussian ? LangRu : LangEn).IsChecked = true;
        _syncingLanguage = false;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

#if DEBUG
    /// <summary>Только Debug: выбор без клика (автотест кнопки «Начальный экран» редактора).</summary>
    internal Task DebugCompleteAsync(StartupChoice choice) => CompleteAsync(choice);
#endif

    /// <summary>Окно гаснет и сообщает выбор; повторные клики во время анимации игнорируются.</summary>
    private async Task CompleteAsync(StartupChoice choice)
    {
        if (_completed) return;
        _completed = true;
        Root.IsHitTestVisible = false;
        await WindowChrome.PlayAsync(WindowChrome.Fade(Root, 1, 0, 1, 0.985, 220));
        Completed?.Invoke(this, choice);
    }

    // ───────────────────────────── Онбординг ─────────────────────────────

    private void OnProfileClick(object sender, RoutedEventArgs e) => ShowOnboarding();

    private void ShowOnboarding()
    {
        _pendingProfile = _settings.Current.ExperienceProfile;
        _pendingModules = _settings.Current.EffectiveModules;
        _onboardingStep = 1;
        UpdateProfileCards();
        ApplyStepChrome();
        ProfileCardsGrid.Visibility = Visibility.Visible;
        ProfileCardsGrid.Opacity = 1;
        ModuleGrid.Visibility = Visibility.Collapsed;
        MainLayer.IsHitTestVisible = false;
        WindowChrome.Fade(MainLayer, MainLayer.Opacity, 0, 1, 0.98, 240).Begin();
        OnboardingOverlay.Visibility = Visibility.Visible;
        WindowChrome.Fade(OnboardingOverlay, 0, 1, 1, 1, 260).Begin();
        WindowChrome.Fade(OnboardingContent, 0, 1, 0.96, 1, 520, 60).Begin();
        int i = 0;
        foreach (var card in new UIElement[] { ProfileBeginnerCard, ProfileArchitectCard, ProfileEngineerCard })
            WindowChrome.Fade(card, 0, 1, 0.92, 1, 520, 160 + 80 * i++).Begin();
    }

    /// <summary>Шаг 1: клик по профилю выделяет карточку и сразу ведёт ко второму шагу — выбору модулей.</summary>
    private async void OnProfileCardClick(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep != 1 || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<UserExperienceProfile>(tag, out var profile)) return;
        _pendingProfile = profile;
        UpdateProfileCards();
        await Task.Delay(160);
        await ShowModulesStepAsync();
    }

    /// <summary>Заголовок, подзаголовок, индикатор «Шаг N из 2» и подписи кнопок текущего шага мастера.</summary>
    private void ApplyStepChrome()
    {
        bool modules = _onboardingStep == 2;
        StepText.Text = L.Format("OnboardingStep", _onboardingStep, 2);
        StepDot1.Width = modules ? 6 : 18;
        StepDot2.Width = modules ? 18 : 6;
        StepDot1.Background = (Brush)Application.Current.Resources[modules ? "StudioBorderBrush" : "AccentBrush"];
        StepDot2.Background = (Brush)Application.Current.Resources[modules ? "AccentBrush" : "StudioBorderBrush"];
        OnboardingTitleText.Text = L.Get(modules ? "OnboardingModulesTitle" : "OnboardingTitle");
        OnboardingSubtitleText.Text = modules
            ? L.Format("OnboardingModulesSubtitle", L.Get(UserExperienceProfiles.TitleKey(_pendingProfile)))
            : L.Get("OnboardingSubtitle");
        OnboardingContinueButton.Content = L.Get(modules ? "OnboardingStart" : "OnboardingContinue");
        OnboardingBackButton.Visibility = modules ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Переход к шагу 2: галочки модулей — по рекомендации выбранного уровня, карточки появляются каскадом.</summary>
    private async Task ShowModulesStepAsync()
    {
        if (_stepAnimating || _onboardingStep == 2) return;
        _stepAnimating = true;
        try
        {
            _pendingModules = WorkspaceModules.ForProfile(_pendingProfile);
            await WindowChrome.PlayAsync(WindowChrome.Fade(ProfileCardsGrid, 1, 0, 1, 0.97, 180));
            ProfileCardsGrid.Visibility = Visibility.Collapsed;
            _onboardingStep = 2;
            ApplyStepChrome();
            BuildModuleCards();
            ModuleGrid.Visibility = Visibility.Visible;
            int i = 0;
            foreach (var card in ModuleGrid.Children.OfType<UIElement>())
                WindowChrome.Fade(card, 0, 1, 0.94, 1, 420, 40 + 55 * i++).Begin();
        }
        finally
        {
            _stepAnimating = false;
        }
    }

    private async void OnOnboardingBackClick(object sender, RoutedEventArgs e)
    {
        if (_stepAnimating || _onboardingStep != 2) return;
        _stepAnimating = true;
        try
        {
            await WindowChrome.PlayAsync(WindowChrome.Fade(ModuleGrid, 1, 0, 1, 0.97, 180));
            ModuleGrid.Visibility = Visibility.Collapsed;
            ModuleGrid.Opacity = 1;
            _onboardingStep = 1;
            ApplyStepChrome();
            ProfileCardsGrid.Visibility = Visibility.Visible;
            await WindowChrome.PlayAsync(WindowChrome.Fade(ProfileCardsGrid, 0, 1, 0.97, 1, 260));
        }
        finally
        {
            _stepAnimating = false;
        }
    }

    /// <summary>Пять карточек-чекбоксов модулей и шестая — сводка выбора с кнопкой «Как рекомендовано».</summary>
    private void BuildModuleCards()
    {
        ModuleGrid.Children.Clear();
        for (int i = 0; i < Modules.Count; i++)
        {
            var module = Modules[i];
            var card = new Button
            {
                Style = (Style)Root.Resources["ProfileCardStyle"],
                Padding = new Thickness(14, 12, 14, 12),
                Tag = module,
                Content = BuildModuleCardContent(module)
            };
            AutomationProperties.SetName(card, L.Get(module.TitleKey));
            card.Click += OnModuleCardClick;
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            ModuleGrid.Children.Add(card);
        }

        var summary = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"]
        };
        var panel = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Name = "ModuleCountText" });
        panel.Children.Add(new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            Text = L.Get(UserExperienceProfiles.TitleKey(_pendingProfile))
        });
        var reset = new Button
        {
            Content = L.Get("OnboardingModulesReset"),
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 5),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = (Brush)Application.Current.Resources["AccentSoftBrush"],
            Foreground = (Brush)Application.Current.Resources["AccentBrush"]
        };
        reset.Click += (_, _) =>
        {
            _pendingModules = WorkspaceModules.ForProfile(_pendingProfile);
            UpdateModuleCards();
        };
        panel.Children.Add(reset);
        summary.Child = panel;
        Grid.SetColumn(summary, 1);
        Grid.SetRow(summary, 2);
        ModuleGrid.Children.Add(summary);
        UpdateModuleCards();
    }

    private Grid BuildModuleCardContent(OnboardingModule module)
    {
        var root = new Grid { ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        root.Children.Add(new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Top,
            Background = XamlHelpers.HexBrush(module.ColorHex),
            Child = new FontIcon { Glyph = module.Glyph, FontSize = 15, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
        });

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Text = L.Get(module.TitleKey) });
        text.Children.Add(new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            Text = L.Get(module.HintKey)
        });
        Grid.SetColumn(text, 1);
        root.Children.Add(text);

        // Круглый чекбокс в стиле iOS: акцентный круг с галочкой или пустое кольцо.
        var check = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
        };
        Grid.SetColumn(check, 2);
        root.Children.Add(check);
        return root;
    }

    private void OnModuleCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OnboardingModule module }) return;
        _pendingModules = module.Set(_pendingModules, !module.Get(_pendingModules));
        UpdateModuleCards();
    }

    /// <summary>Выбранные модули — акцентная рамка, подложка и заполненный круг с галочкой; сводка «Выбрано модулей: N из 5».</summary>
    private void UpdateModuleCards()
    {
        var recommended = WorkspaceModules.ForProfile(_pendingProfile);
        int selected = 0;
        foreach (var card in ModuleGrid.Children.OfType<Button>())
        {
            if (card.Tag is not OnboardingModule module) continue;
            bool on = module.Get(_pendingModules);
            if (on) selected++;
            card.Background = (Brush)Application.Current.Resources[on ? "AccentSoftBrush" : "TrackBrush"];
            card.BorderBrush = (Brush)Application.Current.Resources[on ? "AccentBrush" : "StudioBorderBrush"];
            card.BorderThickness = new Thickness(on ? 2 : 1);
            if (card.Content is Grid { Children: [_, _, Border check] })
            {
                check.Background = on ? (Brush)Application.Current.Resources["AccentBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                check.BorderBrush = (Brush)Application.Current.Resources[on ? "AccentBrush" : "TextSecondaryBrush"];
                check.Child.Opacity = on ? 1 : 0;
            }
            ToolTipService.SetToolTip(card, module.Get(recommended) ? L.Get("OnboardingModulesRecommended") : null);
        }
        if (ModuleGrid.Children.OfType<Border>().FirstOrDefault()?.Child is StackPanel { Children: [TextBlock count, ..] })
            count.Text = L.Format("OnboardingModulesCount", selected, Modules.Count);
    }

    /// <summary>Выбранная карточка — акцентная подложка и рамка, остальные — матовые.</summary>
    private void UpdateProfileCards()
    {
        foreach (var (card, profile) in new[]
                 {
                     (ProfileBeginnerCard, UserExperienceProfile.Beginner),
                     (ProfileArchitectCard, UserExperienceProfile.Architect),
                     (ProfileEngineerCard, UserExperienceProfile.StructuralEngineer)
                 })
        {
            bool selected = profile == _pendingProfile;
            card.Background = (Brush)Application.Current.Resources[selected ? "AccentSoftBrush" : "TrackBrush"];
            card.BorderBrush = (Brush)Application.Current.Resources[selected ? "AccentBrush" : "StudioBorderBrush"];
            card.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    private async void OnOnboardingContinueClick(object sender, RoutedEventArgs e)
    {
        if (_stepAnimating) return;
        if (_onboardingStep == 1)
        {
            await ShowModulesStepAsync();
            return;
        }

        var profile = _pendingProfile;
        var modules = _pendingModules;
        try
        {
            // Уровень опыта и выбранный на шаге 2 набор модулей; дальше — «Настройки → Модули и Плагины».
            _settings.Update(s =>
            {
                s.ExperienceProfile = profile;
                s.Modules = modules;
                s.OnboardingCompleted = true;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.WriteCrashLog("onboarding-save", ex);
        }
        _viewModel.SelectProfile(profile, modules);
        RefreshTexts();
        await WindowChrome.PlayAsync(WindowChrome.Fade(OnboardingOverlay, 1, 0, 1, 1.02, 240));
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        MainLayer.IsHitTestVisible = true;
        await WindowChrome.PlayAsync(WindowChrome.Fade(MainLayer, 0, 1, 0.98, 1, 420));
        FocusSink.Focus(FocusState.Programmatic);
    }
}
