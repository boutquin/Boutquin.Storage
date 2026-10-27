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

namespace Boutquin.Storage.Infrastructure.Tests;

// A record can only refer to itself directly as a class, which the generator flags (BSSG003, not
// configurable). FromType reads only the static descriptor members, so hand-written types that
// expose them stand in for the generated shape a self-referencing record has.
public static class RecursiveEvolutionV1
{
    // Names spell integer keywords on purpose: a name-derived class would treat tag 2 as a scalar.
    public sealed class Long
    {
        public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; } =
        [
            new(1, nameof(A), "int", false),
            new(2, nameof(Next), "RecursiveEvolutionV1.Long?", true),
        ];

        public int A { get; init; }

        public Long? Next { get; init; }
    }

    public sealed class Node
    {
        public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; } =
        [
            new(1, nameof(A), "int", false),
            new(2, nameof(Next), "RecursiveEvolutionV1.Node?", true),
        ];

        public int A { get; init; }

        public Node? Next { get; init; }
    }
}

// Both writers name the root and the inner record "Child" (namespaces differ, so the declared-name
// gate cannot tell them apart). In V1 the inner Child.Next refers to the inner Child itself; in V2 it
// refers back to the root, a record of a different shape.
public static class BackReferenceTargetV1
{
    public static class Outer
    {
        public sealed class Child
        {
            public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; } =
            [
                new(1, nameof(Name), "string", false),
                new(2, nameof(C), "BackReferenceTargetV1.Child?", true),
            ];

            public string Name { get; init; } = string.Empty;

            public BackReferenceTargetV1.Child? C { get; init; }
        }
    }

    public sealed class Child
    {
        public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; } =
        [
            new(1, nameof(X), "int", false),
            new(2, nameof(Next), "BackReferenceTargetV1.Child?", true),
        ];

        public int X { get; init; }

        public Child? Next { get; init; }
    }
}

public static class BackReferenceTargetV2
{
    public static class Outer
    {
        public sealed class Child
        {
            public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; } =
            [
                new(1, nameof(Name), "string", false),
                new(2, nameof(C), "BackReferenceTargetV2.Child?", true),
            ];

            public string Name { get; init; } = string.Empty;

            public BackReferenceTargetV2.Child? C { get; init; }
        }
    }

    public sealed class Child
    {
        public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; } =
        [
            new(1, nameof(X), "int", false),
            new(2, nameof(Next), "BackReferenceTargetV2.Outer.Child?", true),
        ];

        public int X { get; init; }

        public Outer.Child? Next { get; init; }
    }
}

public static class RecursiveEvolutionV2
{
    // Tag 2 stops being the record itself (length-prefixed) and becomes a plain varint.
    public sealed class Long
    {
        public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; } =
        [
            new(1, nameof(A), "int", false),
            new(2, nameof(Next), "long?", true),
        ];

        public int A { get; init; }

        public long? Next { get; init; }
    }

    public sealed class Node
    {
        public static IReadOnlyList<TaggedWireField> TaggedWireSchemaFields { get; } =
        [
            new(1, nameof(A), "int", false),
            new(2, nameof(Next), "long?", true),
        ];

        public int A { get; init; }

        public long? Next { get; init; }
    }
}

// Enums whose declared names spell proto scalar keywords while the bytes they write are their
// underlying integer.
public static partial class EnumNamedLikeProtoScalars
{
    public enum UInt { None = 0, Two = 2 }

    public enum String { None = 0, Two = 2 }

    public enum Long : ulong { None = 0, Two = 2 }

    public enum Status : ulong { None = 0, Two = 2 }

    [StorageSerializable]
    public partial record struct UIntHolder([property: Tag(1)] UInt B);

    [StorageSerializable]
    public partial record struct StringHolder([property: Tag(1)] String B);

    [StorageSerializable]
    public partial record struct LongHolder([property: Tag(1)] Long B);

    [StorageSerializable]
    public partial record struct StatusHolder([property: Tag(1)] Status B);
}

/// <summary>
/// A self-referencing nested record and an enum named like a scalar keyword must be classified by
/// what is written on the wire, not by the declared name, in both the in-family checker and the
/// Protobuf bridge.
/// </summary>
public sealed class TaggedWireRecursiveRecordAndEnumCompatibilityTests
{
    private static readonly TaggedWireCompatibilityChecker s_checker = new();

    private static FileDescriptorSet External(FieldDescriptorProto.Types.Type type)
    {
        var message = new DescriptorProto { Name = "M" };
        message.Field.Add(new FieldDescriptorProto
        {
            Name = "f",
            Number = 1,
            Type = type,
            Label = FieldDescriptorProto.Types.Label.Optional,
        });
        var file = new FileDescriptorProto { Name = "t.proto", Syntax = "proto3" };
        file.MessageType.Add(message);
        var set = new FileDescriptorSet();
        set.File.Add(file);
        return set;
    }

