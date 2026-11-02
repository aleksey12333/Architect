using AIArchitectStudio.Models;
using static AIArchitectStudio.Services.Verification.VerificationText;

namespace AIArchitectStudio.Services.Verification.Verifiers;

/// <summary>
/// Сейсмостойкость и регулярность каркаса (СП 14.13330): ядро или диафрагмы жёсткости при большой этажности,
/// симметрия жёсткости (эксцентриситет центра жёсткости относительно центра масс в каждом направлении ≤ 15 % размера
/// плана) и перегрузка узлов «колонна — плита» по продавливанию (K_исп узла &gt; 1.0).
/// </summary>
public sealed class SeismicAndStabilityVerifier : IBimDesignVerifier
{
    public string Id => "SEIS";
    public string Title => "Сейсмостойкость и регулярность каркаса";
    public int Order => 50;

    public const double MaxEccentricity = 0.15;

    public IEnumerable<VerificationFinding> Verify(VerificationContext ctx)
    {
        foreach (var group in ctx.Elements.GroupBy(e => e.BuildingGroupId))
        {
            var items = group.ToList();
            if (CoreFinding(ctx, group.Key, items) is { } core) yield return core;
            foreach (var f in EccentricityFindings(ctx, group.Key, items)) yield return f;
        }
        foreach (var node in ctx.Analysis.PunchingNodes.Where(n => n.Ratio > 1.0).OrderByDescending(n => n.Ratio))
            if (NodeFinding(ctx, node) is { } f) yield return f;
    }

    // ───────────────────────────── Ядро жёсткости ─────────────────────────────

    private static (int Storeys, double Height) Size(List<ArchitecturalElement> items) =>
        (items.Max(e => e.LevelIndex + Math.Max(1, e.RepeatLevels)), items.Max(StructuralTopology.Top));

    /// <summary>Центр масс постройки — центр площадей плит (иначе — центр габарита).</summary>
    public static (double X, double Z, double W, double D) PlanCenter(IReadOnlyList<ArchitecturalElement> items)
    {
        var slabs = items.Where(e => StructuralTopology.IsSpanningSlab(e) || e.Type == ElementTypes.Foundation).ToList();
        var source = slabs.Count > 0 ? slabs : items;
        var (minX, maxX, minZ, maxZ) = SceneStateService.Bounds(source);
        double ax = 0, az = 0, area = 0;
        foreach (var s in slabs)
        {
            var o = StructuralTopology.Outline(s);
            double a = Geometry2D.Area(o);
            var (cx, cz) = Geometry2D.Centroid(o);
            ax += cx * a; az += cz * a; area += a;
        }
        return area > 1e-6 ? (ax / area, az / area, maxX - minX, maxZ - minZ) : ((minX + maxX) / 2, (minZ + maxZ) / 2, maxX - minX, maxZ - minZ);
    }

