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
using System.Text.Json;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins decode-side contracts of Domain value objects and helpers: decoded or deserialized input
/// either reconstructs the value that was written or surfaces a typed failure — never a silently
/// different value and never a raw BCL argument exception.
/// </summary>
public sealed class DomainValueObjectWireContractTests
{
    [Fact]
    public void SchemaVersion_JsonRoundTrip_PreservesValue()
    {
        var json = JsonSerializer.Serialize(new SchemaVersion(7));

        var decoded = JsonSerializer.Deserialize<SchemaVersion>(json);

        decoded.Value.Should().Be(7);
    }

    [Theory]
    [InlineData("{\"Value\":0}")]
    [InlineData("{\"Value\":-3}")]
    public void SchemaVersion_JsonWithNonPositiveValue_IsRejected(string json)
    {
        // Deserialization must run the same positivity check as construction; a version of 0 is a
        // state the type's own constructor rejects.
        var act = () => JsonSerializer.Deserialize<SchemaVersion>(json);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "a non-positive schema version must be refused on decode exactly as on construction");
    }

    [Fact]
    public void SchemaVersion_JsonWithMissingValue_IsRejected()
    {
        var act = () => JsonSerializer.Deserialize<SchemaVersion>("{}");

        act.Should().Throw<JsonException>(
            "a payload with no version must not decode to the default 0");
    }

    [Fact]
    public void SchemaVersion_WithExpressionToNonPositive_IsRejected()
    {
        var act = () => new SchemaVersion(3) with { Value = 0 };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0L, -60L)]
    [InlineData(0L, -1L)]
    [InlineData(3_155_378_975_999_999_999L, 60L)]
    [InlineData(3_155_378_975_999_999_999L, 840L)]
    public void ToDateTimeOffsetFromUtc_LocalTimeOutsideDateTimeRange_ThrowsInvalidData(long utcTicks, long offsetMinutes)
    {
        // Each part is individually in range, but UTC + offset leaves the DateTime range: no valid
        // writer produces this pair, so it is corrupt input and must surface the typed contract.
        var act = () => CorruptionGuards.ToDateTimeOffsetFromUtc(utcTicks, offsetMinutes);

        act.Should().Throw<InvalidDataException>(
            "a cross-field out-of-range DateTimeOffset is corruption, not an argument error");
    }

    [Fact]
    public void OrderedByteHelpers_ReadDateTimeOffset_ForgedCrossFieldPair_ThrowsInvalidData()
    {
        Span<byte> buf = stackalloc byte[10];
        BinaryPrimitives.WriteUInt64BigEndian(buf[..8], 0UL);
        BinaryPrimitives.WriteInt16BigEndian(buf.Slice(8, 2), (short)(-60 ^ short.MinValue));
        using var ms = new MemoryStream(buf.ToArray());

        var act = () => OrderedByteHelpers.ReadDateTimeOffset(ms);

        act.Should().Throw<InvalidDataException>(
            "the public ordered-wire reader must not leak a raw ArgumentOutOfRangeException on forged bytes");
    }

    [Fact]
    public void ToDateTimeOffsetFromUtc_ValidPair_RoundTrips()
    {
        var original = new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.FromHours(-5));

        var decoded = CorruptionGuards.ToDateTimeOffsetFromUtc(original.UtcTicks, (long)original.Offset.TotalMinutes);

        decoded.Should().Be(original);
        decoded.Offset.Should().Be(original.Offset);
    }
}
