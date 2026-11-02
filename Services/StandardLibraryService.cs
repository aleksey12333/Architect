using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArchitectStudio.Models;

namespace AIArchitectStudio.Services;

/// <summary>Итог применения типа из библиотеки: изменённые элементы и пропущенные (другая категория).</summary>
public sealed record StandardApplyResult(StandardLibraryEntry Entry, IReadOnlyList<ArchitecturalElement> Applied, IReadOnlyList<ArchitecturalElement> Skipped, string StepName);

/// <summary>
/// Библиотеки типовых элементов и конструктивных узлов по ГОСТ/СП: встроенный каталог (колонны ж/б по ГОСТ 25628 / Серии 1.020,
/// ригели по ГОСТ 18980 / СП 63, стальные двутавры по ГОСТ Р 57837-2017, монолитные плиты и фундаментные плиты по СП 63,
/// стеновые пироги по СП 50.13330 и ненесущие перегородки) и пользовательские библиотеки проектного отдела (<c>.aialib.json</c>).
/// <para>
/// Применение типа к выделенным элементам и вставка нового элемента — ровно один шаг журнала транзакций с именем
/// «Тип из библиотеки: К 400×400 (ГОСТ 25628)». Встроенный каталог неизменяем; пользовательские библиотеки хранятся отдельными
/// файлами в папке <see cref="LibraryDirectory"/> (settings.json не меняется).
/// </para>
/// </summary>
public sealed class StandardLibraryService
{
    public const string BuiltInName = "ГОСТ / СП (встроенная)";
    public const string UserLibraryName = "Библиотека проекта";
    public const string UserFileName = "user" + StandardLibrary.FileExtension;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly Lazy<StandardLibrary> BuiltInCatalog = new(CreateBuiltIn);

    private readonly List<StandardLibrary> _imported = [];

    /// <summary>Папка пользовательских библиотек (по умолчанию — %LOCALAPPDATA%\AIArchitectStudio\Libraries).</summary>
    public string LibraryDirectory { get; }

    /// <summary>Не записывать пользовательскую библиотеку на диск (автотест, съёмка).</summary>
    public bool IsSessionOnly { get; init; }

    public StandardLibraryService(string? directory = null)
    {
        LibraryDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIArchitectStudio", "Libraries");
        UserLibrary = LoadUserLibrary();
    }

    /// <summary>Встроенный каталог ГОСТ/СП (общий для всех экземпляров, только чтение).</summary>
    public static StandardLibrary BuiltIn => BuiltInCatalog.Value;

    /// <summary>Пользовательская библиотека проектного отдела («Сохранить тип в библиотеку»).</summary>
    public StandardLibrary UserLibrary { get; private set; }

    /// <summary>Библиотеки, импортированные из файлов в этой сессии.</summary>
    public IReadOnlyList<StandardLibrary> ImportedLibraries => _imported;

    /// <summary>Все библиотеки: встроенная, пользовательская, импортированные.</summary>
    public IEnumerable<StandardLibrary> Libraries => new[] { BuiltIn, UserLibrary }.Concat(_imported);

    public IEnumerable<StandardLibraryEntry> AllEntries => Libraries.SelectMany(l => l.Entries);

    public StandardLibraryEntry? Find(string id) => AllEntries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Тип по умолчанию для вставки нового элемента категории (колонна — К 400×400, стена — монолитная 200 мм…).</summary>
    public static StandardLibraryEntry DefaultFor(string elementType) => elementType switch
    {
        ElementTypes.Column => BuiltIn.Find("col-400x400")!,
        ElementTypes.Beam => BuiltIn.Find("beam-300x600")!,
        ElementTypes.Wall => BuiltIn.Find("wall-mono-200")!,
        ElementTypes.Slab => BuiltIn.Find("slab-200")!,
        ElementTypes.Foundation => BuiltIn.Find("fnd-600")!,
        _ => BuiltIn.Entries[0]
    };

    // ───────────────────────────── Встроенный каталог ─────────────────────────────

