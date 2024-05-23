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
/// Provides a simple file-based key-value store with asynchronous operations.
/// This class implements the <see cref="IKeyValueStore{K, V}"/> interface and 
/// uses a file to store key-value pairs, similar to a basic database.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>The following example demonstrates how to use the <see cref="AppendOnlyFileStorageEngine{K, V}"/> class:</para>
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
/// var store = new AppendOnlyFileStorageEngine&lt;Key, City&gt;(
///     "database",
///     key => key.Value.ToString(),
///     str => new Key(long.Parse(str)),
///     value => JsonSerializer.Serialize(value),
///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
/// 
/// await store.SetAsync(new Key(123456), new City("London", new List&lt;Attraction&gt; { new Attraction("Big Ben"), new Attraction("London Eye") }));
/// await store.SetAsync(new Key(42), new City("San Francisco", new List&lt;Attraction&gt; { new Attraction("Golden Gate Bridge") }));
/// 
/// var value = await store.TryGetValueAsync(new Key(42));
/// if (value.Found)
/// {
///     Console.WriteLine(value.Value); // Output: {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
/// }
/// 
/// await store.SetAsync(new Key(42), new City("San Francisco", new List&lt;Attraction&gt; { new Attraction("Exploratorium") }));
/// value = await store.TryGetValueAsync(new Key(42));
/// if (value.Found)
/// {
///     Console.WriteLine(value.Value); // Output: {"name":"San Francisco","attractions":["Exploratorium"]}
/// }
/// </code>
/// </remarks>
public class AppendOnlyFileStorageEngine<TKey, TValue> : IBulkKeyValueStore<TKey, TValue> where TKey : IComparable<TKey>
{
    private readonly string _databaseFilePath;
    private readonly Func<TKey, string> _keySerializer;
    private readonly Func<string, TKey> _keyDeserializer;
    private readonly Func<TValue, string> _valueSerializer;
    private readonly Func<string, TValue> _valueDeserializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngine{K, V}"/> class.
    /// </summary>
    /// <param name="databaseFilePath">The path to the database file.</param>
    /// <param name="keySerializer">A function to serialize keys to strings.</param>
    /// <param name="keyDeserializer">A function to deserialize keys from strings.</param>
    /// <param name="valueSerializer">A function to serialize values to strings.</param>
    /// <param name="valueDeserializer">A function to deserialize values from strings.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="databaseFilePath"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when any of the serializer or deserializer functions are null.</exception>
    public AppendOnlyFileStorageEngine(
        string databaseFilePath,
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
    /// var store = new AppendOnlyFileStorageEngine&lt;Key, City&gt;(
    ///     "database",
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
        await File.AppendAllTextAsync(_databaseFilePath, entry);
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
    /// var store = new AppendOnlyFileStorageEngine&lt;Key, City&gt;(
    ///     "database",
    ///     key => key.Value.ToString(),
    ///     str => new Key(long.Parse(str)),
    ///     value => JsonSerializer.Serialize(value),
    ///     str => JsonSerializer.Deserialize&lt;City&gt;(str));
    /// 
    /// var value = await store.TryGetValueAsync(new Key(42));
    /// if (value.Found)
    /// {
    ///     Console.WriteLine(value.Value); // Output: {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
    /// }
    /// </code>
    /// </example>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key)
    {
        // Validate the key to ensure it is not null or default.
        Guard.AgainstNullOrDefault(() => key);

        // Check if the database file exists.
        if (!File.Exists(_databaseFilePath))
        {
            return (default, false);
        }

        var serializedKey = _keySerializer(key);
        var lines = await File.ReadAllLinesAsync(_databaseFilePath);
        // Find the last occurrence of the key to get the most recent value.
        var line = lines.LastOrDefault(l => l.StartsWith($"{serializedKey},"));
        if (line == null)
        {
            return (default, false);
        }

        // Deserialize the value.
        var value = _valueDeserializer(line.Substring(serializedKey.Length + 1));
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
    /// var store = new AppendOnlyFileStorageEngine&lt;Key, City&gt;(
    ///     "database",
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

        // Check if the database file exists.
        if (!File.Exists(_databaseFilePath))
        {
            return false;
        }

        var serializedKey = _keySerializer(key);
        var lines = await File.ReadAllLinesAsync(_databaseFilePath);
        // Check if any line starts with the serialized key.
        return lines.Any(l => l.StartsWith($"{serializedKey},"));
    }

    /// <summary>
    /// Removes the value associated with the specified key.
    /// If the key does not exist, the operation is a no-op.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null or default.</exception>
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
    /// var store = new AppendOnlyFileStorageEngine&lt;Key, City&gt;(
    ///     "database",
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

        // Check if the database file exists.
        if (!File.Exists(_databaseFilePath))
        {
            return;
        }

        var serializedKey = _keySerializer(key);
        var lines = await File.ReadAllLinesAsync(_databaseFilePath);
        // Create a new list of lines excluding the ones that start with the serialized key.
        var newLines = lines.Where(l => !l.StartsWith($"{serializedKey},")).ToList();
        // Write the updated list of lines back to the file.
        await File.WriteAllLinesAsync(_databaseFilePath, newLines);
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
    /// var store = new AppendOnlyFileStorageEngine&lt;Key, City&gt;(
    ///     "database",
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
    /// var store = new AppendOnlyFileStorageEngine&lt;Key, City&gt;(
    ///     "database",
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