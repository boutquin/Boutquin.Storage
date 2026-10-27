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

// Nested generated records whose simple names spell proto scalar keywords (Long, String).
public static partial class ProtoNestedScalarNamed
{
    [StorageSerializable]
    public partial record struct Long([property: Tag(1)] int A);

    [StorageSerializable]
    public partial record struct String([property: Tag(1)] int A);

    [StorageSerializable]
    public partial record struct Child([property: Tag(1)] int A);

    [StorageSerializable]
    public partial record struct LongParent([property: Tag(1)] Long Inner);

    [StorageSerializable]
    public partial record struct StringParent([property: Tag(1)] String Inner);

    [StorageSerializable]
    public partial record struct ChildParent([property: Tag(1)] Child Inner);
}

public static partial class ProtoNestedKeyNamed
{
    [Key]
    public partial record struct Long([property: Tag(1)] int A);

    [StorageSerializable]
    public partial record struct Parent([property: Tag(1)] Long Inner);
}

/// <summary>
/// Contract tests for the wire-tag writer's wire-type validation and for the cross-format
/// Protobuf checker's classification of nested-record fields.
/// </summary>
public sealed class WireTagAndProtobufCompatibilityContractTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(255)]
    public void WriteTag_UndefinedWireType_IsRejectedAndWritesNothing(int wire)
    {
        using var ms = new MemoryStream();

        var act = () => VarintHelpers.WriteTag(ms, 2, (WireType)wire);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("wireType");
        ms.Length.Should().Be(0);
    }

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

    [Fact]
    public void NestedRecordNamedLong_VsProtoSint64_IsIncompatible()
    {
        var record = TaggedWireSchema.FromType<ProtoNestedScalarNamed.LongParent>();
        var result = ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.Sint64), "M");
        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 1);
    }

    [Fact]
    public void NestedRecordNamedString_VsProtoString_IsIncompatible()
    {
        var record = TaggedWireSchema.FromType<ProtoNestedScalarNamed.StringParent>();
        var result = ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.String), "M");
        result.IsCompatible.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Tag == 1);
    }

    [Fact]
    public void NestedKeyRecordNamedLong_VsProtoSint64_IsIncompatible()
    {
        var record = TaggedWireSchema.FromType<ProtoNestedKeyNamed.Parent>();
        var result = ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.Sint64), "M");
        result.IsCompatible.Should().BeFalse();
    }

    [Fact]
    public void NestedRecord_VsProtoScalar_StaysIncompatible_Control()
    {
        var record = TaggedWireSchema.FromType<ProtoNestedScalarNamed.ChildParent>();
        var result = ProtobufTagCompatibilityChecker.Check(record, External(FieldDescriptorProto.Types.Type.Sint64), "M");
        result.IsCompatible.Should().BeFalse();
    }
}
