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

// The kinds of encodable types the generators support. Tiers mirror
// Tier ordering mirrors the TypeClassifier dispatch tables so that adding a new tier entry
// here is a single-edit change that propagates to the emitters via TypeClassifier.
internal enum EncodableTypeKind
{
    // Tier 1 — numeric primitives (fixed-width or varint-encodable)
    SByte,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Int128,
    UInt128,
    Half,
    Single,
    Double,

    // Tier 2 — non-numeric primitives
    Bool,
    Char,
    String,

    // Tier 3 — structured BCL primitives (dispatched by fully-qualified name)
    DateOnly,
    TimeOnly,
    TimeSpan,
    DateTimeOffset,
    Guid,
    Decimal,

    // Tier 4 — enums (the underlying numeric kind is carried in EnumUnderlying).
    Enum,

    // Tier 5 — user-annotated recursive types and their collections.
    AnnotatedType,
    PrimitiveCollection,
    AnnotatedCollection,
    EnumCollection,

    // Tier 6 — nullable value types (the inner kind is carried in NullableInner).
    Nullable,

    // The type is not representable by any supported encoding. Emitters skip these; the
    // driving generator raises BSSG001 or BSSG008 depending on the shape of the rejection.
    Unsupported,
}

// Classifier output consumed by every generator emitter. Every field is a value-equatable
// primitive (string / enum / nullable enum) so the record struct participates cleanly in the
// incremental pipeline cache — no ITypeSymbol, Compilation, or SyntaxNode references leak
// past the transform step.
internal readonly record struct EncodableTypeDescriptor(
    EncodableTypeKind Kind,
    string FullyQualifiedTypeName,
    string? AnnotatedTypeName,
    EncodableTypeKind? ElementKind,
    EncodableTypeKind? EnumUnderlying,
    EncodableTypeKind? NullableInner) : IEquatable<EncodableTypeDescriptor>
{
    public static EncodableTypeDescriptor Unsupported(string fqn) =>
        new(EncodableTypeKind.Unsupported, fqn, null, null, null, null);
}
