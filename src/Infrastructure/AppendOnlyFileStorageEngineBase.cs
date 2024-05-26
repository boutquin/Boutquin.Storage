namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Provides a base class for append-only file-based storage engines with asynchronous operations.
/// This class implements the common functionality required by derived classes.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para><b>Usage and Applications:</b></para>
/// <para>This class can be extended to create specialized storage engines that use an append-only file format. It provides the fundamental mechanisms for reading and writing entries to the storage file.</para>
///
/// <para><b>Typical Implementations:</b></para>
/// <para>- **Audit Logs:** Storing immutable logs that record changes over time.</para>
/// <para>- **Event Sourcing:** Capturing all changes to an application's state as a sequence of events.</para>
///
/// <para><b>Methods:</b></para>
/// <para>- <see cref="SetAsync(TKey, TValue, CancellationToken)"/>: Sets or updates the value for a specified key.</para>
/// <para>- <see cref="TryGetValueAsync(TKey, CancellationToken)"/>: Attempts to retrieve the value associated with a specified key.</para>
/// <para>- <see cref="ContainsKeyAsync(TKey, CancellationToken)"/>: Checks whether the store contains the specified key.</para>
/// <para>- <see cref="RemoveAsync(TKey, CancellationToken)"/>: Removes the value associated with the specified key. (Not supported in this implementation)</para>
/// <para>- <see cref="ClearAsync(CancellationToken)"/>: Removes all key-value pairs from the store.</para>
/// <para>- <see cref="GetAllItemsAsync(CancellationToken)"/>: Retrieves all key-value pairs from the store.</para>
/// <para>- <see cref="SetBulkAsync(IEnumerable{KeyValuePair{TKey, TValue}}, CancellationToken)"/>: Sets or updates values for multiple keys.</para>
/// </remarks>
public abstract class AppendOnlyFileStorageEngineBase<TKey, TValue> : IBulkStorageEngine<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    protected readonly string DatabaseFilePath;
    protected readonly IEntrySerializer<TKey, TValue> EntrySerializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngineBase{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="databaseFilePath">The path to the database file.</param>
    /// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="databaseFilePath"/> or <paramref name="entrySerializer"/> is null.</exception>
    protected AppendOnlyFileStorageEngineBase(
        string databaseFilePath,
        IEntrySerializer<TKey, TValue> entrySerializer)
    {
        Guard.AgainstNullOrWhiteSpace(() => databaseFilePath);
        Guard.AgainstNullOrDefault(() => entrySerializer);

        DatabaseFilePath = databaseFilePath;
        EntrySerializer = entrySerializer;
    }

    /// <inheritdoc/>
    public abstract Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes an entry to the given stream.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
    protected async Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EntrySerializer.WriteEntryAsync(stream, key, value, cancellationToken);
    }

    /// <inheritdoc/>
    public virtual async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);

        cancellationToken.ThrowIfCancellationRequested();
        var fileBytes = await File.ReadAllBytesAsync(DatabaseFilePath, cancellationToken);

        using (var stream = new MemoryStream(fileBytes))
        {
            while (EntrySerializer.CanRead(stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = EntrySerializer.ReadEntry(stream);
                if (entry.HasValue && entry.Value.Key.CompareTo(key) == 0)
                {
                    return (entry.Value.Value, true);
                }
            }
        }

        return (default, false);
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var (value, found) = await TryGetValueAsync(key, cancellationToken);
        return found;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc/>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => items);

        var allEntries = new List<byte>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);

            using var memoryStream = new MemoryStream();
            await EntrySerializer.WriteEntryAsync(memoryStream, item.Key, item.Value, cancellationToken);
            allEntries.AddRange(memoryStream.ToArray());
        }

        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(DatabaseFilePath, allEntries.ToArray(), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<(TKey Key, TValue Value)>();
        cancellationToken.ThrowIfCancellationRequested();
        var fileBytes = await File.ReadAllBytesAsync(DatabaseFilePath, cancellationToken);

        using var stream = new MemoryStream(fileBytes);
        while (EntrySerializer.CanRead(stream))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = EntrySerializer.ReadEntry(stream);
            if (entry.HasValue)
            {
                items.Add(entry.Value);
            }
        }

        return items;
    }

    /// <inheritdoc/>
    public virtual async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(DatabaseFilePath))
        {
            File.Delete(DatabaseFilePath);
        }
    }
}