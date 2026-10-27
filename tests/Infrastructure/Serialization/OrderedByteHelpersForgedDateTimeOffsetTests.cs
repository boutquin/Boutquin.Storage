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

namespace Boutquin.Storage.Infrastructure.Tests.Serialization;

// F-W1-04 — OrderedByteHelpers.ReadDateTimeOffset decodes an 8-byte BE UTC tick count + a 2-byte
// sign-flipped offset-minute short and constructs the DateTimeOffset with no range guard. A forged
// out-of-range utcTicks throws a raw ArgumentOutOfRangeException from new DateTimeOffset(long,...);
// a forged offset beyond ±14h throws AOORE from .ToOffset(FromMinutes(...)). The sibling
// ReadDecimal in the same file already range-guards its forged scale with InvalidDataException —
// ReadDateTimeOffset must surface the same TYPED corruption exception, not a raw BCL exception.
public sealed class OrderedByteHelpersForgedDateTimeOffsetTests
{
    // 10-byte wire: 8-byte BE UTC tick count + 2-byte BE sign-flipped offset-minutes short.
    private static byte[] BuildWire(ulong utcTicks, short offsetMinutes)
    {
        var buf = new byte[10];
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(0, 8), utcTicks);
        BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(8, 2), (short)(offsetMinutes ^ short.MinValue));
        return buf;
    }

    [Fact]
    public void ReadDateTimeOffset_ForgedOutOfRangeUtcTicks_ThrowsTypedInvalidData_NotRawAOORE()
    {
        // ulong.MaxValue reinterpreted as (long) is -1 — below DateTime.MinValue.Ticks (0).
        var forged = BuildWire(utcTicks: ulong.MaxValue, offsetMinutes: 0);
        using var ms = new MemoryStream(forged, writable: false);

        var act = () => OrderedByteHelpers.ReadDateTimeOffset(ms);

        act.Should().Throw<InvalidDataException>(
            "a forged out-of-range UTC tick count must surface a typed corruption exception, "
            + "not a raw ArgumentOutOfRangeException");
    }

    [Fact]
    public void ReadDateTimeOffset_ForgedOffsetBeyond14h_ThrowsTypedInvalidData_NotRawAOORE()
    {
        // Valid UTC ticks but a forged offset of 20000 minutes (≈333h) — far beyond the ±14h cap.
        var validTicks = (ulong)new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var forged = BuildWire(utcTicks: validTicks, offsetMinutes: 20000);
        using var ms = new MemoryStream(forged, writable: false);

        var act = () => OrderedByteHelpers.ReadDateTimeOffset(ms);

        act.Should().Throw<InvalidDataException>(
            "a forged offset beyond ±14h must surface a typed corruption exception, "
            + "not a raw ArgumentOutOfRangeException");
    }

    [Fact]
    public void ReadDateTimeOffset_ValidWire_RoundTripsUnchanged()
    {
        // Regression guard — a legitimately written DateTimeOffset must still round-trip.
        var value = new DateTimeOffset(2026, 4, 21, 12, 34, 56, TimeSpan.FromHours(-5));
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteDateTimeOffset(ms, value);
        ms.Position = 0;

        var decoded = OrderedByteHelpers.ReadDateTimeOffset(ms);

        decoded.Should().Be(value, "a valid DateTimeOffset must still round-trip after the range guard");
    }
}
