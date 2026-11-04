using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Economics;

namespace AIArchitectStudio.Services;

/// <summary>Источники веб-импорта: ссылки на страницы магазинов, файлы прошлых проектов и смет, вставленный текст, индексация прошлых цен (%).</summary>
public sealed record PriceImportRequest(IReadOnlyList<string> Urls, IReadOnlyList<string> Paths, string Text, double IndexPercent)
{
    public bool IsEmpty => Urls.Count == 0 && Paths.Count == 0 && string.IsNullOrWhiteSpace(Text);
}

/// <summary>Откуда подтянута расценка.</summary>
public enum PriceSourceKind
{
    /// <summary>Страница интернет-магазина, металлобазы, бетонного завода (по ссылке).</summary>
    WebPage,

    /// <summary>Прошлый проект .aia (блок costEstimate).</summary>
    PastProject,

    /// <summary>Прошлая смета или прайс .csv / .json.</summary>
    PastEstimate,

    /// <summary>Вставленный текст (кусок таблицы Excel / Word, текст страницы сайта).</summary>
    PastedText
}

/// <summary>Достоверность распознанной позиции: микроразметка с единицей — высокая, таблица/текст с единицей — средняя, пересчёт с допущениями — низкая.</summary>
public enum PriceConfidence
{
    High,
    Medium,
    Low
}

/// <summary>Сырое предложение со страницы до нормализации: наименование, цена, единица источника, контекст строки и способ извлечения.</summary>
public sealed record RawPriceOffer(string Name, double Price, string Unit, string Context, string Method, bool StructuredData);

/// <summary>
/// Позиция пакета на подтверждение: материал проекта (код B25 / A500C / A240 / C245 / GLAZING…), найденное наименование, единица
/// проекта, старая и новая цена (новая — редактируется пользователем), пересчёт единиц, магазин/источник и достоверность.
/// </summary>
public sealed class ExtractedPriceItem
{
    public bool IsChecked { get; set; } = true;
    public string MaterialCode { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ProjectTitle { get; set; } = string.Empty;
    public string FoundName { get; set; } = string.Empty;

    /// <summary>Единица проекта (м³, т, м², шт).</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Единица источника (п.м, кг, т, м³ с доставкой…).</summary>
    public string SourceUnit { get; set; } = string.Empty;

    public double SourcePrice { get; set; }
    public double? OldPriceRub { get; set; }
    public double NewPriceRub { get; set; }

    /// <summary>Пояснение пересчёта: «89 ₽/м ÷ 0,888 кг/м × 1000 = 100 225 ₽/т», «6 950 − доставка 500 = 6 450 ₽/м³».</summary>
    public string Conversion { get; set; } = string.Empty;

    public string Supplier { get; set; } = string.Empty;
    public string SourceDocument { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime? SourceDate { get; set; }
    public PriceSourceKind Kind { get; set; }
    public PriceConfidence Confidence { get; set; } = PriceConfidence.Medium;
    public string Method { get; set; } = string.Empty;

    /// <summary>Позиция входит в смету каркаса проекта (иначе — справочно: кирпич, газобетон).</summary>
    public bool InEstimate { get; set; } = true;

    public double DeltaPercent => OldPriceRub is > 0 ? (NewPriceRub - OldPriceRub.Value) / OldPriceRub.Value * 100 : 0;

    /// <summary>Достоверность для чипа окна проверки (способ извлечения — в строке найденного наименования).</summary>
    public string ConfidenceText => Confidence switch
    {
        PriceConfidence.High => "Высокая",
        PriceConfidence.Medium => "Средняя",
        _ => "Низкая · проверьте"
    };

    public string ConfidenceColorHex => Confidence switch
    {
        PriceConfidence.High => "#30D158",
        PriceConfidence.Medium => "#FF9F0A",
        _ => "#FF453A"
    };

    public ExtractedPriceItem Clone() => (ExtractedPriceItem)MemberwiseClone();
}

/// <summary>Пакет расценок на подтверждение пользователем (окно «Проверка подтянутых расценок — всё ли верно?»).</summary>
public sealed class PriceExtractionBatch
{
    public List<ExtractedPriceItem> Items { get; } = [];
    public List<string> Sources { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>Коэффициент индексации прошлых смет, % (например, +5).</summary>
    public double IndexPercent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Пользователь подтвердил пакет («Да, всё верно — применить в смету»).</summary>
    public bool Confirmed { get; set; }

    public IEnumerable<ExtractedPriceItem> Checked => Items.Where(i => i.IsChecked && i.NewPriceRub > 0 && i.MaterialCode.Length > 0);
}

/// <summary>
/// Бесплатный извлекатель расценок из ссылок на магазины стройматериалов (металлобазы, бетонные заводы) и из прошлых смет.
/// Страница загружается <see cref="HttpClient"/> (UTF-8 / Windows-1251 по заголовку, meta charset или эвристике), цены берутся из
/// микроразметки Schema.org/Product (JSON-LD, microdata), HTML-таблиц прайсов и текстовых шаблонов «наименование — цена за единицу».
/// Наименование сопоставляется с кодом материала проекта (B15…B60, A240/A400/A500C, C245/C345, двутавры, остекление, кладка),
/// единицы пересчитываются в единицы сметы (арматура за п.м или кг → ₽/т по массе стержня, двутавр за п.м → ₽/т по массе профиля,
/// бетон с доставкой — чистая цена за м³), поставщик — из og:site_name / заголовка / домена, основание — URL и дата.
/// Прошлые сметы: .aia (costEstimate), .csv / .json прайсы, вставленный текст из Excel/Word — с индексацией цен.
/// Ничего не применяется без подтверждения: <see cref="ConfirmAndApply"/> вызывается только после окна проверки.
/// </summary>
public static class SmartPriceExtractorService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true
        })
        { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(Windows NT 10.0; Win64; x64)"));
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.6");
        return client;
    }

    // ───────────────────────────── Загрузка страницы ─────────────────────────────