    private static VerificationFinding? CoreFinding(VerificationContext ctx, string groupId, List<ArchitecturalElement> items)
    {
        var (storeys, height) = Size(items);
        if (storeys < 9 && height <= 28) return null;
        if (items.Any(e => e.Type == ElementTypes.Core)) return null;
        // Диафрагмы нижнего этажа: не меньше двух ж/б стен длиной от 3 м в каждом направлении.
        double ground = items.Where(StructuralTopology.IsVerticalBearing).Select(e => e.BaseY).DefaultIfEmpty(0).Min();
        var shear = items.Where(e => e.Type == ElementTypes.Wall && e.BaseY <= ground + 0.5 && e.Width >= 3 &&
                                     e.Material.Contains("concrete", StringComparison.OrdinalIgnoreCase)).ToList();
        int alongX = shear.Count(e => Math.Abs(Math.Cos(e.RotationY * Math.PI / 180)) > 0.7);
        if (alongX >= 2 && shear.Count - alongX >= 2) return null;
        var (x, z, w, d) = PlanCenter(items);
        double cw = Math.Clamp(Math.Round(w * 0.15 / 0.1) * 0.1, 4, 10), cd = Math.Clamp(Math.Round(d * 0.15 / 0.1) * 0.1, 4, 10);
        bool critical = ctx.Loads.Seismicity >= 7 || storeys >= 16;
        var f = new VerificationFinding
        {
            Code = "SEIS-CORE",
            Severity = critical ? VerificationSeverity.Critical : VerificationSeverity.Warning,
            Category = "Сейсмика · жёсткость",
            Title = $"«{groupId}»: {storeys} эт. ({M(height)} м) без ядра и диафрагм жёсткости",
            DetailedExplanation = $"При этажности от 9 этажей (выше 28 м) рамный каркас без ядра жёсткости даёт большие перекосы этажей " +
                                  $"от ветра{(ctx.Loads.Seismicity >= 7 ? $" и сейсмики {ctx.Loads.Seismicity} баллов" : string.Empty)}. Нужно ядро (лестнично-лифтовой узел) или диафрагмы.",
            AffectedElementIds = items.Where(e => e.Type == ElementTypes.Column && e.LevelIndex == 0).Select(e => e.Id).Take(24).ToList(),
            Location = [x, height / 2, z],
            Extent = Math.Max(w, d),
            ProposedFixOptions =
            [
                new ProposedFixOption
                {
                    Author = FixAuthor.Structural,
                    Title = $"Устроить ж/б ядро жёсткости {M(cw)} × {M(cd)} м в центре плана",
                    Description = "Ядро на всю высоту от фундамента: воспринимает горизонтальные нагрузки, совмещается с лестнично-лифтовым узлом",
                    Actions = [new FixAction(FixActionKind.AddCore, string.Empty) { GroupId = groupId, X = x, Z = z, Value = cw, Value2 = cd }],
                    Priority = 1
                },
                new ProposedFixOption
                {
                    Author = FixAuthor.Architect,
                    Title = "Диафрагмы жёсткости в торцах здания (по 2 в каждом направлении)",
                    Description = "Центр плана остаётся свободным: стены-диафрагмы 200 мм у торцевых фасадов на всех этажах",
                    Actions =
                    [
                        new FixAction(FixActionKind.AddDiaphragm, string.Empty) { GroupId = groupId, X = x - w / 2 + 0.6, Z = z, Value = Math.Max(3, d * 0.35), Value2 = 90 },
                        new FixAction(FixActionKind.AddDiaphragm, string.Empty) { GroupId = groupId, X = x + w / 2 - 0.6, Z = z, Value = Math.Max(3, d * 0.35), Value2 = 90 },
                        new FixAction(FixActionKind.AddDiaphragm, string.Empty) { GroupId = groupId, X = x, Z = z - d / 2 + 0.6, Value = Math.Max(3, w * 0.25), Value2 = 0 },
                        new FixAction(FixActionKind.AddDiaphragm, string.Empty) { GroupId = groupId, X = x, Z = z + d / 2 - 0.6, Value = Math.Max(3, w * 0.25), Value2 = 0 }
                    ],
                    Priority = 2,
                    PreservesArchitecture = true
                }
            ]
        };
        return f;
    }

    // ───────────────────────────── Симметрия жёсткости ─────────────────────────────

    /// <summary>
    /// Центр жёсткости нижнего этажа для горизонтальной силы вдоль X (координата Z) и вдоль Z (координата X): веса —
    /// моменты инерции сечений вертикальных конструкций с учётом поворота, I' = Ix·cos²θ + Iz·sin²θ.
    /// </summary>
    public static (double Xr, double Zr, double Kx, double Kz) RigidityCenter(IEnumerable<ArchitecturalElement> verticals)
    {
        double sx = 0, sxz = 0, sz = 0, szx = 0;
        foreach (var v in verticals)
        {
            double w = v.Width, d = v.Depth, ix, iz;
            if (v.EffectiveProfile is ProfileTypes.Circle or ProfileTypes.Pipe) ix = iz = Math.PI * Math.Pow(w, 4) / 64;
            else { ix = d * w * w * w / 12; iz = w * d * d * d / 12; }
            double t = v.RotationY * Math.PI / 180, c2 = Math.Cos(t) * Math.Cos(t), s2 = Math.Sin(t) * Math.Sin(t);
            double kx = ix * c2 + iz * s2, kz = ix * s2 + iz * c2;
            sx += kx; sxz += kx * v.Z;
            sz += kz; szx += kz * v.X;
        }
        return (sz > 0 ? szx / sz : 0, sx > 0 ? sxz / sx : 0, sx, sz);
    }

