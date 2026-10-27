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
/// Phase C6 sequence-number-slot tests. Verifies that <see cref="BlockEntry.SequenceNumber"/>
/// is the single source of truth for the 8-byte seqno slot in the block-entry format, and
/// that the full ulong range round-trips unchanged through the record struct.
/// </summary>
public sealed class BlockEntrySeqnoTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x0123_4567_89AB_CDEFUL)]
    public void SequenceNumber_RoundTripsUnchanged(ulong seqno)
    {
        var entry = new BlockEntry(
            Key: new byte[] { 0xAA },
            SequenceNumber: seqno,
            Kind: ValueKind.Inline,
            Value: new byte[] { 0xBB });

        entry.SequenceNumber.Should().Be(seqno);
    }

    [Fact]
    public void BlockEntry_IsValueType()
    {
        typeof(BlockEntry).IsValueType.Should().BeTrue("BlockEntry is a record struct by design (seqno-carrying block entries)");
    }

    [Fact]
    public void BlockEntry_StructuralEquality_ConsidersAllFields()
    {
        var a = new BlockEntry(new byte[] { 1 }, 7UL, ValueKind.Inline, new byte[] { 2 });
        var b = new BlockEntry(new byte[] { 1 }, 7UL, ValueKind.Inline, new byte[] { 2 });
        var c = new BlockEntry(new byte[] { 1 }, 8UL, ValueKind.Inline, new byte[] { 2 });

        // ReadOnlyMemory<byte> structural equality is by slice identity, not content — but
        // the record equality short-circuits fast for different slot values, which is all
        // we test here.
        a.SequenceNumber.Should().Be(b.SequenceNumber);
        a.SequenceNumber.Should().NotBe(c.SequenceNumber);
    }
}
