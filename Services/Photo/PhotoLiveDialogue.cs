using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;

namespace AIArchitectStudio.Services.Photo;

/// <summary>Сборка строк ленты диалога (интерполяция с конкатенацией — культура потока интерфейса).</summary>
internal static class PhotoText
{
    public static string S(string text) => text;
}

/// <summary>Прогресс без захвата контекста синхронизации: вызывающий сам маршалит в UI (через <see cref="Progress{T}"/>).</summary>
internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

/// <summary>
/// Что модель со зрением сама прочитала на снимке (блок "photoReading" ответа): морфология, этажность по рядам окон, стилобат,
/// ярусы, консоли, число дуг фасада и стрела, угол плана, скругление, покрытие, двор, колоннада, остекление, габариты, доверие.
/// </summary>
public sealed record PhotoReading
{
    public PhotoMorphology? Morphology { get; init; }
    public string? MorphologyText { get; init; }
    public int? Floors { get; init; }
    public int? PodiumFloors { get; init; }
    public int? Tiers { get; init; }
    public int? Cantilevers { get; init; }
    public int? FacadeArcs { get; init; }
    public double? ArcBulgeRatio { get; init; }
    public double? CornerAngleDeg { get; init; }
    public double? CornerRadiusM { get; init; }
    public PhotoRoofKind? Roof { get; init; }
    public bool? Courtyard { get; init; }
    public int? ColonnadeColumns { get; init; }
    public int? InclinedPylons { get; init; }
    public double? GlazingRatio { get; init; }
    public double? WidthM { get; init; }
    public double? DepthM { get; init; }
    public double? HeightM { get; init; }
    public double Confidence { get; init; } = 0.6;

    public string Describe() => PhotoText.S(
        $"{MorphologyText ?? "?"}, этажей {Floors?.ToString() ?? "?"}" + (PodiumFloors > 0 ? $", стилобат {PodiumFloors}" : "") + (Tiers > 0 ? $", ярусов {Tiers}" : "") +
        (FacadeArcs > 0 ? $", дуг фасада {FacadeArcs}" : "") + (ArcBulgeRatio is { } a && Math.Abs(a) > 0.01 ? $", стрела/хорда {a:+0.00;−0.00}" : "") +
        (CornerAngleDeg > 0 ? $", угол {CornerAngleDeg:0}°" : "") + (Roof is { } r ? $", кровля {PhotoArchitectureDescriptor.RoofCode(r)}" : "") +
        (Courtyard == true ? ", двор" : "") + (ColonnadeColumns > 0 ? $", колонн портика {ColonnadeColumns}" : "") + $", доверие {Confidence:0.00}");

