; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 2.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
BSSG001 | Boutquin.Storage.SourceGenerator | Error | Unsupported property type
BSSG002 | Boutquin.Storage.SourceGenerator | Error | Type must be partial
BSSG003 | Boutquin.Storage.SourceGenerator | Warning | Type should be a record struct
BSSG004 | Boutquin.Storage.SourceGenerator | Error | Duplicate [StorageDefaults] attribute
BSSG005 | Boutquin.Storage.SourceGenerator | Error | [Key] and [StorageSerializable] are mutually exclusive
BSSG006 | Boutquin.Storage.SourceGenerator | Warning | Interface already implemented
BSSG007 | Boutquin.Storage.SourceGenerator | Error | Incompatible [ColumnEncoding] override
BSSG008 | Boutquin.Storage.SourceGenerator | Error | Explicitly excluded property type (DateTime/nint/nuint remediation — use DateTimeOffset/DateOnly/int/long)
; BSSG009 reserved (slot preserved for future diagnostic — holds the contiguous BSSG010..BSSG013 tag-validation block in place without renumbering)
BSSG010 | Boutquin.Storage.SourceGenerator | Error | Missing [Tag] on annotated property (suggested next unused tag in message)
BSSG011 | Boutquin.Storage.SourceGenerator | Error | Duplicate [Tag] within a type
BSSG012 | Boutquin.Storage.SourceGenerator | Error | [Tag] number out of range (≤0 or >2^28-1; tag 0 reserved for schema-format version)
BSSG013 | Boutquin.Storage.SourceGenerator | Error | [Tag] reuses a [ReservedTag] value on the same type
BSSG014 | Boutquin.Storage.SourceGenerator | Error | [Key] cannot contain a nested [StorageSerializable] record (ordered wire is flat)
BSSG015 | Boutquin.Storage.SourceGenerator | Error | [Key] cannot contain a collection (no byte-lex-comparable canonical form)
BSSG016 | Boutquin.Storage.SourceGenerator | Error | [Key] cannot contain a nested-nullable value (only leaf-level nullability allowed)
BSSG017 | Boutquin.Storage.SourceGenerator | Error | [Key] decimal property missing [DecimalScale(N)] (0 ≤ N ≤ 28) — required for byte-lex stability across mantissa-scale combinations (Phase A2 of v2.0.0 storage hardening)
BSSG018 | Boutquin.Storage.SourceGenerator | Error | [Packed] and [Unpacked] cannot both apply to the same property (Phase F2 of v2.0.0 storage hardening) — apply at most one
BSSG019 | Boutquin.Storage.SourceGenerator | Error | [Key] string properties must be non-nullable (strings have no ordered-wire null discriminator; string? would fail only at flush-time Serialize)
BSSG020 | Boutquin.Storage.SourceGenerator | Error | [ColumnarStorable] settable property type has no columnar encoding (silent omission would mean the column is never written and rehydrates as default(T)); get-only properties are excluded as derived state
BSSG021 | Boutquin.Storage.SourceGenerator | Warning | Allocation sized from an unbounded decoded length (footer/header/length-prefix/varint frame or a decode API's caller-supplied count) with no preceding bound against the remaining payload/stream bytes — a forged segment (CRC is a checksum, not a MAC) can drive an unbounded allocation; bound with an inline `if (n > remaining) throw` or a Validate*/Ensure* guard
BSSG022 | Boutquin.Storage.SourceGenerator | Error | [ColumnarStorable] type must be a record (record class or record struct) — the columnar generator emits `with` expressions in Rehydrate/ProjectTo, which are record-only; applying the attribute to a plain class or struct compiles in the generator but produces CS8858/CS9035 in the consumer build

BSSG023 | Boutquin.Storage.SourceGenerator | Error | Generic annotated types are not supported — the generated partial would declare a separate non-generic type
BSSG024 | Boutquin.Storage.SourceGenerator | Error | Public state outside the generated plan is not serialized — property discovery reads only the annotated type's own public properties, so inherited properties and public instance fields (declared or inherited) would be silently dropped
BSSG025 | Boutquin.Storage.SourceGenerator | Error | Generated code cannot construct the annotated type — it is abstract (Deserialize, Rehydrate), no constructor takes the serialized properties positionally or that constructor leaves required members unset (Deserialize), a non-record class or a get-only property cannot be rebuilt with `with` (ProjectTo), or a [ColumnarStorable] required member is not a column (Rehydrate)
BSSG026 | Boutquin.Storage.SourceGenerator | Error | Containing type of an annotated type is not partial — the generated code re-opens every containing type, so a non-partial one would be declared twice

; Code fixes (informational — the Roslyn release-tracking schema does not define a section
; header for code-fix providers, so this note lives as a comment that the release-tracking
; analyzer ignores):
;
; BSSG010 gains a Roslyn CodeFixProvider (MissingTagCodeFix) that ships as part of
; Boutquin.Storage.SourceGenerator.CodeFixes.dll alongside the generator analyzer DLL under
; analyzers/dotnet/cs/. The code action offers "Add [property: Tag(N)] to '{propertyName}'"
; where N is the lowest unused positive tag on the type, respecting sibling [Tag] and
; [ReservedTag] declarations. Single-document fix only; FixAllProvider is a follow-up.
