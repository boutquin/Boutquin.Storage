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

/// <summary>
/// A ulong-underlying enum with a member above <see cref="long.MaxValue"/> — the
/// two's-complement int64 widening maps <see cref="High"/> to a NEGATIVE widened value,
/// the case where signed widened order diverges from the enum's unsigned natural order.
/// </summary>
public enum SessionFlags : ulong
{
    None = 0,
    Low = 5,
    High = 0x8000_0000_0000_0001UL, // widens to a negative long
}

/// <summary>
/// Fixture pinning the ulong-underlying enum column path — widened-int64 transport
/// round-trip and the conservative-skip contract for range predicates across the
/// sign boundary.
/// </summary>
[ColumnarStorable]
public partial record UlongEnumBar : IHasDate, ISerializable<UlongEnumBar>
{
    public DateOnly Date { get; init; }
    public SessionFlags Flags { get; init; }

    public UlongEnumBar() { }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], Date.DayNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[4..], (ulong)Flags);
        stream.Write(buf);
    }

    public static UlongEnumBar Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[12];
        stream.ReadExactly(buf);
        return new UlongEnumBar
        {
            Date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4])),
            Flags = (SessionFlags)BinaryPrimitives.ReadUInt64LittleEndian(buf[4..]),
        };
    }
}
