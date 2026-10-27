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
/// F-78 — footer bloom count guard. <c>ReadBody</c> read <c>bloom_bits_count</c> and
/// <c>bloom_hash_count</c> with a raw <c>(int)</c> cast of a varint and stamped them straight
/// into the <see cref="SsTableFooter"/> record, with none of the range/<c>EnsureWithinRemaining</c>
/// guarding its sibling length-prefixed fields receive. A forged footer (the CRC is body-only and
/// forgeable) can therefore declare a negative <c>bits_count</c> (a hostile varint wraps under the
/// <c>(int)</c> cast) or an absurd <c>hash_count</c>, smuggling a structurally-impossible bloom
/// descriptor past open. The fix range-checks both counts at read time, failing closed with the
/// footer's typed corruption exception before the record is built.
/// </summary>
public sealed class SsTableFooterForgedBloomCountTests
{
    private static SsTableFooter ValidPopulatedFooter()
    {
        var bloom = new byte[] { 0xAB, 0xCD };
        return new SsTableFooter(
            FormatVersion: 1,
            CodecId: 0x01,
            EntryCount: 1,
            MinKey: new byte[] { 1 },
            MaxKey: new byte[] { 2 },
            MaxSequenceNumber: 7UL,
            BlockIndex: [new SsTableBlockIndexEntry(new byte[] { 1 }, 0)],
            BloomBitsCount: 16,
            BloomHashCount: 3,
            BloomBits: bloom,
            Tombstones: []);
    }

    [Fact]
    public void Deserialize_ForgedNegativeBloomBitsCount_FailsClosed()
    {
        // Forge bits_count to -1. Serialize writes (ulong)(-1) = 0xFFFF...FFFF as the varint;
        // ReadBody's (int) cast wraps it back to -1. bytes_length stays 0 so the existing
        // EnsureWithinRemaining(bloom-bits length) guard is satisfied — the bits_count itself
        // is the only corruption, which the new count guard must reject.
        var forged = ValidPopulatedFooter() with
        {
            BloomBitsCount = -1,
            BloomHashCount = 1,
            BloomBits = Array.Empty<byte>(),
        };
        var bytes = forged.Serialize();

        var act = () => SsTableFooter.Deserialize(bytes);

        act.Should().Throw<InvalidDataException>(
            "a footer declaring a negative bloom bits_count is forged/corrupt and must fail closed " +
            "at read, not be stamped into the SsTableFooter record unchecked");
    }

    [Fact]
    public void Deserialize_ForgedNegativeBloomHashCount_FailsClosed()
    {
        var forged = ValidPopulatedFooter() with
        {
            BloomBitsCount = 0,
            BloomHashCount = -5,
            BloomBits = Array.Empty<byte>(),
        };
        var bytes = forged.Serialize();

        var act = () => SsTableFooter.Deserialize(bytes);

        act.Should().Throw<InvalidDataException>(
            "a footer declaring a negative bloom hash_count is forged/corrupt and must fail closed at read");
    }

    [Fact]
    public void Deserialize_ValidBloomCounts_StillRoundTrips()
    {
        // The guard must not reject a genuine footer: a positive bits_count and hash_count
        // with a matching bits buffer round-trips unchanged.
        var footer = ValidPopulatedFooter();
        var parsed = SsTableFooter.Deserialize(footer.Serialize());

        parsed.BloomBitsCount.Should().Be(16);
        parsed.BloomHashCount.Should().Be(3);
        parsed.BloomBits.ToArray().Should().Equal(0xAB, 0xCD);
    }

    // ── B6: bloom bits/bytes mutual consistency ───────────────────────────────

    [Fact]
    public void Deserialize_BloomBytesLengthInconsistentWithBitsCount_ThrowsInvalidData()
    {
        // Build a valid footer, then splice a forged bloom_bytes_length that does not match
        // ceil(bloom_bits_count/8).  bloom_bits_count=16 → expected 2 bytes; we declare 3.
        // Before the fix the inconsistency would survive Deserialize and surface later as a
        // raw arithmetic exception inside the bloom filter constructor.  After the fix,
        // ReadBody throws InvalidDataException with a descriptive message.
        var footer = ValidPopulatedFooter(); // bits_count=16, bytes=0xAB,0xCD (2 bytes)
        var serialised = footer.Serialize().ToArray();

        // Locate the bloom_bytes_length varint in the serialised body and replace it with 3
        // (one byte too many for 16 bits).  The simplest approach: build a substitute footer
        // with bloom_bits_count=16 but bloom_bytes_length=3 by patching the raw bytes.
        // We serialise a footer with 3 bloom bytes (24 bits declared in bits_count to keep
        // the serialiser consistent), then replace the bits_count varint with 16 (2 bytes)
        // — giving bits_count=16 but bytes_length=3, an inconsistent pair.

        // Easier: serialise the valid footer, then construct a tampered byte array whose
        // bloom section declares 3 bytes but bits_count=16.
        using var ms = new MemoryStream();
        // Rebuild the serialised form with a forged 3-byte bloom buffer.
        var forged = footer with
        {
            BloomBitsCount = 16,   // says 16 bits → expects 2 bytes
            BloomBits = new byte[] { 0xAB, 0xCD, 0xEF } // but provides 3
        };
        // Serialise the forged footer (BloomBitsCount=16 but 3-byte bloom slice).
        // SsTableFooter.Serialize writes bits_count then hash_count then bytes_length then bytes.
        // Since bits_count=16 → ceil/8=2, we need the serialiser to write bytes_length=3.
        // The simplest way: patch the serialised bytes directly.
        // Deserialize is the target; we need to feed it a byte stream whose bloom section is
        // inconsistent.  We use the public Serialize() and then patch the bloom_bytes_length.
        var genuine = footer.Serialize().ToArray();

        // Strategy: serialise a footer with 3 bloom bytes (bits_count=24) to get the correct
        // framing, then change bits_count to 16 at the byte level.  This is fragile; instead,
        // use the forged-footer round-trip approach: serialise a 3-byte-bloom footer normally
        // and then patch only the bits_count varint (which is a single-byte varint for counts
        // < 128).

        var threeByteBloomFooter = footer with
        {
            BloomBitsCount = 24,
            BloomBits = new byte[] { 0xAB, 0xCD, 0xEF }
        };
        var bytes = threeByteBloomFooter.Serialize().ToArray();

        // bloom_bits_count is written as a varint right after the block index section.
        // Find the position of 0x18 (24 as varint) and replace it with 0x10 (16 as varint).
        // Both are single-byte varints so the frame length is unchanged.
        var idx = Array.IndexOf(bytes, (byte)0x18);
        if (idx >= 0)
        {
            bytes[idx] = 0x10; // replace 24 → 16
        }

        var act = () => SsTableFooter.Deserialize(bytes);
        act.Should().Throw<InvalidDataException>("bits_count=16 requires 2 bytes but bloom_bytes_length=3");
    }
}