    /// <summary>Блок photoReading из сырого JSON ответа (на любом уровне вложенности; числа — и строками).</summary>
    public static PhotoReading? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return Find(doc.RootElement, 0) is { } el ? FromElement(el) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Find(JsonElement e, int depth)
    {
        if (depth > 4 || e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals("photoReading", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("photo_reading", StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.Object ? p.Value : null;
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object && Find(p.Value, depth + 1) is { } found) return found;
        return null;
    }

    private static PhotoReading FromElement(JsonElement e)
    {
        string? morph = Str(e, "morphology", "shape", "type");
        bool ringWord = morph is not null && (morph.Contains("ring", StringComparison.OrdinalIgnoreCase) || morph.Contains("annular", StringComparison.OrdinalIgnoreCase) ||
                                              morph.Contains("courtyard", StringComparison.OrdinalIgnoreCase) || morph.Contains("кольц", StringComparison.OrdinalIgnoreCase));
        bool? courtyard = Bool(e, "courtyard", "atrium", "innerCourtyard") ?? (ringWord ? true : null);
        return new PhotoReading
        {
            MorphologyText = morph,
            Morphology = MapMorphology(morph),
            Floors = Int(e, "floors", "storeys", "floorCount"),
            PodiumFloors = Int(e, "podiumFloors", "podium", "stylobateFloors"),
            Tiers = Int(e, "tiers", "setbacks", "terraces"),
            Cantilevers = Int(e, "cantilevers", "overhangs"),
            FacadeArcs = Int(e, "facadeArcs", "arcs", "facadeCurves"),
            ArcBulgeRatio = Num(e, "arcBulgeRatio", "bulgeRatio", "sagittaRatio"),
            CornerAngleDeg = Num(e, "cornerAngleDeg", "planAngleDeg", "acuteAngleDeg"),
            CornerRadiusM = Num(e, "cornerRadiusM", "cornerRadius", "noseRadiusM"),
            Roof = MapRoof(Str(e, "roof", "roofType")),
            Courtyard = courtyard,
            ColonnadeColumns = Int(e, "colonnadeColumns", "columns", "porticoColumns"),
            InclinedPylons = Int(e, "inclinedPylons", "pylons", "buttresses"),
            GlazingRatio = Num(e, "glazingRatio", "glazing"),
            WidthM = Num(e, "widthM", "width"),
            DepthM = Num(e, "depthM", "depth"),
            HeightM = Num(e, "heightM", "height"),
            Confidence = Math.Clamp(Num(e, "confidence") ?? 0.6, 0, 1)
        };
    }

    public static PhotoMorphology? MapMorphology(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string n = s.Trim().ToLowerInvariant();
        if (n.Contains("ring") || n.Contains("rotunda") || n.Contains("annular") || n.Contains("circular") || n.Contains("dome") || n.Contains("ротонд") || n.Contains("кольц")) return PhotoMorphology.Rotunda;
        if (n.Contains("arch") || n.Contains("vault") || n.Contains("arena") || n.Contains("hall") || n.Contains("свод") || n.Contains("арк")) return PhotoMorphology.ArchSpan;
        if (n.Contains("cascad") || n.Contains("terrac") || n.Contains("stepp") || n.Contains("setback") || n.Contains("каскад")) return PhotoMorphology.Cascading;
        if (n.Contains("triang") || n.Contains("flatiron") || n.Contains("wedge") || n.Contains("acute") || n.Contains("утюг") || n.Contains("треуг")) return PhotoMorphology.Triangular;
        if (n.Contains("curv") || n.Contains("wave") || n.Contains("s_") || n.Contains("s-") || n.Contains("bent") || n.Contains("изог") || n.Contains("волн")) return PhotoMorphology.Curved;
        if (n.Contains("round")) return PhotoMorphology.Rounded;
        if (n.Contains("pitch") || n.Contains("gable") || n.Contains("скат")) return PhotoMorphology.Pitched;
        if (n.Contains("box") || n.Contains("rect") || n.Contains("прямоуг")) return PhotoMorphology.Box;
        return null;
    }

    public static PhotoRoofKind? MapRoof(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string n = s.Trim().ToLowerInvariant();
        if (n.Contains("mansard") || n.Contains("мансард")) return PhotoRoofKind.Mansard;
        if (n.Contains("dome") || n.Contains("купол")) return PhotoRoofKind.Dome;
        if (n.Contains("vault") || n.Contains("barrel") || n.Contains("arch") || n.Contains("свод")) return PhotoRoofKind.Vault;
        if (n.Contains("gable") || n.Contains("hip") || n.Contains("pitch")) return PhotoRoofKind.Gable;
        if (n.Contains("cone")) return PhotoRoofKind.Cone;
        if (n.Contains("parapet")) return PhotoRoofKind.Parapet;
        if (n.Contains("flat")) return PhotoRoofKind.Flat;
        return null;
    }

    private static JsonElement? Prop(JsonElement e, string[] names)
    {
        foreach (var p in e.EnumerateObject())
            foreach (var n in names)
                if (p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement e, params string[] names) =>
        Prop(e, names) is { } v ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : null : null;

    private static double? Num(JsonElement e, params string[] names)
    {
        if (Prop(e, names) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString()?.Replace(',', '.').Trim('°', ' ', 'm', 'м'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static int? Int(JsonElement e, params string[] names) => Num(e, names) is { } d && d >= 0 && d < 1000 ? (int)Math.Round(d) : null;

    private static bool? Bool(JsonElement e, params string[] names) =>
        Prop(e, names) is { } v ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() is "true" or "yes" or "да", _ => null } : null;
}

/// <summary>
/// Перекрёстная сверка паспорта Визуального ядра с чтением модели со зрением. Модель, которая видела снимок, уточняет то, в чём
/// Визуальное ядро слабо: морфологию при ненадёжном силуэте (аэрофото, соседние дома) или невысоком доверии, этажность (ряды
/// окон; расхождение ≈ 2× — кратная ошибка периода Визуального ядра), стилобат, ярусы, число и знак дуг фасада, угол плана,
/// мансарду, двор, колоннаду. Модель без снимка (шлюз не принял изображение) паспорт не меняет — её числа это эхо матрицы.
/// </summary>
public static class PhotoReadingReconciler
{
    public static PhotoArchitectureDescriptor Merge(PhotoArchitectureDescriptor cv, PhotoReading? reading, bool sawImage)
    {
        var t = cv.Clone();
        if (reading is null)
        {
            t.ReconcileNotes.Add("модель не вернула photoReading — числа Визуального ядра");
            return t;
        }
        if (!sawImage)
        {
            t.ReconcileNotes.Add("модель не видела снимок (матрица без снимка) — паспорт Визуального ядра без изменений");
            return t;
        }
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        bool cvWeak = cv.SegmentationQuality < 0.55 || cv.Confidence < 0.7;
        // Модель может назвать форму «box», но перечислить признаки иной: несколько дуг фасада, острый угол, ярусы, двор —
        // морфология выводится из признаков (их модель видит надёжнее, чем подбирает слово).
        var implied = reading.Morphology;
        bool plain = implied is null or PhotoMorphology.Box or PhotoMorphology.Rounded;
        // Порядок: двор → острый угол плана (скруглённый нос «утюга» — тоже дуга, но план треугольный) → дуги фасада → ярусы.
        if (reading.Courtyard == true) implied = PhotoMorphology.Rotunda;
        else if ((plain || implied == PhotoMorphology.Curved && reading.FacadeArcs is null or <= 1) && reading.CornerAngleDeg is > 15 and < 75) implied = PhotoMorphology.Triangular;
        else if (plain && (reading.FacadeArcs >= 2 || reading.ArcBulgeRatio is { } ar && Math.Abs(ar) >= 0.05)) implied = PhotoMorphology.Curved;
        else if (plain && reading.Tiers >= 3) implied = PhotoMorphology.Cascading;
        if (implied != reading.Morphology && implied is { } im)
            t.ReconcileNotes.Add($"модель назвала форму «{reading.MorphologyText}», но её признаки (дуг {reading.FacadeArcs}, угол {reading.CornerAngleDeg:0}°, ярусов {reading.Tiers}, двор {reading.Courtyard}) " +
                                 $"соответствуют «{PhotoArchitectureDescriptor.MorphologyCode(im)}»");
        if (implied is { } m && m != cv.Morphology && m != PhotoMorphology.Auto)
        {
            if (cvWeak || reading.Confidence >= 0.65)
            {
                t.Morphology = m;
                t.Confidence = Math.Max(cv.Confidence, reading.Confidence);
                t.ReconcileNotes.Add($"морфология: Визуальное ядро «{PhotoArchitectureDescriptor.MorphologyCode(cv.Morphology)}» (силуэт {cv.SegmentationQuality:0.00}, доверие {cv.Confidence:0.00}) → " +
                                     $"модель со зрением «{PhotoArchitectureDescriptor.MorphologyCode(m)}»");
                t.Roof = m switch
                {
                    PhotoMorphology.Rotunda => PhotoRoofKind.Dome,
                    PhotoMorphology.ArchSpan => PhotoRoofKind.Vault,
                    PhotoMorphology.Cascading => PhotoRoofKind.Parapet,
                    PhotoMorphology.Pitched => PhotoRoofKind.Gable,
                    _ => PhotoRoofKind.Flat
                };
            }
            else t.ReconcileNotes.Add($"морфология оставлена по Визуальному ядру ({PhotoArchitectureDescriptor.MorphologyCode(cv.Morphology)}): модель предложила {PhotoArchitectureDescriptor.MorphologyCode(m)} с доверием {reading.Confidence:0.00}");
        }
        if (reading.Courtyard == true && t.Morphology == PhotoMorphology.Rotunda) t.HasCourtyard = true;
        if (reading.Roof is { } roof && !(t.Morphology == PhotoMorphology.ArchSpan && roof != PhotoRoofKind.Vault))
        {
            if (roof == PhotoRoofKind.Mansard || t.Morphology is PhotoMorphology.Rotunda or PhotoMorphology.Pitched || cvWeak) t.Roof = roof;
        }

        // Этажность: модель считает ряды окон; согласие ±10 % при уверенном ритме — остаётся счёт ядра.
        int floorsBefore = cv.Floors;
        if (reading.Floors is { } rf and >= 1 and <= 120 && t.Morphology != PhotoMorphology.ArchSpan)
        {
            double ratio = (double)cv.Floors / rf;
            bool close = Math.Abs(cv.Floors - rf) <= Math.Max(1, rf * 0.1);
            if (close && cv.FloorConfidence >= 0.25 && !cvWeak) t.ReconcileNotes.Add($"этажность {cv.Floors}: Визуальное ядро и модель согласны (модель {rf})");
            else
            {
                bool harmonic = Math.Abs(ratio - 2) < 0.3 || Math.Abs(ratio - 0.5) < 0.12 || Math.Abs(ratio - 1.5) < 0.12;
                t.Floors = rf;
                t.ReconcileNotes.Add(harmonic
                    ? $"этажность: Визуальное ядро {cv.Floors} — кратная ошибка периода (полэтажа: верх и низ окна), принято по рядам окон модели — {rf}"
                    : $"этажность: Визуальное ядро {cv.Floors} (ритм {cv.FloorConfidence:0.00}) → {rf} по рядам окон, сосчитанным моделью");
            }
        }
        // Масштаб: высота этажа по морфологии, габариты — пропорционально этажности; габариты модели, если правдоподобны.
        t.FloorHeight = t.Morphology switch
        {
            PhotoMorphology.Rotunda when t.HasCourtyard => 4.0,
            PhotoMorphology.Rotunda => 5.0,
            PhotoMorphology.ArchSpan => 8.0,
            PhotoMorphology.Cascading => 3.1,
            PhotoMorphology.Triangular => 3.4,
            PhotoMorphology.Pitched => 3.0,
            _ => cv.FloorHeight
        };
        if (t.Morphology == PhotoMorphology.ArchSpan) t.Floors = 1;
        double scale = floorsBefore > 0 ? t.Floors * t.FloorHeight / Math.Max(1, floorsBefore * cv.FloorHeight) : 1;
        if (t.Morphology != cv.Morphology || Math.Abs(scale - 1) > 0.05)
        {
            t.HeightM = cv.HeightM * scale;
            t.WidthM = Math.Clamp(cv.WidthM * scale, 8, 260);
            t.DepthM = Math.Clamp(cv.DepthM * scale, 8, 200);
        }
        if (reading.WidthM is { } w and >= 6 and <= 300) t.WidthM = cvWeak ? w : (t.WidthM + w) / 2;
        if (reading.DepthM is { } dpt and >= 6 and <= 300) t.DepthM = cvWeak ? dpt : (t.DepthM + dpt) / 2;
        if (t.Morphology == PhotoMorphology.Cascading) t.DepthM = Math.Clamp(t.DepthM, 12, 24);
        if (reading.PodiumFloors is { } pf and >= 0 and <= 8) t.PodiumFloors = pf;
        if (reading.FacadeArcs is { } fa and >= 1 and <= 6 && t.Morphology == PhotoMorphology.Curved) t.FacadeArcs = fa;
        if (reading.ArcBulgeRatio is { } ab && Math.Abs(ab) is >= 0.02 and <= 0.45 && t.Morphology == PhotoMorphology.Curved)
            t.ArcBulgeRatio = Math.Clamp(Math.Abs(ab), 0.06, 0.3) * Math.Sign(ab);
        if (reading.CornerAngleDeg is { } ca and >= 20 and <= 89 && t.Morphology == PhotoMorphology.Triangular) t.CornerAngleDeg = ca;
        if (reading.CornerRadiusM is { } cr and > 0 and < 30) t.CornerRadiusRatio = Math.Clamp(cr / Math.Max(4, Math.Min(t.WidthM, t.DepthM)), 0.02, 0.45);
        if (reading.ColonnadeColumns is { } cc and >= 3 and <= 40) t.ColonnadeColumns = cc;
        if (reading.GlazingRatio is { } g and >= 0.05 and <= 0.95) t.GlazingRatio = (t.GlazingRatio + g) / 2;
        if (reading.Cantilevers is { } cn and >= 0 and <= 10) t.Cantilevers = cn;
        if (reading.InclinedPylons is { } py and >= 2 and <= 80 && t.Morphology == PhotoMorphology.ArchSpan) t.InclinedPylons = py;
        if (reading.Tiers is { } tiers and >= 3 and <= 10 && t.Morphology == PhotoMorphology.Cascading && t.Tiers.Count != tiers)
        {
            t.Tiers.Clear();
            for (int i = 0; i < tiers; i++)
            {
                // Ярусы слева направо: высший — второй слева (как на большинстве каскадов), дальше — строго разные высоты
                // (ранг 0 — второй, 1 — первый, 2… — остальные по порядку): число различных уступов = числу ярусов снимка.
                int rank = i == 1 ? 0 : i == 0 ? 1 : i;
                double frac = 1 - 0.55 * rank / Math.Max(1, tiers - 1);
                t.Tiers.Add(new PhotoTier((double)i / tiers, (double)(i + 1) / tiers, Math.Clamp(frac, 0.35, 1), Math.Max(2, (int)Math.Round(t.Floors * frac))));
            }
            t.ReconcileNotes.Add($"ярусов: Визуальное ядро {cv.Tiers.Count} → {tiers} (модель)");
        }
        t.ReconcileNotes.Add(string.Create(ru, $"итог: {PhotoArchitectureDescriptor.MorphologyCode(t.Morphology)}, этажей {t.Floors}, габарит {t.WidthM:0} × {t.DepthM:0} м"));
        return t;
    }
}

/// <summary>Замечание ядра к ответу модели: код, текст для реплики модели и вес (форма / конструкция / цвет).</summary>
public sealed record CritiqueItem(string Code, string Text, string Kind);

/// <summary>Проверка ответа модели расчётным и геометрическим ядром: замечания, признаки формы, оценка, статика.</summary>
public sealed class PhotoCritique
{
    public List<CritiqueItem> Items { get; } = [];
    public required ModelTraits Traits { get; init; }
    public double FidelityScore { get; init; }
    public int FloorsModel { get; init; }
    public int FloorsTarget { get; init; }
    public double MaxUtilization { get; init; }
    public int Hanging { get; init; }
    public int AuditPassed { get; init; }
    public int AuditTotal { get; init; }
    public double ProfileIoU { get; init; }
    public bool Parsed { get; init; } = true;
    public int Elements { get; init; }

    public bool NeedsRevision => !Parsed || Items.Any(i => i.Kind is "shape" or "structure");

    /// <summary>Нужен второй ход и в быстром режиме: ответ не разобран или чтение снимка скопировано из паспорта.</summary>
    public bool NeedsReadingRevision => !Parsed || Items.Any(i => i.Code is "FLOORS_VERIFY" or "SIZE_VERIFY" or "JSON" or "EMPTY");

    /// <summary>Итоговая оценка для выбора лучшего хода: форма (0…1) − штраф за этажность и перегрузку.</summary>
    public double Rank => !Parsed ? -1 : FidelityScore - Math.Min(0.4, Math.Abs(FloorsModel - FloorsTarget) / Math.Max(1.0, FloorsTarget)) - (MaxUtilization > 1 ? 0.1 : 0);

    /// <summary>Реплика ядра модели (ход 2): конкретные замечания с числами.</summary>
    public string ToPrompt(int turn)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Проверка твоего ответа расчётным и геометрическим ядром программы (ход {turn}). По твоему JSON построена 3D-модель и сверена со снимком:");
        int i = 0;
        foreach (var item in Items) sb.AppendLine($"{++i}. {item.Text}");
        sb.AppendLine("Исправь и верни ПОЛНЫЙ JSON заново (тот же формат: photoReading первым, затем все elements). Не упрощай форму до коробки, " +
                      "сохрани всё, что уже совпадает, используй repeatLevels для типовых этажей.");
        return sb.ToString();
    }

    public string Summary() => PhotoText.S(
        $"форма {FidelityScore:0.00}, этажей {FloorsModel}/{FloorsTarget}, K_исп {MaxUtilization:0.00}, висящих {Hanging}, аудит {AuditPassed}/{AuditTotal}, IoU силуэта {ProfileIoU:0.00}, замечаний {Items.Count}");
}

public static class PhotoAnswerCritic
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static PhotoCritique NotParsed(string reason, ModelTraits empty) => new PhotoCritique { Traits = empty, Parsed = false }.With(new CritiqueItem("JSON", reason, "shape"));

    /// <summary>
    /// Ответ модели (элементы после солвера, без сверки) против паспорта снимка после сверки: этажность, признаки формы морфологии
    /// (дуги и их чередование, стилобат, ярусы, двор, купол, колоннада, острый угол, мансарда, свод без опор, наклонные пилоны),
    /// цвета; конструкция — K_исп (до 3 самых нагруженных с рекомендацией), висящие элементы, критерии логического аудита КЭ.
    /// </summary>
    public static PhotoCritique Review(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups, PhotoArchitectureDescriptor target,
        StructuralLoadSettings loads, PhotoArchitectureDescriptor? cv = null, PhotoReading? reading = null)
    {
        var t = PhotoFidelityEnforcer.Traits(elements);
        var morph = target.Morphology;
        var items = new List<CritiqueItem>();
        // Модель повторила этажность и габариты паспорта при слабом ритме этажей — просьба пересчитать ряды окон самой.
        if (cv is not null && reading?.Floors is { } rf && rf == cv.Floors && cv.FloorConfidence < 0.25 && morph != PhotoMorphology.ArchSpan)
            items.Add(new("FLOORS_VERIFY", string.Create(Ru, $"в photoReading этажность {rf} совпала с паспортом Визуального ядра, а у паспорта ритм этажей слабый ({cv.FloorConfidence:0.00}): ") +
                                           "возможна ошибка вдвое (верх и низ окна дают два перепада на этаж) — пересчитай ряды окон на снимке сам и исправь floors, widthM, depthM и модель", "shape"));
        if (cv is not null && reading?.WidthM is { } rw && Math.Abs(rw - cv.WidthM) < 0.5 && cv.SegmentationQuality < 0.9)
            items.Add(new("SIZE_VERIFY", string.Create(Ru, $"ширина {rw:0} м в photoReading скопирована из паспорта — оцени габарит по снимку (шаг окон ≈ 1,5–3 м)"), "shape"));
        double score = PhotoFidelityEnforcer.Score(t, target, morph);
        if (elements.Count == 0)
            return new PhotoCritique { Traits = t, Parsed = false }.With(new CritiqueItem("EMPTY", "в ответе нет элементов здания", "shape"));

        if (morph != PhotoMorphology.ArchSpan && Math.Abs(t.Floors - target.Floors) > Math.Max(1, (int)Math.Round(target.Floors * 0.1)))
            items.Add(new("FLOORS", $"этажей на снимке {target.Floors} (ряды окон, поправка на перспективу), в ответе {t.Floors} — исправь число этажей (repeatLevels, level)", "shape"));
        switch (morph)
        {
            case PhotoMorphology.Curved:
                if (t.ArcElements == 0)
                    items.Add(new("ARC", string.Create(Ru, $"на снимке изогнутый фасад (стрела/хорда {target.ArcBulgeRatio:+0.00;−0.00}), а в ответе фасад плоский — задай стенам и витражам фасада \"arcBulge\" (м), плиты — \"polygon\" по дуге"), "shape"));
                if (target.FacadeArcs >= 2 && t.AlternatingArcs < Math.Min(3, target.FacadeArcs))
                    items.Add(new("WAVE", string.Create(Ru, $"фасад на снимке — {(target.FacadeArcs >= 3 ? "волна" : "S-образный")} из {target.FacadeArcs} чередующихся дуг, центр {(target.ArcBulgeRatio < 0 ? "вогнутый (arcBulge < 0)" : "выпуклый")}; " +
                                                          $"в ответе чередований {t.AlternatingArcs} — раздели фасад на {target.FacadeArcs} витража с arcBulge разного знака"), "shape"));
                if (target.PodiumFloors > 0 && !t.HasPodium)
                    items.Add(new("PODIUM", $"на снимке стилобат {target.PodiumFloors} эт. (другой материал {target.AccentHex}), шире башни — добавь его отдельными элементами нижних этажей", "shape"));
                break;
            case PhotoMorphology.Rotunda:
                if (!t.HasDome) items.Add(new("DOME", "на снимке купол — добавь элемент \"shell\" (или roofType \"dome\")", "shape"));
                if (target.HasCourtyard && !t.HasRingSlab)
                    items.Add(new("COURTYARD", "здание на снимке — замкнутое кольцо с внутренним двором-атриумом: перекрытия — круг + \"slab_opening\" двора, наружный и внутренний витражи по дугам (\"arcBulge\")", "shape"));
                if (t.ArcElements < 4 && !t.HasRoundedContour) items.Add(new("ROUND", "барабан/кольцо в плане круглый — стены и витражи по дугам (\"arcBulge\"), плиты — круглые \"polygon\"", "shape"));
                if (target.ColonnadeColumns > 0 && t.RoundColumns < Math.Max(4, target.ColonnadeColumns - 2))
                    items.Add(new("COLONNADE", $"на снимке колоннада из ≈ {target.ColonnadeColumns} круглых колонн у входа, в ответе круглых колонн {t.RoundColumns} — \"profile\": \"circle\"", "shape"));
                break;
            case PhotoMorphology.Cascading:
                int need = Math.Max(3, target.Tiers.Count);
                if (t.Tiers < need - 1) items.Add(new("TIERS", $"на снимке {need} ярусов-уступов с террасами, в ответе {t.Tiers} — задай секциям разную этажность и террасы \"roofType\": \"flat_parapet\"", "shape"));
                break;
            case PhotoMorphology.Triangular:
                if (t.MinPlanAngle >= 75 || t.NonOrthogonalWalls == 0)
                    items.Add(new("ANGLE", string.Create(Ru, $"план на снимке — треугольник с острым углом ≈ {(target.CornerAngleDeg > 0 ? target.CornerAngleDeg : 55):0}° на стрелке, в ответе мин. угол {t.MinPlanAngle:0}° — стены под углом (\"miterStart/End\"), плиты-треугольники"), "shape"));
                if (target.CornerRadiusRatio > 0.04 && !t.HasRoundedContour && t.ArcElements == 0)
                    items.Add(new("NOSE", "стрелка на снимке скруглена (эркер-нос) — дуга (\"arcBulge\") или \"cornerRadius\" у плит", "shape"));
                if (target.Roof == PhotoRoofKind.Mansard && !t.HasMansard)
                    items.Add(new("MANSARD", "на снимке мансарда (верхние этажи с крутым скатом) — верхние 1–2 этажа с отступом от фасада в цвете кровли, имя «мансарда»", "shape"));
                break;
            case PhotoMorphology.ArchSpan:
                if (!t.HasVault) items.Add(new("VAULT", "покрытие на снимке — свод: \"roofType\": \"barrel_vault\"", "shape"));
                if (t.InteriorColumnsInHall > 0) items.Add(new("HALL", $"зал на снимке без колонн, в ответе колонн внутри зала {t.InteriorColumnsInHall} — убери их, распор — пилонам", "structure"));
                if (t.InclinedPylons == 0) items.Add(new("PYLONS", "на снимке наклонные пилоны по бокам свода — колонны с наклоном (\"rotationZ\" 10–20°)", "shape"));
                break;
        }
        if (t.ColoredElements < t.Elements * 0.5)
            items.Add(new("COLOR", $"цвета снимка не заданы у {t.Elements - t.ColoredElements} элементов — \"colorHex\": стены {target.WallHex}, стекло {target.GlassHex}, кровля {target.RoofHex}", "color"));

        // Конструкция ответа как есть (до подбора сечений ядром).
        double kmax = 0;
        int hanging = 0, auditPassed = 0, auditTotal = 0;
        try
        {
            var analysis = StructuralAnalysisService.Analyze(elements.ToList(), groups, loads);
            kmax = analysis.MaxUtilization;
            foreach (var e in elements.Where(e => analysis.For(e.Id) is { Utilization: > 1.0 }).OrderByDescending(e => analysis.For(e.Id)!.Utilization).Take(3))
            {
                var r = analysis.For(e.Id)!;
                string governing = r.KPunching >= r.KStrength && r.KPunching > 1 ? "продавливание" : r.KSoil > 1 && r.KSoil >= r.KStrength ? "давление на грунт" :
                    r.KDeflection > 1 && r.KDeflection >= r.KStrength ? "прогиб" : r.KStability > r.KStrength ? "устойчивость" : "прочность";
                string advice = e.Category switch
                {
                    BimCategory.Column => string.Create(Ru, $"увеличь сечение до {Math.Ceiling((e.Width + 0.15) * 10) / 10 * 1000:0} × {Math.Ceiling((e.Depth + 0.15) * 10) / 10 * 1000:0} мм или добавь колонну рядом"),
                    BimCategory.Slab or BimCategory.Roof or BimCategory.CantileverSlab => string.Create(Ru, $"утолщи до {Math.Max(e.Height + 0.05, r.Span / 30):0.00} м или добавь опору (пролёт {r.Span:0.0} м)"),
                    BimCategory.Beam => "увеличь высоту сечения (h ≥ L/12)",
                    BimCategory.Foundation => "увеличь площадь или толщину фундаментной плиты",
                    BimCategory.Wall => "утолщи стену",
                    _ => "усиль элемент"
                };
                items.Add(new("K", string.Create(Ru, $"у «{e.DisplayTitle}» K_исп = {r.Utilization:0.00} ({governing}) — {advice}"), "structure"));
            }
            var audited = elements.Select(e => e.Clone(keepId: true)).ToList();
            StructuralAuditor.Audit(audited, new SolverReport());
            var hangingList = audited.Where(e => e.HasPhysicsError && e.Category is not (BimCategory.Window or BimCategory.Door)).ToList();
            hanging = hangingList.Count;
            if (hanging > 0)
                items.Add(new("HANG", $"без опоры {hanging} элем. (например, «{hangingList[0].DisplayTitle}» на отметке {hangingList[0].BaseY:0.0} м) — поставь их на перекрытия/колонны", "structure"));
            var audit = FeaLogicalAuditService.Audit(elements.ToList(), groups, loads, false);
            auditTotal = audit.Criteria.Count;
            auditPassed = audit.Criteria.Count(c => c.Passed);
            foreach (var c in audit.Criteria.Where(c => !c.Passed).Take(2))
                items.Add(new("AUDIT", $"аудит расчётной схемы: «{c.Title}» не выполнен ({AiHttp.Truncate(c.Detail, 160)})", "structure"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            items.Add(new("FEA", "расчётная схема по ответу не собрана: " + ex.Message, "structure"));
        }
        // Замечания по цвету — последними (их ядро накладывает само), сначала форма и конструкция.
        items = items.OrderBy(i => i.Kind == "color" ? 1 : 0).ToList();
        var critique = new PhotoCritique
        {
            Traits = t, FidelityScore = score, FloorsModel = t.Floors, FloorsTarget = target.Floors, MaxUtilization = kmax, Hanging = hanging,
            AuditPassed = auditPassed, AuditTotal = auditTotal, ProfileIoU = ProfileIoU(elements, target), Elements = elements.Count
        };
        critique.Items.AddRange(items);
        return critique;
    }

    /// <summary>
    /// IoU верхнего профиля фронтальной проекции модели (высота по X, вид с −Z) и профиля силуэта снимка (обе — в долях ширины и
    /// наибольшей высоты): Σ min / Σ max по 48 полосам. Справочно — на угловых и аэроснимках проекция не ортогональна.
    /// </summary>
    public static double ProfileIoU(IReadOnlyList<ArchitecturalElement> elements, PhotoArchitectureDescriptor d)
    {
        var solids = elements.Where(e => e.Category is not (BimCategory.Window or BimCategory.Door or BimCategory.WallOpening or BimCategory.SlabOpening or BimCategory.Asset)).ToList();
        if (solids.Count == 0 || d.TopProfile.Count < 8) return 0;
        var spans = solids.Select(e =>
        {
            var o = AIArchitectStudio.Rendering.ElementGeometryBuilder.WorldOutline(e).ToList();
            return o.Count == 0 ? (A: e.X - e.Width / 2, B: e.X + e.Width / 2, Top: e.TopY) : (A: o.Min(p => p.X), B: o.Max(p => p.X), Top: e.TopY);
        }).ToList();
        double x0 = spans.Min(s => s.A), x1 = spans.Max(s => s.B), hmax = spans.Max(s => s.Top);
        if (x1 - x0 < 1 || hmax < 1) return 0;
        double px0 = d.TopProfile.Min(p => p.X), px1 = d.TopProfile.Max(p => p.X);
        double baseY = d.BoxBase, ptop = d.TopProfile.Min(p => p.Y);
        const int bins = 48;
        double inter = 0, union = 0;
        for (int i = 0; i < bins; i++)
        {
            double f = (i + 0.5) / bins;
            // Правая система координат: левый край снимка — +X модели.
            double mx = x1 - f * (x1 - x0);
            double hm = spans.Where(s => s.A <= mx && s.B >= mx).Select(s => s.Top).DefaultIfEmpty(0).Max() / hmax;
            double px = px0 + f * (px1 - px0);
            var near = d.TopProfile.MinBy(p => Math.Abs(p.X - px));
            double hp = Math.Clamp((baseY - near.Y) / Math.Max(1e-6, baseY - ptop), 0, 1);
            inter += Math.Min(hm, hp);
            union += Math.Max(hm, hp);
        }
        return union > 0 ? inter / union : 0;
    }

    private static PhotoCritique With(this PhotoCritique c, CritiqueItem item)
    {
        c.Items.Add(item);
        return c;
    }
}

/// <summary>Событие живого диалога (для ленты): отправка, ответ модели, проверка ядром, итог.</summary>
public sealed record PhotoDialogueEvent(string Kind, int Turn, string Text, UnifiedChatResult? Call = null, PhotoCritique? Critique = null)
{
    /// <summary>Готовность задачи 0…100 % (−1 — без процента) — для строки состояния.</summary>
    public double Percent { get; init; } = -1;
}

/// <summary>Итог живого диалога «генерация → аудит ядром → доработка моделью» по одному снимку и одной модели.</summary>
public sealed class PhotoDialogueResult
{
    public required string ModelRequested { get; init; }
    public string ModelUsed { get; set; } = string.Empty;
    public bool ImagesDelivered { get; set; }
    public PhotoArchitectureDescriptor? Target { get; set; }
    public PhotoReading? Reading1 { get; set; }
    public PhotoReading? Reading2 { get; set; }
    public PhotoCritique? Critique1 { get; set; }
    public PhotoCritique? Critique2 { get; set; }
    public int ChosenTurn { get; set; }
    public PhotoLiftResult? Lift { get; set; }

    /// <summary>Несущий каркас итога собран ядром (каркас ответа модели не доводился до K_исп ≤ 1).</summary>
    public bool CoreFrame { get; set; }

    /// <summary>Чтение снимка экспресс-моделью (параллельно основной), если было.</summary>
    public PhotoReading? HelperReading { get; set; }
    public List<UnifiedChatResult> Calls { get; } = [];
    public List<PhotoDialogueEvent> Events { get; } = [];
    public long InputTokens => Calls.Sum(c => c.InputTokens);
    public long OutputTokens => Calls.Sum(c => c.OutputTokens);
    public double ModelSeconds => Calls.Sum(c => c.Elapsed.TotalSeconds);
    public double TotalMs { get; set; }
    public string? Error { get; set; }
    public string Turn1Text { get; set; } = string.Empty;
    public string Turn2Text { get; set; } = string.Empty;
}

/// <summary>
/// Волна 4.1 — честный двухходовой диалог с живой облачной моделью по снимку:
/// <list type="number">
/// <item>Ход 1: снимок (JPEG) + оптический паспорт Визуального ядра + требование JSON v2 (photoReading первым, затем elements с
/// arcBulge, cornerRadius, roofType, colorHex, колоннадами, пролётами).</item>
/// <item>Ядро: солвер без сверки (как ответила модель) → чтение снимка моделью сверяется с паспортом
/// (<see cref="PhotoReadingReconciler"/>) → критика формы и конструкции (<see cref="PhotoAnswerCritic"/>: этажность, дуги и
/// волна, ярусы, двор, мансарда, свод; K_исп самых нагруженных, висящие, аудит КЭ).</item>
/// <item>Ход 2 (если есть замечания): вторая реплика в том же диалоге с конкретными числами ядра; исправленный JSON проверяется так же.</item>
/// <item>Лучший ход → <see cref="PhotoFidelityEnforcer"/> (форма снимка гарантируется) → доводка каркаса до K_исп ≤ 1,0 и аудит.</item>
/// </list>
/// Сцена приложения не меняется — вставку делает вызывающий код.
/// </summary>
public static partial class UniversalVisionModelBridge
{
    /// <summary>Требования к ответу по снимку (JSON v2) и блок photoReading — дополнение VisionPromptV2 для живого диалога.</summary>
    public const string JsonV2Guide = """
ФОРМАТ ОТВЕТА ПО СНИМКУ (JSON v2), один объект в блоке ```json:
1) ПЕРВЫМ полем — "photoReading": что ТЫ САМ видишь на снимке (посчитай ряды окон по вертикали, ярусы, дуги фасада):
   {"morphology": "curved|rounded|cascading|triangular|rotunda|ring_atrium|arch_span|pitched|box",
    "floors": <этажей основного объёма над землёй по рядам окон>, "podiumFloors": <этажей стилобата или 0>,
    "tiers": <число ярусов-уступов кровли>, "cantilevers": <консольных сдвигов>,
    "facadeArcs": <дуг вдоль главного фасада: 1 — C, 2 — S, 3 — волна>, "arcBulgeRatio": <стрела/хорда центральной дуги: + выпукло к зрителю, − вогнуто>,
    "cornerAngleDeg": <острый угол плана на стрелке или 90>, "cornerRadiusM": <радиус скругления угла/эркера>,
    "roof": "flat|parapet|dome|barrel_vault|gable|mansard|cone", "courtyard": <true — кольцо с внутренним двором>,
    "colonnadeColumns": <колонн портика или 0>, "inclinedPylons": <наклонных пилонов/контрфорсов или 0>, "glazingRatio": <доля стекла 0…1>,
    "widthM": <ширина>, "depthM": <глубина>, "heightM": <высота>, "confidence": <0…1>}
   Числа photoReading — ТВОЙ собственный подсчёт по снимку, а не копия паспорта: посчитай ряды окон снизу вверх на самом высоком
   фасаде (мансардные этажи — тоже этажи); cornerAngleDeg — угол между двумя уличными фасадами в плане (у «утюга» на стрелке он острый,
   < 75°); widthM/depthM — по ритму окон (шаг ≈ 1,5–3 м) и этажности, а не из паспорта.
   Если паспорт Визуального ядра расходится с тем, что ты видишь, — верь снимку и запиши в photoReading своё.
2) Затем "message" и "elements" (координаты в метрах, Y-Up). Поля формы: стены/витражи — "arcBulge" (м; + выпукло наружу, − вогнуто),
   косые торцы "miterStart"/"miterEnd" (°), плиты/покрытия — "polygon" и "cornerRadius", торец свода — "archRise",
   "colorHex" у КАЖДОГО элемента (цвета снимка), кровля "roofType": "flat|flat_parapet|dome|barrel_vault|gable_hollow|mansard|cone",
   колонны — "profile": "circle" + "diameter" там, где колонны круглые; кольцо — "slab" по наружному кругу + "slab_opening" двора;
   ярусы — секции с разной этажностью; стилобат — отдельный объём нижних этажей; типовые этажи — "repeatLevels" + "levelHeight".
   Каркас: колонны с шагом ≤ 7,2 м, перекрытия h ≥ L/30, фундаментная плита; не больше 160 записей в "elements".
""";

    /// <summary>Модели по умолчанию живых испытаний волны 4.1 (не флагманы).</summary>
    public static readonly string[] WorkhorseModels = [UnifiedCloudAiClient.Sonnet55.Alias, UnifiedCloudAiClient.Luna56.Alias];

    public static async Task<PhotoDialogueResult> RunDialogueAsync(UnifiedCloudAiClient client, AIOrchestrator ai, string model, PhotoArchitectureDescriptor cv, byte[] jpeg,
        PhotoLiftOptions? options, StructuralLoadSettings loads, IProgress<PhotoDialogueEvent>? progress = null, int maxRevisions = 1, CancellationToken ct = default,
        bool fast = true, string? helper = null)
    {
        var sw = Stopwatch.StartNew();
        options ??= new PhotoLiftOptions();
        string resolved = client.Resolve(model);
        var spec = UnifiedCloudAiClient.SpecOf(resolved) ?? UnifiedCloudAiClient.SpecOf(model);
        var option = spec is not null ? UnifiedCloudAiClient.OptionFor(spec, resolved) : new AiModelOption("unified:" + resolved, AiProviderKind.UnifiedGateway, resolved, resolved, resolved, true);
        var result = new PhotoDialogueResult { ModelRequested = resolved };
        void Emit(PhotoDialogueEvent e)
        {
            lock (result.Events) result.Events.Add(e);
            progress?.Report(e);
        }

        var images = new List<AiImage> { new(jpeg, "image/jpeg", "Снимок здания: " + cv.SourceName) };
        var ctx = new AiRequestContext
        {
            Task = VisionPromptV2(cv, null, imageAttached: true) + "\n" + JsonV2Guide,
            Model = option,
            Mode = SolverMode.NewScene,
            Vision = VisionMode.RealPhotoTo3D,
            Images = images,
            Image = jpeg,
            ImageName = cv.SourceName,
            Kind = BlueprintKind.SketchOrPhoto,
            SheetsDescription = cv.ToPromptMatrix()
        };
        var (system, user1) = ai.BuildPrompts(ctx);
        var messages = new List<UnifiedMessage> { UnifiedMessage.User(user1, images) };
        string noImage = "Снимок не передан облачным сервисом (отказ шлюза по изображению): опирайся на матрицу компьютерного зрения (PhotoArchitectureDescriptor) — " +
                         "в photoReading повтори её числа, confidence ≤ 0.4.";
        Emit(new PhotoDialogueEvent("sent", 1, $"Ход 1 → {spec?.Title ?? resolved}: снимок {cv.PixelWidth} × {cv.PixelHeight} px + паспорт Визуального ядра + JSON v2") { Percent = 8 });
        // Прогресс: ход 1 — 8…45 %, проверка ядром — 52 %, ход 2 — 55…85 %, доводка каркаса — 90 %, готово — 100 %.
        string modelTitle = spec?.Title ?? resolved;
        IProgress<int> Stream(int turn) => new InlineProgress<int>(chars =>
        {
            double from = turn == 1 ? 8 : 55, span = turn == 1 ? 37 : 30;
            Emit(new PhotoDialogueEvent("stream", turn, PhotoText.S($"Ход {turn} · {modelTitle} пишет модель: {chars:N0} символов"))
                { Percent = from + span * Math.Min(1, chars / 9000.0) });
        });

        // Экспресс-чтение: быстрая модель (Gemini 3.1 Pro, ~10 с) читает тот же снимок параллельно с основной — пользователь сразу
        // видит, что распознано; её чтение — перекрёстная проверка и резерв, если основная модель не ответит.
        PhotoReading? helperReading = null;
        UnifiedChatResult? helperCall = null;
        Task? helperTask = null;
        if (helper is { Length: > 0 } && client.Resolve(helper) != resolved)
        {
            string helperTitle = UnifiedCloudAiClient.SpecOf(helper)?.Title ?? helper;
            helperTask = Task.Run(async () =>
            {
                try
                {
                    var hc = await client.ChatAsync(new UnifiedChatRequest
                    {
                        Model = helper, System = system, Messages = [UnifiedMessage.User(user1, images)], MaxTokens = 12000, Temperature = 0.15, AllowFallback = false,
                        Purpose = $"фото → BIM · экспресс-чтение · {cv.SourceName}", NoImageNotice = noImage
                    }, ct).ConfigureAwait(false);
                    helperCall = hc;
                    if (AiJsonExtractor.TryExtract(hc.Text, out var hd) && PhotoReading.Parse(hd.RawJson) is { } hr && hc.ImagesDelivered)
                    {
                        helperReading = hr;
                        Emit(new PhotoDialogueEvent("reading", 1, PhotoText.S($"Экспресс-чтение {helperTitle} за {hc.Elapsed.TotalSeconds:0} с: {hr.Describe()}")) { Percent = 25 });
                    }
                }
                catch (AiProviderException ex)
                {
                    Emit(new PhotoDialogueEvent("progress", 1, $"Экспресс-чтение {helperTitle} недоступно: {ex.Message}"));
                }
            }, ct);
        }

        SolvedResponse? best = null;
        PhotoCritique? bestCritique = null;
        PhotoArchitectureDescriptor target = cv;
        for (int turn = 1; turn <= 1 + maxRevisions; turn++)
        {
            UnifiedChatResult call;
            try
            {
                call = await client.ChatAsync(new UnifiedChatRequest
                {
                    Model = resolved, System = system, Messages = messages.ToList(), MaxTokens = 24000, Temperature = 0.15, Purpose = $"фото → BIM · ход {turn} · {cv.SourceName}",
                    NoImageNotice = noImage, StreamProgress = Stream(turn)
                }, ct).ConfigureAwait(false);
            }
            catch (AiProviderException ex)
            {
                // Основная модель не ответила — ход 1 берётся у экспресс-модели (если она ответила).
                if (turn == 1 && helperTask is not null)
                {
                    await helperTask.ConfigureAwait(false);
                    if (helperCall is { } hc)
                    {
                        Emit(new PhotoDialogueEvent("progress", 1, "Основная модель недоступна (" + ex.Message + ") — используется ответ экспресс-чтения"));
                        call = hc;
                        helperCall = null;
                        goto answered;
                    }
                }
                result.Error = ex.Message;
                Emit(new PhotoDialogueEvent("error", turn, "Облачный сервис: " + ex.Message));
                break;
            }
        answered:
            result.Calls.Add(call);
            result.ModelUsed = call.ModelUsed;
            result.ImagesDelivered |= call.ImagesDelivered;
            if (turn == 1) result.Turn1Text = call.Text; else result.Turn2Text = call.Text;
            Emit(new PhotoDialogueEvent("answer", turn, PhotoText.S(
                $"Ответ {UnifiedCloudAiClient.SpecOf(call.ModelUsed)?.Title ?? call.ModelUsed} (ход {turn}): {call.Elapsed.TotalSeconds:0.0} с, токенов {call.InputTokens:N0} → {call.OutputTokens:N0}" +
                (call.ImagesDropped ? " · шлюз не принял снимок — модель работала по матрице" : call.ImagesDelivered ? " · снимок принят" : "") +
                (call.FellBack ? $" · замена модели: {call.ModelRequested} → {call.ModelUsed}" : "")), call) { Percent = turn == 1 ? 45 : 85 });

            PhotoCritique critique;
            SolvedResponse? solved = null;
            PhotoReading? turnReading = null;
            try
            {
                solved = ai.SolveRaw(call.Text, ctx, new SceneStateService());
                var reading = PhotoReading.Parse(solved.Document.RawJson);
                turnReading = reading;
                if (turn == 1) result.Reading1 = reading; else result.Reading2 = reading;
                target = PhotoReadingReconciler.Merge(cv, reading ?? result.Reading1 ?? helperReading, call.ImagesDelivered || result.ImagesDelivered || helperReading is not null);
                if (helperReading?.Floors is { } hf && (reading ?? result.Reading1)?.Floors is { } pf && Math.Abs(hf - pf) > Math.Max(1, pf * 0.25))
                    target.ReconcileNotes.Add($"перекрёстная проверка: экспресс-чтение даёт {hf} эт., основная модель — {pf}; принято по основной модели");
                critique = PhotoAnswerCritic.Review(solved.Result.Elements, solved.Result.Groups, target, loads, cv, reading);
                if (reading is not null) Emit(new PhotoDialogueEvent("reading", turn, "Модель видит на снимке: " + reading.Describe()));
                foreach (var note in target.ReconcileNotes) Emit(new PhotoDialogueEvent("reconcile", turn, "Сверка с Визуальным ядром: " + note));
            }
            catch (AiProviderException ex)
            {
                critique = PhotoAnswerCritic.NotParsed("ответ не разобран ядром: " + ex.Message + " — верни один корректный JSON-объект", PhotoFidelityEnforcer.Traits([]));
            }
            if (turn == 1) result.Critique1 = critique; else result.Critique2 = critique;
            Emit(new PhotoDialogueEvent("review", turn, $"Проверка ядром (ход {turn}): {critique.Summary()}" +
                (critique.Items.Count > 0 ? "\n" + string.Join("\n", critique.Items.Select(i => "· " + i.Text)) : ""), Critique: critique) { Percent = turn == 1 ? 52 : 88 });
            if (solved is not null && (bestCritique is null || critique.Rank >= bestCritique.Rank))
            {
                best = solved;
                bestCritique = critique;
                result.ChosenTurn = turn;
            }
            // Быстрый режим: второй ход — только когда он меняет итог (модель скопировала паспорт, ответ не разобран, нет photoReading);
            // форму снимка и каркас ядро доводит само (сверка формы, подбор, резервный каркас ядра).
            // Этажность, совпавшая с паспортом, но независимо подтверждённая экспресс-чтением (±10 %), перепроверки не требует;
            // скопированная ширина одна второй ход не вызывает (габарит берётся, только если правдоподобен).
            bool confirmed = helperReading?.Floors is { } hfl && turnReading?.Floors is { } tfl && Math.Abs(hfl - tfl) <= Math.Max(1, tfl * 0.1);
            bool readingIssue = !critique.Parsed || critique.Items.Any(i => i.Code is "JSON" or "EMPTY" || i.Code == "FLOORS_VERIFY" && !confirmed);
            bool revise = fast ? readingIssue || turnReading is null : critique.NeedsRevision;
            if (fast && confirmed && critique.Items.Any(i => i.Code == "FLOORS_VERIFY"))
                Emit(new PhotoDialogueEvent("progress", turn, $"Этажность {turnReading!.Floors} подтверждена независимым экспресс-чтением ({helperReading!.Floors}) — перепроверка не нужна"));
            if (!revise || turn > maxRevisions)
            {
                if (fast && critique.NeedsRevision && turn <= maxRevisions)
                    Emit(new PhotoDialogueEvent("skip", turn, "Быстрый режим: чтение снимка моделью согласовано с ядром — замечания по форме и каркасу ядро исправит само, второй ход не нужен") { Percent = 88 });
                break;
            }
            // Ход 2: та же беседа + ответ модели + замечания ядра.
            messages.Add(UnifiedMessage.Assistant(AiHttp.Truncate(call.Text, 60000)));
            messages.Add(UnifiedMessage.User(critique.ToPrompt(turn + 1)));
            Emit(new PhotoDialogueEvent("sent", turn + 1, $"Ход {turn + 1} → {spec?.Title ?? resolved}: замечаний ядра {critique.Items.Count}\n" + critique.ToPrompt(turn + 1)) { Percent = 55 });
        }
        result.Target = target;
        if (helperTask is { IsCompleted: true } && helperCall is { } done) result.Calls.Add(done);
        result.HelperReading = helperReading;
        Emit(new PhotoDialogueEvent("progress", result.ChosenTurn, "Ядро расчётов: сверка формы со снимком и доводка каркаса до K_исп ≤ 1") { Percent = 90 });
        // Итог: лучший ход → сверка формы со снимком (при несовпадении — пересборка по паспорту с числами модели) → доводка каркаса.
        var liftOptions = options with { GroupName = string.IsNullOrWhiteSpace(options.GroupName) || options.GroupName == "По фото" ? Path.GetFileNameWithoutExtension(cv.SourceName) : options.GroupName };
        result.Lift = best is not null
            ? PhotoToBimService.FromModelAnswer(best.Result.Elements.ToList(), best.Result.Groups.ToList(), target, liftOptions, loads)
            : PhotoToBimService.Lift(target, liftOptions, loads);
        if (best is null) result.Lift.Log.Insert(0, "Ответ модели не получен или не разобран — здание поднято Визуальным ядром по паспорту снимка");
        else if (!result.Lift.IsStructurallySound)
        {
            // Каркас ответа модели не доводится подбором и шлюзом (например, покрытие пролётом 56 м без промежуточных опор): форма и
            // числа модели (этажность, стилобат, дуги, ярусы — уже в target) сохраняются, несущий каркас собирает ядро.
            var core = PhotoToBimService.Lift(target, liftOptions, loads);
            if (core.IsStructurallySound || core.MaxUtilization < result.Lift.MaxUtilization)
            {
                core.Log.Insert(0, string.Create(CultureInfo.GetCultureInfo("ru-RU"),
                    $"Каркас ответа модели не доведён до K_исп ≤ 1 (K_исп {result.Lift.MaxUtilization:0.00}) — несущий каркас собран ядром по форме снимка и числам модели"));
                result.Lift = core;
                result.CoreFrame = true;
            }
        }
        result.TotalMs = sw.Elapsed.TotalMilliseconds;
        var lift = result.Lift;
        Emit(new PhotoDialogueEvent("final", result.ChosenTurn, PhotoText.S(
            $"Итог (ответ хода {result.ChosenTurn}{(result.CoreFrame ? ", каркас собран ядром по форме и числам модели" : lift.Fidelity is { Rebuilt: true } ? ", форма пересобрана по паспорту" : ", ответ модели сохранён")}): {PhotoToBimService.Summary(lift)} · аудит КЭ {lift.Audit?.Criteria.Count(c => c.Passed)}/{lift.Audit?.Criteria.Count}")) { Percent = 100 });
        return result;
    }
}