    private static StandardLibrary CreateBuiltIn()
    {
        var lib = new StandardLibrary
        {
            Name = BuiltInName, Author = "Architect Studio Pro", CreatedAt = new DateTime(2026, 11, 2), IsBuiltIn = true,
            Description = "Типовые сечения и конструкции по действующим ГОСТ и сводам правил"
        };
        var e = lib.Entries;

        // Колонны ж/б (ГОСТ 25628 / Серия 1.020): сечения B × H, бетон B25–B40.
        foreach (var (b, h) in new[] { (300, 300), (400, 400), (400, 600), (500, 500), (600, 600) })
            e.Add(new StandardLibraryEntry
            {
                Id = $"col-{b}x{h}", Mark = $"К {b}×{h}", Name = $"Колонна ж/б {b}×{h} мм", Standard = "ГОСТ 25628 / Серия 1.020",
                Section = StandardLibrarySection.Columns, ElementType = ElementTypes.Column, Material = "concrete", MaterialGrade = "B25–B40",
                WidthMm = b, DepthMm = h, Notes = "Монолитная или сборная колонна каркаса; армирование по K_исп (СП 63.13330, п. 8.1.14)"
            });

        // Ригели и балки ж/б (ГОСТ 18980 / СП 63): b × h.
        foreach (var (b, h) in new[] { (300, 500), (300, 600), (400, 600) })
            e.Add(new StandardLibraryEntry
            {
                Id = $"beam-{b}x{h}", Mark = $"Р {b}×{h}", Name = $"Ригель ж/б {b}×{h} мм", Standard = "ГОСТ 18980 / СП 63.13330",
                Section = StandardLibrarySection.Beams, ElementType = ElementTypes.Beam, Material = "concrete", MaterialGrade = "B25–B40",
                WidthMm = b, DepthMm = h, DefaultLengthMm = 6000, Notes = "Ригель каркаса под перекрытием; h ≥ L/15 (СП 63.13330, п. 8.2)"
            });

        // Стальные двутавры (ГОСТ Р 57837-2017): h, b, tw, tf — нормальные (Б) и широкополочные (Ш).
        foreach (var (mark, h, b, tw, tf) in new[] { ("20Б1", 200.0, 100.0, 5.6, 8.5), ("30Б1", 296.0, 140.0, 5.8, 8.5), ("35Ш1", 338.0, 250.0, 9.5, 12.5), ("40Ш1", 388.0, 300.0, 9.5, 14.0) })
            e.Add(new StandardLibraryEntry
            {
                Id = "beam-i-" + mark.ToLowerInvariant().Replace("б", "b").Replace("ш", "sh"), Mark = mark, Name = $"Двутавр {mark}",
                Standard = "ГОСТ Р 57837-2017 / СП 16.13330", Section = StandardLibrarySection.Beams, ElementType = ElementTypes.Beam, Material = "steel",
                MaterialGrade = "С245/С345", ProfileType = ProfileTypes.IBeam, WidthMm = b, DepthMm = h, FlangeMm = tf, WebMm = tw, DefaultLengthMm = 6000,
                Notes = "Горячекатаный двутавр с параллельными гранями полок; λ ≤ 120 (СП 16.13330)"
            });

        // Монолитные плиты перекрытий (СП 63 / ГОСТ 9561).
        foreach (int t in new[] { 180, 200, 220, 250 })
            e.Add(new StandardLibraryEntry
            {
                Id = $"slab-{t}", Mark = $"П {t}", Name = $"Плита перекрытия монолитная {t} мм", Standard = "СП 63.13330 / ГОСТ 9561",
                Section = StandardLibrarySection.Slabs, ElementType = ElementTypes.Slab, Material = "concrete", MaterialGrade = "B25–B30",
                WidthMm = t, Notes = "Безбалочное или балочное перекрытие; h ≥ L/30, продавливание — п. 8.1.46"
            });

        // Фундаментные плиты (СП 63 / СП 22).
        foreach (int t in new[] { 400, 600, 800 })
            e.Add(new StandardLibraryEntry
            {
                Id = $"fnd-{t}", Mark = $"Фп {t}", Name = $"Фундаментная плита {t} мм", Standard = "СП 63.13330 / СП 22.13330",
                Section = StandardLibrarySection.Foundations, ElementType = ElementTypes.Foundation, Material = "concrete", MaterialGrade = "B25–B30",
                WidthMm = t, Notes = "Плитный фундамент на естественном основании; давление под подошвой — СП 22.13330"
            });

        // Стеновые пироги (СП 50.13330 «Тепловая защита зданий»): слои от лицевой грани +Z к внутренней.
        e.Add(new StandardLibraryEntry
        {
            Id = "wall-ext-d400", Mark = "НС-1", Name = "Наружная стена: газобетон D400 + минвата + облицовка", Standard = "СП 50.13330 / СП 15.13330",
            Section = StandardLibrarySection.Walls, ElementType = ElementTypes.Wall, Material = "aerated_block", MaterialGrade = "D400 B2.5",
            Layers =
            [
                new("Облицовочный кирпич", 120, 1800, MaterialLayerRole.Finish),
                new("Утеплитель (минвата)", 100, 110, MaterialLayerRole.Insulation),
                new("Газобетон D400", 300, 400, MaterialLayerRole.Structural),
                new("Штукатурка внутренняя", 20, 1600, MaterialLayerRole.Finish)
            ],
            Notes = "R0 ≈ 4,1 м²·°С/Вт ≥ R0тр для Москвы (ГСОП 4 551) по СП 50.13330"
        });
        foreach (int t in new[] { 200, 250 })
            e.Add(new StandardLibraryEntry
            {
                Id = $"wall-mono-{t}", Mark = $"Мс {t}", Name = $"Монолитная несущая стена {t} мм", Standard = "СП 63.13330 / СП 430.1325800",
                Section = StandardLibrarySection.Walls, ElementType = ElementTypes.Wall, Material = "concrete", MaterialGrade = "B25–B30",
                Layers = [new("Монолитный ж/б B25", t, 2500, MaterialLayerRole.Structural)],
                Notes = "Диафрагма или несущая стена; работает в расчёте на вертикальные и горизонтальные нагрузки"
            });

        // Ненесущие перегородки (СП 163.1325800 / СП 15.13330): нагрузка — только собственный вес.
        e.Add(new StandardLibraryEntry
        {
            Id = "part-gkl-100", Mark = "Пг ГКЛ 100", Name = "Перегородка ГКЛ на металлокаркасе 100 мм", Standard = "СП 163.1325800 / ГОСТ 32614",
            Section = StandardLibrarySection.Partitions, ElementType = ElementTypes.Wall, Material = "gypsum_board", MaterialGrade = "ГКЛ / ПС 75",
            IsLoadBearing = false,
            Layers =
            [
                new("Гипсокартон ГКЛ", 12.5, 850, MaterialLayerRole.Finish),
                new("Звукоизоляция (минвата) в каркасе ПС 75", 75, 45, MaterialLayerRole.Insulation),
                new("Гипсокартон ГКЛ", 12.5, 850, MaterialLayerRole.Finish)
            ],
            Notes = "Ненесущая: не опирает перекрытие, передаёт только собственный вес"
        });
        e.Add(new StandardLibraryEntry
        {
            Id = "part-brick-120", Mark = "Пг К 120", Name = "Перегородка кирпичная 120 мм", Standard = "СП 15.13330 / ГОСТ 530",
            Section = StandardLibrarySection.Partitions, ElementType = ElementTypes.Wall, Material = "brick", MaterialGrade = "М100",
            IsLoadBearing = false,
            Layers = [new("Кирпич керамический М100", 120, 1800, MaterialLayerRole.Structural)],
            Notes = "Ненесущая: деформационный зазор под перекрытием, только собственный вес"
        });
        foreach (var x in e) x.IsBuiltIn = true;
        return lib;
    }

