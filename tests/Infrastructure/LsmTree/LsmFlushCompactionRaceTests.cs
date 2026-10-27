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
/// The background flusher writes its SSTable without holding the engine lock (deliberate —
/// ingest stays live), but it must RESERVE its segment filename under the lock before
/// releasing it. If the name is derived from <c>_segmentCounter</c> in the unlocked region,
/// a concurrent <c>CompactAsync</c> (which names its output under the lock from the same
/// counter) races to the same filename: the later atomic replace silently clobbers the
/// earlier segment's bytes and the manifest records the same file twice with conflicting
/// integrity envelopes — detected-as-corruption data loss at the next reopen.
/// </summary>
public sealed class LsmFlushCompactionRaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmFlushRace_{Guid.NewGuid():N}");

    public LsmFlushCompactionRaceTests()
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
    /// Delegates to the real SST format, but the first WriteAsync after <see cref="Arm"/>
    /// signals <see cref="WriterEntered"/> and parks until <see cref="Release"/> — freezing
    /// the background flusher inside its unlocked write window.
    /// </summary>
    private sealed class BlockingOnceSegmentFormat : ISegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        private readonly SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>> _inner = new();
        private int _armed;

        public TaskCompletionSource WriterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public string FileExtension => _inner.FileExtension;
        public bool SupportsSnapshots => _inner.SupportsSnapshots;

        public async Task<ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>>> WriteAsync(
            string segmentFolder,
            string segmentFileName,
            IReadOnlyList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> sortedEntries,
            IReadOnlyDictionary<SerializableWrapper<int>, ulong>? tombstones = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                WriterEntered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            return await _inner.WriteAsync(segmentFolder, segmentFileName, sortedEntries, tombstones, cancellationToken)
                .ConfigureAwait(false);
        }

        public ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>> Open(
            string segmentFolder, string segmentFileName)
            => _inner.Open(segmentFolder, segmentFileName);
    }

    [Fact]
    public async Task BackgroundFlush_RacingCompactAsync_NeverCollidesOnSegmentFileName()
    {
        var format = new BlockingOnceSegmentFormat();
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _dir,
            segmentPrefix: "seg",
            segmentFormat: format);

        // Two settled segments so CompactAsync has something to merge.
        await engine.SetAsync(K(1), V("v1"));
        await engine.SetAsync(K(2), V("v2"));
        await engine.FlushAsync();
        await engine.SetAsync(K(3), V("v3"));
        await engine.SetAsync(K(4), V("v4"));
        await engine.FlushAsync();
        engine.SegmentCount.Should().Be(2);

        // Freeze the NEXT flush inside its unlocked SST-write window.
        format.Arm();
        await engine.SetAsync(K(5), V("v5"));
        await engine.SetAsync(K(6), V("v6"));
        await engine.SetAsync(K(7), V("v7")); // triggers the seal; background flush begins
        await format.WriterEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While the flusher is parked, compact the two settled segments.
        await engine.CompactAsync();

        // Unfreeze the flusher and wait for its commit (segment count: 1 compacted + 1 flushed).
        format.Release.TrySetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (engine.SegmentCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        // The manifest must list unique filenames — a duplicate means the flush and the
        // compaction raced to the same counter value and one clobbered the other on disk.
        var manifest = SegmentManifest.Read(_dir);
        manifest.Should().NotBeNull();
        manifest!.Should().OnlyHaveUniqueItems(
            "the flusher must reserve its segment filename under the engine lock so a " +
            "concurrent compaction can never pick the same counter value");

        // And every acknowledged write must still be readable.
        for (var i = 1; i <= 7; i++)
        {
            var (v, found) = await engine.TryGetValueAsync(K(i));
            found.Should().BeTrue($"key {i} was acknowledged and must survive the flush/compaction race");
            v.Value.Should().Be($"v{i}");
        }
    }
}
