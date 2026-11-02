using System.Buffers.Binary;
using System.Text;

namespace AIArchitectStudio.Services.Pdf;

/// <summary>Итог сабсеттинга: файл шрифта-подмножества, число сохранённых глифов (с компонентами составных) и тег подмножества.</summary>
public sealed record TrueTypeSubset(byte[] Data, int GlyphCount, string Tag);

/// <summary>
/// Font Subsetting для PDF: из TrueType-шрифта (ISOCPEUR / Arial) остаются только глифы, реально использованные в тексте
/// чертежа (цифры, буквы марок и надписей), глиф .notdef и компоненты составных глифов (буквы с диакритикой: «Й», «Ё»).
/// Номера глифов сохраняются (CIDToGIDMap /Identity и массив /W не меняются): контуры неиспользуемых глифов удаляются из glyf,
/// loca пересобирается в длинном формате. Хинтинг (cvt, fpgm, prep), метрики (head, hhea, hmtx, maxp, OS/2) копируются,
/// cmap (формат 4), name и post (формат 3) — минимальные для подмножества; контрольные суммы таблиц и checkSumAdjustment
/// пересчитываются по спецификации OpenType. Таблицы кернинга, GSUB/GPOS, цифровая подпись и прочие в PDF не нужны.
/// </summary>
public static class TrueTypeSubsetter
{
    private static readonly string[] CopiedTables = ["OS/2", "cvt ", "fpgm", "hhea", "hmtx", "maxp", "prep"];

