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

        // Assert: Verify that the stream contains the expected CSV formatted key-value pair.
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var csvLine = await reader.ReadLineAsync();

        // Expected format is based on the serialization logic of SerializableWrapper
        var expectedKey = Convert.ToChar(1).ToString() + "\0\0\0";
        var expectedValue = Convert.ToChar(6).ToString() + "value1";

        // Build the expected CSV string
        var expectedCsvLine = $"{expectedKey},{expectedValue}";

        Assert.Equal(expectedCsvLine, csvLine);
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
        Assert.Equal("Parameter 'stream' cannot be null. (Parameter 'stream')", exception.Message);
    }

    /// <summary>
    /// Test to ensure that ReadEntry correctly reads a key-value pair from the stream.
    /// </summary>
    [Fact]
    public void ReadEntry_ShouldReadKeyValuePairFromStream()
    {
        // Arrange: Create a memory stream with a CSV formatted key-value pair, and serializer.
        _ = new SerializableWrapper<int>(1);
        _ = new SerializableWrapper<string>("value1");

        var expectedKey = Convert.ToChar(1).ToString() + "\0\0\0";
        var expectedValue = Convert.ToChar(6).ToString() + "value1";
        var csvData = $"{expectedKey},{expectedValue}\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvData));
        var serializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

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
        Assert.Equal("Parameter 'stream' cannot be null. (Parameter 'stream')", exception.Message);
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
        Assert.Equal("Parameter 'stream' cannot be null. (Parameter 'stream')", exception.Message);
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
        var exception = Assert.Throws<DeserializationException>(() => serializer.ReadEntry(stream));
        Assert.Equal("Error occurred while reading entry from CSV.", exception.Message);
    }
}
