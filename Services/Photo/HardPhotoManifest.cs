using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Services.Photo;

/// <summary>Эталон сложного снимка DemoKit/HardPhotos (визуальная оценка с допуском).</summary>
public sealed class HardPhotoTruth
{
    public string File { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Morphology { get; set; } = "box";
    public bool Courtyard { get; set; }
    public int FloorsMin { get; set; } = 1;
    public int FloorsMax { get; set; } = 99;
    public int ColonnadeMin { get; set; }
    public int TiersMin { get; set; }
    public int FacadeArcsMin { get; set; }
    public bool Pylons { get; set; }
    public bool Mansard { get; set; }
    public bool Podium { get; set; }
    public string View { get; set; } = string.Empty;

    [JsonIgnore] public PhotoMorphology Expected => PhotoReading.MapMorphology(Morphology) ?? PhotoMorphology.Box;
}

/// <summary>Сверка поднятой модели с эталоном: признак → выполнен ли, с пояснением.</summary>
public sealed record HardPhotoCheck(string Name, bool Passed, string Detail);

/// <summary>
/// Манифест сложных снимков (DemoKit/HardPhotos/manifest.json) и оценка точности поднятой модели: морфология, этажность в
/// допуске, признаки формы снимка (двор и купол, колоннада, ярусы, волна фасада, стилобат, мансарда, свод с пилонами), каркас
/// (K_исп ≤ 1, ΣF = ΣR_y, аудит КЭ 5/5, 0 висящих).
/// </summary>
public static class HardPhotoManifest
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "DemoKit", "HardPhotos");

    public static List<HardPhotoTruth> Load(string? folder = null)
    {
        string path = Path.Combine(folder ?? Folder, "manifest.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("photos", out var photos) ? photos.Deserialize<List<HardPhotoTruth>>(Json) ?? [] : [];
    }

    public static HardPhotoTruth? For(string fileName, IEnumerable<HardPhotoTruth> truths) =>
        truths.FirstOrDefault(t => string.Equals(t.File, Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase));

    public static List<HardPhotoCheck> Evaluate(PhotoLiftResult lift, HardPhotoTruth truth)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var t = PhotoFidelityEnforcer.Traits(lift.Model.Elements);
        var checks = new List<HardPhotoCheck>
        {
            new("морфология", lift.Model.Morphology == truth.Expected, $"{PhotoArchitectureDescriptor.MorphologyCode(lift.Model.Morphology)} (эталон {truth.Morphology})"),
            new("этажность", lift.Model.Floors >= truth.FloorsMin && lift.Model.Floors <= truth.FloorsMax, $"{lift.Model.Floors} (эталон {truth.FloorsMin}–{truth.FloorsMax})")
        };
        if (truth.Courtyard) checks.Add(new("кольцо с двором и купол", t.HasRingSlab && t.HasDome, $"двор {(t.HasRingSlab ? "да" : "нет")}, купол {(t.HasDome ? "да" : "нет")}"));
        if (truth.ColonnadeMin > 0) checks.Add(new("колоннада", t.RoundColumns >= truth.ColonnadeMin, $"круглых колонн {t.RoundColumns} (≥ {truth.ColonnadeMin})"));
        if (truth.TiersMin > 0) checks.Add(new("ярусы", t.Tiers >= truth.TiersMin, $"ярусов {t.Tiers} (≥ {truth.TiersMin})"));
        if (truth.FacadeArcsMin > 0) checks.Add(new("волна фасада", t.AlternatingArcs >= truth.FacadeArcsMin, $"чередований дуг {t.AlternatingArcs} (≥ {truth.FacadeArcsMin})"));
        if (truth.Podium) checks.Add(new("стилобат", t.HasPodium, t.HasPodium ? "да" : "нет"));
        if (truth.Mansard) checks.Add(new("мансарда", t.HasMansard, t.HasMansard ? "да" : "нет"));
        if (truth.Pylons) checks.Add(new("свод без колонн, наклонные пилоны", t.HasVault && t.InteriorColumnsInHall == 0 && t.InclinedPylons >= 4,
            $"свод {(t.HasVault ? "да" : "нет")}, колонн в зале {t.InteriorColumnsInHall}, пилонов {t.InclinedPylons}"));
        if (truth.Expected == PhotoMorphology.Triangular) checks.Add(new("острый угол плана", t.MinPlanAngle < 80, string.Create(ru, $"{t.MinPlanAngle:0}°")));
        checks.Add(new("каркас", lift.MaxUtilization <= 1.0 + 1e-9 && lift.Hanging == 0 && (lift.Audit?.Passed ?? false),
            string.Create(ru, $"K_исп {lift.MaxUtilization:0.00}, аудит {lift.Audit?.Criteria.Count(c => c.Passed)}/{lift.Audit?.Criteria.Count}, ΣF {lift.Audit?.AppliedLoad:N0} = ΣR {lift.Audit?.GroundReaction:N0} кН, висящих {lift.Hanging}")));
        return checks;
    }
}
