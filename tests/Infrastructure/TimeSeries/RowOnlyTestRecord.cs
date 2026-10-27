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
using Boutquin.Storage.Domain.Interfaces.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

/// <summary>
/// A row-only dated record fixture that implements ONLY <see cref="ISerializable{T}"/> and
/// <see cref="IHasDate"/> — deliberately NOT <c>IColumnarRehydratable&lt;T&gt;</c>. This models a
/// <c>[StorageSerializable]</c>-only record (no <c>[ColumnarStorable]</c> annotation, hence no
/// generator-emitted columnar rehydration contract), e.g. MarketData's <c>OptionQuote</c>,
/// <c>FuturesSettlement</c>, <c>DividendPayment</c>.
/// </summary>
/// <remarks>
/// The mere fact that this type compiles as the <c>TRecord</c> argument of
/// <c>LsmTimeSeriesStore</c> / <c>LsmTimeSeriesStorageEngine</c> with row format is the
/// compile-time proof that the columnar rehydration constraint has been relocated off the
/// format-agnostic surfaces (AC-1.1). Before the relocation this file would not compile.
/// </remarks>
public sealed class RowOnlyTestRecord
    : IHasDate, ISerializable<RowOnlyTestRecord>, IEquatable<RowOnlyTestRecord>
{
    public DateOnly Date { get; set; }
    public int Value { get; set; }

    public RowOnlyTestRecord() { }
    public RowOnlyTestRecord(DateOnly date, int value) { Date = date; Value = value; }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], Date.DayNumber);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..], Value);
        stream.Write(buf);
    }

    public static RowOnlyTestRecord Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        var n = stream.Read(buf);
        if (n != 8)
        {
            throw new EndOfStreamException($"RowOnlyTestRecord.Deserialize: expected 8 bytes, got {n}.");
        }

        return new RowOnlyTestRecord(
            DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4])),
            BinaryPrimitives.ReadInt32LittleEndian(buf[4..]));
    }

    public bool Equals(RowOnlyTestRecord? other)
        => other is not null && Date == other.Date && Value == other.Value;

    public override bool Equals(object? obj) => obj is RowOnlyTestRecord r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Date, Value);
    public override string ToString() => $"({Date:yyyy-MM-dd}, {Value})";
}
