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

namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Twelve-field record mirroring the Boutquin.MarketData bar shape — decimal-heavy so the
/// read-amplification story the columnar-projection benchmark measures is reproduced. Annotated with
/// <c>[ColumnarStorable]</c> so the source generator emits the <c>ColumnarFieldPlan</c>
/// required by <c>CompressedColumnarSegmentFormat&lt;MarketDataShapedBar&gt;</c>;
/// hand-rolled <c>ISerializable&lt;MarketDataShapedBar&gt;</c> is retained for the
/// row-oriented engine path.
/// </summary>
[ColumnarStorable]
public sealed partial record MarketDataShapedBar : IHasDate, ISerializable<MarketDataShapedBar>
{
    public DateOnly Date { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public decimal Vwap { get; set; }
    public int TradeCount { get; set; }
    public decimal Dividend { get; set; }
    public decimal SplitRatio { get; set; }
    public decimal AdjustedClose { get; set; }
    public int ExchangeCode { get; set; }

    public MarketDataShapedBar() { }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, Date.DayNumber);
        stream.Write(buf);
        WriteDecimal(stream, Open);
        WriteDecimal(stream, High);
        WriteDecimal(stream, Low);
        WriteDecimal(stream, Close);
        Span<byte> b8 = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b8, Volume);
        stream.Write(b8);
        WriteDecimal(stream, Vwap);
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b4, TradeCount);
        stream.Write(b4);
        WriteDecimal(stream, Dividend);
        WriteDecimal(stream, SplitRatio);
        WriteDecimal(stream, AdjustedClose);
        BinaryPrimitives.WriteInt32LittleEndian(b4, ExchangeCode);
        stream.Write(b4);
    }

    public static MarketDataShapedBar Deserialize(Stream stream)
    {
        Span<byte> b4 = stackalloc byte[4];
        ReadExact(stream, b4);
        var date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(b4));
        var open = ReadDecimal(stream);
        var high = ReadDecimal(stream);
        var low = ReadDecimal(stream);
        var close = ReadDecimal(stream);
        Span<byte> b8 = stackalloc byte[8];
        ReadExact(stream, b8);
        var volume = BinaryPrimitives.ReadInt64LittleEndian(b8);
        var vwap = ReadDecimal(stream);
        ReadExact(stream, b4);
        var tradeCount = BinaryPrimitives.ReadInt32LittleEndian(b4);
        var dividend = ReadDecimal(stream);
        var splitRatio = ReadDecimal(stream);
        var adjClose = ReadDecimal(stream);
        ReadExact(stream, b4);
        var exchangeCode = BinaryPrimitives.ReadInt32LittleEndian(b4);

        return new MarketDataShapedBar
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
            ExchangeCode = exchangeCode,
        };
    }

    private static void WriteDecimal(Stream s, decimal v)
    {
        Span<byte> buf = stackalloc byte[16];
        var parts = decimal.GetBits(v);
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(i * 4, 4), parts[i]);
        }
        s.Write(buf);
    }

    private static decimal ReadDecimal(Stream s)
    {
        Span<byte> buf = stackalloc byte[16];
        ReadExact(s, buf);
        Span<int> parts = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            parts[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(i * 4, 4));
        }
        return new decimal(parts.ToArray());
    }

    private static void ReadExact(Stream s, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = s.Read(buffer[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"MarketDataShapedBar.Deserialize: expected {buffer.Length} bytes, got {total}.");
            }
            total += read;
        }
    }
}
