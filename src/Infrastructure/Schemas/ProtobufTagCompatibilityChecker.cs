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
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>
/// Diffs an external Protobuf message descriptor against a <see cref="TaggedWireSchema"/> and reports
/// a structured <see cref="SchemaCompatibilityResult"/> — tag-by-tag type agreement plus reserved-tag
/// conflicts. A cross-format pre-flight: "is this externally-published <c>.proto</c> schema compatible
/// with my <c>[StorageSerializable]</c> record?". Treats its external input as untrusted control-plane
/// data: a malformed descriptor or an absent message yields a failed result (a structured reason), not
/// an exception.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 5 — "Encoding and Evolution": Protobuf wire format and tag-numbered field compatibility.</para>
/// </summary>
public static class ProtobufTagCompatibilityChecker
{
    /// <summary>
    /// Checks a <see cref="TaggedWireSchema"/> against a named message in a parsed
    /// <see cref="FileDescriptorSet"/>.
    /// </summary>
    public static SchemaCompatibilityResult Check(
        TaggedWireSchema record,
        FileDescriptorSet external,
        string externalMessageName)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(external);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalMessageName);

        var message = FindMessage(external, externalMessageName);
        if (message is null)
        {
            return Failed(0, $"External message '{externalMessageName}' was not found in the descriptor set.");
        }

        var reasons = new List<SchemaIncompatibility>();
        var externalByTag = new Dictionary<int, FieldDescriptorProto>();
        foreach (var field in message.Field)
        {
            externalByTag[field.Number] = field;
        }

        // A shared tag is compatible only when the two types' COMPATIBILITY CLASSES agree —
        // the same vocabulary the in-family TaggedWireCompatibilityChecker uses. Comparing the
        // framing WireType alone is not enough: record signed ints are zig-zag varints (proto
        // sint*) while proto int32/uint32/bool are plain varints — identical framing, but every
        // nonzero value mis-decodes (1↔2, −1↔1). The class comparison preserves the blessed
        // widenings (record int ↔ sint64, record uint ↔ uint64) and rejects byte-aliased
        // siblings, exactly as the sibling checker does within the tagged-wire family.
        foreach (var field in record.TaggedFields)
        {
            if (externalByTag.TryGetValue(field.Tag, out var externalField))
            {
                // Nested-record descriptors are consulted BEFORE the name-derived class (as the
                // in-family checker does): a generated record may legally be named Long or String,
                // and its simple name would otherwise classify it as a proto scalar although it
                // is written length-prefixed. A nested record is its own non-scalar class that no
                // proto scalar maps into, so the comparison fails closed.
                // A recursive back-reference is a nested record too. A non-record field is
                // classified by the type the encoder actually writes: an enum keeps its declared
                // name (which may spell a scalar keyword) while its bytes are its underlying integer.
                var recordClass = record.NestedSchemas.ContainsKey(field.Tag)
                    || record.NestedKeyLayouts.ContainsKey(field.Tag)
                    || record.RecursiveTags.ContainsKey(field.Tag)
                    ? "record:nested"
                    : TaggedWireTypeMapping.CompatibilityClassOf(field.WireTypeName ?? field.TypeName);
                var externalClass = MapProtoCompatibilityClass(externalField.Type);

                // Framing is part of the datatype: `repeated` changes the bytes under the tag
                // (proto3 scalars default-packed → one LENGTH-PREFIXED block; even unpacked
                // repeated means multiple occurrences), so a repeated external field can never
                // match a record SCALAR's class. Prefixing makes it its own class — no record
                // TypeName maps into it, so the comparison fails closed. (The mirror direction —
                // record collection vs external scalar — already fails closed via the record's
                // own type:ienumerable<…> class.)
                if (externalField.Label == FieldDescriptorProto.Types.Label.Repeated)
                {
                    externalClass = "proto-repeated:" + externalClass;
                }

                if (!string.Equals(recordClass, externalClass, StringComparison.Ordinal))
                {
                    reasons.Add(new SchemaIncompatibility(
                        field.Tag,
                        $"Tag {field.Tag}: record type '{field.TypeName}' (class {recordClass}) is incompatible "
                        + $"with external Protobuf type {externalField.Type} (class {externalClass})."));
                }
            }
        }

        // An external field that reuses a tag the record retired with [ReservedTag] is incompatible.
        foreach (var field in message.Field)
        {
            if (record.ReservedTags.Contains(field.Number))
            {
                reasons.Add(new SchemaIncompatibility(
                    field.Number,
                    $"Tag {field.Number} is reserved in the record but used by external field '{field.Name}'."));
            }
        }

        // Mirror direction: a record live tag inside the EXTERNAL schema's reserved range is
        // incompatible — the external lineage retired those tags, so a record field there
        // collides with the external family's retired data (the hazard proto reservation
        // exists to prevent). ReservedRange.End is exclusive per the proto descriptor spec.
        foreach (var field in record.TaggedFields)
        {
            foreach (var range in message.ReservedRange)
            {
                if (field.Tag >= range.Start && field.Tag < range.End)
                {
                    reasons.Add(new SchemaIncompatibility(
                        field.Tag,
                        $"Tag {field.Tag} ('{field.Name}') is live in the record but reserved by the external "
                        + $"message '{message.Name}' (range [{range.Start}, {range.End}))."));
                    break;
                }
            }
        }

        return reasons.Count == 0
            ? new SchemaCompatibilityResult(true, [])
            : new SchemaCompatibilityResult(false, reasons);
    }

    /// <summary>
    /// Checks a <see cref="TaggedWireSchema"/> against a named message in a serialized
    /// <see cref="FileDescriptorSet"/>. Unparseable bytes yield a failed result, never a throw.
    /// </summary>
    public static SchemaCompatibilityResult Check(
        TaggedWireSchema record,
        byte[] externalDescriptorBytes,
        string externalMessageName)
    {
        ArgumentNullException.ThrowIfNull(externalDescriptorBytes);

        FileDescriptorSet external;
        try
        {
            external = FileDescriptorSet.Parser.ParseFrom(externalDescriptorBytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return Failed(0, $"External descriptor bytes could not be parsed as a FileDescriptorSet: {ex.Message}");
        }

        return Check(record, external, externalMessageName);
    }

    // Searches top-level messages AND nested declarations (DescriptorProto.NestedType) —
    // proto messages are routinely declared inside another message, and a top-level-only
    // lookup reports a false "not found" failure for them. First match by simple name wins.
    private static DescriptorProto? FindMessage(FileDescriptorSet set, string name)
    {
        foreach (var file in set.File)
        {
            foreach (var message in file.MessageType)
            {
                var found = FindMessageRecursive(message, name);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static DescriptorProto? FindMessageRecursive(DescriptorProto message, string name)
    {
        if (string.Equals(message.Name, name, StringComparison.Ordinal))
        {
            return message;
        }

        foreach (var nested in message.NestedType)
        {
            var found = FindMessageRecursive(nested, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static SchemaCompatibilityResult Failed(int tag, string reason)
        => new(false, [new SchemaIncompatibility(tag, reason)]);

    /// <summary>
    /// Maps a Protobuf field type into the record-side compatibility-class vocabulary
    /// (<see cref="TaggedWireTypeMapping.CompatibilityClassOf"/>). Genuine equivalences map into
    /// the record classes: proto <c>sint*</c> are zig-zag varints (the record's signed-int
    /// family), proto <c>int*</c>/<c>uint*</c> are plain varints (the record's unsigned-int
    /// family), and <c>bool</c>/<c>string</c>/<c>float</c>/<c>double</c> match their record
    /// namesakes byte-for-byte. Proto types with no record equivalent (fixed-width ints, enum,
    /// bytes, message, group) map to <c>proto:*</c> classes that match nothing — fail-closed:
    /// the checker cannot verify their value encoding against any record type, so it reports a
    /// structured incompatibility rather than passing on framing alone.
    /// </summary>
    private static string MapProtoCompatibilityClass(FieldDescriptorProto.Types.Type type) => type switch
    {
        // Zig-zag varints — the record's signed-int family (mutually widening).
        FieldDescriptorProto.Types.Type.Sint32 or FieldDescriptorProto.Types.Type.Sint64
            => TaggedWireTypeMapping.CompatibilityClassOf("int"),
        // Plain varints — the record's unsigned-int family (mutually widening). Note proto
        // int32/int64 sign-extend negatives to 10 bytes; a record unsigned reader decodes
        // those as huge positives, the same caveat proto itself documents for int↔uint reads.
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Int64
            or FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Uint64
            => TaggedWireTypeMapping.CompatibilityClassOf("uint"),
        FieldDescriptorProto.Types.Type.Bool => TaggedWireTypeMapping.CompatibilityClassOf("bool"),
        FieldDescriptorProto.Types.Type.String => TaggedWireTypeMapping.CompatibilityClassOf("string"),
        FieldDescriptorProto.Types.Type.Float => TaggedWireTypeMapping.CompatibilityClassOf("float"),
        FieldDescriptorProto.Types.Type.Double => TaggedWireTypeMapping.CompatibilityClassOf("double"),
        // No record equivalent — distinct fail-closed classes.
        FieldDescriptorProto.Types.Type.Fixed32 => "proto:fixed32",
        FieldDescriptorProto.Types.Type.Sfixed32 => "proto:sfixed32",
        FieldDescriptorProto.Types.Type.Fixed64 => "proto:fixed64",
        FieldDescriptorProto.Types.Type.Sfixed64 => "proto:sfixed64",
        FieldDescriptorProto.Types.Type.Enum => "proto:enum",
        FieldDescriptorProto.Types.Type.Bytes => "proto:bytes",
        FieldDescriptorProto.Types.Type.Message => "proto:message",
        FieldDescriptorProto.Types.Type.Group => "proto:group",
        _ => "proto:unknown",
    };
}
