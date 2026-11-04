using AIArchitectStudio.Models.Geometry;
using HelixToolkit.WinUI;
using SharpDX;

namespace AIArchitectStudio.Rendering;

/// <summary>
/// Шкала карты напряжений K_исп: 0–0.35 синий #2563EB (запас), 0.35–0.70 изумрудный #10B981 (оптимально),
/// 0.70–0.95 янтарный #F59E0B (близко к пределу), &gt; 0.95 алый #EF4444 (перегрузка). Между диапазонами — плавный переход ±0.05.
/// </summary>
public static class StressPalette
{
    public static readonly Color4 Blue = new(0x25 / 255f, 0x63 / 255f, 0xEB / 255f, 1f);
    public static readonly Color4 Green = new(0x10 / 255f, 0xB9 / 255f, 0x81 / 255f, 1f);
    public static readonly Color4 Amber = new(0xF5 / 255f, 0x9E / 255f, 0x0B / 255f, 1f);
    public static readonly Color4 Red = new(0xEF / 255f, 0x44 / 255f, 0x44 / 255f, 1f);

    /// <summary>Нейтральный «призрачный» тон для ненесущих элементов в режиме карты.</summary>
    public static readonly Color4 Ghost = new(0.62f, 0.64f, 0.68f, 0.16f);

    private const float Blend = 0.05f;

    public static Color4 ColorFor(double k)
    {
        float v = double.IsFinite(k) ? (float)Math.Max(0, k) : 2f;
        if (v <= 0.35f - Blend) return Blue;
        if (v < 0.35f + Blend) return Color4.Lerp(Blue, Green, (v - 0.35f + Blend) / (2 * Blend));
        if (v <= 0.70f - Blend) return Green;
        if (v < 0.70f + Blend) return Color4.Lerp(Green, Amber, (v - 0.70f + Blend) / (2 * Blend));
        if (v <= 0.95f - Blend) return Amber;
        if (v < 0.95f + Blend) return Color4.Lerp(Amber, Red, (v - 0.95f + Blend) / (2 * Blend));
        return Red;
    }

    public static Windows.UI.Color ToUi(Color4 c) =>
        Windows.UI.Color.FromArgb((byte)(c.Alpha * 255), (byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255));
}

/// <summary>Матовые архитектурные материалы рендера (PhongMaterial) по классификации <see cref="MaterialCatalog"/>.</summary>
public static class ArchitecturalMaterials
{
    private static readonly Dictionary<MaterialKind, PhongMaterial> Cache = [];
    private static readonly Dictionary<(MaterialKind, Color4), PhongMaterial> SelectedCache = [];

    public static bool IsTransparent(MaterialKind kind) => MaterialCatalog.IsTransparent(kind);

    public static Color4 BaseColor(MaterialKind kind) => ToColor4(MaterialCatalog.BaseColor(kind));

    public static Color4 ToColor4(BimColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);
    /// <summary>Собственный (не кэшируемый) материал карты напряжений — цвет меняется анимацией без пересоздания.</summary>
    public static PhongMaterial CreateStress(Color4 color)
    {
        var m = Create(color, MaterialKind.Concrete);
        m.EmissiveColor = new Color4(color.Red * 0.22f, color.Green * 0.22f, color.Blue * 0.22f, 1f);
        return m;
    }

    /// <summary>Перекраска материала карты напряжений (диффузный, фоновый и самосвечение).</summary>
    public static void SetStressColor(PhongMaterial m, Color4 color)
    {
        m.DiffuseColor = color;
        m.AmbientColor = new Color4(color.Red * 0.6f, color.Green * 0.6f, color.Blue * 0.6f, 1f);
        m.EmissiveColor = new Color4(color.Red * 0.22f, color.Green * 0.22f, color.Blue * 0.22f, 1f);
    }

    public static PhongMaterial Get(MaterialKind kind)
    {
        if (Cache.TryGetValue(kind, out var m)) return m;
        m = Create(BaseColor(kind), kind);
        Cache[kind] = m;
        return m;
    }

    // ───────────── Цвета фасада с фотографии (волна 4) ─────────────

    private static readonly Dictionary<(MaterialKind, string), PhongMaterial> TintCache = [];

    /// <summary>Часть элемента окрашивается в цвет фасада: тела и стекло; обвязка, пустоты, срезы и служебные слои — нет.</summary>
    public static bool IsTintable(MaterialKind kind) => kind is not (MaterialKind.Frame or MaterialKind.Void or MaterialKind.DoorWood or MaterialKind.Water
        or MaterialKind.Insulation or MaterialKind.Screed or MaterialKind.Waterproofing or MaterialKind.SectionCut or MaterialKind.InsulationCut
        or MaterialKind.ScreedCut or MaterialKind.WaterproofingCut or MaterialKind.FinishCut);

