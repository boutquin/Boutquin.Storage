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

internal static class DiagnosticDescriptors
{
    private const string Category = "Boutquin.Storage.SourceGenerator";

    public static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        id: RuleIdentifiers.UnsupportedPropertyType,
        title: "Unsupported property type",
        messageFormat: "Property '{0}' on type '{1}' has unsupported type '{2}'. Supported: Tier 1 numerics (sbyte/byte/short/ushort/int/uint/long/ulong/Int128/UInt128/Half/float/double), Tier 2 non-numerics (bool/char/string), Tier 3 BCL (DateOnly/TimeOnly/TimeSpan/DateTimeOffset/Guid/decimal), enums, nullable value types, IEnumerable<T> of any of those, and [Key]/[StorageSerializable] types.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator cannot produce serialization code for this property type. See docs/source-generator-type-reference.md for the complete supported-type table and the Tier 1-3 classification.");

    public static readonly DiagnosticDescriptor ExcludedPropertyType = new(
        id: RuleIdentifiers.ExcludedPropertyType,
        title: "Explicitly excluded property type",
        messageFormat: "Property '{0}' on type '{1}' has explicitly excluded type '{2}'; {3}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Certain .NET types (DateTime, nint, nuint) are explicitly excluded from the encodable type set because their wire representation is ambiguous (DateTime kind) or architecture-dependent (native integers). Use DateTimeOffset / DateOnly / int / long instead. See docs/source-generator-type-reference.md for the exclusion list and rationale.");

    public static readonly DiagnosticDescriptor MissingTag = new(
        id: RuleIdentifiers.MissingTag,
        title: "Missing [Tag] on annotated property",
        messageFormat: "Property '{0}' on type '{1}' must declare a [Tag(N)]. Suggested: [property: Tag({2})].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every property on a [Key] or [StorageSerializable] record MUST carry a [Tag(N)] attribute. Tags are the wire-format field identifier for schema evolution and must remain stable across record versions. See docs/wire-format-2.0.md §2 for the tag-and-wire-type encoding specification.");

    public static readonly DiagnosticDescriptor DuplicateTag = new(
        id: RuleIdentifiers.DuplicateTag,
        title: "Duplicate [Tag] within a type",
        messageFormat: "Properties '{0}' and '{1}' on type '{2}' both declare [Tag({3})]. Tag numbers must be unique within a type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Two properties cannot share a tag number — the reader dispatches on the tag varint to identify which field follows, so duplicates are unresolvable. See docs/wire-format-2.0.md §2 for the tag-and-wire-type encoding specification.");

    public static readonly DiagnosticDescriptor TagOutOfRange = new(
        id: RuleIdentifiers.TagOutOfRange,
        title: "[Tag] number out of range",
        messageFormat: "Property '{0}' on type '{1}' has [Tag({2})] which is out of range. Tag numbers must be positive and ≤ 268435455 (2^28 − 1). Tag 0 is reserved for the schema-format version byte.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Tag numbers must be positive and fit in the high 28 bits of the tag-and-wire-type varint (low 3 bits carry the wire type). Tag 0 is reserved by the generator for the implicit schema-format version byte. See docs/wire-format-2.0.md §2 for the full tag-and-wire-type encoding specification.");

    public static readonly DiagnosticDescriptor KeyNestedRecord = new(
        id: RuleIdentifiers.KeyNestedRecord,
        title: "[Key] cannot contain a nested [StorageSerializable] record",
        messageFormat: "Property '{0}' on [Key] type '{1}' is a nested [StorageSerializable] record ('{2}'). Ordered-wire keys must be flat.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Ordered-wire key encoding is flat by construction — byte-lex comparability requires a single contiguous byte stream per field with no intermediate framing. Nested records re-introduce framing that cannot participate in byte-lex LSM comparisons.");

    public static readonly DiagnosticDescriptor KeyCollection = new(
        id: RuleIdentifiers.KeyCollection,
        title: "[Key] cannot contain a collection",
        messageFormat: "Property '{0}' on [Key] type '{1}' is a collection ('{2}'). Ordered-wire keys cannot carry variable-length enumerables.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Collections lack a byte-lex-comparable canonical form — element count and element ordering would need additional framing that defeats LSM byte comparison. Move variable-length data to the [StorageSerializable] value side.");

    public static readonly DiagnosticDescriptor KeyNestedNullable = new(
        id: RuleIdentifiers.KeyNestedNullable,
        title: "[Key] cannot contain a nested-nullable value",
        messageFormat: "Property '{0}' on [Key] type '{1}' declares nullable-of-annotated-record ('{2}'). Only leaf-level nullability on Tier 1-4 primitives is allowed in key position.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Ordered-wire null discriminator is a leaf-level 1-byte prefix. Nullable-of-annotated-record would require nested discriminator + nested framing — not supportable under byte-lex sort.");

    public static readonly DiagnosticDescriptor KeyNullableString = new(
        id: RuleIdentifiers.KeyNullableString,
        title: "[Key] string properties must be non-nullable",
        messageFormat: "Property '{0}' on [Key] type '{1}' is declared 'string?'. Ordered-wire strings have no null discriminator; use a non-nullable string (empty string is a valid key component).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The ordered-wire null discriminator is only defined for nullable value types (Nullable<T>). A string? key property would compile without diagnostics, participate in in-memory ordering, and then throw at flush time when Serialize encounters the null — turning a compile-time shape error into a runtime data-path failure.");

    public static readonly DiagnosticDescriptor ReservedTagCollision = new(
        id: RuleIdentifiers.ReservedTagCollision,
        title: "[Tag] reuses a [ReservedTag] value",
        messageFormat: "Property '{0}' on type '{1}' has [Tag({2})] which collides with a [ReservedTag({2})] declared on the same type. Reserved tags correspond to previously-deleted properties; reusing them is almost certainly a bug.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "When a property is removed during schema evolution, its old tag is typically marked with [ReservedTag] to prevent accidental reassignment. A new property reusing a reserved tag would cause readers written against the new schema to silently misinterpret data written under the previous schema. See docs/wire-format-2.0.md §3 (schema evolution) and docs/source-generator-type-reference.md for the [ReservedTag] API.");

    public static readonly DiagnosticDescriptor TypeMustBePartial = new(
        id: RuleIdentifiers.TypeMustBePartial,
        title: "Type must be partial",
        messageFormat: "Type '{0}' annotated with [Key] or [StorageSerializable] must be declared as 'partial'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The source generator adds members to this type via a partial declaration. The type must be declared partial.");

    public static readonly DiagnosticDescriptor TypeShouldBeRecordStruct = new(
        id: RuleIdentifiers.TypeShouldBeRecordStruct,
        title: "Type should be a record struct",
        messageFormat: "Type '{0}' annotated with [Key] or [StorageSerializable] should be a 'record struct'. Other type kinds are supported but may produce unexpected results: the generator scans only directly-declared members, so inherited properties are NOT included in the wire plan — a non-record-struct with a base-class property silently omits that property from serialization.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator is optimized for record structs. Using class, struct, or record class types is advisory. Note that the generator scans only the type's direct members; inherited properties from a base class are not discovered and will be silently absent from the generated serialization, so prefer a flat record struct that declares every persisted property directly.");

    public static readonly DiagnosticDescriptor DuplicateAssemblyDefaults = new(
        id: RuleIdentifiers.DuplicateAssemblyDefaults,
        title: "Duplicate [StorageDefaults] attribute",
        messageFormat: "Multiple [assembly: StorageDefaults] attributes found. Only one is allowed per assembly.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Only one [assembly: StorageDefaults] attribute is allowed per project.");

    public static readonly DiagnosticDescriptor MutuallyExclusiveAttributes = new(
        id: RuleIdentifiers.MutuallyExclusiveAttributes,
        title: "[Key] and [StorageSerializable] are mutually exclusive",
        messageFormat: "Type '{0}' has both [Key] and [StorageSerializable] attributes. These are mutually exclusive — use [Key] for types that are storage keys, [StorageSerializable] for values.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "[Key] and [StorageSerializable] cannot both be applied to the same type.");

    public static readonly DiagnosticDescriptor InterfaceAlreadyImplemented = new(
        id: RuleIdentifiers.InterfaceAlreadyImplemented,
        title: "Interface already implemented",
        messageFormat: "Type '{0}' already implements '{1}'. The generator will skip generating that interface and its members.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator skips generating members for interfaces that the type already implements.");

    public static readonly DiagnosticDescriptor IncompatibleColumnEncoding = new(
        id: RuleIdentifiers.IncompatibleColumnEncoding,
        title: "Incompatible [ColumnEncoding] override",
        messageFormat: "Property '{0}' on type '{1}' has [ColumnEncoding({2})], which is not compatible with field type '{3}'. Compatible types: {4}.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A [ColumnEncoding] attribute selects a codec that cannot encode the annotated property's CLR type. Remove the attribute to fall back to the type-based default, or change the property type.");

    public static readonly DiagnosticDescriptor MissingDecimalScale = new(
        id: RuleIdentifiers.MissingDecimalScale,
        title: "Missing or out-of-range [DecimalScale] on a [Key] decimal property",
        messageFormat: "Property '{0}' on [Key] type '{1}' must declare [DecimalScale(N)] with 0 <= N <= 28 for byte-lex stability across mantissa-scale combinations. Suggested: [property: DecimalScale(2)].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The ordered-wire decimal encoding requires a canonical scale so that values with different raw scales but the same numeric magnitude produce byte-identical output. Without [DecimalScale], LSM binary search over decimal keys can fail to find existing keys. See docs/wire-format-2.0.md §4 (ordered-wire decimal encoding) for the byte-layout details.");

    public static readonly DiagnosticDescriptor ConflictingPackedUnpacked = new(
        id: RuleIdentifiers.ConflictingPackedUnpacked,
        title: "[Packed] and [Unpacked] cannot both apply to the same property",
        messageFormat: "Property '{0}' on type '{1}' declares both [Packed] and [Unpacked]. Apply at most one — they select mutually exclusive collection encodings.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "[Packed] (one LengthPrefixed block, element bodies concatenated without per-element tags) and [Unpacked] (one tag-per-element occurrence) are semantic opposites; applying both is a user error. The default for Tier-1 numerics and bool is already packed, so in most cases neither attribute is needed. See docs/wire-format-2.0.md §5 (packed and unpacked collections) for the byte-level layout.");

    public static readonly DiagnosticDescriptor ColumnarFieldNotEncodable = new(
        id: RuleIdentifiers.ColumnarFieldNotEncodable,
        title: "[ColumnarStorable] property type has no columnar encoding",
        messageFormat: "Property '{0}' on [ColumnarStorable] type '{1}' has type '{2}', which has no columnar encoding. Supported column types: DateOnly, int, long, decimal, double, float, bool, string, and enums. Change the property type, or make the property get-only if it is derived state.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every settable public property on a [ColumnarStorable] record must map to a columnar codec, because the record is rehydrated from its columns. A property without a column would never be written and would silently read back as default(T) — runtime data loss from a compile-time shape mistake. Get-only properties are excluded automatically: they are derived state the getter recomputes from stored columns.");

    public static readonly DiagnosticDescriptor UnboundedDecodedLengthAllocation = new(
        id: RuleIdentifiers.UnboundedDecodedLengthAllocation,
        title: "Allocation sized from an unbounded decoded length",
        messageFormat: "Allocation of '{0}' is sized from decoded length '{1}', which is not bounded against the remaining payload/stream bytes before this allocation. A forged or corrupt segment can declare a colossal length (CRC is a checksum, not a MAC); bound it first — e.g. 'if ({1} > remaining) throw' or a Validate/Ensure guard, as ValidateColumnExtent / ValidateRawDoubleRange / EnsureWithinRemaining do.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An on-disk length (footer field, file header, length-prefix, varint frame, or a decode API's caller-supplied count) is attacker-controllable on a corrupt or forged segment, because the block/segment CRC is a checksum, not a MAC — a forged payload with a recomputed CRC reaches the decode path. Using such a length as an array length, stackalloc size, ArrayPool.Shared.Rent count, or Array.CreateInstance length WITHOUT first bounding it against the bytes actually remaining on the same path turns a corrupt read into an unbounded allocation (out-of-memory denial of service, or an uncaught OverflowException ahead of the read-bound). Bound the decoded length before allocating: an inline 'if (n > remaining) throw' overflow-safe comparison, or a named Validate*/Ensure*/Check*/Bound* guard that throws CorruptSegmentException / TruncatedSegmentException / InvalidDataException. A constant-column path with no payload-derived ceiling (e.g. a zero-bit-width run) should carry an explicit absolute-cap check, or suppress this rule in-source with a documented justification. This analyzer is the durable fix for the unbounded-allocation defect class that recurred at every decode layer during the v2.0.0 storage-hardening review.");

    public static readonly DiagnosticDescriptor ColumnarStorableMustBeRecord = new(
        id: RuleIdentifiers.ColumnarStorableMustBeRecord,
        title: "[ColumnarStorable] type must be a record",
        messageFormat: "Type '{0}' annotated with [ColumnarStorable] must be a record type (record class or record struct). The columnar code generator emits C# `with` expressions in the generated Rehydrate and ProjectTo methods; these are only valid on record types. Applying [ColumnarStorable] to a plain class or struct would compile in the generator but produce CS8858/CS9035 in the consumer build.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The [ColumnarStorable] attribute may only be applied to record types (record class or record struct). The columnar code generator emits `with` expressions for row rehydration and columnar projection; `with` is a record-only C# feature and causes CS8858 (on a plain class) or CS9035 (on a plain struct) in the consumer build. Use `public partial record` or `public partial record struct` instead.");

    public static readonly DiagnosticDescriptor TypeNotConstructible = new(
        id: RuleIdentifiers.TypeNotConstructible,
        title: "Generated code cannot construct the annotated type",
        messageFormat: "Type '{0}' cannot be constructed by the generated code: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated Deserialize rebuilds the value with a positional constructor call that passes every public instance property, in declaration order. The type must declare a constructor whose leading parameters have exactly those property types in that order, each named like its property ignoring case (any further parameters optional) — a positional record's primary constructor always does; a record with body-declared properties needs one written by hand. The generated ProjectTo of a [StorageSerializable] type rebuilds the value with `with` expressions, which C# allows only on records and structs, so a plain class is not supported. The type is rejected and no code is emitted for it, instead of emitting code that fails to compile (CS1729, CS1503 or CS8858).");
}
