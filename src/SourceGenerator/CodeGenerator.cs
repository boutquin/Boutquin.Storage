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

internal static class CodeGenerator
{
    private const string GeneratorName = "Boutquin.Storage.SourceGenerator";
    private const string GeneratorVersion = "1.0.0";

    // System.HashCode.Combine has overloads for 1..8 arguments only.
    private const int MaxHashCodeCombineArity = 8;

    public static string Generate(TypeToGenerate type, StorageDefaultsConfig defaults)
    {
        var sb = new StringBuilder();

        AppendFileHeader(sb);

        // Namespace (file-scoped, omit if global)
        if (type.Namespace is not null)
        {
            sb.AppendLine($"namespace {type.Namespace};");
            sb.AppendLine();
        }

        // Open parent type wrappers (for nested types), outermost first
        var parentDepth = 0;
        foreach (var parent in type.ParentTypes)
        {
            var indent = new string(' ', parentDepth * 4);
            AppendParentOpen(sb, parent, indent);
            parentDepth++;
        }

        // Main type declaration
        var typeIndent = new string(' ', parentDepth * 4);
        AppendTypeDeclaration(sb, type, typeIndent);

        // Open brace
        sb.AppendLine($"{typeIndent}{{");

        // Serialize — [StorageSerializable] uses Phase 2 tagged wire; [Key] uses Phase 3
        // ordered wire (byte-lex-comparable). The two paths are fundamentally different
        // wire formats, selected by annotation.
        if (!type.SkipSerializable)
        {
            if (type.IsKey)
            {
                OrderedCodeGenerator.AppendSerialize(sb, type, typeIndent + "    ");
                sb.AppendLine();
                OrderedCodeGenerator.AppendDeserialize(sb, type, typeIndent + "    ");
            }
            else
            {
                TaggedWireEmitter.AppendSerialize(sb, type, typeIndent + "    ");
                sb.AppendLine();
                TaggedWireEmitter.AppendDeserialize(sb, type, typeIndent + "    ");
            }
        }

        // IProjectable<TSelf>.ProjectTo emission — memtable-vs-columnar-segment shape uniformity.
        // Only non-[Key] records participate — [Key] types index the store and don't carry the
        // field-clipping concept. The base-list entry is added in AppendTypeDeclaration; the
        // method body lives here so it sits next to Serialize/Deserialize on the generated partial.
        if (!type.IsKey && !type.SkipProjectable)
        {
            sb.AppendLine();
            ProjectionEmitter.AppendProjectTo(sb, type, typeIndent + "    ");
        }

        // TaggedWireSchemaFields / TaggedWireReservedTags — runtime tag metadata for the
        // schema-registry bridge. The [Tag]/[ReservedTag] attributes are [Conditional] and absent
        // from IL, so this static descriptor is the only runtime source of the tag layout.
        // A [Key] type is written on the ordered wire instead; when it is nested inside a
        // [StorageSerializable] record its bytes are part of that record's schema, so it carries
        // an OrderedWireLayout descriptor the compatibility checker compares exactly.
        if (!type.IsKey)
        {
            sb.AppendLine();
            AppendTaggedWireSchemaDescriptor(sb, type, typeIndent + "    ");
        }
        else
        {
            sb.AppendLine();
            AppendOrderedWireLayoutDescriptor(sb, type, typeIndent + "    ");
        }

        // CompareTo (Key only)
        if (type.IsKey && !type.SkipComparable)
        {
            sb.AppendLine();
            AppendCompareTo(sb, type, typeIndent + "    ");
        }

        // Equals / GetHashCode / operators (Key only, skip for any record kind — record struct
        // AND record class both auto-synthesize these; re-emitting collides (CS0102/CS0111))
        if (type.IsKey && !type.SkipEquatable && !type.IsRecord)
        {
            sb.AppendLine();
            AppendEquality(sb, type, typeIndent + "    ");
        }

        // A record key's synthesized equality compares a DateTimeOffset by UTC instant only,
        // while the generated CompareTo (and the ordered wire) also orders by offset. Equality-
        // keyed engine structures (tombstone maps) would then treat two distinct keys as one, so
        // records with a DateTimeOffset component get an Equals/GetHashCode pair that agrees with
        // CompareTo. The other leaf kinds' default equality already agrees with their comparator.
        if (type.IsKey && type.IsRecord && !type.SkipEquatable && !type.SkipComparable &&
            type.Properties.Any(IsDateTimeOffsetProperty))
        {
            sb.AppendLine();
            AppendRecordKeyEquality(sb, type, typeIndent + "    ");
        }

        // Comparison operators (Key only, controlled by defaults)
        if (type.IsKey && !type.SkipComparable && defaults.GenerateComparisonOperators)
        {
            sb.AppendLine();
            AppendComparisonOperators(sb, type, typeIndent + "    ");
        }

        // Close type
        sb.AppendLine($"{typeIndent}}}");

        // Close parent type wrappers (innermost first)
        for (var i = parentDepth - 1; i >= 0; i--)
        {
            var indent = new string(' ', i * 4);
            sb.AppendLine($"{indent}}}");
        }

        // Generated source is a build artifact: its text and Roslyn spans must not depend on
        // the host's line terminator (StringBuilder.AppendLine uses CRLF on Windows).
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static void AppendFileHeader(StringBuilder sb)
    {
        // <auto-generated> comes first so Roslyn's comment-position check suppresses CS1591
        // without relying solely on the .g.cs extension heuristic.
        // Fixed copyright range: a wall-clock year here makes generator output time-dependent,
        // breaking deterministic builds across a year boundary (same source, same generator,
        // different bytes). The range is part of the generator's own source and is updated when
        // the generator itself is touched, like every hand-written header in the repo.
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"// This code was generated by {GeneratorName}.");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.");
        sb.AppendLine("//");
        sb.AppendLine("//  Licensed under the Apache License, Version 2.0 (the \"License\").");
        sb.AppendLine("//  You may not use this file except in compliance with the License.");
        sb.AppendLine("//  You may obtain a copy of the License at");
        sb.AppendLine("//");
        sb.AppendLine("//      http://www.apache.org/licenses/LICENSE-2.0");
        sb.AppendLine("//");
        sb.AppendLine("//  Unless required by applicable law or agreed to in writing, software");
        sb.AppendLine("//  distributed under the License is distributed on an \"AS IS\" BASIS,");
        sb.AppendLine("//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.");
        sb.AppendLine("//");
        sb.AppendLine("//  See the License for the specific language governing permissions and");
        sb.AppendLine("//  limitations under the License.");
        sb.AppendLine("//");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
    }

