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
using System.Diagnostics;

namespace Boutquin.Storage.Domain.Attributes;

/// <summary>
/// Declares the canonical scale (number of digits after the decimal point) used to encode a
/// <see cref="decimal"/> or <see cref="Nullable{Decimal}"/> property on a <see cref="KeyAttribute"/>
/// record. Required on every decimal-typed key property — without it, two values with different
/// scales but the same numeric magnitude can byte-lex in the wrong order versus
/// <see cref="decimal.Compare"/>, silently breaking LSM binary search.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a mandatory scale.</b> The ordered wire encodes the 96-bit mantissa plus an inverted
/// scale byte. Across mantissas with different scales the ordering is not guaranteed to match
/// numeric ordering; DDIA 2e Ch. 5 "Encoding and Evolution" is silent on decimal total ordering,
/// and the Avro decimal logical type has the same gap. Normalising every value to a declared
/// scale (via <see cref="decimal.Round(decimal, int, MidpointRounding)"/>) at encode time
/// collapses the cross-scale case and restores <c>byte-lex == decimal.Compare</c>.
/// </para>
/// <para>
/// <b>Valid range.</b> <see cref="Scale"/> must satisfy <c>0 &lt;= Scale &lt;= 28</c>, matching
/// the maximum scale representable by <see cref="decimal"/>. The source generator raises
/// <c>BSSG017</c> on any <c>[Key]</c>-annotated record-struct with a <see cref="decimal"/> or
/// <see cref="Nullable{Decimal}"/> property lacking this attribute (or declaring an
/// out-of-range scale), and <see cref="Helpers.OrderedByteHelpers"/> enforces the same range
/// at runtime.
/// </para>
/// <para>
/// <b>Scope.</b> Applies only to <see cref="KeyAttribute"/> record-struct properties. On
/// <see cref="StorageSerializableAttribute"/> records, the tagged-wire 16-byte
/// <c>decimal.GetBits</c> encoding preserves scale in the bits themselves; no canonicalisation
/// is needed on the value side.
/// </para>
/// <para>
/// This attribute is compile-time-only (<c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c>);
/// it vanishes from runtime IL and adds no allocation or reflection cost.
/// </para>
/// </remarks>
/// <remarks>Initializes a new <see cref="DecimalScaleAttribute"/> with the given scale.</remarks>
/// <param name="scale">Scale in the inclusive range [0, 28].</param>
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class DecimalScaleAttribute(int scale) : Attribute
{
    /// <summary>Gets the canonical scale (0–28) used to normalise the annotated decimal before encoding.</summary>
    public int Scale { get; } = scale;
}
