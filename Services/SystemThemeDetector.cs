using Microsoft.Win32;

namespace AIArchitectStudio.Services;

/// <summary>
/// Системная тема Windows: ключ <c>AppsUseLightTheme</c> в <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>
/// (1 — светлые приложения, 0 — тёмные). При первом запуске приложение берёт отсюда Cupertino Light или Cupertino Dark.
/// </summary>
public static class SystemThemeDetector
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>true — светлая, false — тёмная, null — ключа нет (Windows 10 до 1809, политика) или реестр недоступен.</summary>
    public static bool? AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") switch
            {
                int value => value != 0,
                _ => null
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Тема приложения, соответствующая системной (без ключа — тёмная, как по умолчанию).</summary>
    public static string PreferredThemeName() => AppsUseLightTheme() == true ? ThemeService.CupertinoLight : ThemeService.CupertinoDark;
}
