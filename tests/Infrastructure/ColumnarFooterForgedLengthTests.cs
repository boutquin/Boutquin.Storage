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
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// A CRC-valid columnar footer whose extension-section length or bitmap length is forged must be
/// rejected with <see cref="CorruptSegmentException"/> — never a raw
/// <see cref="OverflowException"/> (length at or above 2^31 narrowed with <c>checked</c>) or
/// <see cref="ArgumentOutOfRangeException"/> (length near <see cref="int.MaxValue"/> wrapping the
/// <c>cursor + count</c> bound negative and reaching <see cref="ReadOnlySpan{T}.Slice(int, int)"/>).
/// </summary>
public sealed class ColumnarFooterForgedLengthTests
{
    [Theory]
    [InlineData(0x8000_0000u)]
    [InlineData(0xFFFF_FFFFu)]
    [InlineData(0x7FFF_FFF0u)]
    [InlineData(0x7FFF_FFFFu)]
    [InlineData(1000u)]
    public void ForgedExtensionSectionLength_ThrowsCorruptSegmentException(uint sectionLength)
    {
        var segment = SegmentWithUnknownExtension(sectionLength);

        var act = () => FooterReader.Read(new MemoryStream(segment));

        act.Should().Throw<CorruptSegmentException>();
    }

    [Theory]
    [InlineData(0x8000_0000u)]
    [InlineData(0xFFFF_FFFFu)]
    [InlineData(0x7FFF_FFF0u)]
    [InlineData(0x7FFF_FFFFu)]
    [InlineData(100u)]
    public void ForgedBitmapLength_ThrowsCorruptSegmentException(uint bitmapLength)
    {
        var segment = SegmentWithBitmapLength(bitmapLength);

        var act = () => FooterReader.Read(new MemoryStream(segment));

        act.Should().Throw<CorruptSegmentException>();
    }

    [Theory]
    [InlineData(0x8000_0000u)]
    [InlineData(0x7FFF_FFFFu)]
    public void ForgedExtensionSectionLength_PublicReadZoneMaps_ThrowsCorruptSegmentException(uint sectionLength)
    {
        var segment = SegmentWithUnknownExtension(sectionLength);
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();

        var act = () => format.ReadZoneMaps(new MemoryStream(segment));

        act.Should().Throw<CorruptSegmentException>();
    }

    [Fact]
    public void ExactlyFittingExtensionSection_IsAccepted()
    {
        // Refusal must not over-fire: a section whose length equals the remaining payload parses.
        var segment = SegmentWithUnknownExtension(3, sectionBody: [1, 2, 3]);

        var footer = FooterReader.Read(new MemoryStream(segment));

        footer.Columns.Should().BeEmpty();
    }

    internal static byte[] SegmentWithUnknownExtension(uint sectionLength, byte[]? sectionBody = null)
    {
        using var ms = new MemoryStream();
        WritePayloadPrefix(ms);
        ms.WriteByte(1);    // extension count
        ms.WriteByte(0xEE); // unknown kind — skipped via its length prefix
        WriteUInt32(ms, sectionLength);
        if (sectionBody is not null)
        {
            ms.Write(sectionBody);
        }

        return Segment(ms.ToArray());
    }

    internal static byte[] SegmentWithBitmapLength(uint bitmapLength)
    {
        using var section = new MemoryStream();
        WriteUInt16(section, 1); // entries
        WriteUInt16(section, 1); // name length
        section.WriteByte((byte)'a');
        WriteUInt32(section, 1); // value count
        WriteUInt16(section, 0); // value length
        WriteUInt32(section, bitmapLength);
        var sectionBytes = section.ToArray();

        using var ms = new MemoryStream();
        WritePayloadPrefix(ms);
        ms.WriteByte(1);
        ms.WriteByte((byte)FooterExtensionKind.BitmapIndexes);
        WriteUInt32(ms, (uint)sectionBytes.Length);
        ms.Write(sectionBytes);
        return Segment(ms.ToArray());
    }

    private static void WritePayloadPrefix(MemoryStream ms)
    {
        WriteUInt32(ms, SegmentMagic.AsUInt32);
        WriteUInt32(ms, SegmentMagic.CurrentFormatVersion);
        WriteUInt16(ms, 0); // columns
        WriteUInt16(ms, 0); // row groups
    }

