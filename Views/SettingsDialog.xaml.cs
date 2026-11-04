using AIArchitectStudio.Models;
using AIArchitectStudio.Services;
using AIArchitectStudio.Services.AI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIArchitectStudio.Views;

/// <summary>
/// Настройки в стиле iPadOS: разделы «Общие» (профиль работы, язык, стартовое окно, режим участка, расценки),
/// «Внешний вид» (тема как в Windows, плитки 5 тем), «Модули и Плагины» (независимые от квалификации тумблеры модулей
/// главного окна; смена уровня опыта подставляет его набор), «BIM-Координатор» (модель по умолчанию, локальный интеллект,
/// ключи облачных сервисов с автоопределением и проверкой) и «Хранилище» (папки моделей, файл настроек, недавние).
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private sealed record ProviderRow(AiProviderKind Provider, PasswordBox Box, FontIcon StatusGlyph, TextBlock StatusText);

    private readonly SettingsService _settings;
    private readonly AiModelRegistry _registry;
    private readonly AiKeyInspectorService _inspector;
    private readonly Func<AiModelOption, string> _label;
    private readonly Dictionary<AiProviderKind, ProviderRow> _rows = [];
    private readonly Dictionary<AiProviderKind, string> _storedKeys = [];
    private List<AiModelOption> _modelOptions = [];

    public LocalizationService L { get; }

    /// <summary>Ошибка сохранения (если была) — показывается в ErrorInfoBar главного окна.</summary>
    public Exception? SaveError { get; private set; }

    private readonly Action<double, double, int>? _applyNorms;

    /// <param name="applyNorms">«Применить к текущему проекту»: снег (кг/м²), ветер (кг/м²), класс сооружения 1–3; null — запись в настройки.</param>
    public SettingsDialog(SettingsService settings, LocalizationService localization, AiModelRegistry registry,
        AiKeyInspectorService inspector, Func<AiModelOption, string> label, Action<double, double, int>? applyNorms = null, Func<UIElement>? primaryModelCard = null)
    {
        _settings = settings;
        _registry = registry;
        _inspector = inspector;
        _label = label;
        _applyNorms = applyNorms;
        L = localization;
        InitializeComponent();
        // Основная локальная модель программы (рейтинг, автовыбор) — карточка главного окна в разделе «BIM-Координатор».
        if (primaryModelCard is not null) PrimaryModelHost.Child = primaryModelCard();
        BuildProviderRows();
        LoadValues();
    }

    // ───────────────────────────── Ключи провайдеров ─────────────────────────────

    private void BuildProviderRows()
    {
        _rows[AiProviderKind.Gemini] = new ProviderRow(AiProviderKind.Gemini, GeminiKeyBox, GeminiGlyph, GeminiStatus);
        _rows[AiProviderKind.OpenAI] = new ProviderRow(AiProviderKind.OpenAI, OpenAiKeyBox, OpenAiGlyph, OpenAiStatus);
        _rows[AiProviderKind.Anthropic] = new ProviderRow(AiProviderKind.Anthropic, AnthropicKeyBox, AnthropicGlyph, AnthropicStatus);
        _rows[AiProviderKind.OpenRouter] = new ProviderRow(AiProviderKind.OpenRouter, OpenRouterKeyBox, OpenRouterGlyph, OpenRouterStatus);
        foreach (var (provider, row) in _rows)
            row.Box.PasswordChanged += (_, _) => ShowLocalStatus(provider);
    }

    private void LoadValues()
    {
        var s = _settings.Current;

        foreach (var (provider, row) in _rows)
        {
            var key = _settings.GetApiKey(provider);
            _storedKeys[provider] = key;
            row.Box.Password = key;
            ShowRegistryStatus(provider);
        }

        RefreshModelBox(s.AiModel);

        // Общие: профиль, язык, стартовое окно, режим участка, расценки.
        _profile = s.ExperienceProfile;
        (_profile switch
        {
            UserExperienceProfile.Beginner => ProfileBeginnerRadio,
            UserExperienceProfile.Architect => ProfileArchitectRadio,
            _ => ProfileEngineerRadio
        }).IsChecked = true;
        UpdateProfileHint();
        SetModuleSwitches(s.EffectiveModules);

        LanguageBox.ItemsSource = new[] { "Русский", "English" };
        LanguageBox.SelectedIndex = s.Language == LocalizationService.English ? 1 : 0;
        ShowWelcomeSwitch.IsOn = s.ShowWelcomeScreen;
        AppendSwitch.IsOn = s.AppendToSiteByDefault;

        ConcretePriceBox.Value = s.ConcretePricePerM3;
        GlazingPriceBox.Value = s.GlazingPricePerM2;
        RebarPriceBox.Value = s.RebarPricePerTon;

        // Внешний вид: тема по системе и плитки тем.
        _selectedTheme = s.ThemeName;
        FollowSystemSwitch.IsOn = s.FollowSystemTheme;
        BuildThemeTiles();

        // BIM-Координатор и хранилище.
        LocalAiAutoStartSwitch.IsOn = s.OllamaAutoStart;
        OllamaModelsDirBox.Text = s.OllamaModelsDirectory;
        OllamaModelsDirBox.PlaceholderText = L.Get("SettingsLocalAiFolderAuto");
        ModelsDirBox.Text = s.LocalModelsDirectory;
        ModelsDirBox.PlaceholderText = L.Get("LocalModelsAuto");
        var dir = _settings.ResolveLocalModelsDirectory();
        ModelsDirHint.Text = dir is null
            ? L.Get("LocalModelsMissing")
            : $"{dir}   ·   Qwen 2.5 Coder 3B: {Exists(dir, AiModelCatalog.Qwen3BFile)}   ·   Qwen 2.5 Coder 7B: {Exists(dir, AiModelCatalog.Qwen7BFile)}";

        SettingsPathText.Text = _settings.SettingsFilePath;
        RecentCountText.Text = L.Format("SettingsRecentCount", s.RecentProjects.Count);
        ClearRecentButton.IsEnabled = s.RecentProjects.Count > 0;

        // Материалы и Сметы: библиотека марок; Нормативы и Расчёт: районы, класс сооружения, поведение среды.
        BuildMaterialRows(s.StructuralLoads.Materials);
        LoadNorms(s);

        SectionList.SelectedIndex = 0;
        _loaded = true;
    }

    private string Exists(string dir, string file) => L.Get(File.Exists(Path.Combine(dir, file)) ? "LocalModelFound" : "LocalModelMissing");

    // ───────────────────────────── Разделы и оформление ─────────────────────────────

    private UserExperienceProfile _profile;
    private bool _loaded;
    private string _selectedTheme = ThemeService.CupertinoDark;
    private bool _clearRecent;
    private bool _syncingTheme;

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string tag = (SectionList.SelectedItem as FrameworkElement)?.Tag as string ?? "General";
        GeneralPage.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        ModulesPage.Visibility = tag == "Modules" ? Visibility.Visible : Visibility.Collapsed;
        MaterialsPage.Visibility = tag == "Materials" ? Visibility.Visible : Visibility.Collapsed;
        NormsPage.Visibility = tag == "Norms" ? Visibility.Visible : Visibility.Collapsed;
        AssistantPage.Visibility = tag == "Assistant" ? Visibility.Visible : Visibility.Collapsed;
        StoragePage.Visibility = tag == "Storage" ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ChangeView(null, 0, null, disableAnimation: true);
        var page = tag switch
        {
            "Appearance" => AppearancePage, "Modules" => ModulesPage, "Materials" => MaterialsPage, "Norms" => NormsPage, "Assistant" => AssistantPage,
            "Storage" => StoragePage, _ => GeneralPage
        };
        Views.WindowChrome.Fade(page, 0, 1, 0.985, 1, 260).Begin();
    }

    private void OnProfileChecked(object sender, RoutedEventArgs e)
    {
        _profile = ReferenceEquals(sender, ProfileBeginnerRadio) ? UserExperienceProfile.Beginner
            : ReferenceEquals(sender, ProfileArchitectRadio) ? UserExperienceProfile.Architect
            : UserExperienceProfile.StructuralEngineer;
        UpdateProfileHint();
        // Смена уровня опыта подставляет его набор модулей; дальше любой модуль включается вручную.
        if (_loaded) SetModuleSwitches(WorkspaceModules.ForProfile(_profile));
    }

    private void UpdateProfileHint()
    {
        ProfileHintText.Text = L.Get(UserExperienceProfiles.TitleKey(_profile) + "Hint");
        ModulesLevelText.Text = $"{L.Get("SettingsProfile")}: {L.Get(UserExperienceProfiles.TitleKey(_profile))}";
    }

    // ───────────────────────────── Модули и Плагины ─────────────────────────────

    private void SetModuleSwitches(WorkspaceModules modules)
    {
        ModuleInsolationSwitch.IsOn = modules.Insolation;
        ModuleAnalysisSwitch.IsOn = modules.StructuralAnalysis;
        ModuleRebarSwitch.IsOn = modules.Reinforcement3D;
        ModuleClashSwitch.IsOn = modules.ClashInspector;
        ModuleQuantitiesSwitch.IsOn = modules.QuantitySchedule;
    }

    private WorkspaceModules ReadModuleSwitches() => new()
    {
        Insolation = ModuleInsolationSwitch.IsOn,
        StructuralAnalysis = ModuleAnalysisSwitch.IsOn,
        Reinforcement3D = ModuleRebarSwitch.IsOn,
        ClashInspector = ModuleClashSwitch.IsOn,
        QuantitySchedule = ModuleQuantitiesSwitch.IsOn
    };

    private void OnModulesResetClick(object sender, RoutedEventArgs e) => SetModuleSwitches(WorkspaceModules.ForProfile(_profile));

    // ───────────────────────────── Материалы и Сметы ─────────────────────────────

    /// <summary>Строка таблицы марок: поля R, E, ρ, цены и поставщика, отметка источника (СП / сертификат).</summary>
    private sealed record MaterialRow(MaterialGrade Norm, TextBox Strength, TextBox Modulus, TextBox Density, TextBox Price, TextBox Supplier, FontIcon Source,
        string? Certificate);

    private readonly List<MaterialRow> _materialRows = [];
    private static readonly int[] MaterialColumns = [54, 70, 76, 66, 78, -1, 16];

    /// <summary>Таблица марок (бетон B15–B60, арматура А240/А400/А500С, сталь С245/С345): нормативные значения или переопределения проекта.</summary>
    private void BuildMaterialRows(IReadOnlyList<MaterialGrade>? overrides)
    {
        _materialRows.Clear();
        MaterialRows.Children.Clear();
        var header = MaterialGrid(header: true);
        string[] titles = [L.Get("MatColGrade"), L.Get("MatColStrength"), L.Get("MatColModulus"), L.Get("MatColDensity"), L.Get("MatColPrice"), L.Get("MatColSupplier"), string.Empty];
        for (int i = 0; i < titles.Length; i++) header.Children.Add(HeaderCell(titles[i], i));
        MaterialRows.Children.Add(header);
        MaterialFamily? family = null;
        foreach (var norm in MaterialLibrary.StandardGrades)
        {
            if (family != norm.Family)
            {
                family = norm.Family;
                MaterialRows.Children.Add(new TextBlock
                {
                    Text = norm.Family switch { MaterialFamily.Concrete => L.Get("MatFamilyConcrete"), MaterialFamily.Rebar => L.Get("MatFamilyRebar"), _ => L.Get("MatFamilySteel") } + " · " + norm.NormReference,
                    FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2),
                    Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"]
                });
            }
            var g = MaterialLibrary.Find(overrides, norm.Code) ?? norm;
            var row = MaterialGrid();
            row.Children.Add(Cell(MaterialLibrary.DisplayCode(norm), 0, strong: true));
            var strength = Field(Num(g.Strength), 1, mono: true);
            var modulus = Field(Num(g.ElasticModulus), 2, mono: true);
            var density = Field(Num(g.Density), 3, mono: true);
            var price = Field(g.UnitPriceRub is { } p ? Num(p) : string.Empty, 4, mono: true);
            price.Box.PlaceholderText = L.Get("MatPriceProject");
            var supplier = Field(g.Supplier ?? string.Empty, 5, mono: false);
            supplier.Box.PlaceholderText = L.Get("MatSupplierNone");
            var source = new FontIcon { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(source, 6);
            foreach (var host in new[] { strength.Host, modulus.Host, density.Host, price.Host, supplier.Host }) row.Children.Add(host);
            row.Children.Add(source);
            var item = new MaterialRow(norm, strength.Box, modulus.Box, density.Box, price.Box, supplier.Box, source, g.Certificate);
            foreach (var box in new[] { strength.Box, modulus.Box, density.Box }) box.TextChanged += (_, _) => UpdateSourceMark(item);
            _materialRows.Add(item);
            UpdateSourceMark(item);
            MaterialRows.Children.Add(row);
        }
        UpdateMaterialsStatus();
    }

    private static Grid MaterialGrid(bool header = false)
    {
        var row = new Grid { ColumnSpacing = 6, MinHeight = header ? 18 : 30 };
        foreach (int w in MaterialColumns)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        return row;
    }

    private static TextBlock HeaderCell(string text, int column)
    {
        var cell = new TextBlock { Text = text, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = column is >= 1 and <= 4 ? HorizontalAlignment.Right : HorizontalAlignment.Left, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static TextBlock Cell(string text, int column, bool strong = false)
    {
        var cell = new TextBlock { Text = text, FontSize = 12, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"] };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static (Border Host, TextBox Box) Field(string text, int column, bool mono)
    {
        var box = new TextBox
        {
            Text = text, FontSize = 12, MinHeight = 24, MinWidth = 0, Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), TextAlignment = mono ? TextAlignment.Right : TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        if (mono) box.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
        var host = new Border
        {
            CornerRadius = new CornerRadius(6), Background = (Brush)Application.Current.Resources["CardBrush"], BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"],
            BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = box
        };
        Grid.SetColumn(host, column);
        return (host, box);
    }

    private string Num(double v) => v.ToString("#,0.###", L.Culture);

    private static double Parse(string text, double fallback)
    {
        string clean = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace(',', '.');
        return double.TryParse(clean, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v > 0 ? v : fallback;
    }

    /// <summary>Марка из полей строки: пустые и некорректные значения — нормативные; цена пустая — по расценкам проекта.</summary>
    private MaterialGrade ReadRow(MaterialRow row)
    {
        var g = row.Norm.Clone();
        g.Strength = Parse(row.Strength.Text, row.Norm.Strength);
        g.ElasticModulus = Parse(row.Modulus.Text, row.Norm.ElasticModulus);
        g.Density = Parse(row.Density.Text, row.Norm.Density);
        double price = Parse(row.Price.Text, double.NaN);
        g.UnitPriceRub = double.IsNaN(price) ? null : price;
        g.Supplier = string.IsNullOrWhiteSpace(row.Supplier.Text) ? null : row.Supplier.Text.Trim();
        g.Certificate = g.IsStructurallyOverridden ? row.Certificate ?? L.Get("PassportCertificateDefault") : null;
        return g;
    }

    /// <summary>Отметка источника: серый круг — таблица СП, акцентный — пользовательский сертификат.</summary>
    private void UpdateSourceMark(MaterialRow row)
    {
        bool custom = ReadRow(row).IsStructurallyOverridden;
        row.Source.Glyph = custom ? "" : "";
        row.Source.Foreground = (Brush)Application.Current.Resources[custom ? "AccentBrush" : "TextSecondaryBrush"];
        row.Source.Opacity = custom ? 1 : 0.5;
        ToolTipService.SetToolTip(row.Source, custom ? L.Get("MatSourceCertificate") : row.Norm.NormReference);
        UpdateMaterialsStatus();
    }

    private void UpdateMaterialsStatus()
    {
        if (MaterialsStatusText is null) return;
        int custom = _materialRows.Count(r => ReadRow(r).IsStructurallyOverridden);
        MaterialsStatusText.Text = custom == 0 ? L.Get("SettingsMaterialsAllNorm") : L.Format("SettingsMaterialsCustom", custom);
    }

    private List<MaterialGrade>? ReadMaterials() => MaterialLibrary.Normalize(_materialRows.Select(ReadRow).ToList());

    /// <summary>«Сбросить к нормативам СП 63.13330»: табличные R, E, ρ всех марок (цены и поставщики сохраняются).</summary>
    private void OnMaterialsResetClick(object sender, RoutedEventArgs e)
    {
        var prices = _materialRows.Select(ReadRow).Select(g =>
        {
            var norm = MaterialLibrary.Standard(g.Code)!.Clone();
            norm.UnitPriceRub = g.UnitPriceRub;
            norm.Supplier = g.Supplier;
            return norm;
        }).ToList();
        BuildMaterialRows(MaterialLibrary.Normalize(prices));
    }

    // ───────────────────────────── Нормативы и Расчёт ─────────────────────────────

    private bool _syncingNorms;

    private void LoadNorms(AppSettings s)
    {
        _syncingNorms = true;
        var loads = s.StructuralLoads;
        SnowRegionBox.ItemsSource = StructuralLoadSettings.SnowRegions.Select(r => L.Format("SettingsRegionItem", r.Title, r.Value.ToString("0", L.Culture))).ToList();
        WindRegionBox.ItemsSource = StructuralLoadSettings.WindRegions.Select(r => L.Format("SettingsRegionItem", r.Title, r.Value.ToString("0", L.Culture))).ToList();
        SnowRegionBox.SelectedIndex = Nearest(StructuralLoadSettings.SnowRegions, loads.SnowKgM2);
        WindRegionBox.SelectedIndex = Nearest(StructuralLoadSettings.WindRegions, loads.WindKgM2);
        (loads.ConsequenceClass switch { 1 => Ks1Radio, 3 => Ks3Radio, _ => Ks2Radio }).IsChecked = true;
        LiveRecalcSwitch.IsOn = s.Engineering.LiveRecalculation;
        NormTipsSwitch.IsOn = s.Engineering.NormTooltips;
        SteelGlazingSwitch.IsOn = s.Engineering.IncludeSteelAndGlazing;
        MuMinSwitch.IsOn = s.Engineering.StrictMinimumReinforcement;
        _syncingNorms = false;
        UpdateNormsStatus(applied: false);
    }

    private static int Nearest(IReadOnlyList<(string Title, double Value)> regions, double value)
    {
        int best = 0;
        for (int i = 1; i < regions.Count; i++)
            if (Math.Abs(regions[i].Value - value) < Math.Abs(regions[best].Value - value)) best = i;
        return best;
    }

    private int SelectedConsequenceClass => Ks1Radio.IsChecked == true ? 1 : Ks3Radio.IsChecked == true ? 3 : 2;

    private void OnNormsInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingNorms && NormsStatusText is not null) UpdateNormsStatus(applied: false);
    }

    private void UpdateNormsStatus(bool applied)
    {
        if (SnowRegionBox.SelectedIndex < 0 || WindRegionBox.SelectedIndex < 0) return;
        var snow = StructuralLoadSettings.SnowRegions[SnowRegionBox.SelectedIndex];
        var wind = StructuralLoadSettings.WindRegions[WindRegionBox.SelectedIndex];
        double gn = SelectedConsequenceClass switch { 1 => 0.8, 3 => 1.1, _ => 1.0 };
        var loads = _settings.Current.StructuralLoads;
        bool same = Math.Abs(loads.SnowKgM2 - snow.Value) < 1e-6 && Math.Abs(loads.WindKgM2 - wind.Value) < 1e-6 && (loads.ConsequenceClass == 0 ? 2 : loads.ConsequenceClass) == SelectedConsequenceClass;
        NormsStatusText.Text = L.Format(applied ? "SettingsNormsApplied" : same ? "SettingsNormsCurrent" : "SettingsNormsPending",
            snow.Title, (snow.Value * 9.81 / 1000).ToString("0.00", L.Culture), wind.Title, (wind.Value * 9.81 / 1000).ToString("0.00", L.Culture), gn.ToString("0.0", L.Culture));
    }

    /// <summary>«Применить к текущему проекту»: районы и класс сооружения сразу уходят в расчёт (без закрытия настроек).</summary>
    private void OnApplyNormsClick(object sender, RoutedEventArgs e)
    {
        if (SnowRegionBox.SelectedIndex < 0 || WindRegionBox.SelectedIndex < 0) return;
        double snow = StructuralLoadSettings.SnowRegions[SnowRegionBox.SelectedIndex].Value;
        double wind = StructuralLoadSettings.WindRegions[WindRegionBox.SelectedIndex].Value;
        int ks = SelectedConsequenceClass;
        try
        {
            if (_applyNorms is not null) _applyNorms(snow, wind, ks);
            else
                _settings.Update(s =>
                {
                    s.StructuralLoads.SnowKgM2 = snow;
                    s.StructuralLoads.WindKgM2 = wind;
                    s.StructuralLoads.ConsequenceClass = ks == 2 ? 0 : ks;
                });
            UpdateNormsStatus(applied: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NormsStatusText.Text = ex.Message;
        }
    }

    /// <summary>Плитки 5 тем: миниатюра холста, острова и акцента; выбранная — акцентная рамка.</summary>
    private void BuildThemeTiles()
    {
        ThemeTiles.Children.Clear();
        var themes = App.Services.GetService(typeof(ThemeService)) as ThemeService ?? new ThemeService();
        foreach (var theme in themes.Themes)
        {
            bool selected = theme.Name == _selectedTheme;
            var preview = new Grid
            {
                Height = 84,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(theme.ViewportBackground)
            };
            preview.Children.Add(new Border
            {
                Width = 74, Height = 14, CornerRadius = new CornerRadius(5), Margin = new Thickness(8, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(theme.PanelOpaque)
            });
            preview.Children.Add(new Border
            {
                Width = 40, Height = 52, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 8, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(theme.PanelOpaque)
            });
            preview.Children.Add(new Border
            {
                Width = 48, Height = 26, Margin = new Thickness(28, 30, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(theme.Card), BorderBrush = new SolidColorBrush(theme.Accent), BorderThickness = new Thickness(1.5)
            });
            preview.Children.Add(new Border
            {
                Width = 70, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(theme.PanelOpaque)
            });
            preview.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 54, 10),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Fill = new SolidColorBrush(theme.Accent)
            });

            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(preview);
            content.Children.Add(new TextBlock { FontSize = 12, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Text = theme.Name, HorizontalAlignment = HorizontalAlignment.Center });

            var tile = new Button
            {
                Tag = theme.Name,
                Content = content,
                Width = 172,
                Padding = new Thickness(6),
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(12),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = (Brush)Application.Current.Resources[selected ? "AccentSoftBrush" : "TrackBrush"],
                BorderBrush = (Brush)Application.Current.Resources[selected ? "AccentBrush" : "StudioBorderBrush"],
                BorderThickness = new Thickness(selected ? 2 : 1)
            };
            tile.Click += OnThemeTileClick;
            ThemeTiles.Children.Add(tile);
        }
    }

    private void OnThemeTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        _selectedTheme = name;
        // Ручной выбор темы отключает «Как в Windows».
        _syncingTheme = true;
        FollowSystemSwitch.IsOn = false;
        _syncingTheme = false;
        BuildThemeTiles();
    }

    private void OnFollowSystemToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingTheme || !FollowSystemSwitch.IsOn) return;
        _selectedTheme = SystemThemeDetector.PreferredThemeName();
        BuildThemeTiles();
    }

    private void OnRevealSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_settings.SettingsFilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.WriteCrashLog("settings-reveal", ex);
        }
    }

    private void OnClearRecentClick(object sender, RoutedEventArgs e)
    {
        _clearRecent = true;
        RecentCountText.Text = L.Format("SettingsRecentCount", 0);
        ClearRecentButton.IsEnabled = false;
    }

    private void RefreshModelBox(string selectedId)
    {
        _modelOptions = _registry.All.ToList();
        ModelBox.ItemsSource = _modelOptions.Select(_label).ToList();
        int index = _modelOptions.FindIndex(o => o.Id == selectedId);
        ModelBox.SelectedIndex = index >= 0 ? index : 0;
    }

    /// <summary>Универсальное поле: определяем провайдера по префиксу и переносим ключ в его поле.</summary>
    private void OnUniversalKeyChanged(object sender, RoutedEventArgs e)
    {
        var key = UniversalKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            // Поле очищено нами после переноса ключа — оставляем подпись «Распознан ключ …».
            if (_movingKey)
            {
                _movingKey = false;
                return;
            }
            SetDetect("", L.Get("UniversalKeyHint"), null);
            return;
        }
        var provider = AiKeyInspectorService.DetectProvider(key);
        if (provider is not { } p)
        {
            SetDetect("", L.Get("KeyUnknown"), Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x45, 0x3A));
            return;
        }
        _rows[p].Box.Password = key;
        _movingKey = true;
        UniversalKeyBox.Password = string.Empty;
        SetDetect("", L.Format("KeyDetected", AiModelCatalog.ProviderLongTitle(p)), Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x30, 0xD1, 0x58));
    }

    private bool _movingKey;

    private void SetDetect(string glyph, string text, Windows.UI.Color? color)
    {
        DetectGlyph.Glyph = glyph;
        DetectText.Text = text;
        Paint(DetectGlyph, color);
    }

    /// <summary>Цвет статуса; без цвета глиф наследует цвет текста темы диалога (светлой или тёмной).</summary>
    private static void Paint(FontIcon icon, Windows.UI.Color? color)
    {
        if (color is { } c)
        {
            icon.Foreground = new SolidColorBrush(c);
            icon.Opacity = 1;
        }
        else
        {
            icon.ClearValue(IconElement.ForegroundProperty);
            icon.Opacity = 0.6;
        }
    }

    /// <summary>Проверка всех заполненных ключей реальными HTTP-запросами и загрузка списков моделей.</summary>
    private async void OnCheckKeysClick(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckRing.IsActive = true;
        try
        {
            var tasks = new List<Task>();
            foreach (var (provider, row) in _rows)
            {
                var key = row.Box.Password.Trim();
                try
                {
                    if (!string.Equals(key, _storedKeys[provider], StringComparison.Ordinal))
                    {
                        _settings.SetApiKey(provider, key);
                        _storedKeys[provider] = key;
                    }
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    SetRowStatus(row, AiKeyState.Error, ex.Message);
                    continue;
                }

                if (key.Length == 0)
                {
                    _registry.ClearCloudModels(provider);
                    _registry.SetStatus(AiKeyStatus.NotSet(provider));
                    ShowRegistryStatus(provider);
                    continue;
                }
                SetRowStatus(row, AiKeyState.Checking, L.Get("KeyChecking"));
                tasks.Add(CheckOneAsync(provider, key));
            }
            await Task.WhenAll(tasks);
            RefreshModelBox((ModelBox.SelectedIndex >= 0 && ModelBox.SelectedIndex < _modelOptions.Count ? _modelOptions[ModelBox.SelectedIndex] : null)?.Id ?? _settings.Current.AiModel);
        }
        finally
        {
            CheckRing.IsActive = false;
            CheckButton.IsEnabled = true;
        }
    }

    private async Task CheckOneAsync(AiProviderKind provider, string key)
    {
        var result = await Task.Run(() => _inspector.InspectAsync(provider, key));
        SetRowStatus(_rows[provider], result.IsValid ? AiKeyState.Valid : AiKeyState.Invalid,
            result.IsValid ? result.Message : $"{result.ErrorCode}: {result.Message}");
    }

    private void ShowLocalStatus(AiProviderKind provider)
    {
        var row = _rows[provider];
        var key = row.Box.Password.Trim();
        if (key.Length == 0) SetRowStatus(row, AiKeyState.NotSet, L.Get("KeyNotSet"));
        else if (key != _storedKeys.GetValueOrDefault(provider)) SetRowStatus(row, AiKeyState.Unchecked, $"{AiHttp.MaskKey(key)} · {L.Get("KeyUnchecked")}");
        else ShowRegistryStatus(provider);
    }

    private void ShowRegistryStatus(AiProviderKind provider)
    {
        var row = _rows[provider];
        var status = _registry.GetStatus(provider);
        var key = row.Box.Password.Trim();
        if (key.Length == 0)
        {
            SetRowStatus(row, AiKeyState.NotSet, L.Get("KeyNotSet"));
            return;
        }
        string text = status.State switch
        {
            AiKeyState.NotSet or AiKeyState.Unchecked => status.ModelCount > 0
                ? $"{AiHttp.MaskKey(key)} · {L.Get("KeyUnchecked")} · {status.ModelCount}"
                : $"{AiHttp.MaskKey(key)} · {L.Get("KeyUnchecked")}",
            AiKeyState.Checking => L.Get("KeyChecking"),
            _ => status.Message
        };
        SetRowStatus(row, status.State == AiKeyState.NotSet ? AiKeyState.Unchecked : status.State, text);
    }

    private static void SetRowStatus(ProviderRow row, AiKeyState state, string text)
    {
        (string glyph, Windows.UI.Color? color) = state switch
        {
            AiKeyState.Valid => ("", Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x30, 0xD1, 0x58)),
            AiKeyState.Invalid or AiKeyState.Error => ("", Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x45, 0x3A)),
            AiKeyState.Checking => ("", (Windows.UI.Color?)null),
            AiKeyState.Unchecked => ("", null),
            _ => ("", null)
        };
        row.StatusGlyph.Glyph = glyph;
        Paint(row.StatusGlyph, color);
        row.StatusText.Text = text;
        row.StatusText.Opacity = state is AiKeyState.Valid or AiKeyState.Invalid or AiKeyState.Error ? 1 : 0.7;
    }

