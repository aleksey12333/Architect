using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIArchitectStudio.Services;

/// <summary>Полное описание темы: цвета парящих островов UI и параметры 3D-вьюпорта.</summary>
public sealed record ThemeDefinition(
    string Name,
    bool IsLight,
    Color ViewportBackground,
    Color Panel,
    Color Card,
    Color Hover,
    Color Track,
    Color Pill,
    Color Accent,
    Color OnAccent,
    Color TextPrimary,
    Color TextSecondary,
    Color Border,
    Color GridMinor,
    Color GridMajor,
    Color AxisLine,
    Color AmbientLight,
    Color KeyLight,
    Color FillLight,
    Color Selection)
{
    public Color AccentSoft => Color.FromArgb(0x2E, Accent.R, Accent.G, Accent.B);

    /// <summary>Непрозрачный вариант цвета панели (fallback для акрила).</summary>
    public Color PanelOpaque => Color.FromArgb(0xFF, Panel.R, Panel.G, Panel.B);
}

/// <summary>
/// Движок 5 тем оформления в стиле Apple Cupertino. Мгновенно перекрашивает кисти приложения
/// (мутацией SolidColorBrush.Color и AcrylicBrush.TintColor), кнопки системного TitleBar
/// и сообщает вьюпорту новые цвета фона, сетки и освещения через ThemeChanged.
/// </summary>
public sealed class ThemeService
{
    public const string CupertinoDark = "Cupertino Dark";
    public const string CupertinoLight = "Cupertino Light";
    public const string GraphitePro = "Graphite Pro";
    public const string BlueprintStudio = "Blueprint Studio";
    public const string ObsidianOled = "Obsidian OLED";

    public static readonly IReadOnlyList<string> ThemeNames = [CupertinoDark, CupertinoLight, GraphitePro, BlueprintStudio, ObsidianOled];

    /// <summary>Ключи кистей в ресурсах App.xaml → функция выбора цвета из темы.</summary>
    private static readonly IReadOnlyDictionary<string, Func<ThemeDefinition, Color>> BrushMap = new Dictionary<string, Func<ThemeDefinition, Color>>
    {
        ["ViewportBackgroundBrush"] = t => t.ViewportBackground,
        ["PanelBrush"] = t => t.Panel,
        ["CardBrush"] = t => t.Card,
        ["HoverBrush"] = t => t.Hover,
        ["TrackBrush"] = t => t.Track,
        ["PillBrush"] = t => t.Pill,
        ["AccentBrush"] = t => t.Accent,
        ["AccentSoftBrush"] = t => t.AccentSoft,
        ["OnAccentBrush"] = t => t.OnAccent,
        ["TextPrimaryBrush"] = t => t.TextPrimary,
        ["TextSecondaryBrush"] = t => t.TextSecondary,
        ["StudioBorderBrush"] = t => t.Border,
        ["GridMinorBrush"] = t => t.GridMinor,
        ["GridMajorBrush"] = t => t.GridMajor,
        ["SelectionBrush"] = t => t.Selection,
        ["UserBubbleBrush"] = t => t.AccentSoft,
        ["AssistantBubbleBrush"] = t => t.Card
    };

    private readonly Dictionary<string, ThemeDefinition> _themes;
    private Window? _window;

    public ThemeDefinition Current { get; private set; }

    /// <summary>Вызывается после применения темы (вьюпорт обновляет фон, сетку и свет).</summary>
    public event EventHandler<ThemeDefinition>? ThemeChanged;

    public ThemeService()
    {
        _themes = BuildThemes().ToDictionary(t => t.Name);
        Current = _themes[CupertinoDark];
    }

    public IEnumerable<ThemeDefinition> Themes => ThemeNames.Select(n => _themes[n]);

    public ThemeDefinition Get(string name) => _themes.TryGetValue(name, out var t) ? t : _themes[CupertinoDark];

    /// <summary>Привязывает главное окно, чтобы перекрашивать кнопки заголовка и RequestedTheme корня.</summary>
    public void AttachWindow(Window window)
    {
        _window = window;
        Apply(Current.Name);
    }

