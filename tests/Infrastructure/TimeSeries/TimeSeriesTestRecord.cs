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
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

/// <summary>
/// Minimal dated record used by time-series store tests. Implements the constraints
/// required by <see cref="ITimeSeriesStore{TDatasetKey,TRecord}"/> without depending on
/// the storage source generator.
/// </summary>
public sealed class TimeSeriesTestRecord
    : IHasDate, ISerializable<TimeSeriesTestRecord>, IColumnarRehydratable<TimeSeriesTestRecord>, IEquatable<TimeSeriesTestRecord>
{
    public DateOnly Date { get; set; }
    public int Value { get; set; }

    public TimeSeriesTestRecord() { }
    public TimeSeriesTestRecord(DateOnly date, int value) { Date = date; Value = value; }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], Date.DayNumber);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..], Value);
        stream.Write(buf);
    }

    public static TimeSeriesTestRecord Rehydrate(
        IReadOnlyDictionary<string, Array> columns, int rowIndex)
    {
        var result = new TimeSeriesTestRecord();
        if (columns.TryGetValue(nameof(Date), out var dates))
        {
            result.Date = ((DateOnly[])dates)[rowIndex];
        }

        if (columns.TryGetValue(nameof(Value), out var values))
        {
            result.Value = ((int[])values)[rowIndex];
        }

        return result;
    }

    public static TimeSeriesTestRecord Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[8];
        var n = stream.Read(buf);
        if (n != 8)
        {
            throw new EndOfStreamException($"TimeSeriesTestRecord.Deserialize: expected 8 bytes, got {n}.");
        }

        return new TimeSeriesTestRecord(
            DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4])),
            BinaryPrimitives.ReadInt32LittleEndian(buf[4..]));
    }

    public bool Equals(TimeSeriesTestRecord? other)
        => other is not null && Date == other.Date && Value == other.Value;

    public override bool Equals(object? obj) => obj is TimeSeriesTestRecord r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Date, Value);
    public override string ToString() => $"({Date:yyyy-MM-dd}, {Value})";
}