    /// <summary>Загрузка страницы магазина: HTML в правильной кодировке и итоговый адрес (после переадресаций).</summary>
    public static async Task<(string Html, Uri Url)> FetchAsync(Uri url, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        if (url.Scheme is not ("http" or "https")) throw new InvalidDataException("Поддерживаются только ссылки http:// и https://");
        using var client = handler is null ? null : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        using var response = await (client ?? Http).GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Сайт ответил HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return (Decode(bytes, response.Content.Headers.ContentType?.CharSet), response.RequestMessage?.RequestUri ?? url);
    }

    /// <summary>Декодирование: charset заголовка → meta charset → BOM → проверка UTF-8 → Windows-1251.</summary>
    public static string Decode(byte[] bytes, string? headerCharset = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        string? charset = headerCharset?.Trim('"', '\'', ' ');
        if (string.IsNullOrEmpty(charset))
        {
            string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var m = Regex.Match(head, @"charset\s*=\s*[""']?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (m.Success) charset = m.Groups[1].Value;
        }
        if (!string.IsNullOrEmpty(charset))
        {
            try
            {
                var enc = charset.Equals("cp1251", StringComparison.OrdinalIgnoreCase) ? Encoding.GetEncoding(1251) : Encoding.GetEncoding(charset);
                return enc.GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Неизвестное имя кодировки — эвристика ниже.
            }
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    // ───────────────────────────── Разбор страницы ─────────────────────────────

    /// <summary>Все предложения страницы: JSON-LD → microdata → таблицы → текст (дубли по наименованию и цене убираются).</summary>
    public static List<RawPriceOffer> ParseHtml(string html)
    {
        var offers = new List<RawPriceOffer>();
        offers.AddRange(ParseJsonLd(html));
        offers.AddRange(ParseMicrodata(html));
        offers.AddRange(ParseTables(html));
        offers.AddRange(ParseText(HtmlToText(RemoveTables(html)), "текст страницы"));
        return Dedupe(offers);
    }

    private static List<RawPriceOffer> Dedupe(List<RawPriceOffer> offers)
    {
        var seen = new HashSet<string>();
        var list = new List<RawPriceOffer>();
        foreach (var o in offers)
            if (seen.Add(Clean(o.Name).ToLowerInvariant() + "|" + o.Price.ToString("R", CultureInfo.InvariantCulture) + "|" + o.Unit))
                list.Add(o);
        return list;
    }

    /// <summary>Schema.org/Product в &lt;script type="application/ld+json"&gt;: Offer / AggregateOffer, priceSpecification, unitCode / unitText, @graph, ItemList.</summary>
    public static List<RawPriceOffer> ParseJsonLd(string html)
    {
        var list = new List<RawPriceOffer>();
        foreach (Match m in Regex.Matches(html, @"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            try
            {
                using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value.Trim()), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                Walk(doc.RootElement, list);
            }
            catch (JsonException)
            {
                // Битая микроразметка — остальные способы разбора.
            }
        }
        return list;

        static void Walk(JsonElement e, List<RawPriceOffer> list)
        {
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray()) Walk(x, list);
                return;
            }
            if (e.ValueKind != JsonValueKind.Object) return;
            string type = e.TryGetProperty("@type", out var t) ? t.ValueKind == JsonValueKind.Array ? string.Join(",", t.EnumerateArray().Select(x => x.ToString())) : t.ToString() : string.Empty;
            if (type.Contains("Product", StringComparison.OrdinalIgnoreCase) && e.TryGetProperty("name", out var name))
            {
                string description = e.TryGetProperty("description", out var d) ? d.ToString() : string.Empty;
                if (e.TryGetProperty("offers", out var offers))
                    foreach (var offer in offers.ValueKind == JsonValueKind.Array ? offers.EnumerateArray().ToList() : [offers])
                        if (OfferPrice(offer) is { } p)
                            list.Add(new RawPriceOffer(name.ToString(), p.Price, p.Unit, description, "микроразметка JSON-LD", true));
            }
            foreach (var key in new[] { "@graph", "itemListElement", "item", "mainEntity" })
                if (e.TryGetProperty(key, out var child)) Walk(child, list);
        }

        static (double Price, string Unit)? OfferPrice(JsonElement offer)
        {
            if (offer.ValueKind != JsonValueKind.Object) return null;
            string unit = string.Empty;
            double? price = Num(offer, "price") ?? Num(offer, "lowPrice");
            if (offer.TryGetProperty("priceSpecification", out var spec))
                foreach (var s in spec.ValueKind == JsonValueKind.Array ? spec.EnumerateArray().ToList() : [spec])
                {
                    price ??= Num(s, "price");
                    unit = Unit(s, unit);
                }
            unit = Unit(offer, unit);
            if (offer.TryGetProperty("eligibleQuantity", out var q)) unit = Unit(q, unit);
            return price is > 0 ? (price.Value, unit) : null;
        }

        static string Unit(JsonElement e, string current)
        {
            if (current.Length > 0 || e.ValueKind != JsonValueKind.Object) return current;
            if (e.TryGetProperty("unitText", out var text) && text.ToString().Length > 0) return text.ToString();
            if (e.TryGetProperty("unitCode", out var code))
                return code.ToString().ToUpperInvariant() switch
                {
                    "MTQ" => "м³",
                    "TNE" => "т",
                    "KGM" => "кг",
                    "MTR" => "м",
                    "MTK" => "м²",
                    "H87" or "C62" or "PCE" => "шт",
                    _ => string.Empty
                };
            return current;
        }

        static double? Num(JsonElement e, string key)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            return MaterialPriceCatalog.TryParsePrice(v.ToString(), out var d) ? d : null;
        }
    }

