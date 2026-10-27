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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// Author-facing per-field compression algorithm override for columnar time-series columns.
/// Attach with <c>[ColumnEncoding(ColumnEncoding.X)]</c> on the property; the source generator
/// validates type compatibility at build time (<c>BSSG007</c>) and routes the column to the
/// selected codec. Omitting the attribute uses the default selected by the source generator
/// from the property's CLR type (see the encoding table in
/// <c>docs/source-generator-type-reference.md</c>).
/// </summary>
/// <remarks>
/// Type-compatibility matrix (build-time-enforced):
/// <list type="bullet">
/// <item><see cref="Dictionary"/> — <see cref="string"/>, <see cref="decimal"/></item>
/// <item><see cref="BitmapRle"/> — <see cref="bool"/></item>
/// <item><see cref="DeltaVlq"/> — <see cref="int"/>, <see cref="long"/></item>
/// <item><see cref="DeltaOfDeltaVlq"/> — <see cref="DateOnly"/></item>
/// <item><see cref="GorillaXor"/> — <see cref="double"/></item>
/// <item><see cref="RawDouble"/> — <see cref="double"/></item>
/// <item><see cref="ScaledIntegerBitPacked"/> — <see cref="decimal"/></item>
/// </list>
/// Applying an encoding to an incompatible field type raises <c>BSSG007</c> at compile time.
/// </remarks>
public enum ColumnEncoding
{
    /// <summary>
    /// Dictionary encoding — distinct values stored once in a header; the column payload
    /// stores small integer IDs. Default for <see cref="string"/> columns; default for
    /// <see cref="decimal"/> columns with distinct-value cardinality ≤ 65,536.
    /// </summary>
    Dictionary = 0,

    /// <summary>
    /// Bitmap encoding with run-length compression. Default for <see cref="bool"/> columns
    /// and for low-cardinality integer columns. See DDIA 2nd ed. Ch. 4 pp. 139-140 for the
    /// bitmap-RLE trade-off vs dictionary encoding at different cardinalities.
    /// </summary>
    BitmapRle = 1,

    /// <summary>
    /// Delta + zigzag-VLQ encoding with RLE for long equal-delta runs. Default for
    /// <see cref="int"/> and <see cref="long"/> columns.
    /// </summary>
    DeltaVlq = 2,

    /// <summary>
    /// Delta-of-delta + zigzag-VLQ encoding. Optimal for regularly-sampled date sequences —
    /// evenly-spaced dates compress to ~1 byte per row because the second-order difference
    /// is zero almost always. Default for <see cref="DateOnly"/> columns.
    /// </summary>
    DeltaOfDeltaVlq = 3,

    /// <summary>
    /// Gorilla XOR encoding for IEEE-754 doubles (Pelkonen et al. 2015 §4.1.2). Default for
    /// <see cref="double"/> columns.
    /// </summary>
    /// <remarks>
    /// <para><b>When the default is correct:</b> price time-series (OHLC), curve yields,
    /// regularly-sampled measurements that move slowly between adjacent rows. Slow-changing
    /// gauge data with frequent exact-value repeats (Pelkonen 2015's target workload for
    /// server metrics) compresses 5–10× over raw; smooth multiplicative walks compress
    /// modestly (~1.2–1.3×).</para>
    /// <para><b>When to override to <see cref="RawDouble"/>:</b> high-entropy numeric fields —
    /// computed residuals, Monte-Carlo draws, hashed values, random samples — where
    /// consecutive rows share few leading/trailing zero bits. Gorilla XOR degrades to
    /// ~8 B/value (equal to raw) plus ~1 B/value framing overhead on high-entropy input, so
    /// raw is strictly faster and smaller there.</para>
    /// <para><b>Benchmark citation:</b> Pelkonen et al. 2015 §4.1.2 reports ~1.37 B/value on
    /// monitoring-style doubles; authors should not assume that ratio on their column without
    /// measuring.</para>
    /// </remarks>
    GorillaXor = 4,

    /// <summary>
    /// Raw IEEE-754 double encoding — 8 bytes per value, no compression. Explicit opt-out from
    /// <see cref="GorillaXor"/> for <see cref="double"/> columns known to be high-entropy
    /// (residuals, Monte-Carlo draws, hashed doubles). See <see cref="GorillaXor"/> remarks
    /// for the decision rubric.
    /// </summary>
    RawDouble = 5,

    /// <summary>
    /// Scaled-integer + bit-packing encoding for <see cref="decimal"/> columns whose
    /// distinct-value cardinality exceeds the dictionary threshold (65,536). Finds the common
    /// fractional scale <c>S</c>, rescales each value to a long via <c>×10^S</c>, shifts by
    /// min, bit-packs the unsigned range.
    /// </summary>
    ScaledIntegerBitPacked = 6,
}
