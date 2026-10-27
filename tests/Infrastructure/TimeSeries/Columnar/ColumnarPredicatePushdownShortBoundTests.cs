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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// F-U2-01 — a forged footer (recomputed-valid CRC32C, the codebase's established threat model)
/// can store a min/max zone-map bound that is non-empty but SHORTER than the column field type's
/// fixed width (e.g. a 2-byte <c>MinValue</c> for an <see cref="ColumnFieldTypeCode.Int32"/>
/// column). <see cref="ColumnarPredicatePushdown.DecodeBound"/> runs OUTSIDE the per-column decode
/// try/catch on the predicate-pushdown read path, so a raw fixed-width read over a too-short bound
/// would leak an untyped <see cref="ArgumentOutOfRangeException"/> uncaught through the public query
/// API. The codebase's stated contract (FooterReader.EnsureRemaining: a crafted CRC-matching input
/// must produce a TYPED corruption exception) requires a <see cref="CorruptSegmentException"/>.
/// </summary>
/// <remarks>
/// <see cref="ColumnFieldTypeCode"/> is internal, so the per-type cases live in individual
/// <c>[Fact]</c> bodies (the enum is used inline) rather than as <c>[Theory]</c> parameters.
/// </remarks>
public sealed class ColumnarPredicatePushdownShortBoundTests
{
    private static void AssertShortBoundThrowsTyped(byte[] shortBound, ColumnFieldTypeCode fieldType)
    {
        var act = () => ColumnarPredicatePushdown.DecodeBound(shortBound, fieldType);

        act.Should().Throw<CorruptSegmentException>(
            "a non-empty bound shorter than the field type's fixed width is forged-footer "
            + "corruption and must surface the same typed exception as other footer corruption, "
            + "not a raw ArgumentOutOfRangeException");
    }

    [Fact]
    public void DecodeBound_ShortInt32Bound_ThrowsTypedCorruption()
    {
        AssertShortBoundThrowsTyped(new byte[2], ColumnFieldTypeCode.Int32);
        AssertShortBoundThrowsTyped(new byte[3], ColumnFieldTypeCode.Int32);
    }

    [Fact]
    public void DecodeBound_ShortInt64Bound_ThrowsTypedCorruption()
    {
        AssertShortBoundThrowsTyped(new byte[3], ColumnFieldTypeCode.Int64);
        AssertShortBoundThrowsTyped(new byte[7], ColumnFieldTypeCode.Int64);
    }

    [Fact]
    public void DecodeBound_ShortDecimalBound_ThrowsTypedCorruption()
    {
        AssertShortBoundThrowsTyped(new byte[5], ColumnFieldTypeCode.Decimal);
        AssertShortBoundThrowsTyped(new byte[12], ColumnFieldTypeCode.Decimal);
    }

    [Fact]
    public void DecodeBound_ShortDateOnlyDoubleSingleEnumBounds_ThrowTypedCorruption()
    {
        AssertShortBoundThrowsTyped(new byte[2], ColumnFieldTypeCode.DateOnly);  // needs >= 4
        AssertShortBoundThrowsTyped(new byte[3], ColumnFieldTypeCode.Double);    // needs >= 8
        AssertShortBoundThrowsTyped(new byte[1], ColumnFieldTypeCode.Single);    // needs >= 4
        AssertShortBoundThrowsTyped(new byte[5], ColumnFieldTypeCode.Enum);      // needs >= 8
    }

    [Fact]
    public void CanSkipByZoneMap_ShortInt32MinBound_ThrowsTypedCorruption_NotRawArgException()
    {
        // Drives the public predicate-pushdown decision path the way CanSkipRowGroup does: a forged
        // 2-byte Int32 MinValue must surface CorruptSegmentException, not the raw
        // ArgumentOutOfRangeException BinaryPrimitives.ReadInt32LittleEndian throws on a 2-byte span.
        var stats = new ColumnRowGroupStats(
            new byte[2], BitConverter.GetBytes(20), NullCount: 0, DistinctCount: 2);

        var act = () => ColumnarPredicatePushdown.CanSkipByZoneMap(
            stats, ComparisonOperator.Equal, 5, ColumnFieldTypeCode.Int32);

        act.Should().Throw<CorruptSegmentException>();
    }

    [Fact]
    public void DecodeBound_EmptyBound_StillReturnsNull_NoInformation()
        // The empty-bound short-circuit (no observed non-null values) is a distinct, legitimate
        // case and must keep returning null — only a NON-empty too-short bound is corruption.
        => ColumnarPredicatePushdown.DecodeBound([], ColumnFieldTypeCode.Int32).Should().BeNull();

    [Fact]
    public void DecodeBound_ExactWidthBound_DecodesNormally()
        => ColumnarPredicatePushdown
            .DecodeBound(BitConverter.GetBytes(42), ColumnFieldTypeCode.Int32)
            .Should().Be(42);
}
