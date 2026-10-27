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
/// Assigns a stable numeric identifier to a property on a <see cref="KeyAttribute"/> or
/// <see cref="StorageSerializableAttribute"/> record. Tags identify fields on the wire; they
/// MUST remain stable across versions for schema evolution to work.
/// </summary>
/// <remarks>
/// <para>
/// Tag numbers are positive (≥ 1), unique per type, and capped at 2²⁸ − 1 to keep the
/// tag-and-wire-type varint short. Once assigned and shipped, a tag number must never be
/// reused for a different property — mark the slot as deleted with
/// <see cref="ReservedTagAttribute"/> on the containing type instead.
/// </para>
/// <para>
/// Tag number <c>0</c> is reserved by the generator for the implicit schema-format version
/// byte. The compiler emits <c>BSSG012</c> on <c>[Tag(0)]</c>.
/// </para>
/// <para>
/// DDIA 2nd ed Ch. 5 (Protobuf / Thrift / Avro family) prescribes this pattern for any data
/// that outlives the process that wrote it. Explicit tags — not declaration order — are the
/// schema-stability anchor.
/// </para>
/// <para>
/// This attribute is compile-time-only (<c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c>);
/// it vanishes from runtime IL and adds no allocation or reflection cost.
/// </para>
/// </remarks>
/// <remarks>Initializes a new <see cref="TagAttribute"/> with the given tag number.</remarks>
/// <param name="number">Positive tag number, unique within the containing type.</param>
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class TagAttribute(int number) : Attribute
{
    /// <summary>Gets the stable tag number assigned to the annotated property.</summary>
    public int Number { get; } = number;
}