    /// <param name="font">Исходный файл шрифта (TrueType, контуры glyf).</param>
    /// <param name="used">Используемые глифы → кодовая точка Unicode (для cmap подмножества).</param>
    /// <param name="postScriptName">Имя шрифта для таблицы name.</param>
    public static TrueTypeSubset Subset(byte[] font, IReadOnlyDictionary<ushort, int> used, string postScriptName)
    {
        var tables = ReadDirectory(font);
        if (!tables.ContainsKey("glyf") || !tables.ContainsKey("loca") || !tables.ContainsKey("head") || !tables.ContainsKey("maxp"))
            throw new InvalidDataException("Шрифт без TrueType-контуров (glyf/loca) — подмножество не строится.");

        var head = tables["head"];
        int numGlyphs = U16(font, tables["maxp"].Offset + 4);
        bool longLoca = S16(font, head.Offset + 50) == 1;
        var loca = new long[numGlyphs + 1];
        var locaTable = tables["loca"];
        for (int g = 0; g <= numGlyphs; g++)
            loca[g] = longLoca ? U32(font, locaTable.Offset + g * 4) : U16(font, locaTable.Offset + g * 2) * 2L;

        // Замыкание набора: .notdef, использованные глифы и компоненты составных глифов.
        var keep = new SortedSet<int> { 0 };
        var queue = new Queue<int>();
        foreach (var g in used.Keys.Where(g => g < numGlyphs)) if (keep.Add(g)) queue.Enqueue(g);
        queue.Enqueue(0);
        int glyf = tables["glyf"].Offset;
        while (queue.Count > 0)
        {
            int g = queue.Dequeue();
            long start = loca[g], end = loca[g + 1];
            if (end - start < 10) continue;
            int at = glyf + (int)start;
            if (S16(font, at) >= 0) continue;
            int p = at + 10;
            while (true)
            {
                int flags = U16(font, p), component = U16(font, p + 2);
                if (component < numGlyphs && keep.Add(component)) queue.Enqueue(component);
                p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                if ((flags & 0x0008) != 0) p += 2;
                else if ((flags & 0x0040) != 0) p += 4;
                else if ((flags & 0x0080) != 0) p += 8;
                if ((flags & 0x0020) == 0) break;
            }
        }

        // Новые glyf и loca (длинный формат, глифы выровнены на 4 байта).
        using var glyfOut = new MemoryStream();
        var newLoca = new byte[(numGlyphs + 1) * 4];
        for (int g = 0; g < numGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)glyfOut.Length);
            if (!keep.Contains(g)) continue;
            long start = loca[g], length = loca[g + 1] - start;
            if (length <= 0) continue;
            glyfOut.Write(font, glyf + (int)start, (int)length);
            while (glyfOut.Length % 4 != 0) glyfOut.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(numGlyphs * 4), (uint)glyfOut.Length);

        var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in CopiedTables)
            if (tables.TryGetValue(tag, out var t)) output[tag] = font.AsSpan(t.Offset, t.Length).ToArray();
        var newHead = font.AsSpan(head.Offset, head.Length).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(newHead.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(newHead.AsSpan(50), 1);
        output["head"] = newHead;
        output["glyf"] = glyfOut.ToArray();
        output["loca"] = newLoca;
        output["cmap"] = BuildCmap(used.Where(kv => kv.Key is > 0 && kv.Key < numGlyphs && kv.Value is > 0 and < 0xFFFF).ToDictionary(kv => kv.Value, kv => kv.Key));
        output["post"] = BuildPost(font, tables);
        string tagName = SubsetTag(keep);
        output["name"] = BuildName(tagName + "+" + postScriptName);

        var data = Assemble(output);
        return new TrueTypeSubset(data, keep.Count, tagName);
    }

    /// <summary>Тег подмножества по ISO 32000 (6 прописных латинских букв), детерминированный по набору глифов.</summary>
    public static string SubsetTag(IEnumerable<int> glyphs)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (int g in glyphs) h = (h ^ (uint)g) * 16777619;
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++)
            {
                sb.Append((char)('A' + h % 26));
                h /= 26;
                if (h == 0) h = 0x9E3779B9;
            }
            return sb.ToString();
        }
    }

    // ───────────────────────────── Таблицы подмножества ─────────────────────────────

    /// <summary>cmap с одной подтаблицей (3, 1) формата 4: сегменты подряд идущих кодов с подряд идущими глифами.</summary>
    private static byte[] BuildCmap(Dictionary<int, ushort> map)
    {
        var codes = map.Keys.Order().ToList();
        var segments = new List<(int Start, int End, int Delta)>();
        foreach (int c in codes)
        {
            int delta = (map[c] - c) & 0xFFFF;
            if (segments.Count > 0 && segments[^1].End == c - 1 && segments[^1].Delta == delta)
                segments[^1] = (segments[^1].Start, c, delta);
            else segments.Add((c, c, delta));
        }
        segments.Add((0xFFFF, 0xFFFF, 1));
        int segCount = segments.Count;
        int searchRange = 2 * (1 << (int)Math.Floor(Math.Log2(segCount)));
        int entrySelector = (int)Math.Log2(searchRange / 2);
        int length = 16 + segCount * 8;

        var sub = new byte[length];
        var s = sub.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s, 4);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)(segCount * 2));
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], (ushort)(segCount * 2 - searchRange));
        int ends = 14, starts = ends + segCount * 2 + 2, deltas = starts + segCount * 2, ranges = deltas + segCount * 2;
        for (int i = 0; i < segCount; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[(ends + i * 2)..], (ushort)segments[i].End);
            BinaryPrimitives.WriteUInt16BigEndian(s[(starts + i * 2)..], (ushort)segments[i].Start);
            BinaryPrimitives.WriteUInt16BigEndian(s[(deltas + i * 2)..], (ushort)segments[i].Delta);
            BinaryPrimitives.WriteUInt16BigEndian(s[(ranges + i * 2)..], 0);
        }

        var table = new byte[12 + length];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), 12);
        sub.CopyTo(table, 12);
        return table;
    }

    /// <summary>post формата 3.0 (без имён глифов): угол наклона, подчёркивание и признак моноширинности из исходной таблицы.</summary>
    private static byte[] BuildPost(byte[] font, Dictionary<string, (int Offset, int Length)> tables)
    {
        var post = new byte[32];
        if (tables.TryGetValue("post", out var t) && t.Length >= 32) font.AsSpan(t.Offset, 32).CopyTo(post);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        return post;
    }

    /// <summary>name: семейство, начертание, полное и PostScript-имя (платформа Windows, Unicode BMP, en-US).</summary>
    private static byte[] BuildName(string psName)
    {
        var records = new (int Id, string Value)[] { (1, psName), (2, "Regular"), (4, psName), (6, psName) };
        var strings = records.Select(r => Encoding.BigEndianUnicode.GetBytes(r.Value)).ToList();
        int count = records.Length, storage = 6 + count * 12;
        var table = new byte[storage + strings.Sum(b => b.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), (ushort)storage);
        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            int rec = 6 + i * 12;
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(rec), 3);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(rec + 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(rec + 4), 0x0409);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(rec + 6), (ushort)records[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(rec + 8), (ushort)strings[i].Length);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(rec + 10), (ushort)offset);
            strings[i].CopyTo(table, storage + offset);
            offset += strings[i].Length;
        }
        return table;
    }

    /// <summary>Сборка sfnt: каталог таблиц по алфавиту тегов, выравнивание на 4 байта, контрольные суммы и checkSumAdjustment.</summary>
    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int count = tables.Count;
        int searchRange = 16 * (1 << (int)Math.Floor(Math.Log2(count)));
        int headerSize = 12 + count * 16;
        int total = headerSize + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var data = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), (ushort)Math.Log2(searchRange / 16));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10), (ushort)(count * 16 - searchRange));

        int offset = headerSize, index = 0, headOffset = -1;
        foreach (var (tag, table) in tables)
        {
            int rec = 12 + index++ * 16;
            Encoding.ASCII.GetBytes(tag).CopyTo(data, rec);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(rec + 4), Checksum(table));
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(rec + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(rec + 12), (uint)table.Length);
            if (tag == "head") headOffset = offset;
            table.CopyTo(data, offset);
            offset += (table.Length + 3) & ~3;
        }
        if (headOffset >= 0) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(headOffset + 8), unchecked(0xB1B0AFBA - Checksum(data)));
        return data;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (i + k < data.Length ? data[i + k] : 0u);
            unchecked { sum += v; }
        }
        return sum;
    }

    private static Dictionary<string, (int Offset, int Length)> ReadDirectory(byte[] data)
    {
        var tables = new Dictionary<string, (int Offset, int Length)>();
        int numTables = U16(data, 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            tables[Encoding.ASCII.GetString(data, rec, 4)] = ((int)U32(data, rec + 8), (int)U32(data, rec + 12));
        }
        return tables;
    }

    private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static short S16(byte[] d, int o) => (short)((d[o] << 8) | d[o + 1]);
    private static long U32(byte[] d, int o) => ((long)d[o] << 24) | ((long)d[o + 1] << 16) | ((long)d[o + 2] << 8) | d[o + 3];
}
