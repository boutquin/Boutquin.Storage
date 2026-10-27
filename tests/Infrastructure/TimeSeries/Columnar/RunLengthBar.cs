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

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// A record whose every column rides a run-length / delta codec that compresses a constant
/// column to a handful of bytes regardless of row count: Date (DeltaOfDeltaVlq), Flag (BitmapRle),
/// IntValue (DeltaVlq int), LongValue (DeltaVlq long). Deliberately carries NO fixed-width column
/// (no RawDouble, GorillaXor, or ScaledInteger), so a constant column of N rows produces a segment
/// whose byte length is far below N — the exact regime in which the round-5 segment-byte-length
/// allocation ceiling false-rejected valid data (F-W3-regress-01).
/// </summary>
[ColumnarStorable]
public partial record RunLengthBar : IHasDate
{
    public DateOnly Date { get; init; }
    public bool Flag { get; init; }
    public int IntValue { get; init; }
    public long LongValue { get; init; }

    public RunLengthBar() { }
}