    // ───────────────────────────── Применение ─────────────────────────────

    /// <summary>
    /// Значения типа записи — элементу (через фасад: экземпляр переходит на найденный или новый тип реестра). Колонна —
    /// сечение W × D (высота и низ сохраняются), балка — b × h (длина и верх сохраняются), плита и фундамент — толщина (низ
    /// сохраняется), стена — пирог (ось сохраняется), признак «Несущая конструкция» и марка типоразмера.
    /// Привязанные проёмы стены получают новую глубину выреза. Возвращает затронутые элементы (элемент и его проёмы).
    /// </summary>
    public static List<ArchitecturalElement> ApplyTo(ArchitecturalElement e, StandardLibraryEntry entry, IEnumerable<ArchitecturalElement>? hosted = null)
    {
        var changed = new List<ArchitecturalElement> { e };
        if (!entry.IsCompatible(e)) return [];
        e.Material = entry.Material;
        switch (e.Category)
        {
            case BimCategory.Column:
            {
                double baseY = e.BaseY;
                e.Polygon2D = [];
                e.ProfileType = entry.ProfileType;
                e.SetSize(entry.WidthMm / 1000, e.Height, (entry.DepthMm > 0 ? entry.DepthMm : entry.WidthMm) / 1000);
                e.PlaceOnBase(baseY);
                SetProfileThickness(e, entry);
                break;
            }
            case BimCategory.Beam:
            {
                double top = e.TopY, h = entry.DepthMm / 1000;
                e.ProfileType = entry.ProfileType;
                e.SetSize(e.Width, h, entry.WidthMm / 1000);
                e.SetPosition(e.X, top - h / 2, e.Z);
                SetProfileThickness(e, entry);
                break;
            }
            case BimCategory.Wall:
            {
                var layers = entry.Layers.Select(l => l.ToLayer()).ToList();
                double t = entry.ThicknessMm / 1000;
                e.SetSize(e.Width, e.Height, t);
                e.Layers = layers;
                foreach (var o in hosted ?? [])
                    if (ElementTypes.IsHostedType(o.Category))
                    {
                        o.SetSize(o.Width, o.Height, t + ParametricSolverService.OpeningClearance);
                        changed.Add(o);
                    }
                break;
            }
            default:
            {
                double baseY = e.BaseY, t = entry.ThicknessMm / 1000;
                if (entry.Layers.Count > 0) e.Layers = entry.Layers.Select(l => l.ToLayer()).ToList();
                else if (e.Layers.Count > 0)
                {
                    // Пирог пола сохраняется: толщину типоразмера принимает несущий слой, полная толщина — сумма слоёв.
                    var layers = e.Layers.Select(l => l.Clone()).ToList();
                    var main = layers.Where(l => l.Role == MaterialLayerRole.Structural).OrderByDescending(l => l.ThicknessMm).FirstOrDefault() ?? layers[0];
                    main.ThicknessMm = entry.ThicknessMm;
                    e.Layers = layers;
                    t = MaterialLayers.TotalMm(layers) / 1000;
                }
                e.SetSize(e.Width, t, e.Depth);
                e.PlaceOnBase(baseY);
                foreach (var o in hosted ?? [])
                    if (o.Category == BimCategory.SlabOpening)
                    {
                        o.SetSize(o.Width, t + ParametricSolverService.OpeningClearance, o.Depth);
                        o.SetPosition(o.X, e.Y, o.Z);
                        changed.Add(o);
                    }
                break;
            }
        }
        if (e.Category == BimCategory.Wall) StructuralRole.Set(e, entry.IsLoadBearing ? null : false);
        StandardTypeMark.Write(e, entry.Mark, entry.Standard);
        return changed;
    }

