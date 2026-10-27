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
namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Pins terminal-flush-fault behavior while an ingest waiter is parked behind a saturated
/// immutable queue. The test format exposes exact coordination points; no timing delay is used
/// to create the interleaving.
/// </summary>
public sealed class LsmFlushFaultRaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmFlushFault_{Guid.NewGuid():N}");

    public LsmFlushFaultRaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task FlushAsync_WaitingForSaturatedIngestGate_UnblocksWhenFlusherFaults()
    {
        var format = new BlockingThrowingSegmentFormat();
        var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1,
            segmentFolder: _dir,
            segmentPrefix: "fault",
            segmentFormat: format,
            maxImmutableMemtables: 1);
        var completed = false;

        try
        {
            await engine.SetAsync(new(1), new("one"));
            await engine.SetAsync(new(2), new("two"));
            await format.WriterEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The only ingest permit belongs to the immutable currently blocked in WriteAsync.
            // FlushAsync runs synchronously to its first incomplete await, so when this call
            // returns a Task it is parked on that saturated gate.
            var flush = engine.FlushAsync();

            format.ThrowNow.TrySetResult();

            await Assert.ThrowsAsync<IOException>(
                () => flush.WaitAsync(TimeSpan.FromSeconds(10)));
            completed = true;
        }
        finally
        {
            // On the RED implementation Dispose can inherit the same deadlock as the flush.
            // Skip teardown only on that failing path so the bounded test reports the real
            // timeout instead of hanging during cleanup. The GREEN path always disposes.
            if (completed)
            {
                await engine.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(BlockedOperation.Set)]
    [InlineData(BlockedOperation.SetBulk)]
    [InlineData(BlockedOperation.Remove)]
    public async Task IngestWaiter_UnblocksWhenFlusherFaults(BlockedOperation operation)
    {
        var format = new BlockingThrowingSegmentFormat();
        var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1,
            segmentFolder: _dir,
            segmentPrefix: operation.ToString(),
            segmentFormat: format,
            maxImmutableMemtables: 1);

        await engine.SetAsync(new(1), new("one"));
        await engine.SetAsync(new(2), new("two"));
        await format.WriterEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var pending = operation switch
        {
            BlockedOperation.Set => engine.SetAsync(new(3), new("three")),
            BlockedOperation.SetBulk => engine.SetBulkAsync(
            [
                new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(new(3), new("three")),
            ]),
            BlockedOperation.Remove => engine.RemoveAsync(new(3)),
            _ => throw new InvalidOperationException($"Unknown operation {operation}."),
        };

        format.ThrowNow.TrySetResult();

        await Assert.ThrowsAsync<IOException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_UnblocksIngestWaiterAndCancelsBlockedFlusher()
    {
        var format = new BlockingThrowingSegmentFormat();
        var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1,
            segmentFolder: _dir,
            segmentPrefix: "dispose",
            segmentFormat: format,
            maxImmutableMemtables: 1);

        await engine.SetAsync(new(1), new("one"));
        await engine.SetAsync(new(2), new("two"));
        await format.WriterEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var waitingIngest = engine.SetAsync(new(3), new("three"));
        var dispose = engine.DisposeAsync().AsTask();

        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => waitingIngest.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class BlockingThrowingSegmentFormat
        : ISegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        private readonly SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>> _inner = new();

        internal TaskCompletionSource WriterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ThrowNow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string FileExtension => _inner.FileExtension;
        public bool SupportsSnapshots => _inner.SupportsSnapshots;

        public async Task<ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>>> WriteAsync(
            string segmentFolder,
            string segmentFileName,
            IReadOnlyList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> sortedEntries,
            IReadOnlyDictionary<SerializableWrapper<int>, ulong>? tombstones = null,
            CancellationToken cancellationToken = default)
        {
            WriterEntered.TrySetResult();
            await ThrowNow.Task.WaitAsync(cancellationToken);
            throw new IOException("injected blocked segment-write failure");
        }

        public ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>> Open(
            string segmentFolder,
            string segmentFileName) => _inner.Open(segmentFolder, segmentFileName);
    }

    public enum BlockedOperation
    {
        Set,
        SetBulk,
        Remove,
    }
}
