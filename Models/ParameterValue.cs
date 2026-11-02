using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArchitectStudio.Models;

/// <summary>
/// Типизированное значение параметра без упаковки (boxing): число, целое, строка/ElementId или логическое — в своём поле.
/// В .aia пишется «как есть» (число, строка, true/false); тип хранения при чтении уточняет определение параметра.
/// </summary>
[JsonConverter(typeof(ParameterValueJsonConverter))]
public readonly struct ParameterValue : IEquatable<ParameterValue>
{
    private readonly double _number;
    private readonly long _integer;
    private readonly string? _text;

    private ParameterValue(ParameterStorageType storage, double number = 0, long integer = 0, string? text = null)
    {
        StorageType = storage;
        _number = number;
        _integer = integer;
        _text = text;
    }

    public ParameterStorageType StorageType { get; }

    public bool IsEmpty => StorageType == ParameterStorageType.None;

    public static ParameterValue Empty => default;

    public static ParameterValue FromDouble(double value) => new(ParameterStorageType.Double, number: value);

    public static ParameterValue FromInt(int value) => new(ParameterStorageType.Int, integer: value);

    public static ParameterValue FromString(string? value) => new(ParameterStorageType.String, text: value ?? string.Empty);

    /// <summary>Ссылка на элемент; пустая строка — «не задано» (пишется в .aia как "", чтобы значение пережило Round-Trip).</summary>
    public static ParameterValue FromElementId(string? id) => new(ParameterStorageType.ElementId, text: id?.Trim() ?? string.Empty);

    public static ParameterValue FromBool(bool value) => new(ParameterStorageType.Bool, integer: value ? 1 : 0);

    public double AsDouble() => StorageType switch
    {
        ParameterStorageType.Double => _number,
        ParameterStorageType.Int or ParameterStorageType.Bool => _integer,
        _ => 0
    };

    public int AsInt() => StorageType switch
    {
        ParameterStorageType.Int or ParameterStorageType.Bool => (int)_integer,
        ParameterStorageType.Double => double.IsFinite(_number) ? (int)Math.Clamp(Math.Round(_number), int.MinValue, int.MaxValue) : 0,
        _ => 0
    };

    public bool AsBool() => StorageType switch
    {
        ParameterStorageType.Bool or ParameterStorageType.Int => _integer != 0,
        ParameterStorageType.Double => _number != 0,
        _ => false
    };

    public string? AsString() => StorageType switch
    {
        ParameterStorageType.String or ParameterStorageType.ElementId => _text,
        ParameterStorageType.Double => _number.ToString("R", CultureInfo.InvariantCulture),
        ParameterStorageType.Int => _integer.ToString(CultureInfo.InvariantCulture),
        ParameterStorageType.Bool => _integer != 0 ? "true" : "false",
        _ => null
    };

    /// <summary>
    /// Приведение к типу хранения определения (чтение .aia, правка в инспекторе): число ↔ целое ↔ логическое, строка → число
    /// по инвариантной культуре. Непредставимое значение (текст «abc» в число, нечисловой double) — <see cref="Empty"/>.
    /// </summary>
    public ParameterValue ConvertTo(ParameterStorageType target)
    {
        if (StorageType == target) return target == ParameterStorageType.Double && !double.IsFinite(_number) ? Empty : this;
        switch (target)
        {
            case ParameterStorageType.Double:
                if (StorageType is ParameterStorageType.Int or ParameterStorageType.Bool) return FromDouble(_integer);
                return TryParse(_text, out double d) ? FromDouble(d) : Empty;
            case ParameterStorageType.Int:
                if (StorageType == ParameterStorageType.Double) return double.IsFinite(_number) ? FromInt(AsInt()) : Empty;
                if (StorageType == ParameterStorageType.Bool) return FromInt((int)_integer);
                return TryParse(_text, out double i) ? FromInt((int)Math.Clamp(Math.Round(i), int.MinValue, int.MaxValue)) : Empty;
            case ParameterStorageType.Bool:
                if (StorageType is ParameterStorageType.Double or ParameterStorageType.Int) return FromBool(AsBool());
                return _text?.Trim().ToLowerInvariant() switch
                {
                    "true" or "1" or "да" or "yes" => FromBool(true),
                    "false" or "0" or "нет" or "no" or "" => FromBool(false),
                    _ => Empty
                };
            case ParameterStorageType.String:
                return StorageType == ParameterStorageType.None ? Empty : FromString(AsString());
            case ParameterStorageType.ElementId:
                return StorageType is ParameterStorageType.String ? FromElementId(_text) : Empty;
            default:
                return Empty;
        }
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public bool Equals(ParameterValue other) =>
        StorageType == other.StorageType && _number.Equals(other._number) && _integer == other._integer && string.Equals(_text, other._text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ParameterValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(StorageType, _number, _integer, _text);

    public static bool operator ==(ParameterValue a, ParameterValue b) => a.Equals(b);

    public static bool operator !=(ParameterValue a, ParameterValue b) => !a.Equals(b);

    public override string ToString() => AsString() ?? string.Empty;
}

/// <summary>JSON значения параметра: число, строка, true/false или null; при чтении тип хранения — по токену (уточняет определение).</summary>
public sealed class ParameterValueJsonConverter : JsonConverter<ParameterValue>
{
    public override bool HandleNull => true;

    public override ParameterValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => ParameterValue.FromDouble(reader.GetDouble()),
        JsonTokenType.String => ParameterValue.FromString(reader.GetString()),
        JsonTokenType.True => ParameterValue.FromBool(true),
        JsonTokenType.False => ParameterValue.FromBool(false),
        JsonTokenType.Null => ParameterValue.Empty,
        _ => throw new JsonException($"Значение параметра: неожиданный токен {reader.TokenType}.")
    };

    public override void Write(Utf8JsonWriter writer, ParameterValue value, JsonSerializerOptions options)
    {
        switch (value.StorageType)
        {
            case ParameterStorageType.Double:
                double d = value.AsDouble();
                if (double.IsFinite(d)) writer.WriteNumberValue(d);
                else writer.WriteNullValue();
                break;
            case ParameterStorageType.Int:
                writer.WriteNumberValue(value.AsInt());
                break;
            case ParameterStorageType.Bool:
                writer.WriteBooleanValue(value.AsBool());
                break;
            case ParameterStorageType.String:
            case ParameterStorageType.ElementId:
                if (value.AsString() is { } s) writer.WriteStringValue(s);
                else writer.WriteNullValue();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
