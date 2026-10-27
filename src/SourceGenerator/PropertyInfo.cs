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
namespace Boutquin.Storage.SourceGenerator;

/// <summary>
/// Collection wire-format encoding selector. Resolved at transform time from the
/// <c>[Packed]</c> / <c>[Unpacked]</c> property attributes plus the element-kind defaults.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default</b> means "no attribute declared". The emitter then picks Packed when the element
/// kind is Tier 1 numeric or <c>bool</c>, and CountPrefixed otherwise. This mirrors the packed-collection default rule
/// that packed is default-on for Tier-1 fixed-width + bool, with fall-through to the
/// older count-prefixed collection encoding for non-fixed-width elements.
/// </para>
/// <para>
/// <b>Packed</b> and <b>Unpacked</b> are explicit-intent selectors. Packed writes one
/// <c>LengthPrefixed</c> block with concatenated element bodies and no count. Unpacked writes
/// one <c>tag+body</c> per element. Both are readable by a reader that dispatches on the
/// first tag-varint's wire-type bit.
/// </para>
/// </remarks>
internal enum CollectionEncoding
{
    /// <summary>No attribute declared — emitter picks Packed for fixed-width-capable element kinds, CountPrefixed otherwise.</summary>
    Default,
    /// <summary><c>[Packed]</c> — one LengthPrefixed block, concatenated bodies.</summary>
    Packed,
    /// <summary><c>[Unpacked]</c> — one tag-and-body pair per element.</summary>
    Unpacked,
}

/// <summary>Shape of a property as the generator driver classifies it.</summary>
internal enum TypeKind
{
    /// <summary>
    /// Scalar property handled via the encodable-type table (Tier 1–4, 6). The specific kind
    /// lives on <see cref="PropertyInfo.Descriptor"/>.
    /// </summary>
    Primitive,
    /// <summary>A type annotated with [Key] or [StorageSerializable].</summary>
    AnnotatedType,
    /// <summary>IEnumerable&lt;T&gt; where T is a supported primitive (Tier 1–4, 6).</summary>
    PrimitiveCollection,
    /// <summary>IEnumerable&lt;T&gt; where T is an annotated type.</summary>
    AnnotatedCollection,
    /// <summary>Type not supported by the generator — driver raised BSSG001 or BSSG008.</summary>
    Unsupported,
}

// Property classification record struct — extracted from the semantic model at transform time
// so the CodeGenerator never needs the semantic model. All fields are plain value types;
// EncodableTypeDescriptor is itself a record struct over string/enum fields, so PropertyInfo
// is value-equatable across compilations — required for incremental pipeline caching.
//
// Tag is 0 when the property has no [Tag] attribute (BSSG010 fires in parallel). Non-zero
// tags are what the tagged-wire emitter (Phase 2) uses to identify fields on the wire.
//
// DecimalScale carries the [DecimalScale(N)] value from the property (Phase A2). Null for
// non-decimal properties and for decimal properties without the attribute (BSSG017 fires
// when the property is a [Key] decimal). The ordered-wire emitter substitutes this value
// into OrderedByteHelpers.WriteDecimal(stream, value, N).
internal readonly record struct PropertyInfo(
    string Name,
    string TypeName,
    TypeKind Kind,
    EncodableTypeDescriptor Descriptor,
    // For AnnotatedType/AnnotatedCollection: the fully qualified type name used in generated
    // code. For PrimitiveCollection: the element CLR type name.
    string? AnnotatedTypeName,
    // Tag == null means "no [Tag] attribute declared" (BSSG010 fires). Tag == 0 is a declared
    // but out-of-range value (BSSG012). Non-null positive values are the wire-format tag.
    int? Tag,
    // DecimalScale == null means "no [DecimalScale] attribute declared". On a [Key] decimal
    // property, this triggers BSSG017; on non-decimal or [StorageSerializable] properties
    // it is ignored.
    int? DecimalScale,
    // Phase F2 — [Packed]/[Unpacked] selector for collection properties. Default means
    // "no attribute declared"; the emitter resolves it per element kind. Ignored for
    // non-collection properties.
    CollectionEncoding CollectionEncoding,
    // AnnotatedType only: true when the child's Serialize writes its own length prefix — a
    // generator-emitted [StorageSerializable] body, whose prefix doubles as the parent's LEN.
    // False for a [Key] child (flat ordered wire) or a hand-implemented ISerializable<T>
    // (no framing promise): the parent then writes the LEN itself so a reader that does not
    // know the tag can skip the field. Ignored for every other kind.
    bool ChildSelfFramed = true,
    // AnnotatedType only: representation-exact default test over stored child properties.
    string? ChildDefaultExpression = null) : IEquatable<PropertyInfo>;