    /// <summary>Эксцентриситеты центра жёсткости нижнего этажа относительно центра масс (доли размера плана): (eX, eZ).</summary>
    public static (double Ex, double Ez, double Xr, double Zr) Eccentricity(IReadOnlyList<ArchitecturalElement> items)
    {
        double ground = items.Where(StructuralTopology.IsVerticalBearing).Select(e => e.BaseY).DefaultIfEmpty(0).Min();
        var verticals = items.Where(e => StructuralTopology.IsVerticalBearing(e) && e.BaseY <= ground + 0.5).ToList();
        if (verticals.Count < 4) return (0, 0, 0, 0);
        var (xr, zr, _, _) = RigidityCenter(verticals);
        var (x, z, w, d) = PlanCenter(items);
        return (w > 1 ? Math.Abs(xr - x) / w : 0, d > 1 ? Math.Abs(zr - z) / d : 0, xr, zr);
    }

    private static IEnumerable<VerificationFinding> EccentricityFindings(VerificationContext ctx, string groupId, List<ArchitecturalElement> items)
    {
        var (storeys, _) = Size(items);
        if (storeys < 3 && ctx.Loads.Seismicity < 7) yield break;
        var (ex, ez, xr, zr) = Eccentricity(items);
        var (x, z, w, d) = PlanCenter(items);
        foreach (var (e, alongX) in new[] { (ez, true), (ex, false) })
        {
            if (e <= MaxEccentricity) continue;
            // Диафрагма у противоположной от центра жёсткости грани, ориентированная вдоль направления силы.
            double rx = alongX ? x : (xr > x ? x - w / 2 + 0.6 : x + w / 2 - 0.6);
            double rz = alongX ? (zr > z ? z - d / 2 + 0.6 : z + d / 2 - 0.6) : z;
            double best = 0;
            for (double len = 3; len <= (alongX ? w : d) * 0.7 + 1e-9; len += 1.5)
            {
                var sandbox = DesignFixEngine.Sandbox(ctx);
                DesignFixEngine.Apply(sandbox, [new FixAction(FixActionKind.AddDiaphragm, string.Empty) { GroupId = groupId, X = rx, Z = rz, Value = len, Value2 = alongX ? 0 : 90 }]);
                var (nex, nez, _, _) = Eccentricity(sandbox.Elements.Where(i => i.BuildingGroupId == groupId).ToList());
                best = len;
                if ((alongX ? nez : nex) <= MaxEccentricity * 0.6) break;
            }
            bool critical = ctx.Loads.Seismicity >= 8 || (ctx.Loads.Seismicity >= 7 && e > 0.25);
            yield return new VerificationFinding
            {
                Code = alongX ? "SEIS-ECC-X" : "SEIS-ECC-Z",
                Severity = critical ? VerificationSeverity.Critical : VerificationSeverity.Warning,
                Category = "Сейсмика · регулярность",
                Title = $"«{groupId}»: центр жёсткости смещён на {e * 100:0} % размера плана (сила вдоль {(alongX ? "X" : "Z")})",
                DetailedExplanation = $"Несимметричная жёсткость нижнего этажа вызывает кручение здания при ветре и сейсмике (СП 14.13330, п. 5.2.2). " +
                                      $"Эксцентриситет {e * 100:0} % > {MaxEccentricity * 100:0} % — нужна диафрагма у противоположной грани.",
                AffectedElementIds = items.Where(i => StructuralTopology.IsVerticalBearing(i) && i.LevelIndex == 0).Select(i => i.Id).Take(16).ToList(),
                Location = [rx, 1.5, rz],
                Extent = Math.Max(w, d),
                ProposedFixOptions =
                [
                    new ProposedFixOption
                    {
                        Author = FixAuthor.Structural,
                        Title = $"Диафрагма жёсткости ж/б 200 мм длиной {M(best)} м у противоположной грани",
                        Description = "Стена-диафрагма на всех этажах выравнивает центр жёсткости с центром масс",
                        Actions = [new FixAction(FixActionKind.AddDiaphragm, string.Empty) { GroupId = groupId, X = rx, Z = rz, Value = best, Value2 = alongX ? 0 : 90 }],
                        Priority = 1
                    }
                ]
            };
        }
    }

