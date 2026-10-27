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
/// Tests for <see cref="InMemoryCheckpointStore"/>.
/// </summary>
public sealed class InMemoryCheckpointStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var store = new InMemoryCheckpointStore();
        await store.SaveAsync("proc-1", 42, new byte[] { 1, 2, 3 }).ConfigureAwait(true);

        var result = await store.LoadAsync("proc-1").ConfigureAwait(true);
        result!.Value.Offset.Should().Be(42);
        result.Value.State.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task LoadAsync_MissingProcessorId_ReturnsNull()
    {
        var store = new InMemoryCheckpointStore();
        var result = await store.LoadAsync("missing").ConfigureAwait(true);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Overwrite_ReturnsLatest()
    {
        var store = new InMemoryCheckpointStore();
        await store.SaveAsync("proc", 10, new byte[] { 1 }).ConfigureAwait(true);
        await store.SaveAsync("proc", 20, new byte[] { 2, 3 }).ConfigureAwait(true);

        var result = await store.LoadAsync("proc").ConfigureAwait(true);
        result!.Value.Offset.Should().Be(20);
        result.Value.State.Should().BeEquivalentTo(new byte[] { 2, 3 });
    }

    [Fact]
    public async Task MultipleProcessorIds_AreIndependent()
    {
        var store = new InMemoryCheckpointStore();
        await store.SaveAsync("a", 100, "\n"u8.ToArray()).ConfigureAwait(true);
        await store.SaveAsync("b", 200, new byte[] { 20 }).ConfigureAwait(true);

        var a = await store.LoadAsync("a").ConfigureAwait(true);
        var b = await store.LoadAsync("b").ConfigureAwait(true);
        a!.Value.Offset.Should().Be(100);
        b!.Value.Offset.Should().Be(200);
    }

    [Fact]
    public async Task EmptyState_IsValid()
    {
        var store = new InMemoryCheckpointStore();
        await store.SaveAsync("empty", 99, Array.Empty<byte>()).ConfigureAwait(true);

        var result = await store.LoadAsync("empty").ConfigureAwait(true);
        result!.Value.Offset.Should().Be(99);
        result.Value.State.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentSaves_SameProcessorId_LastWriterWins()
    {
        var store = new InMemoryCheckpointStore();
        var tasks = new Task[10];

        for (var i = 0; i < tasks.Length; i++)
        {
            var offset = (long)i;
            var state = new[] { (byte)i };
            tasks[i] = Task.Run(async () =>
            {
                await store.SaveAsync("concurrent", offset, state).ConfigureAwait(false);
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(true);
        var result = await store.LoadAsync("concurrent").ConfigureAwait(true);
        result.Should().NotBeNull();
    }

    /// <summary>
    /// Sibling contract parity: a processor id the file-backed store rejects (outside its
    /// safe charset) must be rejected by the in-memory sibling too — swapping the
    /// ICheckpointStore implementation must not change which ids are valid.
    /// </summary>
    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("id with spaces")]
    [InlineData("id:colon")]
    public async Task InvalidProcessorId_IsRejected_MatchingTheFileBackedSibling(string processorId)
    {
        var store = new InMemoryCheckpointStore();

        var save = async () => await store.SaveAsync(processorId, 1, Array.Empty<byte>()).ConfigureAwait(true);
        var load = async () => await store.LoadAsync(processorId).ConfigureAwait(true);

        await save.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        await load.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    // AC-8.8 (test gap): cancellation-token coverage for InMemoryCheckpointStore.
    [Fact]
    public async Task SaveAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var store = new InMemoryCheckpointStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await store.SaveAsync("p", 1, Array.Empty<byte>(), cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task LoadAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var store = new InMemoryCheckpointStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await store.LoadAsync("p", cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }
}
