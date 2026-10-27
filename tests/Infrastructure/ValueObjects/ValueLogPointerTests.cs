// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.ValueObjects;

public sealed class ValueLogPointerTests
{
    [Fact]
    public void SerializedSize_IsTwelveBytes()
    {
        ValueLogPointer.SerializedSize.Should().Be(12);
    }

    [Fact]
    public void WriteTo_EmitsLittleEndianFileIndexAndPackedOffsetAndSequenceCheck()
    {
        // Sequence number 0x112232 maps to check value 0x112233 (sequence mod 2^24-1, plus one).
        var pointer = new ValueLogPointer(fileIndex: 0x01020304u, offset: 0x55_6677_8899ul, sequenceNumber: 0x112232ul);
        var buffer = new byte[12];

        pointer.WriteTo(buffer);

        buffer.Should().Equal(
            0x04, 0x03, 0x02, 0x01,                         // fileIndex LE
            0x99, 0x88, 0x77, 0x66, 0x55,                   // offset, low 40 bits LE
            0x33, 0x22, 0x11                                // sequence check, high 24 bits LE
        );
    }

    [Fact]
    public void WriteTo_ThrowsWhenOffsetExceedsFortyBits()
    {
        var pointer = new ValueLogPointer(1u, ValueLogPointer.MaxOffset + 1, sequenceNumber: 5ul);

        Action act = () => pointer.WriteTo(new byte[ValueLogPointer.SerializedSize]);

        act.Should().Throw<InvalidOperationException>("an offset the field cannot hold must never be silently masked");
    }

    [Fact]
    public void ReadFrom_IsInverseOfWriteTo()
    {
        var original = new ValueLogPointer(FileIndex: 42, Offset: 1_073_741_824ul);
        var buffer = original.ToArray();

        var roundTripped = ValueLogPointer.ReadFrom(buffer);

        roundTripped.Should().Be(original);
    }

    [Fact]
    public void ReadFrom_PreservesSequenceCheck()
    {
        var original = new ValueLogPointer(fileIndex: 42, offset: 1_073_741_824ul, sequenceNumber: 123_456_789ul);

        var roundTripped = ValueLogPointer.ReadFrom(original.ToArray());

        roundTripped.SequenceCheck.Should().Be(ValueLogPointer.ComputeSequenceCheck(123_456_789ul),
            "the identity a reader checks must survive the WAL and SSTable encodings");
        roundTripped.Should().Be(original);
    }

    [Fact]
    public void WriteTo_ThrowsWhenSpanTooShort()
    {
        var pointer = new ValueLogPointer(1, 2);
        var buffer = new byte[11];

        Action act = () => pointer.WriteTo(buffer);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReadFrom_ThrowsWhenSpanTooShort()
    {
        var buffer = new byte[11];

        // Action wraps the ReadOnlySpan ctor-less call indirectly via array.
        var act = () => ValueLogPointer.ReadFrom(buffer);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToArray_ReturnsTwelveByteArray()
    {
        var pointer = new ValueLogPointer(7, 42);

        var bytes = pointer.ToArray();

        bytes.Length.Should().Be(12);
    }

    [Fact]
    public void Equals_ByValue()
    {
        var a = new ValueLogPointer(5, 100);
        var b = new ValueLogPointer(5, 100);
        var c = new ValueLogPointer(5, 101);

        a.Equals(b).Should().BeTrue();
        a.Equals(c).Should().BeFalse();
    }
}
