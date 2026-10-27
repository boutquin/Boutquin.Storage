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
#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the CsvEntrySerializer class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class CsvEntrySerializerTests
{
    /// <summary>
    /// Test to ensure that WriteEntryAsync correctly writes a key-value pair to the stream in CSV format.
    /// </summary>
    [Fact]
    public async Task WriteEntryAsync_ShouldWriteKeyValuePairToStream()
    {
        // Arrange: Create a memory stream, key, value, and serializer.
        using var stream = new MemoryStream();
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Write the key-value pair to the stream.
        await serializer.WriteEntryAsync(stream, key, value);

        // Assert: round-trip via the same serializer. Hardcoding the raw byte shape of the
        // CSV-serialized wrapper would couple this test to the wrapper's internal wire format
        // (the CSV serializer reads non-string fields as raw bytes from the wrapper's
        // Serialize stream), which changed in 2.0.0. Exercising the public contract — written
        // bytes read back to the same values — is the right invariant.
        stream.Position = 0;
        var entry = serializer.ReadEntry(stream);
        Assert.NotNull(entry);
        Assert.Equal(key.Value, entry.Value.Key.Value);
        Assert.Equal(value.Value, entry.Value.Value.Value);
    }

    /// <summary>
    /// Test to ensure that WriteEntryAsync throws an exception when the stream is null.
    /// </summary>
    [Fact]
    public async Task WriteEntryAsync_ShouldThrowExceptionIfStreamIsNull()
    {
        // Arrange: Create a null stream, key, value, and serializer.
        Stream stream = null;
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => serializer.WriteEntryAsync(stream, key, value));
        Assert.Equal("Value cannot be null. (Parameter 'stream')", exception.Message);
    }

    /// <summary>
    /// Test to ensure that ReadEntry correctly reads a key-value pair from the stream.
    /// </summary>
    [Fact]
    public async Task ReadEntry_ShouldReadKeyValuePairFromStream()
    {
        // Arrange: seed the stream via the same serializer's write path. Hardcoding raw
        // CSV bytes would couple the test to the wrapper's 1.x wire format; the public
        // contract is "the CSV serializer reads what it writes."
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        await serializer.WriteEntryAsync(stream, new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        stream.Position = 0;

        // Act: Read the key-value pair from the stream.
        var entry = serializer.ReadEntry(stream);

        // Assert: Verify that the key-value pair was read correctly.
        Assert.NotNull(entry);
        Assert.Equal(1, entry?.Key.Value);
        Assert.Equal("value1", entry?.Value.Value);
    }

    /// <summary>
    /// Test to ensure that ReadEntry returns null when the stream is empty.
    /// </summary>
    [Fact]
    public void ReadEntry_ShouldReturnNullIfStreamIsEmpty()
    {
        // Arrange: Create an empty memory stream and serializer.
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Read the key-value pair from the stream.
        var entry = serializer.ReadEntry(stream);

        // Assert: Verify that the entry is null.
        Assert.Null(entry);
    }

    /// <summary>
    /// Test to ensure that ReadEntry throws an exception when the stream is null.
    /// </summary>
    [Fact]
    public void ReadEntry_ShouldThrowExceptionIfStreamIsNull()
    {
        // Arrange: Create a null stream and serializer.
        Stream stream = null;
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        var exception = Assert.Throws<ArgumentNullException>(() => serializer.ReadEntry(stream));
        Assert.Equal("Value cannot be null. (Parameter 'stream')", exception.Message);
    }

    /// <summary>
    /// Test to ensure that CanRead returns true if the stream has more data to read.
    /// </summary>
    [Fact]
    public void CanRead_ShouldReturnTrueIfStreamHasMoreData()
    {
        // Arrange: Create a memory stream with data and serializer.
        var csvData = "1,value1\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvData));
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Check if the stream can be read.
        var canRead = serializer.CanRead(stream);

        // Assert: Verify that CanRead returns true.
        Assert.True(canRead);
    }

    /// <summary>
    /// Test to ensure that CanRead returns false if the stream has no more data to read.
    /// </summary>
    [Fact]
    public void CanRead_ShouldReturnFalseIfStreamHasNoMoreData()
    {
        // Arrange: Create an empty memory stream and serializer.
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Check if the stream can be read.
        var canRead = serializer.CanRead(stream);

        // Assert: Verify that CanRead returns false.
        Assert.False(canRead);
    }

    /// <summary>
    /// Test to ensure that CanRead throws an exception when the stream is null.
    /// </summary>
    [Fact]
    public void CanRead_ShouldThrowExceptionIfStreamIsNull()
    {
        // Arrange: Create a null stream and serializer.
        Stream stream = null;
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        var exception = Assert.Throws<ArgumentNullException>(() => serializer.CanRead(stream));
        Assert.Equal("Value cannot be null. (Parameter 'stream')", exception.Message);
    }

    /// <summary>
    /// Test to ensure that multiple entries can be read sequentially from a single stream.
    /// This verifies that ReadEntry advances the stream position correctly — the bug that
    /// prompted the StreamReader-to-byte-reading fix was that StreamReader's internal buffer
    /// consumed bytes beyond the current entry, corrupting subsequent reads.
    /// </summary>
    [Fact]
    public void ReadEntry_ShouldReadMultipleEntriesSequentiallyFromSingleStream()
    {
        // Arrange: Write multiple entries to a single stream, then read them back.
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var stream = new MemoryStream();

        var entries = new (SerializableWrapper<int> Key, SerializableWrapper<string> Value)[]
        {
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha")),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("beta")),
            (new SerializableWrapper<int>(3), new SerializableWrapper<string>("gamma")),
        };

        foreach (var (key, value) in entries)
        {
            serializer.WriteEntry(stream, key, value);
        }

        // Act: Rewind and read all entries sequentially.
        stream.Position = 0;
        var results = new List<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)>();
        while (stream.Position < stream.Length)
        {
            var entry = serializer.ReadEntry(stream);
            if (entry == null)
            {
                break;
            }

            results.Add(entry.Value);
        }

        // Assert: All three entries should be read correctly.
        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].Key.Value);
        Assert.Equal("alpha", results[0].Value.Value);
        Assert.Equal(2, results[1].Key.Value);
        Assert.Equal("beta", results[1].Value.Value);
        Assert.Equal(3, results[2].Key.Value);
        Assert.Equal("gamma", results[2].Value.Value);
    }

    /// <summary>
    /// Test to ensure that ReadEntry throws a DeserializationException on error.
    /// </summary>
    [Fact]
    public void ReadEntry_ShouldThrowDeserializationExceptionOnError()
    {
        // Arrange: Create a memory stream with invalid CSV data and serializer.
        var invalidCsvData = "1";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalidCsvData));
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act & Assert: Check that a DeserializationException is thrown.
        // AC-8.2 (F-42): DeserializationException is no longer double-wrapped — the original
        // diagnostic propagates directly. The "no comma" line hits the inner
        // DeserializationException("CSV line does not contain a valid key-value pair.")
        // which now propagates without re-wrapping.
        var exception = Assert.Throws<DeserializationException>(() => serializer.ReadEntry(stream));
        Assert.Equal("CSV line does not contain a valid key-value pair.", exception.Message);
    }

    // ── String escape paths (uncovered branches in SerializeToCsvFormat) ─────

    [Fact]
    public void Roundtrip_StringValueWithComma_PreservesContent()
    {
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("hello, world");

        serializer.WriteEntry(stream, key, value);
        stream.Position = 0;
        var entry = serializer.ReadEntry(stream);

        Assert.NotNull(entry);
        Assert.Equal("hello, world", entry.Value.Value.Value);
    }

    [Fact]
    public void Roundtrip_StringValueWithDoubleQuote_PreservesContent()
    {
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(1);
        // Embedded double quotes — must be doubled inside a quoted field.
        var value = new SerializableWrapper<string>("she said \"hi\"");

        serializer.WriteEntry(stream, key, value);
        stream.Position = 0;
        var entry = serializer.ReadEntry(stream);

        Assert.NotNull(entry);
        Assert.Equal("she said \"hi\"", entry.Value.Value.Value);
    }

    [Fact]
    public void Roundtrip_StringValueWithEmbeddedNewline_PreservesContent()
    {
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("line1\nline2");

        serializer.WriteEntry(stream, key, value);
        stream.Position = 0;
        var entry = serializer.ReadEntry(stream);

        Assert.NotNull(entry);
        Assert.Equal("line1\nline2", entry.Value.Value.Value);
    }

    // ── Binary-payload fidelity through the textual format ───────────────────
    // The serialized wire of a wrapper is arbitrary BINARY (varints, length prefixes); a
    // textual format must carry it losslessly (DDIA 2e Ch. 5 prescribes Base64 for binary
    // payloads in text formats). Round-tripping the bytes through a UTF-8 string decode
    // corrupts every byte sequence that is not valid UTF-8 — which is almost all of them.

    [Fact]
    public void Roundtrip_IntBeyondOneByteVarint_PreservesValue()
    {
        // 100 zig-zag-encodes to 0xC8 0x01 — 0xC8 is an invalid UTF-8 lead byte, so a text
        // decode replaces it with U+FFFD and the payload is unreadable after a clean write.
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        serializer.WriteEntry(stream, new SerializableWrapper<int>(1), new SerializableWrapper<int>(100));
        stream.Position = 0;

        var entry = serializer.ReadEntry(stream);

        Assert.NotNull(entry);
        Assert.Equal(100, entry.Value.Value.Value);
    }

    [Fact]
    public void Roundtrip_DoubleValue_PreservesValue()
    {
        // IEEE 754 bit patterns are essentially never valid UTF-8; the lossy decode silently
        // reads back a DIFFERENT double rather than failing.
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<double>>();
        serializer.WriteEntry(stream, new SerializableWrapper<int>(1), new SerializableWrapper<double>(3.14159));
        stream.Position = 0;

        var entry = serializer.ReadEntry(stream);

        Assert.NotNull(entry);
        Assert.Equal(3.14159, entry.Value.Value.Value);
    }

    [Fact]
    public void Roundtrip_LongString_PreservesValue()
    {
        // A 200-char string's length prefix is a multi-byte varint (0xC8 0x01) — same
        // corruption class as the int case, but reached through a string payload.
        var longString = new string('x', 200);
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        serializer.WriteEntry(stream, new SerializableWrapper<int>(1), new SerializableWrapper<string>(longString));
        stream.Position = 0;

        var entry = serializer.ReadEntry(stream);

        Assert.NotNull(entry);
        Assert.Equal(longString, entry.Value.Value.Value);
    }

    [Fact]
    public void Roundtrip_StringWhosePayloadStartsAndEndsWithQuoteByte_PreservesValue()
    {
        // A payload whose restored bytes happen to start AND end with 0x22 (the quote char)
        // must not be re-unwrapped a second time after CSV unescaping.
        var value = "\"" + new string('a', 28) + "\"";
        using var stream = new MemoryStream();
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        serializer.WriteEntry(stream, new SerializableWrapper<int>(1), new SerializableWrapper<string>(value));
        stream.Position = 0;

        var entry = serializer.ReadEntry(stream);

        Assert.NotNull(entry);
        Assert.Equal(value, entry.Value.Value.Value);
    }

    // ── WriteEntry sync path (uncovered catch block) ─────────────────────────

    [Fact]
    public void WriteEntry_OnUnwritableStream_ThrowsSerializationException()
    {
        // A read-only stream causes StreamWriter to throw on flush; the catch block
        // wraps it as SerializationException.
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("v");
        using var readOnly = new MemoryStream(new byte[16], writable: false);

        Assert.Throws<SerializationException>(() => serializer.WriteEntry(readOnly, key, value));
    }

    [Fact]
    public void WriteEntry_NullKey_Throws()
    {
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() =>
            serializer.WriteEntry(stream, null!, new SerializableWrapper<string>("v")));
    }

    [Fact]
    public void WriteEntry_NullValue_Throws()
    {
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() =>
            serializer.WriteEntry(stream, new SerializableWrapper<int>(1), null!));
    }

    [Fact]
    public void WriteEntry_NullStream_Throws()
    {
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        Assert.Throws<ArgumentNullException>(() =>
            serializer.WriteEntry(null!, new SerializableWrapper<int>(1), new SerializableWrapper<string>("v")));
    }

    [Fact]
    public async Task WriteEntryAsync_CancelledToken_Throws()
    {
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var stream = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => serializer.WriteEntryAsync(
            stream,
            new SerializableWrapper<int>(1),
            new SerializableWrapper<string>("v"),
            cts.Token));
    }

    // ── CanRead positive case ────────────────────────────────────────────────

    [Fact]
    public void CanRead_StreamAtEnd_ReturnsFalse()
    {
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var stream = new MemoryStream(new byte[8]);
        stream.Position = stream.Length;
        serializer.CanRead(stream).Should().BeFalse();
    }

    [Fact]
    public void CanRead_NullStream_Throws()
    {
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        Assert.Throws<ArgumentNullException>(() => serializer.CanRead(null!));
    }

    // ── AC-8.1: lone \r at EOF must not corrupt stream position ─────────────

    [Fact]
    public void ReadEntry_LoneCrAtEof_RoundTripsWithoutStreamCorruption()
    {
        // A bare CR at EOF must terminate one record without rewinding into its payload.
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var stream = new MemoryStream();

        // Replace the writer's entire native terminator; changing only LF on Windows
        // would create CR-CR and exercise two records instead of a lone CR.
        serializer.WriteEntry(stream, new SerializableWrapper<int>(7), new SerializableWrapper<string>("lone-cr"));
        var data = WithRecordTerminator(stream.ToArray(), "\r");

        using var ms = new MemoryStream(data);
        var entry = serializer.ReadEntry(ms);

        // The entry must round-trip correctly, and the stream must be at the end
        // (not seeked backward by -1 into the payload).
        Assert.NotNull(entry);
        Assert.Equal(7, entry!.Value.Key.Value);
        Assert.Equal("lone-cr", entry.Value.Value.Value);
        Assert.Equal(ms.Length, ms.Position); // stream not corrupted
    }

    [Fact]
    public void ReadEntry_LoneCrAtEof_NonSeekableStream_RoundTrips()
    {
        // AC-8.1 (F-43): CanSeek guard — a non-seekable stream carrying a lone \r at EOF
        // must not throw NotSupportedException from the Seek call.
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var backing = new MemoryStream();
        serializer.WriteEntry(backing, new SerializableWrapper<int>(9), new SerializableWrapper<string>("no-seek"));
        var data = WithRecordTerminator(backing.ToArray(), "\r");

        // Wrap in a non-seekable stream.
        using var nonSeekable = new NonSeekableStream(new MemoryStream(data));
        var entry = serializer.ReadEntry(nonSeekable);

        Assert.NotNull(entry);
        Assert.Equal(9, entry!.Value.Key.Value);
        Assert.Equal("no-seek", entry.Value.Value.Value);
    }

    // ── AC-8.2: double-wrapping DeserializationException ────────────────────

    [Fact]
    public void ReadEntry_DeserializationExceptionNotDoubleWrapped()
    {
        // AC-8.2 (F-42): when a DeserializationException is thrown inside the try block,
        // it must propagate directly — not re-wrapped in a second DeserializationException.
        // The trigger is a CSV line with no comma: parts.Length != 2 → inner throw of
        // DeserializationException("CSV line does not contain a valid key-value pair.").
        // Without the `when (ex is not DeserializationException)` guard, the outer catch
        // re-wraps it as DeserializationException("Error occurred while reading entry from CSV.")
        // and the original message is buried in InnerException.
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var badLine = "no-comma-here\n"; // no comma → parts.Length == 1 != 2
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(badLine));

        var ex = Assert.Throws<DeserializationException>(() => serializer.ReadEntry(stream));
        // With fix: the original message propagates directly.
        // Without fix: "Error occurred while reading entry from CSV."
        Assert.Equal("CSV line does not contain a valid key-value pair.", ex.Message);
        // And InnerException should be null (no wrapping of the DeserializationException).
        Assert.Null(ex.InnerException);
    }

    // ── AC-8.3: NoCompressionCodec undersized destination ────────────────────

    [Fact]
    public void ReadEntry_CarriageReturnFollowedByNonLinefeed_NonSeekableStream_NextEntryNotCorrupted()
    {
        // A bare \r followed by a non-\n byte on a non-seekable stream: the peeked byte must
        // not be silently discarded (which would corrupt the second entry). The fix stores the
        // peeked byte in a _peekedByte field and reuses it at the top of the next read loop.
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Build two entries separated by bare \r (not CRLF).
        using var buf = new MemoryStream();
        serializer.WriteEntry(buf, new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"));
        var entry1Bytes = WithRecordTerminator(buf.ToArray(), "\r");

        buf.SetLength(0);
        buf.Position = 0;
        serializer.WriteEntry(buf, new SerializableWrapper<int>(2), new SerializableWrapper<string>("second"));
        var entry2Bytes = buf.ToArray();

        // Entry1 ends in one bare \r; entry2 starts immediately after.
        // The effect: ReadEntry reads up to \r, then peeks the first byte of entry2. That byte
        // is not \n, so on a seekable stream we'd seek -1; on a non-seekable stream we must
        // stash it in _peekedByte instead.
        var combined = entry1Bytes.Concat(entry2Bytes).ToArray();
        using var nonSeekable = new NonSeekableStream(new MemoryStream(combined));

        var first = serializer.ReadEntry(nonSeekable);
        var second = serializer.ReadEntry(nonSeekable);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first!.Value.Key.Value);
        Assert.Equal("first", first.Value.Value.Value);
        Assert.Equal(2, second!.Value.Key.Value);
        Assert.Equal("second", second.Value.Value.Value);
    }

    private static byte[] WithRecordTerminator(byte[] encodedEntry, string terminator)
    {
        var text = Encoding.UTF8.GetString(encodedEntry);
        Assert.EndsWith(Environment.NewLine, text);
        return Encoding.UTF8.GetBytes(text[..^Environment.NewLine.Length] + terminator);
    }

    // ── Cross-stream contamination (finding 018d161134f3) ────────────────────

    [Fact]
    public void ReadEntry_DoesNotLeakPeekedByteAcrossStreams()
    {
        // Regression test for finding 018d161134f3: _peekedByte is an instance field.
        // When ReadEntry is called on non-seekable stream A with a trailing bare \r
        // (causing a non-\n byte to be stashed in _peekedByte), then ReadEntry is called
        // on a DIFFERENT stream B, the stash from A is silently injected at the start of
        // B's parse — producing corrupt deserialization of B.
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Build stream A: one entry whose trailing \n is replaced by \r<first-byte-of-B>.
        // This leaves a stash of <first-byte-of-B> in _peekedByte after reading from A.
        using var bufA = new MemoryStream();
        serializer.WriteEntry(bufA, new SerializableWrapper<int>(1), new SerializableWrapper<string>("from-A"));
        var bytesA = WithRecordTerminator(bufA.ToArray(), "\r");

        // Build stream B: one valid entry.
        using var bufB = new MemoryStream();
        serializer.WriteEntry(bufB, new SerializableWrapper<int>(2), new SerializableWrapper<string>("from-B"));
        var bytesB = bufB.ToArray();

        // Append the first byte of bytesB after A's bare CR so that when stream A is read
        // the serializer peeks the first byte of B
        // and stashes it. We don't actually attach B here — we simulate the stash by ending A
        // with \r followed by one byte that will be stashed but NOT consumed.
        var stashBait = bytesB[0];       // the byte that will be stashed in _peekedByte
        var streamABytes = bytesA.Concat([stashBait]).ToArray();

        // Wrap in a non-seekable stream so the \r + peek path exercises _peekedByte.
        using var streamA = new NonSeekableStream(new MemoryStream(streamABytes));
        _ = serializer.ReadEntry(streamA); // seeds _peekedByte with stashBait

        // Now call ReadEntry on a completely independent stream B.
        // With the bug: the first byte consumed from B is _peekedByte (stashBait) instead
        // of bytesB[0], so the Base64 payload is truncated and the key/value decode fails
        // or returns the wrong values.
        using var streamB = new NonSeekableStream(new MemoryStream(bytesB));
        var result = serializer.ReadEntry(streamB);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Key.Value);       // must come from B, not A
        Assert.Equal("from-B", result.Value.Value.Value);
    }

    // ── helper: non-seekable wrapper ─────────────────────────────────────────

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override int ReadByte() => inner.ReadByte();
    }
}
