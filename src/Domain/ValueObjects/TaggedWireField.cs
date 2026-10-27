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
namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// One field in a source-generator tagged-wire schema: the stable <see cref="Tag"/> number, the
/// property <see cref="Name"/>, the declared <see cref="TypeName"/>, and whether the field
/// <see cref="IsOptional"/> (nullable value type, reference type, or a missing-tag-tolerant field).
///
/// <para>Unlike <see cref="SchemaField"/> — which is tag-blind and limited to a seven-value
/// <c>SchemaFieldType</c> enum — a <see cref="TaggedWireField"/> carries the tag identity that the
/// Protobuf-family wire-compatibility rules reason over (a field's identity is its tag, not its
/// declaration order or name). It is the loss-free representation; the projection down to
/// <see cref="SchemaField"/> exists only so a tagged-wire schema can round-trip through the existing
/// schema registry, never for compatibility decisions.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 5 — "Encoding and Evolution": Protobuf/Thrift tag-numbered fields, where the tag is the
/// schema-stability anchor.</para>
/// </summary>
/// <param name="Tag">The stable tag number (≥ 1; tag 0 is reserved for the schema-format version marker).</param>
/// <param name="Name">The property name the tag was assigned to.</param>
/// <param name="TypeName">The field's declared type as a string (e.g. <c>int</c>, <c>string</c>, <c>System.Int64</c>).</param>
/// <param name="IsOptional">Whether the field may be absent on the wire without breaking a reader.</param>
public sealed record TaggedWireField(int Tag, string Name, string TypeName, bool IsOptional)
{
    /// <summary>
    /// The type the tagged wire actually encodes for this field when it differs from
    /// <see cref="TypeName"/>; <see langword="null"/> when <see cref="TypeName"/> already names it.
    /// An enum is written as its underlying integer type, so an enum-bearing field records that
    /// type here — <c>int</c> for an <c>enum : int</c>, <c>uint?</c> for a nullable
    /// <c>enum : uint</c>, <c>System.Collections.Generic.IEnumerable&lt;long&gt;</c> for a collection
    /// of an <c>enum : long</c>. The enum's name alone cannot reveal a change of underlying type,
    /// which moves the field between zig-zag and plain varint or changes its width.
    /// </summary>
    public string? WireTypeName { get; init; }
}