    internal static void AppendParentOpen(StringBuilder sb, ParentTypeInfo parent, string indent)
    {
        var generic = parent.GenericParameters ?? "";
        var constraints = parent.Constraints is not null ? $" {parent.Constraints}" : "";
        sb.AppendLine($"{indent}{parent.Modifiers} {parent.Kind} {parent.Name}{generic}{constraints}");
        sb.AppendLine($"{indent}{{");
    }

    private static void AppendTypeDeclaration(StringBuilder sb, TypeToGenerate type, string indent)
    {
        sb.AppendLine($"{indent}[global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"{GeneratorName}\", \"{GeneratorVersion}\")]");
        sb.AppendLine($"{indent}[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");

        // Type-name positions (the partial declaration name + every TSelf generic argument) must
        // also be escaped — a record named with a verbatim keyword would otherwise emit `partial
        // record struct class : IComparable<class>`, which is uncompilable.
        var typeName = IdentifierEscaper.Escape(type.Name);

        var interfaces = new List<string>();

        // [Key] types satisfy `IStorageKey<TSelf>` — the aggregator over IComparable, IEquatable,
        // and ISerializable that file-backed engine signatures expect. Emit the aggregator when
        // none of the underlying skip flags are set so the generator-emitted record satisfies
        // every storage-engine `where TKey : IStorageKey<TKey>, new()` constraint without the
        // consumer needing to spell out the three base interfaces.
        var emitStorageKeyAggregator =
            type.IsKey
            && !type.SkipSerializable
            && !type.SkipComparable
            && (type.IsRecord || !type.SkipEquatable);

        if (emitStorageKeyAggregator)
        {
            interfaces.Add($"global::Boutquin.Storage.Domain.Interfaces.IStorageKey<{typeName}>");
        }
        else if (!type.SkipSerializable)
        {
            interfaces.Add($"global::Boutquin.Storage.Domain.Interfaces.ISerializable<{typeName}>");
        }

        // IProjectable<TSelf> applies to non-[Key] records only. Adding it to the base
        // list here keeps CS0535 at bay — the ProjectTo body emitted in Generate must satisfy
        // an interface the type actually declares.
        if (!type.IsKey && !type.SkipProjectable)
        {
            interfaces.Add($"global::Boutquin.Storage.Domain.Interfaces.IProjectable<{typeName}>");
        }

        if (type.IsKey && !type.SkipComparable && !emitStorageKeyAggregator)
        {
            interfaces.Add($"global::System.IComparable<{typeName}>");
        }
        // Records (struct or class) already synthesize IEquatable<T> — don't re-declare it
        if (type.IsKey && !type.SkipEquatable && !type.IsRecord && !emitStorageKeyAggregator)
        {
            interfaces.Add($"global::System.IEquatable<{typeName}>");
        }

        var baseList = interfaces.Count > 0 ? " :\n" + indent + "    " + string.Join(",\n" + indent + "    ", interfaces) : "";
        // Repeat the user's own type-declaration keyword. Emitting "record struct" onto a user's
        // "record class" / "struct" / "class" partial is a CS0261 keyword conflict in consumer code.
        sb.AppendLine($"{indent}{type.Accessibility} partial {type.TypeKeyword} {typeName}{baseList}");
    }

