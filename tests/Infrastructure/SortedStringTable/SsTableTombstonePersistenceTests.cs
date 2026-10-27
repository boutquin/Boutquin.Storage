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
namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// Pins the tombstone-persistence contract:
/// <list type="bullet">
///   <item><description>The footer carries an explicit tombstone list (deleted key →
///   deletion seqno).</description></item>
///   <item><description>The footer round-trips through the <c>Serialize</c> /
///   <c>Deserialize</c> path bit-for-bit.</description></item>
///   <item><description><see cref="Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable{TKey, TValue}.Write(System.Collections.Generic.IEnumerable{Boutquin.Storage.Domain.ValueObjects.SortedStringTable.SegmentWriteEntry{TKey, TValue}}, System.Collections.Generic.IReadOnlyDictionary{TKey, ulong}?)"/>
///   pipes the supplied tombstone map all the way through to disk so a re-opened
///   <see cref="Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable{TKey, TValue}.GetTombstoneSeqnos"/>
///   returns the same key / seqno pairs.</description></item>
/// </list>
/// Reference: DDIA Ch. 4 p. 124 — without the seqno the compactor cannot safely reclaim
/// tombstones, so the markers accumulate forever.
/// </summary>
public sealed class SsTableTombstonePersistenceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(), $"SsTableTombstonePersistence_{Guid.NewGuid():N}");

    public SsTableTombstonePersistenceTests() => Directory.CreateDirectory(_testDirectory);

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
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Footer_Tombstones_RoundTripExactly()
    {
        var tombstones = new[]
        {
            new SsTableTombstoneEntry(new byte[] { 1, 2, 3 }, 42UL),
            new SsTableTombstoneEntry(new byte[] { 4, 5 }, 123UL),
            new SsTableTombstoneEntry("\t"u8.ToArray(), ulong.MaxValue),
        };
        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion,
            CodecId: 0x01,
            EntryCount: 3,
            MinKey: new byte[] { 1, 2, 3 },
            MaxKey: "\t"u8.ToArray(),
            MaxSequenceNumber: 200UL,
            BlockIndex: [],
            BloomBitsCount: 0,
            BloomHashCount: 0,
            BloomBits: Array.Empty<byte>(),
            Tombstones: tombstones);

        var bytes = footer.Serialize();
        var parsed = SsTableFooter.Deserialize(bytes);

        parsed.Tombstones.Should().HaveCount(3);
        parsed.Tombstones[0].Key.ToArray().Should().Equal(1, 2, 3);
        parsed.Tombstones[0].SequenceNumber.Should().Be(42UL);
        parsed.Tombstones[1].Key.ToArray().Should().Equal(4, 5);
        parsed.Tombstones[1].SequenceNumber.Should().Be(123UL);
        parsed.Tombstones[2].Key.ToArray().Should().Equal("\t"u8.ToArray());
        parsed.Tombstones[2].SequenceNumber.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void Footer_NoTombstones_RoundTripsEmptyList()
    {
        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion,
            CodecId: 0x01,
            EntryCount: 0,
            MinKey: ReadOnlyMemory<byte>.Empty,
            MaxKey: ReadOnlyMemory<byte>.Empty,
            MaxSequenceNumber: 0UL,
            BlockIndex: [],
            BloomBitsCount: 0,
            BloomHashCount: 0,
            BloomBits: Array.Empty<byte>(),
            Tombstones: []);

        var parsed = SsTableFooter.Deserialize(footer.Serialize());

        parsed.Tombstones.Should().BeEmpty();
    }

    [Fact]
    public void SortedStringTable_WriteWithTombstones_RoundTripsViaGetTombstoneSeqnos()
    {
        var tombstones = new Dictionary<SerializableWrapper<int>, ulong>
        {
            { new SerializableWrapper<int>(7), 42UL },
            { new SerializableWrapper<int>(11), 99UL },
        };

        var sst = new global::Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<
            SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "tombstone_roundtrip.sst");

        var entries = new[]
        {
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"), 1UL),
        };

        sst.Write(entries, tombstones);

        var got = sst.GetTombstoneSeqnos();
        got.Should().HaveCount(2);
        got[new SerializableWrapper<int>(7)].Should().Be(42UL);
        got[new SerializableWrapper<int>(11)].Should().Be(99UL);
    }

    [Fact]
    public void SortedStringTable_ReOpen_RestoresTombstoneSeqnosFromFooter()
    {
        var tombstones = new Dictionary<SerializableWrapper<int>, ulong>
        {
            { new SerializableWrapper<int>(50), 200UL },
        };

        // First instance writes the segment with tombstones.
        {
            var sst = new global::Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<
                SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, "tombstone_reopen.sst");
            var entries = new[]
            {
                SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                    new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"), 1UL),
            };
            sst.Write(entries, tombstones);
        }

        // Re-open and confirm the tombstones survived to disk.
        var reopened = new global::Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<
            SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "tombstone_reopen.sst");

        var got = reopened.GetTombstoneSeqnos();
        got.Should().HaveCount(1);
        got[new SerializableWrapper<int>(50)].Should().Be(200UL,
            "the deletion seqno is what makes compaction's tombstone-GC step safe — it must " +
            "survive to disk and recover bit-exact on re-open");
    }

    [Fact]
    public void SortedStringTable_WriteWithoutTombstones_GetTombstoneSeqnosReturnsEmpty()
    {
        var sst = new global::Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<
            SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "no_tombstones.sst");

        var entries = new[]
        {
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"), 1UL),
        };
        sst.Write(entries);

        sst.GetTombstoneSeqnos().Should().BeEmpty();
    }
}
