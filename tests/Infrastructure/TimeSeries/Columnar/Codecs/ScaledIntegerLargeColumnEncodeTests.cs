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
/// ScaledInteger columns are whole-column encoded (non-restartable), so a large flush hands
/// the encoder the full segment row count. The DECODE side reads bit offsets through a
/// <c>long</c> cursor precisely because a packed payload near the 2 GiB array limit holds
/// more than 2³¹ bits — but the ENCODE side that produces such payloads kept an <c>int</c>
/// cursor, so a legitimate flush of ~34 M rows at bit width 63 wrapped the cursor negative
/// and crashed with a raw <c>IndexOutOfRangeException</c> mid-encode.
/// </summary>
public sealed class ScaledIntegerLargeColumnEncodeTests
{
    [Fact]
    public void EncodeDecimal_ColumnPastTwoPow31PackedBits_EncodesAndSliceDecodes()
    {
        // 34.1 M rows at bit width 63 → 2,148,300,000 packed bits > int.MaxValue.
        // Range 2^62 forces width 63; every other row is the minimum so the input is cheap.
        const int rows = 34_100_000;
        var values = new decimal[rows];
        values[1] = 4_611_686_018_427_387_904m; // 2^62
        values[^1] = 1m;

        var payload = ScaledIntegerBitPackedCodec.EncodeDecimal(values);

        // The final rows live past the 2^31-bit boundary — decode a tail slice to prove the
        // high-offset writes landed where the (long-cursor) reader expects them.
        var tail = ScaledIntegerBitPackedCodec.DecodeDecimalSlice(payload, firstRowIndex: rows - 2, count: 2);
        tail.Should().Equal(0m, 1m);

        var head = ScaledIntegerBitPackedCodec.DecodeDecimalSlice(payload, firstRowIndex: 0, count: 3);
        head.Should().Equal(0m, 4_611_686_018_427_387_904m, 0m);
    }
}
