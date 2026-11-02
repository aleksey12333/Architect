using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace AIArchitectStudio.Models;

/// <summary>
/// Базовый контракт BIM-объекта (по образу Element Revit API): строковый Id (ElementId — ключ .aia, IFC Tag, Undo и привязок),
/// постоянный UniqueId (GUID), категория и имя.
/// </summary>
public interface IBimElement
{
    string Id { get; }

    /// <summary>Постоянный GUID объекта: UUID v5 от Id — одинаков между сохранениями, открытиями и экспортами.</summary>
    Guid UniqueId { get; }

    BimCategory Category { get; }

    string Name { get; }
}

/// <summary>
/// Детерминированные UniqueId (RFC 4122, UUID v5 — SHA-1 от пространства имён и имени). Старые файлы .aia не хранят GUID:
/// он вычисляется из Id, поэтому файлы v1–v5 получают те же UniqueId, что и при любом следующем открытии.
/// </summary>
public static class BimUniqueId
{
    /// <summary>Пространство имён элементов модели (ArchitecturalElement.Id).</summary>
    public static readonly Guid ElementNamespace = new("5d1e4a8c-7b2f-5c39-9e61-a4f0c3b2d781");

    /// <summary>Пространство имён построек участка (BuildingGroup.Id).</summary>
    public static readonly Guid BuildingNamespace = new("8f3b6c21-4d9a-5e07-b1c4-2e7a9d0f6c53");

    /// <summary>Пространство имён закреплённых размеров (PinnedDimension.Id).</summary>
    public static readonly Guid DimensionNamespace = new("c4a7e912-3f6b-5d80-a2e5-71b9c0d4e3f6");

    /// <summary>Пространство имён этажей (BimLevel.Id).</summary>
    public static readonly Guid LevelNamespace = new("2b9d4f07-6e1a-5c83-b7d2-9a4e0f6c1d38");

    /// <summary>Пространство имён типов элементов: Guid типа — UUID v5 от канонической сигнатуры (BimElementType.Signature).</summary>
    public static readonly Guid TypeNamespace = new("e7c3a95b-1d4f-5a26-8b0e-3f9c6d2a7e14");

    /// <summary>UUID v5: SHA-1(пространство имён в сетевом порядке байт ‖ UTF-8 имени), версия 5, вариант RFC 4122.</summary>
    public static Guid Create(Guid ns, string? name)
    {
        name ??= string.Empty;
        int length = 16 + Encoding.UTF8.GetByteCount(name);
        byte[]? rented = null;
        Span<byte> data = length <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(length));
        data = data[..length];
        ns.TryWriteBytes(data, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name, data[16..]);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(data, hash);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    /// <summary>Вычисленный UniqueId вместе с Id, от которого он получен (неизменяемая пара: публикуется одной записью ссылки).</summary>
    public sealed class Cache
    {
        internal Cache(string id, Guid value)
        {
            Id = id;
            Value = value;
        }

        internal string Id { get; }
        internal Guid Value { get; }
    }

    /// <summary>
    /// UniqueId для текущего Id с кэшем в поле владельца: SHA-1 считается один раз на значение Id (смена Id — новый GUID).
    /// Безопасно при чтении из фоновых потоков (экспорт, расчёт): гонка лишь повторно вычисляет то же значение.
    /// </summary>
    public static Guid Resolve(ref Cache? cache, string? id, Guid ns)
    {
        var entry = cache;
        if (entry is null || !ReferenceEquals(entry.Id, id))
        {
            id ??= string.Empty;
            if (entry is null || !string.Equals(entry.Id, id, StringComparison.Ordinal)) entry = new Cache(id, Create(ns, id));
            else entry = new Cache(id, entry.Value);
            cache = entry;
        }
        return entry.Value;
    }
}
