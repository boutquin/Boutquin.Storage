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

// F-81 — DictionaryCodec.DecodeStringSlice / DecodeDecimalSlice compute the full-prefix decode
// length as `firstRowIndex + count` in 32-bit arithmetic. A forged `firstRowIndex` near
// int.MaxValue (sourced from a corrupt footer slice descriptor) overflows that sum to a negative
// number, which then leaks a raw OverflowException / ArgumentOutOfRangeException out of the public
// columnar decode API instead of the typed InvalidDataException corruption signal the rest of the
// codec layer uses. The sum must be computed as `long` (or guarded) so the overflow becomes a
// typed corruption throw.
public sealed class DictionaryCodecForgedSliceOverflowTests
{
    [Fact]
    public void DecodeStringSlice_ForgedFirstRowIndexNearIntMax_ThrowsTypedInvalidData_NotRawOverflow()
    {
        // A small, well-formed single-string dictionary payload — the corruption is in the
        // caller-supplied slice descriptor, not the payload bytes.
        var encoded = DictionaryCodec.EncodeString(["a"]);

        var act = () => DictionaryCodec.DecodeStringSlice(encoded, firstRowIndex: int.MaxValue - 1, count: 5);

        act.Should().Throw<InvalidDataException>(
            "a forged firstRowIndex whose sum with count overflows int must surface a typed "
            + "corruption exception, not a raw OverflowException from `firstRowIndex + count`");
    }

    [Fact]
    public void DecodeDecimalSlice_ForgedFirstRowIndexNearIntMax_ThrowsTypedInvalidData_NotRawOverflow()
    {
        var encoded = DictionaryCodec.EncodeDecimal([1.0m]);

        var act = () => DictionaryCodec.DecodeDecimalSlice(encoded, firstRowIndex: int.MaxValue - 1, count: 5);

        act.Should().Throw<InvalidDataException>(
            "a forged firstRowIndex whose sum with count overflows int must surface a typed "
            + "corruption exception, not a raw OverflowException from `firstRowIndex + count`");
    }

    [Fact]
    public void DecodeStringSlice_ValidSlice_RoundTripsUnchanged()
    {
        // Regression guard — a legitimate slice must still decode after the overflow guard.
        var encoded = DictionaryCodec.EncodeString(["x", "y", "z", "w"]);

        var slice = DictionaryCodec.DecodeStringSlice(encoded, firstRowIndex: 1, count: 2);

        slice.Should().Equal("y", "z");
    }

    [Fact]
    public void DecodeDecimalSlice_ValidSlice_RoundTripsUnchanged()
    {
        var encoded = DictionaryCodec.EncodeDecimal([10m, 20m, 30m, 40m]);

        var slice = DictionaryCodec.DecodeDecimalSlice(encoded, firstRowIndex: 2, count: 2);

        slice.Should().Equal(30m, 40m);
    }
}