    // Phase 3 landmark — positional emission (PositionalEmitter / the AppendSerializeProperty
    // and AppendDeserializeProperty helpers) was retired. [StorageSerializable] uses Phase 2
    // tagged wire; [Key] uses Phase 3 ordered wire. No code path still routes through the
    // 1.x BinaryWriter/BinaryReader dispatch. The old helpers are gone; CodeGenerator.Generate
    // at the top of this file is the sole entry point and delegates to TaggedWireEmitter /
    // OrderedCodeGenerator based on annotation.

    // The user's declaration keyword decides value vs reference semantics (TypeKeyword is
    // "struct" / "record struct" / "class" / "record class").
    private static bool IsReferenceType(TypeToGenerate type) =>
        type.TypeKeyword is not ("struct" or "record struct");

    private static void AppendCompareTo(StringBuilder sb, TypeToGenerate type, string indent)
    {
        var typeName = IdentifierEscaper.Escape(type.Name);
        // A reference-type key implements IComparable<T>.CompareTo(T? other): the parameter is
        // nullable (else CS8767 in the consumer's build) and null orders before every instance.
        var isReference = IsReferenceType(type);
        sb.AppendLine($"{indent}public int CompareTo({typeName}{(isReference ? "?" : "")} other)");
        sb.AppendLine($"{indent}{{");
        if (isReference)
        {
            sb.AppendLine($"{indent}    if (other is null) return 1;");
        }

        var compareableProps = type.Properties
            .Where(p => p.Kind != TypeKind.Unsupported &&
                        p.Kind != TypeKind.PrimitiveCollection &&
                        p.Kind != TypeKind.AnnotatedCollection)
            .ToList();

        if (compareableProps.Count == 0)
        {
            sb.AppendLine($"{indent}    return 0;");
        }
        else if (compareableProps.Count == 1)
        {
            sb.AppendLine($"{indent}    return {BuildCompareExpression(compareableProps[0])};");
        }
        else
        {
            // Multi-property: chained lexicographic
            for (var i = 0; i < compareableProps.Count - 1; i++)
            {
                var varName = $"cmp{i}";
                sb.AppendLine($"{indent}    var {varName} = {BuildCompareExpression(compareableProps[i])};");
                sb.AppendLine($"{indent}    if ({varName} != 0) return {varName};");
            }
            var last = compareableProps[^1];
            sb.AppendLine($"{indent}    return {BuildCompareExpression(last)};");
        }

        sb.AppendLine($"{indent}}}");
    }

