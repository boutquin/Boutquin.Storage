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
/// P4.6 — TDD tests for within-block binary search via restart points.
/// Verifies that: (a) <see cref="BlockFormat.ParseRestartOffsets"/> correctly extracts
/// byte offsets from the block tail; (b) the SSTable <c>TryGetBlockEntry</c> path
/// correctly finds keys in every restart section of a multi-section block.
/// </summary>
public sealed class BlockRestartBinarySearchTests : IDisposable
{
    private readonly string _testDirectory =
        Path.Combine(Path.GetTempPath(), $"BlockRestartBinarySearchTests_{Guid.NewGuid():N}");

    public BlockRestartBinarySearchTests() => Directory.CreateDirectory(_testDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, recursive: true); }
        catch (IOException) { /* test-runner cleanup — ignore */ }
    }

    // ── BlockFormat.ParseRestartOffsets ──────────────────────────────────────

    [Fact]
    public void ParseRestartOffsets_SingleRestartSection_ReturnsOffset0()
    {
        // A block with < restartInterval entries has exactly 1 restart point at offset 0.
        var entries = Enumerable.Range(0, 4)
            .Select(i => new BlockEntry(new byte[] { (byte)i }, (ulong)i, ValueKind.Inline, new byte[] { (byte)i }))
            .ToArray();

        var framed = BlockFormat.WriteBlock(entries, new NoCompressionCodec(), restartInterval: 16);
        using var stream = new MemoryStream(framed);
        var payload = BlockFormat.DecompressPayload(framed, new NoCompressionCodec());

        var offsets = BlockFormat.ParseRestartOffsets(payload);

        offsets.Should().HaveCount(1, "fewer than 16 entries means exactly one restart point");
        offsets[0].Should().Be(0, "first restart point is always at offset 0");
    }

    [Fact]
    public void ParseRestartOffsets_MultipleRestartSections_ReturnsAllOffsets()
    {
        // 33 entries with restartInterval=16 → restart points at indices 0, 16, 32.
        var entries = Enumerable.Range(0, 33)
            .Select(i => new BlockEntry(
                System.BitConverter.GetBytes(i),
                (ulong)i,
                ValueKind.Inline,
                new byte[] { (byte)(i & 0xFF) }))
            .ToArray();

        var framed = BlockFormat.WriteBlock(entries, new NoCompressionCodec(), restartInterval: 16);
        var payload = BlockFormat.DecompressPayload(framed, new NoCompressionCodec());

        var offsets = BlockFormat.ParseRestartOffsets(payload);

        offsets.Should().HaveCount(3, "33 entries / 16 interval = 3 restart points (at indices 0, 16, 32)");
        offsets[0].Should().Be(0, "first restart always at offset 0");
        offsets[1].Should().BeGreaterThan(0, "second restart is deeper in the payload");
        offsets[2].Should().BeGreaterThan(offsets[1], "third restart is deeper than second");
    }

    [Fact]
    public void ParseRestartOffsets_EmptyBlock_ReturnsEmptyArray()
    {
        var framed = BlockFormat.WriteBlock(
            [],
            new NoCompressionCodec(),
            restartInterval: 16);
        var payload = BlockFormat.DecompressPayload(framed, new NoCompressionCodec());

        // An empty block has restart_count = 0 (loop never executes, no restart recorded).
        var offsets = BlockFormat.ParseRestartOffsets(payload);
        offsets.Should().BeEmpty();
    }

    // ── SortedStringTable TryGetBlockEntry with multi-section blocks ──────────

    [Fact]
    public void TryGetBlockEntry_FindsKeyInFirstRestartSection()
    {
        var sst = MakeSstWith50Entries("first_section.sst");

        // Key 5 is in the first restart section (entries 0..15).
        var found = sst.TryGetBlockEntry(new SerializableWrapper<int>(5), out var entry);

        found.Should().BeTrue();
        entry.SequenceNumber.Should().Be(5UL);
        sst.Dispose();
    }

    [Fact]
    public void TryGetBlockEntry_FindsKeyInSecondRestartSection()
    {
        var sst = MakeSstWith50Entries("second_section.sst");

        // Key 20 is in the second restart section (entries 16..31).
        var found = sst.TryGetBlockEntry(new SerializableWrapper<int>(20), out var entry);

        found.Should().BeTrue();
        entry.SequenceNumber.Should().Be(20UL);
        sst.Dispose();
    }

    [Fact]
    public void TryGetBlockEntry_FindsKeyInThirdRestartSection()
    {
        var sst = MakeSstWith50Entries("third_section.sst");

        // Key 35 is in the third restart section (entries 32..47).
        var found = sst.TryGetBlockEntry(new SerializableWrapper<int>(35), out var entry);

        found.Should().BeTrue();
        entry.SequenceNumber.Should().Be(35UL);
        sst.Dispose();
    }

    [Fact]
    public void TryGetBlockEntry_MissingKey_ReturnsFalse()
    {
        var sst = MakeSstWith50Entries("missing_key.sst");

        // Write only keys 0..49; key 99 is not present.
        var found = sst.TryGetBlockEntry(new SerializableWrapper<int>(99), out _);

        found.Should().BeFalse();
        sst.Dispose();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private SortedStringTable<SerializableWrapper<int>, SerializableWrapper<int>> MakeSstWith50Entries(string name)
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<int>>(
            _testDirectory, name, new NoCompressionCodec());

        // Write 50 entries (keys 0..49) forcing 3 restart sections with restartInterval=16.
        sst.Write(Enumerable.Range(0, 50)
            .Select(i => (new SerializableWrapper<int>(i), new SerializableWrapper<int>(i * 10), (ulong)i)));
        return sst;
    }
}
