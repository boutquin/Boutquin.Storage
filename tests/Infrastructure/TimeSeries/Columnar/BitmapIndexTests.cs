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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

[ColumnarStorable]
public partial record IndexedBar : IHasDate
{
    public DateOnly Date { get; init; }

    [BitmapIndex]
    public string Ticker { get; init; } = "";

    [BitmapIndex]
    public bool IsAdjusted { get; init; }

    public double Vwap { get; init; }

    public IndexedBar() { }
}

[ColumnarStorable]
public partial record UnindexedBar : IHasDate
{
    public DateOnly Date { get; init; }
    public string Ticker { get; init; } = "";
    public bool IsAdjusted { get; init; }
    public double Vwap { get; init; }
    public UnindexedBar() { }
}

/// <summary>
/// Phase E2 — cross-row-group bitmap indexes for low-cardinality columns. Tests writer-side
/// emission on <c>[BitmapIndex]</c>-annotated properties, reader-side exposure via
/// <see cref="CompressedColumnarSegmentFormat{T}.ReadBitmapIndexes"/>, and the DDIA Fig 4-9
/// bitmap-AND worked example across two indexed columns.
/// </summary>
public sealed class BitmapIndexTests
{
    [Fact]
    public async Task Writer_EmitsBitmapIndex_ForEveryAnnotatedProperty()
    {
        var format = new CompressedColumnarSegmentFormat<IndexedBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12)
            .Select(i => new IndexedBar
            {
                Date = new DateOnly(2020, 1, 1).AddDays(i),
                Ticker = i < 6 ? "AAPL" : "MSFT",
                IsAdjusted = (i % 2) == 0,
                Vwap = 100.0 + i,
            })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var indexes = format.ReadBitmapIndexes(stream);
        indexes.Should().NotBeNull();
        indexes!.Should().HaveCount(2);

        var tickerIndex = indexes.First(e => e.ColumnName == "Ticker");
        var adjustedIndex = indexes.First(e => e.ColumnName == "IsAdjusted");

        tickerIndex.ValueToBitmap.Should().HaveCount(2); // AAPL, MSFT
        adjustedIndex.ValueToBitmap.Should().HaveCount(2); // true, false

