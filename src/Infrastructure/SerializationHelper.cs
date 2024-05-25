namespace Boutquin.Storage.Infrastructure;

public static class SerializationHelper
{
    /// <summary>
    /// Serializes a string to a binary writer.
    /// </summary>
    /// <param name="writer">The binary writer to serialize to.</param>
    /// <param name="value">The string value to serialize.</param>
    public static void SerializeString(BinaryWriter writer, string value)
    {
        writer.Write(value);
    }

    /// <summary>
    /// Deserializes a string from a binary reader.
    /// </summary>
    /// <param name="reader">The binary reader to deserialize from.</param>
    /// <returns>The deserialized string.</returns>
    public static string DeserializeString(BinaryReader reader)
    {
        return reader.ReadString();
    }

    /// <summary>
    /// Serializes a list of serializable items to a binary writer.
    /// </summary>
    /// <typeparam name="T">The type of items in the list.</typeparam>
    /// <param name="writer">The binary writer to serialize to.</param>
    /// <param name="list">The list of items to serialize.</param>
    public static void SerializeList<T>(BinaryWriter writer, IEnumerable<T> list) where T : ISerializable<T>
    {
        writer.Write(list.Count());
        foreach (var item in list)
        {
            item.Serialize(writer);
        }
    }

    /// <summary>
    /// Deserializes a list of serializable items from a binary reader.
    /// </summary>
    /// <typeparam name="T">The type of items in the list.</typeparam>
    /// <param name="reader">The binary reader to deserialize from.</param>
    /// <param name="deserializer">The deserialization function to use for items.</param>
    /// <returns>The deserialized list of items.</returns>
    public static List<T> DeserializeList<T>(BinaryReader reader, Func<BinaryReader, T> deserializer)
    {
        var count = reader.ReadInt32();
        var list = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(deserializer(reader));
        }
        return list;
    }
}
