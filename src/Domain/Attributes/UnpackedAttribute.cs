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
/// <see cref="StorageSerializableAttribute"/> record should be encoded using the unpacked
/// wire format — one tag-and-wire-type varint per element, repeated for every element.
/// </summary>
/// <remarks>
/// <para>
/// <b>When to use.</b> Apply <c>[Unpacked]</c> to opt out of the v2.0.0 default (packed for
/// Tier-1 fixed-width numerics and <see cref="bool"/>). Unpacked produces a larger wire than
/// packed (each element pays a tag-varint overhead) but matches the encoding a non-.NET
/// consumer running Protobuf 2-style <c>repeated</c> fields would expect, and preserves the
/// ability to interleave collection elements with other fields if a future schema requires it.
/// </para>
/// <para>
/// <b>Wire shape.</b> Unpacked writes, for each element, one tag-and-wire-type varint with
/// the element's scalar wire type (<see cref="Helpers.WireType.Varint"/>,
/// <see cref="Helpers.WireType.Fixed16"/>, <see cref="Helpers.WireType.Fixed32"/>,
/// <see cref="Helpers.WireType.Fixed64"/>, or <see cref="Helpers.WireType.Fixed128"/>),
/// followed by the element payload — no outer <c>LengthPrefixed</c> framing, no count. A
/// reader accumulates every occurrence of the same tag into the list, preserving element
/// order. See DDIA 2e Ch. 5 pp. 170.
/// </para>
/// <para>
/// <b>Collection-element kinds this attribute applies to.</b> Only Tier-1 fixed-width
/// numerics (<see cref="sbyte"/>..<see cref="UInt128"/>, <see cref="Half"/>,
/// <see cref="float"/>, <see cref="double"/>) and <see cref="bool"/>. Non-fixed-width
/// elements (<see cref="string"/>, <see cref="System.Guid"/>, <see cref="decimal"/>, Tier-3
/// date/time types, nested <c>[StorageSerializable]</c> records) always use the count-prefixed
/// collection encoding regardless of this attribute, because their per-element wire type is
/// <see cref="Helpers.WireType.LengthPrefixed"/> and element-body framing requires a length.
/// </para>
/// <para>
/// <b>Interaction with <see cref="PackedAttribute"/>.</b> Applying both <c>[Packed]</c> and
/// <c>[Unpacked]</c> to the same property is rejected by the source generator via
/// <c>BSSG018</c>. Apply at most one.
/// </para>
/// <para>
/// This attribute is compile-time-only (<c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c>);
/// it vanishes from runtime IL and adds no allocation or reflection cost.
/// </para>
/// </remarks>
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class UnpackedAttribute : Attribute
{
}
