// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.ValueLog;

/// <summary>
/// M-4 AC-4.5 + AC-4.6 — the <see cref="ValueLogGarbageCollector"/> direct unit-test suite. It
/// drives the collector against a hand-built <c>IValueLogGcHost</c> fake (wrapping a real
/// <see cref="ValueLogManifest"/> + <see cref="ValueLogWriter"/> so file sizes and the current
/// append target are genuine) so the liveness scan, the fail-closed <c>scanComplete</c> path, the
/// tombstone-before-unlink ordering, and the surfaced-GC-failure behaviour are exercised without
/// standing up a full LSM engine.
/// </summary>
public sealed class ValueLogGarbageCollectorDirectTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;
    private ValueLogWriter? _writer;

    public ValueLogGarbageCollectorDirectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlgc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync();
        }
        if (_manifest is not null)
        {
            await _manifest.DisposeAsync();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Opens a writer and appends one frame into each of <paramref name="totalSegments"/> segments, forcing
    /// rotation so each becomes its own segment file, then appends into one final current target.
    /// Uses a tiny SegmentMaxBytes so every append rotates. Returns the live writer.
    /// </summary>
    private async Task SetUpSegmentsAsync(int totalSegments)
    {
        _manifest = new ValueLogManifest(_manifestPath);

        // Each appended frame is EntryHeaderSize(17) + payload(64) + EntryCrcSize(4) = 85 bytes. A
        // fresh segment opens with an 8-byte header. SegmentMaxBytes=120 fits ONE frame
        // (8 + 85 = 93 ≤ 120) but not two (93 + 85 = 178 > 120), so append i lands in file i exactly:
        // first append → file 0; each subsequent append overflows and rotates to the next file.
        var options = new ValueLogOptions(
            ValueLogThresholdBytes: 64,
            SegmentMaxBytes: 120,
            ReadHandleCacheSize: 16,
            MaxEntryBytes: 64L * 1024 * 1024);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, options);

        for (var i = 0; i < totalSegments; i++)
        {
            await _writer.AppendAsync(new byte[64], (ulong)(i + 1));
        }
    }

    // ----- Async lock contract: EnterLockForGcAsync must exist on IValueLogGcHost -----

    [Fact]
    public async Task EnterLockForGcAsync_IsAwaitableOnIValueLogGcHost()
    {
        // Compile-time RED: verifies IValueLogGcHost.EnterLockForGcAsync exists and returns
        // an awaitable. Without the fix the method does not exist on the interface and this
        // test file fails to compile.
        await SetUpSegmentsAsync(totalSegments: 1);
        IValueLogGcHost host = new FakeGcHost(_manifest!, _writer!, _dir)
        {
            LivePointers = [],
            ScanComplete = true,
        };

        // Await the async entry — must not block the current thread.
        await host.EnterLockForGcAsync();
        host.ExitLockForGc();
    }

    // ----- AC-4.5: liveness scan reclaims only fully-shadowed closed files -----

    [Fact]
    public async Task LivenessScan_ReclaimsClosedFilesWithNoLivePointer()
    {
        // Files 0..3 exist; 3 is the current append target. Files 0 and 2 are live-referenced; file 1
        // is fully shadowed → reclaimable.
        await SetUpSegmentsAsync(totalSegments: 4);
        var currentIndex = _writer!.CurrentFileIndex;
        currentIndex.Should().Be(3u);

        var host = new FakeGcHost(_manifest!, _writer!, _dir)
        {
            LivePointers = [(0u, 8ul), (2u, 8ul)],
            ScanComplete = true,
        };

        var gc = new ValueLogGarbageCollector(host);
        var result = await gc.RunAsync();

        result.FilesReclaimed.Should().Be(1, "only file 1 is fully shadowed");
        result.FilesSkipped.Should().Be(2, "files 0 and 2 still have live pointers");
        result.BytesReclaimed.Should().BeGreaterThan(0, "file 1's bytes are reclaimed");

        (await _manifest!.GetLiveFilesAsync()).Should().NotContain(1u);
        File.Exists(ValueLogWriter.ResolveSegmentPath(_dir, 1)).Should().BeFalse("file 1 was unlinked");
        File.Exists(ValueLogWriter.ResolveSegmentPath(_dir, 0)).Should().BeTrue();
        File.Exists(ValueLogWriter.ResolveSegmentPath(_dir, 2)).Should().BeTrue();
    }

    [Fact]
    public async Task LivenessScan_NeverReclaimsCurrentAppendTarget()
    {
        await SetUpSegmentsAsync(totalSegments: 1); // only file 0, which is the current target
        _writer!.CurrentFileIndex.Should().Be(0u);

        var host = new FakeGcHost(_manifest!, _writer!, _dir)
        {
            LivePointers = [], // no live pointer anywhere
            ScanComplete = true,
        };

        var gc = new ValueLogGarbageCollector(host);
        var result = await gc.RunAsync();

        result.FilesReclaimed.Should().Be(0, "the current append target is never a GC candidate");
        File.Exists(ValueLogWriter.ResolveSegmentPath(_dir, 0)).Should().BeTrue();
    }

    [Fact]
    public void LivenessScan_UsesHashSet_NotPerFileLinearScan()
    {
        // AC-4.5: the reclaimability check must be an O(1) HashSet probe over the distinct live file
        // indexes, not a nested linear scan over every live pointer per closed file. The defect form
        // is an inner "foreach (... in livePointers)" inside the closed-file loop; the fixed form
        // builds a HashSet<uint> of live file indexes once and probes Contains.
        var source = ReadGcSource();
        var run = ExtractMethod(source, "private async Task<GcResult> RunImplAsync");
        var executable = StripLineComments(run);

        // The defect signature is the per-closed-file linear-scan flag ("var anyLive = false;" then
        // a nested loop setting it). The fix builds a HashSet<uint> of live file indexes once and
        // probes Contains — no per-file linear scan, no anyLive flag.
        executable.Should().NotContain("anyLive",
            "AC-4.5: the per-closed-file linear scan over livePointers must be replaced by a HashSet probe");
        executable.Should().Contain("HashSet<uint>",
            "AC-4.5: live file indexes are projected into a HashSet<uint> once for O(1) reclaimability checks");
        executable.Should().Contain(".Contains(fileIndex)",
            "AC-4.5: reclaimability is an O(1) HashSet probe on the file index");
    }

    // ----- AC-4.6: fail-closed scanComplete -----

    [Fact]
    public async Task FailClosed_WhenScanIncomplete_ReclaimsNothing()
    {
        await SetUpSegmentsAsync(totalSegments: 3); // files 0,1 closed; 2 current target

        var host = new FakeGcHost(_manifest!, _writer!, _dir)
        {
            LivePointers = [],
            ScanComplete = false, // a concurrent compaction disposed a reader mid-scan
        };

        var gc = new ValueLogGarbageCollector(host);
        var result = await gc.RunAsync();

        result.FilesReclaimed.Should().Be(0, "a partial scan must reclaim nothing (fail-closed)");
        result.FilesSkipped.Should().Be(3, "every closed file is reported skipped");
        File.Exists(ValueLogWriter.ResolveSegmentPath(_dir, 0)).Should().BeTrue();
        File.Exists(ValueLogWriter.ResolveSegmentPath(_dir, 1)).Should().BeTrue();
    }

    // ----- AC-4.6: tombstone-before-unlink ordering -----

    [Fact]
    public async Task ReclaimOrder_RecordsTombstoneBeforeUnlink_Behavioral()
    {
        await SetUpSegmentsAsync(totalSegments: 2); // file 0 reclaimable, file 1 current target

        var host = new FakeGcHost(_manifest!, _writer!, _dir)
        {
            LivePointers = [],
            ScanComplete = true,
        };

        var gc = new ValueLogGarbageCollector(host);
        var result = await gc.RunAsync();

        result.FilesReclaimed.Should().Be(1);
        // Post-conditions consistent with tombstone-then-unlink: the tombstone landed in the
        // manifest AND the segment file is gone. A crash between the two would leave a recoverable
        // orphan (tombstone present, file present) — never a live file with no tombstone.
        (await _manifest!.GetLiveFilesAsync()).Should().NotContain(0u, "the delete tombstone is recorded");
        File.Exists(ValueLogWriter.ResolveSegmentPath(_dir, 0)).Should().BeFalse("the file is unlinked");
    }

    [Fact]
    public void ReclaimOrder_SourceRecordsTombstoneBeforeUnlink()
    {
        var source = ReadGcSource();
        var recordDelete = source.IndexOf("RecordDeleteAsync", StringComparison.Ordinal);
        var fileDelete = source.IndexOf("File.Delete(path)", StringComparison.Ordinal);

        recordDelete.Should().BeGreaterThanOrEqualTo(0, "the reclaim loop records a delete tombstone");
        fileDelete.Should().BeGreaterThanOrEqualTo(0, "the reclaim loop unlinks the file");
        recordDelete.Should().BeLessThan(fileDelete,
            "AC-4.6: the delete tombstone must be recorded BEFORE the file is unlinked");
    }

    // ----- AC-4.6: a throw inside the scan is surfaced, not silently swallowed -----

    [Fact]
    public async Task ScanThrow_IsSurfacedToObserver_AndRoundFailsClosed()
    {
        await SetUpSegmentsAsync(totalSegments: 2); // file 0 closed, file 1 current target

        var forced = new InvalidOperationException("forced scan failure");
        var host = new FakeGcHost(_manifest!, _writer!, _dir)
        {
            LivePointers = [],
            ScanComplete = true,
            ScanThrow = forced, // the lock-free scan delegate throws an unexpected exception
        };

        Exception? surfaced = null;
        var gc = new ValueLogGarbageCollector(host, onScanFailure: ex => surfaced = ex);
        var result = await gc.RunAsync();

        surfaced.Should().BeSameAs(forced, "the scan throw must be surfaced to the failure observer");
        result.FilesReclaimed.Should().Be(0, "a failed scan reclaims nothing (fail-closed)");
        File.Exists(ValueLogWriter.ResolveSegmentPath(_dir, 0)).Should().BeTrue(
            "nothing is unlinked when the scan failed");
    }

    /// <summary>
    /// Minimal <c>IValueLogGcHost</c> fake wrapping a real manifest + writer (so current-target and
    /// file sizes are genuine). Supplies a configurable live-pointer set, <c>scanComplete</c> result,
    /// optional forced scan throw, and optional unlink observer.
    /// </summary>
    // ----- Reclaim must release every open reference holder -----

    [Fact]
    public async Task Reclaim_ReleasesTheReaderHandleCachedForTheUnlinkedSegment()
    {
        await SetUpSegmentsAsync(totalSegments: 2); // file 0 closed, file 1 current
        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        var segment0 = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var frame0 = new ValueLogPointer(0u, ValueLogFrame.HeaderSize, sequenceNumber: 1ul);
        reader.Read(frame0).Should().HaveCount(64, "the read caches a handle on segment 0");
        var cached = CachedHandles(reader);
        cached.Should().ContainKey(0u);
        var handle = cached[0u];

        var host = new FakeGcHost(_manifest!, _writer!, _dir) { LivePointers = [], ScanComplete = true };
        var result = await new ValueLogGarbageCollector(host).RunAsync();

        result.FilesReclaimed.Should().Be(1);
        File.Exists(segment0).Should().BeFalse();
        handle.IsClosed.Should().BeTrue(
            "an unlinked segment's blocks stay allocated while any descriptor on it is open, so reporting it reclaimed requires releasing the reader's handle");
        CachedHandles(reader).Should().NotContainKey(0u);
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<uint, Microsoft.Win32.SafeHandles.SafeFileHandle> CachedHandles(ValueLogReader reader)
        => (System.Collections.Concurrent.ConcurrentDictionary<uint, Microsoft.Win32.SafeHandles.SafeFileHandle>)typeof(ValueLogReader)
            .GetField("_handles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(reader)!;

    private sealed class FakeGcHost : IValueLogGcHost
    {
        private readonly ValueLogManifest _manifest;
        private readonly ValueLogWriter _writer;
        private readonly string _directory;

        public FakeGcHost(ValueLogManifest manifest, ValueLogWriter writer, string directory)
        {
            _manifest = manifest;
            _writer = writer;
            _directory = directory;
        }

        public IReadOnlyList<(uint, ulong)> LivePointers { get; init; } = [];

        public bool ScanComplete { get; init; } = true;

        public Exception? ScanThrow { get; init; }

        public ValueLogManifest? ValueLogManifest => _manifest;

        public ValueLogWriter? ValueLogWriter => _writer;

        public string ValueLogDirectory => _directory;

        public Func<HashSet<(uint FileIndex, ulong Offset)>, bool> SnapshotSegmentsUnderLock()
        {
            return acc =>
            {
                if (ScanThrow is not null)
                {
                    throw ScanThrow;
                }

                foreach (var p in LivePointers)
                {
                    acc.Add(p);
                }

                return ScanComplete;
            };
        }

        public ValueTask EnterLockForGcAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public void ExitLockForGc() { /* no engine lock in the fake */ }
    }

    private static string StripLineComments(string code)
    {
        var lines = code.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            kept.Add(idx >= 0 ? line[..idx] : line);
        }

        return string.Join('\n', kept);
    }

    private static string ExtractMethod(string source, string signaturePrefix)
    {
        var start = source.IndexOf(signaturePrefix, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected to find '{signaturePrefix}' in the source");

        var braceOpen = source.IndexOf('{', start);
        var depth = 0;
        var i = braceOpen;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
        }

        return source.Substring(start, i - start + 1);
    }

    private static string ReadGcSource()
    {
        var asmDir = Path.GetDirectoryName(typeof(ValueLogGarbageCollectorDirectTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        var sourcePath = Path.Combine(repoRoot, "src", "Infrastructure", "ValueLog", "ValueLogGarbageCollector.cs");
        File.Exists(sourcePath).Should().BeTrue($"expected ValueLogGarbageCollector source at {sourcePath}");
        return File.ReadAllText(sourcePath);
    }
}
