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

public sealed class ValueLogManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ValueLogManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "valuelog.manifest");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task NewManifest_StartsEmpty()
    {
        await using var manifest = new ValueLogManifest(_path);

        (await manifest.GetLiveFilesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NewManifest_WritesHeaderOnCreation()
    {
        await using (var _ = new ValueLogManifest(_path))
        {
            // dispose to release the stream
        }

        var bytes = File.ReadAllBytes(_path);
        bytes.Length.Should().Be(ValueLogManifest.HeaderSize);
        bytes[0..4].Should().Equal(ValueLogManifest.Magic);
        BitConverter.ToUInt32(bytes, 4).Should().Be(ValueLogManifest.CurrentFormatVersion);
    }

    /// <summary>
    /// Creating the manifest over a missing directory tree creates each directory level and the
    /// file; every created entry must be made durable (two directory levels plus the manifest's own
    /// entry), or a crash can drop the subtree together with the manifest and every segment it names.
    /// </summary>
    [Fact]
    public async Task Construct_OverMissingDirectoryTree_FsyncsEveryCreatedEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_dir, "level-1", "level-2", "valuelog.manifest");

        await using var manifest = new ValueLogManifest(path);

        File.Exists(path).Should().BeTrue();
        manifest._directoryFsyncCount.Should().Be(3,
            "the two created directory levels and the manifest's own directory entry are each fsync'd");
    }

    [Fact]
    public async Task RecordAddAsync_AppearsInLiveFiles()
    {
        await using var manifest = new ValueLogManifest(_path);

        await manifest.RecordAddAsync(fileIndex: 7);

        (await manifest.GetLiveFilesAsync()).Should().Contain(7u);
    }

    [Fact]
    public async Task RecordDeleteAsync_RemovesFromLiveFiles()
    {
        await using var manifest = new ValueLogManifest(_path);
        await manifest.RecordAddAsync(7);

        await manifest.RecordDeleteAsync(7);

        (await manifest.GetLiveFilesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_RecoversLiveSet()
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(1);
            await first.RecordAddAsync(2);
            await first.RecordDeleteAsync(1);
            await first.RecordAddAsync(3);
        }

        await using var reopened = new ValueLogManifest(_path);

        var live = await reopened.GetLiveFilesAsync();
        live.Should().HaveCount(2);
        live.Should().Contain(2u);
        live.Should().Contain(3u);
    }

    [Fact]
    public async Task Replay_TruncatesTrailingPartialRecord()
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(1);
        }

        // Append 5 junk bytes (less than RecordSize=9) to simulate a crash mid-append.
        using (var junk = new FileStream(_path, FileMode.Append, FileAccess.Write))
        {
            junk.Write([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        }

        await using var reopened = new ValueLogManifest(_path);

        (await reopened.GetLiveFilesAsync()).Should().Contain(1u);
        new FileInfo(_path).Length.Should().Be(ValueLogManifest.HeaderSize + ValueLogManifest.RecordSize);
    }

    [Fact]
    public async Task Replay_MidFileCrcFailure_IsFatal()
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(1);
            await first.RecordAddAsync(2);
            await first.RecordAddAsync(3);
        }

        // Flip a byte inside the second of three records: a later, acknowledged record follows it,
        // so this is mid-file corruption (offset HeaderSize + RecordSize + some middle).
        using (var rw = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            rw.Seek(ValueLogManifest.HeaderSize + ValueLogManifest.RecordSize + 2, SeekOrigin.Begin);
            rw.WriteByte(0xAB); // bit-flip the fileIndex byte
        }

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<CorruptValueLogException>();
    }

    [Fact]
    public async Task Replay_BadMagic_Throws()
    {
        await using (var _ = new ValueLogManifest(_path))
        {
        }

        using (var rw = new FileStream(_path, FileMode.Open, FileAccess.Write))
        {
            rw.Seek(0, SeekOrigin.Begin);
            rw.WriteByte(0x00);
        }

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<UnrecognisedValueLogFormatException>();
    }

    [Fact]
    public async Task Replay_UnknownVersion_Throws()
    {
        await using (var _ = new ValueLogManifest(_path))
        {
        }

        using (var rw = new FileStream(_path, FileMode.Open, FileAccess.Write))
        {
            rw.Seek(4, SeekOrigin.Begin);
            rw.Write(BitConverter.GetBytes((uint)99));
        }

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<UnrecognisedValueLogFormatException>();
    }

    [Fact]
    public async Task RecordAddAsync_RejectsDuplicateLiveIndex()
    {
        await using var manifest = new ValueLogManifest(_path);
        await manifest.RecordAddAsync(1);

        Func<Task> act = () => manifest.RecordAddAsync(1);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordDeleteAsync_RejectsUnknownIndex()
    {
        await using var manifest = new ValueLogManifest(_path);

        Func<Task> act = () => manifest.RecordDeleteAsync(999);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Replay_ZeroFilledFinalFullRecord_IsTruncatedAsTornTail()
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(1);
        }

        // A crash during the final record's write+fsync can leave a full-length record whose bytes
        // never reached the disk: zero-filled (the file length landed, the data did not). It was
        // never acknowledged.
        using (var append = new FileStream(_path, FileMode.Append, FileAccess.Write))
        {
            append.Write(new byte[ValueLogManifest.RecordSize]);
        }

        ValueLogManifest? reopened = null;
        Action act = () => reopened = new ValueLogManifest(_path);

        act.Should().NotThrow("an all-zero final record is an unwritten extent, not mid-file corruption");
        await using (reopened)
        {
            (await reopened!.GetLiveFilesAsync()).Should().BeEquivalentTo([1u]);
        }
        new FileInfo(_path).Length.Should().Be(ValueLogManifest.HeaderSize + ValueLogManifest.RecordSize,
            "the torn record is cut back to the last good record boundary");
    }

    /// <summary>
    /// A crash during the final record's write can land only its first bytes while the file length
    /// covers the whole record: a record prefix followed by zeros to end of file. That record was
    /// never acknowledged (each record is fsync'd before its append returns), so it is a torn tail.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(23)]
    public async Task Replay_FinalDeleteRecordPrefixThenZeros_IsTruncatedAsTornTail(int landedBytes)
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(0);
            await first.RecordAddAsync(1);
            await first.RecordAddAsync(2);
            await first.RecordDeleteAsync(1);
        }

        var bytes = File.ReadAllBytes(_path);
        Array.Clear(bytes, bytes.Length - ValueLogManifest.RecordSize + landedBytes, ValueLogManifest.RecordSize - landedBytes);
        File.WriteAllBytes(_path, bytes);

        ValueLogManifest? reopened = null;
        Action act = () => reopened = new ValueLogManifest(_path);

        act.Should().NotThrow("a record prefix followed only by zeros to end of file is the unwritten rest of the final append");
        await using (reopened)
        {
            (await reopened!.GetLiveFilesAsync()).Should().BeEquivalentTo([0u, 1u, 2u], "the unacknowledged delete is dropped");
        }

        new FileInfo(_path).Length.Should().Be(ValueLogManifest.HeaderSize + (3 * ValueLogManifest.RecordSize));
    }

    /// <summary>
    /// The same tear for an unacknowledged final add: its first bytes landed and zeros fill the rest
    /// of the record. The add's segment holds no frames, so nothing acknowledged depends on it.
    /// </summary>
    [Fact]
    public async Task Replay_FinalAddRecordPrefixThenZeros_IsTruncatedAsTornTail()
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(0);
            await first.RecordAddAsync(1);
        }

        var bytes = File.ReadAllBytes(_path);
        Array.Clear(bytes, bytes.Length - ValueLogManifest.RecordSize + 5, ValueLogManifest.RecordSize - 5);
        File.WriteAllBytes(_path, bytes);

        ValueLogManifest? reopened = null;
        Action act = () => reopened = new ValueLogManifest(_path);

        act.Should().NotThrow("the final add's landed prefix is followed only by zeros");
        await using (reopened)
        {
            (await reopened!.GetLiveFilesAsync()).Should().BeEquivalentTo([0u]);
        }

        new FileInfo(_path).Length.Should().Be(ValueLogManifest.HeaderSize + ValueLogManifest.RecordSize);
    }

    /// <summary>
    /// Must fail closed: the landed prefix must be one a writer could produce. A kind byte this format
    /// does not define, followed by zeros, is damage, not a torn append.
    /// </summary>
    [Fact]
    public async Task Replay_FinalRecordPrefixWithUnknownKindThenZeros_FailsClosed()
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(0);
            await first.RecordAddAsync(1);
        }

        var bytes = File.ReadAllBytes(_path);
        var start = bytes.Length - ValueLogManifest.RecordSize;
        Array.Clear(bytes, start, ValueLogManifest.RecordSize);
        bytes[start] = 0x7E;
        File.WriteAllBytes(_path, bytes);

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<CorruptValueLogException>();
        File.ReadAllBytes(_path).Should().Equal(bytes);
    }

    [Fact]
    public async Task Replay_GarbageFinalFullRecord_FailsClosed()
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(1);
        }

        // A complete final record whose bytes fail the checksum covering every field is damage,
        // not a torn append: the checksum is what authenticates the record's kind and index.
        var garbage = new byte[ValueLogManifest.RecordSize];
        new Random(17).NextBytes(garbage);
        using (var append = new FileStream(_path, FileMode.Append, FileAccess.Write))
        {
            append.Write(garbage);
        }
        var lengthBefore = new FileInfo(_path).Length;

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<CorruptValueLogException>();
        new FileInfo(_path).Length.Should().Be(lengthBefore);
    }

    /// <summary>
    /// One bit of rot in the kind byte or any fileIndex byte of the final, acknowledged add record.
    /// The record is complete and fails its checksum, so it is corruption, not a torn append —
    /// whatever segment its damaged bytes now name. Reopening must fail closed and leave both the
    /// manifest and the live segment untouched.
    /// </summary>
    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(0, 0x02)]
    [InlineData(0, 0x80)]
    [InlineData(1, 0x01)]
    [InlineData(1, 0x40)]
    [InlineData(2, 0x01)]
    [InlineData(3, 0x10)]
    [InlineData(4, 0x80)]
    public async Task Replay_RotInKindOrIndexOfFinalAdd_FailsClosed(int byteInRecord, byte mask)
    {
        var segment = await WriteThreeAcknowledgedValuesAsync().ConfigureAwait(true);
        var segmentBytes = await File.ReadAllBytesAsync(segment).ConfigureAwait(true);
        FlipFinalRecordByte(byteInRecord, mask);
        var manifestBytes = await File.ReadAllBytesAsync(_path).ConfigureAwait(true);

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<CorruptValueLogException>(
            "a complete final record that fails its checksum is rot of acknowledged history");
        (await File.ReadAllBytesAsync(_path).ConfigureAwait(true)).Should().Equal(manifestBytes);
        (await File.ReadAllBytesAsync(segment).ConfigureAwait(true)).Should().Equal(segmentBytes);
    }

    /// <summary>
    /// The same rule for the final DELETE record (class census of the final-record mechanism): a
    /// complete record that fails its checksum is rot, so reopening fails closed instead of dropping
    /// the tombstone and resurrecting a reclaimed segment whose file is already gone.
    /// </summary>
    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(1, 0x01)]
    [InlineData(4, 0x20)]
    [InlineData(6, 0x08)]
    public async Task Replay_RotInFinalDeleteRecord_FailsClosed(int byteInRecord, byte mask)
    {
        await using (var m = new ValueLogManifest(_path))
        {
            await m.RecordAddAsync(0);
            await m.RecordAddAsync(1);
            await m.RecordDeleteAsync(1);
        }

        FlipFinalRecordByte(byteInRecord, mask);
        var manifestBytes = await File.ReadAllBytesAsync(_path).ConfigureAwait(true);

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<CorruptValueLogException>(
            "a complete final record that fails its checksum is rot, whatever its damaged kind byte now says");
        (await File.ReadAllBytesAsync(_path).ConfigureAwait(true)).Should().Equal(manifestBytes);
    }

    /// <summary>
    /// A complete final record whose CRC32C has a zero most-significant byte ends in 0x00 — about
    /// one record in 256 — so "its last byte is zero" cannot by itself mark it as an unwritten
    /// prefix. A real prefix that reaches into the CRC carries the leading bytes of the CRC over
    /// the fields before it; rot in those fields leaves landed CRC bytes that disagree with them,
    /// so reopening must fail closed and leave the manifest untouched. Index 29 (add) and 574
    /// (delete) are records whose CRC top byte is zero; the test asserts that precondition.
    /// </summary>
    [Theory]
    [InlineData(29u, false)]
    [InlineData(574u, true)]
    public async Task Replay_RotInCompleteFinalRecordWithZeroCrcTopByte_FailsClosed(uint fileIndex, bool withDelete)
    {
        await using (var m = new ValueLogManifest(_path))
        {
            await m.RecordAddAsync(fileIndex);
            if (withDelete)
            {
                await m.RecordDeleteAsync(fileIndex);
            }
        }

        (await File.ReadAllBytesAsync(_path).ConfigureAwait(true))[^1].Should().Be(
            0, "the fixture needs a final record whose CRC top byte is zero");
        FlipFinalRecordByte(2, 0x01);
        var manifestBytes = await File.ReadAllBytesAsync(_path).ConfigureAwait(true);

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<CorruptValueLogException>(
            "a complete record whose landed CRC bytes disagree with its fields is rot, not a torn prefix");
        (await File.ReadAllBytesAsync(_path).ConfigureAwait(true)).Should().Equal(manifestBytes);
    }

    /// <summary>
    /// The genuine tear the zero-CRC-byte rule must still accept: a final record whose trailing
    /// CRC bytes never landed (zeroed) while every landed CRC byte matches the CRC of its fields is
    /// the prefix an interrupted append leaves, and replay drops it.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Replay_FinalRecordWithUnlandedTrailingCrcBytes_IsDroppedAsTornTail(int unlandedCrcBytes)
    {
        await using (var m = new ValueLogManifest(_path))
        {
            await m.RecordAddAsync(30);
            await m.RecordDeleteAsync(30);
        }

        var bytes = await File.ReadAllBytesAsync(_path).ConfigureAwait(true);
        bytes.AsSpan(bytes.Length - unlandedCrcBytes).Clear();
        await File.WriteAllBytesAsync(_path, bytes).ConfigureAwait(true);

        await using (var m = new ValueLogManifest(_path))
        {
            (await m.GetLiveFilesAsync()).Should().Equal([30u], "the torn delete record is dropped");
        }

        new FileInfo(_path).Length.Should().Be(bytes.Length - ValueLogManifest.RecordSize);
    }

    /// <summary>
    /// End to end through the writer: the same rot must never let the writer's orphan sweep delete
    /// the live segment. Opening fails closed, the segment keeps every acknowledged frame, and once
    /// the record is repaired every pointer still reads its value.
    /// </summary>
    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(0, 0x7F)]
    [InlineData(1, 0x40)]
    public async Task WriterOpen_AfterRotInKindOrIndexOfFinalAdd_KeepsLiveSegment(int byteInRecord, byte mask)
    {
        var (segment, pointers) = await WriteAcknowledgedValuesAsync(3).ConfigureAwait(true);
        var segmentBytes = await File.ReadAllBytesAsync(segment).ConfigureAwait(true);
        FlipFinalRecordByte(byteInRecord, mask);

        var open = async () =>
        {
            await using var m = new ValueLogManifest(_path);
            await using var w = await ValueLogWriter.OpenAsync(_dir, m, ValueLogOptions.Default).ConfigureAwait(true);
        };

        await open.Should().ThrowAsync<CorruptValueLogException>().ConfigureAwait(true);
        (await File.ReadAllBytesAsync(segment).ConfigureAwait(true)).Should().Equal(
            segmentBytes, "the live segment holding acknowledged values must not be swept or truncated");

        FlipFinalRecordByte(byteInRecord, mask);
        await using (var m = new ValueLogManifest(_path))
        await using (var w = await ValueLogWriter.OpenAsync(_dir, m, ValueLogOptions.Default).ConfigureAwait(true))
        {
            using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
            for (var i = 0; i < pointers.Count; i++)
            {
                reader.Read(pointers[i]).Should().Equal(Payload(i));
            }
        }
    }

    /// <summary>
    /// A final add record cut short at end of file looks like a torn append, but the segment it
    /// added holds frames — and appends start only after the add is durable — so the add was
    /// acknowledged and lost to damage. Independent evidence (an unlisted segment holding frames,
    /// never named by a delete) makes recovery fail closed instead of letting the sweep delete it.
    /// </summary>
    [Fact]
    public async Task Replay_PartialFinalAddWhileUnlistedSegmentHoldsFrames_FailsClosed()
    {
        var segment = await WriteThreeAcknowledgedValuesAsync().ConfigureAwait(true);
        var segmentBytes = await File.ReadAllBytesAsync(segment).ConfigureAwait(true);
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fs.Length - 4);
        }

        var open = async () =>
        {
            await using var m = new ValueLogManifest(_path);
            await using var w = await ValueLogWriter.OpenAsync(_dir, m, ValueLogOptions.Default).ConfigureAwait(true);
        };

        await open.Should().ThrowAsync<CorruptValueLogException>().ConfigureAwait(true);
        (await File.ReadAllBytesAsync(segment).ConfigureAwait(true)).Should().Equal(segmentBytes);
    }

    /// <summary>
    /// The must-recover pair: a torn final add for a segment that never received a frame (the crash
    /// hit between segment creation and the add's fsync) is dropped, and the sweep removes the
    /// frameless orphan.
    /// </summary>
    [Fact]
    public async Task Replay_PartialFinalAddForFramelessSegment_IsTruncatedAsTornTail()
    {
        await WriteThreeAcknowledgedValuesAsync().ConfigureAwait(true);
        await using (var m = new ValueLogManifest(_path))
        await using (var w = await ValueLogWriter.OpenAsync(_dir, m, ValueLogOptions.Default).ConfigureAwait(true))
        {
        }

        // A second segment created and headed, its add record torn after 5 of 9 bytes.
        File.WriteAllBytes(ValueLogWriter.ResolveSegmentPath(_dir, 1), [(byte)'B', (byte)'S', (byte)'V', (byte)'L', 1, 0, 0, 0]);
        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write))
        {
            fs.Write([ValueLogManifest.KindAdd, 1, 0, 0, 0]);
        }

        await using (var m = new ValueLogManifest(_path))
        {
            (await m.GetLiveFilesAsync()).Should().BeEquivalentTo([0u]);
        }

        new FileInfo(_path).Length.Should().Be(ValueLogManifest.HeaderSize + ValueLogManifest.RecordSize);
    }

    private async Task<string> WriteThreeAcknowledgedValuesAsync()
        => (await WriteAcknowledgedValuesAsync(3).ConfigureAwait(true)).Segment;

    private async Task<(string Segment, List<ValueLogPointer> Pointers)> WriteAcknowledgedValuesAsync(int count)
    {
        var pointers = new List<ValueLogPointer>();
        await using (var m = new ValueLogManifest(_path))
        await using (var w = await ValueLogWriter.OpenAsync(_dir, m, ValueLogOptions.Default).ConfigureAwait(true))
        {
            for (var i = 0; i < count; i++)
            {
                pointers.Add(await w.AppendAsync(Payload(i), (ulong)(i + 1)).ConfigureAwait(true));
            }
        }

        return (ValueLogWriter.ResolveSegmentPath(_dir, 0), pointers);
    }

    private static byte[] Payload(int i) => Enumerable.Repeat((byte)(0x41 + i), 2048).ToArray();

    private void FlipFinalRecordByte(int byteInRecord, byte mask)
    {
        using var rw = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite);
        var position = rw.Length - ValueLogManifest.RecordSize + byteInRecord;
        rw.Seek(position, SeekOrigin.Begin);
        var original = rw.ReadByte();
        rw.Seek(position, SeekOrigin.Begin);
        rw.WriteByte((byte)(original ^ mask));
    }

    [Fact]
    public async Task Replay_DamagedFinalAddForSegmentHoldingFrames_FailsClosed()
    {
        await using (var first = new ValueLogManifest(_path))
        {
            await first.RecordAddAsync(0);
            await first.RecordAddAsync(1);
        }

        // Segment 1 holds a frame beyond its header, so its add record was acknowledged (appends
        // start only after the add is durable). Rot in that final record must not be repaired as
        // a torn tail: dropping the add would let the writer's orphan sweep delete the segment.
        var segment = ValueLogWriter.ResolveSegmentPath(_dir, 1);
        File.WriteAllBytes(segment, new byte[ValueLogFrame.HeaderSize + 64]);
        using (var rw = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            rw.Seek(ValueLogManifest.HeaderSize + ValueLogManifest.RecordSize + 5, SeekOrigin.Begin);
            rw.WriteByte(0x5A); // corrupt the final record's CRC; kind and fileIndex stay intact
        }
        var lengthBefore = new FileInfo(_path).Length;

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<CorruptValueLogException>();
        new FileInfo(_path).Length.Should().Be(lengthBefore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Construct_ManifestTornAtCreation_RewritesHeaderAndOpensEmpty(int presentBytes)
    {
        // A crash between creating the file and fsyncing its header leaves a 0-byte or short
        // file. No record can exist yet (segments are recorded only after the header is durable).
        var header = new byte[ValueLogManifest.HeaderSize];
        ValueLogManifest.Magic.CopyTo(header, 0);
        BitConverter.GetBytes(ValueLogManifest.CurrentFormatVersion).CopyTo(header, 4);
        File.WriteAllBytes(_path, header[..presentBytes]);

        ValueLogManifest? reopened = null;
        Action act = () => reopened = new ValueLogManifest(_path);

        act.Should().NotThrow("a manifest torn at creation holds nothing to lose");
        await using (reopened)
        {
            (await reopened!.GetLiveFilesAsync()).Should().BeEmpty();
        }
        File.ReadAllBytes(_path).Should().Equal(header, "the header is rewritten in full");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public async Task Construct_ManifestWithZeroFilledHeader_RewritesHeaderAndOpensEmpty(int length)
    {
        // The file length reached the disk but the header bytes did not: a zero-filled torn creation.
        File.WriteAllBytes(_path, new byte[length]);

        ValueLogManifest? reopened = null;
        Action act = () => reopened = new ValueLogManifest(_path);

        act.Should().NotThrow("a zero-filled header is a torn creation, not a foreign file");
        await using (reopened)
        {
            (await reopened!.GetLiveFilesAsync()).Should().BeEmpty();
        }
        File.ReadAllBytes(_path).Should().HaveCount(ValueLogManifest.HeaderSize);
    }

    [Fact]
    public void Construct_ShortForeignFile_StillThrows()
    {
        File.WriteAllBytes(_path, [(byte)'X', (byte)'Y', (byte)'Z']);

        Action act = () =>
        {
            var m = new ValueLogManifest(_path);
        };

        act.Should().Throw<UnrecognisedValueLogFormatException>("bytes that are not a header prefix are not a torn creation");
    }

    [Fact]
    public async Task RecordAddAsync_ShortWriteThenFault_RepairsTailSoLaterAddsSurviveReopen()
    {
        await using (var manifest = new ValueLogManifest(_path))
        {
            await manifest.RecordAddAsync(1);
            var faulting = InstallFaultingStream(manifest);
            faulting.ShortWriteBytes = 4;

            Func<Task> failed = () => manifest.RecordAddAsync(2);
            await failed.Should().ThrowAsync<IOException>();

            await manifest.RecordAddAsync(3);
        }

        ValueLogManifest? reopened = null;
        Action act = () => reopened = new ValueLogManifest(_path);

        act.Should().NotThrow("the failed append's partial bytes must not be sealed mid-file");
        await using (reopened)
        {
            (await reopened!.GetLiveFilesAsync()).Should().BeEquivalentTo([1u, 3u]);
        }
    }

    [Fact]
    public async Task RecordAddAsync_WriteLandsButFsyncFails_RecordDoesNotSurviveReopen()
    {
        await using (var manifest = new ValueLogManifest(_path))
        {
            await manifest.RecordAddAsync(1);
            var faulting = InstallFaultingStream(manifest);
            faulting.FailNextFlush = true;

            Func<Task> failed = () => manifest.RecordAddAsync(2);
            await failed.Should().ThrowAsync<IOException>();
            (await manifest.GetLiveFilesAsync()).Should().BeEquivalentTo([1u]);
        }

        await using var reopened = new ValueLogManifest(_path);
        (await reopened.GetLiveFilesAsync()).Should().BeEquivalentTo([1u],
            "a record whose append failed was never acknowledged; replay must agree with the in-memory live set");
    }

    [Fact]
    public async Task RecordAddAsync_FaultWhoseRepairFails_PoisonsLaterAppends()
    {
        await using var manifest = new ValueLogManifest(_path);
        await manifest.RecordAddAsync(1);
        var faulting = InstallFaultingStream(manifest);
        faulting.ShortWriteBytes = 4;
        faulting.FailSetLength = true;

        Func<Task> failed = () => manifest.RecordAddAsync(2);
        await failed.Should().ThrowAsync<IOException>();

        Func<Task> later = () => manifest.RecordAddAsync(3);
        await later.Should().ThrowAsync<CorruptValueLogException>(
            "torn bytes the repair could not remove must never be sealed mid-file by a later append");
    }

    private FaultingFileStream InstallFaultingStream(ValueLogManifest manifest)
    {
        var field = typeof(ValueLogManifest).GetField("_stream",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((FileStream)field.GetValue(manifest)!).Dispose();
        var faulting = new FaultingFileStream(_path);
        field.SetValue(manifest, faulting);
        return faulting;
    }

    // Injects the POSIX failure shapes a real disk cannot be made to produce on demand: a short
    // write followed by an error, and a write that lands but whose fsync reports failure. Each
    // fault fires once.
    private sealed class FaultingFileStream(string path) : FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)
    {
        public int? ShortWriteBytes { get; set; }
        public bool FailNextFlush { get; set; }
        public bool FailSetLength { get; set; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (ShortWriteBytes is { } n)
            {
                ShortWriteBytes = null;
                base.Write(buffer[..n]);
                base.Flush(flushToDisk: true);
                throw new IOException("injected short write");
            }
            base.Write(buffer);
        }

        public override void Flush(bool flushToDisk)
        {
            base.Flush(flushToDisk);
            if (FailNextFlush)
            {
                FailNextFlush = false;
                throw new IOException("injected fsync EIO");
            }
        }

        public override void SetLength(long value)
        {
            if (FailSetLength)
            {
                throw new IOException("injected truncate failure");
            }
            base.SetLength(value);
        }
    }
}