    private static void SetProfileThickness(ArchitecturalElement e, StandardLibraryEntry entry)
    {
        if (entry.ProfileType == ProfileTypes.IBeam)
        {
            e.ProfileWallThickness = entry.FlangeMm / 1000;
            e.ProfileWebThickness = entry.WebMm / 1000;
        }
        else if (entry.ProfileType == ProfileTypes.Pipe) e.ProfileWallThickness = entry.FlangeMm / 1000;
    }

    /// <summary>
    /// «Назначить тип из библиотеки» выделенным элементам: совместимые (та же категория) получают значения типа — ровно один шаг
    /// журнала транзакций, вьюпорт перестраивает только затронутые элементы (DocumentChanged).
    /// </summary>
    public static StandardApplyResult Apply(SceneStateService scene, StandardLibraryEntry entry, IReadOnlyList<ArchitecturalElement> targets)
    {
        var compatible = targets.Where(scene.Elements.Contains).Distinct().Where(entry.IsCompatible).ToList();
        var skipped = targets.Where(t => !compatible.Contains(t)).ToList();
        string name = $"Тип из библиотеки: {entry.Caption}{(compatible.Count > 1 ? $" ({compatible.Count} эл.)" : string.Empty)}";
        if (compatible.Count == 0) return new StandardApplyResult(entry, [], skipped, name);
        var changed = new List<ArchitecturalElement>();
        using (scene.BeginUndoTransaction(name))
        {
            foreach (var e in compatible) changed.AddRange(ApplyTo(e, entry, scene.GetHostedElements(e.Id).ToList()));
            scene.NotifyElementsChanged(changed.Distinct().ToList());
        }
        return new StandardApplyResult(entry, compatible, skipped, name);
    }

