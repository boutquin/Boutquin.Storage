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

    /// <summary>
    /// Processor ids differing only in case are DISTINCT processors and must keep
    /// independent checkpoints — on a case-insensitive filesystem (default macOS APFS,
    /// NTFS) a plain id-as-filename mapping folds them onto one file, so the second
    /// processor's save silently clobbers the first's resume offset.
    /// </summary>
    [Fact]
    public async Task ProcessorIds_DifferingOnlyInCase_KeepIndependentCheckpoints()
    {
        await _store.SaveAsync("OrderProcessor", 5000, Array.Empty<byte>()).ConfigureAwait(true);
        await _store.SaveAsync("orderprocessor", 1, Array.Empty<byte>()).ConfigureAwait(true);

        var upper = await _store.LoadAsync("OrderProcessor").ConfigureAwait(true);
        var lower = await _store.LoadAsync("orderprocessor").ConfigureAwait(true);

        upper!.Value.Offset.Should().Be(5000,
            "case-distinct ids are distinct processors; the filename mapping must stay injective under case folding");
        lower!.Value.Offset.Should().Be(1);
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
        await _store.SaveAsync("proc-a", 100, "\n"u8.ToArray()).ConfigureAwait(true);
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
        await _store.SaveAsync("empty-state", 99, Array.Empty<byte>()).ConfigureAwait(true);

        // Act
        var result = await _store.LoadAsync("empty-state").ConfigureAwait(true);

        // Assert
        result!.Value.Offset.Should().Be(99);
        result.Value.State.Should().BeEmpty();
    }

    // AC-8.5 (F-72): flush-sequencing characterization — SaveAsync must call Flush(flushToDisk:true)
    // BEFORE dispose so the checkpoint file is durable when SaveAsync returns. A new
    // FileCheckpointStore instance (bypassing OS page-cache that a same-instance LoadAsync
    // might use) must read back the checkpoint.
    [Fact]
    public async Task SaveAsync_FlushSequencing_NewInstanceCanLoad()
    {
        var processorId = "flush-seq-test";
        var offset = 12345L;
        var state = new byte[] { 0xAB, 0xCD, 0xEF };

        await _store.SaveAsync(processorId, offset, state).ConfigureAwait(true);

        // Fresh instance — same directory, same file, no OS-cache reuse from _store.
        var freshStore = new FileCheckpointStore(_dir);
        var result = await freshStore.LoadAsync(processorId).ConfigureAwait(true);

        result.Should().NotBeNull("SaveAsync must flush and close before returning");
        result!.Value.Offset.Should().Be(offset);
        result.Value.State.Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task ConcurrentSaves_SameProcessorId_LastWriterWins()
    {
        var processorId = "concurrent";
        var tasks = new Task[10];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < tasks.Length; i++)
        {
            var offset = (long)i;
            var state = new[] { (byte)i, (byte)(i ^ 0xFF) };
            var writer = new FileCheckpointStore(_dir);
            tasks[i] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(true);
                await writer.SaveAsync(processorId, offset, state).ConfigureAwait(true);
            });
        }

        start.SetResult();
        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Publication must choose one whole record, even across distinct store instances.
        var result = await _store.LoadAsync(processorId).ConfigureAwait(true);
        result.Should().NotBeNull();
        result!.Value.Offset.Should().BeInRange(0, tasks.Length - 1);
        result.Value.State.Should().Equal((byte)result.Value.Offset, (byte)(result.Value.Offset ^ 0xFF));

        await _store.SaveAsync(processorId, 100, [0xCA, 0xFE]).ConfigureAwait(true);
        var final = await new FileCheckpointStore(_dir).LoadAsync(processorId).ConfigureAwait(true);
        final!.Value.Offset.Should().Be(100, "a write after all contenders must win deterministically");
        final.Value.State.Should().Equal(0xCA, 0xFE);
    }

    // AC-8.8 (test gap): cancellation-token coverage for FileCheckpointStore.
    [Fact]
    public async Task SaveAsync_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _store.SaveAsync("p", 1, Array.Empty<byte>(), cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task LoadAsync_CancelledToken_ThrowsOperationCanceled()
    {
        // Save first so the file exists; LoadAsync checks the token before reading.
        await _store.SaveAsync("p", 1, Array.Empty<byte>()).ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _store.LoadAsync("p", cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }
}
