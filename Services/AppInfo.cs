namespace AIArchitectStudio.Services;

/// <summary>Сведения о сборке для заставки и стартового окна.</summary>
public static class AppInfo
{
    /// <summary>Название продукта в интерфейсе (заголовки окон, заставка, файлы экспорта). Пространства имён не меняются.</summary>
    public const string ProductName = "Architect Studio Pro";

    /// <summary>Версия «мажор.минор» из сборки (например «1.0»).</summary>
    public static string VersionText
    {
        get
        {
            var version = typeof(AppInfo).Assembly.GetName().Version;
            return version is null ? "1.0" : $"{version.Major}.{version.Minor}";
        }
    }
}
