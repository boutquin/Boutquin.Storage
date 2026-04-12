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
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests.Streaming;

/// <summary>
/// Tests for <see cref="FileCheckpointStore"/>.
/// </summary>
public sealed class FileCheckpointStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly FileCheckpointStore _store;

    public FileCheckpointStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "checkpoint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new FileCheckpointStore(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        // Arrange
        var processorId = "processor-1";
        var offset = 42L;
        var state = new byte[] { 1, 2, 3, 4 };

        // Act
        await _store.SaveAsync(processorId, offset, state).ConfigureAwait(true);
        var result = await _store.LoadAsync(processorId).ConfigureAwait(true);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Offset.Should().Be(offset);
        result.Value.State.Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task LoadAsync_MissingProcessorId_ReturnsNull()
    {
        var result = await _store.LoadAsync("nonexistent").ConfigureAwait(true);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Overwrite_ReturnsLatest()
    {
        // Arrange
        var processorId = "overwrite-test";
        await _store.SaveAsync(processorId, 10, new byte[] { 1 }).ConfigureAwait(true);
        await _store.SaveAsync(processorId, 20, new byte[] { 2, 3 }).ConfigureAwait(true);

        // Act
        var result = await _store.LoadAsync(processorId).ConfigureAwait(true);

        // Assert
        result!.Value.Offset.Should().Be(20);
        result.Value.State.Should().BeEquivalentTo(new byte[] { 2, 3 });
    }

    [Fact]
    public async Task MultipleProcessorIds_AreIndependent()
    {
        // Arrange
        await _store.SaveAsync("proc-a", 100, new byte[] { 10 }).ConfigureAwait(true);
        await _store.SaveAsync("proc-b", 200, new byte[] { 20 }).ConfigureAwait(true);

        // Act
        var a = await _store.LoadAsync("proc-a").ConfigureAwait(true);
        var b = await _store.LoadAsync("proc-b").ConfigureAwait(true);

        // Assert
        a!.Value.Offset.Should().Be(100);
        b!.Value.Offset.Should().Be(200);
    }

    [Fact]
    public async Task EmptyState_IsValid()
    {
        // Arrange — offset-only checkpoint
        await _store.SaveAsync("empty-state", 99, ReadOnlyMemory<byte>.Empty).ConfigureAwait(true);

        // Act
        var result = await _store.LoadAsync("empty-state").ConfigureAwait(true);

        // Assert
        result!.Value.Offset.Should().Be(99);
        result.Value.State.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentSaves_SameProcessorId_LastWriterWins()
    {
        // Arrange
        var processorId = "concurrent";
        var tasks = new Task[10];

        for (var i = 0; i < tasks.Length; i++)
        {
            var offset = (long)i;
            var state = new[] { (byte)i };
            tasks[i] = Task.Run(async () =>
            {
                await _store.SaveAsync(processorId, offset, state).ConfigureAwait(false);
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Assert — file exists and is readable (last writer wins)
        var result = await _store.LoadAsync(processorId).ConfigureAwait(true);
        result.Should().NotBeNull();
    }
}
