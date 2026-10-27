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

using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// F-V1-01 — a forged-but-CRC-valid columnar footer whose row-group <c>minDayNum</c>/<c>maxDayNum</c>
/// fall outside the <see cref="DateOnly"/> day-number range <c>[0, 3 652 058]</c> must surface a
/// TYPED <see cref="CorruptSegmentException"/> out of the public columnar read API — not a raw
/// <see cref="ArgumentOutOfRangeException"/> leaked from <see cref="DateOnly.FromDayNumber(int)"/>.
/// The two sibling fields (<c>rowCount</c>, <c>firstRowIndex</c>) are already guarded; this pins the
/// day-number range guard that closes the gap. Same class as the CONFIRMED-FIXED F-U2-01.
/// </summary>
public sealed class ColumnarFooterDayNumberRangeTests
{
    [Fact]
    public async Task ReadRowGroups_ForgedOutOfRangeMinDayNumber_ThrowsCorruptSegment()
    {
        // Arrange — a small FloatBar segment with a valid footer.
        var format = new CompressedColumnarSegmentFormat<FloatBar>();
        var rows = Enumerable.Range(0, 4).Select(i => new FloatBar
        {
            Date = new DateOnly(2020, 1, 1).AddDays(i),
            Residual = i * 0.5f,
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        // Tamper: forge the first row group's minDayNum to int.MaxValue (far past the
        // DateOnly ceiling 3 652 058), then repair the footer CRC so the read reaches the
        // unguarded FromDayNumber.
        TamperFirstRowGroupMinDayNum(bytes, newMinDayNum: int.MaxValue);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        // Act + Assert — must be the typed corruption exception, NOT a raw ArgumentOutOfRangeException.
        var act = async () =>
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<FloatBar>().ReadRowGroupsAsync(
                corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)))
            {
            }
        };

        await act.Should().ThrowAsync<CorruptSegmentException>(
            "a forged day-number outside the DateOnly range must surface a typed corruption exception");
    }

    [Fact]
    public async Task ReadRowGroups_ValidFooter_StillDecodesUnchanged()
    {
        // Regression guard — the fix must not reject legitimate in-range day numbers.
        var format = new CompressedColumnarSegmentFormat<FloatBar>();
        var rows = Enumerable.Range(0, 4).Select(i => new FloatBar
        {
            Date = new DateOnly(2020, 1, 1).AddDays(i),
            Residual = i * 0.5f,
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        using var clean = new MemoryStream(bytes);

        var count = 0;
        await foreach (var _ in new CompressedColumnarSegmentFormat<FloatBar>().ReadRowGroupsAsync(
            clean,
            new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)))
        {
            count++;
        }

        count.Should().BeGreaterThan(0, "a valid footer must still decode after the day-number guard is added");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers (mirrors ColumnarFooterRowCountBoundTests footer-walk layout)
    // ═══════════════════════════════════════════════════════════════════════════

    private static void TamperFirstRowGroupMinDayNum(byte[] bytes, int newMinDayNum)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart);

        var cursor = 4 + 4; // magic + version
        var columnCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        for (var i = 0; i < columnCount; i++)
        {
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2 + nameLen + 3 + 8 + 8; // name + tag/codec/type + offset + length
        }

        cursor += 2; // rowGroupCount

        // Row group 0: minDayNum is the first int32 field.
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(payloadStart + cursor, 4), newMinDayNum);
    }

    private static (int PayloadStart, int PayloadLength) LocateFooterPayload(byte[] bytes)
    {
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailerStart, 4));
        var footerPayloadEnd = trailerStart - 4; // CRC is 4 bytes before trailer
        var footerPayloadStart = footerPayloadEnd - footerLength;
        return (footerPayloadStart, footerLength);
    }

    private static void RepairFooterCrc(byte[] bytes)
    {
        var (payloadStart, payloadLength) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart, payloadLength).ToArray();
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(payload);
        var crcOffset = payloadStart + payloadLength;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset, 4), crc);
    }
}