    // ───────────────────────────── Узлы продавливания ─────────────────────────────

    private static VerificationFinding? NodeFinding(VerificationContext ctx, PunchingNode node)
    {
        if (ctx.Find(node.SlabId) is not { } slab || ctx.Find(node.ColumnId) is not { } column) return null;
        var f = new VerificationFinding
        {
            Code = "SEIS-NODE",
            Severity = VerificationSeverity.Critical,
            Category = "КЖ · узлы",
            Title = $"Перегрузка узла «{column.DisplayTitle}» — «{slab.DisplayTitle}»: K_исп = {K(node.Ratio)} (продавливание)",
            DetailedExplanation = $"F = {node.Force.ToString("N0", Ru)} кН > F_b,ult = {node.Capacity.ToString("N0", Ru)} кН (u = {M(node.Perimeter)} м, h0 = {Mm(node.H0)} мм). {node.Recommendation}.",
            AffectedElementIds = [column.Id, slab.Id],
            Location = node.Location,
            Extent = 4,
            Utilization = node.Ratio
        };
        double NodeRatio(SceneStateService sandbox, string colId) =>
            DesignFixEngine.Analyze(sandbox, ctx.Loads).PunchingNodes.Where(n => n.ColumnId == colId && n.SlabId == slab.Id).Select(n => n.Ratio).DefaultIfEmpty(0).Max();

        for (double t = Math.Round(slab.Height + 0.02, 3); t <= slab.Height + 0.4 + 1e-9; t = Math.Round(t + 0.02, 3))
        {
            var actions = new List<FixAction> { new(FixActionKind.ThickenSlab, slab.Id) { Value = t } };
            var sandbox = DesignFixEngine.Sandbox(ctx);
            DesignFixEngine.Apply(sandbox, actions);
            double r = NodeRatio(sandbox, column.Id);
            if (r > DesignFixEngine.TargetUtilization) continue;
            f.ProposedFixOptions.Add(new ProposedFixOption
            {
                Author = FixAuthor.Structural,
                Title = $"Утолщить {(slab.Type == ElementTypes.Foundation ? "фундаментную плиту" : "плиту")} до {Mm(t)} мм в узле продавливания",
                Description = $"Растёт рабочая высота h0 и контур u: K_исп узла {K(node.Ratio)} → {K(r)}",
                Actions = actions,
                PredictedUtilization = r,
                Priority = 1,
                PreservesArchitecture = true
            });
            break;
        }
        if (column.Type == ElementTypes.Column)
            for (double s = Math.Round(Math.Max(column.Width, column.Depth) + 0.1, 3); s <= 1.4 + 1e-9; s = Math.Round(s + 0.1, 3))
            {
                var actions = new List<FixAction> { new(FixActionKind.EnlargeColumn, column.Id) { Value = s, Value2 = s } };
                var sandbox = DesignFixEngine.Sandbox(ctx);
                DesignFixEngine.Apply(sandbox, actions);
                double r = NodeRatio(sandbox, column.Id);
                if (r > DesignFixEngine.TargetUtilization) continue;
                f.ProposedFixOptions.Add(new ProposedFixOption
                {
                    Author = FixAuthor.Architect,
                    Title = $"Увеличить сечение колонны до {Mm(s)} × {Mm(s)} мм (вместо капители)",
                    Description = $"Перекрытие сохраняет толщину; контур продавливания длиннее: K_исп узла → {K(r)}",
                    Actions = actions,
                    PredictedUtilization = r,
                    Priority = 2
                });
                break;
            }
        return f;
    }
}
