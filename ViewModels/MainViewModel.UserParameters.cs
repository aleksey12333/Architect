using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AIArchitectStudio.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIArchitectStudio.ViewModels;

/// <summary>Смысл значения для списка «Тип значения» нового свойства.</summary>
public sealed record UserParameterSpecOption(ParameterSpecType Spec, string Title)
{
    public override string ToString() => Title;
}

/// <summary>
/// Строка «Свойства пользователя»: имя, единица и значение общего параметра выбранного элемента в единицах интерфейса
/// (длина — мм, сила — кН). Правка текста или флажка фиксируется в элементе одним шагом Undo.
/// </summary>
public sealed partial class UserParameterItem : ObservableObject
{
    public UserParameterItem(ParameterDefinition definition, string valueText, bool boolValue, string unit)
    {
        Definition = definition;
        _valueText = valueText;
        _boolValue = boolValue;
        Unit = unit;
    }

    public ParameterDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Unit { get; }
    public bool IsBool => Definition.StorageType == ParameterStorageType.Bool;
    public bool IsText => !IsBool;

    private string _valueText;

    /// <summary>Значение в единицах интерфейса (TextBox, фиксация по потере фокуса).</summary>
    public string ValueText
    {
        get => _valueText;
        set => SetProperty(ref _valueText, value ?? string.Empty);
    }

    private bool _boolValue;

    public bool BoolValue
    {
        get => _boolValue;
        set => SetProperty(ref _boolValue, value);
    }

    /// <summary>Возврат к значению элемента без события правки (неверный ввод).</summary>
    internal void Reset(string text)
    {
        _valueText = text;
        OnPropertyChanged(nameof(ValueText));
    }
}

