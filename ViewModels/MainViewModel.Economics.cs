using System.Text;
using AIArchitectStudio.Models;
using AIArchitectStudio.Models.Economics;
using AIArchitectStudio.Services;

namespace AIArchitectStudio.ViewModels;

/// <summary>
/// Блок девелопера: доказательная смета (базы цен поставщиков, ручные правки с поставщиком и основанием, costEstimate в .aia),
/// паспорт расчёта элемента по СП и правка характеристик материалов (сертификат элемента, библиотека материалов проекта,
/// климатические районы и класс ответственности).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Прайс-листы, открытые только в этой сессии (съёмка AIA_DEBUG_PRICELIST, базы из открытого проекта) — в settings.json не пишутся.</summary>
    private readonly List<MaterialPriceCatalog> _sessionCatalogs = [];

    /// <summary>Выбор базы цен сессии: null — выбор из настроек, пустая строка — расценки проекта.</summary>
    private string? _sessionActiveCatalog;

    /// <summary>Ручные правки строк сметы (цена, поставщик, основание) текущего проекта — пишутся в costEstimate .aia.</summary>
    public List<CostEstimateLine> EstimateOverrides { get; } = [];

    /// <summary>Сменилась база цен или ручная правка — окно ведомости пересчитывает смету.</summary>
    public event EventHandler? PricingChanged;

    /// <summary>Базы цен: сохранённые в настройках и открытые в сессии (без дублей по названию).</summary>
    public IReadOnlyList<MaterialPriceCatalog> PriceCatalogs
    {
        get
        {
            var list = Settings.Current.PriceCatalogs.ToList();
            list.AddRange(_sessionCatalogs.Where(c => !list.Any(x => x.Name == c.Name)));
            return list;
        }
    }

    /// <summary>Выбранная база цен поставщика; null — расценки проекта.</summary>
    public MaterialPriceCatalog? ActivePriceCatalog
    {
        get
        {
            string name = _sessionActiveCatalog ?? Settings.Current.ActivePriceCatalog;
            return name.Length == 0 ? null : PriceCatalogs.FirstOrDefault(c => c.Name == name);
        }
    }

    private CostEstimateService.ProjectRates ProjectRates
    {
        get
        {
            var s = Settings.Current;
            return new CostEstimateService.ProjectRates(s.ConcretePricePerM3, s.RebarPricePerTon, s.GlazingPricePerM2, s.StructuralLoads.Materials);
        }
    }

    /// <summary>Доказательная смета по ведомости: цена каждой позиции с поставщиком и документом-основанием.</summary>
    public PricedEstimate BuildPricedEstimate(QuantitySchedule schedule) =>
        CostEstimateService.Build(schedule, Settings.Current.StructuralLoads.Concrete, ProjectRates, ActivePriceCatalog, EstimateOverrides,
            Settings.Current.Engineering.IncludeSteelAndGlazing);

    /// <summary>Смета хранит доказательные строки: выбран поставщик, есть ручные правки, цены библиотеки материалов или полный итог.</summary>
    private bool HasEvidenceLines => ActivePriceCatalog is not null || EstimateOverrides.Count > 0 || Settings.Current.Engineering.IncludeSteelAndGlazing ||
                                     Settings.Current.StructuralLoads.Materials?.Any(m => m.UnitPriceRub is not null || m.Supplier is not null) == true;

    /// <summary>Выбор базы цен (пусто — расценки проекта): база сессии выбирается без записи настроек.</summary>
    public void SelectPriceCatalog(string? name)
    {
        name ??= string.Empty;
        bool sessionOnly = name.Length > 0 && !Settings.Current.PriceCatalogs.Any(c => c.Name == name);
        if (sessionOnly || (_sessionActiveCatalog is not null && name.Length == 0 && Settings.Current.ActivePriceCatalog.Length == 0))
            _sessionActiveCatalog = name;
        else
        {
            _sessionActiveCatalog = null;
            if (Settings.Current.ActivePriceCatalog != name) SaveSettingsSafe(s => s.ActivePriceCatalog = name);
        }
        if (Scene.Elements.Count > 0) Scene.IsDirty = true;
        PricingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Загрузка прайс-листа (CSV или JSON) и выбор его базой цен. persist = false — только на время сессии (без записи settings.json).
    /// Одноимённая база заменяется.
    /// </summary>
    public MaterialPriceCatalog ImportPriceCatalog(string path, bool persist = true)
    {
        var catalog = MaterialPriceCatalog.Load(path);
        if (catalog.Items.Count == 0) throw new InvalidDataException(Loc.Get("PriceImportEmpty"));
        if (persist)
        {
            SaveSettingsSafe(s =>
            {
                s.PriceCatalogs.RemoveAll(c => c.Name == catalog.Name);
                s.PriceCatalogs.Add(catalog);
                s.ActivePriceCatalog = catalog.Name;
            });
            _sessionCatalogs.RemoveAll(c => c.Name == catalog.Name);
            _sessionActiveCatalog = null;
        }
        else
        {
            _sessionCatalogs.RemoveAll(c => c.Name == catalog.Name);
            _sessionCatalogs.Add(catalog);
            _sessionActiveCatalog = catalog.Name;
        }
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("PriceImported", catalog.Name, catalog.Items.Count,
            catalog.UpdatedAt.ToString("dd.MM.yyyy", Loc.Culture), string.Join(", ", catalog.Items.Select(i => i.SupplierName).Where(n => n.Length > 0).Distinct().Take(3)))));
        if (Scene.Elements.Count > 0) Scene.IsDirty = true;
        PricingChanged?.Invoke(this, EventArgs.Empty);
        return catalog;
    }

    /// <summary>Шаблон прайса (CSV для Excel или JSON по расширению) с позициями ведомости и текущими ценами.</summary>
    public async Task ExportPriceTemplateAsync(string path)
    {
        var template = CostEstimateService.TemplateFor(BuildPricedEstimate(BuildQuantitySchedule()));
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            await File.WriteAllTextAsync(path, template.ToJson(), new UTF8Encoding(false));
        else
            await File.WriteAllBytesAsync(path, template.ToCsvBytes());
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("PriceTemplateSaved", path, template.Items.Count)));
    }

    /// <summary>Ручная правка строки сметы: цена, поставщик и основание (сохраняются в проект).</summary>
    public void SetEstimateLine(string key, double unitPriceRub, string? supplier, string? source = null)
    {
        if (!double.IsFinite(unitPriceRub) || unitPriceRub < 0) return;
        EstimateOverrides.RemoveAll(l => l.Key == key);
        EstimateOverrides.Add(new CostEstimateLine
        {
            Key = key,
            UnitPriceRub = unitPriceRub,
            Supplier = string.IsNullOrWhiteSpace(supplier) ? null : supplier.Trim(),
            SourceDocument = string.IsNullOrWhiteSpace(source) ? Loc.Format("PriceManualSource", DateTime.Now.ToString("dd.MM.yyyy", Loc.Culture)) : source.Trim(),
            Manual = true
        });
        if (Scene.Elements.Count > 0) Scene.IsDirty = true;
        PricingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Снять ручную правку строки (цена снова из базы).</summary>
    public void ResetEstimateLine(string key)
    {
        if (EstimateOverrides.RemoveAll(l => l.Key == key) == 0) return;
        if (Scene.Elements.Count > 0) Scene.IsDirty = true;
        PricingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Открытый проект: ручные правки и база цен сметы (если такой базы нет на компьютере — из строк файла, на сессию).</summary>
    private void RestoreEvidence(ProjectCostEstimate? estimate)
    {
        EstimateOverrides.Clear();
        if (estimate?.Lines is not { } lines) return;
        EstimateOverrides.AddRange(lines.Where(l => l.Manual && !string.IsNullOrEmpty(l.Key)).Select(l => l.Clone()));
        if (string.IsNullOrWhiteSpace(estimate.CatalogName)) return;
        if (!PriceCatalogs.Any(c => c.Name == estimate.CatalogName))
        {
            var restored = new MaterialPriceCatalog
            {
                Name = estimate.CatalogName!,
                UpdatedAt = estimate.CatalogDate ?? estimate.SavedAt.Date,
                Items = lines.Where(l => !l.Manual && !string.IsNullOrEmpty(l.Code)).Select(l => new SupplierPriceItem
                {
                    MaterialCode = l.Code!,
                    DisplayName = l.Title ?? l.Code!,
                    Unit = l.Unit ?? string.Empty,
                    UnitPriceRub = l.UnitPriceRub,
                    SupplierName = l.Supplier ?? string.Empty,
                    SourceDocument = l.SourceDocument ?? string.Empty
                }).ToList()
            }.Normalized();
            if (restored.Items.Count > 0) _sessionCatalogs.Add(restored);
        }
        _sessionActiveCatalog = estimate.CatalogName;
    }

    // ───────────────────────────── Паспорт расчёта ─────────────────────────────

    /// <summary>Подпись кнопки паспорта на вкладке «Анализ»: «Обоснование по СП · «К-1»».</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string PassportButtonText { get; set; } = string.Empty;

    /// <summary>«Обоснование по СП…» — главное окно открывает паспорт расчёта элемента (Id).</summary>
    public event EventHandler<string>? PassportRequested;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenPassport()
    {
        if (Scene.SelectedElement is { } e && Analysis.For(e.Id) is not null) PassportRequested?.Invoke(this, e.Id);
    }

    /// <summary>
    /// Паспорт расчёта выбранного (или заданного) элемента: схема строится по снимку сцены и считается заново — тот же результат,
    /// что и у фонового экспресс-расчёта; горячий цикл расчёта отчёт не формирует.
    /// </summary>
    public ElementCalculationPassport? BuildPassport(string? elementId = null)
    {
        string? id = elementId ?? Scene.SelectedElement?.Id;
        if (id is null) return null;
        var elements = Scene.Elements.Select(e => e.Clone()).ToList();
        var groups = Scene.BuildingGroups.Select(g => g.Clone()).ToList();
        return CalculationJustificationService.Build(id, elements, groups, Settings.Current.StructuralLoads.Clone()).Passport;
    }

    /// <summary>Самый нагруженный несущий элемент сцены категории (для съёмки паспорта без выделения).</summary>
    public ArchitecturalElement? MostLoaded(BimCategory? category = null)
    {
        var a = Analysis;
        return Scene.Elements.Where(e => category is null || e.Category == category)
            .Select(e => (e, r: a.For(e.Id)))
            .Where(p => p.r is not null)
            .OrderByDescending(p => p.r!.Utilization)
            .Select(p => p.e)
            .FirstOrDefault();
    }

    /// <summary>
    /// «Задать свои характеристики материала» для выбранных элементов: R, E (МПа) и ρ (кг/м³) по сертификату — свойства
    /// пользователя группы «Материал (сертификат)», одним шагом Undo. NaN — значение снимается.
    /// </summary>
    public int ApplyElementCertificate(double strengthMPa, double modulusMPa, double density)
    {
        var targets = Scene.SelectedElements.Count > 0 ? Scene.SelectedElements.ToList() : Scene.SelectedElement is { } one ? [one] : [];
        targets = targets.Where(e => !Same(e, MaterialCertificate.StrengthGuid, strengthMPa) || !Same(e, MaterialCertificate.ModulusGuid, modulusMPa) ||
                                     !Same(e, MaterialCertificate.DensityGuid, density)).ToList();
        if (targets.Count == 0) return 0;
        Scene.UpdateElements(targets, e =>
        {
            SetCertificate(e, MaterialCertificate.StrengthDefinition, strengthMPa);
            SetCertificate(e, MaterialCertificate.ModulusDefinition, modulusMPa);
            SetCertificate(e, MaterialCertificate.DensityDefinition, density);
        });
        RefreshUserParameters();
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("CertificateApplied", targets.Count)));
        return targets.Count;
    }

    private static bool SetCertificate(ArchitecturalElement e, ParameterDefinition definition, double value) =>
        double.IsFinite(value) && value > 0 ? e.SetUserParameter(definition, ParameterValue.FromDouble(value)) : e.RemoveUserParameter(definition.Guid);

    /// <summary>Значение сертификата у элемента уже такое (NaN — не задано).</summary>
    private static bool Same(ArchitecturalElement e, Guid guid, double value)
    {
        var v = e.GetUserParameter(guid);
        bool wanted = double.IsFinite(value) && value > 0;
        return wanted ? !v.IsEmpty && v.AsDouble().Equals(value) : v.IsEmpty;
    }

    /// <summary>Характеристики марки для всего проекта (библиотека материалов): R, E, ρ, цена и поставщик; null-значения — по СП.</summary>
    public void ApplyMaterialLibrary(IReadOnlyList<MaterialGrade>? grades)
    {
        var normalized = MaterialLibrary.Normalize(grades);
        SaveSettingsSafe(s => s.StructuralLoads.Materials = normalized?.Select(g => g.Clone()).ToList());
        if (Scene.Elements.Count > 0) Scene.IsDirty = true;
        PricingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>«Применить к текущему проекту»: снеговой и ветровой районы СП 20.13330 и класс ответственности ГОСТ 27751.</summary>
    public void ApplySiteNorms(double snowKgM2, double windKgM2, int consequenceClass)
    {
        UpdateLoads(l =>
        {
            l.SnowKgM2 = snowKgM2;
            l.WindKgM2 = windKgM2;
            l.ConsequenceClass = consequenceClass == 2 ? 0 : consequenceClass;
        });
        if (Scene.Elements.Count > 0) Scene.IsDirty = true;
        var c = Loc.Culture;
        ChatMessages.Add(ChatMessageModel.System(Loc.Format("NormsApplied", snowKgM2.ToString("0", c), windKgM2.ToString("0", c),
            consequenceClass is 1 or 3 ? consequenceClass : 2, Settings.Current.StructuralLoads.GammaN.ToString("0.0", c))));
    }

    // ───────────────────────────── Ссылки на СП в подсказках инспектора ─────────────────────────────

    private string? Tip(string key) => Settings.Current.Engineering.NormTooltips ? Loc.Get(key) : null;

    public string? ForceTipN => Tip("TipForceN");
    public string? ForceTipM => Tip("TipForceM");
    public string? ForceTipQ => Tip("TipForceQ");
    public string? ForceTipF => Tip("TipForceF");
    public string? ForceTipLambda => Tip("TipForceLambda");
    public string? ForceTipPunch => Tip("TipForcePunch");
    public string? ForceTipK => Tip("TipForceK");

    private void RefreshNormTips()
    {
        foreach (var name in new[] { nameof(ForceTipN), nameof(ForceTipM), nameof(ForceTipQ), nameof(ForceTipF), nameof(ForceTipLambda), nameof(ForceTipPunch), nameof(ForceTipK) })
            OnPropertyChanged(name);
    }
}