    public void Apply(string themeName)
    {
        var theme = Get(themeName);
        Current = theme;

        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            foreach (var (key, pick) in BrushMap)
            {
                if (resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                    brush.Color = pick(theme);
                else
                    resources[key] = new SolidColorBrush(pick(theme));
            }

            // Матовое стекло островов: оттенок и плотность берутся из цвета панели темы.
            if (resources.TryGetValue("IslandBrush", out var island) && island is AcrylicBrush acrylic)
            {
                acrylic.TintColor = theme.PanelOpaque;
                acrylic.TintOpacity = theme.Panel.A / 255.0 * 0.82;
                acrylic.TintLuminosityOpacity = theme.IsLight ? 0.85 : 0.9;
                acrylic.FallbackColor = theme.PanelOpaque;
            }
            if (resources.TryGetValue("SoftGlassBrush", out var soft) && soft is AcrylicBrush softAcrylic)
            {
                softAcrylic.TintColor = theme.PanelOpaque;
                softAcrylic.TintOpacity = theme.Panel.A / 255.0 * 0.45;
                softAcrylic.TintLuminosityOpacity = theme.IsLight ? 0.55 : 0.62;
                softAcrylic.FallbackColor = theme.PanelOpaque;
            }
            ApplyLiquidGlass(resources, theme);
        }

        if (_window?.Content is FrameworkElement root)
            root.RequestedTheme = theme.IsLight ? ElementTheme.Light : ElementTheme.Dark;

        ApplyTitleBarColors(theme);
        ThemeChanged?.Invoke(this, theme);
    }