    // Enums don't implement IComparable<T>, so SafeCompareTo<T> with a `where T : IComparable<T>`
    // constraint rejects them. Cast both sides to the underlying numeric type so the comparison
    // flows through the underlying's own IComparable<T>. Non-enum kinds (including Tier 3 BCL
    // primitives like DateOnly, which does implement IComparable<T>) retain the existing path.
    //
    // Strings on [Key] types compare via OrderedByteHelpers.CompareStringByteLex — Unicode
    // code-point order, matching the byte-lex order of the ordered-wire UTF-8 emission.
    // Neither string.CompareTo (CurrentCulture-sensitive) nor String.CompareOrdinal (UTF-16
    // code-UNIT order, which inverts supplementary-plane code points against U+E000..U+FFFF)
    // tracks the encoded bytes. The ordered-wire contract prescribes UTF-8 byte-lex ordering for key strings; the
    // in-memory CompareTo must track that invariant.
    private static string BuildCompareExpression(PropertyInfo prop)
    {
        // Identifier positions throughout this method — escape keyword-named properties so
        // `this.@class` / `other.@class` compiles. The source name (prop.Name) is bare.
        var name = IdentifierEscaper.Escape(prop.Name);
        if (prop.Descriptor.Kind == EncodableTypeKind.Enum)
        {
            var underlyingCSharp = EnumUnderlyingCSharpName(prop.Descriptor.EnumUnderlying);
            return $"global::Boutquin.Storage.Domain.Helpers.ComparisonHelper.SafeCompareTo(({underlyingCSharp})this.{name}, ({underlyingCSharp})other.{name})";
        }
        if (prop.Descriptor.Kind == EncodableTypeKind.String)
        {
            return $"global::Boutquin.Storage.Domain.Helpers.OrderedByteHelpers.CompareStringByteLex(this.{name}, other.{name})";
        }
        // Nullable value-type [Key] fields: a nullable cannot satisfy SafeCompareTo's
        // `where T : IComparable<T>` constraint (CS0313), so compare with explicit null handling.
        // Null sorts strictly first, matching the ordered-wire null discriminator (0x00 < 0x01) the
        // writer emits. When both are present, compare the underlying values with the SAME comparator
        // the non-nullable path would use for that inner kind (e.g. Guid still uses byte-lex BE).
        if (prop.Descriptor.Kind == EncodableTypeKind.Nullable)
        {
            var left = $"this.{name}";
            var right = $"other.{name}";
            string innerCompare;
            if (prop.Descriptor.NullableInner == EncodableTypeKind.Guid)
            {
                innerCompare = $"global::Boutquin.Storage.Domain.Helpers.OrderedByteHelpers.CompareGuidByteLex({left}.Value, {right}.Value)";
            }
            else if (prop.Descriptor.NullableInner == EncodableTypeKind.DateTimeOffset)
            {
                // Same byte-lex tracking as the non-nullable DateTimeOffset path below:
                // DateTimeOffset.CompareTo collapses same-instant/different-offset pairs to 0
                // while the wire encodes the offset in the trailing two bytes.
                innerCompare = $"global::Boutquin.Storage.Domain.Helpers.OrderedByteHelpers.CompareDateTimeOffsetByteLex({left}.Value, {right}.Value)";
            }
            else if (prop.Descriptor.NullableInner == EncodableTypeKind.Enum)
            {
                // An enum present value compares by its underlying numeric kind (matching the
                // non-nullable enum path and the ordered-wire body), since the enum itself does
                // not satisfy SafeCompareTo's `where T : IComparable<T>` constraint.
                var underlyingCSharp = EnumUnderlyingCSharpName(prop.Descriptor.EnumUnderlying);
                innerCompare = $"global::Boutquin.Storage.Domain.Helpers.ComparisonHelper.SafeCompareTo(({underlyingCSharp}){left}.Value, ({underlyingCSharp}){right}.Value)";
            }
            else
            {
                innerCompare = $"global::Boutquin.Storage.Domain.Helpers.ComparisonHelper.SafeCompareTo({left}.Value, {right}.Value)";
            }
            return $"(!{left}.HasValue && !{right}.HasValue) ? 0 "
                 + $": !{left}.HasValue ? -1 "
                 + $": !{right}.HasValue ? 1 "
                 + $": {innerCompare}";
        }
        // Phase A3 — [Key] Guid fields must compare by the ordered-wire BE byte-lex, not by
        // Guid.CompareTo. Guid's default comparator reads the first three groups as little-
        // endian fields, while the encoder emits big-endian bytes; the two orderings disagree
        // on most random GUIDs, which breaks SSTable binary search on Guid-keyed datasets.
        if (prop.Descriptor.Kind == EncodableTypeKind.Guid)
        {
            return $"global::Boutquin.Storage.Domain.Helpers.OrderedByteHelpers.CompareGuidByteLex(this.{name}, other.{name})";
        }
        // [Key] DateTimeOffset fields must compare instant-first-then-offset, tracking the
        // 10-byte encoding (8 BE UtcTicks + 2 BE offset-minutes). DateTimeOffset.CompareTo
        // compares the UTC instant only, reporting same-instant/different-offset pairs as
        // equal while their encodings differ in the trailing two bytes — sign(0) != sign(±1)
        // at the equality boundary breaks SSTable binary search against the memtable comparator.
        if (prop.Descriptor.Kind == EncodableTypeKind.DateTimeOffset)
        {
            return $"global::Boutquin.Storage.Domain.Helpers.OrderedByteHelpers.CompareDateTimeOffsetByteLex(this.{name}, other.{name})";
        }
        return $"global::Boutquin.Storage.Domain.Helpers.ComparisonHelper.SafeCompareTo(this.{name}, other.{name})";
    }

