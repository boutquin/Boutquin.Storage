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

// Tagged-wire emitter for [StorageSerializable] types. Replaces the positional
// emission path with a Protobuf-family self-describing format: each field writes a tag-and-
// wire-type varint followed by a wire-type-specific payload. Unknown tags skip forward.
//
// Records are self-framed: Serialize prefixes the record payload with its byte length, so
// back-to-back records on a shared stream (BinaryEntrySerializer, LSM block layouts) can
// always find the boundary without external framing.
//
// Nested records piggy-back on the LEN wire type: the parent writes LengthPrefixed at the
// tag header, then calls the child's Serialize (which writes its own length-prefixed
// payload). From the reader's perspective, the LEN's length varint and the child's length
// prefix are the same bytes — the parent skip logic and the child decode both work.
//
// Phase 2 covers [StorageSerializable] only; [Key] types use the Phase 3 OrderedCodeGenerator
// (byte-lex-comparable).
internal static class TaggedWireEmitter
{
    // The schema-format version the Phase 2 generator writes into tag 0. Readers compare
    // this at record entry; any other value (or absent tag 0) triggers
    // UnsupportedFormatVersionException. The check is best-effort for 1.x-written bytes: they can
    // fail the length-prefix or wire-type checks first, and some are accepted outright.
    private const int SchemaFormatVersion = 1;

    private const string Helpers = "global::Boutquin.Storage.Domain.Helpers.VarintHelpers";

    // Range-checked narrowing-conversion guards (see CorruptionGuards). A bare
    // DateOnly.FromDayNumber / new TimeOnly on a forged decoded value leaks a raw BCL exception
    // out of the public Deserialize; these emit the typed InvalidDataException corruption signal.
    private const string Guards = "global::Boutquin.Storage.Domain.Helpers.CorruptionGuards";
    private const string WireType = "global::Boutquin.Storage.Domain.Helpers.WireType";
    private const string FormatException = "global::Boutquin.Storage.Domain.Exceptions.UnsupportedFormatVersionException";
    // Scratch buffer for nested LEN-framed payloads: rented from ArrayPool<byte> via
    // PooledMemoryStream — drives per-record managed allocations on the write hot path to ~zero.
    private const string PooledStream = "global::Boutquin.Storage.Domain.Helpers.PooledMemoryStream";
    // Read-side complement: rents from ArrayPool<byte>, reads exact bytes, exposes as
    // MemoryStream, returns to pool on disposal — mirrors PooledMemoryStream for the read path.
    private const string ReadBuffer = "global::Boutquin.Storage.Domain.Helpers.PooledReadBuffer";
    // The method-level scratch every fixed-width read in a generated Deserialize slices (16 bytes
    // covers the widest body), plus the decimal parts scratch; see AppendDeserialize.
    private const string ReadScratch = "__readBuf";
    private const string ReadScratchParts = "__readBufParts";

    // Fixed reservation width for backpatched LEN and count prefixes on count-prefixed
    // and annotated collection paths. Matches VarintHelpers.MaxVarInt64Bytes; a 10-byte slot
    // accepts any ulong-domain value the LEN or count can take. Wire bytes are padded ULEB128;
    // the reader accepts any valid up-to-10-byte encoding. See docs/wire-format-2.0.md §6.3.
    private const string PaddedVarintWidth = "10";

    // Per-type nesting bound for a recursive schema (Protobuf's default recursion limit). Every
    // nested level is a child Deserialize/Serialize call, so without a bound the bytes decide
    // the stack depth: a ~27 KB forged chain overflows a thread-pool stack, an uncatchable
    // process crash instead of the typed corruption signal.
    private const int MaxNestingDepth = 100;

    // Set by the driver around generating each type: true when that type's schema reaches
    // itself (StorageSourceGenerator.IsOnSerializableCycle), so its codec is emitted bounded.
    // Thread-static because generator drivers may run in parallel for different compilations.
    [ThreadStatic]
    internal static bool s_boundNestingDepth;

