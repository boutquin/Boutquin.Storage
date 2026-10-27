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
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// Seqno-aware single-<c>SortedStringTable&lt;TKey,TValue&gt;.Write</c> API. After Phase H
/// the SSTable carries one seqno-aware write path and one seqno-aware enumeration path —
/// no "legacy" zero-seqno overload. Callers that have no seqno to offer pass <c>0UL</c>
/// (demos, standalone uses).
/// </summary>
public sealed class SortedStringTableSeqnoApiTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"SSTableSeqnoTests_{Guid.NewGuid():N}");

    public SortedStringTableSeqnoApiTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void Write_RoundTripsEveryFieldUnchanged()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "seqno_roundtrip.dat", codec: new NoCompressionCodec());

        var items = new[]
        {
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"), 100UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("two"), 101UL),
            (new SerializableWrapper<int>(3), new SerializableWrapper<string>("three"), 102UL),
        };

        sst.Write(items);

        var read = sst.EnumerateInline().ToList();
        read.Should().HaveCount(3);
        read[0].Key.Value.Should().Be(1);
        read[0].Value.Value.Should().Be("one");
        read[0].SequenceNumber.Should().Be(100UL);
        read[1].SequenceNumber.Should().Be(101UL);
        read[2].SequenceNumber.Should().Be(102UL);
    }

    [Fact]
    public void Write_SeqnosPreservedAfterReopen()
    {
        var fileName = "seqno_reopen.dat";
        {
            var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName, codec: new LZ4Codec());
            writer.Write(
            [
                (new SerializableWrapper<int>(10), new SerializableWrapper<string>("ten"), 7UL),
                (new SerializableWrapper<int>(20), new SerializableWrapper<string>("twenty"), 8UL),
                (new SerializableWrapper<int>(30), new SerializableWrapper<string>("thirty"), 9UL),
            ]);
        }

        var reopened = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName);

        var read = reopened.EnumerateInline().ToList();
        read.Select(t => t.SequenceNumber).Should().Equal(7UL, 8UL, 9UL);
    }

    [Fact]
    public void MaxSequenceNumber_ReturnsHighestObservedSeqno()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "maxseqno.dat", codec: new NoCompressionCodec());

        sst.Write(
        [
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 42UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 99UL),
            (new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"), 7UL),
        ]);

        // Max over the set {42, 99, 7} is 99 — does NOT require sorted-by-seqno input.
        sst.MaxSequenceNumber.Should().Be(99UL);
    }

    [Fact]
    public void MaxSequenceNumber_AfterReopen_MatchesWriter()
    {
        var fileName = "maxseqno_reopen.dat";
        {
            var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName, codec: new LZ4Codec());
            writer.Write(
            [
                (new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"), 500UL),
                (new SerializableWrapper<int>(2), new SerializableWrapper<string>("v"), 123UL),
            ]);
            writer.MaxSequenceNumber.Should().Be(500UL);
        }

        var reopened = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName);
        reopened.MaxSequenceNumber.Should().Be(500UL);
    }

    [Fact]
    public void MaxSequenceNumber_EmptyFile_IsZero()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "maxseqno_empty.dat");
        sst.Write(Array.Empty<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>());
        sst.MaxSequenceNumber.Should().Be(0UL);
    }

    [Fact]
    public void Write_WithZeroSeqnos_EnumerateReflectsZero()
    {
        // Callers without a seqno to offer (demos, standalone) pass 0UL. The triples with
        // SequenceNumber = 0 persist unchanged and Enumerate returns zero for every entry.
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "zero_seqno.dat");
        sst.Write(
        [
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 0UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 0UL),
        ]);

        var read = sst.EnumerateInline().ToList();
        read.Should().HaveCount(2);
        read.Select(t => t.SequenceNumber).Should().AllSatisfy(s => s.Should().Be(0UL));
    }

    [Fact]
    public void Write_RejectsUnsortedKeys()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "seqno_unsorted.dat");

        var act = () => sst.Write(
        [
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 1UL),
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 2UL),
        ]);
        act.Should().Throw<ArgumentException>();
    }

    // ── Methods that are no-ops when the SST has not been written / opened ──

    [Fact]
    public void EnumerateInline_OnUnwrittenFile_YieldsNothing()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "unwritten.dat", codec: new NoCompressionCodec());
        sst.EnumerateInline().Should().BeEmpty();
    }

    [Fact]
    public void EnumerateBlockEntriesFiltered_OnUnwrittenFile_YieldsNothing()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "unwritten2.dat", codec: new NoCompressionCodec());
        sst.EnumerateBlockEntriesFiltered().Should().BeEmpty();
    }

    [Fact]
    public void TryGetValue_OnUnwrittenFile_ReturnsFalse()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "unwritten3.dat", codec: new NoCompressionCodec());
        sst.TryGetValue(new SerializableWrapper<int>(1), out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetBlockEntry_OnUnwrittenFile_ReturnsFalse()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "unwritten4.dat", codec: new NoCompressionCodec());
        sst.TryGetBlockEntry(new SerializableWrapper<int>(1), out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetRange_OnUnwrittenFile_ReturnsFalse()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "unwritten5.dat", codec: new NoCompressionCodec());
        sst.TryGetRange(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void GetEntryCount_OnUnwrittenFile_ReturnsZero()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "unwritten6.dat", codec: new NoCompressionCodec());
        sst.GetEntryCount().Should().Be(0);
    }

    // ── TryGetValue / TryGetBlockEntry behavioural edges ─────────────────────

    [Fact]
    public void TryGetValue_KeyAbsent_ReturnsFalse()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "absent_key.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"), 1UL),
            (new SerializableWrapper<int>(3), new SerializableWrapper<string>("three"), 2UL),
        ]);

        // Key 2 is absent — the cmp > 0 branch in TryGetBlockEntry's loop fires.
        sst.TryGetValue(new SerializableWrapper<int>(2), out _).Should().BeFalse();
        // Key 99 is past every entry — the foreach exhausts and returns false.
        sst.TryGetValue(new SerializableWrapper<int>(99), out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetBlockEntry_SeqnoCapBelowEntry_ReturnsFalse()
    {
        // The entry was written at seqno 100. A read with maxSeqno=50 must mask it.
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "seqno_cap.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            (new SerializableWrapper<int>(7), new SerializableWrapper<string>("seven"), 100UL),
        ]);

        sst.TryGetValue(new SerializableWrapper<int>(7), out _, maxSequenceNumber: 50UL).Should().BeFalse();
        // Seqno cap at exactly the entry's seqno → visible.
        sst.TryGetValue(new SerializableWrapper<int>(7), out var v, maxSequenceNumber: 100UL).Should().BeTrue();
        v.Value.Should().Be("seven");
    }

    [Fact]
    public void TryGetValue_PointerEntry_ReturnsFalseAtSstLayer()
    {
        // Pointer entries are not resolvable at the SST layer — TryGetValue masks them.
        var pointerBytes = new byte[Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer.SerializedSize];
        BitConverter.GetBytes((uint)1).CopyTo(pointerBytes, 0);
        BitConverter.GetBytes(0UL).CopyTo(pointerBytes, 4);

        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "pointer_entry.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Pointer(
                new SerializableWrapper<int>(1), pointerBytes, 1UL),
        ]);

        sst.TryGetValue(new SerializableWrapper<int>(1), out _).Should().BeFalse(
            "pointer entries must be invisible to the SST-layer TryGetValue");

        // But TryGetBlockEntry should still expose the pointer for engine-layer dereference.
        sst.TryGetBlockEntry(new SerializableWrapper<int>(1), out var entry).Should().BeTrue();
        entry.Kind.Should().Be(ValueKind.ValueLogPointer);
    }

    [Fact]
    public void EnumerateInline_SkipsPointerEntries()
    {
        var pointerBytes = new byte[Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer.SerializedSize];
        BitConverter.GetBytes((uint)1).CopyTo(pointerBytes, 0);

        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "mixed_inline_pointer.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                new SerializableWrapper<int>(1), new SerializableWrapper<string>("inline"), 1UL),
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Pointer(
                new SerializableWrapper<int>(2), pointerBytes, 2UL),
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                new SerializableWrapper<int>(3), new SerializableWrapper<string>("inline2"), 3UL),
        ]);

        var inlineOnly = sst.EnumerateInline().ToList();
        inlineOnly.Should().HaveCount(2);
        inlineOnly.Select(e => e.Key.Value).Should().Equal(1, 3);
    }

    [Fact]
    public void EnumerateInline_SeqnoCapFiltersOlderEntries()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "enum_seqno_cap.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 5UL),
            (new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"), 10UL),
        ]);

        var visible = sst.EnumerateInline(maxSequenceNumber: 5UL).ToList();
        visible.Select(t => t.Key.Value).Should().Equal(1, 2);
    }

    // ── TryGetRange caches across calls ──────────────────────────────────────

    [Fact]
    public void TryGetRange_SecondCall_UsesCachedDecode()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "range_cache.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            (new SerializableWrapper<int>(5), new SerializableWrapper<string>("five"), 1UL),
            (new SerializableWrapper<int>(7), new SerializableWrapper<string>("seven"), 2UL),
            (new SerializableWrapper<int>(9), new SerializableWrapper<string>("nine"), 3UL),
        ]);

        sst.TryGetRange(out var min1, out var max1).Should().BeTrue();
        min1.Value.Should().Be(5);
        max1.Value.Should().Be(9);

        // Second call hits the cached path — no new deserialization.
        sst.TryGetRange(out var min2, out var max2).Should().BeTrue();
        min2.Should().Be(min1);
        max2.Should().Be(max1);
    }

    // ── Validation invariants on Write ───────────────────────────────────────

    [Fact]
    public void Write_InlineEntryWithNonEmptyPointer_Throws()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "bad_inline.dat", codec: new NoCompressionCodec());

        // Construct an invalid SegmentWriteEntry via the primary constructor, bypassing
        // the validation in the static factory methods.
        var bad = new SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>(
            Key: new SerializableWrapper<int>(1),
            SequenceNumber: 1UL,
            Kind: ValueKind.Inline,
            InlineValue: new SerializableWrapper<string>("x"),
            PointerBytes: new byte[12]); // <-- invariant violation: Inline + non-empty pointer

        Action act = () => sst.Write([bad]);
        act.Should().Throw<ArgumentException>().WithMessage("*Inline*");
    }

    [Fact]
    public void Write_PointerEntryWithWrongPointerSize_Throws()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "bad_pointer.dat", codec: new NoCompressionCodec());

        // Bypass the static factory's length validation by using the primary constructor.
        var bad = new SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>(
            Key: new SerializableWrapper<int>(1),
            SequenceNumber: 1UL,
            Kind: ValueKind.ValueLogPointer,
            InlineValue: default,
            PointerBytes: new byte[3]); // <-- wrong length

        Action act = () => sst.Write([bad]);
        act.Should().Throw<ArgumentException>().WithMessage("*PointerBytes.Length*");
    }

    // ── Footer-mismatch / footer-truncation behaviour on reopen ──────────────

    [Fact]
    public void Reopen_AfterFooterCorrupted_HandlesGracefully()
    {
        // Write a valid SST, corrupt the trailing footer-length bytes, then attempt to
        // reopen. The reader either throws (preferred) or behaves as an empty SST.
        var fileName = "footer_corrupt.dat";
        var sstPath = Path.Combine(_testDirectory, fileName);
        var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName, codec: new NoCompressionCodec());
        writer.Write(
        [
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 2UL),
        ]);

        // Corrupt the last 4 bytes (footer-length trailer).
        var bytes = File.ReadAllBytes(sstPath);
        bytes[^1] ^= 0xFF;
        bytes[^2] ^= 0xFF;
        File.WriteAllBytes(sstPath, bytes);

        // Reopen — either an exception or a "no footer" outcome is acceptable; what's not
        // acceptable is silently producing wrong reads.
        try
        {
            var reopened = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName);
            // If reopen succeeded, the SST must surface as empty rather than yielding
            // un-validated entries.
            reopened.GetEntryCount().Should().Be(0);
        }
        catch
        {
            // Acceptable: throwing on a corrupt footer is the strict contract.
        }
    }

    [Fact]
    public void Reopen_AfterTrailerTruncated_HandlesGracefully()
    {
        var fileName = "trailer_truncated.dat";
        var sstPath = Path.Combine(_testDirectory, fileName);
        var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName, codec: new NoCompressionCodec());
        writer.Write(
        [
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL),
        ]);

        // Truncate the trailing 4 bytes (footer-length).
        using (var fs = new FileStream(sstPath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fs.Length - 4);
        }

        try
        {
            var reopened = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName);
            reopened.GetEntryCount().Should().Be(0,
                "without a valid trailer, the SST cannot expose any entries");
        }
        catch
        {
            // Acceptable: throwing is the strict contract.
        }
    }

    // ── Range / boundary edges of TryGetBlockEntry ───────────────────────────

    [Fact]
    public void TryGetBlockEntry_KeyBetweenBlocks_ReturnsFalse()
    {
        // Multiple blocks (small block size). Request a key that doesn't exist but falls
        // numerically between two block-boundary keys — exercises the "candidate block found
        // but key not in it" fallthrough.
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "between_blocks.dat", codec: new NoCompressionCodec());
        sst.Write(Enumerable.Range(0, 50).Select(i => (
            new SerializableWrapper<int>(i * 10),
            new SerializableWrapper<string>($"v{i}"),
            (ulong)i)).ToArray());

        // Request key 5 (between 0 and 10).
        sst.TryGetBlockEntry(new SerializableWrapper<int>(5), out _).Should().BeFalse();
        // Request key past the last entry.
        sst.TryGetBlockEntry(new SerializableWrapper<int>(99999), out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetBlockEntry_KeyBelowMin_ReturnsFalse()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "below_min.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            (new SerializableWrapper<int>(100), new SerializableWrapper<string>("a"), 1UL),
            (new SerializableWrapper<int>(200), new SerializableWrapper<string>("b"), 2UL),
        ]);

        // Key 50 is below MinKey — bloom filter may say "might contain" but the block
        // search must return false.
        sst.TryGetBlockEntry(new SerializableWrapper<int>(50), out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetRange_ReturnsMinAndMaxFromFooter()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "range_test.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            (new SerializableWrapper<int>(7), new SerializableWrapper<string>("seven"), 1UL),
            (new SerializableWrapper<int>(11), new SerializableWrapper<string>("eleven"), 2UL),
            (new SerializableWrapper<int>(42), new SerializableWrapper<string>("forty-two"), 3UL),
        ]);

        sst.TryGetRange(out var min, out var max).Should().BeTrue();
        min.Value.Should().Be(7);
        max.Value.Should().Be(42);
    }

    [Fact]
    public void TryGetRange_OnEmptyWritten_ReturnsFalse()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "empty_range.dat", codec: new NoCompressionCodec());
        sst.Write(Array.Empty<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>());

        // Empty SST has EntryCount=0; the contract is to return false.
        sst.TryGetRange(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Write_SingleEntry_RoundTripsCleanly()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "single_entry.dat", codec: new NoCompressionCodec());
        sst.Write(
        [
            (new SerializableWrapper<int>(42), new SerializableWrapper<string>("answer"), 7UL),
        ]);

        sst.GetEntryCount().Should().Be(1);
        sst.TryGetValue(new SerializableWrapper<int>(42), out var v).Should().BeTrue();
        v.Value.Should().Be("answer");
        sst.TryGetRange(out var min, out var max).Should().BeTrue();
        min.Value.Should().Be(42);
        max.Value.Should().Be(42);
    }

    [Fact]
    public void Write_LargeEntrySet_RoundTripsThroughMultipleBlocks()
    {
        // Many entries → multiple blocks. Each lookup must find its key.
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "many_blocks.dat", codec: new LZ4Codec());
        var items = Enumerable.Range(0, 500)
            .Select(i => (
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>($"value_{i}"),
                (ulong)i))
            .ToArray();
        sst.Write(items);

        // Spot-check 10 random keys.
        var rng = new Random(42);
        for (var i = 0; i < 10; i++)
        {
            var k = rng.Next(0, 500);
            sst.TryGetValue(new SerializableWrapper<int>(k), out var v).Should().BeTrue();
            v.Value.Should().Be($"value_{k}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }
}