    private static string EnumUnderlyingCSharpName(EncodableTypeKind? underlying) => underlying switch
    {
        EncodableTypeKind.SByte => "sbyte",
        EncodableTypeKind.Byte => "byte",
        EncodableTypeKind.Int16 => "short",
        EncodableTypeKind.UInt16 => "ushort",
        EncodableTypeKind.Int32 => "int",
        EncodableTypeKind.UInt32 => "uint",
        EncodableTypeKind.Int64 => "long",
        EncodableTypeKind.UInt64 => "ulong",
        // A silent "int" fallback would mask a future enum-underlying-kind addition behind a
        // wrong-type cast in the generated comparator. Fail loudly instead, matching the other
        // exhaustive switches in this file (and OrderedCodeGenerator.EnumUnderlyingCSharpName).
        _ => throw new ArgumentOutOfRangeException(
            nameof(underlying), underlying, "Unrecognized enum underlying kind."),
    };

    private static void AppendEquality(StringBuilder sb, TypeToGenerate type, string indent)
    {
        var allProps = type.Properties.Where(p => p.Kind != TypeKind.Unsupported).ToList();
        var typeName = IdentifierEscaper.Escape(type.Name);

        // Equals(T)
        var isReference = IsReferenceType(type);
        sb.AppendLine($"{indent}public bool Equals({typeName}{(isReference ? "?" : "")} other)");
        sb.AppendLine($"{indent}{{");
        if (isReference)
        {
            sb.AppendLine($"{indent}    if (other is null) return false;");
        }

        if (allProps.Count == 0)
        {
            sb.AppendLine($"{indent}    return true;");
        }
        else
        {
            var conditions = allProps.Select(p => BuildEqualityExpression(p, "this", "other"));
            sb.AppendLine($"{indent}    return {string.Join($"\n{indent}        && ", [.. conditions])};");
        }
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();

        // Equals(object)
        sb.AppendLine($"{indent}public override bool Equals(object? obj) => obj is {typeName} other && Equals(other);");
        sb.AppendLine();

        // GetHashCode
        sb.AppendLine($"{indent}public override int GetHashCode()");
        sb.AppendLine($"{indent}{{");
        if (allProps.Count == 0)
        {
            sb.AppendLine($"{indent}    return 0;");
        }
        else if (allProps.Count > MaxHashCodeCombineArity || allProps.Any(IsDateTimeOffsetProperty))
        {
            // DateTimeOffset hashes by (UtcTicks, Offset) to stay consistent with its
            // CompareTo-aligned equality; the default hash covers the instant only. Keys wider
            // than HashCode.Combine's largest overload accumulate incrementally too.
            AppendHashCodeBody(sb, allProps, indent + "    ");
        }
        else if (allProps.Count == 1)
        {
            sb.AppendLine($"{indent}    return global::System.Collections.Generic.EqualityComparer<{allProps[0].TypeName}>.Default.GetHashCode({IdentifierEscaper.Escape(allProps[0].Name)});");
        }
        else
        {
            var args = string.Join(", ", allProps.Select(p => IdentifierEscaper.Escape(p.Name)));
            sb.AppendLine($"{indent}    return global::System.HashCode.Combine({args});");
        }
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();

        // == and !=
        if (isReference)
        {
            sb.AppendLine($"{indent}public static bool operator ==({typeName}? left, {typeName}? right) => left is null ? right is null : left.Equals(right);");
            sb.AppendLine($"{indent}public static bool operator !=({typeName}? left, {typeName}? right) => !(left == right);");
        }
        else
        {
            sb.AppendLine($"{indent}public static bool operator ==({typeName} left, {typeName} right) => left.Equals(right);");
            sb.AppendLine($"{indent}public static bool operator !=({typeName} left, {typeName} right) => !left.Equals(right);");
        }
    }

