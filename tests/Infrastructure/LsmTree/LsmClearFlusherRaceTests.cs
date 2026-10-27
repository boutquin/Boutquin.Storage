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
using System.Collections.Concurrent;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// ClearAsync drains in-flight flushes before discarding the immutable queue, but a seal can
/// land in the window between the drain and the lock acquisition — and the flusher can pick
/// that slot up and start writing it. ClearAsync then discards the mid-flush slot, and the
/// flusher's commit finds its slot gone from the queue. That is a benign consequence of a
/// legal concurrent call sequence, not an invariant violation: the commit must treat it as a
/// cancellation (deleting its now-orphan segment file), never terminally fault the engine.
/// </summary>
public sealed class LsmClearFlusherRaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmClearRace_{Guid.NewGuid():N}");

    public LsmClearFlusherRaceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    /// <summary>
    /// Re-armable blocking wrapper: each <see cref="Arm"/> makes one subsequent WriteAsync
    /// signal its entry and park until released.
    /// </summary>
    private sealed class BlockableSegmentFormat : ISegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        private readonly SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>> _inner = new();
        private readonly ConcurrentQueue<(TaskCompletionSource Entered, TaskCompletionSource Release)> _blocks = new();

        public (TaskCompletionSource Entered, TaskCompletionSource Release) Arm()
        {
            var pair = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _blocks.Enqueue(pair);
            return pair;
        }

        public string FileExtension => _inner.FileExtension;
        public bool SupportsSnapshots => _inner.SupportsSnapshots;

        public async Task<ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>>> WriteAsync(
            string segmentFolder,
            string segmentFileName,
            IReadOnlyList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> sortedEntries,
            IReadOnlyDictionary<SerializableWrapper<int>, ulong>? tombstones = null,
            CancellationToken cancellationToken = default)
        {
            if (_blocks.TryDequeue(out var pair))
            {
                pair.Entered.TrySetResult();
                await pair.Release.Task.ConfigureAwait(false);
            }
            return await _inner.WriteAsync(segmentFolder, segmentFileName, sortedEntries, tombstones, cancellationToken)
                .ConfigureAwait(false);
        }

        public ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>> Open(
            string segmentFolder, string segmentFileName)
            => _inner.Open(segmentFolder, segmentFileName);
    }

    [Fact]
    public async Task ClearAsync_RacingFlusherCommit_DoesNotFaultTheEngine()
    {
        var format = new BlockableSegmentFormat();
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _dir,
            segmentPrefix: "seg",
            segmentFormat: format);

        // Park ClearAsync in its drain→lock window (one-shot hook).
        var clearParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookFired = 0;
        engine.ClearDrainToLockHook = async () =>
        {
            if (Interlocked.Exchange(ref hookFired, 1) == 0)
            {
                clearParked.TrySetResult();
                await clearResume.Task.ConfigureAwait(false);
            }
        };

        var clearTask = engine.ClearAsync();
        await clearParked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Inject a seal while ClearAsync is parked, and freeze the flusher mid-write on it.
        var block = format.Arm();
        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        await engine.SetAsync(K(3), V("c")); // seals; flusher picks the slot up
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // ClearAsync now acquires the lock and discards the mid-flush slot.
        clearResume.TrySetResult();
        await clearTask;

        // Unfreeze the flusher: its commit finds the slot gone. This must be benign.
        block.Release.TrySetResult();

        // Give the flusher a moment to run its commit path.
        await Task.Delay(200);

        // The engine must remain fully operational — a legal Clear + concurrent Set sequence
        // must never terminally fault it.
        await engine.SetAsync(K(10), V("post-clear"));
        var (v, found) = await engine.TryGetValueAsync(K(10));
        found.Should().BeTrue("the engine must stay usable after a clear/flush race");
        v.Value.Should().Be("post-clear");

        // And the discarded flush's segment file must not linger as an orphan that would
        // fail the next open (segment files without a manifest fail closed).
        await engine.FlushAsync();
        var manifest = SegmentManifest.Read(_dir);
        var onDisk = Directory.GetFiles(_dir, "seg_*.sst").Select(Path.GetFileName).ToHashSet();
        onDisk.Should().BeSubsetOf(manifest ?? [],
            "every segment file on disk must be accounted for by the manifest");
    }
}
