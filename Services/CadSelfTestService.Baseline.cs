#if DEBUG
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Geometry;
using AIArchitectStudio.Rendering;

namespace AIArchitectStudio.Services;

/// <summary>
/// Эталон ядра — страховочная сетка рефакторинга BIM-ядра: для 4 пресетов, тестового каркаса, сцены Совета КБ и сцены
/// прямой компиляции чертежа через солвер снимается слепок — состояние каждого элемента (координаты, габариты, поворот,
/// контур, пирог, привязки) с отпечатком его 3D-геометрии, КЭ-отпечаток, смета, ведомость объёмов, текст IFC / DXF /
/// OBJ + MTL и .aia. Первый прогон пишет selftest-baseline.json, каждый следующий сравнивает с ним до бита.
/// Id элементов заменяются порядковыми (у пресетов они случайны), даты экспорта — постоянной меткой.
/// Путь: AIA_DEBUG_BASELINE, иначе SelfTest\selftest-baseline.json рядом с .csproj; AIA_DEBUG_REBASELINE=1 — перезаписать.
/// </summary>
public static partial class CadSelfTestService
{
    public const string BaselineFileName = "selftest-baseline.json";
    private const int BaselineFormat = 1;

    /// <summary>Строк (у КЭ-отпечатка — записей) в одном фрагменте: по фрагментам находится место расхождения.</summary>
    private const int BaselineChunk = 128;

    internal sealed class CoreBaseline
    {
        public int FormatVersion { get; set; } = BaselineFormat;
        public string RecordedAt { get; set; } = string.Empty;
        public List<BaselineScene> Scenes { get; set; } = [];
    }

    internal sealed class BaselineScene
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Элемент на строку: индекс|имя|тип|этаж|постройка|центр|габариты|поворот|контур|хост|повторы|материал|профиль|кровля|пирог|геометрия.</summary>
        public List<string> Elements { get; set; } = [];