    private static bool IsDateTimeOffsetProperty(PropertyInfo prop) =>
        prop.Kind != TypeKind.Unsupported &&
        (prop.Descriptor.Kind == EncodableTypeKind.DateTimeOffset ||
         (prop.Descriptor.Kind == EncodableTypeKind.Nullable &&
          prop.Descriptor.NullableInner == EncodableTypeKind.DateTimeOffset));

    // Equality of one key component, exactly CompareTo()==0 for that component. DateTimeOffset
    // compares instant AND offset (EqualsExact), matching CompareDateTimeOffsetByteLex and the
    // 10-byte ordered encoding; every other kind keeps its default equality.
    private static string BuildEqualityExpression(PropertyInfo prop, string left, string right)
    {
        var name = IdentifierEscaper.Escape(prop.Name);
        var l = $"{left}.{name}";
        var r = $"{right}.{name}";
        if (prop.Descriptor.Kind == EncodableTypeKind.DateTimeOffset)
        {
            return $"{l}.EqualsExact({r})";
        }

        if (prop.Descriptor.Kind == EncodableTypeKind.Nullable &&
            prop.Descriptor.NullableInner == EncodableTypeKind.DateTimeOffset)
        {
            return $"({l}.HasValue ? {r}.HasValue && {l}.GetValueOrDefault().EqualsExact({r}.GetValueOrDefault()) : !{r}.HasValue)";
        }

        return $"global::System.Collections.Generic.EqualityComparer<{prop.TypeName}>.Default.Equals({l}, {r})";
    }

    private static void AppendHashCodeBody(StringBuilder sb, List<PropertyInfo> props, string indent)
    {
        sb.AppendLine($"{indent}var __hash = new global::System.HashCode();");
        foreach (var prop in props)
        {
            var value = $"this.{IdentifierEscaper.Escape(prop.Name)}";
            if (prop.Descriptor.Kind == EncodableTypeKind.DateTimeOffset)
            {
                sb.AppendLine($"{indent}__hash.Add({value}.UtcTicks);");
                sb.AppendLine($"{indent}__hash.Add({value}.Offset);");
            }
            else if (prop.Descriptor.Kind == EncodableTypeKind.Nullable &&
                     prop.Descriptor.NullableInner == EncodableTypeKind.DateTimeOffset)
            {
                sb.AppendLine($"{indent}__hash.Add({value}.HasValue);");
                sb.AppendLine($"{indent}__hash.Add({value}.GetValueOrDefault().UtcTicks);");
                sb.AppendLine($"{indent}__hash.Add({value}.GetValueOrDefault().Offset);");
            }
            else
            {
                sb.AppendLine($"{indent}__hash.Add({value});");
            }
        }

        sb.AppendLine($"{indent}return __hash.ToHashCode();");
    }

    // Equals(T) / GetHashCode() for a [Key] record, replacing the compiler-synthesized pair
    // (the synthesized Equals(object) and ==/!= route through Equals(T), so they follow).
    private static void AppendRecordKeyEquality(StringBuilder sb, TypeToGenerate type, string indent)
    {
        var typeName = IdentifierEscaper.Escape(type.Name);
        var props = type.Properties.Where(p => p.Kind != TypeKind.Unsupported).ToList();
        var isRecordStruct = type.TypeKeyword == "record struct";
        var conditions = string.Join(
            $"\n{indent}        && ",
            props.Select(p => BuildEqualityExpression(p, "this", "other")));

        if (isRecordStruct)
        {
            sb.AppendLine($"{indent}public bool Equals({typeName} other)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    return {conditions};");
        }
        else
        {
            // A non-sealed record's Equals(T?) must be virtual (CS8872); a sealed one must not
            // be (CS0549). The transform repeats `sealed` in the declaration modifiers.
            var isSealed = type.Accessibility.EndsWith(" sealed", StringComparison.Ordinal);
            sb.AppendLine($"{indent}public {(isSealed ? "" : "virtual ")}bool Equals({typeName}? other)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    if (object.ReferenceEquals(this, other)) return true;");
            sb.AppendLine($"{indent}    if (other is null || this.EqualityContract != other.EqualityContract) return false;");
            sb.AppendLine($"{indent}    return {conditions};");
        }

        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
        sb.AppendLine($"{indent}public override int GetHashCode()");
        sb.AppendLine($"{indent}{{");
        AppendHashCodeBody(sb, props, indent + "    ");
        sb.AppendLine($"{indent}}}");
    }

