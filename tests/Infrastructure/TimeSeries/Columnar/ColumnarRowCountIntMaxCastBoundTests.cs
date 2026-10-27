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

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// F-37 (residual int.MaxValue-cast gap) — the non-restartable assembly path allocates a flat
/// array of <c>(int)total</c> rows. The pre-landed <c>ValidateRowCountAgainstPayload</c> bounds
/// <c>total</c> against <c>payloadByteLength × 8</c> (≥ 1 bit per value), but that ceiling can
/// exceed <see cref="int.MaxValue"/> for a column payload above 256 MiB. A forged <c>total</c>
/// in <c>(int.MaxValue, payloadByteLength × 8]</c> then clears the bytes×8 ceiling yet overflows
/// the <c>(int)total</c> cast — producing a negative or wrapped allocation length. The guard must
/// reject any <c>total &gt; int.MaxValue</c> with the typed corruption exception BEFORE the cast.
/// </summary>
public sealed class ColumnarRowCountIntMaxCastBoundTests
{
    [Fact]
    public void ValidateRowCountAgainstPayload_TotalAboveIntMax_ClearingBytesTimesEightCeiling_Throws()
    {
        // total just above int.MaxValue; payload large enough that total ≤ payloadByteLength × 8,
        // so the pre-landed bytes×8 ceiling does NOT fire and the residual (int) cast is the only
        // remaining defense. Choose payloadByteLength = total so the ceiling is total × 8 ≫ total.
        var total = (long)int.MaxValue + 1;
        var payloadByteLength = total; // bytes×8 ceiling = 8 × total, far above `total`.

        Action act = () => CompressedColumnarSegmentFormat<NarrowBar>
            .ValidateRowCountAgainstPayload("Date", total, payloadByteLength);

        act.Should().Throw<TruncatedSegmentException>(
            "a summed rowCount above int.MaxValue overflows the (int)total flat-array cast and must be "
            + "rejected even when it clears the bytes×8 ceiling");
    }

    [Fact]
    public void ValidateRowCountAgainstPayload_TotalAtIntMax_WithinCeiling_DoesNotThrow()
    {
        // The boundary just below the cast limit, comfortably within the bytes×8 ceiling, stays valid.
        var total = (long)int.MaxValue;
        var payloadByteLength = total; // bytes×8 = 8 × total ≥ total.

        Action act = () => CompressedColumnarSegmentFormat<NarrowBar>
            .ValidateRowCountAgainstPayload("Date", total, payloadByteLength);

        act.Should().NotThrow("int.MaxValue rows cast to int without overflow and clears the ceiling");
    }
}