        /// <summary>analysis, estimate, schedule, ifc, dxf, obj, mtl, aia.</summary>
        public Dictionary<string, BaselineText> Parts { get; set; } = [];
    }

    internal sealed class BaselineText
    {
        public string Sha256 { get; set; } = string.Empty;
        public int Length { get; set; }
        public int Units { get; set; }
        public List<string> Chunks { get; set; } = [];
    }

    private sealed record BaselineSource(string Name, List<ArchitecturalElement> Elements, List<BuildingGroup> Groups, List<StructuralGridAxis> Axes);

    private static readonly JsonSerializerOptions BaselineJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex IfcFileDate = new(@"(FILE_NAME\('(?:[^']|'')*',)'[^']*'", RegexOptions.Compiled);
    private static readonly Regex IfcOwnerTime = new(@"(IFCOWNERHISTORY\([^;]*,)\d+\)", RegexOptions.Compiled);
    private static readonly Regex ObjExportDate = new(@"^# Exported: [^\r\n]*", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex AiaSavedAt = new("\"savedAt\": \"[^\"]*\"", RegexOptions.Compiled);

    /// <summary>Штамп версии формата .aia — метаданные, как дата записи: смена версии без изменения полей не ломает эталон.</summary>
    private static readonly Regex AiaFormatVersion = new("^  \"formatVersion\": \\d+,", RegexOptions.Compiled | RegexOptions.Multiline);

    private static async Task<int> CheckCoreBaselineAsync(string directory, StringBuilder report)
    {
        int failures = 0;
        string path = BaselinePath(directory);
        string work = Path.Combine(directory, "baseline");
        Directory.CreateDirectory(work);

        CoreBaseline? stored = null;
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_REBASELINE") != "1" && File.Exists(path))
        {
            stored = JsonSerializer.Deserialize<CoreBaseline>(await File.ReadAllTextAsync(path), BaselineJson);
            if (stored?.FormatVersion != BaselineFormat)
            {
                Check(report, ref failures, false, $"эталон {path} другого формата (v{stored?.FormatVersion}, нужен v{BaselineFormat}) — перезапишите его с AIA_DEBUG_REBASELINE=1");
                return failures;
            }
        }
        report.AppendLine($"  Эталон: {path} — {(stored is null ? "записывается этим прогоном" : $"от {stored.RecordedAt}, сравнение до бита")}");

        var current = new CoreBaseline { RecordedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) };
        var roundTrip = new List<string>();
        foreach (var source in BaselineSources())
        {
            var (scene, texts, reloaded) = await SnapshotSceneAsync(source, work);
            current.Scenes.Add(scene);
            if (!reloaded.SequenceEqual(scene.Elements)) roundTrip.Add($"{scene.Name}: {FirstDifference(scene.Elements, reloaded)}");

            if (stored is null)
            {
                Check(report, ref failures, true, $"эталон «{scene.Name}» записан: {BaselineSummary(scene)}");
                continue;
            }
            var was = stored.Scenes.FirstOrDefault(s => s.Name == scene.Name);
            var problems = was is null ? ["сцены нет в эталоне"] : CompareBaseline(was, scene, texts, work);
            Check(report, ref failures, problems.Count == 0,
                $"эталон «{scene.Name}»: {BaselineSummary(scene)} — {(problems.Count == 0 ? "совпадает до бита" : "ОТЛИЧИЯ: " + string.Join("; ", problems))}");
        }
        foreach (var lost in stored?.Scenes.Where(s => current.Scenes.All(c => c.Name != s.Name)) ?? Enumerable.Empty<BaselineScene>())
            Check(report, ref failures, false, $"эталон «{lost.Name}»: сцена больше не строится");

        Check(report, ref failures, roundTrip.Count == 0,
            $"Round-Trip .aia v{SceneProjectFile.CurrentFormatVersion}: элементы {current.Scenes.Count} сцен после записи и чтения ≡ исходным (координаты, контуры, пироги, привязки, геометрия)" +
            (roundTrip.Count > 0 ? ": " + string.Join("; ", roundTrip) : string.Empty));

        string snapshot = JsonSerializer.Serialize(current, BaselineJson);
        await File.WriteAllTextAsync(Path.Combine(work, "selftest-baseline.current.json"), snapshot, new UTF8Encoding(false));
        if (stored is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, snapshot, new UTF8Encoding(false));
        }
        return failures;
    }

    private static string BaselinePath(string directory)
    {
        if (Environment.GetEnvironmentVariable("AIA_DEBUG_BASELINE") is { Length: > 0 } explicitPath) return Path.GetFullPath(explicitPath);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AIArchitectStudio.csproj"))) return Path.Combine(dir.FullName, "SelfTest", BaselineFileName);
        return Path.Combine(directory, BaselineFileName);
    }

    /// <summary>Сцены эталона: пресеты стартового окна, тестовый каркас (SCENE=frame), сцена Совета (SCENE=council), чертёж через солвер.</summary>
    private static IEnumerable<BaselineSource> BaselineSources()
    {
        foreach (var id in new[] { PresetScenesService.Cottage, PresetScenesService.Frame, PresetScenesService.Tower, PresetScenesService.Pavilion })
        {
            var preset = PresetScenesService.Create(id, LocalizationService.Russian);
            yield return Canonical("preset-" + id, [.. preset.Elements], [.. preset.Groups], [.. preset.Axes ?? []]);
        }
        var (frame, frameGroups, frameAxes) = CreateFrameScene();
        ApplyFrameUserParameters(frame);
        yield return Canonical("selftest-frame", frame, frameGroups, frameAxes);
        var (council, councilGroups, councilAxes) = CreateCouncilTestScene();
        yield return Canonical("selftest-council", council, councilGroups, councilAxes);
        if (CompileBlueprint(DirectCompileDefinition()) is { } solved)
            yield return Canonical("solver-blueprint", solved.Elements, solved.Groups, solved.GridAxes);
    }

    /// <summary>Id элементов → порядковые «el_» + 10 hex (привязки HostElementId переназначаются): слепок не зависит от случайных Id.</summary>
    private static BaselineSource Canonical(string name, List<ArchitecturalElement> elements, List<BuildingGroup> groups, List<StructuralGridAxis> axes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < elements.Count; i++) map.TryAdd(elements[i].Id, "el_" + i.ToString("x10", Inv));
        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            e.Id = "el_" + i.ToString("x10", Inv);
            if (e.HostElementId is { } host) e.HostElementId = map.GetValueOrDefault(host, host);
        }
        return new BaselineSource(name, elements, groups, axes);
    }

    private static async Task<(BaselineScene Scene, Dictionary<string, string> Texts, List<string> Reloaded)> SnapshotSceneAsync(BaselineSource source, string work)
    {
        var (elements, groups, axes) = (source.Elements, source.Groups, source.Axes);
        var scene = new BaselineScene { Name = source.Name, Elements = ElementRecords(elements, groups) };
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);

        var loads = new StructuralLoadSettings { Seismicity = 8 };
        var analysis = StructuralAnalysisService.Analyze(elements, groups, loads);
        var punching = BimAnalyticsService.PunchingReinforcedIds(analysis);
        texts["analysis"] = AnalysisFingerprint(analysis);
        texts["estimate"] = SpecificationFingerprint(BimAnalyticsService.Calculate(elements, groups, 9500, 18000, key => key, analysis.UtilizationById, 85000, loads.Rebar, punching));
        var schedule = QuantityScheduleService.Calculate(elements, analysis.UtilizationById, punching, loads.Rebar);
        texts["schedule"] = ScheduleFingerprint(schedule);
        // Ведомость по свойствам пользователя — только у сцен с такими свойствами.
        foreach (var definition in schedule.UserParameters)
            texts["schedule"] += ParameterBreakdownFingerprint(QuantityScheduleService.GroupByParameter(elements, definition, analysis.UtilizationById, punching, loads.Rebar));

        var export = new CadExportService();
        foreach (var format in new[] { CadExportFormat.Ifc, CadExportFormat.Dxf, CadExportFormat.Obj })
        {
            string file = Path.Combine(work, source.Name + CadExportService.Extension(format));
            var result = await export.ExportAsync(format, file, elements.Select(e => e.Clone()).ToList(), groups, "Эталон ядра", axes);
            foreach (var written in result.Files)
                texts[Path.GetExtension(written).TrimStart('.').ToLowerInvariant()] = NormalizeExport(await File.ReadAllTextAsync(written));
        }

        string aia = Path.Combine(work, source.Name + SceneProjectFile.Extension);
        await new SceneProjectFile
        {
            ProjectName = "Эталон ядра",
            AppVersion = "baseline",
            BuildingGroups = groups.Select(g => g.Clone()).ToList(),
            Elements = elements.Select(e => e.Clone()).ToList(),
            GridAxes = axes.Select(a => a.Clone()).ToList()
        }.SaveAsync(aia);
        texts["aia"] = AiaFormatVersion.Replace(AiaSavedAt.Replace(await File.ReadAllTextAsync(aia), "\"savedAt\": \"—\""), "  \"formatVersion\": \"—\",");
        var loaded = await SceneProjectFile.LoadAsync(aia);
        var reloaded = ElementRecords(loaded.Elements, loaded.BuildingGroups);

        foreach (var (key, text) in texts) scene.Parts[key] = Digest(text, key == "analysis" ? ';' : '\n');
        return (scene, texts, reloaded);
    }

    private static string NormalizeExport(string text)
    {
        text = IfcFileDate.Replace(text, "$1'—'");
        text = IfcOwnerTime.Replace(text, "${1}0)");
        return ObjExportDate.Replace(text, "# Exported: —");
    }

    private static string BaselineSummary(BaselineScene s)
    {
        string Kb(string key) => s.Parts.TryGetValue(key, out var p) ? $"{p.Length / 1024.0:0} КБ" : "—";
        return $"{s.Elements.Count} эл. (координаты, контуры, геометрия), КЭ {s.Parts.GetValueOrDefault("analysis")?.Units ?? 0} записей, смета, ведомость, " +
               $"IFC {Kb("ifc")}, DXF {Kb("dxf")}, OBJ {Kb("obj")}, .aia {Kb("aia")}";
    }

    private static List<string> CompareBaseline(BaselineScene was, BaselineScene now, Dictionary<string, string> texts, string work)
    {
        var problems = new List<string>();
        if (!was.Elements.SequenceEqual(now.Elements)) problems.Add("элементы: " + FirstDifference(was.Elements, now.Elements));
        foreach (var (key, digest) in now.Parts)
        {
            if (!was.Parts.TryGetValue(key, out var old)) { problems.Add($"{key}: нет в эталоне"); continue; }
            if (old.Sha256 == digest.Sha256) continue;
            int chunk = 0;
            while (chunk < old.Chunks.Count && chunk < digest.Chunks.Count && old.Chunks[chunk] == digest.Chunks[chunk]) chunk++;
            string dump = Path.Combine(work, $"{now.Name}.{key}.current.txt");
            File.WriteAllText(dump, texts[key], new UTF8Encoding(false));
            problems.Add($"{key}: {(key == "analysis" ? "записи" : "строки")} {chunk * BaselineChunk + 1}–{Math.Min(digest.Units, (chunk + 1) * BaselineChunk)} из {digest.Units} " +
                         $"(было {old.Units}, длина {old.Length} → {digest.Length}), текущий текст — {Path.GetFileName(dump)}");
        }
        foreach (var key in was.Parts.Keys.Where(k => !now.Parts.ContainsKey(k))) problems.Add($"{key}: пропал");
        return problems;
    }

    /// <summary>Первый отличающийся элемент и его первое отличающееся поле.</summary>
    private static string FirstDifference(IReadOnlyList<string> was, IReadOnlyList<string> now)
    {
        for (int i = 0; i < Math.Min(was.Count, now.Count); i++)
        {
            if (was[i] == now[i]) continue;
            string[] a = was[i].Split('|'), b = now[i].Split('|');
            int f = 0;
            while (f < a.Length && f < b.Length && a[f] == b[f]) f++;
            string Field(string[] x) => f >= x.Length ? "—" : x[f].Length > 160 ? x[f][..160] + "…" : x[f];
            return $"№{i} «{(b.Length > 1 ? b[1] : string.Empty)}»: {Field(a)} → {Field(b)}";
        }
        return $"число элементов {was.Count} → {now.Count}";
    }

    private static BaselineText Digest(string text, char separator)
    {
        var units = text.Split(separator);
        var digest = new BaselineText { Sha256 = Sha256Hex(text), Length = text.Length, Units = units.Length };
        for (int i = 0; i < units.Length; i += BaselineChunk)
            digest.Chunks.Add(Sha256Hex(string.Join(separator, units, i, Math.Min(BaselineChunk, units.Length - i)))[..16]);
        return digest;
    }

    private static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Bits(double v) => v.ToString("R", Inv);

    // ═════════════════════════════ Слепок элементов ═════════════════════════════

    private static List<string> ElementRecords(IReadOnlyList<ArchitecturalElement> elements, IReadOnlyList<BuildingGroup> groups)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < elements.Count; i++) index.TryAdd(elements[i].Id, i);
        var origins = new Dictionary<string, (double X, double Z)>(StringComparer.Ordinal);
        foreach (var g in groups) origins.TryAdd(g.Id, (g.OriginX, g.OriginZ));

        var records = new List<string>(elements.Count);
        var sb = new StringBuilder(512);
        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            sb.Clear().Append(i).Append('|').Append(e.Name).Append('|').Append(e.Type).Append("|L").Append(e.LevelIndex).Append("|g=").Append(e.BuildingGroupId);
            Vector(sb.Append("|p="), e.Position);
            Vector(sb.Append("|s="), e.Size);
            Vector(sb.Append("|r="), e.Rotation);
            sb.Append("|poly=");
            foreach (var p in e.Polygon2D) sb.Append(Bits(p.X)).Append(',').Append(Bits(p.Z)).Append(';');
            sb.Append("|host=").Append(e.HostElementId is null ? "-" : index.TryGetValue(e.HostElementId, out int h) ? h.ToString(Inv) : "?" + e.HostElementId);
            sb.Append("|rep=").Append(e.RepeatLevels).Append(',').Append(Bits(e.LevelHeight)).Append(',').Append(Bits(e.TwistAnglePerFloor)).Append(',').Append(Bits(e.TaperScalePerFloor));
            sb.Append("|m=").Append(e.Material).Append("|err=").Append(e.HasPhysicsError ? 1 : 0);
            sb.Append("|prof=").Append(e.ProfileType).Append(',').Append(Bits(e.ProfileWallThickness)).Append(',').Append(Bits(e.ProfileWebThickness));
            sb.Append("|roof=").Append(e.RoofShape).Append("|steps=").Append(e.StepCount).Append("|layers=");
            foreach (var l in e.Layers)
                sb.Append(l.Name).Append(':').Append(Bits(l.ThicknessMm)).Append(':').Append(l.Role).Append(':').Append(Bits(l.DensityKgM3)).Append(':').Append(l.ColorHex).Append(';');
            sb.Append("|geo=").Append(GeometryDigest(e, elements, origins.GetValueOrDefault(e.BuildingGroupId)));
            // Свойства пользователя — только у элементов со значениями (строки остальных элементов не меняются).
            if (e.UserParameters is { } user)
            {
                sb.Append("|user=");
                foreach (var (guid, value) in user.OrderBy(p => p.Key))
                    sb.Append(guid.ToString("N")).Append(':').Append(value.StorageType).Append(':')
                        .Append(value.StorageType == ParameterStorageType.Double ? Bits(value.AsDouble()) : value.AsString()).Append(';');
            }
            records.Add(sb.ToString());
        }
        return records;
    }

    private static void Vector(StringBuilder sb, double[] v)
    {
        for (int i = 0; i < v.Length; i++) sb.Append(i == 0 ? "" : ",").Append(Bits(v[i]));
    }

    /// <summary>Отпечаток 3D-геометрии ядра (BimMesh): мировая матрица, габарит, вершины, нормали, UV и индексы всех частей и рёбер (биты float).</summary>
    private static string GeometryDigest(ArchitecturalElement e, IReadOnlyList<ArchitecturalElement> all, (double X, double Z) origin)
    {
        var g = ElementGeometryBuilder.Build(e, SceneStateService.OpeningsFor(e, all), new GeometryContext(SceneStateService.AllLevels, origin.X, origin.Z));
        using var sink = new HashSink();
        foreach (float f in BimMath.ToArray(g.Transform)) sink.Add(f);
        sink.Add(g.LocalBounds.Minimum);
        sink.Add(g.LocalBounds.Maximum);
        foreach (var part in g.Parts)
        {
            sink.Add((int)part.Material);
            sink.AddAll(part.Mesh.Positions);
            sink.AddAll(part.Mesh.Normals);
            sink.AddAll(part.Mesh.TextureCoordinates);
            sink.AddAll(part.Mesh.TriangleIndices);
        }
        if (g.Edges is { } edges)
        {
            sink.AddAll(edges.Positions);
            sink.AddAll(edges.Indices);
        }
        return sink.Finish();
    }

    /// <summary>SHA-256 по битам чисел без промежуточных строк (геометрия башни — сотни тысяч вершин).</summary>
    private sealed class HashSink : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _buffer = new byte[1 << 14];
        private int _count;

        public void Add(int v)
        {
            if (_count + 4 > _buffer.Length) Flush();
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_count), v);
            _count += 4;
        }

        public void Add(float v) => Add(BitConverter.SingleToInt32Bits(v));

        public void Add(System.Numerics.Vector3 v)
        {
            Add(v.X);
            Add(v.Y);
            Add(v.Z);
        }

        /// <summary>Число элементов, затем значения: пустая коллекция отличается от отсутствующей.</summary>
        public void AddAll(IReadOnlyList<System.Numerics.Vector3>? values)
        {
            if (values is null) { Add(-1); return; }
            Add(values.Count);
            foreach (var v in values) Add(v);
        }

        public void AddAll(IReadOnlyList<System.Numerics.Vector2>? values)
        {
            if (values is null) { Add(-1); return; }
            Add(values.Count);
            foreach (var v in values) { Add(v.X); Add(v.Y); }
        }

        public void AddAll(IReadOnlyList<int>? values)
        {
            if (values is null) { Add(-1); return; }
            Add(values.Count);
            foreach (int v in values) Add(v);
        }

        public string Finish()
        {
            Flush();
            return Convert.ToHexString(_hash.GetHashAndReset(), 0, 8);
        }

        private void Flush()
        {
            _hash.AppendData(_buffer, 0, _count);
            _count = 0;
        }

        public void Dispose() => _hash.Dispose();
    }

    // ═════════════════════════════ Смета и ведомость ═════════════════════════════

    private static string SpecificationFingerprint(BimSpecification spec)
    {
        var sb = new StringBuilder();
        foreach (var l in spec.Lines)
            sb.Append(l.Section).Append('|').Append(l.Item).Append('|').Append(Bits(l.Quantity)).Append('|').Append(l.Unit).Append('|').Append(Bits(l.Cost))
                .Append('|').Append(l.IsHeader ? 'H' : '-').Append(l.IsTotal ? 'T' : '-').Append('\n');
        foreach (var b in spec.SiteTotal is null ? spec.Buildings : spec.Buildings.Append(spec.SiteTotal))
        {
            sb.Append(b.GroupId).Append(": эт ").Append(b.Storeys).Append(", S ").Append(Bits(b.FootprintArea)).Append('/').Append(Bits(b.FloorArea))
                .Append(", V ").Append(Bits(b.ConcreteVolume)).Append(", G ").Append(Bits(b.GlazingArea)).Append(", W ").Append(Bits(b.PoolVolume))
                .Append(", ₽ ").Append(Bits(b.ConcreteCost)).Append('/').Append(Bits(b.GlazingCost)).Append('/').Append(Bits(b.SteelCost)).Append(", сталь ");
            Steel(sb, b.Steel);
            foreach (var (type, steel) in b.SteelByType.OrderBy(p => p.Key, StringComparer.Ordinal)) Steel(sb.Append(", ").Append(type).Append(' '), steel);
            foreach (var ((name, role), (volume, area)) in b.Layers.OrderBy(p => p.Key.Name, StringComparer.Ordinal).ThenBy(p => p.Key.Role))
                sb.Append(", ").Append(name).Append('/').Append(role).Append(' ').Append(Bits(volume)).Append('/').Append(Bits(area));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string ScheduleFingerprint(QuantitySchedule s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Concrete) sb.Append(c.GroupKey).Append('|').Append(c.Count).Append('|').Append(Bits(c.Volume)).Append('\n');
        Steel(sb.Append("rebar ").Append(s.WorkingClass).Append(' '), s.Rebar);
        sb.Append('\n').Append("glazing ").Append(s.WindowCount).Append('|').Append(Bits(s.WindowArea)).Append('|').Append(s.CurtainWallCount).Append('|').Append(Bits(s.CurtainWallArea)).Append('\n');
        foreach (var r in s.RolledSteel)
            sb.Append(r.GroupKey).Append('|').Append(r.Profile).Append('|').Append(r.Count).Append('|').Append(Bits(r.Length)).Append('|').Append(Bits(r.Mass)).Append('\n');
        return sb.Append("elements ").Append(s.ElementCount).Append('\n').ToString();
    }

    private static string ParameterBreakdownFingerprint(ParameterQuantityBreakdown b)
    {
        var sb = new StringBuilder().Append("param ").Append(b.Definition.Guid.ToString("N")).Append(' ').Append(b.Definition.Name).Append('\n');
        foreach (var g in b.Groups)
            sb.Append(g.IsUnset ? "—" : g.Value.AsString()).Append('|').Append(g.Count).Append('|').Append(Bits(g.Concrete)).Append('|').Append(Bits(g.Rebar))
                .Append('|').Append(Bits(g.Glazing)).Append('|').Append(Bits(g.RolledSteel)).Append('\n');
        return sb.ToString();
    }

    private static void Steel(StringBuilder sb, SteelBreakdown s)
    {
        foreach (var (title, table) in new[] { ("A240", s.A240), ("W", s.Working), ("P", s.Plates) })
        {
            sb.Append(title).Append('{');
            foreach (var (d, kg) in table) sb.Append(d).Append(':').Append(Bits(kg)).Append(',');
            sb.Append('}');
        }
    }
}
#endif
