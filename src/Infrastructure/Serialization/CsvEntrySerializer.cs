// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
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
/// <remarks>
/// <para>
/// <b>Why CSV alongside binary?</b> CSV provides a human-readable serialization format for
/// debugging, data export, and interoperability with external tools (Excel, scripts, etc.). It is
/// slower and larger than binary, but invaluable during development and for scenarios where data
/// must be inspected without specialized tooling.
/// </para>
/// <para>
/// <b>Why custom CSV parsing (SplitCsvLine) instead of a library?</b> The parser handles the
/// minimal CSV subset needed for key-value pairs: comma-separated fields with quoted strings
/// supporting escaped quotes and embedded commas/newlines. A full CSV library (e.g., CsvHelper)
/// would add a dependency for functionality that's mostly unused. The custom parser is ~25 lines
/// and handles exactly the cases this serializer produces.
/// </para>
/// <para>
/// <b>Why catch-and-rethrow as SerializationException/DeserializationException?</b> Wrapping
/// exceptions in domain-specific types allows callers to handle serialization failures uniformly
/// without catching <see cref="IOException"/>, <see cref="FormatException"/>, etc. individually.
/// The original exception is preserved as <see cref="Exception.InnerException"/> for debugging.
/// </para>
/// </remarks>
public sealed class CsvEntrySerializer<TKey, TValue> : IEntrySerializer<TKey, TValue>
    where TKey : IComparable<TKey>, ISerializable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    // BOM-less UTF-8 encoding. Encoding.UTF8 emits a 3-byte BOM (0xEF 0xBB 0xBF) preamble on
    // the first write of each new StreamWriter. Since WriteEntry creates a fresh StreamWriter per
    // call, every entry would get a BOM prepended — corrupting sequential reads. BOM-less encoding
    // avoids this: entries are written as pure UTF-8 without preamble bytes between them.
    private static readonly Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc/>
    public void WriteEntry(Stream stream, TKey key, TValue value)
    {
        Guard.AgainstNullOrDefault(() => stream);
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);

        try
        {
            using var writer = new StreamWriter(stream, s_utf8NoBom, 1024, leaveOpen: true);
            var keyString = SerializeToCsvFormat(key);
            var valueString = SerializeToCsvFormat(value);
            var csvLine = $"{keyString},{valueString}";
            writer.WriteLine(csvLine);
            writer.Flush();
        }
        catch (Exception ex)
        {
            throw new SerializationException("Error occurred while writing entry to CSV.", ex);
        }
    }

    /// <inheritdoc/>
    public async Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => stream);
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var writer = new StreamWriter(stream, s_utf8NoBom, 1024, leaveOpen: true);
            await using var _ = writer.ConfigureAwait(false);
            var keyString = SerializeToCsvFormat(key);
            var valueString = SerializeToCsvFormat(value);
            var csvLine = $"{keyString},{valueString}";
            await writer.WriteLineAsync(csvLine).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SerializationException("Error occurred while writing entry to CSV.", ex);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads directly from the stream byte-by-byte instead of using a <see cref="StreamReader"/>.
    /// StreamReader's internal buffering (default 1024 bytes) causes it to read ahead from the
    /// underlying stream beyond the current entry. On dispose, those buffered-but-unconsumed bytes
    /// are lost — the stream position has already advanced past them, corrupting subsequent reads.
    /// Byte-level reading avoids this: the stream position advances exactly to the end of the
    /// parsed entry. CSV's special characters (comma, quote, newline, CR) are all single-byte in
    /// UTF-8, so byte-level parsing is safe for detecting record boundaries.
    /// </remarks>
    public (TKey Key, TValue Value)? ReadEntry(Stream stream)
    {
        Guard.AgainstNullOrDefault(() => stream);

        try
        {
            var line = ReadCsvLineFromStream(stream);
            if (line == null)
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

    private static string SerializeToCsvFormat<T>(T obj) where T : ISerializable<T>, new()
    {
        if (obj is string str)
        {
            // Handle escaping of quotes and commas in strings
            str = str.Replace("\"", "\"\"");
            if (str.Contains(',') || str.Contains('"') || str.Contains('\n'))
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

    /// <summary>
    /// Reads a single CSV line directly from the stream, byte-by-byte.
    /// </summary>
    /// <remarks>
    /// Why byte-by-byte from the raw stream instead of using StreamReader? StreamReader buffers
    /// ahead (typically 1024 bytes), consuming more from the underlying stream than the single
    /// entry we need. On dispose, those extra bytes are lost. Reading bytes directly keeps the
    /// stream position exactly at the end of the parsed entry.
    ///
    /// Why is byte-level parsing safe for CSV? The record delimiters (comma 0x2C, quote 0x22,
    /// LF 0x0A, CR 0x0D) are all single-byte ASCII values. In UTF-8, multi-byte sequences never
    /// contain bytes in the 0x00–0x7F range, so these delimiters cannot appear as part of a
    /// multi-byte character. We accumulate raw bytes and decode the complete line to string once
    /// at the end.
    /// </remarks>
    private static string? ReadCsvLineFromStream(Stream stream)
    {
        var bytes = new List<byte>();
        var inQuotes = false;

        // Skip UTF-8 BOM (0xEF 0xBB 0xBF) if present. Old versions of this serializer used
        // Encoding.UTF8 (with BOM) in StreamWriter, which prepended a BOM to each entry.
        // New writes use s_utf8NoBom, but we still need to read old data gracefully.
        if (stream.Position < stream.Length)
        {
            var first = stream.ReadByte();
            if (first == 0xEF && stream.Length - stream.Position >= 2)
            {
                var second = stream.ReadByte();
                var third = stream.ReadByte();
                if (second != 0xBB || third != 0xBF)
                {
                    // Not a BOM — rewind all three bytes.
                    stream.Seek(-3, SeekOrigin.Current);
                }

                // BOM consumed — continue to read the actual data.
            }
            else if (first == -1)
            {
                return null;
            }
            else
            {
                // Not a BOM — rewind the one byte.
                stream.Seek(-1, SeekOrigin.Current);
            }
        }

        while (true)
        {
            var b = stream.ReadByte();
            if (b == -1)
            {
                return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
            }

            if (b == '"')
            {
                inQuotes = !inQuotes;
                bytes.Add((byte)b);
            }
            else if (b == '\n' && !inQuotes)
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            else if (b == '\r' && !inQuotes)
            {
                // Consume \r\n as a single newline.
                if (stream.ReadByte() != '\n')
                {
                    // Not \n — rewind the one byte we consumed.
                    stream.Seek(-1, SeekOrigin.Current);
                }

                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            else
            {
                bytes.Add((byte)b);
            }
        }
    }

    private static string[] SplitCsvLine(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped quote (doubled): consume both, emit one quote
                    sb.Append('"');
                    i++;
                }
                else
                {
                    // Toggle quote state
                    inQuotes = !inQuotes;
                }
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
