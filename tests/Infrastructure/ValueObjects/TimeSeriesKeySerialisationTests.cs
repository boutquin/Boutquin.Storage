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

using Boutquin.Storage.Domain.ValueObjects.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.ValueObjects;

public sealed class TimeSeriesKeySerialisationTests
{
    [Fact]
    public void Deserialize_ForgedOutOfRangeDayNumber_ThrowsTypedCorruption()
    {
        // F-V1-01 (registry WA-4-01) — a forged/bit-rotted 4-byte day number outside the DateOnly
        // range must surface a TYPED corruption exception (InvalidDataException), not a raw
        // ArgumentOutOfRangeException leaked from DateOnly.FromDayNumber.
        using var ms = new MemoryStream();
        new SerializableWrapper<string>("tiingo:1d:AAPL").Serialize(ms);
        Span<byte> dayBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(dayBuf, int.MaxValue);
        ms.Write(dayBuf);
        ms.Position = 0;

        var act = () => TimeSeriesKey<SerializableWrapper<string>>.Deserialize(ms);

        act.Should().Throw<InvalidDataException>(
            "a forged out-of-range day number must surface a typed corruption exception");
    }

    [Fact]
    public void SerializeDeserialize_Roundtrips_StringDatasetAndDate()
    {
        var original = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("tiingo:1d:AAPL"),
            new DateOnly(2020, 6, 14));

        using var ms = new MemoryStream();
        original.Serialize(ms);
        ms.Position = 0;
        var roundtripped = TimeSeriesKey<SerializableWrapper<string>>.Deserialize(ms);

        roundtripped.Dataset.Value.Should().Be("tiingo:1d:AAPL");
        roundtripped.Date.Should().Be(new DateOnly(2020, 6, 14));
    }

    [Fact]
    public void SerializeDeserialize_Roundtrips_IntDataset()
    {
        var original = new TimeSeriesKey<SerializableWrapper<int>>(
            new SerializableWrapper<int>(42),
            new DateOnly(2026, 4, 18));

        using var ms = new MemoryStream();
        original.Serialize(ms);
        ms.Position = 0;
        var roundtripped = TimeSeriesKey<SerializableWrapper<int>>.Deserialize(ms);

        roundtripped.Dataset.Value.Should().Be(42);
        roundtripped.Date.Should().Be(new DateOnly(2026, 4, 18));
    }

    [Fact]
    public void Deserialize_StreamReturningOneBytePerRead_Roundtrips()
    {
        // Stream.Read may legally return fewer bytes than requested before end of stream. The
        // 4-byte day number must be read fully, not rejected on the first short read.
        var original = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("tiingo:1d:AAPL"),
            new DateOnly(2024, 1, 2));
        using var ms = new MemoryStream();
        original.Serialize(ms);

        using var trickle = new OneBytePerReadStream(ms.ToArray());
        var act = () => TimeSeriesKey<SerializableWrapper<string>>.Deserialize(trickle);

        var roundtripped = act.Should().NotThrow("a short read before end of stream is not truncation").Subject;
        roundtripped.Should().Be(original, "a conforming short-reading stream carries the same key bytes");
    }

    [Fact]
    public void Deserialize_TruncatedDayNumber_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream();
        new SerializableWrapper<string>("tiingo:1d:AAPL").Serialize(ms);
        ms.Write([0x01, 0x02]);

        using var trickle = new OneBytePerReadStream(ms.ToArray());
        var act = () => TimeSeriesKey<SerializableWrapper<string>>.Deserialize(trickle);

        act.Should().Throw<EndOfStreamException>("two of the four day-number bytes is a truncated key");
    }

    // A non-seekable stream that returns at most one byte per Read call — legal under the
    // Stream.Read contract and the shape a network or pipe stream can take.
    private sealed class OneBytePerReadStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= data.Length)
            {
                return 0;
            }

            buffer[0] = data[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
