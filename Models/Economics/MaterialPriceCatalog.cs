using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models.Economics;

/// <summary>
/// База цен: название (поставщик или сборник), дата актуализации и позиции прайс-листа. Обмен со сметчиком — CSV для Excel
/// (UTF-8 с BOM, разделитель «;», десятичная запятая) и JSON.
/// </summary>
public sealed class MaterialPriceCatalog
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Дата актуализации цен.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Today;

    public List<SupplierPriceItem> Items { get; set; } = [];

    /// <summary>Позиция по коду материала (последняя с таким кодом побеждает — поправки дописываются в конец прайса).</summary>
    public SupplierPriceItem? Find(string code)
    {
        string key = PriceCodes.Normalize(code);
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].Key == key) return Items[i];
        return null;
    }

    public MaterialPriceCatalog Clone() => new() { Name = Name, UpdatedAt = UpdatedAt, Items = Items.Select(i => i.Clone()).ToList() };

    /// <summary>Копия без некорректных позиций; пустые категория и единица — по коду материала, пустое имя базы — «Прайс-лист».</summary>
    public MaterialPriceCatalog Normalized()
    {
        var copy = Clone();
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? "Прайс-лист" : copy.Name.Trim();
        copy.Items = copy.Items.Where(i => i is not null && i.IsValid).ToList();
        foreach (var i in copy.Items)
        {
            i.MaterialCode = i.MaterialCode.Trim();
            if (string.IsNullOrWhiteSpace(i.Category)) i.Category = PriceCodes.CategoryOf(i.MaterialCode);
            if (string.IsNullOrWhiteSpace(i.Unit)) i.Unit = PriceCodes.UnitOf(i.MaterialCode);
            if (string.IsNullOrWhiteSpace(i.DisplayName)) i.DisplayName = i.MaterialCode;
            i.SupplierName = i.SupplierName?.Trim() ?? string.Empty;
            i.SourceDocument = i.SourceDocument?.Trim() ?? string.Empty;
        }
        return copy;
    }

    // ───────────────────────────── CSV (Excel, русская локаль) ─────────────────────────────

    public const string CsvMarkerName = "База цен";
    public const string CsvMarkerDate = "Дата актуализации";

    public static readonly string[] CsvColumns =
        ["Код материала", "Категория", "Наименование", "Ед. изм.", "Цена за ед., руб.", "Поставщик", "Документ-основание"];

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>CSV для Excel: строки «База цен» и «Дата актуализации», пустая строка, заголовок и позиции (цена с запятой).</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append(CsvMarkerName).Append(';').Append(Escape(Name)).Append("\r\n");
        sb.Append(CsvMarkerDate).Append(';').Append(UpdatedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("\r\n");
        sb.Append(string.Join(";", CsvColumns.Select(Escape))).Append("\r\n");
        foreach (var i in Items)
        {
            sb.Append(Escape(i.MaterialCode)).Append(';').Append(Escape(i.Category)).Append(';').Append(Escape(i.DisplayName)).Append(';')
              .Append(Escape(i.Unit)).Append(';').Append(i.UnitPriceRub.ToString("0.##", Ru)).Append(';')
              .Append(Escape(i.SupplierName)).Append(';').Append(Escape(i.SourceDocument)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Байты файла CSV: UTF-8 с BOM — Excel открывает кириллицу без мастера импорта.</summary>
    public byte[] ToCsvBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(ToCsv())];

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>
    /// Разбор CSV прайс-листа: разделитель «;» (или «,»/табуляция, если «;» в заголовке нет), кавычки по RFC 4180, колонки — по
    /// заголовку (русские или английские имена в любом порядке), цена — с пробелами разрядов, «руб»/«₽», запятой или точкой.
    /// Без строки заголовка колонки берутся в порядке шаблона. Название базы — из строки «База цен» или имени файла.
    /// </summary>
    public static MaterialPriceCatalog FromCsv(string text, string? fallbackName = null)
    {
        text = text.TrimStart('﻿');
        var lines = SplitRecords(text);
        char sep = DetectSeparator(lines);
        var catalog = new MaterialPriceCatalog { Name = fallbackName ?? string.Empty };
        int[]? map = null;
        foreach (var line in lines)
        {
            var cells = SplitCells(line, sep);
            if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace)) continue;
            string first = cells[0].Trim();
            if (Is(first, CsvMarkerName, "Catalog", "Price base"))
            {
                if (cells.Count > 1 && cells[1].Trim().Length > 0) catalog.Name = cells[1].Trim();
                continue;
            }
            if (Is(first, CsvMarkerDate, "Updated", "Date"))
            {
                if (cells.Count > 1 && TryParseDate(cells[1], out var date)) catalog.UpdatedAt = date;
                continue;
            }
            if (map is null && HeaderMap(cells) is { } header)
            {
                map = header;
                continue;
            }
            var m = map ?? [0, 1, 2, 3, 4, 5, 6];
            string Cell(int column) => m[column] >= 0 && m[column] < cells.Count ? cells[m[column]].Trim() : string.Empty;
            if (!TryParsePrice(Cell(4), out double price)) continue;
            catalog.Items.Add(new SupplierPriceItem
            {
                MaterialCode = Cell(0),
                Category = Cell(1),
                DisplayName = Cell(2),
                Unit = Cell(3),
                UnitPriceRub = price,
                SupplierName = Cell(5),
                SourceDocument = Cell(6)
            });
        }
        return catalog.Normalized();
    }

    private static bool Is(string cell, params string[] names) => names.Any(n => string.Equals(cell.TrimEnd(':'), n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Номера колонок шаблона по заголовку (−1 — колонки нет); null — строка не заголовок.</summary>
    private static int[]? HeaderMap(List<string> cells)
    {
        string[][] aliases =
        [
            ["код материала", "код", "материал", "code", "material code", "materialcode"],
            ["категория", "category"],
            ["наименование", "название", "name", "description", "displayname"],
            ["ед. изм.", "ед.изм.", "единица", "ед", "unit"],
            ["цена за ед., руб.", "цена", "цена, руб.", "цена за единицу", "price", "unit price", "unitpricerub"],
            ["поставщик", "организация", "supplier", "vendor", "suppliername"],
            ["документ-основание", "основание", "документ", "источник", "source", "source document", "sourcedocument"]
        ];
        var map = new int[aliases.Length];
        Array.Fill(map, -1);
        for (int c = 0; c < cells.Count; c++)
        {
            string name = cells[c].Trim().ToLowerInvariant();
            for (int k = 0; k < aliases.Length; k++)
                if (map[k] < 0 && aliases[k].Contains(name))
                {
                    map[k] = c;
                    break;
                }
        }
        return map[0] >= 0 && map[4] >= 0 ? map : null;
    }

    /// <summary>Цена: «8 500,50», «8500.5», «8 500 руб.», «₽ 8500» (неразрывные пробелы разрядов — тоже).</summary>
    public static bool TryParsePrice(string text, out double value)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            if (char.IsDigit(ch) || ch is ',' or '.' or '-') sb.Append(ch);
        string clean = sb.ToString().TrimEnd('.', ',');
        // Запятая и точка вместе: последний знак — дробная часть, остальные — разряды.
        int lastSep = Math.Max(clean.LastIndexOf(','), clean.LastIndexOf('.'));
        if (lastSep >= 0) clean = clean[..lastSep].Replace(",", string.Empty).Replace(".", string.Empty) + "." + clean[(lastSep + 1)..];
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0;
    }

    private static bool TryParseDate(string text, out DateTime date) =>
        DateTime.TryParseExact(text.Trim(), ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd.MM.yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
        DateTime.TryParse(text.Trim(), Ru, DateTimeStyles.None, out date);

    private static char DetectSeparator(List<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains(';')) return ';';
            if (line.Contains('\t')) return '\t';
        }
        return ',';
    }

    /// <summary>Записи CSV с учётом переводов строк внутри кавычек.</summary>
    private static List<string> SplitRecords(string text)
    {
        var records = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char ch in text)
        {
            if (ch == '"') quoted = !quoted;
            if (!quoted && ch is '\r' or '\n')
            {
                if (sb.Length > 0) records.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) records.Add(sb.ToString());
        return records;
    }

    private static List<string> SplitCells(string line, char sep)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    // ───────────────────────────── JSON ─────────────────────────────

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MaterialPriceCatalog FromJson(string json, string? fallbackName = null)
    {
        var catalog = JsonSerializer.Deserialize<MaterialPriceCatalog>(json.TrimStart('﻿'), JsonOptions) ?? new MaterialPriceCatalog();
        catalog.Items ??= [];
        if (string.IsNullOrWhiteSpace(catalog.Name)) catalog.Name = fallbackName ?? string.Empty;
        return catalog.Normalized();
    }

    /// <summary>Чтение файла прайса: .json — JSON, иначе CSV; имя базы без строки «База цен» — имя файла.</summary>
    public static MaterialPriceCatalog Load(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        string name = Path.GetFileNameWithoutExtension(path);
        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? FromJson(text, name) : FromCsv(text, name);
    }

    /// <summary>Шаблон прайса для заполнения сметчиком: все позиции ведомости с ценами текущей базы.</summary>
    public static MaterialPriceCatalog Template(IEnumerable<SupplierPriceItem> items, string name = "Шаблон прайс-листа") => new()
    {
        Name = name,
        UpdatedAt = DateTime.Today,
        Items = items.Select(i => i.Clone()).ToList()
    };

    [JsonIgnore]
    public string Title => $"{Name} · {UpdatedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";
}