    /// <summary>Материал в цвете фасада «#RRGGBB»: стекло сохраняет прозрачность (0.55), тела — непрозрачные.</summary>
    public static PhongMaterial GetTinted(MaterialKind kind, string hex)
    {
        if (TintCache.TryGetValue((kind, hex), out var m)) return m;
        if (!Models.CurvedDesign.TryParseHex(hex, out var rgb)) return Get(kind);
        var color = new Color4(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, IsTransparent(kind) ? 0.55f : 1f);
        m = Create(color, kind);
        TintCache[(kind, hex)] = m;
        return m;
    }

    // ───────────── Архитектурный макет (White Clay) ─────────────

    /// <summary>Благородный матовый гипс макета #F4F4F6.</summary>
    public static readonly Color4 ClayColor = Hex(0xF4F4F6);

    /// <summary>Графитовые рёбра макета #2C2C2E.</summary>
    public static readonly Windows.UI.Color ClayEdgeColor = Windows.UI.Color.FromArgb(255, 0x2C, 0x2C, 0x2E);

    private static readonly Dictionary<MaterialKind, PhongMaterial> ClayCache = [];
    private static readonly Dictionary<(MaterialKind, Color4), PhongMaterial> ClaySelectedCache = [];

    /// <summary>
    /// Материал макета: все тела — единый матовый гипс без бликов; стекло и вода — тот же гипс полупрозрачным «матовым
    /// стеклом» (окна читаются), пустоты проёмов остаются служебными, срез Section Box — светло-серым гипсом.
    /// </summary>
    public static PhongMaterial GetClay(MaterialKind kind)
    {
        if (kind == MaterialKind.Void) return Get(kind);
        if (ClayCache.TryGetValue(kind, out var m)) return m;
        m = CreateClay(ClayBase(kind));
        ClayCache[kind] = m;
        return m;
    }

    /// <summary>Выделенный элемент макета: гипс с лёгким подмешиванием акцента темы.</summary>
    public static PhongMaterial GetClaySelected(MaterialKind kind, Color4 accent)
    {
        if (kind == MaterialKind.Void) return GetSelected(kind, accent);
        if (ClaySelectedCache.TryGetValue((kind, accent), out var m)) return m;
        var baseColor = ClayBase(kind);
        var mixed = Color4.Lerp(baseColor, accent, 0.3f);
        mixed.Alpha = baseColor.Alpha < 1 ? Math.Max(baseColor.Alpha, 0.5f) : 1f;
        m = CreateClay(mixed);
        m.EmissiveColor = new Color4(accent.Red * 0.12f, accent.Green * 0.12f, accent.Blue * 0.12f, 1f);
        ClaySelectedCache[(kind, accent)] = m;
        return m;
    }

    private static Color4 ClayBase(MaterialKind kind) => kind switch
    {
        MaterialKind.Glass or MaterialKind.Water => new Color4(ClayColor.Red, ClayColor.Green, ClayColor.Blue, 0.42f),
        MaterialKind.SectionCut or MaterialKind.InsulationCut or MaterialKind.ScreedCut or MaterialKind.WaterproofingCut or MaterialKind.FinishCut => Hex(0xC7C7CC),
        _ => ClayColor
    };

    private static PhongMaterial CreateClay(Color4 color) => new()
    {
        DiffuseColor = color,
        AmbientColor = new Color4(color.Red * 0.7f, color.Green * 0.7f, color.Blue * 0.7f, 1f),
        SpecularColor = new Color4(0.03f, 0.03f, 0.03f, 1f),
        SpecularShininess = 4f,
        EmissiveColor = new Color4(0.05f, 0.05f, 0.055f, 1f)
    };

    /// <summary>Вариант материала для выделенного элемента: подмешивание акцентного цвета темы.</summary>
    public static PhongMaterial GetSelected(MaterialKind kind, Color4 accent)
    {
        if (SelectedCache.TryGetValue((kind, accent), out var m)) return m;
        var baseColor = BaseColor(kind);
        var mixed = Color4.Lerp(baseColor, accent, 0.35f);
        mixed.Alpha = IsTransparent(kind) ? Math.Max(baseColor.Alpha, 0.45f) : 1f;
        m = Create(mixed, kind);
        m.EmissiveColor = new Color4(accent.Red * 0.18f, accent.Green * 0.18f, accent.Blue * 0.18f, 1f);
        SelectedCache[(kind, accent)] = m;
        return m;
    }

    private static PhongMaterial Create(Color4 color, MaterialKind kind)
    {
        bool glossy = kind is MaterialKind.Glass or MaterialKind.Water or MaterialKind.Steel;
        return new PhongMaterial
        {
            DiffuseColor = color,
            AmbientColor = new Color4(color.Red * 0.6f, color.Green * 0.6f, color.Blue * 0.6f, 1f),
            SpecularColor = glossy ? new Color4(0.55f, 0.55f, 0.55f, 1f) : new Color4(0.06f, 0.06f, 0.06f, 1f),
            SpecularShininess = glossy ? 60f : 8f,
            EmissiveColor = new Color4(0, 0, 0, 1)
        };
    }

    private static Color4 Hex(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
