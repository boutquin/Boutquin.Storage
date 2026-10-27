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
/// Fixture with a <see cref="float"/> column — exercises the Single field-type code end to
/// end (GorillaXor default encoding carrying zero-extended raw float bits).
/// </summary>
[ColumnarStorable]
public partial record FloatBar : IHasDate, ISerializable<FloatBar>
{
    public DateOnly Date { get; init; }
    public float Residual { get; init; }

    public FloatBar() { }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], Date.DayNumber);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..], BitConverter.SingleToInt32Bits(Residual));
        stream.Write(buf);
    }

    public static FloatBar Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        stream.ReadExactly(buf);
        return new FloatBar
        {
            Date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4])),
            Residual = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buf[4..])),
        };
    }
}
