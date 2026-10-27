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

// Single source of truth for classifying property types across the row, columnar, and
// (in later phases) ordered-key emitters. Adding a new type is a single-edit change here:
// add a member to EncodableTypeKind, add a case in one of the dispatch tables below, then
// add per-emitter cases. Adding a new type is a single-edit change here.
//
// Tier dispatch order:
//   1. Nullable unwrap          →  produces Kind=Nullable with NullableInner populated
//   2. Enum check               →  produces Kind=Enum with EnumUnderlying populated
//   3. SpecialType dispatch     →  Tier 1 numeric + Tier 2 non-numeric primitives
//   4. Named-type dispatch      →  Tier 1 Int128/UInt128/Half + Tier 3 BCL primitives
//   5. Explicit exclusions      →  Unsupported (DateTime / nint / nuint — driver raises BSSG008)
//   6. Fallback                 →  Unsupported (driver raises BSSG001 or skips silently per path)
//
// Annotated types (Tier 5) and collections are resolved one level up — the driver already
// knows whether a property's target is annotated / enumerable and supplies context the
// classifier cannot derive purely from the type symbol (e.g., whether an enumerable element
// is itself annotated). The classifier returns Unsupported for those shapes; the driver
// overrides to AnnotatedType / PrimitiveCollection / AnnotatedCollection / EnumCollection
// based on its own checks.
internal static class TypeClassifier
{
    /// <summary>Classify a type symbol into its encoding descriptor.</summary>
    public static EncodableTypeDescriptor Classify(ITypeSymbol type)
    {
        var fqn = GetFullyQualifiedName(type);

        // 1. Nullable value types — unwrap and recurse so the descriptor carries the inner kind.
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableType)
        {
            var inner = Classify(nullableType.TypeArguments[0]);
            if (inner.Kind == EncodableTypeKind.Unsupported)
            {
                return EncodableTypeDescriptor.Unsupported(fqn);
            }

            return new EncodableTypeDescriptor(
                Kind: EncodableTypeKind.Nullable,
                FullyQualifiedTypeName: fqn,
                AnnotatedTypeName: null,
                ElementKind: null,
                // Carry the inner enum's underlying numeric kind (null for non-enum inners) so the
                // emitters can reconstruct the inner Enum descriptor. Dropping it produced a
                // nullable-enum descriptor the emitters could not serialize ("Enum missing
                // EnumUnderlying"), silently generating zero source for the whole record.
                EnumUnderlying: inner.EnumUnderlying,
                NullableInner: inner.Kind);
        }

        // 2. Enums — dispatch the underlying to the Tier 1 table.
        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            var underlying = TryClassifySpecialType(enumType.EnumUnderlyingType?.SpecialType ?? SpecialType.None);
            if (underlying is null)
            {
                return EncodableTypeDescriptor.Unsupported(fqn);
            }

