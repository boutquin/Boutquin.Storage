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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

// Phase 11 — crash-recovery harness for LsmStorageEngine. Each test starts an engine,
// writes / flushes, simulates a crash by corrupting on-disk state, then proves the
// reopened engine either (a) recovers cleanly or (b) surfaces the right structured
// failure for upstream alerting. These cover the "MarketData ingest crashed mid-flush"
// and "operator overwrote a manifest entry" paths that the stack must handle.
public sealed class LsmStorageEngineCrashRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmCrashRec_{Guid.NewGuid():N}");

    public LsmStorageEngineCrashRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>> Options(
        IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>? wal = null,
        int memTableCapacity = 4)
        => new(
            MemTableCapacity: memTableCapacity,
            SegmentFolder: _dir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            WriteAheadLog: wal,
            CompactionStrategy: null,
            ValueLog: null);

    private static SerializableWrapper<int> K(int v) => new(v);
    private static SerializableWrapper<string> V(string s) => new(s);

    // ── (a) WAL with trailing partial frame ─────────────────────────────────

    [Fact]
    public async Task Reopen_WithWalTrailingPartialFrame_RecoversValidEntries()
    {
        var walDir = Path.Combine(_dir, "wal");
        Directory.CreateDirectory(walDir);

        // First lifetime: write through the engine + WAL.
        {
            using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
                SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                Options(wal, memTableCapacity: 8));
            await engine.SetAsync(K(1), V("a"));
            await engine.SetAsync(K(2), V("b"));
            // Note: don't flush — entries live only in the WAL until the second engine reads them.
        }

        // Tamper: append a 4-byte negative-length prefix to the WAL segment.
        var walSegment = Path.Combine(walDir, "wal_000001.log");
        File.Exists(walSegment).Should().BeTrue();
        using (var fs = new FileStream(walSegment, FileMode.Append, FileAccess.Write))
        {
            Span<byte> negLen = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(negLen, -1);
            fs.Write(negLen);
        }

        // Reopen — both pre-corruption entries must be recovered into the engine.
        using var wal2 = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        using var engine2 = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(Options(wal2, memTableCapacity: 8));

        var (v1, f1) = await engine2.TryGetValueAsync(K(1));
        f1.Should().BeTrue("WAL entries before the corruption must replay into memtable");
        v1.Value.Should().Be("a");
        var (v2, f2) = await engine2.TryGetValueAsync(K(2));
        f2.Should().BeTrue();
        v2.Value.Should().Be("b");
    }

    [Fact]
    public async Task ConstructorReopen_OverWalWithUnreplayedFrames_KeepsAcknowledgedWrites()
    {
        var walDir = Path.Combine(_dir, "wal");
        Directory.CreateDirectory(walDir);

        // Lifetime 1: two acknowledged writes, durable only in the WAL.
        {
            using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
                SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                Options(wal, memTableCapacity: 8));
            await engine.SetAsync(K(1), V("a"));
            await engine.SetAsync(K(2), V("b"));
        }

        // Lifetime 2: reopen through the public constructor, then write and flush. The flush
        // truncates the WAL up to its watermark, so frames the reopen never applied are gone.
        {
            using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
                SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                Options(wal, memTableCapacity: 8));

            var (v1, f1) = await engine.TryGetValueAsync(K(1));
            f1.Should().BeTrue("a constructor reopen over an existing WAL must replay its acknowledged frames");
            v1.Value.Should().Be("a");
            engine.LastAssignedSequenceNumber.Should().BeGreaterThanOrEqualTo(2UL,
                "the seqno counter is reseeded past every replayed frame");

            for (var i = 10; i < 20; i++)
            {
                await engine.SetAsync(K(i), V($"x{i}"));
            }

            await engine.FlushAsync();
        }

        // Lifetime 3: a full OpenAsync recovery still finds both lifetime-1 writes.
        using var wal3 = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        using var engine3 = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(Options(wal3, memTableCapacity: 8));
        var (r1, g1) = await engine3.TryGetValueAsync(K(1));
        var (r2, g2) = await engine3.TryGetValueAsync(K(2));
        g1.Should().BeTrue("key 1 was acknowledged in lifetime 1");
        r1.Value.Should().Be("a");
        g2.Should().BeTrue("key 2 was acknowledged in lifetime 1");
        r2.Value.Should().Be("b");
    }

    // ── (b) Manifest pointing at missing / truncated SSTable ────────────────

    [Fact]
    public async Task Reopen_AfterSegmentFileDeleted_Throws()
    {
        // Write + flush so the manifest references an SST file, then delete the file out
        // from under the manifest. CRC computation throws FileNotFoundException → reopen fails.
        {
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(Options());
            await engine.SetAsync(K(1), V("a"));
            await engine.FlushAsync();
        }

        // Locate the SSTable file (manifest is "CURRENT", segments use the configured prefix).
        var segments = Directory.GetFiles(_dir, "seg_*.sst");
        segments.Should().NotBeEmpty("flush should have produced at least one segment file");
        File.Delete(segments[0]);

        Action act = () => _ = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(Options());
        act.Should().Throw<Exception>("missing segment named by the CURRENT manifest must surface");
    }

    [Fact]
    public async Task Reopen_AfterSegmentTruncated_FailsClosed()
    {
        // A manifest-listed segment failing its CRC/size check holds acknowledged, flushed
        // data the engine can no longer trust. Skipping it silently serves reads as if those
        // keys never existed — and resurrects older values and deletes from elder segments —
        // with only a Trace warning as signal. Recovery has DETECTED the damage, so it must
        // fail closed (matching the missing-manifest and missing-segment-file dispositions)
        // and let the operator restore or explicitly discard, rather than convert detected
        // corruption into silent data absence.
        {
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(Options());
            await engine.SetAsync(K(1), V("a"));
            await engine.SetAsync(K(2), V("b"));
            await engine.FlushAsync();
        }

        var segments = Directory.GetFiles(_dir, "seg_*.sst");
        segments.Should().NotBeEmpty();
        using (var fs = new FileStream(segments[0], FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fs.Length / 2);
        }

        Action act = () => _ = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(Options());
        act.Should().Throw<InvalidDataException>(
                "detected segment corruption must fail recovery loudly, not silently drop acked data")
            .WithMessage($"*{Path.GetFileName(segments[0])}*");
    }

    // ── (c) Value-log mid-file CRC failure ───────────────────────────────────

    [Fact]
    public async Task ValueLog_MidFileCorruption_IsDetectedOnRead()
    {
        // Reopen succeeds (CRC is verified per-frame at read time, not at open time).
        // The corruption surfaces when the engine actually tries to read the corrupted
        // frame — DDIA-shape "lazy verification" rather than upfront scan.
        var optsWithVl = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 4,
            SegmentFolder: _dir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null,
            ValueLog: new Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogOptions(
                ValueLogThresholdBytes: 64));

        {
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(optsWithVl);
            await engine.SetAsync(K(1), V(new string('x', 200)));
            await engine.SetAsync(K(2), V(new string('y', 200)));
            await engine.FlushAsync();
        }

        // Corrupt a byte mid-file in the value-log segment (well past the header).
        var valueLogDir = Path.Combine(_dir, "valuelog");
        var vlSegment = Path.Combine(valueLogDir, "valuelog_000000.log");
        File.Exists(vlSegment).Should().BeTrue();
        var bytes = File.ReadAllBytes(vlSegment);
        bytes[20] ^= 0xFF;
        File.WriteAllBytes(vlSegment, bytes);

        // Reopen succeeds — value-log frames are verified lazily on dereference.
        using var reopened = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(optsWithVl);

        // Reading the affected key surfaces the corruption.
        Func<Task> act = async () => await reopened.TryGetValueAsync(K(1));
        await act.Should().ThrowAsync<Exception>(
            "value-log frame CRC verifies on read; the corrupted frame must surface as a structured exception");
    }

    // ── Multi-snapshot pin/release sequencing ──────────────────────────────

    [Fact]
    public async Task TwoSnapshots_BothPinsReleasedDeletesPinnedSegments()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(memTableCapacity: 2));

        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        await engine.FlushAsync();

        var snap1 = await engine.AcquireSnapshotAsync();
        await engine.SetAsync(K(3), V("c"));
        await engine.FlushAsync();
        var snap2 = await engine.AcquireSnapshotAsync();

        // Compaction merges both segments; both snapshots pin distinct file sets.
        await engine.CompactAsync();

        // Both snapshots still see their pinned data.
        var (v1Snap1, f1Snap1) = await snap1.TryGetValueAsync(K(1));
        f1Snap1.Should().BeTrue();
        v1Snap1.Value.Should().Be("a");
        var (v3Snap2, f3Snap2) = await snap2.TryGetValueAsync(K(3));
        f3Snap2.Should().BeTrue();
        v3Snap2.Value.Should().Be("c");

        // Release both snapshots in reverse order.
        await snap2.DisposeAsync();
        await snap1.DisposeAsync();

        // Engine should still serve the merged data.
        var (v1, f1) = await engine.TryGetValueAsync(K(1));
        f1.Should().BeTrue();
        v1.Value.Should().Be("a");
    }

    [Fact]
    public async Task Snapshot_TakenAfterCompaction_StillSeesAllKeys()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(memTableCapacity: 2));

        await engine.SetAsync(K(1), V("a"));
        await engine.FlushAsync();
        await engine.SetAsync(K(2), V("b"));
        await engine.FlushAsync();
        await engine.CompactAsync();

        await using var snap = await engine.AcquireSnapshotAsync();
        var (v1, f1) = await snap.TryGetValueAsync(K(1));
        f1.Should().BeTrue();
        v1.Value.Should().Be("a");
        var (v2, f2) = await snap.TryGetValueAsync(K(2));
        f2.Should().BeTrue();
        v2.Value.Should().Be("b");
    }

    [Fact]
    public async Task Snapshot_HoldsEntryThatIsLaterDeleted_ReadStillSucceeds()
    {
        // Snapshot isolation guarantee: after AcquireSnapshotAsync, the snapshot reads
        // must return the value even if the engine subsequently removes it.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(memTableCapacity: 4));
        await engine.SetAsync(K(1), V("a"));
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        await engine.RemoveAsync(K(1));
        await engine.FlushAsync();

        var (v, found) = await snap.TryGetValueAsync(K(1));
        found.Should().BeTrue("snapshot acquired before delete must still see the original value");
        v.Value.Should().Be("a");

        // Engine itself sees the delete.
        var (_, engineFound) = await engine.TryGetValueAsync(K(1));
        engineFound.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_DisposedTwice_IsNoOp()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(Options());
        await engine.SetAsync(K(1), V("a"));
        await engine.FlushAsync();

        var snap = await engine.AcquireSnapshotAsync();
        await snap.DisposeAsync();
        Func<Task> act = async () => await snap.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SnapshotsAcquiredInSequence_DifferentSequenceNumbers()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(memTableCapacity: 8));
        await engine.SetAsync(K(1), V("a"));

        await using var snap1 = await engine.AcquireSnapshotAsync();
        await engine.SetAsync(K(2), V("b"));
        await using var snap2 = await engine.AcquireSnapshotAsync();

        snap1.SequenceNumber.Should().BeLessThan(snap2.SequenceNumber,
            "snapshots acquired later must observe a later seqno");
    }

    // ── (e) First flush interrupted: the unfinished segment is discarded ────

    private static global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>
        OpenWal(string walFolder) => new(walFolder);

    /// <summary>
    /// A first flush whose segment write fails must not leave its unfinished segment file behind:
    /// no <c>CURRENT</c> exists yet, so a leftover file is "segment files without a manifest" and
    /// the next open refuses the store, stranding every write the WAL acknowledged.
    /// </summary>
    [Fact]
    public async Task FirstFlush_SegmentWriteFails_LeavesNoSegmentFile_AndStoreReopensWithAcknowledgedWrites()
    {
        var segmentFolder = Path.Combine(_dir, "seg");
        var walFolder = Path.Combine(_dir, "wal");
        Directory.CreateDirectory(segmentFolder);

        // A directory squatting on the writer's temp path makes the first segment write fail after
        // the segment file was reserved.
        var blockedTemp = Path.Combine(segmentFolder, "seg_000000.sst.tmp");
        Directory.CreateDirectory(blockedTemp);

        using (var wal = OpenWal(walFolder))
        using (var engine = LsmEngineFactory.Build(
            memTableCapacity: 4, segmentFolder: segmentFolder, segmentPrefix: "seg", writeAheadLog: wal))
        {
            await engine.SetAsync(K(1), V("acknowledged"));
            var flushFailure = await Record.ExceptionAsync(() => engine.FlushAsync());
            flushFailure.Should().NotBeNull("the segment write could not create its temp file");

            Directory.GetFiles(segmentFolder, "seg_*.sst").Should().BeEmpty(
                "a failed flush must discard its unfinished segment file instead of leaving it for the next open");
        }

        Directory.Delete(blockedTemp);
        using var reopenedWal = OpenWal(walFolder);
        using var reopened = await LsmEngineFactory.BuildAsync(
            memTableCapacity: 4, segmentFolder: segmentFolder, segmentPrefix: "seg", writeAheadLog: reopenedWal);
        var (value, found) = await reopened.TryGetValueAsync(K(1));
        found.Should().BeTrue("the write was acknowledged through the WAL, which the failed flush never truncated");
        value.Value.Should().Be("acknowledged");
    }

    /// <summary>
    /// A crash during a store's first flush — after its segment file reached its final name, before
    /// any manifest lists it — leaves an unfinished segment and no record of the store's segment set.
    /// Recovery must discard the unfinished segment and replay the WAL, not refuse the store.
    /// </summary>
    [Fact]
    public async Task CrashDuringFirstFlush_BeforeManifestCommit_ReopensWithAcknowledgedWrites()
    {
        var live = Path.Combine(_dir, "live");
        var image = Path.Combine(_dir, "image");
        Directory.CreateDirectory(live);

        using (var wal = OpenWal(Path.Combine(live, "wal")))
        using (var engine = LsmEngineFactory.Build(
            memTableCapacity: 4, segmentFolder: Path.Combine(live, "seg"), segmentPrefix: "seg", writeAheadLog: wal))
        {
            var captured = false;
            engine.FlusherCommitLockHook = _ =>
            {
                if (!captured)
                {
                    CopyTree(live, image);
                    captured = true;
                }

                return Task.CompletedTask;
            };

            await engine.SetAsync(K(1), V("acknowledged"));
            await engine.FlushAsync();
            captured.Should().BeTrue("the crash image is taken between the segment write and its commit");
        }

        Directory.GetFiles(Path.Combine(image, "seg"), "seg_*.sst").Should().NotBeEmpty(
            "the crash image holds the first flush's unfinished segment");

        using var reopenedWal = OpenWal(Path.Combine(image, "wal"));
        var open = async () => await LsmEngineFactory.BuildAsync(
            memTableCapacity: 4, segmentFolder: Path.Combine(image, "seg"), segmentPrefix: "seg", writeAheadLog: reopenedWal);
        var reopened = (await open.Should().NotThrowAsync(
            "an unfinished first-flush segment is discarded at recovery, not treated as a lost manifest")).Subject;
        using (reopened)
        {
            var (value, found) = await reopened.TryGetValueAsync(K(1));
            found.Should().BeTrue("the write was acknowledged through the WAL");
            value.Value.Should().Be("acknowledged");
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if (!file.EndsWith(".lock", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