#if DEBUG
    /// <summary>Только Debug (AIA_DEBUG_SETTINGS=раздел): выбрать раздел для скриншота.</summary>
    public void DebugSelectSection(string tag)
    {
        var item = SectionList.Items.OfType<FrameworkElement>().FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (item is not null) SectionList.SelectedItem = item;
    }

    /// <summary>Только Debug (AIA_DEBUG_KEYTEST): вставка ключа в универсальное поле и запуск проверки для скриншотов.</summary>
    public async void DebugInjectKey(string key)
    {
        UniversalKeyBox.Password = key;
        await Task.Delay(600);
        OnCheckKeysClick(CheckButton, new RoutedEventArgs());
    }
#endif

    // ───────────────────────────── Сохранение ─────────────────────────────

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            var changed = new List<(AiProviderKind Provider, string Key)>();
            foreach (var (provider, row) in _rows)
            {
                var key = row.Box.Password.Trim();
                if (string.Equals(key, _storedKeys[provider], StringComparison.Ordinal)) continue;
                _settings.SetApiKey(provider, key);
                _storedKeys[provider] = key;
                changed.Add((provider, key));
            }

            var model = ModelBox.SelectedIndex >= 0 && ModelBox.SelectedIndex < _modelOptions.Count ? _modelOptions[ModelBox.SelectedIndex] : null;
            var materials = ReadMaterials();
            _settings.Update(s =>
            {
                if (model is not null) s.AiModel = model.Id;
                s.ThemeName = _selectedTheme;
                s.FollowSystemTheme = FollowSystemSwitch.IsOn;
                s.Language = LanguageBox.SelectedIndex == 1 ? LocalizationService.English : LocalizationService.Russian;
                s.ExperienceProfile = _profile;
                s.Modules = ReadModuleSwitches();
                s.ShowWelcomeScreen = ShowWelcomeSwitch.IsOn;
                s.AppendToSiteByDefault = AppendSwitch.IsOn;
                if (double.IsFinite(ConcretePriceBox.Value)) s.ConcretePricePerM3 = ConcretePriceBox.Value;
                if (double.IsFinite(GlazingPriceBox.Value)) s.GlazingPricePerM2 = GlazingPriceBox.Value;
                if (double.IsFinite(RebarPriceBox.Value)) s.RebarPricePerTon = RebarPriceBox.Value;
                s.OllamaAutoStart = LocalAiAutoStartSwitch.IsOn;
                s.OllamaModelsDirectory = OllamaModelsDirBox.Text.Trim();
                s.LocalModelsDirectory = ModelsDirBox.Text.Trim();
                if (_clearRecent) s.RecentProjects.Clear();
                s.StructuralLoads.Materials = materials;
                s.Engineering = new EngineeringOptions
                {
                    LiveRecalculation = LiveRecalcSwitch.IsOn,
                    NormTooltips = NormTipsSwitch.IsOn,
                    IncludeSteelAndGlazing = SteelGlazingSwitch.IsOn,
                    StrictMinimumReinforcement = MuMinSwitch.IsOn
                };
            });

            // Изменённые и непроверенные ключи проверяются в фоне: модели появятся в ИИ-доке автоматически.
            foreach (var (provider, key) in changed)
            {
                if (key.Length == 0)
                {
                    _registry.ClearCloudModels(provider);
                    _registry.SetStatus(AiKeyStatus.NotSet(provider));
                }
                else _ = Task.Run(() => _inspector.InspectAsync(provider, key));
            }
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            SaveError = ex;
        }
    }
}
