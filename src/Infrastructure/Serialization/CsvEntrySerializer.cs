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
namespace Boutquin.Storage.Infrastructure.Serialization;

/// <summary>
/// Provides a CSV implementation of the <see cref="IEntrySerializer{TKey, TValue}"/> interface.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TValue">The type of the value.</typeparam>
public sealed class CsvEntrySerializer<TKey, TValue> : IEntrySerializer<TKey, TValue>
    where TKey : IComparable<TKey>, ISerializable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <inheritdoc/>
    public async Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => stream);
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true);
            var keyString = SerializeToCsvFormat(key);
            var valueString = SerializeToCsvFormat(value);
            var csvLine = $"{keyString},{valueString}";
            await writer.WriteLineAsync(csvLine).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SerializationException("Error occurred while writing entry to CSV.", ex);
        }
    }

    /// <inheritdoc/>
    public (TKey Key, TValue Value)? ReadEntry(Stream stream)
    {
        Guard.AgainstNullOrDefault(() => stream);

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
            if (reader.EndOfStream)
            {
                return null;
            }

            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var parts = SplitCsvLine(line);
            if (parts.Length != 2)
            {
                throw new DeserializationException("CSV line does not contain a valid key-value pair.");
            }

            var key = DeserializeFromCsvFormat<TKey>(parts[0]);
            var value = DeserializeFromCsvFormat<TValue>(parts[1]);
            return (key, value);
        }
        catch (Exception ex)
        {
            throw new DeserializationException("Error occurred while reading entry from CSV.", ex);
        }
    }

    /// <inheritdoc/>
    public bool CanRead(Stream stream)
    {
        Guard.AgainstNullOrDefault(() => stream);
        return stream.Position < stream.Length;
    }

    private string SerializeToCsvFormat<T>(T obj) where T : ISerializable<T>, new()
    {
        if (obj is string str)
        {
            // Handle escaping of quotes and commas in strings
            str = str.Replace("\"", "\"\"");
            if (str.Contains(",") || str.Contains("\"") || str.Contains("\n"))
            {
                str = $"\"{str}\"";
            }
            return str;
        }
        else
        {
            using var memoryStream = new MemoryStream();
            using var writer = new StreamWriter(memoryStream);
            obj.Serialize(writer.BaseStream);
            writer.Flush();
            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream);
            return reader.ReadToEnd();
        }
    }

    private static T DeserializeFromCsvFormat<T>(string csv) where T : ISerializable<T>, new()
    {
        if (typeof(T) == typeof(string))
        {
            // Handle unescaping of quotes in strings
            if (csv.StartsWith("\"") && csv.EndsWith("\""))
            {
                csv = csv.Substring(1, csv.Length - 2).Replace("\"\"", "\"");
            }
            return (T)(object)csv;
        }
        else
        {
            using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            return T.Deserialize(memoryStream);
        }
    }

    private string[] SplitCsvLine(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"' && (sb.Length == 0 || sb[^1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        parts.Add(sb.ToString());
        return parts.ToArray();
    }
}