    private static void AppendComparisonOperators(StringBuilder sb, TypeToGenerate type, string indent)
    {
        var typeName = IdentifierEscaper.Escape(type.Name);
        if (IsReferenceType(type))
        {
            // null sorts before every instance and equal to null (the BCL comparison-operator contract).
            sb.AppendLine($"{indent}public static bool operator <({typeName}? left, {typeName}? right) => left is null ? right is not null : left.CompareTo(right) < 0;");
            sb.AppendLine($"{indent}public static bool operator >({typeName}? left, {typeName}? right) => left is not null && left.CompareTo(right) > 0;");
            sb.AppendLine($"{indent}public static bool operator <=({typeName}? left, {typeName}? right) => left is null || left.CompareTo(right) <= 0;");
            sb.AppendLine($"{indent}public static bool operator >=({typeName}? left, {typeName}? right) => left is null ? right is null : left.CompareTo(right) >= 0;");
            return;
        }

        sb.AppendLine($"{indent}public static bool operator <({typeName} left, {typeName} right) => left.CompareTo(right) < 0;");
        sb.AppendLine($"{indent}public static bool operator >({typeName} left, {typeName} right) => left.CompareTo(right) > 0;");
        sb.AppendLine($"{indent}public static bool operator <=({typeName} left, {typeName} right) => left.CompareTo(right) <= 0;");
        sb.AppendLine($"{indent}public static bool operator >=({typeName} left, {typeName} right) => left.CompareTo(right) >= 0;");
    }

    // Emits the runtime tagged-wire schema descriptor consumed by the schema-registry bridge:
    //   public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; }
    //   public static IReadOnlyList<int>             TaggedWireReservedTags { get; }
    // Fields are sorted by tag (stable, deterministic order). IsOptional is derived from a trailing
    // nullable annotation on the declared type name. Only invoked for [StorageSerializable] records.
    // An enum is written on the tagged wire as its underlying integer, so its declared name hides
    // the field's real wire identity: changing the underlying type moves the tag between zig-zag
    // and plain varint (or changes its width) while the name stays the same. Record the encoded
    // type for every enum-bearing shape the generator supports — scalar, nullable, collection —
    // so the compatibility checker compares what the bytes are, not what the enum is called.
    private static string? EnumWireTypeName(PropertyInfo p)
    {
        if (p.Descriptor.EnumUnderlying is not { } underlyingKind)
        {
            return null;
        }

        var underlying = underlyingKind switch
        {
            EncodableTypeKind.SByte => "sbyte",
            EncodableTypeKind.Byte => "byte",
            EncodableTypeKind.Int16 => "short",
            EncodableTypeKind.UInt16 => "ushort",
            EncodableTypeKind.Int32 => "int",
            EncodableTypeKind.UInt32 => "uint",
            EncodableTypeKind.Int64 => "long",
            EncodableTypeKind.UInt64 => "ulong",
            _ => throw new InvalidOperationException($"Kind '{underlyingKind}' is not a legal enum underlying."),
        };

        return p.Descriptor.Kind switch
        {
            EncodableTypeKind.Enum => underlying,
            EncodableTypeKind.Nullable => underlying + "?",
            EncodableTypeKind.EnumCollection => $"System.Collections.Generic.IEnumerable<{underlying}>",
            _ => throw new InvalidOperationException(
                $"Property '{p.Name}' carries an enum underlying kind on unexpected shape '{p.Descriptor.Kind}'."),
        };
    }

    // PropertyInfo.TypeName is global::-qualified because emitted code re-binds it inside the
    // generated file's namespace scope. Text that is not code — the schema descriptor's declared
    // type, a diagnostic message — keeps the display form, which is the qualified form with every
    // `global::` alias qualifier removed (the qualifier can appear only as a name prefix, never
    // inside an identifier).
    internal static string WithoutGlobalQualifier(string qualifiedTypeName) =>
        qualifiedTypeName.Replace("global::", string.Empty);

