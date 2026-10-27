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
#nullable disable

using System.Buffers.Binary;

using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Engine-side dispatch acceptance criteria. Covers the shipped subset:
///   V-INT-1 — MemTable round-trip for both inline and pointer values
///   V-INT-2 — WAL replay of mixed inline/pointer/tombstone frames
///   V-INT-3 (partial) — value-log segment file is created for pointer-sized writes
/// SSTable-side propagation (flush emits BlockEntry.Kind=ValueLogPointer) is deferred;
/// this session's flush path dereferences pointers back to inline bytes at flush time, so
/// V-INT-3's "block entries carry the correct Kind per entry" sub-assertion cannot land yet.
/// </summary>
public sealed class WiscKeyIntegrationTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"WiscKey_{Guid.NewGuid():N}");

    public WiscKeyIntegrationTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    private string SegmentFolder => _testDirectory;

    private static LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>> Options(
        string folder,
        int threshold,
        IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>> wal = null,
        int memTableCapacity = 100)
        => new(
            MemTableCapacity: memTableCapacity,
            SegmentFolder: folder,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            WriteAheadLog: wal,
            ValueLog: new ValueLogOptions(ValueLogThresholdBytes: threshold));

    private static string LargePayload(int approxBytes)
        => new('x', approxBytes);

    /// <summary>
    /// V-INT-1 (inline half): small values below the threshold stay in the MemTable slot
    /// as <c>ValueKind.Inline</c> and round-trip through <c>TryGetValueAsync</c>.
    /// </summary>
    [Fact]
    public async Task InlineValue_RoundTripsThroughMemTable()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(SegmentFolder, threshold: 1024));

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("small");

        await engine.SetAsync(key, value);

        var (result, found) = await engine.TryGetValueAsync(key);
        found.Should().BeTrue();
        result.Value.Should().Be("small");

        // The value-log directory should not exist — a small write must not trigger the
        // value-log subsystem (inline path; only large writes cross the threshold).
        var valueLogDir = Path.Combine(SegmentFolder, "valuelog");
        Directory.Exists(valueLogDir).Should().BeFalse(
            "inline writes stay off the value log entirely");
    }

    /// <summary>
    /// V-INT-1 (pointer half): values larger than the threshold are persisted to the
    /// value log; the MemTable carries a <c>ValueLogPointer</c>; <c>TryGetValueAsync</c>
    /// dereferences through the reader and returns the original bytes.
    /// </summary>
    [Fact]
    public async Task PointerValue_RoundTripsThroughValueLog()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(SegmentFolder, threshold: 64));

        var key = new SerializableWrapper<int>(1);
        var payload = LargePayload(approxBytes: 512);
        var value = new SerializableWrapper<string>(payload);

        await engine.SetAsync(key, value);

        var (result, found) = await engine.TryGetValueAsync(key);
        found.Should().BeTrue();
        result.Value.Should().Be(payload);

        // A value-log segment must exist on disk — the writer opens a fresh file on the
        // first append and fsyncs the header even before any payload bytes land.
        var valueLogDir = Path.Combine(SegmentFolder, "valuelog");
        Directory.Exists(valueLogDir).Should().BeTrue();
        Directory.GetFiles(valueLogDir, "valuelog_*.log").Should().NotBeEmpty();
    }

    /// <summary>
    /// V-INT-1 (mixed): a single engine instance correctly round-trips interleaved inline
    /// and pointer writes and returns the right payload for each.
    /// </summary>
    [Fact]
    public async Task MixedInlineAndPointerWrites_AllRoundTrip()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(SegmentFolder, threshold: 64));

        var inlineKey = new SerializableWrapper<int>(1);
        var inlineValue = new SerializableWrapper<string>("x");
        var pointerKey = new SerializableWrapper<int>(2);
        var pointerPayload = LargePayload(256);
        var pointerValue = new SerializableWrapper<string>(pointerPayload);

        await engine.SetAsync(inlineKey, inlineValue);
        await engine.SetAsync(pointerKey, pointerValue);

        var (inlineResult, inlineFound) = await engine.TryGetValueAsync(inlineKey);
        inlineFound.Should().BeTrue();
        inlineResult.Value.Should().Be("x");

        var (pointerResult, pointerFound) = await engine.TryGetValueAsync(pointerKey);
        pointerFound.Should().BeTrue();
        pointerResult.Value.Should().Be(pointerPayload);
    }

    /// <summary>
    /// V-INT-2: redesigned WAL frames round-trip through <c>WriteAheadLog.RecoverAsync</c>.
    /// Mix of <c>(Set, Inline)</c>, <c>(Set, ValueLogPointer)</c>, and <c>(Delete, *)</c>
    /// frames recovers to the same state the engine saw pre-crash.
    /// </summary>
    [Fact]
    public async Task WalReplay_RoundTripsAllDiscriminatorCombinations()
    {
        var walDir = Path.Combine(_testDirectory, "wal");
        Directory.CreateDirectory(walDir);

        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL);
            await wal.AppendPointerAsync(new SerializableWrapper<int>(2), new ValueLogPointer(0, 8), 2UL);
            await wal.AppendDeleteAsync(new SerializableWrapper<int>(3), 3UL);
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        var entries = await wal2.RecoverAsync();

        entries.Should().HaveCount(3);

        entries[0].Key.Value.Should().Be(1);
        entries[0].Kind.Should().Be(EntryKind.Set);
        entries[0].ValueKind.Should().Be(ValueKind.Inline);
        entries[0].SequenceNumber.Should().Be(1UL);
        entries[0].InlineValue.Value.Should().Be("a");
        entries[0].Pointer.Should().BeNull();

        entries[1].Key.Value.Should().Be(2);
        entries[1].Kind.Should().Be(EntryKind.Set);
        entries[1].ValueKind.Should().Be(ValueKind.ValueLogPointer);
        entries[1].SequenceNumber.Should().Be(2UL);
        entries[1].Pointer.Should().NotBeNull();
        entries[1].Pointer.Value.FileIndex.Should().Be(0u);
        entries[1].Pointer.Value.Offset.Should().Be(8UL);

        entries[2].Key.Value.Should().Be(3);
        entries[2].Kind.Should().Be(EntryKind.Delete);
        entries[2].IsTombstone.Should().BeTrue();
        entries[2].SequenceNumber.Should().Be(3UL);
    }

    /// <summary>
    /// V-INT-2 (end-to-end): engine writes a mix of inline, pointer, and tombstone
    /// mutations with a WAL attached, disposes, then a new engine instance over the same
    /// directory reads back every live key. Pointer reads after recovery must resolve
    /// through the value-log reader — exercising the value-log replay dispatch.
    /// </summary>
    [Fact]
    public async Task EngineRestart_RecoversInlineAndPointerMemTableState()
    {
        var walDir = Path.Combine(_testDirectory, "wal");
        Directory.CreateDirectory(walDir);

        var payload = LargePayload(400);

        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir))
        using (var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(SegmentFolder, threshold: 64, wal: wal)))
        {
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("inline"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>(payload));
            await engine.RemoveAsync(new SerializableWrapper<int>(1));
            await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>(payload));
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        using var engine2 = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(Options(SegmentFolder, threshold: 64, wal: wal2));

        var (v1, found1) = await engine2.TryGetValueAsync(new SerializableWrapper<int>(1));
        found1.Should().BeFalse("key 1 was deleted");

        var (v2, found2) = await engine2.TryGetValueAsync(new SerializableWrapper<int>(2));
        found2.Should().BeTrue();
        v2.Value.Should().Be(payload);

        var (v3, found3) = await engine2.TryGetValueAsync(new SerializableWrapper<int>(3));
        found3.Should().BeTrue();
        v3.Value.Should().Be(payload);
    }

    /// <summary>
    /// Sanity: <see cref="LsmStorageEngineOptions{TKey, TValue}.ResolvedValueLog"/> falls back
    /// to <see cref="ValueLogOptions.Default"/> when the caller leaves the value-log knobs
    /// unset. Guards against regressions in the engine's ctor wiring.
    /// </summary>
    [Fact]
    public void Options_ResolvedValueLog_DefaultsWhenUnset()
    {
        var opts = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 10,
            SegmentFolder: SegmentFolder,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>());

        opts.ResolvedValueLog.Should().BeSameAs(ValueLogOptions.Default);
    }

    // ───── P3.7.4 WAL pointer-replay mid-frame validity ──────────────────────────

    /// <summary>
    /// V-INT-4: if the value-log kind byte at <c>pointer.Offset + 8</c> is not a valid
    /// frame kind (0x00 or 0x01) the engine must throw <see cref="CorruptValueLogPointerException"/>
    /// during WAL replay, not silently carry the corrupt pointer into the MemTable.
    /// The frame's header checksum (which covers the kind byte) is re-sealed so the value-log
    /// writer's recovery scan accepts the frame as structurally intact — only the kind byte is wrong.
    /// </summary>
    [Fact]
    public async Task WalReplay_PointerWithCorruptKindByte_ThrowsCorruptException()
    {
        var walDir = Path.Combine(_testDirectory, "wal");
        Directory.CreateDirectory(walDir);
        var vlDir = Path.Combine(SegmentFolder, "valuelog");

        var payload = LargePayload(400);

        // Write a pointer-valued entry so the WAL carries a ValueLogPointer frame.
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir))
        using (var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(SegmentFolder, threshold: 64, wal: wal)))
        {
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(payload));
        }

        var vlSegment = Path.Combine(vlDir, "valuelog_000000.log");
        File.Exists(vlSegment).Should().BeTrue("the engine must have written a value-log segment");

        // Corrupt the kind byte at absolute offset 16 (= HeaderSize(8) + length(4) + header CRC(4))
        // and re-seal the header checksum — CRC32C over the length (entry bytes 0..3) and the kind
        // byte plus seqno (entry bytes 8..16) — so the value-log writer's recovery scan still
        // accepts the frame (valid checksums, corrupt kind byte). Without the fixup the scan
        // fails closed on the damaged header before WAL replay reaches the kind check.
        using (var rw = new FileStream(vlSegment, FileMode.Open, FileAccess.ReadWrite))
        {
            const int fileHeaderSize = 8;           // ValueLogFrame.HeaderSize
            const int entryHeaderSize = 17;         // ValueLogFrame.EntryHeaderSize
            const int headerCrcOffset = 4;          // within entry header: after 4B length
            const int kindByteOffset = 8;           // within entry header: after length + header CRC

            rw.Seek(fileHeaderSize, SeekOrigin.Begin);
            var entryHeader = new byte[entryHeaderSize];
            rw.ReadExactly(entryHeader);

            // Corrupt the kind byte.
            entryHeader[kindByteOffset] = 0xFF;

            // Re-seal the header checksum over the corrupted header fields.
            var newHeaderCrc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(
                entryHeader.AsSpan(0, headerCrcOffset), entryHeader.AsSpan(kindByteOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(entryHeader.AsSpan(headerCrcOffset, 4), newHeaderCrc);

            // Rewrite the header with the corrupt kind byte; the payload checksum covers the
            // payload only and stays valid.
            rw.Seek(fileHeaderSize, SeekOrigin.Begin);
            rw.Write(entryHeader);
        }

        // Reopening with the same WAL must detect the corrupt kind byte and throw.
        Func<Task> act = async () =>
        {
            using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
            using var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
                .OpenAsync(Options(SegmentFolder, threshold: 64, wal: wal2));
        };

        await act.Should().ThrowAsync<CorruptValueLogPointerException>();
    }
}
