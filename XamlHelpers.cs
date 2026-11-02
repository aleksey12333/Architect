using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace AIArchitectStudio;

/// <summary>Статические функции для x:Bind (вместо конвертеров).</summary>
public static class XamlHelpers
{
    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Профиль работы для шаблонов данных, где нет доступа к ViewModel окна (обновляет MainViewModel.ExperienceProfile).</summary>
    public static Models.UserExperienceProfile CurrentProfile { get; set; } = Models.UserExperienceProfile.StructuralEngineer;

    /// <summary>Инженерные цифры в шаблонах (прогноз K_исп вариантов Совета) — только профилю «КЖ и расчёты».</summary>
    public static Visibility VisEngineer(string? text) =>
        CurrentProfile == Models.UserExperienceProfile.StructuralEngineer && !string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Видимость при выполнении обоих условий (данные есть и профиль их показывает).</summary>
    public static Visibility VisAll(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Видно, только если ни одно из условий не выполнено (палитра элементов прячется под левыми панелями).</summary>
    public static Visibility VisNone(bool first, bool second, bool third) => first || second || third ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Ширина колонки сегмента, скрываемого профилем: звезда или 0.</summary>
    public static GridLength StarIf(bool visible, double stars) => new(visible ? stars : 0, GridUnitType.Star);

    public static HorizontalAlignment ChatAlign(bool isUser) => isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public static Brush ChatBubble(bool isUser, bool isError) =>
        Resource(isError ? "AccentSoftBrush" : isUser ? "UserBubbleBrush" : "AssistantBubbleBrush");

    public static Brush SpecRowBackground(bool isHeader) =>
        isHeader ? Resource("TrackBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>Свёрнутый остров прижимается к верху и не растягивается по высоте.</summary>
    public static VerticalAlignment CollapsedAlign(bool collapsed) => collapsed ? VerticalAlignment.Top : VerticalAlignment.Stretch;

    /// <summary>Шеврон сворачивания: вниз — развернуть, вверх — свернуть.</summary>
    public static string CollapseGlyph(bool collapsed) => collapsed ? "" : "";

    /// <summary>Видимость вкладки острова: вкладка активна и остров не свёрнут.</summary>
    public static Visibility VisBoth(bool tabActive, bool collapsed) => tabActive && !collapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Значение в метрах для моноширинных подписей (две цифры после точки).</summary>
    public static string Meters(double value) => double.IsFinite(value) ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "—";

    public static FontWeight Weight(bool bold) => bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>Локализованная строка для шаблонов данных (где нет доступа к L окна).</summary>
    public static string Text(string key) =>
        (Application.Current as App) is not null && App.Services.GetService(typeof(Services.LocalizationService)) is Services.LocalizationService loc ? loc.Get(key) : key;

    /// <summary>Рамка карточки (лист PDF): акцент — выбран, тонкая граница — нет.</summary>
    public static Brush SelectionBorder(bool selected) => Resource(selected ? "AccentBrush" : "StudioBorderBrush");

    /// <summary>Сплошная кисть цвета (шкала K_исп, статус продавливания).</summary>
    public static Brush ColorBrush(Windows.UI.Color color) => new SolidColorBrush(color);

    /// <summary>Кисть «#RRGGBB» (образец цвета слоя пирога).</summary>
    public static Brush HexBrush(string? hex)
    {
        var s = (hex ?? string.Empty).TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return Resource("TextSecondaryBrush");
    }

    /// <summary>Полупрозрачная подложка цвета «#RRGGBB» (бейдж роли агента, номер варианта).</summary>
    public static Brush HexSoftBrush(string? hex)
    {
        var s = (hex ?? string.Empty).TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return Resource("TrackBrush");
    }

    /// <summary>Цвет важности коллизии: критическая — алый, предупреждение — янтарный.</summary>
    public static Brush SeverityBrush(bool critical) =>
        new SolidColorBrush(critical ? Windows.UI.Color.FromArgb(255, 0xEF, 0x44, 0x44) : Windows.UI.Color.FromArgb(255, 0xF5, 0x9E, 0x0B));

    /// <summary>Цвет результата нормативной проверки: выполнена — зелёный, не выполнена — алый.</summary>
    public static Brush PassBrush(bool passes) =>
        new SolidColorBrush(passes ? Windows.UI.Color.FromArgb(255, 0x30, 0xD1, 0x58) : Windows.UI.Color.FromArgb(255, 0xFF, 0x45, 0x3A));

    /// <summary>
    /// «Внутренние рассуждения агента» в ленте: свёрнуто — последние ~420 символов (при потоковой генерации видна свежая
    /// мысль), развёрнуто — полностью (до 24 000 символов).
    /// </summary>
    public static string ReasoningPreview(string? reasoning, bool expanded)
    {
        if (string.IsNullOrEmpty(reasoning)) return string.Empty;
        if (expanded) return reasoning.Length <= 24000 ? reasoning : "…" + reasoning[^24000..];
        string tail = reasoning.Length <= 420 ? reasoning : "…" + reasoning[^420..].TrimStart();
        return tail.Replace("\r", string.Empty).Replace("\n\n", "\n");
    }

    /// <summary>Строк рассуждений: свёрнуто — 5, развёрнуто — без ограничения.</summary>
    public static int ReasoningLines(bool expanded) => expanded ? 0 : 5;

    /// <summary>Ширина заполнения индикатора K_исп (доля 0…1 от полной ширины).</summary>
    public static double BarWidth(double fraction, double full) => Math.Max(3, Math.Clamp(fraction, 0, 1) * full);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], ImageSource> Thumbnails = new();

    /// <summary>Миниатюра вложения (JPEG/PNG в памяти) — декодируется один раз на массив байтов.</summary>
    public static ImageSource? Thumbnail(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        if (Thumbnails.TryGetValue(bytes, out var cached)) return cached;
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelHeight = 120 };
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            System.WindowsRuntimeSystemExtensions.AsTask(stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes))).GetAwaiter().GetResult();
            stream.Seek(0);
            bitmap.SetSource(stream);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null;
        }
        Thumbnails.AddOrUpdate(bytes, bitmap);
        return bitmap;
    }

    /// <summary>Подпись вида вложения: «3D-вид» / «План».</summary>
    public static string AttachmentKindText(Services.AI.AttachmentKind kind) =>
        Text(kind is Services.AI.AttachmentKind.ViewportSnapshot or Services.AI.AttachmentKind.NodeSnapshot ? "AttachKindView" : "AttachKindPlan");

    /// <summary>Миниатюры сообщения: первая из списка (или null).</summary>
    public static ImageSource? FirstThumbnail(IReadOnlyList<byte[]>? list) => list is { Count: > 0 } ? Thumbnail(list[0]) : null;

    public static ImageSource? SecondThumbnail(IReadOnlyList<byte[]>? list) => list is { Count: > 1 } ? Thumbnail(list[1]) : null;

    public static Visibility VisCount(IReadOnlyList<byte[]>? list, int min) => list is not null && list.Count >= min ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Resource(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
