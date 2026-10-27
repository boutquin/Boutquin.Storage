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
/// Fixture that satisfies <see cref="IHasDate"/> through an explicit interface implementation —
/// its date-carrying public property is <see cref="TradeDate"/>, and there is no public
/// property literally named <c>Date</c>. Exercises the projected-range path's dependency on
/// the <c>"Date"</c> column name.
/// </summary>
[ColumnarStorable]
public partial record ExplicitDateBar : IHasDate, ISerializable<ExplicitDateBar>
{
    public DateOnly TradeDate { get; init; }
    public double Value { get; init; }

    DateOnly IHasDate.Date => TradeDate;

    public ExplicitDateBar() { }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], TradeDate.DayNumber);
        BinaryPrimitives.WriteInt64LittleEndian(buf[4..], BitConverter.DoubleToInt64Bits(Value));
        stream.Write(buf);
    }

    public static ExplicitDateBar Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[12];
        stream.ReadExactly(buf);
        return new ExplicitDateBar
        {
            TradeDate = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4])),
            Value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buf[4..])),
        };
    }
}
