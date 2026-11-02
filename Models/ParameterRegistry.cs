namespace AIArchitectStudio.Models;

/// <summary>
/// Реестр определений параметров: встроенные (<see cref="BuiltInParameters"/>) и общие пользовательские по Guid — из инспектора
/// («Свойства пользователя») и из блока parameterDefinitions файлов .aia. Один на приложение (как файл общих параметров Revit):
/// одинаковый Guid в разных проектах — один параметр. Потокобезопасен: чтение возможно из фонового сохранения проекта.
/// </summary>
public sealed class ParameterRegistry
{
    public static ParameterRegistry Shared { get; } = new();

    private readonly Dictionary<Guid, ParameterDefinition> _byGuid = [];
    private readonly object _gate = new();

    /// <summary>Добавлено новое определение (обновить списки инспектора).</summary>
    public event EventHandler? DefinitionsChanged;

    public IReadOnlyList<BuiltInParameterInfo> BuiltIn => BuiltInParameters.All;

    /// <summary>Снимок общих определений, по имени.</summary>
    public IReadOnlyList<ParameterDefinition> Definitions
    {
        get
        {
            lock (_gate) return [.. _byGuid.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Guid)];
        }
    }

    public bool TryGet(Guid guid, out ParameterDefinition definition)
    {
        lock (_gate)
        {
            if (_byGuid.TryGetValue(guid, out var found))
            {
                definition = found;
                return true;
            }
        }
        definition = null!;
        return false;
    }

    public ParameterDefinition? Find(Guid guid) => TryGet(guid, out var d) ? d : null;

    /// <summary>
    /// Регистрирует определение (из файла проекта): Guid уже известен — возвращается прежнее определение (первое побеждает,
    /// значения элементов приводятся к его типу хранения); некорректное определение — null.
    /// </summary>
    public ParameterDefinition? Register(ParameterDefinition definition)
    {
        if (!definition.IsValid) return null;
        lock (_gate)
        {
            if (_byGuid.TryGetValue(definition.Guid, out var existing)) return existing;
            _byGuid[definition.Guid] = definition;
        }
        DefinitionsChanged?.Invoke(this, EventArgs.Empty);
        return definition;
    }

    /// <summary>
    /// Определение по имени и смыслу: уже есть такое (имя без учёта регистра) — оно же (общий Guid у всех элементов), иначе новое.
    /// </summary>
    public ParameterDefinition Define(string name, ParameterSpecType spec, string group = "")
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Имя параметра не задано.", nameof(name));
        ParameterDefinition created;
        lock (_gate)
        {
            var same = _byGuid.Values.FirstOrDefault(d => d.SpecType == spec && string.Equals(d.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
            if (same is not null) return same;
            created = ParameterDefinition.Create(trimmed, spec, group);
            _byGuid[created.Guid] = created;
        }
        DefinitionsChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }
}
