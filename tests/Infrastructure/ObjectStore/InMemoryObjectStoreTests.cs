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
/// Tests for <see cref="InMemoryObjectStore"/>.
/// </summary>
public sealed class InMemoryObjectStoreTests
{
    [Fact]
    public async Task WriteAndRead_RoundTrip_ReturnsOriginalContent()
    {
        // Arrange
        var store = new InMemoryObjectStore();
        var key = "test-key";
        var expected = "Hello, InMemory!"u8.ToArray();

        // Act
        await using (var writeStream = new MemoryStream(expected))
        {
            await store.WriteAsync(key, writeStream).ConfigureAwait(true);
        }

        await using var readStream = await store.ReadAsync(key).ConfigureAwait(true);

        // Assert
        readStream.Should().NotBeNull();
        using var ms = new MemoryStream();
        await readStream!.CopyToAsync(ms).ConfigureAwait(true);
        ms.ToArray().Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueAfterWrite_FalseBeforeAndAfterDelete()
    {
        // Arrange
        var store = new InMemoryObjectStore();
        var key = "exists-key";

        // Assert — before
        (await store.ExistsAsync(key).ConfigureAwait(true)).Should().BeFalse();

        // Write
        await using (var stream = new MemoryStream([42]))
        {
            await store.WriteAsync(key, stream).ConfigureAwait(true);
        }

        (await store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();

        // Delete
        await store.DeleteAsync(key).ConfigureAwait(true);
        (await store.ExistsAsync(key).ConfigureAwait(true)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_OnMissingKey_DoesNotThrow()
    {
        var store = new InMemoryObjectStore();
        var act = () => store.DeleteAsync("missing").AsTask();
        await act.Should().NotThrowAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task IndependentReadStreams_DifferentPositions()
    {
        // Arrange
        var store = new InMemoryObjectStore();
        var key = "independent";
        var data = new byte[] { 1, 2, 3, 4, 5 };

        await using (var ws = new MemoryStream(data))
        {
            await store.WriteAsync(key, ws).ConfigureAwait(true);
        }

        // Act — read twice
        await using var stream1 = await store.ReadAsync(key).ConfigureAwait(true);
        await using var stream2 = await store.ReadAsync(key).ConfigureAwait(true);

        // Advance stream1
        var buffer = new byte[3];
        await stream1!.ReadAsync(buffer).ConfigureAwait(true);

        // Assert — stream2 position is still 0
        stream2!.Position.Should().Be(0);
        stream1.Position.Should().Be(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrEmptyKey_ThrowsArgumentException(string? key)
    {
        var store = new InMemoryObjectStore();
        var act = () => store.ExistsAsync(key!).AsTask();
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadAsync_MissingKey_ReturnsNull()
    {
        var store = new InMemoryObjectStore();
        var result = await store.ReadAsync("no-such-key").ConfigureAwait(true);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentWrites_SameKey_NoCorruption()
    {
        // Arrange
        var store = new InMemoryObjectStore();
        var key = "concurrent";
        var tasks = new Task[10];

        for (var i = 0; i < tasks.Length; i++)
        {
            var data = Encoding.UTF8.GetBytes($"writer-{i}");
            tasks[i] = Task.Run(async () =>
            {
                await using var stream = new MemoryStream(data);
                await store.WriteAsync(key, stream).ConfigureAwait(false);
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Assert — key exists and is readable
        (await store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();
        await using var readStream = await store.ReadAsync(key).ConfigureAwait(true);
        readStream.Should().NotBeNull();
        using var ms = new MemoryStream();
        await readStream!.CopyToAsync(ms).ConfigureAwait(true);
        ms.Length.Should().BeGreaterThan(0);
    }
}
