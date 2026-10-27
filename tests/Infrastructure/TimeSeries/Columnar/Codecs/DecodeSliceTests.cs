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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar.Codecs;

/// <summary>
/// P3.2 — DecodeSlice on each codec: verifies that the slice path skips
/// <c>firstRowIndex</c> rows and returns exactly <c>count</c> values with correct
/// content. Non-restartable codecs (GorillaXor, DeltaOfDeltaVlq) are not tested
/// here because they are encoded per-row-group on disk; their round-trip correctness
/// is covered by <see cref="ColumnBatchTests"/>.
/// </summary>
public sealed class DecodeSliceTests
{
    // ───── ScaledIntegerBitPacked ────────────────────────────────────────

    [Fact]
    public void ScaledIntegerBitPacked_DecodeDecimalSlice_FromZero()
    {
        var values = new[] { 10m, 20m, 30m, 40m, 50m };
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(values);

        var slice = ScaledIntegerBitPackedCodec.DecodeDecimalSlice(encoded, firstRowIndex: 0, count: 5);

        slice.Should().Equal(values);
    }

    [Fact]
    public void ScaledIntegerBitPacked_DecodeDecimalSlice_SkipsFirstN()
    {
        var values = new[] { 100m, 200m, 300m, 400m, 500m };
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(values);

        var slice = ScaledIntegerBitPackedCodec.DecodeDecimalSlice(encoded, firstRowIndex: 2, count: 2);

        slice.Should().Equal(300m, 400m);
    }

    [Fact]
    public void ScaledIntegerBitPacked_DecodeDecimalSlice_LastElement()
    {
        var values = new[] { 1m, 2m, 3m };
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(values);

        var slice = ScaledIntegerBitPackedCodec.DecodeDecimalSlice(encoded, firstRowIndex: 2, count: 1);

        slice.Should().Equal(3m);
    }

    [Fact]
    public void ScaledIntegerBitPacked_DecodeDecimalSlice_CountZero_ReturnsEmpty()
    {
        var values = new[] { 1m, 2m, 3m };
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(values);

        var slice = ScaledIntegerBitPackedCodec.DecodeDecimalSlice(encoded, firstRowIndex: 1, count: 0);

        slice.Should().BeEmpty();
    }

    // ───── DeltaVlq Int64 ────────────────────────────────────────────────

    [Fact]
    public void DeltaVlq_DecodeSliceInt64_FromZero()
    {
        var values = new long[] { 10, 20, 30, 40, 50 };
        var encoded = DeltaVlqCodec.EncodeInt64(values);

        var slice = DeltaVlqCodec.DecodeSliceInt64(encoded, firstRowIndex: 0, count: 5);

        slice.Should().Equal(values);
    }

    [Fact]
    public void DeltaVlq_DecodeSliceInt64_SkipsFirstN()
    {
        var values = new long[] { 10, 20, 30, 40, 50 };
        var encoded = DeltaVlqCodec.EncodeInt64(values);

        var slice = DeltaVlqCodec.DecodeSliceInt64(encoded, firstRowIndex: 1, count: 3);

        slice.Should().Equal(20L, 30L, 40L);
    }

    // ───── DeltaVlq Int32 ────────────────────────────────────────────────

    [Fact]
    public void DeltaVlq_DecodeSliceInt32_SkipsFirstN()
    {
        var values = new[] { 10, 20, 30, 40, 50 };
        var encoded = DeltaVlqCodec.EncodeInt32(values);

        var slice = DeltaVlqCodec.DecodeSliceInt32(encoded, firstRowIndex: 2, count: 2);

        slice.Should().Equal(30, 40);
    }

    [Fact]
    public void DeltaVlq_DecodeSliceInt32_CountZero_ReturnsEmpty()
    {
        var values = new[] { 1, 2, 3 };
        var encoded = DeltaVlqCodec.EncodeInt32(values);

        var slice = DeltaVlqCodec.DecodeSliceInt32(encoded, firstRowIndex: 1, count: 0);

        slice.Should().BeEmpty();
    }

    // ───── BitmapRle ────────────────────────────────────────────────────

    [Fact]
    public void BitmapRle_DecodeSlice_FromZero()
    {
        var values = new[] { true, true, false, false, true };
        var encoded = BitmapRleCodec.Encode(values);

        var slice = BitmapRleCodec.DecodeSlice(encoded, firstRowIndex: 0, count: 5);

        slice.Should().Equal(values);
    }

    [Fact]
    public void BitmapRle_DecodeSlice_SkipsFirstN()
    {
        var values = new[] { true, true, false, false, true };
        var encoded = BitmapRleCodec.Encode(values);

        var slice = BitmapRleCodec.DecodeSlice(encoded, firstRowIndex: 2, count: 2);

        slice.Should().Equal(false, false);
    }

    [Fact]
    public void BitmapRle_DecodeSlice_LastElement()
    {
        var values = new[] { true, true, false, false, true };
        var encoded = BitmapRleCodec.Encode(values);

        var slice = BitmapRleCodec.DecodeSlice(encoded, firstRowIndex: 4, count: 1);

        slice.Should().Equal(true);
    }

    // ───── Dictionary (string) ───────────────────────────────────────────

    [Fact]
    public void DictionaryCodec_DecodeStringSlice_FromZero()
    {
        var values = new[] { "A", "B", "C", "A", "B" };
        var encoded = DictionaryCodec.EncodeString(values);

        var slice = DictionaryCodec.DecodeStringSlice(encoded, firstRowIndex: 0, count: 5);

        slice.Should().Equal(values);
    }

    [Fact]
    public void DictionaryCodec_DecodeStringSlice_SkipsFirstN()
    {
        var values = new[] { "A", "B", "C", "A", "B" };
        var encoded = DictionaryCodec.EncodeString(values);

        var slice = DictionaryCodec.DecodeStringSlice(encoded, firstRowIndex: 1, count: 3);

        slice.Should().Equal("B", "C", "A");
    }

    // ───── Dictionary (decimal) ──────────────────────────────────────────

    [Fact]
    public void DictionaryCodec_DecodeDecimalSlice_SkipsFirstN()
    {
        var values = new[] { 1.5m, 2.5m, 1.5m, 3.5m };
        var encoded = DictionaryCodec.EncodeDecimal(values);

        var slice = DictionaryCodec.DecodeDecimalSlice(encoded, firstRowIndex: 2, count: 2);

        slice.Should().Equal(1.5m, 3.5m);
    }
}
