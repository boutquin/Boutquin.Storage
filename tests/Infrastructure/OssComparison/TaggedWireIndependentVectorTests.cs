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
using Google.Protobuf;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

[StorageSerializable]
public partial record struct ReadinessWireOld(
    [property: Tag(1)] long Number,
    [property: Tag(2)] string Text,
    [property: Tag(3)] IEnumerable<int> Values);

[StorageSerializable]
public partial record struct ReadinessWireNew(
    [property: Tag(1)] long Number,
    [property: Tag(2)] string Text,
    [property: Tag(3)] IEnumerable<int> Values,
    [property: Tag(4)] int? Optional,
    [property: Tag(5)] ReadinessWireChild Child);

[StorageSerializable]
public partial record struct ReadinessWireChild([property: Tag(1)] int Value);

public sealed class TaggedWireIndependentVectorTests
{
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(127L)]
    [InlineData(128L)]
    public void ScalarVarintAndGeneratedFrame_AgreeWithGoogleProtobufSignedIntegerBodies(long value)
    {
        using var protobufBytes = new MemoryStream();
        using (var protobuf = new CodedOutputStream(protobufBytes, leaveOpen: true))
        {
            protobuf.WriteSInt64(value);
        }
        using var handwritten = new MemoryStream();
        VarintHelpers.WriteZigZagInt64(handwritten, value);
        handwritten.ToArray().Should().Equal(protobufBytes.ToArray());
        handwritten.Position = 0;
        VarintHelpers.ReadZigZagInt64(handwritten).Should().Be(value);
        var wire = new ReadinessWireOld(value, "é\0東京", [-1, 0, 128]);
        using var generated = new MemoryStream();
        wire.Serialize(generated);
        // Fixed literal tags are repository-specific. Only the scalar body is Protobuf-compatible.
        var text = Encoding.UTF8.GetBytes(wire.Text);
        byte[] payload = [0, 1, 8, .. protobufBytes.ToArray(), 20, (byte)text.Length, .. text,
            28, 4, 1, 0, 128, 2];
        var expected = Frame(payload);
        generated.ToArray().Should().Equal(expected);
        using var input = new MemoryStream(expected, false);
        var decoded = ReadinessWireOld.Deserialize(input);
        decoded.Number.Should().Be(value);
        decoded.Text.Should().Be(wire.Text);
        decoded.Values.Should().Equal(-1, 0, 128);
    }

    [Fact]
    public void ReorderedMixedPackedAndUnpackedFields_AndUnknownWireTypes_PreserveOldAndNewReaders()
    {
        // Independently authored tags: samples appear as unpacked -1, packed [0,128], then
        // unpacked -2. Unknown fields exercise all six legal wire types, including nested LEN.
        byte[] payload = [0, 1,
            24, 1, 28, 3, 0, 128, 2, 24, 3,
            20, 2, 104, 105,
            8, 13,
            32, 84,
            44, 4, 0, 1, 8, 18,
            80, 150, 1,
            89, 1, 2,
            98, 1, 2, 3, 4,
            107, 1, 2, 3, 4, 5, 6, 7, 8,
            116, 3, 9, 8, 7,
            125, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        var frame = Frame(payload);
        using var oldInput = new MemoryStream(frame, false);
        using var newInput = new MemoryStream(frame, false);
        var old = ReadinessWireOld.Deserialize(oldInput);
        var newer = ReadinessWireNew.Deserialize(newInput);
        old.Number.Should().Be(-7);
        newer.Number.Should().Be(-7);
        old.Text.Should().Be("hi");
        newer.Text.Should().Be("hi");
        old.Values.Should().Equal(-1, 0, 128, -2);
        newer.Values.Should().Equal(-1, 0, 128, -2);
        newer.Optional.Should().Be(42);
        newer.Child.Value.Should().Be(9);
        oldInput.Position.Should().Be(frame.Length);
        newInput.Position.Should().Be(frame.Length);
    }

    [Fact]
    public void MissingFields_ProduceDefaultsAndFutureFieldsRemainSkippable()
    {
        using var input = new MemoryStream(Frame([0, 1, 8, 2]), false);
        var decoded = ReadinessWireNew.Deserialize(input);
        decoded.Number.Should().Be(1);
        decoded.Text.Should().BeEmpty();
        decoded.Values.Should().BeEmpty();
        decoded.Optional.Should().BeNull();
        decoded.Child.Should().Be(default(ReadinessWireChild));
        using var newerBytes = new MemoryStream();
        new ReadinessWireNew(9, "new", [1, 2], 7, new ReadinessWireChild(3)).Serialize(newerBytes);
        newerBytes.Position = 0;
        var old = ReadinessWireOld.Deserialize(newerBytes);
        old.Number.Should().Be(9);
        old.Text.Should().Be("new");
        old.Values.Should().Equal(1, 2);
    }

    [Theory]
    [InlineData("00010880")]
    [InlineData("00011405C3A9")]
    [InlineData("00011C0180")]
    [InlineData("00012C04000108")]
    [InlineData("00017D000102")]
    public void TruncatedScalarPackedNestedAndUnknownFixedFields_AreRejected(string hex)
    {
        using var input = new MemoryStream(Frame(Convert.FromHexString(hex)), false);
        Action decode = () => ReadinessWireNew.Deserialize(input);
        decode.Should().Throw<Exception>().Where(error => error.GetType() == typeof(InvalidDataException) || error.GetType() == typeof(EndOfStreamException));
    }

    private static byte[] Frame(byte[] payload)
    {
        var length = (uint)payload.Length;
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(length & 127);
            length >>= 7;
            bytes.Add(length == 0 ? next : (byte)(next | 128));
        } while (length != 0);
        bytes.AddRange(payload);
        return bytes.ToArray();
    }
}
