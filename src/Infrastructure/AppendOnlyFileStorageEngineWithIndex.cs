// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Provides an append-only file-based storage engine with asynchronous operations,
/// using an index to speed up reads by storing the offset in the file.
/// This class implements the <see cref="IKeyValueStore{TKey, TValue}"/> interface.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>This class is designed to provide efficient read and write operations for key-value pairs stored in a file.
/// It uses an in-memory index to map keys to their locations within the file, reducing the amount of file I/O required to retrieve values.</para>
///
/// <para>Example usage:</para>
/// <code>
/// public readonly record struct Key(long Value) : IComparable&lt;Key&gt;
/// {
///     public int CompareTo(Key other)
///     {
///         return Value.CompareTo(other.Value);
///     }
/// }
///
/// public readonly record struct City(string Name, IEnumerable&lt;Attraction&gt; Attractions);
/// public readonly record struct Attraction(string Name);
///
/// var index = new InMemoryFileIndex&lt;Key&gt;();
/// var store = new AppendOnlyFileStorageEngineWithIndex&lt;Key, City&gt;(
///     "database",
///     index,
///     key => key.Value.ToString(),
///     str => new Key(long.Parse(str)),
///     value => JsonSerializer.Serialize(value),
///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
///
/// // Set values in the store
/// await store.SetAsync(new Key(123456), new City("London", new List&lt;Attraction&gt; { new Attraction("Big Ben"), new Attraction("London Eye") }));
/// await store.SetAsync(new Key(42), new City("San Francisco", new List&lt;Attraction&gt; { new Attraction("Golden Gate Bridge") }));
///
/// // Retrieve a value from the store
/// var value = await store.TryGetValueAsync(new Key(42));
/// if (value.Found)
/// {
///     Console.WriteLine(JsonSerializer.Serialize(value.Value));
///     // Output: {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
/// }
///
/// // Update a value in the store
/// await store.SetAsync(new Key(42), new City("San Francisco", new List&lt;Attraction&gt; { new Attraction("Exploratorium") }));
///
/// // Retrieve the updated value
/// value = await store.TryGetValueAsync(new Key(42));
/// if (value.Found)
/// {
///     Console.WriteLine(JsonSerializer.Serialize(value.Value));
///     // Output: {"name":"San Francisco","attractions":["Exploratorium"]}
/// }
/// </code>
///
/// <para>This class ensures that read operations are fast and efficient, leveraging the in-memory index to quickly locate the data in the file.</para>
/// </remarks>
public class AppendOnlyFileStorageEngineWithIndex<TKey, TValue> : 
    IKeyValueStore<TKey, TValue> where TKey : IComparable<TKey>
{
    private readonly string _databaseFilePath;
    private readonly IFileStorageIndex<TKey> _index;
    private readonly Func<TKey, string> _keySerializer;
    private readonly Func<string, TKey> _keyDeserializer;
    private readonly Func<TValue, string> _valueSerializer;
    private readonly Func<string, TValue> _valueDeserializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="databaseFilePath">The path to the database file.</param>
    /// <param name="index">The index to speed up reads by storing the offset in the file.</param>
    /// <param name="keySerializer">A function to serialize keys to strings.</param>
    /// <param name="keyDeserializer">A function to deserialize keys from strings.</param>
    /// <param name="valueSerializer">A function to serialize values to strings.</param>
    /// <param name="valueDeserializer">A function to deserialize values from strings.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="databaseFilePath"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when any of the serializer or deserializer functions are null.</exception>
    /// <remarks>
    /// This constructor initializes the storage engine with the specified parameters and ensures that the file path and functions are valid.
    /// </remarks>
    public AppendOnlyFileStorageEngineWithIndex(
        string databaseFilePath,
        IFileStorageIndex<TKey> index,
        Func<TKey, string> keySerializer,
        Func<string, TKey> keyDeserializer,
        Func<TValue, string> valueSerializer,
        Func<string, TValue> valueDeserializer)
    {
        // Validate the file path to ensure it is not null, empty, or whitespace.
        Guard.AgainstNullOrWhiteSpace(() => databaseFilePath);

        // Validate the serializers and deserializers to ensure they are not null.
        Guard.AgainstNullOrDefault(() => keySerializer);
        Guard.AgainstNullOrDefault(() => keyDeserializer);
        Guard.AgainstNullOrDefault(() => valueSerializer);
        Guard.AgainstNullOrDefault(() => valueDeserializer);

        _databaseFilePath = databaseFilePath;
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _keySerializer = keySerializer;
        _keyDeserializer = keyDeserializer;
        _valueSerializer = valueSerializer;
        _valueDeserializer = valueDeserializer;
    }

    /// <summary>
    /// Sets or updates the value for the specified key.
    /// If the key already exists in the store, the value is updated.
    /// If the key does not exist, a new key-value pair is added.
    /// </summary>
    /// <param name="key">The key to set or update.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the key or value is null or default.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <remarks>
    /// This method appends the serialized key-value pair to the file and updates the index with the new file location.
    /// </remarks>
    /// <example>
    /// <code>
    /// var index = new InMemoryFileIndex&lt;Key&gt;();
    /// var store = new AppendOnlyFileStorageEngineWithIndex&lt;Key, City&gt;(
    ///     "database",
    ///     index,
    ///     key => key.Value.ToString(),
    ///     str => new Key(long.Parse(str)),
    ///     value => JsonSerializer.Serialize(value),
    ///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
    ///
    /// await store.SetAsync(new Key(123456), new City("London", new List&lt;Attraction&gt; { new Attraction("Big Ben"), new Attraction("London Eye") }));
    /// </code>
    /// </example>
    public async Task SetAsync(TKey key, TValue value)
    {
        // Validate the key and value to ensure they are not null or default.
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);

        var serializedKey = _keySerializer(key);
        var serializedValue = _valueSerializer(value);
        var entry = $"{serializedKey},{serializedValue}{Environment.NewLine}";

        // Append the serialized key-value pair to the file.
        byte[] entryBytes = Encoding.UTF8.GetBytes(entry);
        using (var stream = new FileStream(_databaseFilePath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            var offset = (int)stream.Position;
            await stream.WriteAsync(entryBytes, 0, entryBytes.Length);

            // Update the index with the new file location.
            var fileLocation = new FileLocation(offset, entryBytes.Length);
            await _index.SetAsync(key, fileLocation);
        }
    }

    /// <summary>
    /// Attempts to retrieve the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key whose value to retrieve.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains a tuple with the value associated with the key 
    /// and a boolean indicating whether the key was found.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null or default.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <remarks>
    /// This method retrieves the value associated with the specified key from the file using the stored offset in the index.
    /// </remarks>
    /// <example>
    /// <code>
    /// var index = new InMemoryFileIndex&lt;Key&gt;();
    /// var store = new AppendOnlyFileStorageEngineWithIndex&lt;Key, City&gt;(
    ///     "database",
    ///     index,
    ///     key => key.Value.ToString(),
    ///     str => new Key(long.Parse(str)),
    ///     value => JsonSerializer.Serialize(value),
    ///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
    ///
    /// var value = await store.TryGetValueAsync(new Key(42));
    /// if (value.Found)
    /// {
    ///     Console.WriteLine(JsonSerializer.Serialize(value.Value));
    ///     // Output: {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
    /// }
    /// </code>
    /// </example>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key)
    {
        // Validate the key to ensure it is not null or default.
        Guard.AgainstNullOrDefault(() => key);

        // Try to get the file location from the index.
        var (fileLocation, found) = await _index.TryGetValueAsync(key);
        if (!found)
        {
            return (default, false);
        }

        // Read the entry from the file using the file location.
        byte[] buffer = new byte[fileLocation.Count];
        using (var stream = new FileStream(_databaseFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(fileLocation.Offset, SeekOrigin.Begin);
            await stream.ReadAsync(buffer, 0, buffer.Length);
        }

        var entry = Encoding.UTF8.GetString(buffer);
        var serializedValue = entry.Substring(entry.IndexOf(',') + 1);
        var value = _valueDeserializer(serializedValue);
        return (value, true);
    }

    /// <summary>
    /// Checks whether the store contains the specified key.
    /// </summary>
    /// <param name="key">The key to check for existence.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains a boolean indicating whether the key exists in the store.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null or default.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <remarks>
    /// This method checks if the key exists in the store by checking the index.
    /// </remarks>
    /// <example>
    /// <code>
    /// var index = new InMemoryFileIndex&lt;Key&gt;();
    /// var store = new AppendOnlyFileStorageEngineWithIndex&lt;Key, City&gt;(
    ///     "database",
    ///     index,
    ///     key => key.Value.ToString(),
    ///     str => new Key(long.Parse(str)),
    ///     value => JsonSerializer.Serialize(value),
    ///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
    ///
    /// var exists = await store.ContainsKeyAsync(new Key(42));
    /// Console.WriteLine(exists); // Output: true or false
    /// </code>
    /// </example>
    public async Task<bool> ContainsKeyAsync(TKey key)
    {
        // Validate the key to ensure it is not null or default.
        Guard.AgainstNullOrDefault(() => key);

        // Check if the key exists in the index.
        return await _index.ContainsKeyAsync(key);
    }

    /// <summary>
    /// Removes the value associated with the specified key.
    /// If the key does not exist, the operation is a no-op.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null or default.</exception>
    /// <remarks>
    /// This method removes the key from the index, effectively making the entry inaccessible.
    /// </remarks>
    /// <example>
    /// <code>
    /// var index = new InMemoryFileIndex&lt;Key&gt;();
    /// var store = new AppendOnlyFileStorageEngineWithIndex&lt;Key, City&gt;(
    ///     "database",
    ///     index,
    ///     key => key.Value.ToString(),
    ///     str => new Key(long.Parse(str)),
    ///     value => JsonSerializer.Serialize(value),
    ///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
    ///
    /// await store.RemoveAsync(new Key(42));
    /// </code>
    /// </example>
    public async Task RemoveAsync(TKey key)
    {
        // Validate the key to ensure it is not null or default.
        Guard.AgainstNullOrDefault(() => key);

        // Remove the key from the index.
        await _index.RemoveAsync(key);
    }

    /// <summary>
    /// Removes all key-value pairs from the store.
    /// </summary>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <example>
    /// <code>
    /// public readonly record struct Key(long Value) : IComparable&lt;Key&gt;
    /// {
    ///     public int CompareTo(Key other)
    ///     {
    ///         return Value.CompareTo(other.Value);
    ///     }
    /// }
    /// public readonly record struct City(string Name, IEnumerable&lt;Attraction&gt; Attractions);
    /// public readonly record struct Attraction(string Name);
    ///
    /// var index = new InMemoryFileIndex&lt;Key&gt;();
    /// var store = new AppendOnlyFileStorageEngineWithIndex&lt;Key, City&gt;(
    ///     "database",
    ///     index,
    ///     key => key.Value.ToString(),
    ///     str => new Key(long.Parse(str)),
    ///     value => JsonSerializer.Serialize(value),
    ///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
    /// 
    /// await store.Clear();
    /// </code>
    /// </example>
    public async Task Clear()
    {
        // Check if the database file exists.
        if (File.Exists(_databaseFilePath))
        {
            // Delete the database file.
            File.Delete(_databaseFilePath);
        }
    }

    /// <summary>
    /// Retrieves all key-value pairs from the store.
    /// </summary>
    /// <returns>A task representing the asynchronous operation. 
    /// The task result contains an enumerable collection of all key-value pairs in the store.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <example>
    /// <code>
    /// public readonly record struct Key(long Value) : IComparable&lt;Key&gt;
    /// {
    ///     public int CompareTo(Key other)
    ///     {
    ///         return Value.CompareTo(other.Value);
    ///     }
    /// }
    /// public readonly record struct City(string Name, IEnumerable&lt;Attraction&gt; Attractions);
    /// public readonly record struct Attraction(string Name);
    ///
    /// var index = new InMemoryFileIndex&lt;Key&gt;();
    /// var store = new AppendOnlyFileStorageEngineWithIndex&lt;Key, City&gt;(
    ///     "database",
    ///     index,
    ///     key => key.Value.ToString(),
    ///     str => new Key(long.Parse(str)),
    ///     value => JsonSerializer.Serialize(value),
    ///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
    /// 
    /// var items = await store.GetAllItems();
    /// foreach (var item in items)
    /// {
    ///     Console.WriteLine($"{item.Key.Value}, {JsonSerializer.Serialize(item.Value)}");
    /// }
    /// </code>
    /// </example>
    public async Task<IEnumerable<KeyValuePair<TKey, TValue>>> GetAllItems()
    {
        var result = new List<KeyValuePair<TKey, TValue>>();

        // Check if the database file exists.
        if (!File.Exists(_databaseFilePath))
        {
            return result;
        }

        var lines = await File.ReadAllLinesAsync(_databaseFilePath);
        foreach (var line in lines)
        {
            var parts = line.Split(new[] { ',' }, 2);
            if (parts.Length == 2)
            {
                var key = _keyDeserializer(parts[0]);
                var value = _valueDeserializer(parts[1]);
                result.Add(new KeyValuePair<TKey, TValue>(key, value));
            }
        }

        return result;
    }
}