    // Header + footer payload + CRC32C + trailer (payload length, magic): a CRC-valid segment, so
    // the forged length reaches the payload parser rather than the CRC gate.
    private static byte[] Segment(byte[] payload)
    {
        using var ms = new MemoryStream();
        WriteUInt32(ms, SegmentMagic.AsUInt32);
        WriteUInt32(ms, SegmentMagic.CurrentFormatVersion);
        ms.Write(payload);
        WriteUInt32(ms, Crc32C.HashToUInt32(payload));
        WriteUInt32(ms, (uint)payload.Length);
        WriteUInt32(ms, SegmentMagic.AsUInt32);
        return ms.ToArray();
    }

    private static void WriteUInt32(MemoryStream ms, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        ms.Write(b);
    }

    private static void WriteUInt16(MemoryStream ms, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        ms.Write(b);
    }
}

/// <summary>
/// A coverage manifest whose <c>datasets</c> array holds a <c>null</c> element is corruption and
/// must fail closed like a null key: reads return <see langword="null"/> (so the store falls back to
/// an engine scan) and writes throw <see cref="CorruptCoverageManifestException"/> — never a raw
/// <see cref="NullReferenceException"/>.
/// </summary>
public sealed class FileDatasetCoverageIndexNullEntryTests : IDisposable
{
    private readonly string _manifestPath;

    public FileDatasetCoverageIndexNullEntryTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "boutquin-storage-tests-null-entry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _manifestPath = Path.Combine(dir, "coverage.json");
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_manifestPath)!;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NullDatasetElement_GetAsyncReturnsNull_AndRegisterAsyncThrowsCorruptManifest()
    {
        const string Manifest = """{ "schemaVersion": 1, "datasets": [ null ] }""";
        await File.WriteAllTextAsync(_manifestPath, Manifest);
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        var key = new SerializableWrapper<string>("AAPL");

        var read = await idx.Awaiting(x => x.GetAsync(key)).Should().NotThrowAsync();

        read.Subject.Should().BeNull();
        await idx.Awaiting(x => x.RegisterAsync(key, new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 2)))
            .Should().ThrowAsync<CorruptCoverageManifestException>()
            .WithMessage("*null dataset entry*");
        (await File.ReadAllTextAsync(_manifestPath)).Should().Be(Manifest);
    }
}

/// <summary>
/// Bitmap-index construction must cost memory proportional to the segment's row count, not to
/// distinct-values × rows, and must emit byte-identical bitmaps to RLE-encoding the full per-value
/// boolean column.
/// </summary>
public sealed class BitmapIndexComputerAllocationTests
{
    private static readonly ColumnDescriptor s_int64Column =
        new("Tag", 0, 8, ColumnEncodingTag.DeltaVlq, 0, ColumnFieldTypeCode.Int64);

    [Fact]
    public void HighCardinalityColumn_AllocatesInRowsNotDistinctTimesRows()
    {
        const int Rows = 20_000;
        const int Distinct = 5_000;
        var values = new long[Rows];
        for (var i = 0; i < Rows; i++)
        {
            values[i] = i % Distinct;
        }

        BitmapIndexComputer.Compute(s_int64Column, values); // JIT warm-up
        var before = GC.GetAllocatedBytesForCurrentThread();
        BitmapIndexComputer.Compute(s_int64Column, values);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // distinct × rows bool[] buffers would be 100 MB; a row-proportional build stays far below
        // 1 KB per row.
        allocated.Should().BeLessThan(Rows * 1_000L);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void BitmapsMatchRleEncodingOfThePerValueBooleanColumn(int seed)
    {
        var rng = new Random(seed);
        var rows = rng.Next(1, 3_000);
        var distinct = rng.Next(1, 40);
        var values = new long[rows];
        for (var i = 0; i < rows; i++)
        {
            // Mix runs and scatter so leading, trailing and interior gaps all occur.
            values[i] = rng.Next(4) == 0 && i > 0 ? values[i - 1] : rng.Next(distinct);
        }

        var entry = BitmapIndexComputer.Compute(s_int64Column, values);

        var seen = new HashSet<long>(values);
        entry.ValueToBitmap.Should().HaveCount(seen.Count);
        foreach (var v in entry.ValueToBitmap)
        {
            var key = BinaryPrimitives.ReadInt64LittleEndian(v.Value);
            var expected = new bool[rows];
            for (var i = 0; i < rows; i++)
            {
                expected[i] = values[i] == key;
            }

            v.Bitmap.Should().Equal(BitmapRleCodec.Encode(expected));
        }
    }
}
