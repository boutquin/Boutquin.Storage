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

/// <summary>Trade venue with a deliberately negative member to pin signed widening.</summary>
public enum TradeVenue
{
    Unknown = -1,
    Nyse = 0,
    Nasdaq = 1,
    Otc = 2,
}

/// <summary>Quality flag on a 64-bit underlying to pin the widening across underlyings.</summary>
public enum QualityFlag : long
{
    Raw = 0,
    Adjusted = long.MaxValue,
}

/// <summary>
/// Fixture with enum columns — exercises the Enum field-type code end to end
/// (DeltaVlq default encoding carrying the underlying value widened to int64).
/// </summary>
[ColumnarStorable]
public partial record EnumBar : IHasDate, ISerializable<EnumBar>
{
    public DateOnly Date { get; init; }
    public TradeVenue Venue { get; init; }
    public QualityFlag Quality { get; init; }

    public EnumBar() { }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], Date.DayNumber);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..8], (int)Venue);
        BinaryPrimitives.WriteInt64LittleEndian(buf[8..], (long)Quality);
        stream.Write(buf);
    }

    public static EnumBar Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[16];
        stream.ReadExactly(buf);
        return new EnumBar
        {
            Date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4])),
            Venue = (TradeVenue)BinaryPrimitives.ReadInt32LittleEndian(buf[4..8]),
            Quality = (QualityFlag)BinaryPrimitives.ReadInt64LittleEndian(buf[8..]),
        };
    }
}
