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
using System.Buffers.Binary;
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// End-to-end corruption contract of a persisted <see cref="SortedStringTable{TKey, TValue}"/>:
/// bytes that pass the block CRC32C or footer CRC32C (checksums, not MACs — a forger recomputes
/// them) but carry structurally impossible values must surface the component's typed
/// corruption exception through the public open / point-lookup API, never a BCL argument
/// exception and never a silently accepted layout.
/// </summary>
public sealed class SortedStringTableCorruptionContractTests : IDisposable
{
    private const string FileName = "corruption-contract.dat";
    private const int EntryCount = 8;
    private const int RestartInterval = 2;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sst-corruption-{Guid.NewGuid():N}");

    public SortedStringTableCorruptionContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Restart offsets decoded from a CRC-valid block ──────────────────────────

    [Theory]
    [InlineData("int-max")]
    [InlineData("negative")]
    [InlineData("entries-end")]
    public void TryGetValue_ForgedRestartOffsetOutOfRange_ThrowsCorruptBlockException(string forgery)
    {
        var path = WriteSingleBlockTable();
        ForgeRestartOffsets(path, (offsets, entriesEnd) =>
        {
            // The binary search probes the middle restart first, so forge that one.
            offsets[offsets.Length / 2] = forgery switch
            {
                "int-max" => int.MaxValue,
                "negative" => -5,
                _ => entriesEnd,
            };
        });

        var lookup = () => OpenAndLookup(3);

        lookup.Should().Throw<CorruptBlockException>(
            "a restart offset outside [0, entries_end) is corrupt block data, not a caller argument");
    }

    [Fact]
    public void TryGetValue_ForgedRestartOffsetsNotIncreasing_ThrowsCorruptBlockException()
    {
        var path = WriteSingleBlockTable();
        ForgeRestartOffsets(path, (offsets, _) => offsets[offsets.Length / 2] = offsets[(offsets.Length / 2) - 1]);

        var lookup = () => OpenAndLookup(3);

        lookup.Should().Throw<CorruptBlockException>(
            "restart offsets are written strictly increasing; a repeated or descending offset is corrupt");
    }

    // ── Footer format_version ──────────────────────────────────────────────────

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    [InlineData((byte)255)]
    public void Open_FooterFormatVersionOtherThanCurrent_ThrowsInvalidDataException(byte forgedVersion)
    {
        var path = WriteSingleBlockTable();
        var original = File.ReadAllBytes(path);
        SsTableFooter.TryReadFromEnd(original, out var footer).Should().BeTrue();
        var dataEnd = DataEnd(original);

        var forgedFooter = (footer! with { FormatVersion = forgedVersion }).Serialize();
        var tampered = new byte[dataEnd + forgedFooter.Length];
        Array.Copy(original, 0, tampered, 0, dataEnd);
        forgedFooter.CopyTo(tampered, dataEnd);
        File.WriteAllBytes(path, tampered);

        var reopen = () => new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(_dir, FileName);

        reopen.Should().Throw<InvalidDataException>(
            "this build reads only format_version {0}; any other version must not be parsed with the version-{0} layout",
            SsTableFooter.CurrentFormatVersion);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private string WriteSingleBlockTable()
    {
        var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, FileName, codec: new NoCompressionCodec(), restartInterval: RestartInterval);
        writer.Write(Enumerable.Range(0, EntryCount)
            .Select(i => (new SerializableWrapper<int>(i), new SerializableWrapper<string>($"v{i}"), 0UL))
            .ToList());
        return Path.Combine(_dir, FileName);
    }

    private bool OpenAndLookup(int key)
    {
        var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(_dir, FileName);
        return table.TryGetValue(new SerializableWrapper<int>(key), out _);
    }

    private static int DataEnd(byte[] file)
    {
        var trailer = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(file.Length - 4, 4));
        return file.Length - 4 - (int)trailer;
    }

    /// <summary>
    /// Rewrites the restart-offset tail of the table's single uncompressed block and recomputes
    /// the block CRC32C, so the forgery passes every integrity check and reaches the decoder.
    /// </summary>
    private static void ForgeRestartOffsets(string path, Action<int[], int> forge)
    {
        var file = File.ReadAllBytes(path);
        SsTableFooter.TryReadFromEnd(file, out var footer).Should().BeTrue();
        footer!.BlockIndex.Should().HaveCount(1, "the forgery helper expects a single-block table");

        var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0, 4));
        BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4, 4)).Should().Be(payloadLen,
            "NoCompressionCodec stores the payload verbatim");
        var payload = file.AsSpan(8, payloadLen);

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload[^4..]);
        count.Should().Be(EntryCount / RestartInterval);
        var tailStart = payloadLen - 4 - (count * 4);
        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(tailStart + (i * 4), 4));
        }

        forge(offsets, tailStart);

        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(tailStart + (i * 4), 4), offsets[i]);
        }

        var crc = Crc32C.HashToUInt32(file.AsSpan(0, 8 + payloadLen));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8 + payloadLen, 4), crc);
        File.WriteAllBytes(path, file);
    }
}
