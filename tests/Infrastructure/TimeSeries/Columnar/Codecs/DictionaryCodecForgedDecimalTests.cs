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

using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar.Codecs;

// F-W1-01 — DictionaryCodec.DecodeDecimal reads four raw int32 words straight from the (untrusted)
// payload and constructs `new decimal(bits)` with no validity check on the flags word. A forged
// flags word with scale > 28 or a reserved bit leaks a raw ArgumentException out of the public
// columnar decode API instead of the typed InvalidDataException corruption signal (the convention
// the sibling OrderedByteHelpers.ReadDecimal already follows).
public sealed class DictionaryCodecForgedDecimalTests
{
    // Build a single-entry decimal dictionary payload: VLQ dictSize=1, then a 16-byte decimal
    // envelope (lo/mid/hi/flags little-endian), then one row id (VLQ 0).
    private static byte[] BuildSingleEntryPayload(int lo, int mid, int hi, int flags)
    {
        var payload = new byte[1 + 16 + 1];
        payload[0] = 0x01; // VLQ dictSize = 1
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), lo);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), mid);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), hi);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(13, 4), flags);
        payload[17] = 0x00; // VLQ row id = 0
        return payload;
    }

    [Fact]
    public void DecodeDecimal_ForgedScaleAbove28_ThrowsTypedInvalidData_NotRawArgument()
    {
        // flags word with scale = 31 (0x001F0000) — above the decimal cap of 28.
        var forged = BuildSingleEntryPayload(lo: 0, mid: 0, hi: 0, flags: 0x001F0000);

        var act = () => DictionaryCodec.DecodeDecimal(forged, count: 1);

        act.Should().Throw<InvalidDataException>(
            "a forged decimal flags word with scale > 28 must surface a typed corruption exception, "
            + "not a raw ArgumentException from new decimal(bits)");
    }

    [Fact]
    public void DecodeDecimal_ForgedReservedBits_ThrowsTypedInvalidData_NotRawArgument()
    {
        // flags word with reserved bits set (0x7FFFFFFF) — not a valid decimal layout.
        var forged = BuildSingleEntryPayload(lo: 0, mid: 0, hi: 0, flags: 0x7FFFFFFF);

        var act = () => DictionaryCodec.DecodeDecimal(forged, count: 1);

        act.Should().Throw<InvalidDataException>(
            "a forged decimal flags word with reserved bits set must surface a typed corruption "
            + "exception, not a raw ArgumentException");
    }

    [Fact]
    public void DecodeDecimal_ValidEntries_RoundTripUnchanged()
    {
        // Regression guard — a legitimate decimal dictionary must still round-trip after the guard.
        decimal[] values = [1.50m, 1.50m, -42.0001m, 0m];
        var encoded = DictionaryCodec.EncodeDecimal(values);

        var decoded = DictionaryCodec.DecodeDecimal(encoded, values.Length);

        decoded.Should().Equal(values, "valid decimal values must still decode after the bit guard");
    }
}