    // ───────────────────────────── Пользовательские библиотеки ─────────────────────────────

    /// <summary>
    /// «Сохранить тип в библиотеку»: запись по типу элемента (категория, материал, профиль, размеры типа, пирог, признак несущей).
    /// </summary>
    public static StandardLibraryEntry CaptureEntry(ArchitecturalElement e, string? mark = null, string? standard = null)
    {
        var c = CultureInfo.InvariantCulture;
        string Mm(double m) => Math.Round(m * 1000, 1).ToString("0.#", c);
        var entry = new StandardLibraryEntry
        {
            Id = "user-" + Guid.NewGuid().ToString("N")[..10],
            ElementType = e.Type,
            Material = e.Material,
            ProfileType = ProfileTypes.Normalize(e.ProfileType),
            Standard = standard ?? "Корпоративный стандарт",
            IsLoadBearing = StructuralRole.IsLoadBearing(e),
            Layers = e.Layers.Select(StandardLayerSpec.From).ToList()
        };
        switch (e.Category)
        {
            case BimCategory.Column:
                entry.Section = StandardLibrarySection.Columns;
                entry.WidthMm = Math.Round(e.Width * 1000, 1);
                entry.DepthMm = Math.Round(e.Depth * 1000, 1);
                entry.Mark = mark ?? $"К {Mm(e.Width)}×{Mm(e.Depth)}";
                break;
            case BimCategory.Beam:
                entry.Section = StandardLibrarySection.Beams;
                entry.WidthMm = Math.Round(e.Depth * 1000, 1);
                entry.DepthMm = Math.Round(e.Height * 1000, 1);
                entry.DefaultLengthMm = Math.Round(e.Width * 1000);
                entry.Mark = mark ?? $"Б {Mm(e.Depth)}×{Mm(e.Height)}";
                break;
            case BimCategory.Wall:
                entry.Section = entry.IsLoadBearing ? StandardLibrarySection.Walls : StandardLibrarySection.Partitions;
                entry.WidthMm = Math.Round(e.Depth * 1000, 1);
                if (entry.Layers.Count == 0) entry.Layers = [new StandardLayerSpec(e.Material, entry.WidthMm, MaterialLayers.DefaultDensity(MaterialLayerRole.Structural, e.Material), MaterialLayerRole.Structural)];
                entry.Mark = mark ?? $"{(entry.IsLoadBearing ? "С" : "Пг")} {Mm(e.Depth)}";
                break;
            case BimCategory.Foundation:
                entry.Section = StandardLibrarySection.Foundations;
                entry.WidthMm = Math.Round(e.Height * 1000, 1);
                entry.Mark = mark ?? $"Фп {Mm(e.Height)}";
                break;
            default:
                entry.Section = StandardLibrarySection.Slabs;
                entry.WidthMm = Math.Round(e.Height * 1000, 1);
                entry.Mark = mark ?? $"П {Mm(e.Height)}";
                break;
        }
        if (e.ProfileType == ProfileTypes.IBeam || e.ProfileType == ProfileTypes.Pipe)
        {
            var (wall, web) = e.ProfileThicknesses(e.Category == BimCategory.Beam ? e.Depth : e.Width, e.Category == BimCategory.Beam ? e.Height : e.Depth);
            entry.FlangeMm = Math.Round(wall * 1000, 2);
            entry.WebMm = Math.Round(web * 1000, 2);
        }
        entry.Name = string.IsNullOrWhiteSpace(e.Name) ? entry.Mark : $"{entry.Mark} · по образцу «{e.Name}»";
        return entry;
    }

    /// <summary>Добавляет запись в пользовательскую библиотеку и сохраняет её файл (кроме режима сессии).</summary>
    public StandardLibraryEntry AddToUserLibrary(StandardLibraryEntry entry)
    {
        var copy = entry.Clone();
        copy.IsBuiltIn = false;
        if (string.IsNullOrWhiteSpace(copy.Id) || UserLibrary.Find(copy.Id) is not null || BuiltIn.Find(copy.Id) is not null)
            copy.Id = "user-" + Guid.NewGuid().ToString("N")[..10];
        UserLibrary.Entries.Add(copy);
        SaveUserLibrary();
        return copy;
    }

