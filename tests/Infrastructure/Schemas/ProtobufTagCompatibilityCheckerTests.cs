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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Infrastructure.Schemas;

using Google.Protobuf.Reflection;

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

// A record that retires tag 3 — exercises the reserved-tag-collision rule against external descriptors.
[StorageSerializable]
[ReservedTag(3)]
public partial record struct ProtoPersonReserved([property: Tag(1)] string Name, [property: Tag(2)] int Age);

// A record with a float field (Fixed32 on the wire) — exercises the float/double wire-type
// distinction the lossy SchemaFieldType comparison used to collapse (finding #5).
[StorageSerializable]
public partial record struct ProtoMeasurement([property: Tag(1)] float Value);

/// <summary>
/// Tests for <see cref="ProtobufTagCompatibilityChecker"/> — cross-format diff of an external Protobuf
/// descriptor against a <see cref="TaggedWireSchema"/>. Reuses <c>BridgePerson</c> (tag 1 string, tag 2 int).
/// </summary>
public sealed class ProtobufTagCompatibilityCheckerTests
{
    private static FileDescriptorSet DescriptorSet(
        string messageName,
        params (string Name, int Tag, FieldDescriptorProto.Types.Type Type)[] fields)
    {
        var message = new DescriptorProto { Name = messageName };
        foreach (var (name, tag, type) in fields)
        {
            message.Field.Add(new FieldDescriptorProto
            {
                Name = name,
                Number = tag,
                Type = type,
                Label = FieldDescriptorProto.Types.Label.Optional,
            });
        }

        var file = new FileDescriptorProto { Name = "test.proto", Syntax = "proto3" };
        file.MessageType.Add(message);
        var set = new FileDescriptorSet();
        set.File.Add(file);
        return set;
    }

    [Fact]
    public void IdenticalSchema_IsCompatible()
    {
        // The proto twin of a record signed int is sint32 — the emitter writes signed integers
        // zig-zag. (proto int32 is a PLAIN varint: same framing, different value encoding.)
        var record = TaggedWireSchema.FromType<BridgePerson>();
        var external = DescriptorSet(
            "Person",
            ("name", 1, FieldDescriptorProto.Types.Type.String),
            ("age", 2, FieldDescriptorProto.Types.Type.Sint32));

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "Person");

