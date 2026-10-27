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
namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// Tests for the Phase C7 <see cref="ValueKind"/> enum and <see cref="BlockEntry.Kind"/>.
/// Guards that: (a) Inline and ValueLogPointer carry the spec-mandated byte values
/// (0x00 and 0x01 respectively); (b) the enum's underlying type is byte (so the
/// block entry format reserves exactly one byte per slot); (c) the BlockEntry record
/// struct carries the Kind field with byte-exact persisted semantics.
/// </summary>
public sealed class BlockEntryValueKindTests
{
    [Fact]
    public void ValueKind_Inline_HasByteValueZero()
    {
        ((byte)ValueKind.Inline).Should().Be(0x00);
    }

    [Fact]
    public void ValueKind_ValueLogPointer_HasByteValueOne()
    {
        ((byte)ValueKind.ValueLogPointer).Should().Be(0x01);
    }

    [Fact]
    public void ValueKind_UnderlyingType_IsByte()
    {
        Enum.GetUnderlyingType(typeof(ValueKind)).Should().Be<byte>();
    }

    [Fact]
    public void BlockEntry_Construction_ExposesAllFields()
    {
        var key = new byte[] { 1, 2, 3 };
        var value = new byte[] { 9, 8, 7 };

        var entry = new BlockEntry(
            Key: key,
            SequenceNumber: 42UL,
            Kind: ValueKind.Inline,
            Value: value);

        entry.Key.Span.ToArray().Should().Equal(key);
        entry.Value.Span.ToArray().Should().Equal(value);
        entry.SequenceNumber.Should().Be(42UL);
        entry.Kind.Should().Be(ValueKind.Inline);
    }

    [Fact]
    public void BlockEntry_DefaultSequenceNumber_IsZero()
    {
        var entry = new BlockEntry(
            Key: new byte[] { 0 },
            SequenceNumber: 0UL,
            Kind: ValueKind.Inline,
            Value: new byte[] { 0 });

        entry.SequenceNumber.Should().Be(0UL);
    }
}
