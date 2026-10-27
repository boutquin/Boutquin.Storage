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

// Phase 3 ordered-wire emitter for [Key] types. Emits Serialize/Deserialize
// bodies that produce byte-lex-comparable payloads: the byte-wise ordering of the emitted
// buffer exactly matches IComparable<T>.CompareTo on the decoded record.
//
// Composite keys concatenate fields in declaration order. Variable-length fields (string)
// self-terminate via 0x00 0x00 with 0x00-escape — the next field's bytes start immediately
// after the terminator. Leaf-level nullability is a 1-byte prefix (0x00 null, 0x01 present).
//
internal static class OrderedCodeGenerator
{
    private const string Helpers = "global::Boutquin.Storage.Domain.Helpers.OrderedByteHelpers";

    // Range-checked narrowing-conversion guards. A bare DateOnly.FromDayNumber / new TimeOnly on a
    // forged decoded value leaks a raw ArgumentOutOfRangeException out of the public Deserialize;
    // these helpers throw the typed InvalidDataException corruption signal instead.
    private const string Guards = "global::Boutquin.Storage.Domain.Helpers.CorruptionGuards";

    /// <summary>Emit the body of <c>void Serialize(Stream stream)</c> for a [Key] type.</summary>
    public static void AppendSerialize(StringBuilder sb, TypeToGenerate type, string indent)
    {
        sb.AppendLine($"{indent}public void Serialize(global::System.IO.Stream stream)");
        sb.AppendLine($"{indent}{{");
        foreach (var prop in type.Properties)
        {
            if (prop.Kind != TypeKind.Primitive)
            {
                // Shape diagnostics BSSG014/015/016 already fired at transform time for
                // nested / collection / nullable-annotated shapes. Keys only emit leaf kinds.
                continue;
            }
            // Member-access position — escape keyword-named properties (`this.@class`). The
            // propertyName argument stays bare: it is only used in string-literal error messages
            // and to build `_{name}` locals (an underscore prefix is never a keyword).
            AppendWriteField(sb, indent + "    ", prop.Descriptor, $"this.{IdentifierEscaper.Escape(prop.Name)}", prop.Name, prop.DecimalScale);
        }
        sb.AppendLine($"{indent}}}");
    }

