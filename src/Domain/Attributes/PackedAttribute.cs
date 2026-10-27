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
/// Declares that an <see cref="System.Collections.Generic.IEnumerable{T}"/> property on a
/// <see cref="StorageSerializableAttribute"/> record should be encoded using the packed
/// wire format — a single <c>LengthPrefixed</c> tag followed by the concatenated element
/// bodies, with no per-element tag and no element-count prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default behaviour.</b> In v2.0.0, packed is already the default for
/// <see cref="System.Collections.Generic.IEnumerable{T}"/> properties whose element type is a
/// Tier-1 fixed-width numeric (<see cref="sbyte"/>, <see cref="byte"/>, <see cref="short"/>,
/// <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>,
/// <see cref="ulong"/>, <see cref="Int128"/>, <see cref="UInt128"/>, <see cref="Half"/>,
/// <see cref="float"/>, <see cref="double"/>) or <see cref="bool"/>. Applying
/// <c>[Packed]</c> to a property whose element type is already default-packed is a no-op and
/// serves as explicit-intent documentation. The attribute is required when exotic element
/// types that later join the default-packed set want to opt-in explicitly.
/// </para>
/// <para>
/// <b>Wire shape.</b> Packed writes one tag-and-wire-type varint
/// (<see cref="Helpers.WireType.LengthPrefixed"/>), followed by a varint <c>LEN</c>, followed
/// by <c>LEN</c> bytes of concatenated element payloads. Readers consume the length block and
/// decode elements until the block is exhausted; no count varint is written. DDIA 2e Ch. 5
/// pp. 171 documents this as Protobuf's packed-repeated encoding and cites ~15% wire-size
/// savings on fixed-width collections.
/// </para>
/// <para>
/// <b>Interaction with <see cref="UnpackedAttribute"/>.</b> Applying both <c>[Packed]</c> and
/// <c>[Unpacked]</c> to the same property is rejected by the source generator via
/// <c>BSSG018</c>. Apply at most one.
/// </para>
/// <para>
/// <b>Reader tolerance.</b> The generated <c>Deserialize</c> accepts both packed and unpacked
/// encodings for the same property on a single read by dispatching on the first tag-varint's
/// wire-type bit, so a property can be switched from unpacked to packed (or back) without
/// breaking round-trip for historical bytes written under the prior choice.
/// </para>
/// <para>
/// This attribute is compile-time-only (<c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c>);
/// it vanishes from runtime IL and adds no allocation or reflection cost.
/// </para>
/// </remarks>
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class PackedAttribute : Attribute
{
}
