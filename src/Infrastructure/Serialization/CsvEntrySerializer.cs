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
/// <b>Why CSV alongside binary?</b> CSV provides a line-per-entry serialization format for
/// debugging, data export, and interoperability with external tools (Excel, scripts, etc.). It is
/// slower and larger than binary, but invaluable during development and for scenarios where data
/// must be inspected without specialized tooling.
/// </para>
/// <para>
/// <b>Why Base64 fields?</b> The key and value wire formats are arbitrary binary (varints,
/// length prefixes, IEEE 754 bit patterns) — byte sequences that are almost never valid UTF-8
/// text. Textual formats cannot carry raw binary directly; the standard remedy (DDIA 2e Ch. 5,
/// "JSON, XML, and Binary Variants") is to encode binary payloads as Base64 text. Each CSV line
/// is therefore <c>base64(keyBytes),base64(valueBytes)</c>: the Base64 alphabet
/// (<c>A–Z a–z 0–9 + / =</c>) contains no CSV-significant characters, so no quoting or escaping
/// layer is needed at all, and the encoded payload survives the line-based framing byte-exactly.
/// The ~33% size overhead is the documented Base64 cost — acceptable for a debug/export format.
/// </para>
/// <para>
/// <b>Why catch-and-rethrow as SerializationException/DeserializationException?</b> Wrapping
/// exceptions in domain-specific types allows callers to handle serialization failures uniformly
/// without catching <see cref="IOException"/>, <see cref="FormatException"/>, etc. individually.
/// The original exception is preserved as <see cref="Exception.InnerException"/> for debugging.
/// </para>
/// </remarks>
public sealed class CsvEntrySerializer<TKey, TValue> : IEntrySerializer<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    // BOM-less UTF-8 encoding. Encoding.UTF8 emits a 3-byte BOM (0xEF 0xBB 0xBF) preamble on
    // the first write of each new StreamWriter. Since WriteEntry creates a fresh StreamWriter per
    // call, every entry would get a BOM prepended — corrupting sequential reads. BOM-less encoding
    // avoids this: entries are written as pure UTF-8 without preamble bytes between them.
    private static readonly Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Per-stream stash for a peeked byte on a non-seekable stream. When \r is followed by a byte
    // that is not \n and the stream cannot seek backward, we must preserve that peeked byte for
    // the next ReadEntry call on the SAME stream. The stash is keyed by stream instance so that
    // reusing one serializer across different streams never causes cross-stream contamination:
    // a stash left from stream A is invisible when ReadEntry is later called with stream B.
    // ConditionalWeakTable holds entries weakly — when a stream is GC'd its stash is released
    // automatically with no manual cleanup required.
    private readonly ConditionalWeakTable<Stream, PeekedByteHolder> _peekedBytes = new();

    // Mutable reference-type box for the per-stream peeked-byte stash.
    // -1 means "no stashed byte"; any other value is a valid 0–255 byte.
    private sealed class PeekedByteHolder
    {
        public int Value = -1;
    }

    /// <inheritdoc/>
    public void WriteEntry(Stream stream, TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            using var writer = new StreamWriter(stream, s_utf8NoBom, 1024, leaveOpen: true);
            // AC-8.6 (F-19): reuse a single MemoryStream for both the key and value
            // serializations within one WriteEntry call to avoid two heap allocations.
            using var buf = new MemoryStream();
            var csvLine = $"{SerializeToBase64Field(key, buf)},{SerializeToBase64Field(value, buf)}";
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
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var writer = new StreamWriter(stream, s_utf8NoBom, 1024, leaveOpen: true);
            await using var _ = writer.ConfigureAwait(false);
            // AC-8.6 (F-19): reuse a single MemoryStream for both serializations.
            using var buf = new MemoryStream();
            var csvLine = $"{SerializeToBase64Field(key, buf)},{SerializeToBase64Field(value, buf)}";
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
    /// parsed entry. Base64 fields and the line/field delimiters are all single-byte ASCII, so
    /// byte-level parsing is trivially safe.
    /// </remarks>
    public (TKey Key, TValue Value)? ReadEntry(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            var line = ReadCsvLineFromStream(stream);
            if (line == null)
            {
                return null;
            }

            var parts = line.Split(',');
            if (parts.Length != 2)
            {
                throw new DeserializationException("CSV line does not contain a valid key-value pair.");
            }

            var key = DeserializeFromBase64Field<TKey>(parts[0]);
            var value = DeserializeFromBase64Field<TValue>(parts[1]);
            return (key, value);
        }
        catch (Exception ex) when (ex is not DeserializationException)
        {
            // AC-8.2 (F-42): do NOT re-wrap a DeserializationException — the original
            // diagnostic (message, inner exception chain) must be preserved. Only low-level
            // I/O, Base64, and format exceptions need the domain wrapping.
            throw new DeserializationException("Error occurred while reading entry from CSV.", ex);
        }
    }

    /// <inheritdoc/>
    public bool CanRead(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.Position < stream.Length;
    }

    // Serializes the object's binary wire to a Base64 CSV field. The wire bytes are arbitrary
    // binary; decoding them as UTF-8 text would silently replace every invalid sequence with
    // U+FFFD — corrupting virtually every varint, float, and length prefix. Base64 is the
    // standard binary-in-text carrier (DDIA 2e Ch. 5) and its alphabet needs no CSV escaping.
    //
    // AC-8.6 (F-19): accepts a reusable MemoryStream (reset to position 0 before use) so
    // the caller can share one allocation across multiple field serializations per entry.
    private static string SerializeToBase64Field<T>(T obj, MemoryStream buf) where T : ISerializable<T>, new()
    {
        buf.SetLength(0); // reset without deallocating the backing array
        obj.Serialize(buf);
        return Convert.ToBase64String(buf.GetBuffer().AsSpan(0, (int)buf.Length));
    }

    private static T DeserializeFromBase64Field<T>(string field) where T : ISerializable<T>, new()
    {
        using var memoryStream = new MemoryStream(Convert.FromBase64String(field), writable: false);
        return T.Deserialize(memoryStream);
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
    /// Fields are Base64 (pure ASCII, no quotes, no embedded delimiters), so line parsing is a
    /// plain scan to LF / CR-LF with no quote-state tracking.
    ///
    /// AC-8.6 (F-19): per-byte syscall cost is mitigated by the callers — <see cref="ReadEntry"/>
    /// is invoked through <see cref="IEntrySerializer{TKey,TValue}"/> whose typical caller-side
    /// stream is already a <c>FileStream</c> (file-backed, with OS page-cache reads), or a
    /// <c>MemoryStream</c> (no syscall at all). Wrapping in <c>BufferedStream</c> per call
    /// would break multi-entry sequential reads because the wrapper's look-ahead advances the
    /// underlying stream beyond the first entry's bytes; the wrapper is discarded at return
    /// and the underlying stream is left at the wrong position for the next call. The buffering
    /// responsibility therefore belongs to the caller — callers that pay per-byte OS cost should
    /// wrap the stream in a <c>BufferedStream</c> before the first <see cref="ReadEntry"/> call
    /// and retain it across all reads on that stream.
    /// </remarks>
    private string? ReadCsvLineFromStream(Stream stream)
    {
        // Retrieve (or lazily create) the per-stream stash so the stash follows the stream,
        // not the serializer instance. This prevents a peeked byte from stream A from leaking
        // into a subsequent ReadEntry call that uses a different stream B.
        var stash = _peekedBytes.GetOrCreateValue(stream);
        var bytes = new List<byte>();

        while (true)
        {
            // Consume a previously-stashed peeked byte first. This arises when the previous
            // call on THIS stream saw \r followed by a non-\n byte on a non-seekable stream:
            // we could not seek backward, so we stashed that byte here for the next call to
            // consume.
            int b;
            if (stash.Value != -1)
            {
                b = stash.Value;
                stash.Value = -1;
            }
            else
            {
                b = stream.ReadByte();
            }

            if (b == -1)
            {
                return bytes.Count > 0 ? Encoding.UTF8.GetString([.. bytes]) : null;
            }

            if (b == '\n')
            {
                return Encoding.UTF8.GetString([.. bytes]);
            }

            if (b == '\r')
            {
                // Consume \r\n as a single newline. For a lone \r at EOF (ReadByte returns -1),
                // do not seek backward — there is nothing to rewind, and calling Seek on a
                // non-seekable stream throws NotSupportedException.
                var next = stream.ReadByte();
                if (next != -1 && next != '\n')
                {
                    if (stream.CanSeek)
                    {
                        // Seekable: rewind the one byte we peeked.
                        stream.Seek(-1, SeekOrigin.Current);
                    }
                    else
                    {
                        // Non-seekable: stash the peeked byte in the per-stream stash so the
                        // next call on this same stream picks it up at the top of the loop
                        // rather than losing it silently.
                        stash.Value = next;
                    }
                }

                return Encoding.UTF8.GetString([.. bytes]);
            }

            bytes.Add((byte)b);
        }
    }
}