        // Decode AAPL's bitmap — first 6 rows should be set.
        var aaplEntry = tickerIndex.ValueToBitmap
            .First(v => System.Text.Encoding.UTF8.GetString(v.Value) == "AAPL");
        var aaplBits = BitmapRleCodec.Decode(aaplEntry.Bitmap, 12);
        aaplBits.Take(6).Should().AllBeEquivalentTo(true);
        aaplBits.Skip(6).Should().AllBeEquivalentTo(false);
    }

    [Fact]
    public async Task Writer_OmitsBitmapIndexSection_WhenNoColumnsAreAnnotated()
    {
        var format = new CompressedColumnarSegmentFormat<UnindexedBar>();
        var input = new[]
        {
            new UnindexedBar { Date = new DateOnly(2020, 1, 1), Ticker = "AAPL", IsAdjusted = true, Vwap = 100.0 },
            new UnindexedBar { Date = new DateOnly(2020, 1, 2), Ticker = "MSFT", IsAdjusted = false, Vwap = 101.0 },
        };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var indexes = format.ReadBitmapIndexes(stream);
        indexes.Should().BeNull();
    }

    [Fact]
    public async Task BitmapIndex_DDIA_Fig4_9_BitmapAnd_IsReachable()
    {
        // DDIA 2e Fig 4-9: bitmap-AND across two low-cardinality columns identifies rows
        // matching (Ticker = "AAPL" AND IsAdjusted = true) in one ANDed bit vector.
        var format = new CompressedColumnarSegmentFormat<IndexedBar>(rowGroupMaxRows: 4);
        var input = new[]
        {
            new IndexedBar { Date = new DateOnly(2020, 1, 1), Ticker = "AAPL", IsAdjusted = true, Vwap = 1 },
            new IndexedBar { Date = new DateOnly(2020, 1, 2), Ticker = "AAPL", IsAdjusted = false, Vwap = 2 },
            new IndexedBar { Date = new DateOnly(2020, 1, 3), Ticker = "MSFT", IsAdjusted = true, Vwap = 3 },
            new IndexedBar { Date = new DateOnly(2020, 1, 4), Ticker = "AAPL", IsAdjusted = true, Vwap = 4 },
            new IndexedBar { Date = new DateOnly(2020, 1, 5), Ticker = "MSFT", IsAdjusted = false, Vwap = 5 },
        };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;
        var indexes = format.ReadBitmapIndexes(stream)!;

        var tickerIndex = indexes.First(e => e.ColumnName == "Ticker");
        var adjustedIndex = indexes.First(e => e.ColumnName == "IsAdjusted");

        var aaplBits = BitmapRleCodec.Decode(
            tickerIndex.ValueToBitmap.First(v => System.Text.Encoding.UTF8.GetString(v.Value) == "AAPL").Bitmap,
            5);
        var adjustedBits = BitmapRleCodec.Decode(
            adjustedIndex.ValueToBitmap.First(v => v.Value.Length == 1 && v.Value[0] == 1).Bitmap,
            5);

        // AND them row-wise.
        var anded = new bool[5];
        for (var i = 0; i < 5; i++)
        {
            anded[i] = aaplBits[i] && adjustedBits[i];
        }

        anded.Should().Equal(true, false, false, true, false);
    }

    // ── BitmapIndexValue equality (byte-content, not reference) ───────────────

    [Fact]
    public void BitmapIndexValue_Equals_SameContentDifferentInstances_ReturnsTrue()
    {
        var a = new BitmapIndexValue([1, 2, 3], [4, 5, 6]);
        var b = new BitmapIndexValue([1, 2, 3], [4, 5, 6]);
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void BitmapIndexValue_Equals_DifferentValueBytes_ReturnsFalse()
    {
        var a = new BitmapIndexValue([1, 2, 3], [4, 5, 6]);
        var b = new BitmapIndexValue([1, 2, 9], [4, 5, 6]);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void BitmapIndexValue_Equals_DifferentBitmapBytes_ReturnsFalse()
    {
        var a = new BitmapIndexValue([1, 2, 3], [4, 5, 6]);
        var b = new BitmapIndexValue([1, 2, 3], [4, 5, 7]);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void BitmapIndexValue_Equals_Null_ReturnsFalse()
    {
        var a = new BitmapIndexValue([1], [2]);
        a.Equals((BitmapIndexValue?)null).Should().BeFalse();
    }

    [Fact]
    public void BitmapIndexValue_Constructor_NullValue_Throws()
    {
        Action act = () => _ = new BitmapIndexValue(null!, [1]);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BitmapIndexValue_Constructor_NullBitmap_Throws()
    {
        Action act = () => _ = new BitmapIndexValue([1], null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── BitmapIndexEntry equality (column name + ordered SequenceEqual on values) ─

    [Fact]
    public void BitmapIndexEntry_Equals_SameContent_ReturnsTrue()
    {
        var a = new BitmapIndexEntry("col", [new BitmapIndexValue([1], [2])]);
        var b = new BitmapIndexEntry("col", [new BitmapIndexValue([1], [2])]);
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void BitmapIndexEntry_Equals_DifferentColumnName_ReturnsFalse()
    {
        var a = new BitmapIndexEntry("col", [new BitmapIndexValue([1], [2])]);
        var b = new BitmapIndexEntry("other", [new BitmapIndexValue([1], [2])]);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void BitmapIndexEntry_Equals_DifferentValueCount_ReturnsFalse()
    {
        var a = new BitmapIndexEntry("col", [new BitmapIndexValue([1], [2])]);
        var b = new BitmapIndexEntry("col",
        [
            new BitmapIndexValue([1], [2]),
            new BitmapIndexValue([3], [4]),
        ]);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void BitmapIndexEntry_Equals_DifferentValueOrdering_ReturnsFalse()
    {
        // SequenceEqual is order-sensitive; an entry with the same set of values in a
        // different order must NOT compare equal — the encoder relies on a deterministic
        // sort order at footer-write time.
        var v1 = new BitmapIndexValue([1], [9]);
        var v2 = new BitmapIndexValue([2], [8]);
        var a = new BitmapIndexEntry("col", [v1, v2]);
        var b = new BitmapIndexEntry("col", [v2, v1]);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void BitmapIndexEntry_Equals_Null_ReturnsFalse()
    {
        var a = new BitmapIndexEntry("col", [new BitmapIndexValue([1], [2])]);
        a.Equals((BitmapIndexEntry?)null).Should().BeFalse();
    }

    [Fact]
    public void BitmapIndexEntry_Constructor_NullColumnName_Throws()
    {
        Action act = () => _ = new BitmapIndexEntry(null!, []);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BitmapIndexEntry_Constructor_NullValueToBitmap_Throws()
    {
        Action act = () => _ = new BitmapIndexEntry("col", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // Equality and hash derive from ValueToBitmap, so the record must not alias the caller's
    // list: a later mutation of that list would silently change the record's identity.
    [Fact]
    public void BitmapIndexEntry_CallerMutatesSourceListAfterConstruction_IdentityIsUnchanged()
    {
        var source = new List<BitmapIndexValue> { new([1], [2]) };
        var entry = new BitmapIndexEntry("col", source);
        var twin = new BitmapIndexEntry("col", [new BitmapIndexValue([1], [2])]);
        var hashBefore = entry.GetHashCode();
        var set = new HashSet<BitmapIndexEntry> { entry };

        source.Add(new BitmapIndexValue([3], [4]));
        source[0] = new BitmapIndexValue([9], [9]);

        entry.ValueToBitmap.Should().ContainSingle("the caller's later Add must not reach the record");
        entry.ValueToBitmap[0].Should().Be(new BitmapIndexValue([1], [2]));
        entry.Should().Be(twin);
        entry.GetHashCode().Should().Be(hashBefore);
        set.Contains(entry).Should().BeTrue("a hash-set member must stay findable");
    }

    // The exposed list must not be writable through a cast of the IReadOnlyList<T> getter.
    [Fact]
    public void BitmapIndexEntry_ValueToBitmapCastToIList_RejectsWrites()
    {
        var entry = new BitmapIndexEntry("col", new List<BitmapIndexValue> { new([1], [2]) });
        var twin = new BitmapIndexEntry("col", [new BitmapIndexValue([1], [2])]);

        var asList = entry.ValueToBitmap.Should().BeAssignableTo<IList<BitmapIndexValue>>().Subject;
        Action write = () => asList[0] = new BitmapIndexValue([7], [7]);

        write.Should().Throw<NotSupportedException>("a consumer must not rewrite the record through its getter");
        entry.Should().Be(twin);
    }

    [Fact]
    public void Compute_DoubleColumn_BucketsNaNTogether_AndCollapsesSignedZero()
    {
        // Finding #13 — float/double must bucket by raw bit pattern, not value equality, so all
        // NaNs canonicalise to one bucket. ±0.0 ALSO collapse to the +0.0 bucket: the row-level
        // filter compares via Comparer<double>.Default (-0.0 == +0.0), so distinct zero buckets
        // would let an equality skip false-drop a segment whose every row matches (the DDIA
        // data-skipping conservatism rule: a skip must never lose a matching row).
        var values = new[] { double.NaN, double.NaN, double.NaN, 0.0, -0.0 };
        var desc = new Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.ColumnDescriptor(
            "V", 0, 0, ColumnEncodingTag.GorillaXor, 1, ColumnFieldTypeCode.Double);

        var entry = BitmapIndexComputer.Compute(desc, values);

        // All three NaN rows share one canonical-NaN bucket.
        var nanBytes = BitConverter.GetBytes(double.NaN);
        var nan = entry.ValueToBitmap.Single(v => v.Value.AsSpan().SequenceEqual(nanBytes));
        BitmapRleCodec.Decode(nan.Bitmap, values.Length).Should().Equal(true, true, true, false, false);

        // Both zero rows land in the single +0.0 bucket; no -0.0 bucket exists.
        var posZero = BitConverter.GetBytes(0.0);
        var negZero = BitConverter.GetBytes(-0.0);
        var zero = entry.ValueToBitmap.Single(v => v.Value.AsSpan().SequenceEqual(posZero));
        BitmapRleCodec.Decode(zero.Bitmap, values.Length).Should().Equal(false, false, false, true, true);
        entry.ValueToBitmap.Any(v => v.Value.AsSpan().SequenceEqual(negZero)).Should().BeFalse(
            "-0.0 canonicalises into the +0.0 bucket, matching the row filter's zero identity");
        entry.ValueToBitmap.Should().HaveCount(2, "NaN ∪ zero");
    }
    [Fact]
    public void Compute_Int32ColumnWithNegativeValues_QueryEncodingMatchesBucketKeys()
    {
        // Signed-integer bucket keys are raw little-endian bytes; the query side
        // (ColumnarPredicatePushdown.EncodeValue) must produce byte-identical keys for
        // negative values or an equality lookup would miss the bucket and wrongly skip.
        var values = new[] { -1, 0, 1, -1 };
        var desc = new Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.ColumnDescriptor(
            "N", 0, 0, ColumnEncodingTag.DeltaVlq, 1, ColumnFieldTypeCode.Int32);

        var entry = BitmapIndexComputer.Compute(desc, values);

        var queryKey = ColumnarPredicatePushdown.EncodeValue(-1, ColumnFieldTypeCode.Int32);
        queryKey.Should().NotBeNull();
        var bucket = entry.ValueToBitmap.Single(v => v.Value.AsSpan().SequenceEqual(queryKey));
        BitmapRleCodec.Decode(bucket.Bitmap, values.Length).Should().Equal(true, false, false, true);
    }

}
