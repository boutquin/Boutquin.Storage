namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Extends the <see cref="IKeyValueStore{K, V}"/> interface with additional methods
/// for bulk operations, such as clearing the store and retrieving all items.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para><b>Methods:</b></para>
/// <para>- <see cref="Clear"/>: Removes all key-value pairs from the store.</para>
/// <para>- <see cref="GetAllItems"/>: Retrieves all key-value pairs from the store.</para>
/// </remarks>
public interface IBulkKeyValueStore<TKey, TValue> : IKeyValueStore<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <summary>
    /// Removes all key-value pairs from the store.
    /// </summary>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    Task Clear();

    /// <summary>
    /// Retrieves all key-value pairs from the store.
    /// </summary>
    /// <returns>A task representing the asynchronous operation. 
    /// The task result contains an enumerable collection of all key-value pairs in the store.
    /// </returns>
    Task<IEnumerable<KeyValuePair<TKey, TValue>>> GetAllItems();
}