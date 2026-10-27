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
/// Benchmark-local dated record for the row-vs-columnar storage comparison. Fixed 20-byte
/// layout (int day + double price + long volume). Annotated with <c>[ColumnarStorable]</c>
/// so the source generator emits the <c>ColumnarFieldPlan</c> required by
/// <c>CompressedColumnarSegmentFormat&lt;BenchmarkBar&gt;</c>; hand-rolled
/// <c>ISerializable&lt;BenchmarkBar&gt;</c> is retained for the row-oriented engine path.
/// </summary>
[ColumnarStorable]
public sealed partial record BenchmarkBar : IHasDate, ISerializable<BenchmarkBar>
{
    public DateOnly Date { get; set; }
    public double Price { get; set; }
    public long Volume { get; set; }

    public BenchmarkBar() { }

    public BenchmarkBar(DateOnly date, double price, long volume)
    {
        Date = date;
        Price = price;
        Volume = volume;
    }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], Date.DayNumber);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.Slice(4, 8), Price);
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(12, 8), Volume);
        stream.Write(buf);
    }

    public static BenchmarkBar Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[20];
        var n = stream.Read(buf);
        if (n != 20)
        {
            throw new EndOfStreamException($"BenchmarkBar.Deserialize: expected 20 bytes, got {n}.");
        }

        return new BenchmarkBar(
            DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4])),
            BinaryPrimitives.ReadDoubleLittleEndian(buf.Slice(4, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(12, 8)));
    }
}
