using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIArchitectStudio.Views;

/// <summary>
/// Общие построители содержимого стеклянных окон (библиотека ГОСТ/СП, история изменений, справочный центр): текст, карточки,
/// чипы, кнопки с глифом — в кистях и стилях темы (Application.Resources), без эмодзи.
/// </summary>
internal static class DialogKit
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static Style StyleRes(string key) => (Style)Application.Current.Resources[key];

    public static FontFamily Mono => (FontFamily)Application.Current.Resources["MonoFont"];

    public static SolidColorBrush Hex(string hex, byte alpha = 0xFF)
    {
        string h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];
        byte r = Convert.ToByte(h[..2], 16), g = Convert.ToByte(h[2..4], 16), b = Convert.ToByte(h[4..6], 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, r, g, b));
    }

    public static TextBlock Text(string text, double size, bool strong = false, bool secondary = false, bool wrap = false, bool mono = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = mono ? Mono : FontFamily.XamlAutoFontFamily,
        Foreground = Res(secondary ? "TextSecondaryBrush" : "TextPrimaryBrush")
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        CharacterSpacing = 40,
        FontWeight = FontWeights.SemiBold,
        Foreground = Res("TextSecondaryBrush")
    };

    public static FontIcon Glyph(string glyph, double size = 13, string brush = "AccentBrush") =>
        new() { Glyph = glyph, FontSize = size, Foreground = Res(brush), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Квадратный бейдж с глифом (как у карточек инспектора).</summary>
    public static Border GlyphBadge(string glyph, double size = 28, Brush? background = null, Brush? foreground = null) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 3.5),
        Background = background ?? Res("AccentSoftBrush"),
        VerticalAlignment = VerticalAlignment.Top,
        Child = new FontIcon { Glyph = glyph, FontSize = size * 0.46, Foreground = foreground ?? Res("AccentBrush") }
    };

    /// <summary>Карточка на подложке темы со скруглением 12 и обводкой.</summary>
    public static Border Card(UIElement child, double padding = 12, bool accent = false) => new()
    {
        Background = Res(accent ? "AccentSoftBrush" : "TrackBrush"),
        BorderBrush = accent ? Res("AccentBrush") : Res("StudioBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(padding),
        Child = child
    };

    /// <summary>Чип: текст на полупрозрачном фоне цвета (по умолчанию акцент).</summary>
    public static Border Chip(string text, string? hex = null, double size = 10)
    {
        var fg = hex is null ? Res("AccentBrush") : Hex(hex);
        return new Border
        {
            Background = hex is null ? Res("AccentSoftBrush") : Hex(hex, 0x26),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = fg }
        };
    }

    /// <summary>Кнопка панели с глифом и подписью (стиль PanelButtonStyle; accent — акцентная подложка).</summary>
    public static Button ActionButton(string glyph, string text, RoutedEventHandler click, bool accent = false, bool enabled = true, string? tip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content,
            Height = 30,
            Padding = new Thickness(10, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            IsEnabled = enabled,
            Background = Res(accent ? "AccentSoftBrush" : "HoverBrush"),
            Foreground = Res(accent ? "AccentBrush" : "TextPrimaryBrush")
        };
        if (tip is not null) ToolTipService.SetToolTip(b, tip);
        b.Click += click;
        return b;
    }

    /// <summary>Поле поиска без рамки на подложке темы.</summary>
    public static TextBox SearchBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        FontSize = 12,
        MinHeight = 30,
        Padding = new Thickness(10, 5, 8, 5),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(0),
        Background = Res("CardBrush"),
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Сегмент (RadioButton стиля SegmentRadioStyle) на дорожке SegmentTrackStyle.</summary>
    public static Border Segments(IReadOnlyList<(string Text, object Tag)> items, object selected, Action<object> onSelect, string group)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, tag) in items)
        {
            var rb = new RadioButton { Content = text, Tag = tag, GroupName = group, Style = StyleRes("SegmentRadioStyle"), MinWidth = 64, IsChecked = Equals(tag, selected) };
            rb.Checked += (s, _) => onSelect(((RadioButton)s).Tag);
            panel.Children.Add(rb);
        }
        return new Border { Style = StyleRes("SegmentTrackStyle"), Child = panel, HorizontalAlignment = HorizontalAlignment.Left };
    }

    public static Border Divider(double margin = 4) => new() { Height = 1, Margin = new Thickness(0, margin, 0, margin), Background = Res("StudioBorderBrush") };
}
