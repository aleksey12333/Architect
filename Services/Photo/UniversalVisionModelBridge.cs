using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Services.AI;

namespace AIArchitectStudio.Services.Photo;

/// <summary>Чем выполнять подъём по фото (выбор в шапке студии).</summary>
public enum PhotoLiftRoute
{
    /// <summary>Визуальное ядро + основная локальная модель (без модели — только Визуальное ядро).</summary>
    Auto,
    /// <summary>Основная локальная модель программы (Локальное ядро).</summary>
    PrimaryLocal,
    /// <summary>Совет КБ (консилиум ролей).</summary>
    Council,
    /// <summary>Внешний веб-проектировщик (вход в аккаунт в панели WebView2).</summary>
    Web
}

/// <summary>Подготовленная отправка снимка модели: модель, изображения (пусто — модель без зрения), промпт, канал.</summary>
public sealed record VisionDispatch(AiModelOption Model, IReadOnlyList<AiImage> Images, string Prompt, bool ImageAttached, string Channel);

/// <summary>
/// Единый мультимодальный конвейер для ЛЮБОЙ модели программы. Мультимодальные модели (облако с зрением, веб-чаты, локальные
/// LLaVA / Qwen-VL / Gemma 3 / Llama 3.2 Vision) получают снимок (нормализованный JPEG ≤ 1600 px) и структурированный промпт
/// VisionPromptV2 с паспортом Визуального ядра; текстовые и кодовые модели (qwen2.5-coder, deepseek-r1, gpt-oss) — подробную
/// матрицу компьютерного зрения (силуэт, ярусы, кривизна, углы, ритм, покрытие, палитра HEX) вместо картинки. Ответ любой модели
/// проходит <see cref="PhotoFidelityEnforcer"/> и проверку каркаса (хук <see cref="AIOrchestrator.Solve(string, AiRequestContext, SceneStateService)"/>).
/// </summary>
public static class UniversalVisionModelBridge
{
    public static bool AcceptsImages(AiModelOption model) => model.IsWeb || model.SupportsVision;

    /// <summary>Канал доставки для подписи в чате.</summary>
    public static string ChannelOf(AiModelOption model) => model.IsWeb ? "веб-чат: файл в поле загрузки + промпт" :
        model.IsOnDevice ? model.SupportsVision ? "локальная мультимодальная модель: снимок + паспорт" : "локальная текстовая модель: матрица компьютерного зрения" :
        model.SupportsVision ? "облачная модель: снимок + паспорт" : "облачная текстовая модель: матрица компьютерного зрения";

    public static VisionDispatch Prepare(AiModelOption model, PhotoArchitectureDescriptor d, byte[] jpeg, string? task)
    {
        bool images = AcceptsImages(model);
        var list = images ? new List<AiImage> { new(jpeg, "image/jpeg", "Снимок здания: " + d.SourceName) } : [];
        return new VisionDispatch(model, list, VisionPromptV2(d, task, images), images, ChannelOf(model));
    }

    /// <summary>Контекст запроса с паспортом снимка: режим «Фото здания → 3D», матрица — в описании листов, сверка — в солвере.</summary>
    public static AiRequestContext CreateContext(AIOrchestrator ai, VisionDispatch dispatch, PhotoArchitectureDescriptor d, PhotoLiftOptions? options,
        StructuralLoadSettings loads, bool appendToSite) =>
        ai.CreateContext(dispatch.Prompt, dispatch.Model, appendToSite, VisionMode.RealPhotoTo3D, dispatch.Images, d.SourceName, BlueprintKind.SketchOrPhoto,
            d.ToPromptMatrix(), dispatch.Images.Count > 0 ? dispatch.Images[0].Bytes : null).WithPhoto(d, options, loads);