    private static FileDescriptorSet ExternalAtTag(int tag, FieldDescriptorProto.Types.Type type)
    {
        var set = External(type);
        set.File[0].MessageType[0].Field[0].Number = tag;
        return set;
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void RecursiveRecordNamedLong_ChangedToScalar_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<RecursiveEvolutionV1.Long>();
        var v2 = TaggedWireSchema.FromType<RecursiveEvolutionV2.Long>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse("tag 2 was the record itself (length-prefixed) and became a varint");
        s_checker.IsCompatible(v2, v1, mode).Should().BeFalse("the reverse change is equally a wire-type change");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void RecursiveRecordNamedNode_ChangedToScalar_IsIncompatible_Control(CompatibilityMode mode)
        => s_checker
            .IsCompatible(TaggedWireSchema.FromType<RecursiveEvolutionV1.Node>(), TaggedWireSchema.FromType<RecursiveEvolutionV2.Node>(), mode)
            .Should().BeFalse();

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void RecursiveRecord_AgainstItself_StaysCompatible(CompatibilityMode mode)
        => s_checker
            .IsCompatible(TaggedWireSchema.FromType<RecursiveEvolutionV1.Long>(), TaggedWireSchema.FromType<RecursiveEvolutionV1.Long>(), mode)
            .Should().BeTrue();

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void BackReferenceRetargetedToAnotherRecordOfTheSameName_IsIncompatible(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<BackReferenceTargetV1.Outer.Child>();
        var v2 = TaggedWireSchema.FromType<BackReferenceTargetV2.Outer.Child>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "the inner back-reference moved from the inner record to the root; old bytes decode as the wrong record");
        s_checker.IsCompatible(v2, v1, mode).Should().BeFalse("the reverse retarget is the same change");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void BackReferenceToTheSameTarget_StaysCompatible_Control(CompatibilityMode mode)
        => s_checker
            .IsCompatible(TaggedWireSchema.FromType<BackReferenceTargetV1.Outer.Child>(), TaggedWireSchema.FromType<BackReferenceTargetV1.Outer.Child>(), mode)
            .Should().BeTrue();

    [Fact]
    public void RecursiveRecord_ReportsItsBackReferenceTag()
    {
        TaggedWireSchema.FromType<RecursiveEvolutionV1.Long>().RecursiveTags.Should().BeEquivalentTo(new Dictionary<int, int> { [2] = 0 });
        TaggedWireSchema.FromType<RecursiveEvolutionV2.Long>().RecursiveTags.Should().BeEmpty();
    }

    [Fact]
    public void RecursiveRecordNamedLong_VsProtoSint64_IsIncompatible()
    {
        var record = TaggedWireSchema.FromType<RecursiveEvolutionV1.Long>();

        var result = ProtobufTagCompatibilityChecker.Check(record, ExternalAtTag(2, FieldDescriptorProto.Types.Type.Sint64), "M");

        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 2);
    }

    [Fact]
    public void EnumNamedUInt_WrittenAsInt_VsProtoUint64_IsIncompatible()
    {
        var record = TaggedWireSchema.FromType<EnumNamedLikeProtoScalars.UIntHolder>();

        var result = ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.Uint64), "M");

        result.IsCompatible.Should().BeFalse("the enum writes a zig-zag int; a uint64 reader would decode 4 for 2");
    }

    [Fact]
    public void EnumNamedString_WrittenAsInt_VsProtoString_IsIncompatible()
    {
        var record = TaggedWireSchema.FromType<EnumNamedLikeProtoScalars.StringHolder>();

        ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.String), "M")
            .IsCompatible.Should().BeFalse("the enum is a varint, proto string is length-prefixed");
    }

    [Fact]
    public void EnumNamedLong_WrittenAsUlong_ClassifiedByItsEncodedType()
    {
        var record = TaggedWireSchema.FromType<EnumNamedLikeProtoScalars.LongHolder>();

        ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.Sint64), "M")
            .IsCompatible.Should().BeFalse("a plain-varint ulong is not a zig-zag sint64");
        ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.Uint64), "M")
            .IsCompatible.Should().BeTrue("the bytes are exactly a plain uint64 varint");
    }

    [Fact]
    public void EnumNamedStatus_WrittenAsUlong_VsProtoSint64_IsIncompatible_Control()
    {
        var record = TaggedWireSchema.FromType<EnumNamedLikeProtoScalars.StatusHolder>();

        ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.Sint64), "M")
            .IsCompatible.Should().BeFalse();
    }
}
