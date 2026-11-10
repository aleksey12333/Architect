namespace AIArchitectStudio.Services;

/// <summary>
/// Строки волны 4.1: облачный маршрут подъёма по фото (живой диалог «ответ модели → проверка ядром → доработка моделью»), лента
/// диалога, сводки прогона (отдельный словарь, общий поиск — <see cref="LocalizationService.Get(string)"/>).
/// </summary>
public sealed partial class LocalizationService
{
    private static readonly Dictionary<string, (string Ru, string En)> Wave41Strings = new()
    {
        ["W41RouteCloud"] = ("Облачный сервис — быстро и точно (GPT 5.6 Luna + Gemini 3.1 Pro, ~1 мин)", "Cloud service — fast and accurate (GPT 5.6 Luna + Gemini 3.1 Pro, ~1 min)"),
        ["W41RouteCloudLuna"] = ("Облачный сервис — самый быстрый (Gemini 3.1 Pro, ~20 с)", "Cloud service — fastest (Gemini 3.1 Pro, ~20 s)"),
        ["W41NoCloud"] = ("Облачный сервис не подключён — здание поднято Визуальным ядром", "The cloud service is not connected — the building was lifted by the Visual core"),
        ["W41Engine"] = ("Ядро расчётов", "Analysis core"),
        ["W41Cloud"] = ("Облачный сервис", "Cloud service"),
        ["W41DialogStart"] = ("Живой диалог по снимку «{0}» с моделью {1}: ход 1 — снимок и оптический паспорт, затем проверка ядром и доработка",
            "Live dialogue on «{0}» with {1}: turn 1 — photo and optical passport, then core check and revision"),
        ["W41Turn"] = ("Ход {0}", "Turn {0}"),
        ["W41Review"] = ("Проверка ядром · ход {0}", "Core check · turn {0}"),
        ["W41Final"] = ("Итог диалога: {0}", "Dialogue result: {0}"),
        ["W41Tokens"] = ("Модель {0} · токенов {1} → {2} · {3} с", "Model {0} · tokens {1} → {2} · {3} s"),
        ["W41Showcase"] = ("Живой прогон: Sonnet 5.5 и GPT 5.6 Luna на сложных фото", "Live run: Sonnet 5.5 and GPT 5.6 Luna on hard photos"),
        ["W41ShowcaseHint"] = ("Исходные снимки с деревьями, перспективой и непрямоугольной геометрией → 3D-модели, поднятые через облачный сервис в двухходовом диалоге с проверкой ядром расчётов; этажность, K_исп, ΣF = ΣR_y и затраченные токены",
            "Source photos with trees, perspective and non-rectangular geometry → 3D models lifted through the cloud service in a two-turn dialogue with analysis-core checks; storeys, utilisation, ΣF = ΣR_y and tokens spent"),
        ["W41Original"] = ("Снимок", "Photo"),
        ["W41Clutter"] = ("Отфильтрованные помехи: растительность и улица (зелёным), линия тротуара (оранжевым)", "Filtered clutter: vegetation and street (green), sidewalk line (orange)"),
        ["W41Close"] = ("Закрыть", "Close"),
        ["W41Sending"] = ("снимок и паспорт отправлены", "photo and passport sent"),
        ["W41Checking"] = ("проверка ответа ядром", "checking the answer in the core"),
        ["W41Done"] = ("Готово", "Done")    };
}
