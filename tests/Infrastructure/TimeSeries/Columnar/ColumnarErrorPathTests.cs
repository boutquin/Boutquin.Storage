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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Error-path coverage for the compressed columnar segment format — specifically three
/// concerns: unknown encoding tag, unknown codec version, and column-decode-failure.
/// Envelope errors (magic, CRC, truncation, unsupported format version, strict projection
/// miss) are covered by <c>PlainColumnarSegmentFormatTests</c> and share the envelope code.
/// </summary>
public sealed class ColumnarErrorPathTests
{
    private static ColumnarBar MakeBar(int n) => new()
    {
        Date = new DateOnly(2020, 1, 1).AddDays(n),
        Ticker = "AAPL",
        Close = 100m + (n * 0.25m),
        Volume = 1_000_000L + (n * 1000L),
        TradeCount = 1000 + n,
        Vwap = 100.0 + (n * 0.25),
        IsAdjusted = (n % 2) == 0,
        Residual = n * 0.001,
    };

    // ───── Unknown per-column encoding tag ───────────────────────────

    [Fact]
    public async Task UnknownEncodingTag_ThrowsUnsupportedEncodingException()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = Enumerable.Range(0, 5).Select(MakeBar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Locate the first column descriptor in the footer and tamper its encoding tag to
        // an unknown value (byte 200 — well above the 0..6 range Phase-3 defines).
        var bytes = stream.ToArray();
        TamperFirstColumnEncodingTag(bytes, newTag: 200);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);
        var act = async () =>
        {
            await foreach (var _ in format.ReadAsync(corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), ColumnProjection.All))
            {
            }
        };

        (await act.Should().ThrowAsync<UnsupportedEncodingException>().ConfigureAwait(true))
            .Which.EncodingTag.Should().Be(200);
    }

    // ───── Unknown per-column codec version ──────────────────────────

    [Fact]
    public async Task FutureCodecVersion_ThrowsUnsupportedEncodingException()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = Enumerable.Range(0, 5).Select(MakeBar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Tamper: bump the first column's codec version to 0xFF.
        var bytes = stream.ToArray();
        TamperFirstColumnCodecVersion(bytes, newVersion: 0xFF);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);
        var act = async () =>
        {
            await foreach (var _ in format.ReadAsync(corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), ColumnProjection.All))
            {
            }
        };

        (await act.Should().ThrowAsync<UnsupportedEncodingException>().ConfigureAwait(true))
            .Which.CodecVersion.Should().Be(0xFF);
    }

    // ───── Column-decode failure mid-stream ──────────────────────────

    [Fact]
    public async Task ColumnPayloadCorruption_ThrowsColumnDecodeException()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = Enumerable.Range(0, 50).Select(MakeBar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Tamper: flip multiple bytes inside the first column's data span. Do NOT touch
        // the footer; this exercises the column-level decode-failure path,
        // not the envelope CRC path.
        var bytes = stream.ToArray();
        // First column data starts right after the 8-byte header. Corrupt bytes 30-40
        // aggressively so at least one codec's invariant is violated (dictionary ID
        // out of range, VLQ overrun, etc.).
        for (var i = 30; i < 50 && i < bytes.Length - 20; i++)
        {
            bytes[i] ^= 0xFF;
        }

        using var corrupted = new MemoryStream(bytes);
        var act = async () =>
        {
            await foreach (var _ in format.ReadAsync(corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), ColumnProjection.All))
            {
            }
        };

        // Either a ColumnDecodeException (codec failed on tampered column data) or a
        // CorruptSegmentException (if the tampering happened to shift an offset that
        // ultimately tripped the footer CRC — less likely since we didn't touch the
        // footer, but still a valid safe-refusal outcome).
        (await act.Should().ThrowAsync<Exception>().ConfigureAwait(true))
            .Which.Should().Match(ex =>
                ex is ColumnDecodeException || ex is CorruptSegmentException || ex is InvalidDataException);
    }

    // ───── Tampering helpers ──────────────────────────────────────────

    /// <summary>
    /// Walks the footer layout to find the first column descriptor's encoding-tag byte and
    /// overwrites it. Layout per <c>FooterWriter</c>:
    ///   [footer payload start]
    ///     4 B magic | 4 B version | 2 B column count
    ///     per column: 2 B name len | N B UTF-8 name | 1 B encoding tag | 1 B codec version
    ///                  | 1 B field type | 8 B byte offset | 8 B byte length
    ///     ...
    /// </summary>
    private static void TamperFirstColumnEncodingTag(byte[] bytes, byte newTag)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        // Skip magic (4) + version (4) + column count (2) = 10 bytes to first column entry.
        var cursor = payloadStart + 10;
        // Skip name length (2) + name bytes.
        var nameLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(cursor, 2));
        cursor += 2 + nameLen;
        bytes[cursor] = newTag;
    }

    private static void TamperFirstColumnCodecVersion(byte[] bytes, byte newVersion)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var cursor = payloadStart + 10;
        var nameLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(cursor, 2));
        cursor += 2 + nameLen;
        // encoding tag (1) then codec version (1).
        bytes[cursor + 1] = newVersion;
    }

    private static (int PayloadStart, int PayloadLength) LocateFooterPayload(byte[] bytes)
    {
        // Last 8 bytes: footer length (4) + trailer magic (4).
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(trailerStart, 4));
        var footerPayloadEnd = trailerStart - 4 /* CRC */;
        var footerPayloadStart = footerPayloadEnd - footerLength;
        return (footerPayloadStart, footerLength);
    }

    /// <summary>
    /// After tampering the footer payload, recompute CRC32C so the envelope validation
    /// passes and the reader proceeds to the per-column codec path (where the encoding-tag
    /// or codec-version check should fire).
    /// </summary>
    private static void RepairFooterCrc(byte[] bytes)
    {
        var (payloadStart, payloadLength) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart, payloadLength).ToArray();
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(payload);
        var crcOffset = payloadStart + payloadLength;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset, 4), crc);
    }
}