    /// <summary>
    /// Кисти дизайн-системы Liquid Glass (навык winui-liquid-glass): стеклянная поверхность,
    /// приподнятое стекло и спекулярный рефракционный кант 1 px. Создаются при первом применении
    /// темы и затем мутируются, чтобы {StaticResource} в коде окон оставался живым.
    /// </summary>
    private static void ApplyLiquidGlass(ResourceDictionary resources, ThemeDefinition theme)
    {
        static AcrylicBrush Acrylic(ResourceDictionary r, string key)
        {
            if (r.TryGetValue(key, out var v) && v is AcrylicBrush a) return a;
            var created = new AcrylicBrush();
            r[key] = created;
            return created;
        }
        var surface = Acrylic(resources, "LiquidGlassSurfaceBrush");
        surface.TintColor = theme.PanelOpaque;
        surface.TintOpacity = theme.Panel.A / 255.0 * 0.62;
        surface.TintLuminosityOpacity = theme.IsLight ? 0.88 : 0.78;
        surface.FallbackColor = theme.PanelOpaque;

        var elevated = Acrylic(resources, "LiquidGlassElevatedBrush");
        elevated.TintColor = theme.IsLight ? theme.PanelOpaque : theme.Card;
        elevated.TintOpacity = theme.Panel.A / 255.0 * 0.80;
        elevated.TintLuminosityOpacity = theme.IsLight ? 0.92 : 0.86;
        elevated.FallbackColor = theme.IsLight ? theme.PanelOpaque : theme.Card;

        LinearGradientBrush rim;
        if (resources.TryGetValue("LiquidGlassSpecularBorderBrush", out var rv) && rv is LinearGradientBrush existing)
            rim = existing;
        else
        {
            rim = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
            for (int i = 0; i < 4; i++) rim.GradientStops.Add(new GradientStop());
            resources["LiquidGlassSpecularBorderBrush"] = rim;
        }
        Color[] stops = theme.IsLight
            ? [Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0, 0, 0), Color.FromArgb(0x14, 0, 0, 0), Color.FromArgb(0x2A, 0x25, 0x63, 0xEB)]
            : [Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x28, 0x38, 0xBD, 0xF8)];
        double[] offsets = [0.0, 0.45, 0.70, 1.0];
        for (int i = 0; i < 4; i++)
        {
            rim.GradientStops[i].Color = stops[i];
            rim.GradientStops[i].Offset = offsets[i];
        }
    }

    private void ApplyTitleBarColors(ThemeDefinition theme)
    {
        if (_window is null || !AppWindowTitleBar.IsCustomizationSupported()) return;
        var bar = _window.AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = theme.TextPrimary;
        bar.ButtonInactiveForegroundColor = theme.TextSecondary;
        bar.ButtonHoverBackgroundColor = theme.Hover;
        bar.ButtonHoverForegroundColor = theme.TextPrimary;
        bar.ButtonPressedBackgroundColor = theme.Card;
        bar.ButtonPressedForegroundColor = theme.TextPrimary;
    }

    public static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 0xFF;
        if (hex.Length == 8)
        {
            a = Convert.ToByte(hex[..2], 16);
            hex = hex[2..];
        }
        return Color.FromArgb(a,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static IEnumerable<ThemeDefinition> BuildThemes()
    {
        yield return new ThemeDefinition(CupertinoDark, false,
            ViewportBackground: FromHex("#0D0D10"), Panel: FromHex("#D81C1C1E"), Card: FromHex("#2C2C2E"), Hover: FromHex("#1FFFFFFF"),
            Track: FromHex("#14FFFFFF"), Pill: FromHex("#5C5C60"),
            Accent: FromHex("#0A84FF"), OnAccent: FromHex("#FFFFFF"),
            TextPrimary: FromHex("#F5F5F7"), TextSecondary: FromHex("#8E8E93"), Border: FromHex("#18FFFFFF"),
            GridMinor: FromHex("#1A1A1F"), GridMajor: FromHex("#2C2C33"), AxisLine: FromHex("#0A84FF"),
            AmbientLight: FromHex("#4C4C52"), KeyLight: FromHex("#D4D4D6"), FillLight: FromHex("#6C6C74"), Selection: FromHex("#0A84FF"));

        yield return new ThemeDefinition(CupertinoLight, true,
            ViewportBackground: FromHex("#F2F2F7"), Panel: FromHex("#E6FFFFFF"), Card: FromHex("#F2F2F7"), Hover: FromHex("#0F000000"),
            Track: FromHex("#0F000000"), Pill: FromHex("#FFFFFF"),
            Accent: FromHex("#007AFF"), OnAccent: FromHex("#FFFFFF"),
            TextPrimary: FromHex("#1C1C1E"), TextSecondary: FromHex("#6E6E73"), Border: FromHex("#14000000"),
            GridMinor: FromHex("#DEDEE4"), GridMajor: FromHex("#B4B4BC"), AxisLine: FromHex("#3A3A3C"),
            AmbientLight: FromHex("#8E8E96"), KeyLight: FromHex("#E4E4E6"), FillLight: FromHex("#C7C7CC"), Selection: FromHex("#007AFF"));

        yield return new ThemeDefinition(GraphitePro, false,
            ViewportBackground: FromHex("#1C1D20"), Panel: FromHex("#D8292A2E"), Card: FromHex("#36373C"), Hover: FromHex("#1FFFFFFF"),
            Track: FromHex("#14FFFFFF"), Pill: FromHex("#606168"),
            Accent: FromHex("#64D2FF"), OnAccent: FromHex("#0B1A22"),
            TextPrimary: FromHex("#F2F2F4"), TextSecondary: FromHex("#9A9AA2"), Border: FromHex("#18FFFFFF"),
            GridMinor: FromHex("#25262A"), GridMajor: FromHex("#3A3B41"), AxisLine: FromHex("#64D2FF"),
            AmbientLight: FromHex("#5A5B62"), KeyLight: FromHex("#D2D2D6"), FillLight: FromHex("#70737C"), Selection: FromHex("#64D2FF"));

        yield return new ThemeDefinition(BlueprintStudio, false,
            ViewportBackground: FromHex("#0A1A33"), Panel: FromHex("#D80F2445"), Card: FromHex("#173463"), Hover: FromHex("#1FFFFFFF"),
            Track: FromHex("#1A5AC8FA"), Pill: FromHex("#2A5A9A"),
            Accent: FromHex("#5AC8FA"), OnAccent: FromHex("#04121F"),
            TextPrimary: FromHex("#E6F4FF"), TextSecondary: FromHex("#86A8CC"), Border: FromHex("#225AC8FA"),
            GridMinor: FromHex("#12284A"), GridMajor: FromHex("#2A5C8E"), AxisLine: FromHex("#5AC8FA"),
            AmbientLight: FromHex("#34507A"), KeyLight: FromHex("#C4DDF2"), FillLight: FromHex("#4A90C8"), Selection: FromHex("#5AC8FA"));

        yield return new ThemeDefinition(ObsidianOled, false,
            ViewportBackground: FromHex("#000000"), Panel: FromHex("#E00C0C0E"), Card: FromHex("#1C1C1E"), Hover: FromHex("#1AFFFFFF"),
            Track: FromHex("#12FFFFFF"), Pill: FromHex("#3A3A3C"),
            Accent: FromHex("#FF9F0A"), OnAccent: FromHex("#140A00"),
            TextPrimary: FromHex("#F5F5F7"), TextSecondary: FromHex("#8E8E93"), Border: FromHex("#16FFFFFF"),
            GridMinor: FromHex("#121214"), GridMajor: FromHex("#262629"), AxisLine: FromHex("#FF9F0A"),
            AmbientLight: FromHex("#48484C"), KeyLight: FromHex("#E2DACB"), FillLight: FromHex("#6A5A40"), Selection: FromHex("#FF9F0A"));
    }
}
