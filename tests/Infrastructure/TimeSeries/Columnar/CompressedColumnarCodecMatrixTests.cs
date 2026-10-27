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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

// Phase 11 — explicit codec × column-type round-trip matrix for the
// CompressedColumnarSegmentFormat. Each test pins a specific (codec, CLR-type)
// pair MarketData will use, so a regression in any one arm of the column
// encoder/decoder dispatch surfaces here. Also covers footer corruption fault
// injection and mid-segment EOF.

[ColumnarStorable]
public partial record CodecBarInt32 : IHasDate
{
    public DateOnly Date { get; init; }
    public int Value { get; init; }
    public CodecBarInt32() { }
}

[ColumnarStorable]
public partial record CodecBarInt64 : IHasDate
{
    public DateOnly Date { get; init; }
    public long Value { get; init; }
    public CodecBarInt64() { }
}

[ColumnarStorable]
public partial record CodecBarDouble : IHasDate
{
    public DateOnly Date { get; init; }
    public double Value { get; init; }
    public CodecBarDouble() { }
}

[ColumnarStorable]
public partial record CodecBarDecimal : IHasDate
{
    public DateOnly Date { get; init; }

    [DecimalScale(4)]
    public decimal Value { get; init; }
    public CodecBarDecimal() { }
}

[ColumnarStorable]
public partial record CodecBarString : IHasDate
{
    public DateOnly Date { get; init; }
    public string Value { get; init; } = "";
    public CodecBarString() { }
}

[ColumnarStorable]
public partial record CodecBarBool : IHasDate
{
    public DateOnly Date { get; init; }
    public bool Value { get; init; }
    public CodecBarBool() { }
}

public sealed class CompressedColumnarCodecMatrixTests
{
    // ── Int32 round-trip ─────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Int32_PreservesEveryValue()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 20)
            .Select(i => new CodecBarInt32 { Date = new DateOnly(2026, 1, 1).AddDays(i), Value = i * 1000 - 5000 })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = new List<CodecBarInt32>();
        await foreach (var r in format.ReadAsync(stream, input[0].Date, input[^1].Date, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }
        recovered.Should().HaveCount(input.Count);
        recovered.Select(r => r.Value).Should().Equal(input.Select(r => r.Value));
    }

    // ── Int64 round-trip ─────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Int64_PreservesEveryValue()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt64>();
        var input = Enumerable.Range(0, 20)
            .Select(i => new CodecBarInt64 { Date = new DateOnly(2026, 1, 1).AddDays(i), Value = (long)i * 100_000_000L })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = new List<CodecBarInt64>();
        await foreach (var r in format.ReadAsync(stream, input[0].Date, input[^1].Date, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }
        recovered.Select(r => r.Value).Should().Equal(input.Select(r => r.Value));
    }

    // ── Double round-trip ────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Double_PreservesEveryValue()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarDouble>();
        var input = Enumerable.Range(0, 20)
            .Select(i => new CodecBarDouble { Date = new DateOnly(2026, 1, 1).AddDays(i), Value = i * 0.123456789 })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = new List<CodecBarDouble>();
        await foreach (var r in format.ReadAsync(stream, input[0].Date, input[^1].Date, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }
        recovered.Select(r => r.Value).Should().Equal(input.Select(r => r.Value));
    }

    // ── Decimal round-trip (with explicit DecimalScale) ──────────────────────

    [Fact]
    public async Task RoundTrip_Decimal_PreservesPrices()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarDecimal>();
        var input = Enumerable.Range(0, 20)
            .Select(i => new CodecBarDecimal
            {
                Date = new DateOnly(2026, 1, 1).AddDays(i),
                Value = 100.0m + (i * 0.0125m),
            })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = new List<CodecBarDecimal>();
        await foreach (var r in format.ReadAsync(stream, input[0].Date, input[^1].Date, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }
        recovered.Select(r => r.Value).Should().Equal(input.Select(r => r.Value));
    }

    // ── String round-trip (typically dictionary-encoded) ─────────────────────

    [Fact]
    public async Task RoundTrip_String_PreservesEveryValue()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarString>();
        var input = Enumerable.Range(0, 20)
            .Select(i => new CodecBarString
            {
                Date = new DateOnly(2026, 1, 1).AddDays(i),
                Value = i % 2 == 0 ? "AAPL" : "MSFT", // low-cardinality → dictionary path
            })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = new List<CodecBarString>();
        await foreach (var r in format.ReadAsync(stream, input[0].Date, input[^1].Date, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }
        recovered.Select(r => r.Value).Should().Equal(input.Select(r => r.Value));
    }

    // ── Bool round-trip (RLE bitmap) ─────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Bool_PreservesAlternatingPattern()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarBool>();
        var input = Enumerable.Range(0, 20)
            .Select(i => new CodecBarBool { Date = new DateOnly(2026, 1, 1).AddDays(i), Value = (i % 3) == 0 })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = new List<CodecBarBool>();
        await foreach (var r in format.ReadAsync(stream, input[0].Date, input[^1].Date, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }
        recovered.Select(r => r.Value).Should().Equal(input.Select(r => r.Value));
    }

    // ── Multiple row groups across each codec ────────────────────────────────

