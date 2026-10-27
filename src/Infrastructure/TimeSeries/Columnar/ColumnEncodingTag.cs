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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Internal per-column encoding tag written into the segment footer. Maps to a codec that
/// reads/writes the column's bytes.
/// </summary>
/// <remarks>
/// Stored as a single byte in the footer. Values are append-only — never reuse a tag for a
/// different codec, as doing so would cause existing segments to decode silently wrong.
/// Unknown tags are rejected at read time with <c>UnsupportedEncodingException</c>
/// (Phase 5).
/// </remarks>
internal enum ColumnEncodingTag : byte
{
    /// <summary>
    /// Each value is written as a contiguous run of raw bytes in the natural little-endian
    /// representation of its field type. No compression. Phase 2 baseline; serves as the
    /// fallback when no other codec applies.
    /// </summary>
    PlainRawBytes = 0,

    /// <summary>
    /// Run-length-encoded bitmap for <see cref="bool"/> (and, in principle, any
    /// two-valued column). Per DDIA 2nd ed Ch. 4 pp 140–141. Phase 3a.
    /// </summary>
    BitmapRle = 1,

    /// <summary>
    /// Dictionary encoding — distinct values stored once, payload stores small integer IDs.
    /// Applied to <see cref="string"/> and to <see cref="decimal"/> when distinct-value
    /// cardinality is below the threshold. Phase 3a.
    /// </summary>
    Dictionary = 2,

    /// <summary>
    /// Delta + zigzag-VLQ encoding for monotone-ish integers (<see cref="int"/>,
    /// <see cref="long"/>) with run-length-encoding fallback for long equal-delta runs.
    /// Phase 3a.
    /// </summary>
    DeltaVlq = 3,

    /// <summary>
    /// Delta-of-delta + zigzag-VLQ encoding for <see cref="DateOnly"/> columns in monotone
    /// date order. Per Pelkonen et al. 2015 §4.1 (Gorilla). Phase 3a.
    /// </summary>
    DeltaOfDeltaVlq = 4,

    /// <summary>
    /// Scaled-integer + bit-packing fallback for <see cref="decimal"/> columns whose
    /// distinct-value cardinality exceeds the dictionary threshold. Determines a common
    /// scale, converts to packed fixed-width integers. Phase 3a.
    /// </summary>
    ScaledIntegerBitPacked = 5,

    /// <summary>
    /// Gorilla XOR encoding for <see cref="double"/> columns — leading/trailing-zero XOR
    /// delta, per Pelkonen et al. 2015 §4.1.2. Fully implemented: the codec is registered in
    /// <c>CodecRegistry</c> with <c>IsImplemented = true</c> and round-trips column data through
    /// <c>GorillaXorCodec</c>.
    /// </summary>
    GorillaXor = 6,
}