    /// <summary>Microdata: блоки itemtype=…schema.org/Product с itemprop="name" и itemprop="price" (content или текст).</summary>
    public static List<RawPriceOffer> ParseMicrodata(string html)
    {
        var list = new List<RawPriceOffer>();
        var starts = Regex.Matches(html, @"itemtype\s*=\s*[""']https?://schema\.org/Product[""']", RegexOptions.IgnoreCase).Select(m => m.Index).ToList();
        for (int i = 0; i < starts.Count; i++)
        {
            string block = html[starts[i]..(i + 1 < starts.Count ? starts[i + 1] : Math.Min(html.Length, starts[i] + 20000))];
            string? name = Prop(block, "name");
            string? priceText = Prop(block, "price");
            if (name is null || priceText is null || !MaterialPriceCatalog.TryParsePrice(priceText, out var price) || price <= 0) continue;
            string unit = Prop(block, "unitText") ?? UnitAfterPrice(HtmlToText(block)) ?? string.Empty;
            list.Add(new RawPriceOffer(name, price, unit, HtmlToText(block), "микроразметка Microdata", true));
        }
        return list;

        static string? Prop(string block, string prop)
        {
            var m = Regex.Match(block, $@"itemprop\s*=\s*[""']{prop}[""'][^>]*?content\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(block, $@"content\s*=\s*[""']([^""']*)[""'][^>]*?itemprop\s*=\s*[""']{prop}[""']", RegexOptions.IgnoreCase);
            if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            m = Regex.Match(block, $@"itemprop\s*=\s*[""']{prop}[""'][^>]*>(.*?)</", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success ? Clean(HtmlToText(m.Groups[1].Value)) : null;
        }
    }

    /// <summary>
    /// HTML-таблицы прайсов: строка с наименованием материала и ценой; колонки «ед. изм.» и «цена» — по заголовку таблицы, иначе
    /// цена — первая ячейка-число с «руб»/«₽» или последняя числовая ячейка, единица — из ячейки цены или соседней.
    /// </summary>
    public static List<RawPriceOffer> ParseTables(string html)
    {
        var list = new List<RawPriceOffer>();
        foreach (Match table in Regex.Matches(html, @"<table\b.*?</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            int unitCol = -1, priceCol = -1, nameCol = -1;
            foreach (Match row in Regex.Matches(table.Value, @"<tr\b.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var cells = Regex.Matches(row.Value, @"<t[dh]\b[^>]*>(.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                    .Select(c => Clean(HtmlToText(c.Groups[1].Value))).ToList();
                if (cells.Count < 2) continue;
                if (Regex.IsMatch(row.Value, "<th", RegexOptions.IgnoreCase) || cells.All(c => !Regex.IsMatch(c, @"\d{2,}")))
                {
                    for (int i = 0; i < cells.Count; i++)
                    {
                        string c = cells[i].ToLowerInvariant();
                        if (unitCol < 0 && (c.StartsWith("ед") || c.Contains("единиц"))) unitCol = i;
                        else if (priceCol < 0 && (c.Contains("цена") || c.Contains("стоимост") || c.Contains("price"))) priceCol = i;
                        else if (nameCol < 0 && (c.Contains("наименован") || c.Contains("товар") || c.Contains("продукц") || c.Contains("материал") || c.Contains("марка"))) nameCol = i;
                    }
                    continue;
                }
                string name = nameCol >= 0 && nameCol < cells.Count ? cells[nameCol] : cells.FirstOrDefault(c => MatchMaterial(c) is not null) ?? cells[0];
                if (MatchMaterial(name) is null && MatchMaterial(string.Join(" ", cells)) is null) continue;
                double price = 0;
                string unit = unitCol >= 0 && unitCol < cells.Count ? cells[unitCol] : string.Empty;
                if (priceCol >= 0 && priceCol < cells.Count && MaterialPriceCatalog.TryParsePrice(cells[priceCol], out var p)) price = p;
                else
                {
                    var priced = cells.Select((c, i) => (c, i)).Where(x => x.i != (nameCol < 0 ? 0 : nameCol) && Regex.IsMatch(x.c, @"(₽|руб|р\.)", RegexOptions.IgnoreCase)).ToList();
                    var cell = priced.Count > 0 ? priced[0].c : cells.LastOrDefault(c => MaterialPriceCatalog.TryParsePrice(c, out var v) && v >= 10) ?? string.Empty;
                    if (MaterialPriceCatalog.TryParsePrice(StripUnit(cell), out var v2)) price = v2;
                    if (unit.Length == 0) unit = UnitAfterPrice(cell) ?? string.Empty;
                }
                if (price <= 0) continue;
                if (unit.Length == 0) unit = cells.Select(c => NormalizeUnit(c)).FirstOrDefault(u => u.Length > 0) ?? string.Empty;
                list.Add(new RawPriceOffer(name, price, NormalizeUnit(unit), string.Join(" · ", cells), "таблица прайса", false));
            }
        }
        return list;
    }

    private static readonly Regex PriceRx = new(@"(?<!\d)(\d{1,3}(?:[\s  ]\d{3})+|\d+)(?:[.,](\d{1,2}))?\s*(?:₽|руб(?:лей|\.)?|р\.|RUB)(?:\s*(?:/|за|в|за\s+1)\s*(?<unit>[а-яa-z0-9³²\.\s]{1,12}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Текстовые шаблоны: «Бетон В25 (М350) — 6 450 руб./м³», «Арматура А500С Ø12 — 89 ₽ за п.м», «цена: 72 900 ₽/т». Строка с
    /// материалом без цены объединяется со следующей (карточка товара: наименование и цена в разных строках).
    /// </summary>
    public static List<RawPriceOffer> ParseText(string text, string method = "текст")
    {
        var list = new List<RawPriceOffer>();
        var lines = text.Replace("\r", string.Empty).Split('\n').Select(l => Clean(l.Replace('\t', ' '))).Where(l => l.Length > 0).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (MatchMaterial(line) is null) continue;
            var prices = PriceRx.Matches(line);
            if (prices.Count == 0 && i + 1 < lines.Count && MatchMaterial(lines[i + 1]) is null)
            {
                line = line + " " + lines[i + 1];
                prices = PriceRx.Matches(line);
            }
            if (prices.Count == 0)
            {
                // Строка таблицы Excel без «руб»: последнее число ≥ 10 — цена, единица — по ячейкам строки.
                var cells = lines[i].Split(new[] { "  ", " | ", ";" }, StringSplitOptions.RemoveEmptyEntries);
                var last = Regex.Matches(lines[i], @"(?<![\dA-Za-zА-Яа-яØ])\d{1,3}(?:[\s ]\d{3})+(?:[.,]\d{1,2})?|(?<![\dA-Za-zА-Яа-яØ])\d{2,7}(?:[.,]\d{1,2})?(?![\dмММ])").Cast<Match>().LastOrDefault();
                if (last is null || !MaterialPriceCatalog.TryParsePrice(last.Value, out var v) || v < 10) continue;
                string head = lines[i][..last.Index];
                list.Add(new RawPriceOffer(Clean(head), v, NormalizeUnit(head), lines[i], method, false));
                continue;
            }
            // Несколько цен в строке («с доставкой 6 950 / без доставки 6 450») — каждая отдельным предложением с контекстом.
            foreach (Match p in prices)
            {
                if (!MaterialPriceCatalog.TryParsePrice(p.Groups[1].Value + (p.Groups[2].Success ? "," + p.Groups[2].Value : string.Empty), out var price) || price <= 0) continue;
                if (IsDeliveryPrice(line, p.Index)) continue;
                string unit = p.Groups["unit"].Success && NormalizeUnit(p.Groups["unit"].Value) is { Length: > 0 } u ? u : NormalizeUnit(line[p.Index..]);
                string name = Clean(line[..Math.Max(0, p.Index)]);
                if (MatchMaterial(name) is null) name = Clean(lines[i]);
                string context = line + (i + 1 < lines.Count ? " " + lines[i + 1] : string.Empty);
                list.Add(new RawPriceOffer(name.Length > 0 ? name : line, price, unit, context, method, false));
            }
        }
        return list;
    }

    /// <summary>Цена относится к доставке («доставка — 500 руб/м³»), а не к товару.</summary>
    private static bool IsDeliveryPrice(string line, int priceIndex)
    {
        string before = line[..priceIndex].ToLowerInvariant();
        int d = before.LastIndexOf("доставк", StringComparison.Ordinal);
        if (d < 0) return false;
        string tail = before[d..];
        // «с доставкой 6 950» — цена товара с доставкой (не сама доставка); «доставка 500», «доставка: 3 500 за рейс» — доставка.
        return !Regex.IsMatch(before[Math.Max(0, d - 3)..d], @"\bс\s*$") && !tail.Contains("без");
    }

    /// <summary>Стоимость доставки бетона на 1 м³ по тексту: «доставка 500 руб/м³» или «3 500 ₽ за рейс (миксер 7 м³)».</summary>
    public static double? DeliveryPerM3(string text)
    {
        var perM3 = Regex.Match(text, @"доставк\w*[^\d\n]{0,25}?(\d{1,3}(?:[\s ]\d{3})*|\d+)\s*(?:₽|руб\.?|р\.)\s*(?:/|за)\s*(?:1\s*)?(?:м³|м3|куб)", RegexOptions.IgnoreCase);
        if (perM3.Success && MaterialPriceCatalog.TryParsePrice(perM3.Groups[1].Value, out var v)) return v;
        var trip = Regex.Match(text, @"доставк\w*[^\d\n]{0,25}?(\d{1,3}(?:[\s ]\d{3})*|\d+)\s*(?:₽|руб\.?|р\.)[^\n]{0,40}?(?:рейс|миксер|автобетоносмесител)\w*[^\d\n]{0,25}?(\d+(?:[.,]\d+)?)\s*(?:м³|м3|куб)",
            RegexOptions.IgnoreCase);
        if (trip.Success && MaterialPriceCatalog.TryParsePrice(trip.Groups[1].Value, out var t) &&
            double.TryParse(trip.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var vol) && vol > 0)
            return Math.Round(t / vol, 2);
        return null;
    }

    // ───────────────────────────── Сопоставление и пересчёт ─────────────────────────────

    /// <summary>Сопоставление наименования с материалом проекта: код, категория, единица сметы и признак позиции сметы каркаса.</summary>
    public static (string Code, string Category, string Unit, string Title, bool InEstimate)? MatchMaterial(string name)
    {
        string n = name.ToLowerInvariant().Replace('ё', 'е');
        string lat = MaterialLibrary.NormalizeCode(name).ToUpperInvariant();
        if (Regex.IsMatch(n, @"бетон|бетонн|concrete") && !Regex.IsMatch(n, @"газобетон|пенобетон|блок"))
        {
            var b = Regex.Match(name, @"[ВB]\s?(\d{2}(?:[.,]5)?)", RegexOptions.IgnoreCase);
            string? code = b.Success ? "B" + b.Groups[1].Value.Replace(',', '.') : null;
            if (code is null && Regex.Match(name, @"[МM]\s?(\d{3})") is { Success: true } mm)
                code = mm.Groups[1].Value switch
                {
                    "200" => "B15",
                    "250" => "B20",
                    "350" => "B25",
                    "400" => "B30",
                    "450" => "B35",
                    "500" or "550" => "B40",
                    "600" => "B45",
                    _ => null
                };
            if (code is null || MaterialLibrary.Standard(code) is null) return null;
            return (code, PriceCodes.CategoryConcrete, "м³", "Бетон " + code, true);
        }
        if (Regex.IsMatch(n, @"арматур|rebar|стержн"))
        {
            string code = Regex.IsMatch(lat, @"A\s?500|A-?III[^I]|A500C") || n.Contains("500") ? "A500C"
                : Regex.IsMatch(lat, @"A\s?400") || n.Contains("a-iii") || n.Contains("а-iii") || n.Contains("а3") || n.Contains("a3") ? "A400"
                : Regex.IsMatch(lat, @"A\s?240|A-?I\b") || n.Contains("гладк") ? "A240" : "A500C";
            return (code, PriceCodes.CategoryRebar, "т", "Арматура " + code, true);
        }
        if (Regex.IsMatch(n, @"двутавр|балка\s*\d{2}[бш]|i-beam|швеллер|прокат|лист\s+г/?к|сталь\s*(?:с|c)\s?[23]45"))
        {
            string code = Regex.IsMatch(lat, @"C\s?345") ? "C345" : "C245";
            return (code, PriceCodes.CategorySteel, "т", (n.Contains("двутавр") ? "Двутавр " : "Прокат ") + code, true);
        }
        if (Regex.IsMatch(n, @"стеклопакет|остеклен|витраж|окно\s+пвх|окна\s+пвх"))
            return (PriceCodes.Glazing, PriceCodes.CategoryGlazing, "м²", "Остекление", true);
        if (Regex.IsMatch(n, @"кирпич"))
            return ("BRICK", "Кладка", NormalizeUnit(n) is { Length: > 0 } u && u != "м³" ? u : "шт", "Кирпич", false);
        if (Regex.IsMatch(n, @"газобетон|газоблок|ячеист"))
            return ("AERATED", "Кладка", "м³", "Газобетон", false);
        return null;
    }

    /// <summary>Единица по тексту: м³, т, кг, м (погонный), м², шт; пусто — не распознана.</summary>
    public static string NormalizeUnit(string text)
    {
        string t = " " + text.ToLowerInvariant().Replace(" ", " ") + " ";
        if (Regex.IsMatch(t, @"(м³|м3|куб\.?\s*м|кубометр|/\s*куб|за\s+куб)")) return "м³";
        if (Regex.IsMatch(t, @"(м²|м2|кв\.?\s*м)")) return "м²";
        if (Regex.IsMatch(t, @"(п\.?\s*м|пог\.?\s*м|м\.?\s*п\.|погонн|/\s*м\b|за\s+м\b|за\s+метр|/\s*метр)")) return "м";
        if (Regex.IsMatch(t, @"(/\s*кг|за\s+кг|\bкг\b|килограмм)")) return "кг";
        if (Regex.IsMatch(t, @"(/\s*т\b|/\s*тн|за\s+т\b|за\s+тонн|\bтн\b|\bтонн|\bт\b)")) return "т";
        if (Regex.IsMatch(t, @"(/\s*шт|за\s+шт|\bшт\b)")) return "шт";
        return string.Empty;
    }

    private static string? UnitAfterPrice(string text)
    {
        var m = PriceRx.Match(text);
        if (!m.Success) return null;
        string u = m.Groups["unit"].Success ? NormalizeUnit(m.Groups["unit"].Value) : NormalizeUnit(text[m.Index..]);
        return u.Length > 0 ? u : null;
    }

    private static string StripUnit(string cell) => Regex.Replace(cell, @"(₽|руб\.?|р\.|/.*$|за\s.*$)", string.Empty, RegexOptions.IgnoreCase).Trim();

    /// <summary>Диаметр стержня, мм: «Ø12», «d12», «ф12», «12 мм», «12мм», «диаметр 12».</summary>
    public static double? BarDiameterMm(string text)
    {
        var m = Regex.Match(text, @"(?:Ø|ø|⌀|d\s?=?|ф|диаметр(?:ом)?\s*)\s?(\d{1,2})(?!\d)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(text, @"(?<![\dА-Яа-яA-Za-z])(\d{1,2})\s?мм", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d is >= 4 and <= 40 ? d : null;
    }

    /// <summary>Масса 1 м стержня, кг: 0,00617·d² (d, мм; ГОСТ 34028-2016).</summary>
    public static double RebarKgPerMeter(double diameterMm) => 0.00617 * diameterMm * diameterMm;

    /// <summary>Масса 1 м двутавра, кг, по ГОСТ Р 57837-2017: (2·b·tf + (h − 2·tf)·tw)·ρ.</summary>
    public static double? BeamKgPerMeter(string name)
    {
        var m = Regex.Match(name, @"(\d{2})\s?([БШKК]|B)\s?(\d)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string mark = m.Groups[1].Value + (m.Groups[2].Value.ToUpperInvariant() is "Ш" ? "Ш" : "Б") + m.Groups[3].Value;
        (double h, double b, double tw, double tf)? s = mark switch
        {
            "20Б1" => (200, 100, 5.6, 8.5),
            "25Б1" => (248, 124, 5.0, 8.0),
            "30Б1" => (296, 140, 5.8, 8.5),
            "35Б1" => (346, 155, 6.2, 8.5),
            "40Б1" => (392, 165, 7.0, 9.5),
            "35Ш1" => (338, 250, 9.5, 12.5),
            "40Ш1" => (388, 300, 9.5, 14.0),
            _ => null
        };
        if (s is not { } v) return null;
        return (2 * v.b * v.tf + (v.h - 2 * v.tf) * v.tw) * 1e-6 * QuantityScheduleService.SteelDensity;
    }

    /// <summary>
    /// Нормализация предложения в позицию сметы: код материала, пересчёт в единицу проекта, чистая цена бетона без доставки.
    /// null — материал не распознан или единица не пересчитывается.
    /// </summary>
    /// <param name="pageDelivery">Стоимость доставки бетона на 1 м³, найденная в тексте всей страницы (микроразметка товара её не содержит).</param>
    public static ExtractedPriceItem? Normalize(RawPriceOffer offer, string supplier, string source, PriceSourceKind kind, string url = "", DateTime? date = null,
        double? pageDelivery = null)
    {
        if (MatchMaterial(offer.Name) is not { } mat && MatchMaterial(offer.Context) is not { } ctxMat) return null;
        var material = MatchMaterial(offer.Name) ?? MatchMaterial(offer.Context)!.Value;
        string unit = offer.Unit.Length > 0 ? NormalizeUnit(offer.Unit) is { Length: > 0 } nu ? nu : offer.Unit : string.Empty;
        var confidence = offer.StructuredData && unit.Length > 0 ? PriceConfidence.High : unit.Length > 0 ? PriceConfidence.Medium : PriceConfidence.Low;
        double price = offer.Price;
        string conversion = string.Empty;

        switch (material.Unit)
        {
            case "м³" when material.Category == PriceCodes.CategoryConcrete:
            {
                if (unit.Length == 0) unit = "м³";
                if (unit != "м³") return null;
                bool withDelivery = Regex.IsMatch(offer.Name + " " + offer.Context, @"\bс\s+доставк", RegexOptions.IgnoreCase) &&
                                    !Regex.IsMatch(offer.Name, @"без\s+доставк", RegexOptions.IgnoreCase);
                if (withDelivery && (DeliveryPerM3(offer.Context) ?? pageDelivery) is { } delivery && delivery < price)
                {
                    conversion = $"{Rub(price)}/м³ с доставкой − доставка {Rub(delivery)}/м³ = {Rub(price - delivery)}/м³";
                    price -= delivery;
                    confidence = PriceConfidence.Medium;
                }
                else if (withDelivery)
                {
                    conversion = "цена с доставкой — стоимость доставки на странице не найдена";
                    confidence = PriceConfidence.Low;
                }
                break;
            }
            case "т" when material.Category == PriceCodes.CategoryRebar:
            {
                if (unit == "кг")
                {
                    conversion = $"{Rub(price)}/кг × 1 000 = {Rub(price * 1000)}/т";
                    price *= 1000;
                }
                else if (unit == "м")
                {
                    var d = BarDiameterMm(offer.Name) ?? BarDiameterMm(offer.Context);
                    if (d is null) return null;
                    double kg = RebarKgPerMeter(d.Value);
                    double perTon = price / kg * 1000;
                    conversion = $"{Rub(price)}/п.м ÷ {kg.ToString("0.###", Ru)} кг/м (Ø{d.Value:0}) × 1 000 = {Rub(perTon)}/т";
                    price = perTon;
                    if (confidence == PriceConfidence.High) confidence = PriceConfidence.Medium;
                }
                else if (unit.Length == 0)
                {
                    // Без единицы: цена > 20 000 — за тонну, иначе не определить.
                    if (price < 20000) return null;
                    unit = "т";
                    conversion = "единица не указана — принята тонна";
                    confidence = PriceConfidence.Low;
                }
                else if (unit != "т") return null;
                break;
            }
            case "т" when material.Category == PriceCodes.CategorySteel:
            {
                if (unit == "кг")
                {
                    conversion = $"{Rub(price)}/кг × 1 000 = {Rub(price * 1000)}/т";
                    price *= 1000;
                }
                else if (unit == "м")
                {
                    var kg = BeamKgPerMeter(offer.Name) ?? BeamKgPerMeter(offer.Context);
                    if (kg is null) return null;
                    double perTon = price / kg.Value * 1000;
                    conversion = $"{Rub(price)}/п.м ÷ {kg.Value.ToString("0.##", Ru)} кг/м (ГОСТ Р 57837) × 1 000 = {Rub(perTon)}/т";
                    price = perTon;
                    if (confidence == PriceConfidence.High) confidence = PriceConfidence.Medium;
                }
                else if (unit.Length == 0 && price >= 20000)
                {
                    unit = "т";
                    conversion = "единица не указана — принята тонна";
                    confidence = PriceConfidence.Low;
                }
                else if (unit != "т") return null;
                break;
            }
            case "м²":
                if (unit.Length == 0) unit = "м²";
                if (unit != "м²") return null;
                break;
            default:
                if (unit.Length == 0) unit = material.Unit;
                break;
        }
        return new ExtractedPriceItem
        {
            MaterialCode = material.Code,
            Category = material.Category,
            ProjectTitle = material.Title,
            FoundName = Clean(offer.Name).Length > 120 ? Clean(offer.Name)[..120] + "…" : Clean(offer.Name),
            Unit = material.Unit == "шт" ? unit : material.Unit,
            SourceUnit = unit,
            SourcePrice = offer.Price,
            NewPriceRub = Math.Round(price, 2),
            Conversion = conversion,
            Supplier = supplier,
            SourceDocument = source,
            Url = url,
            SourceDate = date,
            Kind = kind,
            Confidence = confidence,
            Method = offer.Method,
            InEstimate = material.InEstimate
        };
    }

    /// <summary>Поставщик страницы: og:site_name → последняя часть заголовка («… | Металлобаза Север») → домен.</summary>
    public static string SupplierFromHtml(string html, Uri? url)
    {
        var og = Regex.Match(html, @"<meta[^>]+property\s*=\s*[""']og:site_name[""'][^>]*content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!og.Success) og = Regex.Match(html, @"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]*property\s*=\s*[""']og:site_name[""']", RegexOptions.IgnoreCase);
        if (og.Success) return Clean(WebUtility.HtmlDecode(og.Groups[1].Value));
        var title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (title.Success)
        {
            var parts = Regex.Split(Clean(WebUtility.HtmlDecode(title.Groups[1].Value)), @"\s[|—–-]\s").Where(p => p.Trim().Length > 2).ToList();
            if (parts.Count > 1) return parts[^1].Trim();
        }
        return url is null ? "Источник без названия" : url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
    }

    /// <summary>Разбор страницы магазина в позиции (без загрузки — для HTML, полученного любым способом).</summary>
    public static List<ExtractedPriceItem> ExtractFromHtml(string html, Uri? url, DateTime? date = null)
    {
        string supplier = SupplierFromHtml(html, url);
        var day = date ?? DateTime.Today;
        string source = $"{url?.ToString() ?? "страница сайта"} · {day.ToString("dd.MM.yyyy", Ru)}";
        double? delivery = DeliveryPerM3(HtmlToText(html));
        var items = ParseHtml(html).Select(o => Normalize(o, supplier, source, PriceSourceKind.WebPage, url?.ToString() ?? string.Empty, day, delivery))
            .OfType<ExtractedPriceItem>().ToList();
        return PreferBest(items);
    }

    /// <summary>
    /// По одному предложению на код материала: выше достоверность, затем цена прямо в единице сметы (без пересчёта и вычета доставки), затем — минимальная цена
    /// (минимальная цена разумна для одинакового материала одного магазина; пользователь видит и правит её в окне проверки).
    /// </summary>
    public static List<ExtractedPriceItem> PreferBest(IEnumerable<ExtractedPriceItem> items) =>
        items.GroupBy(i => i.MaterialCode + "|" + i.Supplier)
            .Select(g => g.OrderBy(i => i.Confidence).ThenBy(i => i.Conversion.Length > 0 ? 1 : 0).ThenBy(i => i.NewPriceRub).First())
            .ToList();

    /// <summary>Загрузка и разбор ссылки (страница магазина, металлобазы, завода).</summary>
    public static async Task<List<ExtractedPriceItem>> ExtractFromUrlAsync(Uri url, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        var (html, final) = await FetchAsync(url, ct, handler).ConfigureAwait(false);
        return ExtractFromHtml(html, final);
    }

    // ───────────────────────────── Прошлые сметы ─────────────────────────────

    /// <summary>Прошлый проект .aia: строки блока costEstimate (цена, поставщик, основание) и дата сметы; индексация — в пакете.</summary>
    public static List<ExtractedPriceItem> ExtractFromProject(string json, string fileName)
    {
        var items = new List<ExtractedPriceItem>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        if (!TryGet(doc.RootElement, "parameters", out var parameters) || !TryGet(parameters, "costEstimate", out var estimate)) return items;
        DateTime? saved = TryGet(estimate, "savedAt", out var s) && s.TryGetDateTime(out var d) ? d : null;
        DateTime? catalogDate = TryGet(estimate, "catalogDate", out var cd) && cd.ValueKind == JsonValueKind.String && cd.TryGetDateTime(out var d2) ? d2 : null;
        var date = catalogDate ?? saved;
        string dateText = date?.ToString("dd.MM.yyyy", Ru) ?? "без даты";
        if (TryGet(estimate, "lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            foreach (var line in lines.EnumerateArray())
            {
                string code = TryGet(line, "code", out var c) ? c.ToString() : string.Empty;
                double price = TryGet(line, "unitPriceRub", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
                if (code.Length == 0 || price <= 0) continue;
                string key = TryGet(line, "key", out var k) ? k.ToString() : string.Empty;
                string title = TryGet(line, "title", out var t) ? t.ToString() : code;
                string supplier = TryGet(line, "supplier", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() ?? string.Empty : string.Empty;
                items.Add(PastItem(MaterialLibrary.NormalizeCode(code), title, TryGet(line, "unit", out var u) ? u.ToString() : PriceCodes.UnitOf(code), price,
                    supplier.Length > 0 ? supplier : "Прошлый проект", $"{fileName} · смета от {dateText}" + (key == CostEstimateService.RebarA240 ? " (хомуты)" : string.Empty),
                    PriceSourceKind.PastProject, date));
            }
        if (items.Count == 0)
        {
            // Смета прежнего формата: только расценки проекта (бетон ₽/м³, арматура ₽/т).
            if (TryGet(estimate, "concretePricePerM3", out var cp) && cp.ValueKind == JsonValueKind.Number && cp.GetDouble() > 0)
                items.Add(PastItem("B25", "Бетон (расценка проекта)", "м³", cp.GetDouble(), "Прошлый проект", $"{fileName} · смета от {dateText}", PriceSourceKind.PastProject, date));
            if (TryGet(estimate, "rebarPricePerTon", out var rp) && rp.ValueKind == JsonValueKind.Number && rp.GetDouble() > 0)
                items.Add(PastItem("A500C", "Арматура (расценка проекта)", "т", rp.GetDouble(), "Прошлый проект", $"{fileName} · смета от {dateText}", PriceSourceKind.PastProject, date));
        }
        // Одна строка на код: рабочая арматура и хомуты A240 — разные коды; закладные и прокат C245 — первая.
        return items.GroupBy(i => i.MaterialCode).Select(g => g.First()).ToList();
    }

    /// <summary>Прошлая смета или прайс .csv / .json (формат прайса программы или чужой CSV с заголовками).</summary>
    public static List<ExtractedPriceItem> ExtractFromCatalog(MaterialPriceCatalog catalog, string fileName) =>
        catalog.Items.Where(i => i.IsValid && i.UnitPriceRub > 0).Select(i => PastItem(i.Key, i.DisplayName.Length > 0 ? i.DisplayName : i.MaterialCode,
            i.Unit.Length > 0 ? i.Unit : PriceCodes.UnitOf(i.Key), i.UnitPriceRub, i.SupplierName.Length > 0 ? i.SupplierName : catalog.Name,
            i.SourceDocument.Length > 0 ? i.SourceDocument : $"{fileName} · {catalog.UpdatedAt.ToString("dd.MM.yyyy", Ru)}", PriceSourceKind.PastEstimate, catalog.UpdatedAt)).ToList();

    /// <summary>
    /// Вставленный текст: CSV прайса (заголовок с «;»), кусок таблицы Excel/Word (ячейки через табуляцию) или текст страницы сайта.
    /// </summary>
    public static List<ExtractedPriceItem> ExtractFromText(string text, string? supplier = null)
    {
        if (Regex.IsMatch(text, @"^.*(код|наименование|code).*;.*(цена|price)", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var catalog = MaterialPriceCatalog.FromCsv(text, "Вставленная смета");
            if (catalog.Items.Count > 0) return ExtractFromCatalog(catalog, "вставленная таблица");
        }
        string source = $"вставленный текст · {DateTime.Today.ToString("dd.MM.yyyy", Ru)}";
        var items = ParseText(text, "вставленная таблица").Select(o => Normalize(o, supplier ?? "Вставленная таблица", source, PriceSourceKind.PastedText, date: DateTime.Today))
            .OfType<ExtractedPriceItem>().ToList();
        return PreferBest(items);
    }

    private static ExtractedPriceItem PastItem(string code, string title, string unit, double price, string supplier, string source, PriceSourceKind kind, DateTime? date)
    {
        var material = MatchMaterial(title) ?? MatchMaterial(code);
        string category = PriceCodes.CategoryOf(code) is { Length: > 0 } cat ? cat : material?.Category ?? string.Empty;
        return new ExtractedPriceItem
        {
            MaterialCode = code,
            Category = category,
            ProjectTitle = material?.Title is { } mt && MaterialLibrary.NormalizeCode(material.Value.Code) == code ? mt : code switch
            {
                PriceCodes.Glazing => "Остекление",
                _ => category.Length > 0 ? $"{category} {code}" : code
            },
            FoundName = title,
            Unit = unit.Length > 0 ? unit : PriceCodes.UnitOf(code),
            SourceUnit = unit,
            SourcePrice = price,
            NewPriceRub = price,
            Supplier = supplier,
            SourceDocument = source,
            SourceDate = date,
            Kind = kind,
            Confidence = PriceConfidence.Medium,
            Method = kind == PriceSourceKind.PastProject ? "смета прошлого проекта" : "прошлая смета / прайс",
            InEstimate = code is not ("BRICK" or "AERATED")
        };
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        return false;
    }

    // ───────────────────────────── Пакет и подтверждение ─────────────────────────────

    /// <summary>
    /// Пакет на подтверждение: старая цена — по текущей смете проекта (<paramref name="currentPrice"/> по коду), индексация прошлых
    /// смет (+<paramref name="indexPercent"/> %) — к ценам из проектов и файлов (не к ценам страниц магазинов на сегодня).
    /// </summary>
    public static PriceExtractionBatch BuildBatch(IEnumerable<ExtractedPriceItem> items, Func<string, double?> currentPrice, double indexPercent = 0,
        IEnumerable<string>? sources = null)
    {
        var batch = new PriceExtractionBatch { IndexPercent = indexPercent };
        if (sources is not null) batch.Sources.AddRange(sources);
        foreach (var source in items)
        {
            var item = source.Clone();
            if (indexPercent != 0 && item.Kind is PriceSourceKind.PastProject or PriceSourceKind.PastEstimate)
            {
                double indexed = Math.Round(item.NewPriceRub * (1 + indexPercent / 100), 2);
                item.Conversion = (item.Conversion.Length > 0 ? item.Conversion + "; " : string.Empty) +
                                  $"индексация {(indexPercent >= 0 ? "+" : "−")}{Math.Abs(indexPercent).ToString("0.##", Ru)} %: {Rub(item.NewPriceRub)} → {Rub(indexed)}";
                item.NewPriceRub = indexed;
            }
            item.OldPriceRub = currentPrice(item.MaterialCode);
            item.IsChecked = item.InEstimate && item.Confidence != PriceConfidence.Low;
            batch.Items.Add(item);
        }
        // Несколько источников одного материала — отмечен один: актуальная цена магазина, затем вставленная таблица, прошлая смета,
        // прошлый проект; при равенстве — выше достоверность, затем первый по порядку источников.
        foreach (var group in batch.Items.Where(i => i.IsChecked).GroupBy(i => i.MaterialCode).Where(g => g.Count() > 1))
            foreach (var extra in group.OrderBy(i => i.Kind switch { PriceSourceKind.WebPage => 0, PriceSourceKind.PastedText => 1, PriceSourceKind.PastEstimate => 2, _ => 3 })
                         .ThenBy(i => i.Confidence).Skip(1))
                extra.IsChecked = false;
        if (batch.Items.Count == 0) batch.Warnings.Add("Расценки не найдены: проверьте ссылку или вставьте текст страницы / таблицу");
        if (batch.Items.Any(i => i.Confidence == PriceConfidence.Low)) batch.Warnings.Add("Позиции с низкой достоверностью не отмечены — проверьте единицы и цены вручную");
        return batch;
    }

    /// <summary>
    /// Применение подтверждённого пакета: новая база цен (копия текущей базы + отмеченные позиции; одноимённый код заменяется) с
    /// поставщиком и документом-основанием. Вызывать только после «Да, всё верно — применить в смету».
    /// </summary>
    public static MaterialPriceCatalog ConfirmAndApply(PriceExtractionBatch batch, MaterialPriceCatalog? baseCatalog, string? name = null)
    {
        var catalog = BuildCatalog(batch, baseCatalog, name);
        batch.Confirmed = true;
        return catalog;
    }

    /// <summary>База цен из отмеченных позиций пакета (для предпросмотра «Было → Станет»; пакет не помечается подтверждённым).</summary>
    public static MaterialPriceCatalog BuildCatalog(PriceExtractionBatch batch, MaterialPriceCatalog? baseCatalog, string? name = null)
    {
        var catalog = baseCatalog?.Clone() ?? new MaterialPriceCatalog();
        catalog.Name = name ?? $"Подтянутые расценки · {DateTime.Now.ToString("dd.MM.yyyy HH:mm", Ru)}";
        catalog.UpdatedAt = DateTime.Today;
        foreach (var item in batch.Checked)
        {
            string key = PriceCodes.Normalize(item.MaterialCode);
            catalog.Items.RemoveAll(i => i.Key == key);
            catalog.Items.Add(new SupplierPriceItem
            {
                MaterialCode = key,
                Category = item.Category,
                DisplayName = item.FoundName.Length > 0 ? item.FoundName : item.ProjectTitle,
                Unit = item.Unit,
                UnitPriceRub = Math.Round(item.NewPriceRub, 2),
                SupplierName = item.Supplier,
                SourceDocument = item.SourceDocument + (item.Conversion.Length > 0 ? " · " + item.Conversion : string.Empty)
            });
        }
        return catalog.Normalized();
    }

    // ───────────────────────────── Текст ─────────────────────────────

    /// <summary>HTML → текст: блоки — переводы строк, теги и скрипты убираются, сущности раскодируются.</summary>
    public static string HtmlToText(string html)
    {
        string t = Regex.Replace(html, @"<(script|style|noscript)\b.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        t = Regex.Replace(t, @"<br\s*/?>|</(p|div|li|tr|h\d|section|article|dd|dt)>|<(p|div|li|tr|h\d)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"</t[dh]>", " \t ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<[^>]+>", " ");
        return WebUtility.HtmlDecode(t);
    }

    private static string RemoveTables(string html) => Regex.Replace(html, @"<table\b.*?</table>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static string Clean(string text) => Regex.Replace(text, @"[\s  ]+", " ").Trim(' ', '—', '–', '-', ':', '|', '·', ',');

    private static string Rub(double v) => v.ToString("N0", Ru) + " ₽";
}
