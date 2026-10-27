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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// WAL tombstone + seqno round-trip tests for the v2.0.0 single-format WAL.
/// </summary>
public sealed class WriteAheadLogTombstoneTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"WalTombstone_{Guid.NewGuid():N}");

    // The directory containing the rolling WAL segments (D2). Tests use the directory as the
    // WAL handle; segments are wal_NNNNNN.log inside.
    private string WalPath => _testDirectory;

    // The first (and only, for these small-volume tests) segment file inside WalPath. Used
    // for byte-level inspection / corruption tests.
    private string FirstSegmentPath => Path.Combine(WalPath, "wal_000001.log");

    public WriteAheadLogTombstoneTests()
    {
        Directory.CreateDirectory(_testDirectory);
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
            // Best-effort cleanup.
        }
    }

    private static readonly byte[] s_magic = [(byte)'B', (byte)'S', (byte)'W', (byte)'L'];

    /// <summary>
    /// AppendDeleteAsync writes a tombstone frame that round-trips through RecoverAsync with Kind=Delete.
    /// </summary>
    [Fact]
    public async Task AppendDeleteAsync_RoundTripsWithKindDelete()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalPath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha"), 1UL);
            await wal.AppendDeleteAsync(new SerializableWrapper<int>(1), 2UL);
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("beta"), 3UL);
        }

        using var readWal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalPath);
        var entries = await readWal.RecoverAsync();

        entries.Should().HaveCount(3);
        entries[0].Kind.Should().Be(EntryKind.Set);
        entries[0].IsTombstone.Should().BeFalse();
        entries[0].Key.Value.Should().Be(1);
        entries[0].InlineValue!.Value.Should().Be("alpha");

        entries[1].Kind.Should().Be(EntryKind.Delete);
        entries[1].IsTombstone.Should().BeTrue();
        entries[1].Key.Value.Should().Be(1);

        entries[2].Kind.Should().Be(EntryKind.Set);
        entries[2].Key.Value.Should().Be(2);
    }

    /// <summary>
    /// A brand-new WAL file starts with the 8-byte header [magic 'BSWL'][format_version=1 LE uint32].
    /// Format-version 1 is the first persisted WAL format under the greenfield premise — it uses
    /// Crc32C (Castagnoli polynomial) for the per-frame checksum, with the CRC scope covering
    /// [entry_kind][value_kind][seqno][payload].
    /// </summary>
    [Fact]
    public async Task AppendAsync_OnFreshFile_WritesFileHeader()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalPath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha"), 1UL);
        }

        var bytes = await File.ReadAllBytesAsync(FirstSegmentPath);
        bytes.Length.Should().BeGreaterThanOrEqualTo(8);
        bytes.AsSpan(0, 4).ToArray().Should().Equal(s_magic);
        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        version.Should().Be(1u);
    }

    /// <summary>
    /// Interleaving Set / Delete for the same key round-trips in position order so the caller
    /// can reconstruct last-writer-wins semantics during recovery.
    /// </summary>
    [Fact]
    public async Task SetThenDeleteThenSet_PreservesPositionOrder()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalPath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("a"), 1UL);
            await wal.AppendDeleteAsync(new SerializableWrapper<int>(5), 2UL);
            await wal.AppendAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("b"), 3UL);
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalPath);
        var entries = await wal2.RecoverAsync();

        entries.Should().HaveCount(3);
        entries[0].Kind.Should().Be(EntryKind.Set);
        entries[0].InlineValue!.Value.Should().Be("a");
        entries[1].Kind.Should().Be(EntryKind.Delete);
        entries[2].Kind.Should().Be(EntryKind.Set);
        entries[2].InlineValue!.Value.Should().Be("b");
    }

    /// <summary>
    /// H3 (Cluster 1): non-magic leading bytes on a non-empty WAL file raise the typed
    /// WriteAheadLogCorruptException naming the segment — there is no fallback reader in v2.0.0.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_OnFileWithoutMagic_ThrowsWriteAheadLogCorrupt()
    {
        // Pre-create the segment file with garbage bytes so the recovery path observes the
        // missing 'BSWL' magic and rejects it (no fallback reader in v2.0.0).
        Directory.CreateDirectory(WalPath);
        await File.WriteAllBytesAsync(FirstSegmentPath, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A]);

        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalPath);
        var act = () => wal.RecoverAsync();
        (await act.Should().ThrowAsync<WriteAheadLogCorruptException>())
            .Which.SegmentPath.Should().Be(FirstSegmentPath);
    }

    /// <summary>
    /// H8: AppendAsync persists the engine-assigned sequence number on the wire so
    /// RecoverAsync surfaces it on the resulting <see cref="WalEntry{TKey,TValue}"/>.
    /// </summary>
    [Fact]
    public async Task AppendAsync_PersistsSequenceNumber_RecoverAsyncReturnsIt()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalPath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 100UL);
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 101UL);
            await wal.AppendDeleteAsync(new SerializableWrapper<int>(1), 102UL);
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalPath);
        var entries = await wal2.RecoverAsync();

        entries.Should().HaveCount(3);
        entries[0].SequenceNumber.Should().Be(100UL);
        entries[1].SequenceNumber.Should().Be(101UL);
        entries[2].SequenceNumber.Should().Be(102UL);
        entries[2].Kind.Should().Be(EntryKind.Delete);
    }
}
