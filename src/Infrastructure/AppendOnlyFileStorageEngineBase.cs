namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Provides a base class for append-only file-based storage engines with asynchronous operations.
/// This class implements the common functionality required by derived classes.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
public abstract class AppendOnlyFileStorageEngineBase<TKey, TValue> : IBulkKeyValueStore<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    protected readonly string DatabaseFilePath;
    protected readonly IEntrySerializer<TKey, TValue> EntrySerializer;

    protected AppendOnlyFileStorageEngineBase(
        string databaseFilePath,
        IEntrySerializer<TKey, TValue> entrySerializer)
    {
        Guard.AgainstNullOrWhiteSpace(() => databaseFilePath);
        Guard.AgainstNullOrDefault(() => entrySerializer);

        DatabaseFilePath = databaseFilePath;
        EntrySerializer = entrySerializer;
    }

    public abstract Task SetAsync(TKey key, TValue value);

    protected async Task WriteEntryAsync(Stream stream, TKey key, TValue value)
    {
        await EntrySerializer.WriteEntryAsync(stream, key, value);
    }

    public virtual async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key)
    {
        Guard.AgainstNullOrDefault(() => key);

        var fileBytes = await File.ReadAllBytesAsync(DatabaseFilePath);

        using (var stream = new MemoryStream(fileBytes))
        {
            while (EntrySerializer.CanRead(stream))
            {
                var entry = EntrySerializer.ReadEntry(stream);
                if (entry.HasValue && entry.Value.Key.CompareTo(key) == 0)
                {
                    return (entry.Value.Value, true);
                }
            }
        }

        return (default, false);
    }

    public async Task<bool> ContainsKeyAsync(TKey key)
    {
        var (value, found) = await TryGetValueAsync(key);
        return found;
    }

    public Task RemoveAsync(TKey key)
    {
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items)
    {
        var allEntries = new List<byte>();

        foreach (var item in items)
        {
            using (var memoryStream = new MemoryStream())
            {
                await EntrySerializer.WriteEntryAsync(memoryStream, item.Key, item.Value);
                allEntries.AddRange(memoryStream.ToArray());
            }
        }

        await File.WriteAllBytesAsync(DatabaseFilePath, allEntries.ToArray());
    }

    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync()
    {
        var items = new List<(TKey Key, TValue Value)>();
        var fileBytes = await File.ReadAllBytesAsync(DatabaseFilePath);

        using (var stream = new MemoryStream(fileBytes))
        {
            while (EntrySerializer.CanRead(stream))
            {
                var entry = EntrySerializer.ReadEntry(stream);
                if (entry.HasValue)
                {
                    items.Add(entry.Value);
                }
            }

            return items;
        }
    }

    public virtual async Task Clear()
    {
        if (File.Exists(DatabaseFilePath))
        {
            File.Delete(DatabaseFilePath);
        }
    }
}