            return new EncodableTypeDescriptor(
                Kind: EncodableTypeKind.Enum,
                FullyQualifiedTypeName: fqn,
                AnnotatedTypeName: null,
                ElementKind: null,
                EnumUnderlying: underlying,
                NullableInner: null);
        }

        // 3. Explicit exclusions — caller raises BSSG008. Classifier reports Unsupported.
        switch (type.SpecialType)
        {
            case SpecialType.System_DateTime:
            case SpecialType.System_IntPtr when IsNativeIntegerType(type):
            case SpecialType.System_UIntPtr when IsNativeIntegerType(type):
                return EncodableTypeDescriptor.Unsupported(fqn);
        }

        // 4. Tier 1/2 via SpecialType (most common path — covers Int32, Int64, String, etc.).
        var special = TryClassifySpecialType(type.SpecialType);
        if (special is not null)
        {
            return new EncodableTypeDescriptor(
                Kind: special.Value,
                FullyQualifiedTypeName: fqn,
                AnnotatedTypeName: null,
                ElementKind: null,
                EnumUnderlying: null,
                NullableInner: null);
        }

        // 5. Tier 1 extended + Tier 3 via fully-qualified metadata name.
        var namedKind = TryClassifyNamedType(fqn);
        if (namedKind is not null)
        {
            return new EncodableTypeDescriptor(
                Kind: namedKind.Value,
                FullyQualifiedTypeName: fqn,
                AnnotatedTypeName: null,
                ElementKind: null,
                EnumUnderlying: null,
                NullableInner: null);
        }

        // 6. Everything else — driver decides whether this is AnnotatedType / collection / BSSG001.
        return EncodableTypeDescriptor.Unsupported(fqn);
    }

    /// <summary>
    /// True iff the symbol corresponds to a DateTime / nint / nuint that the 2.0.0 design
    /// explicitly excludes. Separate entry point so the generator driver can raise
    /// BSSG008 with a targeted remediation message rather than falling through to BSSG001.
    /// </summary>
    public static bool IsExplicitlyExcluded(ITypeSymbol type, out string remediation)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_DateTime:
                remediation = "use DateTimeOffset for instant timestamps or DateOnly for calendar dates.";
                return true;
            case SpecialType.System_IntPtr when IsNativeIntegerType(type):
                remediation = "use int or long for wire-stable width.";
                return true;
            case SpecialType.System_UIntPtr when IsNativeIntegerType(type):
                remediation = "use uint or ulong for wire-stable width.";
                return true;
            default:
                remediation = string.Empty;
                return false;
        }
    }

    private static EncodableTypeKind? TryClassifySpecialType(SpecialType st) => st switch
    {
        SpecialType.System_SByte => EncodableTypeKind.SByte,
        SpecialType.System_Byte => EncodableTypeKind.Byte,
        SpecialType.System_Int16 => EncodableTypeKind.Int16,
        SpecialType.System_UInt16 => EncodableTypeKind.UInt16,
        SpecialType.System_Int32 => EncodableTypeKind.Int32,
        SpecialType.System_UInt32 => EncodableTypeKind.UInt32,
        SpecialType.System_Int64 => EncodableTypeKind.Int64,
        SpecialType.System_UInt64 => EncodableTypeKind.UInt64,
        SpecialType.System_Single => EncodableTypeKind.Single,
        SpecialType.System_Double => EncodableTypeKind.Double,
        SpecialType.System_Boolean => EncodableTypeKind.Bool,
        SpecialType.System_Char => EncodableTypeKind.Char,
        SpecialType.System_String => EncodableTypeKind.String,
        SpecialType.System_Decimal => EncodableTypeKind.Decimal,
        _ => null,
    };

    private static EncodableTypeKind? TryClassifyNamedType(string fqn) => fqn switch
    {
        "System.Int128" => EncodableTypeKind.Int128,
        "System.UInt128" => EncodableTypeKind.UInt128,
        "System.Half" => EncodableTypeKind.Half,
        "System.DateOnly" => EncodableTypeKind.DateOnly,
        "System.TimeOnly" => EncodableTypeKind.TimeOnly,
        "System.TimeSpan" => EncodableTypeKind.TimeSpan,
        "System.DateTimeOffset" => EncodableTypeKind.DateTimeOffset,
        "System.Guid" => EncodableTypeKind.Guid,
        _ => null,
    };

    // Produces the "global::"-stripped FQN emitters use for string dispatch. Deliberately
    // stable across Compilation instances so two descriptors for the same type compare equal
    // via the incremental pipeline's record-struct cache.
    private static string GetFullyQualifiedName(ITypeSymbol type)
    {
        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return qualified.StartsWith("global::", StringComparison.Ordinal)
            ? qualified["global::".Length..]
            : qualified;
    }

    // SpecialType.System_IntPtr is shared by IntPtr and nint — they differ only in whether
    // Roslyn flagged the symbol as a native integer type. We reject nint/nuint but want to
    // allow IntPtr properties to fall through to the "Unsupported reference/struct" path so
    // BSSG001 (not BSSG008) applies. In practice the generator never sees IntPtr either, but
    // this keeps the dispatch unambiguous.
    private static bool IsNativeIntegerType(ITypeSymbol type) =>
        type is INamedTypeSymbol named && named.IsNativeIntegerType;
}