    /// <summary>
    /// VisionPromptV2: задача, правила точного совпадения формы и цвета со снимком для морфологии паспорта, требования к несущему
    /// каркасу и поля JSON криволинейной геометрии (arcBulge, cornerRadius, archRise, miterStart/End, colorHex, roofType dome/cone/
    /// barrel_vault, profile circle).
    /// </summary>
    public static string VisionPromptV2(PhotoArchitectureDescriptor d, string? task, bool imageAttached)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VisionPromptV2 · Реконструкция по фото и плану.");
        sb.AppendLine(string.IsNullOrWhiteSpace(task) ? $"Задача: построить 3D BIM-модель здания со снимка «{d.SourceName}» — визуально идентичную снимку и конструктивно правильную." : "Задача: " + task.Trim());
        sb.AppendLine(imageAttached
            ? "Снимок приложен. Числа паспорта Визуального ядра (ниже) точнее оценки на глаз — используй их."
            : "Снимок этой модели недоступен: строй модель строго по матрице компьютерного зрения (ниже), не упрощай форму.");
        sb.AppendLine($"Морфология: {PhotoArchitectureDescriptor.MorphologyCode(d.Morphology)} — {PhotoToBimService.MorphologyTitle(d.Morphology)}. Обязательно:");
        foreach (var rule in Rules(d)) sb.AppendLine("- " + rule);
        sb.AppendLine("- Цвета: у каждого элемента поле \"colorHex\" из палитры (стены — " + d.WallHex + ", стекло и окна — " + d.GlassHex + ", кровля/купол — " + d.RoofHex +
                      ", перекрытия и колонны — " + d.AccentHex + ").");
        sb.AppendLine($"- Этажей {d.Floors}, высота этажа {d.FloorHeight:0.0} м; габарит ≈ {d.WidthM:0} × {d.DepthM:0} м.");
        sb.AppendLine("- Несущий каркас внутри оболочки: фундаментная плита по контуру, сетка колонн с шагом ≤ 7,2 м (у дуг — радиально), перекрытия толщиной ≥ L/30, " +
                      "ядро жёсткости при ≥ 9 этажах, окна и двери — в простенках, не на колоннах.");
        sb.AppendLine("- Не возвращай упрощённую прямоугольную коробку: ответ сверяется с паспортом снимка, несовпадение формы будет пересобрано Визуальным ядром.");
        return sb.ToString();
    }

    private static IEnumerable<string> Rules(PhotoArchitectureDescriptor d)
    {
        switch (d.Morphology)
        {
            case PhotoMorphology.Curved:
                yield return $"изогнутый фасад: стены/витражи с \"arcBulge\" (стрела дуги ≈ {Math.Max(0.12, d.ArcBulgeRatio) * d.WidthM:0.0} м на хорде {d.WidthM:0} м), плиты — \"polygon2D\" по дуге";
                yield return $"скругление углов перекрытий \"cornerRadius\" ≈ {Math.Max(2.5, d.CornerRadiusRatio * d.DepthM):0.0} м" + (d.PodiumFloors > 0 ? $"; стилобат {d.PodiumFloors} эт. шире башни" : string.Empty);
                break;
            case PhotoMorphology.Rounded:
                yield return $"скруглённые углы плит и кровли \"cornerRadius\" ≈ {Math.Max(2, d.CornerRadiusRatio * Math.Min(d.WidthM, d.DepthM)):0.0} м";
                break;
            case PhotoMorphology.Cascading:
                yield return $"каскад: {Math.Max(3, d.Tiers.Count)} яруса с уступами (этажность секций убывает), террасы — \"roofType\": \"flat_parapet\"";
                break;
            case PhotoMorphology.Triangular:
                yield return $"треугольный план: острый угол на стрелке {(d.CornerAngleDeg > 0 ? d.CornerAngleDeg : 50):0} °, плиты \"polygon2D\" треугольником со скруглённой стрелкой, стены под углом (\"miterStart\"/\"miterEnd\")";
                break;
            case PhotoMorphology.Rotunda:
                yield return "ротонда: круглый барабан (дуговые стены \"arcBulge\", круглые плиты), купол — элемент \"shell\" (или \"roofType\": \"dome\")";
                yield return $"портик: колоннада {Math.Max(4, d.ColonnadeColumns)} колонн \"profile\": \"circle\", фронтон; боковые крылья под углом";
                break;
            case PhotoMorphology.ArchSpan:
                yield return "большепролётный зал: покрытие \"roofType\": \"barrel_vault\" без промежуточных колонн внутри; торцы — витражи с \"archRise\"; наклонные пилоны по бокам";
                break;
            case PhotoMorphology.Pitched:
                yield return "скатная кровля \"roofType\": \"gable_hollow\" по снимку";
                break;
            default:
                yield return "прямоугольный корпус по снимку";
                break;
        }
    }
}
