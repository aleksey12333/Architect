using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace AIArchitectStudio.Views;

/// <summary>
/// Окна без системной рамки (заставка, стартовое окно): без заголовка и кнопок Windows, матовое стекло
/// DesktopAcrylic, скругление 8 px и мягкая тень DWM (Windows 11), фиксированный размер по центру рабочей области.
/// В WinUI 3 нет WPF-свойств WindowStyle/AllowsTransparency — тот же вид даёт OverlappedPresenter.SetBorderAndTitleBar(true, false):
/// рамка в 1 px остаётся (она и несёт тень и скругление), заголовок исчезает.
/// </summary>
public static class WindowChrome
{
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;

    public static void ConfigureBorderless(Window window, double widthDip, double heightDip, bool minimizable = false)
    {
        window.ExtendsContentIntoTitleBar = true;
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = minimizable;
            presenter.SetBorderAndTitleBar(true, false);
        }

        nint hwnd = WindowNative.GetWindowHandle(window);
        int corner = DwmCornerRound;
        _ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));

        double scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;
        var size = new SizeInt32((int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale));
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));

        if (DesktopAcrylicController.IsSupported()) window.SystemBackdrop = new DesktopAcrylicBackdrop();
    }

    /// <summary>Тема корня окна (и стекла DesktopAcrylic, которое следует ActualTheme корня) — по активной теме приложения.</summary>
    public static void ApplyTheme(Window window, Services.ThemeService theme)
    {
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = theme.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark;
    }

    /// <summary>Запускает раскадровку и ждёт её окончания.</summary>
    public static Task PlayAsync(Storyboard storyboard)
    {
        var done = new TaskCompletionSource();
        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            done.TrySetResult();
        }
        storyboard.Completed += OnCompleted;
        storyboard.Begin();
        return done.Task;
    }

    /// <summary>
    /// Плавное появление / исчезновение элемента: Opacity и равномерный масштаб (ScaleTransform вокруг центра).
    /// Исчезновение — ускоряющееся, появление — с замедлением в конце (как анимации окон macOS).
    /// </summary>
    public static Storyboard Fade(UIElement target, double fromOpacity, double toOpacity, double fromScale, double toScale, int milliseconds, int delayMilliseconds = 0)
    {
        if (target.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform();
            target.RenderTransform = scale;
        }
        target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

        EasingFunctionBase ease = toOpacity >= fromOpacity
            ? new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var begin = TimeSpan.FromMilliseconds(delayMilliseconds);

        var storyboard = new Storyboard();
        Add(storyboard, target, "Opacity", fromOpacity, toOpacity, duration, begin, ease);
        Add(storyboard, scale, "ScaleX", fromScale, toScale, duration, begin, ease);
        Add(storyboard, scale, "ScaleY", fromScale, toScale, duration, begin, ease);
        return storyboard;
    }

    private static void Add(Storyboard storyboard, DependencyObject target, string property, double from, double to,
        Duration duration, TimeSpan begin, EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = duration, BeginTime = begin, EasingFunction = ease };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