    // Emits the [Key] ordered-wire layout descriptor:
    //   public static string OrderedWireLayout { get; }
    // One comma-separated token per field, in declaration order — the order the ordered wire
    // writes them. The ordered wire is positional, fixed-width per type and carries no tags,
    // names or lengths, so the token records exactly what decides the bytes: the encoded kind
    // ("Int32", "String", …), a leading "?" for the 1-byte null discriminator of a nullable
    // field, an enum's underlying kind (an enum is written as that integer), and a decimal's
    // [DecimalScale]. Field names are not recorded — they never reach the wire.
    private static void AppendOrderedWireLayoutDescriptor(StringBuilder sb, TypeToGenerate type, string indent)
    {
        var tokens = type.Properties
            .Where(p => p.Kind == TypeKind.Primitive)
            .Select(p => OrderedWireToken(p.Descriptor.Kind, p.Descriptor.EnumUnderlying, p.Descriptor.NullableInner, p.DecimalScale));
        var layout = string.Join(",", tokens);

        sb.AppendLine($"{indent}/// <summary>Ordered-wire field layout (encoded kind per position) the schema-compatibility checker compares when this [Key] record is nested.</summary>");
        sb.AppendLine($"{indent}public static string OrderedWireLayout {{ get; }} = \"{layout}\";");
    }

    private static string OrderedWireToken(EncodableTypeKind kind, EncodableTypeKind? enumUnderlying, EncodableTypeKind? nullableInner, int? decimalScale)
    {
        if (kind == EncodableTypeKind.Nullable)
        {
            var inner = nullableInner
                ?? throw new InvalidOperationException("Nullable missing NullableInner.");
            return "?" + OrderedWireToken(inner, enumUnderlying, null, decimalScale);
        }

        if (kind == EncodableTypeKind.Enum)
        {
            var underlying = enumUnderlying
                ?? throw new InvalidOperationException("Enum missing EnumUnderlying.");
            return OrderedWireToken(underlying, null, null, decimalScale);
        }

        return kind == EncodableTypeKind.Decimal
            ? "Decimal(" + (decimalScale ?? -1).ToString(global::System.Globalization.CultureInfo.InvariantCulture) + ")"
            : kind.ToString();
    }

    private static void AppendTaggedWireSchemaDescriptor(StringBuilder sb, TypeToGenerate type, string indent)
    {
        const string fieldFqn = "global::Boutquin.Storage.Domain.ValueObjects.TaggedWireField";

        var fields = type.Properties
            .Where(p => p.Kind != TypeKind.Unsupported && p.Tag is > 0)
            .OrderBy(p => p.Tag!.Value)
            .ToList();

        sb.AppendLine($"{indent}/// <summary>Runtime tagged-wire schema descriptor (tag, name, declared type, optionality) for the schema-registry bridge.</summary>");
        sb.AppendLine($"{indent}public static global::System.Collections.Generic.IReadOnlyList<{fieldFqn}> TaggedWireSchemaFields {{ get; }} = new {fieldFqn}[]");
        sb.AppendLine($"{indent}{{");
        foreach (var p in fields)
        {
            var optional = p.TypeName.EndsWith("?", global::System.StringComparison.Ordinal) ? "true" : "false";
            var escapedName = p.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var escapedType = WithoutGlobalQualifier(p.TypeName).Replace("\\", "\\\\").Replace("\"", "\\\"");
            var wireType = EnumWireTypeName(p);
            var wireInit = wireType is null ? string.Empty : $" {{ WireTypeName = \"{wireType}\" }}";
            sb.AppendLine($"{indent}    new {fieldFqn}({p.Tag!.Value}, \"{escapedName}\", \"{escapedType}\", {optional}){wireInit},");
        }

        sb.AppendLine($"{indent}}};");
        sb.AppendLine();

        sb.AppendLine($"{indent}/// <summary>Tag numbers retired via [ReservedTag]; a later schema reusing one is wire-incompatible.</summary>");
        if (type.ReservedTags.Length == 0)
        {
            sb.AppendLine($"{indent}public static global::System.Collections.Generic.IReadOnlyList<int> TaggedWireReservedTags {{ get; }} = global::System.Array.Empty<int>();");
        }
        else
        {
            var tags = string.Join(
                ", ",
                type.ReservedTags.Select(t => t.ToString(global::System.Globalization.CultureInfo.InvariantCulture)));
            sb.AppendLine($"{indent}public static global::System.Collections.Generic.IReadOnlyList<int> TaggedWireReservedTags {{ get; }} = new int[] {{ {tags} }};");
        }
    }
}
