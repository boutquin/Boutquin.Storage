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
namespace Boutquin.Storage.Domain.Attributes;

/// <summary>
/// Opt-in marker that asks the columnar writer to emit a cross-row-group bitmap index for
/// the annotated property (Phase E2). Applies to a property on a type marked with
/// <see cref="ColumnarStorableAttribute"/>. Bitmap indexes pay off only for low-cardinality
/// columns where many rows share each distinct value (regime flags, ticker tags, quality
/// grades); leave them off for high-cardinality columns where a footer-sized distinct-value
/// table would exceed the column's on-disk footprint.
/// </summary>
/// <remarks>
/// <para><b>Theory:</b> DDIA 2e Ch. 4 (Figs 4-8, 4-9) — per-value bitmaps plus
/// cross-column bitmap-AND / bitmap-OR turn point predicates (<c>WHERE col = X</c>) and
/// conjunctive predicates (<c>WHERE col1 = X AND col2 = Y</c>) into footer-scan + bit-vector
/// operations, skipping all row groups whose bitmap for the target value is empty.</para>
/// <para><b>Encoding:</b> each distinct observed value maps to a
/// <c>BitmapRleCodec</c>-encoded presence bitmap spanning the whole segment. Stored in the
/// columnar-segment footer's <c>BitmapIndexes</c> extension section (kind <c>0x02</c>).</para>
/// <para><b>Runtime (not compile-time).</b> Unlike <see cref="ColumnEncodingAttribute"/> and
/// <see cref="DecimalScaleAttribute"/>, this attribute is retained in compiled IL — the
/// columnar writer inspects it via reflection at segment-write time to decide whether to
/// emit a bitmap index for the column. It is therefore <em>not</em> gated on the
/// <c>BOUTQUIN_STORAGE_GENERATOR</c> conditional.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class BitmapIndexAttribute : Attribute
{
    /// <summary>Creates a new bitmap-index marker.</summary>
    public BitmapIndexAttribute()
    {
    }
}