/// <summary>
/// Инспектор: «Свойства пользователя» — общие параметры (Shared Parameters) по Guid определения из <see cref="ParameterRegistry"/>.
/// Свойство с тем же именем и типом у разных элементов — одно определение. Правка и добавление при мультивыделении применяются
/// ко всем выделенным элементам одним шагом Undo; значения показываются по главному элементу выделения.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _syncingUserParameters;

    public ObservableCollection<UserParameterItem> UserParameters { get; } = [];

    public ObservableCollection<UserParameterSpecOption> UserParameterSpecs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddUserParameterCommand))]
    public partial string NewUserParameterName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial UserParameterSpecOption? NewUserParameterSpec { get; set; }

    public bool HasUserParameters => UserParameters.Count > 0;

    public bool HasNoUserParameters => UserParameters.Count == 0;

    /// <summary>«Правка применяется ко всем выделенным: 4» — только при мультивыделении.</summary>
    public string UserParametersBatchText => Scene.IsMultiSelection ? Loc.Format("UserParamBatch", Scene.SelectedElements.Count) : string.Empty;

    private void InitializeUserParameters()
    {
        FillUserParameterSpecs();
        Loc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Services.LocalizationService.Language)) return;
            FillUserParameterSpecs();
            RefreshUserParameters();
        };
    }

    private void FillUserParameterSpecs()
    {
        var selected = NewUserParameterSpec?.Spec ?? ParameterSpecType.Text;
        UserParameterSpecs.Clear();
        foreach (var spec in ParameterSpecs.UserSpecs) UserParameterSpecs.Add(new UserParameterSpecOption(spec, Loc.Get("Spec" + spec)));
        NewUserParameterSpec = UserParameterSpecs.FirstOrDefault(o => o.Spec == selected) ?? UserParameterSpecs[0];
    }

    /// <summary>Строки по главному элементу выделения: определения из реестра, по имени.</summary>
    private void RefreshUserParameters()
    {
        if (_syncingUserParameters) return;
        foreach (var item in UserParameters) item.PropertyChanged -= OnUserParameterEdited;
        UserParameters.Clear();
        if (Scene.SelectedElement?.UserParameters is { } values)
        {
            var c = Loc.Culture;
            var rows = values.Select(v => (Definition: ParameterRegistry.Shared.Find(v.Key) ?? ParameterDefinition.Fallback(v.Key, v.Value), v.Value))
                .OrderBy(r => r.Definition.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var (definition, value) in rows)
            {
                var item = new UserParameterItem(definition, DisplayText(definition, value, c), value.AsBool(),
                    ParameterSpecs.DisplayUnit(definition.SpecType, !Loc.IsRussian));
                item.PropertyChanged += OnUserParameterEdited;
                UserParameters.Add(item);
            }
        }
        OnPropertyChanged(nameof(HasUserParameters));
        OnPropertyChanged(nameof(HasNoUserParameters));
        OnPropertyChanged(nameof(UserParametersBatchText));
    }

    /// <summary>Текст поля: число — в единицах интерфейса без подписи единицы (она в отдельной колонке).</summary>
    private static string DisplayText(ParameterDefinition definition, ParameterValue value, CultureInfo culture) => value.StorageType switch
    {
        ParameterStorageType.Double => ParameterSpecs.ToDisplay(definition.SpecType, value.AsDouble()).ToString("0.######", culture),
        ParameterStorageType.Int => value.AsInt().ToString(culture),
        _ => value.AsString() ?? string.Empty
    };

    private void OnUserParameterEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingUserParameters || sender is not UserParameterItem item || e.PropertyName is not (nameof(UserParameterItem.ValueText) or nameof(UserParameterItem.BoolValue))) return;
        if (Scene.SelectedElement is not { } el) return;

        var definition = item.Definition;
        if (ParseUserValue(definition, item, out var value))
        {
            var stored = value.ConvertTo(definition.StorageType);
            // Строки не пересобираются (фокус остаётся в списке): поле лишь показывает сохранённое значение в единицах интерфейса.
            CommitUserParameter(x => x.SetUserParameter(definition, value), x => x.GetUserParameter(definition.Guid) != stored, rebuild: false);
        }
        else
        {
            string unit = ParameterSpecs.DisplayUnit(definition.SpecType, !Loc.IsRussian);
            ShowNotice(Loc.Get("UserParamsTitle"), Loc.Format("UserParamInvalid", item.ValueText, definition.Name, unit.Length > 0 ? unit : Loc.Get("Spec" + definition.SpecType)),
                "AIA-PARAM-VALUE");
        }
        _syncingUserParameters = true;
        try
        {
            item.Reset(DisplayText(definition, el.GetUserParameter(definition.Guid), Loc.Culture));
        }
        finally
        {
            _syncingUserParameters = false;
        }
    }

    /// <summary>Ввод в единицах интерфейса → значение в СИ (запятая и точка — десятичный разделитель).</summary>
    private bool ParseUserValue(ParameterDefinition definition, UserParameterItem item, out ParameterValue value)
    {
        value = ParameterValue.Empty;
        string text = item.ValueText.Trim();
        switch (definition.StorageType)
        {
            case ParameterStorageType.Bool:
                value = ParameterValue.FromBool(item.BoolValue);
                return true;
            case ParameterStorageType.String:
                value = ParameterValue.FromString(text);
                return true;
            case ParameterStorageType.ElementId:
                value = ParameterValue.FromElementId(text);
                return true;
            case ParameterStorageType.Int:
                if (!TryParseNumber(text, out double i) || Math.Abs(i - Math.Round(i)) > 1e-9 || Math.Abs(i) > int.MaxValue) return false;
                value = ParameterValue.FromInt((int)Math.Round(i));
                return true;
            default:
                if (!TryParseNumber(text, out double d)) return false;
                value = ParameterValue.FromDouble(ParameterSpecs.FromDisplay(definition.SpecType, d));
                return true;
        }
    }

    private bool TryParseNumber(string text, out double value)
    {
        string normalized = text.Replace(" ", string.Empty).Replace(' '.ToString(), string.Empty).Replace(',', '.');
        if (normalized.Length == 0)
        {
            value = 0;
            return true;
        }
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private bool CanAddUserParameter() => Scene.SelectedElement is not null && !string.IsNullOrWhiteSpace(NewUserParameterName);

    /// <summary>«+ Свойство»: определение по имени и типу (уже есть — общее) и значение по умолчанию всем выделенным без него.</summary>
    [RelayCommand(CanExecute = nameof(CanAddUserParameter))]
    private void AddUserParameter()
    {
        if (!CanAddUserParameter()) return;
        var definition = ParameterRegistry.Shared.Define(NewUserParameterName, NewUserParameterSpec?.Spec ?? ParameterSpecType.Text);
        CommitUserParameter(e => e.SetUserParameter(definition, definition.DefaultValue), e => e.GetUserParameter(definition.Guid).IsEmpty);
        NewUserParameterName = string.Empty;
    }

    /// <summary>Съёмка состояния без кликов: «Марка:Text=К1;Отступ:Length=250;Несущий:YesNo=1» — выделенным элементам, каждое — шаг Undo.</summary>
    public void ApplyUserParametersText(string spec)
    {
        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = entry.IndexOf(':'), equals = entry.IndexOf('=');
            if (colon <= 0 || equals < colon || !Enum.TryParse<ParameterSpecType>(entry[(colon + 1)..equals], ignoreCase: true, out var kind)) continue;
            var definition = ParameterRegistry.Shared.Define(entry[..colon], kind);
            string text = entry[(equals + 1)..];
            var item = new UserParameterItem(definition, text, text is "1" or "true", string.Empty);
            if (ParseUserValue(definition, item, out var value))
                CommitUserParameter(x => x.SetUserParameter(definition, value), _ => true);
        }
    }

    public void RemoveUserParameter(UserParameterItem item)
    {
        var guid = item.Definition.Guid;
        CommitUserParameter(e => e.RemoveUserParameter(guid), e => !e.GetUserParameter(guid).IsEmpty);
    }

    /// <summary>Правка выделенных элементов, которым она нужна, — один шаг Undo (без лишнего шага, если менять нечего).</summary>
    private void CommitUserParameter(Action<ArchitecturalElement> apply, Func<ArchitecturalElement, bool> needed, bool rebuild = true)
    {
        var targets = Scene.SelectedElements.Where(needed).ToList();
        if (targets.Count > 0)
        {
            _syncingUserParameters = true;
            try
            {
                Scene.UpdateElements(targets, apply);
            }
            finally
            {
                _syncingUserParameters = false;
            }
        }
        if (rebuild) RefreshUserParameters();
    }
}
