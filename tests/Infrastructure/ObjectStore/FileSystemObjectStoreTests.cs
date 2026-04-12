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
using Boutquin.Storage.Infrastructure.ObjectStore;

namespace Boutquin.Storage.Infrastructure.Tests.ObjectStore;

/// <summary>
/// Tests for <see cref="FileSystemObjectStore"/>.
/// Each test uses an isolated temp directory to prevent cross-test interference.
/// </summary>
public sealed class FileSystemObjectStoreTests : IDisposable
{
    private readonly string _rootDir;
    private readonly FileSystemObjectStore _store;

    public FileSystemObjectStoreTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "objstore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _store = new FileSystemObjectStore(_rootDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAndRead_RoundTrip_ReturnsOriginalContent()
    {
        // Arrange
        var key = "test-key.dat";
        var expected = "Hello, Object Store!"u8.ToArray();

        // Act
        await using (var writeStream = new MemoryStream(expected))
        {
            await _store.WriteAsync(key, writeStream).ConfigureAwait(true);
        }

        await using var readStream = await _store.ReadAsync(key).ConfigureAwait(true);

        // Assert
        readStream.Should().NotBeNull();
        var actual = new byte[expected.Length];
        var bytesRead = await readStream!.ReadAsync(actual).ConfigureAwait(true);
        bytesRead.Should().Be(expected.Length);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueAfterWrite_FalseBeforeAndAfterDelete()
    {
        // Arrange
        var key = "exists-test.dat";

        // Assert — before write
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeFalse();

        // Act — write
        await using (var stream = new MemoryStream([1, 2, 3]))
        {
            await _store.WriteAsync(key, stream).ConfigureAwait(true);
        }

        // Assert — after write
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();

        // Act — delete
        await _store.DeleteAsync(key).ConfigureAwait(true);

        // Assert — after delete
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_OnMissingKey_DoesNotThrow()
    {
        // Act & Assert — should be a no-op
        var act = () => _store.DeleteAsync("nonexistent-key").AsTask();
        await act.Should().NotThrowAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task HierarchicalKeys_CreatesNestedDirectories()
    {
        // Arrange
        var key = "data/2026/AAPL.json";
        var content = "{\"price\": 150.00}"u8.ToArray();

        // Act
        await using (var stream = new MemoryStream(content))
        {
            await _store.WriteAsync(key, stream).ConfigureAwait(true);
        }

        // Assert — file exists at nested path
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();
        var filePath = Path.Combine(_rootDir, "data", "2026", "AAPL.json");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentWrites_SameKey_NoCorruption()
    {
        // Arrange
        var key = "concurrent.dat";
        var tasks = new Task[10];
        var lastContent = Array.Empty<byte>();

        // Act — write concurrently with different content
        for (var i = 0; i < tasks.Length; i++)
        {
            var data = Encoding.UTF8.GetBytes($"writer-{i}");
            if (i == tasks.Length - 1)
            {
                lastContent = data;
            }

            tasks[i] = Task.Run(async () =>
            {
                await using var stream = new MemoryStream(data);
                await _store.WriteAsync(key, stream).ConfigureAwait(false);
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Assert — file exists and is not corrupted (readable, non-zero length)
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();
        await using var readStream = await _store.ReadAsync(key).ConfigureAwait(true);
        readStream.Should().NotBeNull();
        using var ms = new MemoryStream();
        await readStream!.CopyToAsync(ms).ConfigureAwait(true);
        ms.Length.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrEmptyKey_ThrowsArgumentException(string? key)
    {
        // Act & Assert
        var act = () => _store.ExistsAsync(key!).AsTask();
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadAsync_MissingKey_ReturnsNull()
    {
        // Act
        var result = await _store.ReadAsync("no-such-key.dat").ConfigureAwait(true);

        // Assert
        result.Should().BeNull();
    }
}
