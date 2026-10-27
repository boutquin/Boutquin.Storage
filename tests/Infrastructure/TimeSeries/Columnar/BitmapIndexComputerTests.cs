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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Direct coverage for <see cref="BitmapIndexComputer.Compute"/>'s type-dispatch switch. The
/// integration tests in <see cref="BitmapIndexTests"/> only exercise <c>String</c> and
/// <c>Boolean</c> arms via the codegen path — these tests reach every <see cref="ColumnFieldTypeCode"/>
/// arm directly so a future arm regression surfaces here.
/// </summary>
public sealed class BitmapIndexComputerTests
{
    private static ColumnDescriptor Desc(string name, ColumnFieldTypeCode type) =>
        new(name, ByteOffset: 0, ByteLength: 0, ColumnEncodingTag.PlainRawBytes, CodecVersion: 0, type);

    [Fact]
    public void Compute_Int32_BuildsBitmapPerDistinctValue()
    {
        var values = new[] { 1, 2, 1, 3, 1 };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Int32), values);

        entry.ColumnName.Should().Be("x");
        entry.ValueToBitmap.Should().HaveCount(3);
    }

    [Fact]
    public void Compute_Int64_BuildsBitmapPerDistinctValue()
    {
        var values = new[] { 1L, 2L, 1L };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Int64), values);
        entry.ValueToBitmap.Should().HaveCount(2);
    }

    [Fact]
    public void Compute_Double_BuildsBitmapPerDistinctValue()
    {
        var values = new[] { 1.5, 2.5, 1.5 };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Double), values);
        entry.ValueToBitmap.Should().HaveCount(2);
    }

    [Fact]
    public void Compute_Single_BuildsBitmapPerDistinctValue()
    {
        var values = new[] { 1.5f, 2.5f, 1.5f };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Single), values);
        entry.ValueToBitmap.Should().HaveCount(2);
    }

    [Fact]
    public void Compute_Boolean_BuildsBitmapPerDistinctValue()
    {
        var values = new[] { true, false, true, false };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Boolean), values);
        entry.ValueToBitmap.Should().HaveCount(2);
    }

    [Fact]
    public void Compute_DateOnly_BuildsBitmapPerDistinctValue()
    {
        var values = new[]
        {
            new DateOnly(2020, 1, 1),
            new DateOnly(2020, 1, 2),
            new DateOnly(2020, 1, 1),
        };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.DateOnly), values);
        entry.ValueToBitmap.Should().HaveCount(2);
    }

    [Fact]
    public void Compute_Decimal_BuildsBitmapPerDistinctValue()
    {
        var values = new[] { 1.0m, 2.0m, 1.0m };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Decimal), values);
        entry.ValueToBitmap.Should().HaveCount(2);
    }

    [Fact]
    public void Compute_Decimal_BucketKeyNormalizesScale_MatchingQueryEncoding()
    {
        // #I/F: a value stored at scale 2 (100.00m) must produce a bitmap-bucket key equal to the
        // query-side encoding of the value-equal literal 100m. decimal.GetBits encodes the scale, so
        // without normalization the scale-2 key and a scale-0 query key differ and the bitmap-skip
        // path drops a row group that actually matches (data-skipping must be a conservative
        // superset — DDIA 2e p. 95-101).
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Decimal), new[] { 100.00m });
        var bucketKey = entry.ValueToBitmap.Single().Value;

        var queryKey = ColumnarPredicatePushdown.EncodeValue(100m, ColumnFieldTypeCode.Decimal);

        bucketKey.AsSpan().SequenceEqual(queryKey).Should().BeTrue(
            "100.00m stored and a 100m equality query must encode to the same bitmap-bucket key");
    }

    [Fact]
    public void Compute_String_NullValue_Throws(/* was: NullValuesGetSentinelEntry */)
    {
        // The string arm's former null-sentinel encoded null as an empty byte array ([]), which is
        // byte-identical to the encoding of a non-null empty string ("") — two distinct logical
        // buckets collided on one bitmap key, a latent silent-wrong-skip. The columnar writer
        // guarantees string columns are non-null (rejectNulls at write time; BSSG019 at compile
        // time), so the sentinel path is dead by construction. BuildString now fails fast on a null
        // rather than materialise the colliding sentinel: a null reaching the builder is a contract
        // violation that must surface, not encode ambiguously.
        var values = new string?[] { "a", null, "a", null, "b" };
        var act = () => BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.String), values);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*null*");
    }

    [Fact]
    public void Compute_String_AllNullValues_Throws(/* was: OnlyNullSentinelEmitted */)
    {
        var values = new string?[] { null, null, null };
        var act = () => BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.String), values);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*null*");
    }

    [Fact]
    public void Compute_String_EmptyString_DoesNotCollideWithNullSentinel()
    {
        // The collision the fail-fast guard prevents: a non-null empty string must produce its own
        // bucket whose key ([]) is no longer shared with a null sentinel (which can no longer exist).
        var values = new string?[] { "", "a", "" };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.String), values);

        entry.ValueToBitmap.Should().HaveCount(2, "two distinct non-null strings: \"\" and \"a\"");
        entry.ValueToBitmap.Should().ContainSingle(v => v.Value.Length == 0,
            "the empty-string bucket owns the empty-byte key unambiguously now that no null sentinel exists");
    }

    [Fact]
    public void Compute_String_NoNullValues_NoSentinelEmitted()
    {
        var values = new string?[] { "a", "b", "a" };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.String), values);
        entry.ValueToBitmap.Should().HaveCount(2);
        entry.ValueToBitmap.Should().NotContain(v => v.Value.Length == 0);
    }

    [Fact]
    public void Compute_NullValuesArray_Throws()
    {
        var act = () => BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Int32), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compute_OutputIsSortedByValueBytes()
    {
        // Determinism contract: entries must be ordered by the byte-lex sort of their
        // canonical value bytes so the footer is reproducible across runs.
        var values = new[] { 3, 1, 2, 1, 3 };
        var entry = BitmapIndexComputer.Compute(Desc("x", ColumnFieldTypeCode.Int32), values);

        var bytes = entry.ValueToBitmap.Select(v => v.Value).ToList();
        for (var i = 1; i < bytes.Count; i++)
        {
            bytes[i - 1].AsSpan().SequenceCompareTo(bytes[i]).Should().BeLessThan(0);
        }
    }
}
