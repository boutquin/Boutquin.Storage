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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

// MarketData-shaped fixture for the projection integration test. Twelve fields — decimal-heavy —
// mirrors the Boutquin.MarketData `Bar` shape so the measured read-amplification story of the
// parent spec (~12× reduction for a close-only projection) is exercised on the same field
// count the narrative is built on.
//
// `record class` for continuity with the 20+ existing tests that reference it. Migration to
// `record struct` is deferred to the Boutquin.MarketData integration milestone.
//
// The ColumnarSourceGenerator emits IProjectable<MarketDataBar>.ProjectTo and
// IColumnarRehydratable<MarketDataBar>.Rehydrate for this record. Serialize/Deserialize are
// hand-rolled because StorageSourceGenerator targets record struct exclusively.
[ColumnarStorable]
public partial record MarketDataBar : IHasDate, ISerializable<MarketDataBar>, IEquatable<MarketDataBar>
{
    public DateOnly Date { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long Volume { get; init; }
    public decimal Vwap { get; init; }
    public int TradeCount { get; init; }
    public decimal Dividend { get; init; }
    public decimal SplitRatio { get; init; }
    public decimal AdjustedClose { get; init; }
    // SourceFlags is `int` rather than `short` because the Phase-2 PlainColumnarSegmentFormat
    // only ships fixed-width codecs for DateOnly, Int32, Int64, decimal, double, float, and bool.
    // A real Boutquin.MarketData bar would type this narrower; the integration-test fixture
    // widens to Int32 to exercise the full 12-field shape without waiting on a Phase-3 Int16
    // codec. The parent spec's read-amplification narrative is still intact — the field count,
    // decimal density, and total row width are what matter for the measurement.
    public int SourceFlags { get; init; }

    public MarketDataBar() { }

    public void Serialize(Stream stream)
    {
        // 4 (Date) + 8×16 (8 decimals) + 8 (Volume) + 4 (TradeCount) + 2 (SourceFlags) = 142.
        // Writing field-at-a-time keeps the layout easy to reason about; a blat would be faster
        // but this fixture is for correctness, not throughput.
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, Date.DayNumber);
        stream.Write(buf);
        WriteDecimal(stream, Open);
        WriteDecimal(stream, High);
        WriteDecimal(stream, Low);
        WriteDecimal(stream, Close);
        Span<byte> volBuf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(volBuf, Volume);
        stream.Write(volBuf);
        WriteDecimal(stream, Vwap);
        Span<byte> tcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tcBuf, TradeCount);
        stream.Write(tcBuf);
        WriteDecimal(stream, Dividend);
        WriteDecimal(stream, SplitRatio);
        WriteDecimal(stream, AdjustedClose);
        Span<byte> flagsBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(flagsBuf, SourceFlags);
        stream.Write(flagsBuf);
    }

    public static MarketDataBar Deserialize(Stream stream)
    {
        Span<byte> dateBuf = stackalloc byte[4];
        ReadExact(stream, dateBuf);
        var date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(dateBuf));
        var open = ReadDecimal(stream);
        var high = ReadDecimal(stream);
        var low = ReadDecimal(stream);
        var close = ReadDecimal(stream);
        Span<byte> volBuf = stackalloc byte[8];
        ReadExact(stream, volBuf);
        var volume = BinaryPrimitives.ReadInt64LittleEndian(volBuf);
        var vwap = ReadDecimal(stream);
        Span<byte> tcBuf = stackalloc byte[4];
        ReadExact(stream, tcBuf);
        var tradeCount = BinaryPrimitives.ReadInt32LittleEndian(tcBuf);
        var dividend = ReadDecimal(stream);
        var splitRatio = ReadDecimal(stream);
        var adjClose = ReadDecimal(stream);
        Span<byte> flagsBuf = stackalloc byte[4];
        ReadExact(stream, flagsBuf);
        var sourceFlags = BinaryPrimitives.ReadInt32LittleEndian(flagsBuf);

        return new MarketDataBar
        {
            Date = date,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,
            Vwap = vwap,
            TradeCount = tradeCount,
            Dividend = dividend,
            SplitRatio = splitRatio,
            AdjustedClose = adjClose,
            SourceFlags = sourceFlags,
        };
    }

    private static void WriteDecimal(Stream stream, decimal value)
    {
        Span<byte> buf = stackalloc byte[16];
        var parts = decimal.GetBits(value);
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(i * 4, 4), parts[i]);
        }
        stream.Write(buf);
    }

    private static decimal ReadDecimal(Stream stream)
    {
        Span<byte> buf = stackalloc byte[16];
        ReadExact(stream, buf);
        Span<int> parts = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            parts[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(i * 4, 4));
        }
        return new decimal(parts.ToArray());
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"MarketDataBar.Deserialize: expected {buffer.Length} bytes, got {total}.");
            }
            total += read;
        }
    }
}