    [Fact]
    public async Task RoundTrip_MultipleRowGroups_DecimalAndString()
    {
        // Row-group boundary forces the decoder to decode each row group separately and
        // splice them. This catches off-by-one errors in the FirstRowIndex bookkeeping.
        var format = new CompressedColumnarSegmentFormat<CodecBarDecimal>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12)
            .Select(i => new CodecBarDecimal
            {
                Date = new DateOnly(2026, 1, 1).AddDays(i),
                Value = (100m + i * 0.5m),
            })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = new List<CodecBarDecimal>();
        await foreach (var r in format.ReadAsync(stream, input[0].Date, input[^1].Date, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }
        recovered.Should().HaveCount(12);
        recovered.Select(r => r.Value).Should().Equal(input.Select(r => r.Value));
    }

    // ── Date range filter excludes earlier / later dates ─────────────────────

    [Fact]
    public async Task ReadAsync_DateRangeFilter_ExcludesOutOfRangeRecords()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarDecimal>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12)
            .Select(i => new CodecBarDecimal
            {
                Date = new DateOnly(2026, 1, 1).AddDays(i),
                Value = 100m + i,
            })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        // Request [Jan 5, Jan 8] only → 4 records.
        var from = new DateOnly(2026, 1, 5);
        var to = new DateOnly(2026, 1, 8);
        var recovered = new List<CodecBarDecimal>();
        await foreach (var r in format.ReadAsync(stream, from, to, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }
        recovered.Should().HaveCount(4);
        recovered.Select(r => r.Date).Should().AllSatisfy(d =>
        {
            d.Should().BeOnOrAfter(from);
            d.Should().BeOnOrBefore(to);
        });
    }

    // ── Footer / structural corruption fault injection ───────────────────────

    [Fact]
    public async Task ReadAsync_FooterMagicCorrupted_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = new[]
        {
            new CodecBarInt32 { Date = new DateOnly(2026, 1, 1), Value = 1 },
            new CodecBarInt32 { Date = new DateOnly(2026, 1, 2), Value = 2 },
        };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Flip a byte in the last 64 bytes of the segment — the footer lives at the tail
        // and any tampering there breaks structural validation at read time.
        var bytes = stream.ToArray();
        var nearTail = Math.Max(0, bytes.Length - 32);
        bytes[nearTail] ^= 0xFF;

        using var corruptStream = new MemoryStream(bytes);
        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadAsync(corruptStream,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                ColumnProjection.All).ConfigureAwait(true))
            {
                break;
            }
        };
        await act.Should().ThrowAsync<Exception>("footer with corrupted magic must surface");
    }

    [Fact]
    public async Task ReadAsync_TrailerCorrupted_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = new[]
        {
            new CodecBarInt32 { Date = new DateOnly(2026, 1, 1), Value = 1 },
        };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        var bytes = stream.ToArray();
        // Overwrite trailer to a wildly wrong length.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(bytes.Length - 4, 4), 999_999u);

        using var corrupt = new MemoryStream(bytes);
        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadAsync(corrupt,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                ColumnProjection.All).ConfigureAwait(true))
            {
                break;
            }
        };
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ReadAsync_TruncatedSegment_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 5)
            .Select(i => new CodecBarInt32 { Date = new DateOnly(2026, 1, 1).AddDays(i), Value = i })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        var bytes = stream.ToArray();
        // Truncate to half — destroys the footer.
        var truncated = bytes.Take(bytes.Length / 2).ToArray();

        using var corrupt = new MemoryStream(truncated);
        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadAsync(corrupt,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                ColumnProjection.All).ConfigureAwait(true))
            {
                break;
            }
        };
        await act.Should().ThrowAsync<Exception>("truncated segment cannot be read");
    }

    [Fact]
    public async Task ReadAsync_ColumnDataCorrupted_SurfacesColumnDecodeException()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 8)
            .Select(i => new CodecBarInt32 { Date = new DateOnly(2026, 1, 1).AddDays(i), Value = i * 1000 })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var bytes = stream.ToArray();
        // Flip a byte in the middle of the column data region (well past the file header).
        bytes[bytes.Length / 2] ^= 0xFF;

        using var corrupt = new MemoryStream(bytes);
        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadAsync(corrupt,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
                ColumnProjection.All).ConfigureAwait(true))
            {
                // Drain to surface any decoder error.
            }
        };
        await act.Should().ThrowAsync<Exception>("mid-column corruption must surface as a structured exception");
    }

    [Fact]
    public async Task ReadAsync_MidSegmentEof_StopsCleanlyOrThrows()
    {
        // Truncate at a random mid-file offset — exercises the EOF detection in the
        // column decoder dispatch.
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 16)
            .Select(i => new CodecBarInt32 { Date = new DateOnly(2026, 1, 1).AddDays(i), Value = i })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        var bytes = stream.ToArray();
        var truncated = bytes.Take((int)(bytes.Length * 0.7)).ToArray();
        using var corrupt = new MemoryStream(truncated);

        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadAsync(corrupt,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                ColumnProjection.All).ConfigureAwait(true))
            {
                // intentionally empty
            }
        };
        // Either the truncation manifests as a decode/io exception, or — if the truncation
        // happens to land between row groups — the read enumerates fewer records cleanly.
        // Both are acceptable; what's not acceptable is a silent successful read of garbage.
        await act.Should().ThrowAsync<Exception>();
    }
}
