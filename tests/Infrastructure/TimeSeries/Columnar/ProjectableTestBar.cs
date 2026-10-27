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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Multi-field test record that hand-implements <see cref="IProjectable{TSelf}"/> so the
/// projection unit tests can exercise <c>ProjectTo</c> without depending on generator-emitted
/// code. Stands in for the source-generator-emitted implementation until Phase B of the spec's
/// follow-up work lands.
/// </summary>
public sealed class ProjectableTestBar
    : IHasDate,
      ISerializable<ProjectableTestBar>,
      IProjectable<ProjectableTestBar>,
      IColumnarRehydratable<ProjectableTestBar>,
      IEquatable<ProjectableTestBar>
{
    public DateOnly Date { get; set; }
    public decimal Open { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }

    public ProjectableTestBar() { }

    public ProjectableTestBar(DateOnly date, decimal open, decimal close, long volume)
    {
        Date = date;
        Open = open;
        Close = close;
        Volume = volume;
    }

    public static ProjectableTestBar Rehydrate(
        IReadOnlyDictionary<string, Array> columns, int rowIndex)
    {
        var result = new ProjectableTestBar();
        if (columns.TryGetValue(nameof(Date), out var dates))
        {
            result.Date = ((DateOnly[])dates)[rowIndex];
        }

        if (columns.TryGetValue(nameof(Open), out var opens))
        {
            result.Open = ((decimal[])opens)[rowIndex];
        }

        if (columns.TryGetValue(nameof(Close), out var closes))
        {
            result.Close = ((decimal[])closes)[rowIndex];
        }

        if (columns.TryGetValue(nameof(Volume), out var volumes))
        {
            result.Volume = ((long[])volumes)[rowIndex];
        }

        return result;
    }

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4 + 16 + 16 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], Date.DayNumber);
        var opens = decimal.GetBits(Open);
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4 + i * 4, 4), opens[i]);
        }

        var closes = decimal.GetBits(Close);
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(20 + i * 4, 4), closes[i]);
        }

        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(36, 8), Volume);
        stream.Write(buf);
    }

    public static ProjectableTestBar Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4 + 16 + 16 + 8];
        var n = stream.Read(buf);
        if (n != buf.Length)
        {
            throw new EndOfStreamException($"ProjectableTestBar.Deserialize: expected {buf.Length} bytes, got {n}.");
        }

        var date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4]));
        var openParts = new int[4];
        for (var i = 0; i < 4; i++)
        {
            openParts[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4 + i * 4, 4));
        }

        var closeParts = new int[4];
        for (var i = 0; i < 4; i++)
        {
            closeParts[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(20 + i * 4, 4));
        }

        var volume = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(36, 8));
        return new ProjectableTestBar(date, new decimal(openParts), new decimal(closeParts), volume);
    }

    public ProjectableTestBar ProjectTo(ColumnProjection projection)
    {
        if (projection.FieldNames.Count == 0)
        {
            return this;
        }

        var result = new ProjectableTestBar { Date = Date };
        var declared = new[] { "Date", "Open", "Close", "Volume" };
        foreach (var name in projection.FieldNames)
        {
            switch (name)
            {
                case "Date":
                    result.Date = Date;
                    break;
                case "Open":
                    result.Open = Open;
                    break;
                case "Close":
                    result.Close = Close;
                    break;
                case "Volume":
                    result.Volume = Volume;
                    break;
                default:
                    if (!projection.AllowMissing)
                    {
                        throw new ColumnNotFoundException(name, segmentFormatVersion: 1, declared);
                    }

                    break;
            }
        }

        return result;
    }

    public bool Equals(ProjectableTestBar? other)
        => other is not null
           && Date == other.Date
           && Open == other.Open
           && Close == other.Close
           && Volume == other.Volume;

    public override bool Equals(object? obj) => obj is ProjectableTestBar b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(Date, Open, Close, Volume);
    public override string ToString() => $"({Date:yyyy-MM-dd}, O={Open}, C={Close}, V={Volume})";
}
