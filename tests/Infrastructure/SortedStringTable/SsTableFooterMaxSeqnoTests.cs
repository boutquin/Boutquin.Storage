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
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// P4.5 — TDD tests for footer-carried <c>MaxSequenceNumber</c>.
/// Verifies: (a) the footer record round-trips a non-zero value; (b) the
/// SSTable reopen path reads <c>MaxSequenceNumber</c> directly from the footer
/// without scanning blocks; (c) <c>CurrentFormatVersion</c> is 1.
/// </summary>
public sealed class SsTableFooterMaxSeqnoTests : IDisposable
{
    private readonly string _testDirectory =
        Path.Combine(Path.GetTempPath(), $"SsTableFooterMaxSeqnoTests_{Guid.NewGuid():N}");

    public SsTableFooterMaxSeqnoTests() => Directory.CreateDirectory(_testDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, recursive: true); }
        catch (IOException) { /* test-runner cleanup — ignore */ }
    }

    // ── footer serialization ─────────────────────────────────────────────────

    [Fact]
    public void Footer_RoundTrips_MaxSequenceNumber_NonZero()
    {
        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion,
            CodecId: 0x01,
            EntryCount: 3,
            MinKey: new byte[] { 1 },
            MaxKey: new byte[] { 3 },
            MaxSequenceNumber: 42UL,
            BlockIndex: [],
            BloomBitsCount: 0,
            BloomHashCount: 0,
            BloomBits: Array.Empty<byte>(), Tombstones: []);

        var bytes = footer.Serialize();
        var parsed = SsTableFooter.Deserialize(bytes);

        parsed.MaxSequenceNumber.Should().Be(42UL);
    }

    [Fact]
    public void Footer_RoundTrips_MaxSequenceNumber_Zero()
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
            BloomBits: Array.Empty<byte>(), Tombstones: []);

        var bytes = footer.Serialize();
        var parsed = SsTableFooter.Deserialize(bytes);

        parsed.MaxSequenceNumber.Should().Be(0UL);
    }

    [Fact]
    public void Footer_RoundTrips_MaxSequenceNumber_MaxValue()
    {
        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion,
            CodecId: 0x01,
            EntryCount: 1,
            MinKey: new byte[] { 1 },
            MaxKey: new byte[] { 1 },
            MaxSequenceNumber: ulong.MaxValue,
            BlockIndex: [],
            BloomBitsCount: 0,
            BloomHashCount: 0,
            BloomBits: Array.Empty<byte>(), Tombstones: []);

        var bytes = footer.Serialize();
        var parsed = SsTableFooter.Deserialize(bytes);

        parsed.MaxSequenceNumber.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void Footer_CurrentFormatVersion_Is1()
    {
        // On-disk format versions start at 1 at GA under the greenfield premise (finding #10).
        SsTableFooter.CurrentFormatVersion.Should().Be(1);
    }

    // ── SSTable reopen path ───────────────────────────────────────────────────

    [Fact]
    public void SortedStringTable_ReOpen_ReadsMaxSeqnoFromFooter()
    {
        // Write an SSTable with entries carrying known sequence numbers.
        var sst1 = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<int>>(
            _testDirectory, "maxseqno.sst", new NoCompressionCodec());
        sst1.Write(
        [
            (new SerializableWrapper<int>(1), new SerializableWrapper<int>(10), 5UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<int>(20), 12UL),
            (new SerializableWrapper<int>(3), new SerializableWrapper<int>(30), 8UL),
        ]);
        sst1.Dispose();

        // Reopen the file in a fresh instance — simulates engine restart.
        var sst2 = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<int>>(
            _testDirectory, "maxseqno.sst", new NoCompressionCodec());

        sst2.MaxSequenceNumber.Should().Be(12UL, "highest seqno across all entries is 12");
        sst2.Dispose();
    }

    [Fact]
    public void SortedStringTable_Write_MaxSeqnoMatchesHighestEntry()
    {
        // Verify the SSTable immediately after Write (no reopen) reports correct MaxSeqno.
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<int>>(
            _testDirectory, "write_maxseqno.sst", new NoCompressionCodec());
        sst.Write(
        [
            (new SerializableWrapper<int>(1), new SerializableWrapper<int>(10), 3UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<int>(20), 99UL),
            (new SerializableWrapper<int>(3), new SerializableWrapper<int>(30), 1UL),
        ]);

        sst.MaxSequenceNumber.Should().Be(99UL, "highest seqno written is 99");
        sst.Dispose();
    }
}