    /// <summary>Emit the body of <c>static T Deserialize(Stream stream)</c> for a [Key] type.</summary>
    public static void AppendDeserialize(StringBuilder sb, TypeToGenerate type, string indent)
    {
        sb.AppendLine($"{indent}public static {IdentifierEscaper.Escape(type.Name)} Deserialize(global::System.IO.Stream stream)");
        sb.AppendLine($"{indent}{{");
        var ctorArgs = new List<string>();
        foreach (var prop in type.Properties)
        {
            if (prop.Kind != TypeKind.Primitive)
            {
                ctorArgs.Add("default");
                continue;
            }
            AppendReadField(sb, indent + "    ", prop.Descriptor, prop.Name, prop.TypeName);
            ctorArgs.Add($"_{prop.Name}");
        }
        sb.AppendLine($"{indent}    return new {IdentifierEscaper.Escape(type.Name)}({string.Join(", ", ctorArgs)});");
        sb.AppendLine($"{indent}}}");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Per-field emission.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static void AppendWriteField(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string valueExpr,
        string propertyName,
        int? decimalScale)
    {
        if (descriptor.Kind == EncodableTypeKind.Nullable)
        {
            var innerKind = descriptor.NullableInner
                ?? throw new InvalidOperationException("Nullable missing NullableInner.");
            sb.AppendLine($"{indent}{Helpers}.WriteNullDiscriminator(stream, {valueExpr}.HasValue);");
            sb.AppendLine($"{indent}if ({valueExpr}.HasValue)");
            sb.AppendLine($"{indent}{{");
            var innerFqn = descriptor.FullyQualifiedTypeName.EndsWith('?') ? descriptor.FullyQualifiedTypeName[..^1] : descriptor.FullyQualifiedTypeName;
            var innerDescriptor = new EncodableTypeDescriptor(
                innerKind, innerFqn, null, null, descriptor.EnumUnderlying, null);
            AppendWritePayload(sb, indent + "    ", innerDescriptor, $"{valueExpr}.Value", propertyName, decimalScale);
            sb.AppendLine($"{indent}}}");
            return;
        }

        AppendWritePayload(sb, indent, descriptor, valueExpr, propertyName, decimalScale);
    }

    private static void AppendWritePayload(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string valueExpr,
        string propertyName,
        int? decimalScale)
    {
        switch (descriptor.Kind)
        {
            case EncodableTypeKind.SByte:
                sb.AppendLine($"{indent}{Helpers}.WriteInt8(stream, {valueExpr});"); break;
            case EncodableTypeKind.Byte:
                sb.AppendLine($"{indent}{Helpers}.WriteUInt8(stream, {valueExpr});"); break;
            case EncodableTypeKind.Int16:
                sb.AppendLine($"{indent}{Helpers}.WriteInt16(stream, {valueExpr});"); break;
            case EncodableTypeKind.UInt16:
                sb.AppendLine($"{indent}{Helpers}.WriteUInt16(stream, {valueExpr});"); break;
            case EncodableTypeKind.Int32:
                sb.AppendLine($"{indent}{Helpers}.WriteInt32(stream, {valueExpr});"); break;
            case EncodableTypeKind.UInt32:
                sb.AppendLine($"{indent}{Helpers}.WriteUInt32(stream, {valueExpr});"); break;
            case EncodableTypeKind.Int64:
                sb.AppendLine($"{indent}{Helpers}.WriteInt64(stream, {valueExpr});"); break;
            case EncodableTypeKind.UInt64:
                sb.AppendLine($"{indent}{Helpers}.WriteUInt64(stream, {valueExpr});"); break;
            case EncodableTypeKind.Bool:
                sb.AppendLine($"{indent}{Helpers}.WriteBool(stream, {valueExpr});"); break;
            case EncodableTypeKind.Char:
                sb.AppendLine($"{indent}{Helpers}.WriteChar(stream, {valueExpr});"); break;
            case EncodableTypeKind.String:
                sb.AppendLine($"{indent}{Helpers}.WriteOrderedString(stream, {valueExpr} ?? throw new global::System.InvalidOperationException(\"Property '{propertyName}' cannot be null during key serialization. Use leaf-level nullability for optional key fields.\"));");
                break;
            case EncodableTypeKind.DateOnly:
                // DayNumber is int and always ≥ 0 — unsigned BE preserves order.
                sb.AppendLine($"{indent}{Helpers}.WriteUInt32(stream, (uint){valueExpr}.DayNumber);");
                break;
            case EncodableTypeKind.TimeOnly:
                // Ticks is long and always ≥ 0 — unsigned BE.
                sb.AppendLine($"{indent}{Helpers}.WriteUInt64(stream, (ulong){valueExpr}.Ticks);");
                break;
            case EncodableTypeKind.TimeSpan:
                // Ticks can be negative; use the signed BE + sign-flip path.
                sb.AppendLine($"{indent}{Helpers}.WriteInt64(stream, {valueExpr}.Ticks);");
                break;
            case EncodableTypeKind.Guid:
                sb.AppendLine($"{indent}{Helpers}.WriteGuid(stream, {valueExpr});"); break;

            case EncodableTypeKind.Half:
                sb.AppendLine($"{indent}{Helpers}.WriteHalf(stream, {valueExpr});"); break;
            case EncodableTypeKind.Single:
                sb.AppendLine($"{indent}{Helpers}.WriteSingle(stream, {valueExpr});"); break;
            case EncodableTypeKind.Double:
                sb.AppendLine($"{indent}{Helpers}.WriteDouble(stream, {valueExpr});"); break;
            case EncodableTypeKind.Int128:
                sb.AppendLine($"{indent}{Helpers}.WriteInt128(stream, {valueExpr});"); break;
            case EncodableTypeKind.UInt128:
                sb.AppendLine($"{indent}{Helpers}.WriteUInt128(stream, {valueExpr});"); break;
            case EncodableTypeKind.DateTimeOffset:
                sb.AppendLine($"{indent}{Helpers}.WriteDateTimeOffset(stream, {valueExpr});"); break;
            case EncodableTypeKind.Decimal:
                {
                    // BSSG017 is an Error diagnostic that blocks code emission when
                    // [DecimalScale] is absent or out-of-range, so reaching this branch
                    // without a valid scale is a generator bug — throw rather than silently
                    // emitting code that would produce wrong byte-lex ordering.
                    if (decimalScale is not int s || s < 0 || s > 28)
                    {
                        throw new InvalidOperationException(
                            $"Decimal key property requires a valid [DecimalScale(N)] (0 \u2264 N \u2264 28). " +
                            "BSSG017 should have blocked emission before reaching this code path.");
                    }

                    sb.AppendLine($"{indent}{Helpers}.WriteDecimal(stream, {valueExpr}, {s});");
                    break;
                }

            case EncodableTypeKind.Enum:
                {
                    var underlying = descriptor.EnumUnderlying
                        ?? throw new InvalidOperationException("Enum missing EnumUnderlying.");
                    var underlyingCSharp = EnumUnderlyingCSharpName(underlying);
                    var underlyingDescriptor = new EncodableTypeDescriptor(
                        underlying, underlyingCSharp, null, null, null, null);
                    AppendWritePayload(sb, indent, underlyingDescriptor, $"({underlyingCSharp}){valueExpr}", propertyName, decimalScale);
                    break;
                }

        }
    }

    private static void AppendReadField(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string localName,
        string clrTypeName)
    {
        if (descriptor.Kind == EncodableTypeKind.Nullable)
        {
            var innerKind = descriptor.NullableInner
                ?? throw new InvalidOperationException("Nullable missing NullableInner.");
            var innerFqn = descriptor.FullyQualifiedTypeName.EndsWith('?') ? descriptor.FullyQualifiedTypeName[..^1] : descriptor.FullyQualifiedTypeName;
            var innerClrName = clrTypeName.EndsWith("?", StringComparison.Ordinal)
                ? clrTypeName[..^1]
                : clrTypeName;
            sb.AppendLine($"{indent}{clrTypeName} _{localName} = default;");
            sb.AppendLine($"{indent}if ({Helpers}.ReadNullDiscriminator(stream))");
            sb.AppendLine($"{indent}{{");
            var innerDescriptor = new EncodableTypeDescriptor(
                innerKind, innerFqn, null, null, descriptor.EnumUnderlying, null);
            // AppendReadPayloadInto prepends a single '_' to whatever local name it is handed, so the
            // emitted variable is `_{tempLocal}`. Pick a base name WITHOUT a leading underscore here
            // so the result is a single-underscore local (`_{localName}__nullable`) rather than the
            // triple-underscore `___{localName}_val` an "__"-prefixed base would have produced.
            var tempLocal = $"{localName}__nullable";
            AppendReadPayloadInto(sb, indent + "    ", innerDescriptor, tempLocal, innerClrName);
            sb.AppendLine($"{indent}    _{localName} = _{tempLocal};");
            sb.AppendLine($"{indent}}}");
            return;
        }

        AppendReadPayloadInto(sb, indent, descriptor, localName, clrTypeName);
    }

    private static void AppendReadPayloadInto(
        StringBuilder sb,
        string indent,
        EncodableTypeDescriptor descriptor,
        string localName,
        string clrTypeName)
    {
        switch (descriptor.Kind)
        {
            case EncodableTypeKind.SByte:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadInt8(stream);"); break;
            case EncodableTypeKind.Byte:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadUInt8(stream);"); break;
            case EncodableTypeKind.Int16:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadInt16(stream);"); break;
            case EncodableTypeKind.UInt16:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadUInt16(stream);"); break;
            case EncodableTypeKind.Int32:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadInt32(stream);"); break;
            case EncodableTypeKind.UInt32:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadUInt32(stream);"); break;
            case EncodableTypeKind.Int64:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadInt64(stream);"); break;
            case EncodableTypeKind.UInt64:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadUInt64(stream);"); break;
            case EncodableTypeKind.Bool:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadBool(stream);"); break;
            case EncodableTypeKind.Char:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadChar(stream);"); break;
            case EncodableTypeKind.String:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadOrderedString(stream);"); break;
            case EncodableTypeKind.DateOnly:
                sb.AppendLine($"{indent}var _{localName} = {Guards}.ToDateOnly((long){Helpers}.ReadUInt32(stream));");
                break;
            case EncodableTypeKind.TimeOnly:
                sb.AppendLine($"{indent}var _{localName} = {Guards}.ToTimeOnly((long){Helpers}.ReadUInt64(stream));");
                break;
            case EncodableTypeKind.TimeSpan:
                sb.AppendLine($"{indent}var _{localName} = new global::System.TimeSpan({Helpers}.ReadInt64(stream));");
                break;
            case EncodableTypeKind.Guid:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadGuid(stream);"); break;

            case EncodableTypeKind.Half:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadHalf(stream);"); break;
            case EncodableTypeKind.Single:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadSingle(stream);"); break;
            case EncodableTypeKind.Double:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadDouble(stream);"); break;
            case EncodableTypeKind.Int128:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadInt128(stream);"); break;
            case EncodableTypeKind.UInt128:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadUInt128(stream);"); break;
            case EncodableTypeKind.DateTimeOffset:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadDateTimeOffset(stream);"); break;
            case EncodableTypeKind.Decimal:
                sb.AppendLine($"{indent}var _{localName} = {Helpers}.ReadDecimal(stream);"); break;

            case EncodableTypeKind.Enum:
                {
                    var underlying = descriptor.EnumUnderlying
                        ?? throw new InvalidOperationException("Enum missing EnumUnderlying.");
                    var underlyingCSharp = EnumUnderlyingCSharpName(underlying);
                    var rawLocal = $"__{localName}_raw";
                    var underlyingDescriptor = new EncodableTypeDescriptor(
                        underlying, underlyingCSharp, null, null, null, null);
                    AppendReadPayloadInto(sb, indent, underlyingDescriptor, rawLocal, underlyingCSharp);
                    sb.AppendLine($"{indent}var _{localName} = checked(({clrTypeName})_{rawLocal});");
                    break;
                }

        }
    }

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
