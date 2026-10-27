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
/// Reserves a tag number previously used by a now-deleted property on a
/// <see cref="KeyAttribute"/> or <see cref="StorageSerializableAttribute"/> type. Prevents
/// accidental reassignment. Mirrors Protobuf's <c>reserved</c> field declaration.
/// </summary>
/// <remarks>
/// <para>
/// When a field is removed from an annotated type during schema evolution, mark the retired
/// tag slot on the type with <c>[ReservedTag(N)]</c>. A subsequent <c>[Tag(N)]</c> on a
/// different property of the same type then raises <c>BSSG013</c> (Warning by default). This
/// guards against the specific schema-evolution hazard where two successive authors recycle
/// an old tag number for an unrelated field, silently corrupting data written by the previous
/// schema.
/// </para>
/// <para>
/// Multiple <c>[ReservedTag]</c> attributes may decorate the same type; each reserves one
/// tag number.
/// </para>
/// <para>
/// This attribute is compile-time-only (<c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c>);
/// it vanishes from runtime IL.
/// </para>
/// </remarks>
/// <remarks>Initializes a new <see cref="ReservedTagAttribute"/> reserving the given tag.</remarks>
/// <param name="number">Tag number previously held by a now-deleted property.</param>
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = true)]
public sealed class ReservedTagAttribute(int number) : Attribute
{
    /// <summary>Gets the tag number reserved by this attribute instance.</summary>
    public int Number { get; } = number;
}
