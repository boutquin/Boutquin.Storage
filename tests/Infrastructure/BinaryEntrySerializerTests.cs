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

        // Assert: Verify that the stream contains the expected serialized key-value pair.
        memoryStream.Position = 0;
        var reader = new BinaryReader(memoryStream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("value1", reader.ReadString());
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
    public void ReadEntry_ShouldReadKeyValuePair()
    {
        // Arrange: Create a memory stream with a serialized key-value pair.
        using var memoryStream = new MemoryStream();
        var writer = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true);
        writer.Write(1);
        writer.Write("value1");
        writer.Flush();
        memoryStream.Position = 0;

        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

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
    private class ExceptionSerializableWrapper<T> : ISerializable<ExceptionSerializableWrapper<T>>, IComparable<ExceptionSerializableWrapper<T>>
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
}