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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the LogSegmentFile class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class LogSegmentFileTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogSegmentFileTests"/> class.
    /// </summary>
    /// <param name="output">The test output helper for logging test details.</param>
    public LogSegmentFileTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Test to ensure that the constructor throws an ArgumentNullException
    /// if the storageEngine parameter is null.
    /// </summary>
    [Fact]
    public void Constructor_ShouldThrowArgumentNullExceptionIfStorageEngineIsNull()
    {
        // Arrange, Act & Assert: Attempt to create a LogSegmentFile with a null storage engine.
        // Expect an ArgumentNullException to be thrown.
        Assert.Throws<ArgumentNullException>(() => new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(null!, 1024));
    }

    /// <summary>
    /// Test to ensure that the constructor correctly initializes a new instance
    /// of the LogSegmentFile class with valid parameters.
    /// </summary>
    [Fact]
    public void Constructor_ShouldInitializeInstanceWithValidParameters()
    {
        // Arrange: Create a storage file and serializer.
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();

        // Act: Initialize the LogSegmentFile.
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Assert: Verify that the instance was initialized correctly.
        Assert.NotNull(segmentFile);
    }

    /// <summary>
    /// Test to ensure that the SetAsync method correctly appends a key-value pair
    /// to the segment file when the file size is within the maximum segment size limit.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAppendKeyValuePairWithinSegmentSizeLimit()
    {
        // Arrange: Create a storage file and serializer.
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        storageEngineMock.SetupGet(se => se.FileSize).Returns(512); // File size within limit
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act: Write the key-value pair to the segment file.
        await segmentFile.SetAsync(key, value);

        // Assert: Verify that the SetAsync method of the storage engine was called.
        storageEngineMock.Verify(se => se.SetAsync(key, value, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test to ensure that the SetAsync method throws an InvalidOperationException
    /// if the segment size is exceeded.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldThrowExceptionIfSegmentSizeExceeded()
    {
        // Arrange: Create a storage file mock that exceeds the segment size.
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        storageEngineMock.SetupGet(se => se.FileSize).Returns(2048); // File size exceeds limit
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act & Assert: Check that an InvalidOperationException is thrown.
        await Assert.ThrowsAsync<InvalidOperationException>(() => segmentFile.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")));
    }

    /// <summary>
    /// Test to ensure that the SetAsync method appends a key-value pair
    /// to the segment file when called concurrently.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldHandleConcurrentCalls()
    {
        // Arrange: Create a storage file and serializer.
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        storageEngineMock.SetupGet(se => se.FileSize).Returns(512); // File size within limit
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        var key1 = new SerializableWrapper<int>(1);
        var value1 = new SerializableWrapper<string>("value1");
        var key2 = new SerializableWrapper<int>(2);
        var value2 = new SerializableWrapper<string>("value2");

        // Act: Write the key-value pairs to the segment file concurrently.
        var task1 = segmentFile.SetAsync(key1, value1);
        var task2 = segmentFile.SetAsync(key2, value2);
        await Task.WhenAll(task1, task2);

        // Assert: Verify that the SetAsync method of the storage engine was called for both keys.
        storageEngineMock.Verify(se => se.SetAsync(key1, value1, It.IsAny<CancellationToken>()), Times.Once);
        storageEngineMock.Verify(se => se.SetAsync(key2, value2, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test that TryGetValueAsync delegates to the underlying storage engine.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_DelegatesToStorageEngine()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        var key = new SerializableWrapper<int>(1);
        var expectedValue = new SerializableWrapper<string>("value1");
        storageEngineMock.Setup(se => se.TryGetValueAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((expectedValue, true));
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act
        var (value, found) = await segmentFile.TryGetValueAsync(key);

        // Assert
        Assert.True(found);
        Assert.Equal("value1", value.Value);
    }

    /// <summary>
    /// Test that ContainsKeyAsync delegates to the underlying storage engine.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_DelegatesToStorageEngine()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        var key = new SerializableWrapper<int>(42);
        storageEngineMock.Setup(se => se.ContainsKeyAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act
        var result = await segmentFile.ContainsKeyAsync(key);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Test that RemoveAsync delegates to the underlying storage engine.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_DelegatesToStorageEngine()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        var key = new SerializableWrapper<int>(1);
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act
        await segmentFile.RemoveAsync(key);

        // Assert
        storageEngineMock.Verify(se => se.RemoveAsync(key, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test that ClearAsync delegates to the underlying storage engine.
    /// </summary>
    [Fact]
    public async Task ClearAsync_DelegatesToStorageEngine()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act
        await segmentFile.ClearAsync();

        // Assert
        storageEngineMock.Verify(se => se.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test that FileName property delegates to the underlying storage engine.
    /// </summary>
    [Fact]
    public void FileName_DelegatesToStorageEngine()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        storageEngineMock.SetupGet(se => se.FileName).Returns("segment_001.dat");
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act & Assert
        Assert.Equal("segment_001.dat", segmentFile.FileName);
    }

    /// <summary>
    /// Test that FileLocation property delegates to the underlying storage engine.
    /// </summary>
    [Fact]
    public void FileLocation_DelegatesToStorageEngine()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        storageEngineMock.SetupGet(se => se.FileLocation).Returns("/tmp/segments");
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act & Assert
        Assert.Equal("/tmp/segments", segmentFile.FileLocation);
    }

    /// <summary>
    /// Test that SetAsync succeeds at exactly the boundary (fileSize == maxSegmentSize - 1).
    /// </summary>
    [Fact]
    public async Task SetAsync_AtBoundary_JustBelowLimit_Succeeds()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        storageEngineMock.SetupGet(se => se.FileSize).Returns(1023); // One byte below limit
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act — should succeed because 1023 < 1024
        await segmentFile.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"));

        // Assert
        storageEngineMock.Verify(se => se.SetAsync(It.IsAny<SerializableWrapper<int>>(), It.IsAny<SerializableWrapper<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test that SetAsync throws when file size is exactly at the limit.
    /// </summary>
    [Fact]
    public async Task SetAsync_AtExactLimit_ThrowsException()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        storageEngineMock.SetupGet(se => se.FileSize).Returns(1024); // Exactly at limit
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act & Assert — fileSize >= maxSegmentSize triggers the exception
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => segmentFile.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v")));
    }

    /// <summary>
    /// Test that GetAllItemsAsync delegates to the underlying storage engine.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_DelegatesToStorageEngine()
    {
        // Arrange
        var storageEngineMock = new Mock<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        var items = new List<(SerializableWrapper<int>, SerializableWrapper<string>)>
        {
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("a")),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("b")),
        };
        storageEngineMock.Setup(se => se.GetAllItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(items.AsEnumerable());
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageEngineMock.Object, 1024);

        // Act
        var result = (await segmentFile.GetAllItemsAsync()).ToList();

        // Assert
        Assert.Equal(2, result.Count);
    }
}