    public bool RemoveFromUserLibrary(string id)
    {
        int removed = UserLibrary.Entries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) SaveUserLibrary();
        return removed > 0;
    }

    private StandardLibrary LoadUserLibrary()
    {
        string path = Path.Combine(LibraryDirectory, UserFileName);
        try
        {
            if (File.Exists(path)) return Read(path) is { } lib ? WithName(lib) : NewUserLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Повреждённый файл библиотеки не мешает работе: начинается пустая библиотека, файл не перезаписывается до первой правки.
        }
        return NewUserLibrary();

        static StandardLibrary WithName(StandardLibrary lib)
        {
            if (string.IsNullOrWhiteSpace(lib.Name)) lib.Name = UserLibraryName;
            return lib;
        }
    }

    private static StandardLibrary NewUserLibrary() => new() { Name = UserLibraryName, Author = Environment.UserName, Description = "Корпоративные стандарты конструкций" };

    private void SaveUserLibrary()
    {
        if (IsSessionOnly) return;
        Directory.CreateDirectory(LibraryDirectory);
        Write(UserLibrary, Path.Combine(LibraryDirectory, UserFileName));
    }

    // ───────────────────────────── Обмен .aialib.json ─────────────────────────────

    /// <summary>Сериализация библиотеки (JSON camelCase, кириллица без экранирования).</summary>
    public static string Serialize(StandardLibrary library) => JsonSerializer.Serialize(library, Json);

    public static StandardLibrary Deserialize(string json)
    {
        var lib = JsonSerializer.Deserialize<StandardLibrary>(json, Json) ?? throw new InvalidDataException("Пустой файл библиотеки");
        if (!string.Equals(lib.Format, StandardLibrary.FileFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Файл не является библиотекой Architect Studio Pro (формат «{lib.Format}»)");
        if (lib.Version > 1) throw new InvalidDataException($"Версия библиотеки {lib.Version} новее поддерживаемой (1)");
        lib.Entries = lib.Entries.Where(e => !string.IsNullOrWhiteSpace(e.ElementType) && BimCategories.FromType(e.ElementType) is var c &&
                                             c is BimCategory.Column or BimCategory.Beam or BimCategory.Wall or BimCategory.Slab or BimCategory.Foundation &&
                                             (e.WidthMm > 0 || e.Layers.Count > 0)).ToList();
        foreach (var e in lib.Entries)
        {
            e.IsBuiltIn = false;
            e.Layers = e.Layers.Where(l => l.ThicknessMm > 0).ToList();
        }
        return lib;
    }

    /// <summary>Экспорт библиотеки в файл .aialib.json (запись атомарная — через .tmp).</summary>
    public static void Write(StandardLibrary library, string path)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(library), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static StandardLibrary? Read(string path) => Deserialize(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Импорт файла .aialib.json: библиотека добавляется в список (одноимённая — заменяется).</summary>
    public StandardLibrary Import(string path)
    {
        var lib = Read(path)!;
        if (string.IsNullOrWhiteSpace(lib.Name)) lib.Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
        _imported.RemoveAll(l => string.Equals(l.Name, lib.Name, StringComparison.OrdinalIgnoreCase));
        _imported.Add(lib);
        return lib;
    }

    /// <summary>Перенос записей импортированной библиотеки в пользовательскую (корпоративный стандарт — на этом компьютере).</summary>
    public int MergeIntoUser(StandardLibrary library)
    {
        int n = 0;
        foreach (var e in library.Entries)
        {
            if (UserLibrary.Entries.Any(u => u.Mark == e.Mark && u.ElementType == e.ElementType && Math.Abs(u.ThicknessMm - e.ThicknessMm) < 0.01 && Math.Abs(u.DepthMm - e.DepthMm) < 0.01)) continue;
            var copy = e.Clone();
            if (UserLibrary.Find(copy.Id) is not null || BuiltIn.Find(copy.Id) is not null) copy.Id = "user-" + Guid.NewGuid().ToString("N")[..10];
            UserLibrary.Entries.Add(copy);
            n++;
        }
        if (n > 0) SaveUserLibrary();
        return n;
    }
}