        result.IsCompatible.Should().BeTrue();
        result.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void TypeMismatchOnTag_IsIncompatibleWithReason()
    {
        var record = TaggedWireSchema.FromType<BridgePerson>();
        var external = DescriptorSet(
            "Person",
            ("name", 1, FieldDescriptorProto.Types.Type.String),
            ("age", 2, FieldDescriptorProto.Types.Type.String)); // tag 2 is string externally, int in the record

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "Person");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 2);
    }

    [Fact]
    public void RecordFloat_VsExternalDouble_IsIncompatible()
    {
        // float (Fixed32) vs double (Fixed64) is a genuine wire incompatibility. The old lossy
        // SchemaFieldType comparison mapped both to Float64 and passed it (finding #5).
        var record = TaggedWireSchema.FromType<ProtoMeasurement>();
        var external = DescriptorSet("M", ("value", 1, FieldDescriptorProto.Types.Type.Double));

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "M");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 1);
    }

    [Fact]
    public void RecordFloat_VsExternalFloat_IsCompatible()
    {
        var record = TaggedWireSchema.FromType<ProtoMeasurement>();
        var external = DescriptorSet("M", ("value", 1, FieldDescriptorProto.Types.Type.Float));

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "M");

        result.IsCompatible.Should().BeTrue();
    }

    [Fact]
    public void RecordInt_VsExternalSint64_IsCompatible_VarintWidening()
    {
        // Record signed ints are zig-zag varints — proto's sint family. sint32↔sint64 widening
        // is wire-safe, so record int against external sint64 is compatible.
        var record = TaggedWireSchema.FromType<BridgePerson>(); // tag 2 is int
        var external = DescriptorSet(
            "Person",
            ("name", 1, FieldDescriptorProto.Types.Type.String),
            ("age", 2, FieldDescriptorProto.Types.Type.Sint64));

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "Person");

        result.IsCompatible.Should().BeTrue();
    }

    [Fact]
    public void RecordInt_VsExternalPlainInt64_IsIncompatible()
    {
        // Record signed ints are zig-zag (proto sint*); external int64 is a PLAIN varint. The
        // framing matches but every nonzero value mis-decodes (1↔2, −1↔1) — the same zig-zag/
        // plain split the in-family TaggedWireCompatibilityChecker already rejects. Comparing
        // only the framing WireType silently passed this.
        var record = TaggedWireSchema.FromType<BridgePerson>(); // tag 2 is int
        var external = DescriptorSet(
            "Person",
            ("name", 1, FieldDescriptorProto.Types.Type.String),
            ("age", 2, FieldDescriptorProto.Types.Type.Int64));

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "Person");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 2);
    }

    [Fact]
    public void RecordInt_VsExternalBool_IsIncompatible()
    {
        // bool shares the varint framing but is not a number; the value reinterpretation is the
        // same silent mis-decode class as zig-zag/plain.
        var record = TaggedWireSchema.FromType<BridgePerson>(); // tag 2 is int
        var external = DescriptorSet(
            "Person",
            ("name", 1, FieldDescriptorProto.Types.Type.String),
            ("age", 2, FieldDescriptorProto.Types.Type.Bool));

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "Person");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 2);
    }

    [Fact]
    public void NestedExternalMessage_IsFoundByName()
    {
        // proto messages may be declared inside another message (DescriptorProto.NestedType);
        // a top-level-only lookup reports a false "not found" failure for them.
        var nested = new DescriptorProto { Name = "Inner" };
        nested.Field.Add(new FieldDescriptorProto
        {
            Name = "name",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional,
        });
        nested.Field.Add(new FieldDescriptorProto
        {
            Name = "age",
            Number = 2,
            Type = FieldDescriptorProto.Types.Type.Sint32,
            Label = FieldDescriptorProto.Types.Label.Optional,
        });
        var outer = new DescriptorProto { Name = "Outer" };
        outer.NestedType.Add(nested);
        var file = new FileDescriptorProto { Name = "test.proto", Syntax = "proto3" };
        file.MessageType.Add(outer);
        var set = new FileDescriptorSet();
        set.File.Add(file);

        var record = TaggedWireSchema.FromType<BridgePerson>();
        var result = ProtobufTagCompatibilityChecker.Check(record, set, "Inner");

        result.IsCompatible.Should().BeTrue();
    }

    [Fact]
    public void ExternalReservedRange_CoveringRecordLiveTag_IsIncompatible()
    {
        // Mirror of the record-side [ReservedTag] check: when the EXTERNAL schema reserves a tag
        // range, its lineage retired those tags — a record writing a live field on a reserved tag
        // collides with the external family's retired data (the hazard proto reservation exists
        // to prevent).
        var message = new DescriptorProto { Name = "Person" };
        message.Field.Add(new FieldDescriptorProto
        {
            Name = "name",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional,
        });
        // Reserve tags [2, 3) — covering BridgePerson's live tag 2 (proto end is exclusive).
        message.ReservedRange.Add(new DescriptorProto.Types.ReservedRange { Start = 2, End = 3 });
        var file = new FileDescriptorProto { Name = "test.proto", Syntax = "proto3" };
        file.MessageType.Add(message);
        var set = new FileDescriptorSet();
        set.File.Add(file);

        var record = TaggedWireSchema.FromType<BridgePerson>(); // tag 2 is live
        var result = ProtobufTagCompatibilityChecker.Check(record, set, "Person");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 2);
    }

    [Fact]
    public void MissingMessage_IsFailedResult_NotThrow()
    {
        var record = TaggedWireSchema.FromType<BridgePerson>();
        var external = DescriptorSet("Person", ("name", 1, FieldDescriptorProto.Types.Type.String));

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "Nonexistent");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().NotBeEmpty();
    }

    [Fact]
    public void MalformedDescriptorBytes_IsFailedResult_NotThrow()
    {
        var record = TaggedWireSchema.FromType<BridgePerson>();
        var garbage = new byte[] { 0xFF, 0xFF, 0xFF, 0x07 };

        var result = ProtobufTagCompatibilityChecker.Check(record, garbage, "Person");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().NotBeEmpty();
    }

    [Fact]
    public void ExternalUsesReservedTag_IsIncompatible()
    {
        var record = TaggedWireSchema.FromType<ProtoPersonReserved>(); // reserves tag 3
        var external = DescriptorSet(
            "Person",
            ("name", 1, FieldDescriptorProto.Types.Type.String),
            ("age", 2, FieldDescriptorProto.Types.Type.Int32),
            ("ghost", 3, FieldDescriptorProto.Types.Type.String)); // reuses retired tag 3

        var result = ProtobufTagCompatibilityChecker.Check(record, external, "Person");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 3);
    }

    [Fact]
    public void ExternalRepeatedScalar_AgainstRecordScalar_IsIncompatible()
    {
        // Framing is part of the datatype: a proto3 `repeated sint32` is default-packed — a
        // LENGTH-PREFIXED block on the wire — while the record's scalar `int` reader expects a
        // bare zig-zag varint under the tag. Ignoring FieldDescriptorProto.Label passes the pair
        // on value-class alone and every actual read mis-frames.
        var record = TaggedWireSchema.FromType<BridgePerson>(); // tag 2 is a scalar int
        var message = new DescriptorProto { Name = "Person" };
        message.Field.Add(new FieldDescriptorProto
        {
            Name = "name",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional,
        });
        message.Field.Add(new FieldDescriptorProto
        {
            Name = "ages",
            Number = 2,
            Type = FieldDescriptorProto.Types.Type.Sint32,
            Label = FieldDescriptorProto.Types.Label.Repeated, // packed LEN block, not a scalar varint
        });
        var file = new FileDescriptorProto { Name = "test.proto", Syntax = "proto3" };
        file.MessageType.Add(message);
        var set = new FileDescriptorSet();
        set.File.Add(file);

        var result = ProtobufTagCompatibilityChecker.Check(record, set, "Person");

        result.IsCompatible.Should().BeFalse(
            "a repeated external field changes the wire framing under the shared tag; a scalar record reader mis-frames it");
        result.Reasons.Should().Contain(r => r.Tag == 2);
    }
}
