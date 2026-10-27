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
/// This class contains unit tests for the BinaryEntrySerializer class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class BinaryEntrySerializerTests
{
    /// <summary>
    /// Test to ensure that WriteEntryAsync correctly writes a key-value pair to the stream.
    /// </summary>
    [Fact]
    public async Task WriteEntryAsync_ShouldWriteKeyValuePair()
    {
        // Arrange: Create a memory stream to write to and a serializer.
        using var memoryStream = new MemoryStream();
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act: Write the key-value pair to the stream.
        await serializer.WriteEntryAsync(memoryStream, key, value);

        // Assert: round-trip the written bytes through the same serializer and compare values.
        // Raw-byte assertions would couple this test to the wrapper's internal wire format
        // (tagged wire in 2.0.0); the serializer contract we care about is "read-your-writes".
        memoryStream.Position = 0;
        var entry = serializer.ReadEntry(memoryStream);
        Assert.NotNull(entry);
        Assert.Equal(key.Value, entry.Value.Key.Value);
        Assert.Equal(value.Value, entry.Value.Value.Value);
    }

    /// <summary>
    /// Test to ensure that WriteEntryAsync throws ArgumentNullException when stream is null.
    /// </summary>
    [Fact]
    public async Task WriteEntryAsync_ShouldThrowArgumentNullException_WhenStreamIsNull()
    {
        // Arrange: Create a serializer with a null stream.
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => serializer.WriteEntryAsync(null, key, value));
    }

    /// <summary>
    /// Test to ensure that WriteEntryAsync throws ArgumentNullException when key is null.
    /// </summary>
    [Fact]
    public async Task WriteEntryAsync_ShouldThrowArgumentNullException_WhenKeyIsNull()
    {
        // Arrange: Create a memory stream and a serializer.
        using var memoryStream = new MemoryStream();
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        SerializableWrapper<int> key = null;
        var value = new SerializableWrapper<string>("value1");

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => serializer.WriteEntryAsync(memoryStream, key, value));
    }

    /// <summary>
    /// Test to ensure that WriteEntryAsync throws ArgumentNullException when value is null.
    /// </summary>
    [Fact]
    public async Task WriteEntryAsync_ShouldThrowArgumentNullException_WhenValueIsNull()
    {
        // Arrange: Create a memory stream and a serializer.
        using var memoryStream = new MemoryStream();
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(1);
        SerializableWrapper<string> value = null;

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => serializer.WriteEntryAsync(memoryStream, key, value));
    }

    /// <summary>
    /// Test to ensure that ReadEntry correctly reads a key-value pair from the stream.
    /// </summary>
    [Fact]
    public async Task ReadEntry_ShouldReadKeyValuePair()
    {
        // Arrange: write the entry via the same serializer's write path, then re-read. Using
        // the wrapper's 2.0.0 tagged-wire Serialize directly (or a raw BinaryWriter pre-seed)
        // would couple this test to the wrapper's internal wire format; round-tripping via
        // WriteEntryAsync exercises the serializer's published contract.
        using var memoryStream = new MemoryStream();
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        await serializer.WriteEntryAsync(memoryStream, new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        memoryStream.Position = 0;

        // Act: Read the key-value pair from the stream.
        var entry = serializer.ReadEntry(memoryStream);

        // Assert: Verify that the key-value pair was read correctly.
        Assert.NotNull(entry);
        Assert.Equal(1, entry.Value.Key.Value);
        Assert.Equal("value1", entry.Value.Value.Value);
    }

    /// <summary>
    /// Test to ensure that ReadEntry returns null when the stream is empty.
    /// </summary>
    [Fact]
    public void ReadEntry_ShouldReturnNull_WhenStreamIsEmpty()
    {
        // Arrange: Create an empty memory stream and a serializer.
        using var memoryStream = new MemoryStream();
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Try to read a key-value pair from the empty stream.
        var entry = serializer.ReadEntry(memoryStream);

        // Assert: Verify that null is returned.
        Assert.Null(entry);
    }

    /// <summary>
    /// Test to ensure that ReadEntry throws ArgumentNullException when stream is null.
    /// </summary>
    [Fact]
    public void ReadEntry_ShouldThrowArgumentNullException_WhenStreamIsNull()
    {
        // Arrange: Create a serializer with a null stream.
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        Assert.Throws<ArgumentNullException>(() => serializer.ReadEntry(null));
    }

    /// <summary>
    /// Test to ensure that CanRead returns true when the stream can be read.
    /// </summary>
    [Fact]
    public void CanRead_ShouldReturnTrue_WhenStreamCanBeRead()
    {
        // Arrange: Create a memory stream with data and a serializer.
        using var memoryStream = new MemoryStream();
        var writer = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true);
        writer.Write(1);
        writer.Write("value1");
        writer.Flush();
        memoryStream.Position = 0;

        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Check if the stream can be read.
        var canRead = serializer.CanRead(memoryStream);

        // Assert: Verify that the stream can be read.
        Assert.True(canRead);
    }

    /// <summary>
    /// Test to ensure that CanRead returns false when the stream cannot be read.
    /// </summary>
    [Fact]
    public void CanRead_ShouldReturnFalse_WhenStreamCannotBeRead()
    {
        // Arrange: Create an empty memory stream and a serializer.
        using var memoryStream = new MemoryStream();
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Check if the stream can be read.
        var canRead = serializer.CanRead(memoryStream);

        // Assert: Verify that the stream cannot be read.
        Assert.False(canRead);
    }

    /// <summary>
    /// Test to ensure that CanRead throws ArgumentNullException when stream is null.
    /// </summary>
    [Fact]
    public void CanRead_ShouldThrowArgumentNullException_WhenStreamIsNull()
    {
        // Arrange: Create a serializer with a null stream.
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        Assert.Throws<ArgumentNullException>(() => serializer.CanRead(null));
    }

    /// <summary>
    /// Test to ensure that WriteEntryAsync throws OperationCanceledException when cancellation is requested.
    /// </summary>
    [Fact]
    public async Task WriteEntryAsync_ShouldThrowOperationCanceledException_WhenCancellationIsRequested()
    {
        // Arrange: Create a memory stream, a serializer, and a cancellation token.
        using var memoryStream = new MemoryStream();
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");
        var cancellationToken = new CancellationToken(true); // Cancellation requested

        // Act & Assert: Check that an OperationCanceledException is thrown.
        await Assert.ThrowsAsync<OperationCanceledException>(() => serializer.WriteEntryAsync(memoryStream, key, value, cancellationToken));
    }

    /// <summary>
    /// Custom mock class that implements ISerializable and throws an exception during serialization.
    /// </summary>
    private sealed class ExceptionSerializableWrapper<T> : IStorageKey<ExceptionSerializableWrapper<T>>
    {
        public T Value { get; set; }

        public ExceptionSerializableWrapper() { }

        public ExceptionSerializableWrapper(T value)
        {
            Value = value;
        }

        public void Serialize(Stream stream)
        {
            throw new InvalidOperationException("Serialization exception");
        }

        public static ExceptionSerializableWrapper<T> Deserialize(Stream stream)
        {
            return new ExceptionSerializableWrapper<T>();
        }

        public int CompareTo(ExceptionSerializableWrapper<T> other)
        {
            return Comparer<T>.Default.Compare(Value, other.Value);
        }

        public bool Equals(ExceptionSerializableWrapper<T> other)
            => other is not null && EqualityComparer<T>.Default.Equals(Value, other.Value);

        public override bool Equals(object obj)
            => obj is ExceptionSerializableWrapper<T> other && Equals(other);

        public override int GetHashCode()
            => Value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(Value);
    }

    /// <summary>
    /// Test to ensure that WriteEntryAsync correctly handles an exception during serialization.
    /// </summary>
    [Fact]
    public async Task WriteEntryAsync_ShouldHandleSerializationException()
    {
        // Arrange: Create a key that throws an exception during serialization.
        var key = new ExceptionSerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");
        using var memoryStream = new MemoryStream();
        var serializer = new BinaryEntrySerializer<ExceptionSerializableWrapper<int>, SerializableWrapper<string>>();

        // Act & Assert: Check that the exception is propagated.
        await Assert.ThrowsAsync<InvalidOperationException>(() => serializer.WriteEntryAsync(memoryStream, key, value));
    }

    [Fact]
    public void WriteEntry_ShouldWriteKeyValuePair_RoundTrippable()
    {
        using var ms = new MemoryStream();
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(42);
        var value = new SerializableWrapper<string>("hello");

        serializer.WriteEntry(ms, key, value);
        ms.Position = 0;
        var entry = serializer.ReadEntry(ms);

        Assert.NotNull(entry);
        Assert.Equal(key.Value, entry.Value.Key.Value);
        Assert.Equal(value.Value, entry.Value.Value.Value);
    }

    [Fact]
    public void WriteEntry_NullStream_ThrowsArgumentNullException()
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("v");

        Assert.Throws<ArgumentNullException>(() => serializer.WriteEntry(null!, key, value));
    }

    [Fact]
    public void WriteEntry_NullKey_ThrowsArgumentNullException()
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var ms = new MemoryStream();
        var value = new SerializableWrapper<string>("v");

        Assert.Throws<ArgumentNullException>(() => serializer.WriteEntry(ms, null!, value));
    }

    [Fact]
    public void WriteEntry_NullValue_ThrowsArgumentNullException()
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var ms = new MemoryStream();
        var key = new SerializableWrapper<int>(1);

        Assert.Throws<ArgumentNullException>(() => serializer.WriteEntry(ms, key, null!));
    }

    /// <summary>
    /// The two <c>IEntrySerializer</c> implementations must share one exception contract: the
    /// CSV sibling wraps every parse fault as <see cref="DeserializationException"/>, so a torn
    /// entry (crash mid-append) read through the binary serializer must surface the same domain
    /// type, not a raw <see cref="EndOfStreamException"/> — callers switching serializers must
    /// not need format-specific corruption handling.
    /// </summary>
    [Fact]
    public void ReadEntry_TornEntry_ThrowsDeserializationException()
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var full = new MemoryStream();
        serializer.WriteEntry(full, new SerializableWrapper<int>(1), new SerializableWrapper<string>("hello"));
        var bytes = full.ToArray();

        // Truncate mid-entry — a crash-torn final record.
        using var torn = new MemoryStream(bytes.AsSpan(0, bytes.Length - 2).ToArray());

        var act = () => serializer.ReadEntry(torn);
        act.Should().Throw<DeserializationException>(
            "a torn entry is a parse fault and both entry serializers advertise the same domain exception contract");
    }

    /// <summary>
    /// The binary wire is not self-delimiting, so <c>CanRead</c> genuinely requires a seekable
    /// stream — but the limitation must surface as a CLEAR contract statement naming the type,
    /// not as the BCL's opaque "Stream does not support seeking" from the Position getter.
    /// </summary>
    [Fact]
    public void CanRead_NonSeekableStream_ThrowsWithClearContractMessage()
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var nonSeekable = new NonSeekableReadStream(new byte[4]);

        var act = () => serializer.CanRead(nonSeekable);
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*BinaryEntrySerializer*seekable*");
    }

    private sealed class NonSeekableReadStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
