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
using System.Reflection;

using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// The restartable whole-column decode path (<c>DecodeColumn</c>, reached when a column carries no
/// per-row-group byte offsets — e.g. a Dictionary string column) sums every footer row group's
/// <c>rowCount</c> into the segment-wide total handed to the self-bounding codecs. A forged-but-CRC
/// -valid footer whose row-group <c>rowCount</c>s sum past <see cref="int.MaxValue"/> must fail
/// closed with the same typed corruption exception the non-restartable sibling path uses, NOT a raw
/// <c>OverflowException</c> (negative wrap → negative-size array) nor a wrapped/uncaught
/// <c>IndexOutOfRangeException</c> (positive wrap → short decoded array indexed by the rehydrate
/// loop, which sits outside the per-column decode try/catch). The non-restartable path already
/// accumulates in <c>long</c> and calls <c>ValidateRowCountAgainstPayload</c>; this pins the same
/// discipline on the restartable path, invoking the private <c>DecodeColumn</c> directly via
/// reflection (the established isolation seam — see <c>Phase10HardeningTests</c>) so the forged
/// row counts reach the restartable accumulator without an upstream non-restartable column
/// (e.g. Date/DeltaOfDeltaVlq) intercepting them first.
/// </summary>
public sealed class ColumnarRestartableSummedRowCountOverflowTests
{
    // Two groups whose int sum wraps NEGATIVE: 0x4000_0001 + 0x4000_0001 = 0x8000_0002 which, as
    // a 32-bit int, is -2147483646; as a long it is 2147483650 (> int.MaxValue = 2147483647).
    private const int NegativeWrapRowCountPerGroup = 0x4000_0001; // 1,073,741,825

    // Four groups whose int sum wraps to a SMALL POSITIVE value: 3 × 0x5555_5556 + 8 sums (as long)
    // to 4294967306 = 0x1_0000_000A, i.e. int total = 10, while the true row total is ~3.2 billion.
    private const int PositiveWrapRowCountA = 0x5555_5556; // 1,431,655,766
    private const int PositiveWrapRowCountB = 8;

    [Fact]
    public void DecodeColumn_RestartableDictionaryColumn_SummedRowCountWrapsNegative_ThrowsTyped()
    {
        var act = InvokeDecodeColumnWithForgedRowCounts(
            new[] { NegativeWrapRowCountPerGroup, NegativeWrapRowCountPerGroup });

        var ex = act.Should().Throw<TargetInvocationException>().Which.InnerException;
        ex.Should().BeOfType<TruncatedSegmentException>(
            "a restartable-column summed rowCount that wraps an int accumulator negative must surface "
            + "the typed corruption exception, not a raw OverflowException from a negative-size array");
    }

    [Fact]
    public void DecodeColumn_RestartableDictionaryColumn_SummedRowCountWrapsSmallPositive_ThrowsTyped()
    {
        // The positive-wrap arm is the dangerous one: a short decoded array fed to the rehydrate
        // loop (outside the per-column try/catch) leaks a raw IndexOutOfRangeException. The fix
        // rejects the wrap before any decode/allocation, so this also surfaces the typed exception.
        var act = InvokeDecodeColumnWithForgedRowCounts(
            new[] { PositiveWrapRowCountA, PositiveWrapRowCountA, PositiveWrapRowCountA, PositiveWrapRowCountB });

        var ex = act.Should().Throw<TargetInvocationException>().Which.InnerException;
        ex.Should().BeOfType<TruncatedSegmentException>(
            "a restartable-column summed rowCount that wraps an int accumulator to a small positive "
            + "value must be rejected with the typed corruption exception before the short-array decode "
            + "reaches the out-of-try-catch rehydrate loop");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Reflection harness — mirrors Phase10HardeningTests' DecodeColumn invocation.
    // ═══════════════════════════════════════════════════════════════════════════

    private static Action InvokeDecodeColumnWithForgedRowCounts(int[] forgedRowCounts)
    {
        var formatType = typeof(CompressedColumnarSegmentFormat<ColumnarBar>);
        var decodeColumn = formatType.GetMethod(
            "DecodeColumn",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DecodeColumn not found via reflection.");

        // ColumnInfo for the Ticker (string Dictionary) column — restartable, so DecodeColumn
        // dispatches to DictionaryCodec.DecodeString after computing the summed total.
        var openColumnInfoType = typeof(CompressedColumnarSegmentFormat<>)
            .GetNestedType("ColumnInfo", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ColumnInfo nested type not found.");
        var columnInfoType = openColumnInfoType.MakeGenericType(typeof(ColumnarBar));
        var columnInfoCtor = columnInfoType.GetConstructors()[0];

        var tickerProp = typeof(ColumnarBar).GetProperty("Ticker")!;
        var columnInfo = columnInfoCtor.Invoke(
        [
            "Ticker",
            tickerProp,
            ColumnEncoding.Dictionary,
            ColumnEncodingTag.Dictionary,
            (byte)0,
        ]);

        // SegmentFooter with N row groups carrying the forged rowCounts.
        var footerType = typeof(FooterReader)
            .Assembly.GetType("Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.SegmentFooter")!;
        var rowGroupType = typeof(FooterReader)
            .Assembly.GetType("Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.RowGroupDescriptor")!;

        var rowGroupsList = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(rowGroupType))!;
        var firstRowIndex = 0;
        foreach (var rowCount in forgedRowCounts)
        {
            var rg = Activator.CreateInstance(
                rowGroupType,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), rowCount, firstRowIndex)!;
            rowGroupsList.Add(rg);
            firstRowIndex += rowCount; // overflow here is irrelevant to the accumulator under test
        }

        var footerCtorParams = footerType.GetConstructors()[0].GetParameters();
        var footerArgs = new object?[footerCtorParams.Length];
        for (var i = 0; i < footerCtorParams.Length; i++)
        {
            footerArgs[i] = footerCtorParams[i].Name switch
            {
                "FormatVersion" => (uint)1,
                "Columns" => Activator.CreateInstance(
                    typeof(List<>).MakeGenericType(typeof(ColumnDescriptor))),
                "RowGroups" => rowGroupsList,
                _ => null,
            };
        }
        var footer = footerCtorParams[0].ParameterType == footerType
            ? footerArgs[0]
            : Activator.CreateInstance(footerType, footerArgs);

        // A small payload — far too few bytes to encode billions of rows, so the payload-derived
        // bound is what a correctly-hardened DecodeColumn must consult after summing in long.
        var payload = new byte[64];

        return () => decodeColumn.Invoke(null, [columnInfo, payload, footer]);
    }
}
