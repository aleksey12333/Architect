using AIArchitectStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace AIArchitectStudio.Views;

/// <summary>
/// Заставка запуска 640 × 400 без рамки: строгий знак-чертёж здания проявляется и вырастает (0.9 → 1.0, 0.8 с), внизу сменяются
/// статусы фоновой загрузки («Запуск вычислительного ядра…», «Инициализация 3D-ядра…», «Проверка модулей…»). Шаги ведёт App.
/// </summary>
public sealed partial class SplashWindow : Window
{
    private readonly LocalizationService _loc;

    public SplashWindow(ThemeService theme, LocalizationService loc)
    {
        _loc = loc;
        InitializeComponent();
        Title = AppInfo.ProductName;
        WindowChrome.ConfigureBorderless(this, 640, 400);
        WindowChrome.ApplyTheme(this, theme);
        TaglineText.Text = loc.Get("SplashTagline");
        VersionText.Text = loc.Format("VersionLabel", AppInfo.VersionText);
        Root.Loaded += (_, _) => ((Storyboard)Root.Resources["IntroStoryboard"]).Begin();
    }

    /// <summary>Смена строки статуса: старая гаснет, новая проявляется.</summary>
    public async Task ShowStatusAsync(string key)
    {
        if (StatusText.Text.Length > 0) await WindowChrome.PlayAsync(WindowChrome.Fade(StatusText, 1, 0, 1, 1, 120));
        StatusText.Text = _loc.Get(key);
        await WindowChrome.PlayAsync(WindowChrome.Fade(StatusText, 0, 1, 1, 1, 220));
    }

    /// <summary>Плавное исчезновение содержимого (стекло окна гаснет вместе с закрытием).</summary>
    public async Task FadeOutAsync() => await WindowChrome.PlayAsync(WindowChrome.Fade(Root, 1, 0, 1, 0.97, 260));
}
