namespace AIArchitectStudio.Services;

/// <summary>
/// Строки волны 3: основная локальная модель программы (локальное ядро), Инженерный агент и его инструменты, цепочка рассуждений,
/// веб-импорт прайса по ссылке и окно проверки подтянутых расценок, каркасы продавливания в схеме армирования
/// (отдельный словарь, общий поиск — <see cref="LocalizationService.Get(string)"/>).
/// </summary>
public sealed partial class LocalizationService
{
    private static readonly Dictionary<string, (string Ru, string En)> Wave3Strings = new()
    {
        // ── Армирование ──
        ["W3RebarPunchingInfo"] = ("каркасы продавливания у колонн: {0}", "punching shear cages at columns: {0}"),

        // ── Локальное ядро: основная локальная модель ──
        ["W3CoreTitle"] = ("Локальное ядро", "Local core"),
        ["W3CoreModel"] = ("Основная локальная модель", "Primary local model"),
        ["W3CoreNone"] = ("Локальная модель не найдена", "No local model found"),
        ["W3CoreNoneHint"] = ("Установите модель в локальное ядро или положите файл GGUF в папку LocalModels — программа выберет лучшую сама",
            "Install a model into the local core or put a GGUF file into LocalModels — the program picks the best one itself"),
        ["W3CoreAuto"] = ("Автовыбор лучшей модели", "Pick the best model"),
        ["W3CoreAutoDone"] = ("Основная локальная модель: {0} · рейтинг {1}", "Primary local model: {0} · rating {1}"),
        ["W3CoreManual"] = ("Выбрана вручную", "Chosen manually"),
        ["W3CoreAutoState"] = ("Выбрана автоматически", "Chosen automatically"),
        ["W3CoreRating"] = ("Инженерный рейтинг", "Engineering rating"),
        ["W3CoreSpeed"] = ("Скорость", "Speed"),
        ["W3CoreSpeedUnknown"] = ("скорость замеряется при первом задании", "speed is measured on the first task"),
        ["W3CoreMeasure"] = ("Замерить скорость", "Measure speed"),
        ["W3CoreMeasuring"] = ("Замер скорости…", "Measuring speed…"),
        ["W3CoreSpeedValue"] = ("{0} ток/с · отклик {1} мс", "{0} tok/s · response {1} ms"),
        ["W3CoreRole"] = ("Внутренний мозг программы (Инженерный агент, цепочка рассуждений, аудит, разбор смет) — и участник проектирования и Совета КБ",
            "The program's internal brain (engineering agent, reasoning chain, audit, estimate parsing) — and a participant in design and the design council"),
        ["W3CoreCandidates"] = ("Установленные модели по рейтингу", "Installed models by rating"),
        ["W3CoreUnavailable"] = ("недоступна", "unavailable"),
        ["W3CoreChoose"] = ("Сделать основной", "Make primary"),
        ["W3CoreSettingsHint"] = ("Рейтинг учитывает семейство и специализацию (логика, код, математика), число параметров с учётом памяти ПК и скорость отклика",
            "The rating weighs family and specialization (logic, code, math), parameter count against the PC memory, and response speed"),

        // ── Инженерный агент ──
        ["W3AgentMode"] = ("Инженерный агент", "Engineering agent"),
        ["W3AgentModeShort"] = ("Агент", "Agent"),
        ["W3AgentModeTip"] = ("Инженерный агент: цепочка рассуждений, проверка в песочнице и инструменты программы — одним шагом журнала",
            "Engineering agent: reasoning chain, sandbox check and program tools — in one journal step"),
        ["W3AgentPlaceholder"] = ("Задача агенту: «Проведи аудит схемы, увеличь высоту 1-го этажа до 3600 мм и подбери колонны по ГОСТ»",
            "Agent task: \"Audit the model, raise the 1st storey to 3600 mm and size the columns by GOST\""),
        ["W3AgentTitle"] = ("Инженерный агент", "Engineering agent"),
        ["W3AgentEmpty"] = ("Напишите задачу в панели команд в режиме «Инженерный агент»: агент построит цепочку рассуждений, проверит решение расчётом и выполнит инструменты одним шагом журнала",
            "Type a task in the command panel in Engineering agent mode: the agent builds a reasoning chain, checks the solution by analysis and runs the tools in one journal step"),
        ["W3AgentBusy"] = ("Инженерный агент: рассуждения и проверка в песочнице…", "Engineering agent: reasoning and sandbox check…"),
        ["W3AgentTask"] = ("Задача", "Task"),
        ["W3AgentPlanner"] = ("План: {0}", "Plan: {0}"),
        ["W3AgentTools"] = ("Вызванные инструменты · {0}", "Tools called · {0}"),
        ["W3AgentReport"] = ("Инженерный отчёт", "Engineering report"),
        ["W3AgentUndo"] = ("Отменить действия агента (Ctrl+Z)", "Undo the agent's actions (Ctrl+Z)"),
        ["W3AgentUndone"] = ("Действия агента отменены одним шагом журнала", "The agent's actions were undone in one journal step"),
        ["W3AgentCopy"] = ("Копировать отчёт", "Copy report"),
        ["W3AgentCopied"] = ("Отчёт агента скопирован", "Agent report copied"),
        ["W3AgentDone"] = ("Инженерный агент · {0} · {1} мс", "Engineering agent · {0} · {1} ms"),
        ["W3AgentNoChange"] = ("Модель не изменена", "The model was not changed"),
        ["W3AgentStepName"] = ("Шаг журнала: {0}", "Journal step: {0}"),
        ["W3AgentHistory"] = ("Задания агента · {0}", "Agent tasks · {0}"),
        ["W3AgentStatusOk"] = ("выполнено", "done"),
        ["W3AgentStatusSkipped"] = ("пропущено", "skipped"),
        ["W3AgentStatusFailed"] = ("ошибка", "failed"),
        ["W3AgentArgs"] = ("Аргументы", "Arguments"),
        ["W3AgentElapsed"] = ("{0} мс", "{0} ms"),

        // ── Цепочка рассуждений ──
        ["W3ReasoningExpand"] = ("Подробности", "Details"),
        ["W3ReasoningHypotheses"] = ("Проверенные гипотезы", "Checked hypotheses"),
        ["W3ReasoningChosen"] = ("принята", "chosen"),
        ["W3ReasoningRejected"] = ("отклонена", "rejected"),
        ["W3ReasoningPassed"] = ("проходит", "passes"),
        ["W3ReasoningActions"] = ("Атомарные действия", "Atomic actions"),

        // ── Веб-импорт прайса и окно проверки ──
        ["W3PricePull"] = ("Подтянуть цены по ссылке / из сметы…", "Pull prices from a link / estimate…"),
        ["W3PricePullTip"] = ("Веб-импорт прайса по ссылке на магазин, металлобазу или завод и расценки прошлых смет — с проверкой перед применением",
            "Price import from a store, steel stockist or plant link and from past estimates — checked before applying"),
        ["W3PriceSourcesTitle"] = ("Веб-импорт прайса по ссылке", "Price import from a link"),
        ["W3PriceSourcesHint"] = ("Вставьте ссылки на страницы магазинов (по одной в строке), выберите прошлые проекты и сметы или вставьте кусок таблицы из Excel / Word. Ничего не применяется без вашего подтверждения.",
            "Paste store page links (one per line), pick past projects and estimates or paste a table fragment from Excel / Word. Nothing is applied without your confirmation."),
        ["W3PriceUrls"] = ("Ссылки на страницы магазинов", "Store page links"),
        ["W3PriceFiles"] = ("Прошлые проекты и сметы (.aia, .csv, .json, .txt)", "Past projects and estimates (.aia, .csv, .json, .txt)"),
        ["W3PricePickFiles"] = ("Выбрать файлы…", "Choose files…"),
        ["W3PriceNoFiles"] = ("Файлы не выбраны", "No files chosen"),
        ["W3PriceText"] = ("Текст страницы или таблица из Excel / Word", "Page text or a table from Excel / Word"),
        ["W3PriceIndex"] = ("Индексация цен прошлых смет, %", "Indexation of past estimates, %"),
        ["W3PriceExtract"] = ("Подтянуть расценки", "Pull prices"),
        ["W3PriceExtracting"] = ("Загрузка страниц и разбор расценок…", "Loading pages and parsing prices…"),
        ["W3PriceNothing"] = ("Укажите хотя бы одну ссылку, файл или вставьте текст", "Provide at least one link, file or pasted text"),
        ["W3PriceVerifyTitle"] = ("Проверка подтянутых расценок — всё ли верно?", "Check the pulled prices — is everything correct?"),
        ["W3PriceVerifySubtitle"] = ("Позиций {0} · источников {1} · отмечено {2}", "Items {0} · sources {1} · checked {2}"),
        ["W3PriceColMaterial"] = ("Материал в проекте", "Project material"),
        ["W3PriceColFound"] = ("Найдено в источнике", "Found in source"),
        ["W3PriceColUnit"] = ("Ед.", "Unit"),
        ["W3PriceColOld"] = ("Старая, ₽", "Old, ₽"),
        ["W3PriceColNew"] = ("Новая, ₽", "New, ₽"),
        ["W3PriceColDelta"] = ("Δ, %", "Δ, %"),
        ["W3PriceColSource"] = ("Магазин / источник", "Store / source"),
        ["W3PriceColConfidence"] = ("Достоверность", "Confidence"),
        ["W3PriceReference"] = ("справочно, вне сметы каркаса", "for reference, outside the frame estimate"),
        ["W3PriceSummary"] = ("Стоимость каркаса здания: было {0} ₽ → станет {1} ₽ (разница {2} ₽)", "Building frame cost: was {0} ₽ → will be {1} ₽ (difference {2} ₽)"),
        ["W3PriceQuestion"] = ("Проверьте распознанные позиции и цены поставщика. Всё ли верно для применения в расчёт сметы?",
            "Check the recognized items and supplier prices. Is everything correct for the estimate?"),
        ["W3PriceApply"] = ("Да, всё верно — применить в смету", "Yes, all correct — apply to the estimate"),
        ["W3PriceCancel"] = ("Отмена", "Cancel"),
        ["W3PriceApplied"] = ("Расценки применены в смету: позиций {0}, база «{1}», стоимость каркаса {2} → {3} ₽", "Prices applied: {0} items, base \"{1}\", frame cost {2} → {3} ₽"),
        ["W3PriceCancelled"] = ("Подтянутые расценки не применены — смета проекта не изменилась", "The pulled prices were not applied — the estimate is unchanged"),
        ["W3PriceError"] = ("Источник «{0}»: {1}", "Source \"{0}\": {1}"),
        ["W3PriceConversion"] = ("Пересчёт", "Conversion")
    };
}
