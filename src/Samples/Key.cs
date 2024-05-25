using Boutquin.Storage.Domain.Helpers;

namespace Boutquin.Storage.Samples;

/// <summary>
/// Represents a key for the storage engine.
/// </summary>
public readonly record struct Key(long Value) : ISerializable<Key>, IComparable<Key>
{
    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Value);
    }

    public static Key Deserialize(BinaryReader reader)
    {
        return new Key(reader.ReadInt64());
    }

    public int CompareTo(Key other)
    {
        return Value.CompareTo(other.Value);
    }
}