    /// <summary>
    /// Emit the full body of <c>void Serialize(Stream stream)</c> for a
    /// <see cref="TypeToGenerate"/> that represents a [StorageSerializable] record.
    /// </summary>
    public static void AppendSerialize(StringBuilder sb, TypeToGenerate type, string indent)
    {
        if (s_boundNestingDepth)
        {
            // The writer refuses the depth the reader refuses, so a value that serializes can
            // always be read back.
            sb.AppendLine($"{indent}[global::System.ThreadStatic]");
            sb.AppendLine($"{indent}private static int __serializeNestingDepth;");
            sb.AppendLine();
            sb.AppendLine($"{indent}public void Serialize(global::System.IO.Stream stream)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    if (__serializeNestingDepth >= {MaxNestingDepth})");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(\"{type.Name} nests itself more than {MaxNestingDepth} levels deep; generated readers refuse that depth, so it is not written.\");");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}    __serializeNestingDepth++;");
            sb.AppendLine($"{indent}    try");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        __SerializeUnbounded(stream);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}    finally");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        __serializeNestingDepth--;");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine();
            sb.AppendLine($"{indent}private void __SerializeUnbounded(global::System.IO.Stream stream)");
        }
        else
        {
            sb.AppendLine($"{indent}public void Serialize(global::System.IO.Stream stream)");
        }

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    using var __payload = new {PooledStream}();");

        // Tag 0 schema-format version. The user-facing WriteTag rejects tag 0 (BSSG012
        // guards it), so we write the combined tag+wire varint directly. (0 << 3) | 0 = 0.
        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64(__payload, 0UL);  // tag 0, wire Varint");
        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64(__payload, {SchemaFormatVersion}UL);  // schema-format version");

        foreach (var prop in type.Properties)
        {
            if (prop.Kind == TypeKind.Unsupported)
            {
                continue;
            }

            var tag = prop.Tag;
            if (tag is null || tag.Value <= 0)
            {
                // BSSG010/BSSG012 already surfaced; skip emission for the invalid property so
                // the generator output still compiles (the record is then effectively
                // Default for that field on round-trip).
                continue;
            }

            AppendWriteField(sb, indent + "    ", prop, tag.Value);
        }

        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64(stream, (ulong)__payload.Length);");
        sb.AppendLine($"{indent}    __payload.WriteTo(stream);");
        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// Emit the full body of <c>static T Deserialize(Stream stream)</c> — reads the length
    /// prefix, validates tag 0 schema-format version, and loops over field tags with an
    /// unknown-tag-skip default.
    /// </summary>
    public static void AppendDeserialize(StringBuilder sb, TypeToGenerate type, string indent)
    {
        if (s_boundNestingDepth)
        {
            sb.AppendLine($"{indent}[global::System.ThreadStatic]");
            sb.AppendLine($"{indent}private static int __deserializeNestingDepth;");
            sb.AppendLine();
            sb.AppendLine($"{indent}public static {IdentifierEscaper.Escape(type.Name)} Deserialize(global::System.IO.Stream stream)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    if (__deserializeNestingDepth >= {MaxNestingDepth})");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        throw new global::System.IO.InvalidDataException(\"{type.Name} nests itself more than {MaxNestingDepth} levels deep — corrupt or hostile input; generated readers refuse deeper nesting.\");");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}    __deserializeNestingDepth++;");
            sb.AppendLine($"{indent}    try");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        return __DeserializeUnbounded(stream);");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}    finally");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        __deserializeNestingDepth--;");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine();
            sb.AppendLine($"{indent}private static {IdentifierEscaper.Escape(type.Name)} __DeserializeUnbounded(global::System.IO.Stream stream)");
        }
        else
        {
            sb.AppendLine($"{indent}public static {IdentifierEscaper.Escape(type.Name)} Deserialize(global::System.IO.Stream stream)");
        }

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __payloadLength = {Helpers}.ReadVarInt32LengthOrCount(stream, \"Payload length\");");
        sb.AppendLine($"{indent}    using var __payloadBufRented = {ReadBuffer}.RentAndRead(stream, __payloadLength);");
        sb.AppendLine($"{indent}    using var __s = __payloadBufRented.AsStream();");

        // Tag 0 schema-format check — any other first tag (or a non-Varint wire type there)
        // means the bytes are not Boutquin.Storage 2.0 tagged wire.
        sb.AppendLine($"{indent}    if (!{Helpers}.TryReadTag(__s, out var __tag, out var __wireType)");
        sb.AppendLine($"{indent}        || __tag != 0 || __wireType != {WireType}.Varint)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        throw new {FormatException}(0u,");
        sb.AppendLine($"{indent}            \"{type.Name} payload is missing the tag-0 schema-format version byte. The bytes are not a Boutquin.Storage tagged-wire frame — typically a corrupt file or a foreign byte stream.\");");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    var __version = {Helpers}.ReadVarUInt64(__s);");
        sb.AppendLine($"{indent}    if (__version != {SchemaFormatVersion}UL)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        throw new {FormatException}(");
        sb.AppendLine($"{indent}            (uint)__version,");
        sb.AppendLine($"{indent}            $\"Boutquin.Storage 2.0.0 cannot read {type.Name} schema-format version {{__version}}; supported: 1.\");");
        sb.AppendLine($"{indent}    }}");

        // Declare defaults for every field.
        foreach (var prop in type.Properties)
        {
            if (prop.Kind == TypeKind.Unsupported)
            {
                continue;
            }

            // Non-nullable string fields default to `string.Empty` when their tag is
            // absent. DDIA p. 171 prescribes `""` for missing strings; previously the emitter
            // initialised to `default!` (i.e., `null` cast to `string`), which NRE'd a forward-
            // compatible reader walking a record whose writer omitted the tag. The detection
            // looks at the descriptor kind (String) and the property's nullability annotation
            // (no trailing `?` on TypeName means non-nullable).
            //
            // `default!` rather than `default` for the other reference-defaultable cases
            // suppresses CS8600/CS8604 — the Phase 2 reader uses default when a tag is absent,
            // and the tagged-wire contract prescribes `null` for nullable references / empty collection / etc.
            // Constructing via `default!` defers that choice to the property's declared
            // nullability and keeps the generated code warning-clean under nullable-enabled.
            var isNonNullableString = prop.Kind == TypeKind.Primitive
                && prop.Descriptor.Kind == EncodableTypeKind.String
                && !prop.TypeName.EndsWith("?", StringComparison.Ordinal);
            var defaultExpr = prop.Kind switch
            {
                TypeKind.PrimitiveCollection or TypeKind.AnnotatedCollection =>
                    $"new global::System.Collections.Generic.List<{prop.AnnotatedTypeName}>()",
                _ when isNonNullableString => "string.Empty",
                _ => "default!",
            };
            var fieldType = prop.Kind switch
            {
                TypeKind.PrimitiveCollection or TypeKind.AnnotatedCollection =>
                    $"global::System.Collections.Generic.IEnumerable<{prop.AnnotatedTypeName}>",
                _ => prop.TypeName,
            };
            sb.AppendLine($"{indent}    {fieldType} _{prop.Name} = {defaultExpr};");
        }

        // Every field read runs inside the tag loop (once per occurrence of its tag) and every
        // element read inside an element loop as well, while stackalloc memory is released only
        // when the method returns: a buffer allocated per read grows the stack once per element or
        // repeated tag, so a 200 000-Guid record or forged bytes repeating one tag overflow it — an
        // uncatchable crash. The fixed-width reads therefore slice one scratch allocated here,
        // before the loop. The loop is emitted first so the scratch is declared exactly when a read
        // in it uses the scratch.
        var readLoop = new StringBuilder();
        readLoop.AppendLine($"{indent}    while ({Helpers}.TryReadTag(__s, out __tag, out __wireType))");
        readLoop.AppendLine($"{indent}    {{");
        readLoop.AppendLine($"{indent}        switch (__tag)");
        readLoop.AppendLine($"{indent}        {{");
        foreach (var prop in type.Properties)
        {
            if (prop.Kind == TypeKind.Unsupported || prop.Tag is null || prop.Tag.Value <= 0)
            {
                continue;
            }
            AppendReadCase(readLoop, indent + "            ", prop, prop.Tag.Value);
        }
        readLoop.AppendLine($"{indent}            default:");
        readLoop.AppendLine($"{indent}                {Helpers}.SkipField(__s, __wireType);");
        readLoop.AppendLine($"{indent}                break;");
        readLoop.AppendLine($"{indent}        }}");
        readLoop.AppendLine($"{indent}    }}");

        var readLoopText = readLoop.ToString();
        if (readLoopText.IndexOf(ReadScratch + ".Slice(", StringComparison.Ordinal) >= 0)
        {
            sb.AppendLine($"{indent}    global::System.Span<byte> {ReadScratch} = stackalloc byte[16];");
        }

        if (readLoopText.IndexOf(ReadScratchParts, StringComparison.Ordinal) >= 0)
        {
            sb.AppendLine($"{indent}    global::System.Span<int> {ReadScratchParts} = stackalloc int[4];");
        }

        sb.Append(readLoopText);

        // Build the record from the constructor. Properties in declaration order; skipped
        // (Unsupported) properties fall back to `default` so the record still constructs.
        var ctorArgs = new List<string>();
        foreach (var prop in type.Properties)
        {
            if (prop.Kind == TypeKind.Unsupported || prop.Tag is null || prop.Tag.Value <= 0)
            {
                ctorArgs.Add("default");
            }
            else
            {
                ctorArgs.Add($"_{prop.Name}");
            }
        }
        sb.AppendLine($"{indent}    return new {IdentifierEscaper.Escape(type.Name)}({string.Join(", ", ctorArgs)});");
        sb.AppendLine($"{indent}}}");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Per-property write emission. Picks the wire type based on descriptor kind, writes the
    // tag+wiretype varint, then the payload.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static void AppendWriteField(StringBuilder sb, string indent, PropertyInfo prop, int tag)
    {
        // Member-access position — escape keyword-named properties (`this.@class`). The bare
        // prop.Name still flows as the propertyName argument (string-literal messages / `_name`
        // locals, which never need escaping).
        var member = IdentifierEscaper.Escape(prop.Name);
        switch (prop.Kind)
        {
            case TypeKind.Primitive:
                // A nullable-annotated string (`string?`) writes nothing when null: absent tag ⇒
                // null is the reader's default for the field, the same contract as `T?` and
                // `Child?`. Throwing here instead would make a value the generated reader itself
                // produces from older bytes unwritable.
                if (prop.Descriptor.Kind == EncodableTypeKind.String
                    && prop.TypeName.EndsWith("?", StringComparison.Ordinal))
                {
                    sb.AppendLine($"{indent}if (this.{member} is not null)");
                    sb.AppendLine($"{indent}{{");
                    AppendWritePrimitive(sb, indent + "    ", prop.Descriptor, $"this.{member}", prop.Name, tag);
                    sb.AppendLine($"{indent}}}");
                }
                else
                {
                    AppendWritePrimitive(sb, indent, prop.Descriptor, $"this.{member}", prop.Name, tag);
                }
                break;
            case TypeKind.AnnotatedType:
                // A nullable-annotated nested record (`Child?`) writes nothing when null: absent
                // tag ⇒ null, which is the reader's default for the field.
                if (prop.TypeName.EndsWith("?", StringComparison.Ordinal))
                {
                    sb.AppendLine($"{indent}if (this.{member} is not null)");
                    sb.AppendLine($"{indent}{{");
                    AppendWriteNestedChild(sb, indent + "    ", prop, member, tag);
                    sb.AppendLine($"{indent}}}");
                }
                else
                {
                    // A non-nullable nested record has no null path of its own, yet the reader
                    // gives it `default` (null for a record class, the zero-initialised value for a
                    // record struct) when the tag is absent (docs/wire-format-2.0.md §8). That value
                    // is unwritable as a child (its own non-nullable members are null), so the
                    // writer closes the rule: a `default` child writes an absent tag, and bytes from
                    // an older schema read and write back byte-identically.
                    sb.AppendLine($"{indent}if (!({prop.ChildDefaultExpression}))");
                    sb.AppendLine($"{indent}{{");
                    AppendWriteNestedChild(sb, indent + "    ", prop, member, tag);
                    sb.AppendLine($"{indent}}}");
                }
                break;
            case TypeKind.PrimitiveCollection:
                AppendWritePrimitiveCollection(sb, indent, prop, tag);
                break;
            case TypeKind.AnnotatedCollection:
                AppendWriteAnnotatedCollection(sb, indent, prop, tag);
                break;
        }
    }

    // A self-framed child's own length prefix is the field's LEN (docs/wire-format-2.0.md §7).
    // Any other child is framed here with the padded backpatch-LEN slot the collection paths use
    // (docs/wire-format-2.0.md §6.3), so a reader that does not know the tag can still skip it.
    private static void AppendWriteNestedChild(StringBuilder sb, string indent, PropertyInfo prop, string member, int tag)
    {
        sb.AppendLine($"{indent}{Helpers}.WriteTag(__payload, {tag}, {WireType}.LengthPrefixed);");
        if (prop.ChildSelfFramed)
        {
            sb.AppendLine($"{indent}this.{member}.Serialize(__payload);");
            return;
        }

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __lenOffset = __payload.Reserve({PaddedVarintWidth});");
        sb.AppendLine($"{indent}    var __childStart = checked((int)__payload.Length);");
        sb.AppendLine($"{indent}    this.{member}.Serialize(__payload);");
        sb.AppendLine($"{indent}    var __childEnd = checked((int)__payload.Length);");
        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64Padded(__payload.GetBackpatchSpan(__lenOffset, {PaddedVarintWidth}), (ulong)(__childEnd - __childStart), {PaddedVarintWidth});");
        sb.AppendLine($"{indent}}}");
    }

    private static void AppendWritePrimitive(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string valueExpr,
        string propertyName,
        int tag)
    {
        switch (descriptor.Kind)
        {
            case EncodableTypeKind.Nullable:
                {
                    // Absent tag == null. Emit tag+payload only when HasValue.
                    sb.AppendLine($"{indent}if ({valueExpr}.HasValue)");
                    sb.AppendLine($"{indent}{{");
                    var innerKind = descriptor.NullableInner
                        ?? throw new InvalidOperationException("Nullable missing NullableInner.");
                    var innerFqn = descriptor.FullyQualifiedTypeName.EndsWith('?') ? descriptor.FullyQualifiedTypeName[..^1] : descriptor.FullyQualifiedTypeName;
                    var innerDescriptor = new EncodableTypeDescriptor(
                        innerKind, innerFqn, null, null, descriptor.EnumUnderlying, null);
                    AppendWritePrimitive(sb, indent + "    ", innerDescriptor, $"{valueExpr}.Value", propertyName, tag);
                    sb.AppendLine($"{indent}}}");
                    return;
                }

            case EncodableTypeKind.Enum:
                {
                    var underlyingKind = descriptor.EnumUnderlying
                        ?? throw new InvalidOperationException("Enum missing EnumUnderlying.");
                    var underlyingCSharp = EnumUnderlyingCSharpName(underlyingKind);
                    var underlyingDescriptor = new EncodableTypeDescriptor(
                        underlyingKind, underlyingCSharp, null, null, null, null);
                    AppendWritePrimitive(sb, indent, underlyingDescriptor, $"({underlyingCSharp}){valueExpr}", propertyName, tag);
                    return;
                }
        }

        var wireType = WireTypeFor(descriptor.Kind);
        sb.AppendLine($"{indent}{Helpers}.WriteTag(__payload, {tag}, {WireType}.{wireType});");
        AppendScalarWrite(sb, indent, descriptor, valueExpr, "__payload", propertyName);
    }

    private static void AppendWritePrimitiveCollection(StringBuilder sb, string indent, PropertyInfo prop, int tag)
    {
        var elementKind = prop.Descriptor.ElementKind
            ?? throw new InvalidOperationException("Primitive collection missing ElementKind.");
        var elementDescriptor = new EncodableTypeDescriptor(
            elementKind,
            prop.AnnotatedTypeName ?? string.Empty,
            null,
            null,
            prop.Descriptor.EnumUnderlying,
            prop.Descriptor.NullableInner);

        // Null guard before any wire bytes land: a null collection would otherwise NRE from
        // deep inside the foreach — the same user error a null string reports with a guarded
        // InvalidOperationException naming the property. IEnumerable<T> is a reference type,
        // so the check is always emittable.
        sb.AppendLine($"{indent}if (this.{IdentifierEscaper.Escape(prop.Name)} is null) {{ throw new global::System.InvalidOperationException(\"Property '{prop.Name}' cannot be null during serialization.\"); }}");

        // Phase F2 — default-packed for Tier-1 numerics and bool. Non-packable element kinds
        // (Char/String/Guid/Decimal/Enum/Tier-3 date-time) stay on the count-prefixed encoding
        // regardless of [Packed]/[Unpacked], because their ordering / framing semantics make a
        // count-less block ambiguous to readers written against a strict Protobuf-style spec.
        var resolved = ResolveCollectionEncoding(prop.CollectionEncoding, elementKind);
        switch (resolved)
        {
            case ResolvedCollectionEncoding.Packed:
                AppendWritePackedCollection(sb, indent, prop, tag, elementDescriptor);
                return;
            case ResolvedCollectionEncoding.Unpacked:
                AppendWriteUnpackedCollection(sb, indent, prop, tag, elementDescriptor);
                return;
            case ResolvedCollectionEncoding.CountPrefixed:
            default:
                AppendWriteCountPrefixedCollection(sb, indent, prop, tag, elementDescriptor);
                return;
        }
    }

    // Packed wire (F2 default for Tier-1 + bool): one tag-varint with LengthPrefixed, followed
    // by a varint LEN, followed by the concatenated element bodies — no per-element tag, no
    // count varint. DDIA 2e Ch. 5 p. 171 describes the unpacked form (Fig 5-3 shows repeated
    // per-element tags); the packed default matches proto3's packed repeated scalars.
    private static void AppendWritePackedCollection(
        StringBuilder sb,
        string indent,
        PropertyInfo prop,
        int tag,
        EncodableTypeDescriptor elementDescriptor)
    {
        sb.AppendLine($"{indent}{Helpers}.WriteTag(__payload, {tag}, {WireType}.LengthPrefixed);");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    using var __collBuf = new {PooledStream}();");
        var scratch = AppendElementScratch(sb, indent + "    ", elementDescriptor);
        sb.AppendLine($"{indent}    foreach (var __item in this.{IdentifierEscaper.Escape(prop.Name)})");
        sb.AppendLine($"{indent}    {{");
        AppendWriteCollectionElement(sb, indent + "        ", elementDescriptor, "__item", prop.Name, scratch: scratch);
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64(__payload, (ulong)__collBuf.Length);");
        sb.AppendLine($"{indent}    __collBuf.WriteTo(__payload);");
        sb.AppendLine($"{indent}}}");
    }

    // Unpacked wire (F2 opt-out via [Unpacked]): one tag-varint+body per element, repeated.
    // The reader accumulates every occurrence of the same tag into the target list.
    private static void AppendWriteUnpackedCollection(
        StringBuilder sb,
        string indent,
        PropertyInfo prop,
        int tag,
        EncodableTypeDescriptor elementDescriptor)
    {
        var elementWireType = WireTypeFor(elementDescriptor.Kind);
        // The element scratch, when the kind needs one, is scoped to a block of its own so two
        // unpacked collection fields never declare it twice in the same scope.
        var scoped = NeedsElementScratch(elementDescriptor);
        var loopIndent = scoped ? indent + "    " : indent;
        if (scoped)
        {
            sb.AppendLine($"{indent}{{");
        }

        var scratch = AppendElementScratch(sb, loopIndent, elementDescriptor);
        sb.AppendLine($"{loopIndent}foreach (var __item in this.{IdentifierEscaper.Escape(prop.Name)})");
        sb.AppendLine($"{loopIndent}{{");
        sb.AppendLine($"{loopIndent}    {Helpers}.WriteTag(__payload, {tag}, {WireType}.{elementWireType});");
        AppendWriteUnpackedElementBody(sb, loopIndent + "    ", elementDescriptor, "__item", prop.Name, scratch);
        sb.AppendLine($"{loopIndent}}}");
        if (scoped)
        {
            sb.AppendLine($"{indent}}}");
        }
    }

    // Count-prefixed wire (kept for non-packable element kinds: Char/String/Guid/Decimal/
    // Enum/Tier-3 date-time types and any collection element whose body is LengthPrefixed or
    // needs self-framing). Layout: tag+LengthPrefixed, LEN, count varint, concatenated
    // element bodies.
    //
    // Backpatch-LEN trick. Reserves a fixed-width 10-byte slot on the parent
    // __payload for each of the LEN and count prefixes, streams element bodies directly into
    // __payload, then backpatches the two prefixes once the body length and element count
    // are known. Eliminates the PooledMemoryStream rentals the earlier emitter required to
    // buffer element bodies before writing the LEN/count prefixes. Wire bytes grow by up to 18 per
    // count-prefixed collection field versus a canonical encoding (the LEN and count prefixes
    // are now padded ULEB128); the reader at VarintHelpers.ReadVarUInt64 accepts any valid
    // up-to-10-byte ULEB128, so over-padded encodings round-trip correctly. Documented in
    // docs/wire-format-2.0.md §6.3.
    private static void AppendWriteCountPrefixedCollection(
        StringBuilder sb,
        string indent,
        PropertyInfo prop,
        int tag,
        EncodableTypeDescriptor elementDescriptor)
    {
        sb.AppendLine($"{indent}{Helpers}.WriteTag(__payload, {tag}, {WireType}.LengthPrefixed);");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __lenOffset = __payload.Reserve({PaddedVarintWidth});");
        sb.AppendLine($"{indent}    var __blockStart = checked((int)__payload.Length);");
        sb.AppendLine($"{indent}    var __countOffset = __payload.Reserve({PaddedVarintWidth});");
        sb.AppendLine($"{indent}    var __elemCount = 0UL;");
        var scratch = AppendElementScratch(sb, indent + "    ", elementDescriptor);
        sb.AppendLine($"{indent}    foreach (var __item in this.{IdentifierEscaper.Escape(prop.Name)})");
        sb.AppendLine($"{indent}    {{");
        AppendWriteCollectionElement(sb, indent + "        ", elementDescriptor, "__item", prop.Name, "__payload", scratch);
        sb.AppendLine($"{indent}        __elemCount++;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    var __blockEnd = checked((int)__payload.Length);");
        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64Padded(__payload.GetBackpatchSpan(__countOffset, {PaddedVarintWidth}), __elemCount, {PaddedVarintWidth});");
        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64Padded(__payload.GetBackpatchSpan(__lenOffset, {PaddedVarintWidth}), (ulong)(__blockEnd - __blockStart), {PaddedVarintWidth});");
        sb.AppendLine($"{indent}}}");
    }

    // Unpacked-body writer: the per-element tag header was already written by the caller.
    // The target stream is __payload (no nested __collBuf) because unpacked elements are
    // interleaved directly with other fields. Element-kind dispatch mirrors the top-level
    // scalar writer — identical byte shape per element.
    private static void AppendWriteUnpackedElementBody(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string valueExpr,
        string propertyName,
        string? scratch)
    {
        if (!IsPackableElement(descriptor.Kind))
        {
            throw new InvalidOperationException(
                $"Unpacked collection emission is only defined for Tier-1 numerics and bool; got '{descriptor.Kind}' on '{propertyName}'. " +
                "Non-packable element kinds fall through to count-prefixed encoding via ResolveCollectionEncoding.");
        }

        AppendScalarWrite(sb, indent, descriptor, valueExpr, "__payload", propertyName, scratch);
    }

    // Classifier: given the property-level attribute choice and the element kind, resolve
    // which wire shape the emitter should produce. This is the one place F2's default-on
    // rule for Tier-1 + bool lives — everything else in the emitter is a straight dispatch.
    private enum ResolvedCollectionEncoding
    {
        Packed,
        Unpacked,
        CountPrefixed,
    }

    private static ResolvedCollectionEncoding ResolveCollectionEncoding(
        CollectionEncoding userChoice,
        EncodableTypeKind elementKind)
    {
        var packable = IsPackableElement(elementKind);
        if (!packable)
        {
            // Non-packable elements always use the count-prefixed encoding, regardless of
            // user attribute choice. [Packed]/[Unpacked] on such a property is a no-op; this
            // matches the Protobuf precedent where packed does not apply to strings/bytes/
            // submessages.
            return ResolvedCollectionEncoding.CountPrefixed;
        }

        return userChoice switch
        {
            CollectionEncoding.Unpacked => ResolvedCollectionEncoding.Unpacked,
            CollectionEncoding.Packed => ResolvedCollectionEncoding.Packed,
            CollectionEncoding.Default => ResolvedCollectionEncoding.Packed,
            _ => ResolvedCollectionEncoding.Packed,
        };
    }

    // Default-packed set: Tier-1 numerics (fixed-width integers and floating-point) + bool.
    // Deliberately excludes fixed-width types that are packable in principle but outside the
    // v2.0.0 GA scope (post-GA extension candidates in parentheses):
    //   Char      — 2-byte fixed (extend post-GA when AppendReadCollectionElement supports it)
    //   DateOnly  — 4-byte fixed (same; currently routes through count-prefixed path)
    //   TimeOnly  — 8-byte fixed (same; currently routes through count-prefixed path)
    //   TimeSpan  — 8-byte fixed (same)
    //   Guid      — 16-byte fixed (Fixed128 wire type; extend post-GA)
    //   DateTimeOffset — variable-length (nested LEN block; not packable without re-encoding)
    //   Decimal   — variable-length without [DecimalScale]; not packable in general
    //   Enum      — packable if underlying is numeric; extend post-GA
    // String, nested records, and Nullable are inherently variable-length and remain unpacked.
    private static bool IsPackableElement(EncodableTypeKind kind) => kind is
        EncodableTypeKind.SByte or EncodableTypeKind.Byte or
        EncodableTypeKind.Int16 or EncodableTypeKind.UInt16 or
        EncodableTypeKind.Int32 or EncodableTypeKind.UInt32 or
        EncodableTypeKind.Int64 or EncodableTypeKind.UInt64 or
        EncodableTypeKind.Int128 or EncodableTypeKind.UInt128 or
        EncodableTypeKind.Half or EncodableTypeKind.Single or EncodableTypeKind.Double or
        EncodableTypeKind.Bool;

    // Annotated count-prefixed collection — IEnumerable<T> where T is itself a [Key] or
    // [StorageSerializable] record. Same wire shape as AppendWriteCountPrefixedCollection
    // (tag+LengthPrefixed, LEN, count, body bytes); each element body is the scalar field's body
    // (docs/wire-format-2.0.md §6.3, §7.1): a self-framed child's own Serialize output, or any
    // other child behind its own padded LEN slot. Without that per-element frame a child that
    // makes no framing promise reads its neighbours' bytes as its own, and a zero-byte child
    // would break the reader's at-least-one-byte-per-element count bound. Uses the
    // backpatch-LEN trick on the same basis — see AppendWriteCountPrefixedCollection's banner comment.
    private static void AppendWriteAnnotatedCollection(StringBuilder sb, string indent, PropertyInfo prop, int tag)
    {
        // Same null guard as the primitive-collection dispatcher above.
        sb.AppendLine($"{indent}if (this.{IdentifierEscaper.Escape(prop.Name)} is null) {{ throw new global::System.InvalidOperationException(\"Property '{prop.Name}' cannot be null during serialization.\"); }}");
        sb.AppendLine($"{indent}{Helpers}.WriteTag(__payload, {tag}, {WireType}.LengthPrefixed);");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __lenOffset = __payload.Reserve({PaddedVarintWidth});");
        sb.AppendLine($"{indent}    var __blockStart = checked((int)__payload.Length);");
        sb.AppendLine($"{indent}    var __countOffset = __payload.Reserve({PaddedVarintWidth});");
        sb.AppendLine($"{indent}    var __elemCount = 0UL;");
        sb.AppendLine($"{indent}    foreach (var __item in this.{IdentifierEscaper.Escape(prop.Name)})");
        sb.AppendLine($"{indent}    {{");
        if (prop.ChildSelfFramed)
        {
            sb.AppendLine($"{indent}        __item.Serialize(__payload);");
        }
        else
        {
            sb.AppendLine($"{indent}        var __elemLenOffset = __payload.Reserve({PaddedVarintWidth});");
            sb.AppendLine($"{indent}        var __elemStart = checked((int)__payload.Length);");
            sb.AppendLine($"{indent}        __item.Serialize(__payload);");
            sb.AppendLine($"{indent}        {Helpers}.WriteVarUInt64Padded(__payload.GetBackpatchSpan(__elemLenOffset, {PaddedVarintWidth}), (ulong)(checked((int)__payload.Length) - __elemStart), {PaddedVarintWidth});");
        }

        sb.AppendLine($"{indent}        __elemCount++;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    var __blockEnd = checked((int)__payload.Length);");
        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64Padded(__payload.GetBackpatchSpan(__countOffset, {PaddedVarintWidth}), __elemCount, {PaddedVarintWidth});");
        sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64Padded(__payload.GetBackpatchSpan(__lenOffset, {PaddedVarintWidth}), (ulong)(__blockEnd - __blockStart), {PaddedVarintWidth});");
        sb.AppendLine($"{indent}}}");
    }

    // Collection elements skip the per-element tag header — the enclosing LEN already scopes
    // the block. They emit only the payload bytes their kind requires, into the target stream
    // (default: __collBuf for packed; count-prefixed callers pass __payload directly via the backpatch path).
    private static void AppendWriteCollectionElement(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string valueExpr,
        string propertyName,
        string streamVar = "__collBuf",
        string? scratch = null)
    {
        if (descriptor.Kind == EncodableTypeKind.Enum)
        {
            var underlyingKind = descriptor.EnumUnderlying
                ?? throw new InvalidOperationException("Enum missing underlying.");
            var underlyingCSharp = EnumUnderlyingCSharpName(underlyingKind);
            var underlyingDescriptor = new EncodableTypeDescriptor(
                underlyingKind, underlyingCSharp, null, null, null, null);
            AppendWriteCollectionElement(sb, indent, underlyingDescriptor, $"({underlyingCSharp}){valueExpr}", propertyName, streamVar, scratch);
            return;
        }

        // Nullable in collection position is not supported — a nullable element type is
        // rejected upstream by the driver (BSSG001 UnsupportedPropertyType; BSSG016 covers
        // the separate [Key] nested-nullable shape).
        if (descriptor.Kind is EncodableTypeKind.Nullable)
        {
            throw new InvalidOperationException(
                $"Nullable collection elements are rejected at transform time (BSSG001); " +
                "reaching this path is a generator bug.");
        }

        AppendScalarWrite(sb, indent, descriptor, valueExpr, streamVar, propertyName, scratch);
    }

    // stackalloc memory is released only when the method returns, so a scratch buffer allocated
    // inside a per-element loop grows the stack once per element: a 100 000-element collection
    // of doubles or Guids overflows a thread-pool stack, an uncatchable process crash. A collection
    // writer therefore allocates its element scratch once, before the loop, and every element
    // reuses it. Returns the scratch name, or null when the element kind writes without one.
    private static string? AppendElementScratch(StringBuilder sb, string indent, EncodableTypeDescriptor elementDescriptor)
    {
        if (!NeedsElementScratch(elementDescriptor))
        {
            return null;
        }

        sb.AppendLine($"{indent}global::System.Span<byte> __elemBuf = stackalloc byte[16];");
        if (elementDescriptor.Kind == EncodableTypeKind.Decimal)
        {
            sb.AppendLine($"{indent}global::System.Span<int> __elemBufParts = stackalloc int[4];");
        }

        return "__elemBuf";
    }

    // The fixed-width kinds AppendScalarWrite formats through a scratch buffer (16 bytes covers
    // the widest). Enum elements write their integral underlying value, which needs none.
    private static bool NeedsElementScratch(EncodableTypeDescriptor elementDescriptor) => elementDescriptor.Kind is
        EncodableTypeKind.Half or EncodableTypeKind.Single or EncodableTypeKind.Double or
        EncodableTypeKind.Int128 or EncodableTypeKind.UInt128 or
        EncodableTypeKind.Guid or EncodableTypeKind.Decimal;

    // Single write-body helper shared by AppendWritePrimitive, AppendWriteUnpackedElementBody,
    // and AppendWriteCollectionElement. Emits the wire-type-specific payload bytes for
    // descriptor.Kind into streamVar. The caller is responsible for any tag write that
    // precedes the payload (AppendWritePrimitive does this; element-body helpers do not).
    private static void AppendScalarWrite(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string valueExpr,
        string streamVar,
        string propertyName,
        string? scratch = null)
    {
        // A call inside a per-element loop passes the scratch span its caller allocated once,
        // before the loop; a top-level field write runs once per Serialize and allocates its own.
        string ByteBuffer(int width) => scratch is null ? $"stackalloc byte[{width}]" : $"{scratch}.Slice(0, {width})";
        var parts = scratch is null ? "stackalloc int[4]" : scratch + "Parts";
        switch (descriptor.Kind)
        {
            case EncodableTypeKind.SByte:
            case EncodableTypeKind.Int16:
            case EncodableTypeKind.Int32:
            case EncodableTypeKind.Int64:
                sb.AppendLine($"{indent}{Helpers}.WriteZigZagInt64({streamVar}, (long){valueExpr});");
                break;
            case EncodableTypeKind.Byte:
            case EncodableTypeKind.UInt16:
            case EncodableTypeKind.UInt32:
            case EncodableTypeKind.UInt64:
                sb.AppendLine($"{indent}{Helpers}.WriteVarUInt64({streamVar}, (ulong){valueExpr});");
                break;
            case EncodableTypeKind.Bool:
                sb.AppendLine($"{indent}{Helpers}.WriteVarUInt64({streamVar}, {valueExpr} ? 1UL : 0UL);");
                break;
            case EncodableTypeKind.Char:
                sb.AppendLine($"{indent}{Helpers}.WriteVarUInt64({streamVar}, (ulong)(ushort){valueExpr});");
                break;
            case EncodableTypeKind.String:
                sb.AppendLine($"{indent}{Helpers}.WriteString({streamVar}, {valueExpr} ?? throw new global::System.InvalidOperationException(\"Property '{propertyName}' cannot be null during serialization.\"));");
                break;
            case EncodableTypeKind.Half:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ByteBuffer(2)};");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(__buf, global::System.BitConverter.HalfToUInt16Bits({valueExpr}));");
                sb.AppendLine($"{indent}    {streamVar}.Write(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Single:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ByteBuffer(4)};");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(__buf, {valueExpr});");
                sb.AppendLine($"{indent}    {streamVar}.Write(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Double:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ByteBuffer(8)};");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(__buf, {valueExpr});");
                sb.AppendLine($"{indent}    {streamVar}.Write(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Int128:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ByteBuffer(16)};");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteInt128LittleEndian(__buf, {valueExpr});");
                sb.AppendLine($"{indent}    {streamVar}.Write(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.UInt128:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ByteBuffer(16)};");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(__buf, {valueExpr});");
                sb.AppendLine($"{indent}    {streamVar}.Write(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.DateOnly:
                sb.AppendLine($"{indent}{Helpers}.WriteVarUInt64({streamVar}, (ulong){valueExpr}.DayNumber);");
                break;
            case EncodableTypeKind.TimeOnly:
                sb.AppendLine($"{indent}{Helpers}.WriteVarUInt64({streamVar}, (ulong){valueExpr}.Ticks);");
                break;
            case EncodableTypeKind.TimeSpan:
                sb.AppendLine($"{indent}{Helpers}.WriteZigZagInt64({streamVar}, {valueExpr}.Ticks);");
                break;
            case EncodableTypeKind.DateTimeOffset:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    using var __dtoBuf = new {PooledStream}();");
                sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64(__dtoBuf, (ulong){valueExpr}.DateTime.Ticks);");
                sb.AppendLine($"{indent}    {Helpers}.WriteZigZagInt64(__dtoBuf, (long)({valueExpr}.Offset.TotalMinutes));");
                sb.AppendLine($"{indent}    {Helpers}.WriteVarUInt64({streamVar}, (ulong)__dtoBuf.Length);");
                sb.AppendLine($"{indent}    __dtoBuf.WriteTo({streamVar});");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Guid:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ByteBuffer(16)};");
                sb.AppendLine($"{indent}    {valueExpr}.TryWriteBytes(__buf, bigEndian: false, out _);");
                sb.AppendLine($"{indent}    {streamVar}.Write(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Decimal:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<int> __parts = {parts};");
                sb.AppendLine($"{indent}    global::System.Decimal.GetBits({valueExpr}, __parts);");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ByteBuffer(16)};");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(__buf.Slice(0, 4), __parts[0]);");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(__buf.Slice(4, 4), __parts[1]);");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(__buf.Slice(8, 4), __parts[2]);");
                sb.AppendLine($"{indent}    global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(__buf.Slice(12, 4), __parts[3]);");
                sb.AppendLine($"{indent}    {streamVar}.Write(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Per-property read emission. Switch-case arm per tag; payload parsed by wire type.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static void AppendReadCase(StringBuilder sb, string indent, PropertyInfo prop, int tag)
    {
        sb.AppendLine($"{indent}case {tag}:");
        switch (prop.Kind)
        {
            case TypeKind.Primitive:
                AppendReadPrimitive(sb, indent + "    ", prop.Descriptor, prop.Name, prop.TypeName);
                break;
            case TypeKind.AnnotatedType:
                sb.AppendLine($"{indent}    if (__wireType != {WireType}.LengthPrefixed) {{ {Helpers}.SkipField(__s, __wireType); break; }}");
                if (prop.ChildSelfFramed)
                {
                    sb.AppendLine($"{indent}    _{prop.Name} = {prop.AnnotatedTypeName}.Deserialize(__s);");
                }
                else
                {
                    // Parent-framed child: decode it from exactly its LEN-bounded bytes.
                    sb.AppendLine($"{indent}    {{");
                    sb.AppendLine($"{indent}        var __childLen = {Helpers}.ReadVarInt32LengthOrCount(__s, \"Nested field length\");");
                    sb.AppendLine($"{indent}        using var __childBufRented = {ReadBuffer}.RentAndRead(__s, __childLen);");
                    sb.AppendLine($"{indent}        using var __childStream = __childBufRented.AsStream();");
                    sb.AppendLine($"{indent}        _{prop.Name} = {prop.AnnotatedTypeName}.Deserialize(__childStream);");
                    // Bytes the child left unread mean the frame and the child disagree: the
                    // payload is corrupt (or written for a different child shape), not data.
                    sb.AppendLine($"{indent}        if (__childStream.Position != __childStream.Length) {{ throw new global::System.IO.InvalidDataException($\"Nested field '{prop.Name}' left {{__childStream.Length - __childStream.Position}} of its {{__childLen}} framed bytes unread — corrupt or mismatched child payload.\"); }}");
                    sb.AppendLine($"{indent}    }}");
                }
                break;
            case TypeKind.PrimitiveCollection:
                AppendReadPrimitiveCollection(sb, indent + "    ", prop);
                break;
            case TypeKind.AnnotatedCollection:
                AppendReadAnnotatedCollection(sb, indent + "    ", prop);
                break;
        }
        sb.AppendLine($"{indent}    break;");
    }

    private static void AppendReadPrimitive(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string localName,
        string clrTypeName)
    {
        switch (descriptor.Kind)
        {
            case EncodableTypeKind.Nullable:
                {
                    var innerKind = descriptor.NullableInner
                        ?? throw new InvalidOperationException("Nullable missing NullableInner.");
                    var innerFqn = descriptor.FullyQualifiedTypeName.EndsWith('?') ? descriptor.FullyQualifiedTypeName[..^1] : descriptor.FullyQualifiedTypeName;
                    var innerClrName = clrTypeName.EndsWith("?", StringComparison.Ordinal)
                        ? clrTypeName[..^1]
                        : clrTypeName;
                    var innerDescriptor = new EncodableTypeDescriptor(
                        innerKind, innerFqn, null, null, descriptor.EnumUnderlying, null);
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    {innerClrName} __nullableInner;");
                    AppendReadPrimitiveInto(sb, indent + "    ", innerDescriptor, "__nullableInner");
                    sb.AppendLine($"{indent}    _{localName} = __nullableInner;");
                    sb.AppendLine($"{indent}}}");
                    return;
                }
            case EncodableTypeKind.Enum:
                {
                    var underlyingKind = descriptor.EnumUnderlying
                        ?? throw new InvalidOperationException("Enum missing underlying.");
                    var underlyingCSharp = EnumUnderlyingCSharpName(underlyingKind);
                    var underlyingDescriptor = new EncodableTypeDescriptor(
                        underlyingKind, underlyingCSharp, null, null, null, null);
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    {underlyingCSharp} __enumRaw;");
                    AppendReadPrimitiveInto(sb, indent + "    ", underlyingDescriptor, "__enumRaw");
                    sb.AppendLine($"{indent}    _{localName} = ({clrTypeName})__enumRaw;");
                    sb.AppendLine($"{indent}}}");
                    return;
                }
        }

        // Plain Tier 1/2/3: check wire type, read payload into the declared field.
        var expected = WireTypeFor(descriptor.Kind);
        sb.AppendLine($"{indent}if (__wireType != {WireType}.{expected}) {{ {Helpers}.SkipField(__s, __wireType); break; }}");
        AppendReadPrimitivePayload(sb, indent, descriptor, $"_{localName}");
    }

    // Read a primitive payload into a pre-declared variable (used for enums, nullables,
    // and the top-level field case via a shared helper).
    private static void AppendReadPrimitiveInto(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string targetLocal)
    {
        // An enum inner (reached via the nullable-enum unwrap) reads its underlying numeric kind
        // and casts back. Without this, WireTypeFor(Enum) throws and the whole record generates no
        // source (a warning, not an error) — leaving a nullable-enum property silently unserializable.
        if (descriptor.Kind == EncodableTypeKind.Enum)
        {
            var underlyingKind = descriptor.EnumUnderlying
                ?? throw new InvalidOperationException("Enum missing underlying.");
            var underlyingCSharp = EnumUnderlyingCSharpName(underlyingKind);
            var underlyingDescriptor = new EncodableTypeDescriptor(
                underlyingKind, underlyingCSharp, null, null, null, null);
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    {underlyingCSharp} __enumRawInto;");
            AppendReadPrimitiveInto(sb, indent + "    ", underlyingDescriptor, "__enumRawInto");
            sb.AppendLine($"{indent}    {targetLocal} = ({descriptor.FullyQualifiedTypeName})__enumRawInto;");
            sb.AppendLine($"{indent}}}");
            return;
        }

        var expected = WireTypeFor(descriptor.Kind);
        sb.AppendLine($"{indent}if (__wireType != {WireType}.{expected}) {{ {Helpers}.SkipField(__s, __wireType); {targetLocal} = default; break; }}");
        AppendReadPrimitivePayload(sb, indent, descriptor, targetLocal);
    }

    private static void AppendReadPrimitivePayload(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string targetLocal)
    {
        AppendScalarRead(sb, indent, descriptor, targetLocal, "__s");
    }

    private static void AppendReadPrimitiveCollection(StringBuilder sb, string indent, PropertyInfo prop)
    {
        var elementKind = prop.Descriptor.ElementKind
            ?? throw new InvalidOperationException("Primitive collection missing ElementKind.");
        var elementDescriptor = new EncodableTypeDescriptor(
            elementKind,
            prop.AnnotatedTypeName ?? string.Empty,
            null, null,
            prop.Descriptor.EnumUnderlying,
            prop.Descriptor.NullableInner);

        // Phase F2 — packable elements tolerate both packed (LengthPrefixed) and unpacked
        // (element wire type) bytes on a single read. Non-packable elements use the count-
        // prefixed path exclusively. The property's [Packed]/[Unpacked] attribute governs
        // writer dispatch, not reader dispatch — the reader always takes both.
        if (IsPackableElement(elementKind))
        {
            AppendReadPackableCollection(sb, indent, prop, elementDescriptor);
        }
        else
        {
            AppendReadCountPrefixedCollection(sb, indent, prop, elementDescriptor);
        }
    }

    // Packable read (F2): dispatch on wire type. LengthPrefixed → packed block, everything
    // else → single unpacked element that appends into the list initialised via the field's
    // default expression at the top of Deserialize.
    private static void AppendReadPackableCollection(
        StringBuilder sb,
        string indent,
        PropertyInfo prop,
        EncodableTypeDescriptor elementDescriptor)
    {
        var elementWireType = WireTypeFor(elementDescriptor.Kind);
        sb.AppendLine($"{indent}if (__wireType == {WireType}.LengthPrefixed)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    // Packed: one LengthPrefixed block, concatenated element bodies, no count.");
        sb.AppendLine($"{indent}    var __collLen = {Helpers}.ReadVarInt32LengthOrCount(__s, \"Collection block length\");");
        sb.AppendLine($"{indent}    using var __collBufRented = {ReadBuffer}.RentAndRead(__s, __collLen);");
        sb.AppendLine($"{indent}    using var __collStream = __collBufRented.AsStream();");
        sb.AppendLine($"{indent}    var __list = (global::System.Collections.Generic.List<{prop.AnnotatedTypeName}>)_{prop.Name};");
        sb.AppendLine($"{indent}    while (__collStream.Position < __collStream.Length)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        {prop.AnnotatedTypeName} __elem;");
        AppendReadCollectionElement(sb, indent + "        ", elementDescriptor, "__elem", prop.AnnotatedTypeName!);
        sb.AppendLine($"{indent}        __list.Add(__elem);");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine($"{indent}else if (__wireType == {WireType}.{elementWireType})");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    // Unpacked: one element body at this tag — append to the list and let the");
        sb.AppendLine($"{indent}    // outer switch loop pick up the next tag occurrence.");
        sb.AppendLine($"{indent}    var __list = (global::System.Collections.Generic.List<{prop.AnnotatedTypeName}>)_{prop.Name};");
        sb.AppendLine($"{indent}    {prop.AnnotatedTypeName} __elem;");
        AppendReadUnpackedElementFromMainStream(sb, indent + "    ", elementDescriptor, "__elem");
        sb.AppendLine($"{indent}    __list.Add(__elem);");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine($"{indent}else");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    {Helpers}.SkipField(__s, __wireType);");
        sb.AppendLine($"{indent}}}");
    }

    // Count-prefixed read for non-packable element kinds. Single LengthPrefixed block
    // containing a count varint and that many element bodies. The canonical encoding for
    // string / Guid / Decimal / Tier-3 date-time / enum / nested-record collections.
    private static void AppendReadCountPrefixedCollection(
        StringBuilder sb,
        string indent,
        PropertyInfo prop,
        EncodableTypeDescriptor elementDescriptor)
    {
        sb.AppendLine($"{indent}if (__wireType != {WireType}.LengthPrefixed) {{ {Helpers}.SkipField(__s, __wireType); break; }}");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __collLen = {Helpers}.ReadVarInt32LengthOrCount(__s, \"Collection block length\");");
        sb.AppendLine($"{indent}    using var __collBufRented = {ReadBuffer}.RentAndRead(__s, __collLen);");
        sb.AppendLine($"{indent}    using var __collStream = __collBufRented.AsStream();");
        sb.AppendLine($"{indent}    var __count = {Helpers}.ReadVarInt32LengthOrCount(__collStream, \"Collection element count\");");
        // The count is corruption-controlled data; every element occupies at least one byte,
        // so a count beyond the block's remaining bytes is corrupt — fail loud before the
        // List constructor attempts a backing array sized by the bogus claim (a 5-byte block
        // declaring int.MaxValue elements would otherwise OutOfMemory on a ~17 GB array).
        sb.AppendLine($"{indent}    if (__count > __collStream.Length - __collStream.Position) {{ throw new global::System.IO.InvalidDataException($\"Collection element count {{__count}} exceeds the {{__collStream.Length - __collStream.Position}} bytes remaining in its block — corrupt count prefix.\"); }}");
        // Append into the accumulated list: repeated occurrences of one tag CONCATENATE
        // (Protobuf semantics), matching the packed/unpacked readers. Replacing the list here
        // would silently drop earlier blocks a foreign writer emitted for the same tag.
        sb.AppendLine($"{indent}    var __list = (global::System.Collections.Generic.List<{prop.AnnotatedTypeName}>)_{prop.Name};");
        sb.AppendLine($"{indent}    __list.EnsureCapacity(checked(__list.Count + __count));");
        sb.AppendLine($"{indent}    for (int __i = 0; __i < __count; __i++)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        {prop.AnnotatedTypeName} __elem;");
        AppendReadCollectionElement(sb, indent + "        ", elementDescriptor, "__elem", prop.AnnotatedTypeName!);
        sb.AppendLine($"{indent}        __list.Add(__elem);");
        sb.AppendLine($"{indent}    }}");
        AppendCollectionFullyConsumedCheck(sb, indent + "    ", prop.Name);
        sb.AppendLine($"{indent}}}");
    }

    // The writer backpatches the count and the LEN over the same elements, so a block whose
    // count leaves bytes unread is corrupt (a forged-down count would otherwise silently drop
    // the trailing elements) — the same rule the parent-framed nested child applies.
    private static void AppendCollectionFullyConsumedCheck(StringBuilder sb, string indent, string propertyName)
    {
        sb.AppendLine($"{indent}if (__collStream.Position != __collStream.Length) {{ throw new global::System.IO.InvalidDataException($\"Collection '{propertyName}' left {{__collStream.Length - __collStream.Position}} of its {{__collLen}} block bytes unread after {{__count}} elements — corrupt count prefix.\"); }}");
    }

    // Unpacked element read path from the MAIN Deserialize stream (__s) — the element's wire
    // type has already been validated by the outer switch arm. No tag is read here; the
    // outer loop already consumed the tag when it dispatched.
    private static void AppendReadUnpackedElementFromMainStream(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string targetLocal)
    {
        if (!IsPackableElement(descriptor.Kind))
        {
            throw new InvalidOperationException(
                $"Unpacked element read is only defined for Tier-1 numerics and bool; got '{descriptor.Kind}'. " +
                "Non-packable element kinds go through the count-prefixed reader (AppendReadCountPrefixedCollection).");
        }

        AppendScalarRead(sb, indent, descriptor, targetLocal, "__s");
    }

    private static void AppendReadAnnotatedCollection(StringBuilder sb, string indent, PropertyInfo prop)
    {
        sb.AppendLine($"{indent}if (__wireType != {WireType}.LengthPrefixed) {{ {Helpers}.SkipField(__s, __wireType); break; }}");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __collLen = {Helpers}.ReadVarInt32LengthOrCount(__s, \"Collection block length\");");
        sb.AppendLine($"{indent}    using var __collBufRented = {ReadBuffer}.RentAndRead(__s, __collLen);");
        sb.AppendLine($"{indent}    using var __collStream = __collBufRented.AsStream();");
        sb.AppendLine($"{indent}    var __count = {Helpers}.ReadVarInt32LengthOrCount(__collStream, \"Collection element count\");");
        // The count is corruption-controlled data; every element occupies at least one byte
        // (a self-framed child writes its own length prefix; any other child sits behind its
        // own LEN slot, so even a zero-byte child costs its frame), so a count beyond the
        // block's remaining bytes is corrupt — fail loud before the List constructor attempts
        // a backing array sized by the bogus claim (a 5-byte block declaring int.MaxValue
        // elements would otherwise OutOfMemory on a ~17 GB array).
        sb.AppendLine($"{indent}    if (__count > __collStream.Length - __collStream.Position) {{ throw new global::System.IO.InvalidDataException($\"Collection element count {{__count}} exceeds the {{__collStream.Length - __collStream.Position}} bytes remaining in its block — corrupt count prefix.\"); }}");
        // Append into the accumulated list — see the concatenation note in
        // AppendReadCountPrefixedCollection.
        sb.AppendLine($"{indent}    var __list = (global::System.Collections.Generic.List<{prop.AnnotatedTypeName}>)_{prop.Name};");
        sb.AppendLine($"{indent}    __list.EnsureCapacity(checked(__list.Count + __count));");
        sb.AppendLine($"{indent}    for (int __i = 0; __i < __count; __i++)");
        sb.AppendLine($"{indent}    {{");
        if (prop.ChildSelfFramed)
        {
            sb.AppendLine($"{indent}        __list.Add({prop.AnnotatedTypeName}.Deserialize(__collStream));");
        }
        else
        {
            // Parent-framed element: decode it from exactly its LEN-bounded bytes, as the
            // scalar nested-child read does.
            sb.AppendLine($"{indent}        var __elemLen = {Helpers}.ReadVarInt32LengthOrCount(__collStream, \"Collection element length\");");
            sb.AppendLine($"{indent}        using var __elemBufRented = {ReadBuffer}.RentAndRead(__collStream, __elemLen);");
            sb.AppendLine($"{indent}        using var __elemStream = __elemBufRented.AsStream();");
            sb.AppendLine($"{indent}        __list.Add({prop.AnnotatedTypeName}.Deserialize(__elemStream));");
            sb.AppendLine($"{indent}        if (__elemStream.Position != __elemStream.Length) {{ throw new global::System.IO.InvalidDataException($\"Collection '{prop.Name}' element {{__i}} left {{__elemStream.Length - __elemStream.Position}} of its {{__elemLen}} framed bytes unread — corrupt or mismatched element payload.\"); }}");
        }

        sb.AppendLine($"{indent}    }}");
        AppendCollectionFullyConsumedCheck(sb, indent + "    ", prop.Name);
        sb.AppendLine($"{indent}}}");
    }

    private static void AppendReadCollectionElement(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string targetLocal,
        string clrTypeName)
    {
        if (descriptor.Kind == EncodableTypeKind.Enum)
        {
            var underlyingKind = descriptor.EnumUnderlying
                ?? throw new InvalidOperationException("Enum missing underlying.");
            var underlyingCSharp = EnumUnderlyingCSharpName(underlyingKind);
            var underlyingDescriptor = new EncodableTypeDescriptor(
                underlyingKind, underlyingCSharp, null, null, null, null);
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    {underlyingCSharp} __eraw;");
            AppendReadCollectionElement(sb, indent + "    ", underlyingDescriptor, "__eraw", underlyingCSharp);
            sb.AppendLine($"{indent}    {targetLocal} = checked(({clrTypeName})__eraw);");
            sb.AppendLine($"{indent}}}");
            return;
        }

        if (descriptor.Kind is EncodableTypeKind.Nullable)
        {
            throw new InvalidOperationException(
                $"Nullable collection elements are rejected at transform time (BSSG001); " +
                "reaching this path is a generator bug.");
        }

        AppendScalarRead(sb, indent, descriptor, targetLocal, "__collStream");
    }

    // Single read-body helper shared by AppendReadPrimitivePayload, AppendReadUnpackedElementFromMainStream,
    // and AppendReadCollectionElement. Reads the wire-type-specific payload bytes from streamVar
    // into targetLocal. Callers enforce their own kind-restriction guards before delegating here.
    // Every call sits inside the tag loop, so fixed-width bodies are read through a slice of the
    // scratch AppendDeserialize allocates once before that loop, never a per-read stackalloc.
    private static void AppendScalarRead(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string targetLocal,
        string streamVar)
    {
        // Every Tier-1 narrowing scalar read is wrapped in checked(...) so a writer that
        // emitted a wider scalar than the reader's schema declares surfaces an OverflowException
        // at the read site rather than silently truncating. DDIA p. 171 frames this as a hazard
        // the reader must surface, not silently swallow. Previously the same `checked()` was
        // applied only on the enum read path; the same hazard applies to every Tier-1 narrowing
        // read (sbyte/short/int/byte/ushort/uint/char). Int64/UInt64/String/Bool do not narrow
        // and are unchanged.
        switch (descriptor.Kind)
        {
            case EncodableTypeKind.SByte:
                sb.AppendLine($"{indent}{targetLocal} = checked((sbyte){Helpers}.ReadZigZagInt64({streamVar}));"); break;
            case EncodableTypeKind.Int16:
                sb.AppendLine($"{indent}{targetLocal} = checked((short){Helpers}.ReadZigZagInt64({streamVar}));"); break;
            case EncodableTypeKind.Int32:
                sb.AppendLine($"{indent}{targetLocal} = checked((int){Helpers}.ReadZigZagInt64({streamVar}));"); break;
            case EncodableTypeKind.Int64:
                sb.AppendLine($"{indent}{targetLocal} = {Helpers}.ReadZigZagInt64({streamVar});"); break;
            case EncodableTypeKind.Byte:
                sb.AppendLine($"{indent}{targetLocal} = checked((byte){Helpers}.ReadVarUInt64({streamVar}));"); break;
            case EncodableTypeKind.UInt16:
                sb.AppendLine($"{indent}{targetLocal} = checked((ushort){Helpers}.ReadVarUInt64({streamVar}));"); break;
            case EncodableTypeKind.UInt32:
                sb.AppendLine($"{indent}{targetLocal} = checked((uint){Helpers}.ReadVarUInt64({streamVar}));"); break;
            case EncodableTypeKind.UInt64:
                sb.AppendLine($"{indent}{targetLocal} = {Helpers}.ReadVarUInt64({streamVar});"); break;
            case EncodableTypeKind.Bool:
                sb.AppendLine($"{indent}{targetLocal} = {Helpers}.ReadVarUInt64({streamVar}) != 0UL;"); break;
            case EncodableTypeKind.Char:
                sb.AppendLine($"{indent}{targetLocal} = checked((char){Helpers}.ReadVarUInt64({streamVar}));"); break;
            case EncodableTypeKind.String:
                sb.AppendLine($"{indent}{targetLocal} = {Helpers}.ReadString({streamVar});"); break;
            case EncodableTypeKind.Half:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ReadScratch}.Slice(0, 2);");
                sb.AppendLine($"{indent}    {Helpers}.ReadExact({streamVar}, __buf);");
                sb.AppendLine($"{indent}    {targetLocal} = global::System.BitConverter.UInt16BitsToHalf(global::System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(__buf));");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Single:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ReadScratch}.Slice(0, 4);");
                sb.AppendLine($"{indent}    {Helpers}.ReadExact({streamVar}, __buf);");
                sb.AppendLine($"{indent}    {targetLocal} = global::System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Double:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ReadScratch}.Slice(0, 8);");
                sb.AppendLine($"{indent}    {Helpers}.ReadExact({streamVar}, __buf);");
                sb.AppendLine($"{indent}    {targetLocal} = global::System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Int128:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ReadScratch}.Slice(0, 16);");
                sb.AppendLine($"{indent}    {Helpers}.ReadExact({streamVar}, __buf);");
                sb.AppendLine($"{indent}    {targetLocal} = global::System.Buffers.Binary.BinaryPrimitives.ReadInt128LittleEndian(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.UInt128:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ReadScratch}.Slice(0, 16);");
                sb.AppendLine($"{indent}    {Helpers}.ReadExact({streamVar}, __buf);");
                sb.AppendLine($"{indent}    {targetLocal} = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(__buf);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.DateOnly:
                // ToDateOnly range-checks the decoded value against [0, 3 652 058] and throws the
                // typed InvalidDataException corruption signal. Taking the long-width decoded value
                // directly also subsumes the beyond-Int32 case (the old checked((int)) only caught
                // the >Int32 arm, leaking a raw AOORE on in-Int32-but-out-of-DateOnly-range input).
                sb.AppendLine($"{indent}{targetLocal} = {Guards}.ToDateOnly((long){Helpers}.ReadVarUInt64({streamVar}));"); break;
            case EncodableTypeKind.TimeOnly:
                sb.AppendLine($"{indent}{targetLocal} = {Guards}.ToTimeOnly((long){Helpers}.ReadVarUInt64({streamVar}));"); break;
            case EncodableTypeKind.TimeSpan:
                sb.AppendLine($"{indent}{targetLocal} = new global::System.TimeSpan({Helpers}.ReadZigZagInt64({streamVar}));"); break;
            case EncodableTypeKind.DateTimeOffset:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var __dtoLen = {Helpers}.ReadVarInt32LengthOrCount({streamVar}, \"Nested record length\");");
                sb.AppendLine($"{indent}    using var __dtoBufRented = {ReadBuffer}.RentAndRead({streamVar}, __dtoLen);");
                sb.AppendLine($"{indent}    using var __dtoStream = __dtoBufRented.AsStream();");
                sb.AppendLine($"{indent}    var __ticks = (long){Helpers}.ReadVarUInt64(__dtoStream);");
                sb.AppendLine($"{indent}    var __offsetMin = {Helpers}.ReadZigZagInt64(__dtoStream);");
                // ToDateTimeOffsetFromLocal range-checks the decoded local ticks and offset minutes
                // (and the cross-field UTC-instant constraint) and throws the typed InvalidDataException
                // corruption signal, instead of letting new DateTime(long) / the DateTimeOffset ctor
                // leak a raw ArgumentOutOfRangeException out of the public Deserialize on forged input.
                sb.AppendLine($"{indent}    {targetLocal} = {Guards}.ToDateTimeOffsetFromLocal(__ticks, __offsetMin);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Guid:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ReadScratch}.Slice(0, 16);");
                sb.AppendLine($"{indent}    {Helpers}.ReadExact({streamVar}, __buf);");
                sb.AppendLine($"{indent}    {targetLocal} = new global::System.Guid(__buf, bigEndian: false);");
                sb.AppendLine($"{indent}}}");
                break;
            case EncodableTypeKind.Decimal:
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    global::System.Span<byte> __buf = {ReadScratch}.Slice(0, 16);");
                sb.AppendLine($"{indent}    {Helpers}.ReadExact({streamVar}, __buf);");
                sb.AppendLine($"{indent}    global::System.Span<int> __parts = {ReadScratchParts};");
                sb.AppendLine($"{indent}    __parts[0] = global::System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(__buf.Slice(0, 4));");
                sb.AppendLine($"{indent}    __parts[1] = global::System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(__buf.Slice(4, 4));");
                sb.AppendLine($"{indent}    __parts[2] = global::System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(__buf.Slice(8, 4));");
                sb.AppendLine($"{indent}    __parts[3] = global::System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(__buf.Slice(12, 4));");
                // ToDecimal range-checks the decoded flags word (reserved bits / scale > 28) and throws
                // the typed InvalidDataException corruption signal, instead of letting new Decimal(parts)
                // leak a raw ArgumentException out of the public Deserialize on forged input.
                sb.AppendLine($"{indent}    {targetLocal} = {Guards}.ToDecimal(__parts);");
                sb.AppendLine($"{indent}}}");
                break;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Wire-type dispatch table.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static string WireTypeFor(EncodableTypeKind kind) => kind switch
    {
        EncodableTypeKind.SByte or EncodableTypeKind.Byte or EncodableTypeKind.Int16 or
        EncodableTypeKind.UInt16 or EncodableTypeKind.Int32 or EncodableTypeKind.UInt32 or
        EncodableTypeKind.Int64 or EncodableTypeKind.UInt64 or EncodableTypeKind.Bool or
        EncodableTypeKind.Char or EncodableTypeKind.DateOnly or EncodableTypeKind.TimeOnly or
        EncodableTypeKind.TimeSpan => "Varint",

        EncodableTypeKind.Half => "Fixed16",
        EncodableTypeKind.Single => "Fixed32",
        EncodableTypeKind.Double => "Fixed64",

        EncodableTypeKind.Int128 or EncodableTypeKind.UInt128 or
        EncodableTypeKind.Guid or EncodableTypeKind.Decimal => "Fixed128",

        EncodableTypeKind.String or EncodableTypeKind.DateTimeOffset => "LengthPrefixed",

        _ => throw new InvalidOperationException($"No wire type mapped for kind '{kind}'."),
    };

    private static string EnumUnderlyingCSharpName(EncodableTypeKind kind) => kind switch
    {
        EncodableTypeKind.SByte => "sbyte",
        EncodableTypeKind.Byte => "byte",
        EncodableTypeKind.Int16 => "short",
        EncodableTypeKind.UInt16 => "ushort",
        EncodableTypeKind.Int32 => "int",
        EncodableTypeKind.UInt32 => "uint",
        EncodableTypeKind.Int64 => "long",
        EncodableTypeKind.UInt64 => "ulong",
        _ => throw new InvalidOperationException($"Kind '{kind}' is not a legal enum underlying."),
    };
}
