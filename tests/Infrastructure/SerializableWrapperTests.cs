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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the SerializableWrapper class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SerializableWrapperTests
{
    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle int values.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleInt()
    {
        // Arrange: Create a SerializableWrapper with an int value and a memory stream.
        var wrapper = new SerializableWrapper<int>(42);
        using var memoryStream = new MemoryStream();

        // Act: Serialize and then Deserialize the value.
        wrapper.Serialize(memoryStream);
        memoryStream.Position = 0; // Reset stream position to the beginning.
        var deserializedWrapper = SerializableWrapper<int>.Deserialize(memoryStream);

        // Assert: Check that the deserialized value is equal to the original value.
        Assert.Equal(wrapper.Value, deserializedWrapper.Value);
    }

    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle string values.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleString()
    {
        // Arrange: Create a SerializableWrapper with a string value and a memory stream.
        var wrapper = new SerializableWrapper<string>("test");
        using var memoryStream = new MemoryStream();

        // Act: Serialize and then Deserialize the value.
        wrapper.Serialize(memoryStream);
        memoryStream.Position = 0; // Reset stream position to the beginning.
        var deserializedWrapper = SerializableWrapper<string>.Deserialize(memoryStream);

        // Assert: Check that the deserialized value is equal to the original value.
        Assert.Equal(wrapper.Value, deserializedWrapper.Value);
    }

    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle long values.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleLong()
    {
        // Arrange: Create a SerializableWrapper with a long value and a memory stream.
        var wrapper = new SerializableWrapper<long>(1234567890123456789L);
        using var memoryStream = new MemoryStream();

        // Act: Serialize and then Deserialize the value.
        wrapper.Serialize(memoryStream);
        memoryStream.Position = 0; // Reset stream position to the beginning.
        var deserializedWrapper = SerializableWrapper<long>.Deserialize(memoryStream);

        // Assert: Check that the deserialized value is equal to the original value.
        Assert.Equal(wrapper.Value, deserializedWrapper.Value);
    }

    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle float values.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleFloat()
    {
        // Arrange: Create a SerializableWrapper with a float value and a memory stream.
        var wrapper = new SerializableWrapper<float>(3.14f);
        using var memoryStream = new MemoryStream();

        // Act: Serialize and then Deserialize the value.
        wrapper.Serialize(memoryStream);
        memoryStream.Position = 0; // Reset stream position to the beginning.
        var deserializedWrapper = SerializableWrapper<float>.Deserialize(memoryStream);

        // Assert: Check that the deserialized value is equal to the original value.
        Assert.Equal(wrapper.Value, deserializedWrapper.Value);
    }

    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle double values.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleDouble()
    {
        // Arrange: Create a SerializableWrapper with a double value and a memory stream.
        var wrapper = new SerializableWrapper<double>(3.14159265359);
        using var memoryStream = new MemoryStream();

        // Act: Serialize and then Deserialize the value.
        wrapper.Serialize(memoryStream);
        memoryStream.Position = 0; // Reset stream position to the beginning.
        var deserializedWrapper = SerializableWrapper<double>.Deserialize(memoryStream);

        // Assert: Check that the deserialized value is equal to the original value.
        Assert.Equal(wrapper.Value, deserializedWrapper.Value);
    }

    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle bool values.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleBool()
    {
        // Arrange: Create a SerializableWrapper with a bool value and a memory stream.
        var wrapper = new SerializableWrapper<bool>(true);
        using var memoryStream = new MemoryStream();

        // Act: Serialize and then Deserialize the value.
        wrapper.Serialize(memoryStream);
        memoryStream.Position = 0; // Reset stream position to the beginning.
        var deserializedWrapper = SerializableWrapper<bool>.Deserialize(memoryStream);

        // Assert: Check that the deserialized value is equal to the original value.
        Assert.Equal(wrapper.Value, deserializedWrapper.Value);
    }

    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle byte values.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleByte()
    {
        // Arrange: Create a SerializableWrapper with a byte value and a memory stream.
        var wrapper = new SerializableWrapper<byte>(0x42);
        using var memoryStream = new MemoryStream();

        // Act: Serialize and then Deserialize the value.
        wrapper.Serialize(memoryStream);
        memoryStream.Position = 0; // Reset stream position to the beginning.
        var deserializedWrapper = SerializableWrapper<byte>.Deserialize(memoryStream);

        // Assert: Check that the deserialized value is equal to the original value.
        Assert.Equal(wrapper.Value, deserializedWrapper.Value);
    }

    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle char values.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleChar()
    {
        // Arrange: Create a SerializableWrapper with a char value and a memory stream.
        var wrapper = new SerializableWrapper<char>('A');
        using var memoryStream = new MemoryStream();

        // Act: Serialize and then Deserialize the value.
        wrapper.Serialize(memoryStream);
        memoryStream.Position = 0; // Reset stream position to the beginning.
        var deserializedWrapper = SerializableWrapper<char>.Deserialize(memoryStream);

        // Assert: Check that the deserialized value is equal to the original value.
        Assert.Equal(wrapper.Value, deserializedWrapper.Value);
    }

    /// <summary>
    /// Test to ensure that the Serialize method throws a NotSupportedException for unsupported types.
    /// </summary>
    [Fact]
    public void Serialize_ShouldThrowNotSupportedExceptionForUnsupportedTypes()
    {
        // Arrange: Create a SerializableWrapper with an unsupported type (DateTime).
        var wrapper = new SerializableWrapper<DateTime>(DateTime.Now);
        using var memoryStream = new MemoryStream();

        // Act & Assert: Check that a NotSupportedException is thrown during serialization.
        Assert.Throws<NotSupportedException>(() => wrapper.Serialize(memoryStream));
    }

    /// <summary>
    /// Test to ensure that the CompareTo method correctly compares two SerializableWrapper objects with int values.
    /// </summary>
    [Fact]
    public void CompareTo_ShouldCorrectlyCompareIntValues()
    {
        // Arrange: Create two SerializableWrapper objects with int values.
        var wrapper1 = new SerializableWrapper<int>(42);
        var wrapper2 = new SerializableWrapper<int>(43);

        // Act & Assert: Check that the comparison is correct.
        Assert.True(wrapper1.CompareTo(wrapper2) < 0);
        Assert.True(wrapper2.CompareTo(wrapper1) > 0);
        Assert.Equal(0, wrapper1.CompareTo(wrapper1));
    }

    /// <summary>
    /// Test to ensure that the Equals method correctly compares two SerializableWrapper objects with int values.
    /// </summary>
    [Fact]
    public void Equals_ShouldCorrectlyCompareIntValues()
    {
        // Arrange: Create two SerializableWrapper objects with int values.
        var wrapper1 = new SerializableWrapper<int>(42);
        var wrapper2 = new SerializableWrapper<int>(42);
        var wrapper3 = new SerializableWrapper<int>(43);

        // Act & Assert: Check that the equality comparison is correct.
        Assert.True(wrapper1.Equals(wrapper2));
        Assert.False(wrapper1.Equals(wrapper3));
        Assert.True(wrapper1.Equals(wrapper1));
    }

    /// <summary>
    /// Test to ensure that the GetHashCode method returns the correct hash code for the wrapped value.
    /// </summary>
    [Fact]
    public void GetHashCode_ShouldReturnCorrectHashCode()
    {
        // Arrange: Create a SerializableWrapper with an int value.
        var wrapper = new SerializableWrapper<int>(42);

        // Act: Get the hash code of the wrapped value.
        var hashCode = wrapper.GetHashCode();

        // Assert: Check that the hash code is correct.
        Assert.Equal(42.GetHashCode(), hashCode);
    }

    /// <summary>
    /// Test to ensure that the implicit conversion operators work correctly for int values.
    /// </summary>
    [Fact]
    public void ImplicitConversion_ShouldWorkForIntValues()
    {
        // Arrange: Create an int value.
        var originalValue = 42;

        // Act: Implicitly convert the int value to SerializableWrapper and back to int.
        SerializableWrapper<int> wrapper = originalValue;
        int convertedValue = wrapper;

        // Assert: Check that the converted value is equal to the original value.
        Assert.Equal(originalValue, convertedValue);
    }

    /// <summary>
    /// Test to ensure that the ToString method returns the correct string representation of the wrapped value.
    /// </summary>
    [Fact]
    public void ToString_ShouldReturnCorrectStringRepresentation()
    {
        // Arrange: Create a SerializableWrapper with an int value.
        var wrapper = new SerializableWrapper<int>(42);

        // Act: Get the string representation of the wrapped value.
        var stringValue = wrapper.ToString();

        // Assert: Check that the string representation is correct.
        Assert.Equal("42", stringValue);
    }

    /// <summary>
    /// Test to ensure that the Serialize and Deserialize methods correctly handle a complex type.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ShouldHandleComplexType()
    {
        // Arrange: Create a SerializableWrapper with a KeyValuePair as the complex type.
        var complexValue = new KeyValuePair<int, string>(1, "one");
        var wrapper = new SerializableWrapper<KeyValuePair<int, string>>(complexValue);
        using var memoryStream = new MemoryStream();

        // Act & Assert: Check that a NotSupportedException is thrown during serialization.
        Assert.Throws<NotSupportedException>(() => wrapper.Serialize(memoryStream));
    }

    /// <summary>
    /// Pins the 2.0.0 tagged-wire schema-format marker. Every serialized wrapper payload must
    /// open with tag 0 + schema-format version 1, matching the generator-emitted frame for
    /// [StorageSerializable] records. A future wrapper-format bump surfaces here at review time.
    /// </summary>
    [Fact]
    public void Serialize_PinsTaggedWire_TagZero_SchemaFormatVersionOne()
    {
        // Arrange: any supported type round-trips through the same frame shape.
        var wrapper = new SerializableWrapper<int>(42);
        using var stream = new MemoryStream();

        // Act
        wrapper.Serialize(stream);
        stream.Position = 0;

        // Assert: the ULEB128 length prefix frames the record, and the first two
        // payload bytes are the combined (tag=0, wire=Varint) varint (0x00) and the
        // schema-format version varint (0x01). This matches TaggedWireEmitter.cs:54-57.
        _ = VarintHelpers.ReadVarUInt64(stream);
        Assert.Equal(0x00, stream.ReadByte());
        Assert.Equal(0x01, stream.ReadByte());
    }

    // ── CompareTo, Equals, GetHashCode, ToString ─────────────────────────────

    [Fact]
    public void CompareTo_NullOther_ReturnsPositive()
    {
        var a = new SerializableWrapper<int>(1);
        a.CompareTo(null).Should().BeGreaterThan(0);
    }

    [Fact]
    public void CompareTo_LesserAndGreater_OrdersByValue()
    {
        var a = new SerializableWrapper<int>(1);
        var b = new SerializableWrapper<int>(2);
        a.CompareTo(b).Should().BeLessThan(0);
        b.CompareTo(a).Should().BeGreaterThan(0);
        a.CompareTo(new SerializableWrapper<int>(1)).Should().Be(0);
    }

    [Fact]
    public void Equals_NullOther_ReturnsFalse()
    {
        var a = new SerializableWrapper<int>(1);
        a.Equals((SerializableWrapper<int>?)null).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentRuntimeType_ReturnsFalse()
    {
        var a = new SerializableWrapper<int>(1);
        a.Equals((object)"not-a-wrapper").Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_SameValue_ProducesSameHash()
    {
        var a = new SerializableWrapper<string>("hello");
        var b = new SerializableWrapper<string>("hello");
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    // ── Deserialize error / format-version paths ─────────────────────────────

    [Fact]
    public void Deserialize_MissingTagZeroSchemaVersion_Throws()
    {
        // Build a frame with payload that opens with a non-zero tag.
        using var payload = new MemoryStream();
        VarintHelpers.WriteTag(payload, tagNumber: 1, WireType.Varint);
        VarintHelpers.WriteVarUInt64(payload, 99);
        var payloadBytes = payload.ToArray();

        using var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, (ulong)payloadBytes.Length);
        framed.Write(payloadBytes);
        framed.Position = 0;

        Action act = () => SerializableWrapper<int>.Deserialize(framed);
        act.Should().Throw<UnsupportedFormatVersionException>();
    }

    [Fact]
    public void Deserialize_UnsupportedSchemaVersion_Throws()
    {
        // Build a frame that has tag 0 but version != 1.
        using var payload = new MemoryStream();
        // Tag 0 + WireType.Varint encodes as the single byte 0x00.
        payload.WriteByte(0x00);
        VarintHelpers.WriteVarUInt64(payload, 999);
        var payloadBytes = payload.ToArray();

        using var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, (ulong)payloadBytes.Length);
        framed.Write(payloadBytes);
        framed.Position = 0;

        Action act = () => SerializableWrapper<int>.Deserialize(framed);
        act.Should().Throw<UnsupportedFormatVersionException>();
    }

    [Fact]
    public void Read_UnrecognizedFormatVersion_ThrowsInvalidDataException()
    {
        // Craft a frame that opens with the correct tag-0 header position but carries an
        // unrecognized schema-format version (255).  SerializableWrapper<T>.Deserialize reads
        // the tag-0 varint first, then the version varint; a version != 1 surfaces as
        // UnsupportedFormatVersionException.
        using var payload = new MemoryStream();
        // Tag 0 + WireType.Varint encodes as the single byte 0x00.
        payload.WriteByte(0x00);
        VarintHelpers.WriteVarUInt64(payload, 255UL);
        var payloadBytes = payload.ToArray();

        using var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, (ulong)payloadBytes.Length);
        framed.Write(payloadBytes);
        framed.Position = 0;

        Action act = () => SerializableWrapper<int>.Deserialize(framed);
        // Version 255 is unrecognized; SerializableWrapper<T> throws UnsupportedFormatVersionException.
        act.Should().Throw<UnsupportedFormatVersionException>();
    }

    [Fact]
    public void Deserialize_PayloadMissingValueTag_Throws()
    {
        // Frame with tag 0, version 1, but no value tag follows.
        using var payload = new MemoryStream();
        // Tag 0 + WireType.Varint encodes as the single byte 0x00.
        payload.WriteByte(0x00);
        VarintHelpers.WriteVarUInt64(payload, 1);
        var payloadBytes = payload.ToArray();

        using var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, (ulong)payloadBytes.Length);
        framed.Write(payloadBytes);
        framed.Position = 0;

        Action act = () => SerializableWrapper<int>.Deserialize(framed);
        act.Should().Throw<InvalidDataException>().WithMessage("*missing tag*");
    }

    [Fact]
    public void Deserialize_UnknownTagSkipsForwardCompatibility()
    {
        // Frame with tag 0, version 1, an unknown tag (skipped), then the value tag.
        using var payload = new MemoryStream();
        // Tag 0 + WireType.Varint encodes as the single byte 0x00.
        payload.WriteByte(0x00);
        VarintHelpers.WriteVarUInt64(payload, 1);
        // Unknown future tag (varint)
        VarintHelpers.WriteTag(payload, tagNumber: 99, WireType.Varint);
        VarintHelpers.WriteVarUInt64(payload, 12345);
        // The actual value tag = 1, wire-type Varint, zigzag value 7.
        VarintHelpers.WriteTag(payload, tagNumber: 1, WireType.Varint);
        VarintHelpers.WriteZigZagInt64(payload, 7);
        var payloadBytes = payload.ToArray();

        using var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, (ulong)payloadBytes.Length);
        framed.Write(payloadBytes);
        framed.Position = 0;

        var w = SerializableWrapper<int>.Deserialize(framed);
        w.Value.Should().Be(7);
    }

    [Fact]
    public void Deserialize_WrongWireType_Throws()
    {
        // tag 0 (varint), version 1, value tag with Fixed64 wire type but T is int (expects Varint).
        using var payload = new MemoryStream();
        // Tag 0 + WireType.Varint encodes as the single byte 0x00.
        payload.WriteByte(0x00);
        VarintHelpers.WriteVarUInt64(payload, 1);
        VarintHelpers.WriteTag(payload, tagNumber: 1, WireType.Fixed64);
        Span<byte> eight = stackalloc byte[8];
        payload.Write(eight);
        var payloadBytes = payload.ToArray();

        using var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, (ulong)payloadBytes.Length);
        framed.Write(payloadBytes);
        framed.Position = 0;

        Action act = () => SerializableWrapper<int>.Deserialize(framed);
        act.Should().Throw<InvalidDataException>().WithMessage("*wire type*");
    }

    // The writer emits only type ids 1..8 at schema-format version 1, so any other persisted id
    // is corrupt or foreign input and must surface the same typed corruption contract as the
    // sibling wrong-wire-type, out-of-range and not-assignable arms.
    [Theory]
    [InlineData(9UL)]
    [InlineData(200UL)]
    [InlineData(ulong.MaxValue)]
    public void Deserialize_UnknownPersistedTypeId_ThrowsInvalidDataException(ulong forgedTypeId)
    {
        var framed = FrameWithTypeId(forgedTypeId);

        Action asInt = () => SerializableWrapper<int>.Deserialize(new MemoryStream(framed));
        Action asInterface = () => SerializableWrapper<IComparable>.Deserialize(new MemoryStream(framed));

        asInt.Should().Throw<InvalidDataException>().WithMessage($"*type id {forgedTypeId}*");
        asInterface.Should().Throw<InvalidDataException>().WithMessage($"*type id {forgedTypeId}*");
    }

    // A frame without the type-id tag cannot name the CLR type when T is an interface; the writer
    // always emits that tag, so its absence is corruption, not an unsupported caller type.
    [Fact]
    public void Deserialize_InterfaceTargetFrameMissingTypeId_ThrowsInvalidDataException()
    {
        var framed = FrameWithTypeId(typeId: null);

        Action act = () => SerializableWrapper<IComparable>.Deserialize(new MemoryStream(framed));

        act.Should().Throw<InvalidDataException>().WithMessage("*type id*");
    }

    private static byte[] FrameWithTypeId(ulong? typeId)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(0x00); // tag 0, wire type Varint
        VarintHelpers.WriteVarUInt64(payload, 1); // schema-format version 1
        if (typeId is { } id)
        {
            VarintHelpers.WriteTag(payload, tagNumber: 2, WireType.Varint);
            VarintHelpers.WriteVarUInt64(payload, id);
        }

        VarintHelpers.WriteTag(payload, tagNumber: 1, WireType.Varint);
        VarintHelpers.WriteZigZagInt64(payload, 5);
        var payloadBytes = payload.ToArray();

        using var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, (ulong)payloadBytes.Length);
        framed.Write(payloadBytes);
        return framed.ToArray();
    }

    /// <summary>
    /// Pins the oversized-payload guard: a seekable frame that declares a payload length far
    /// exceeding the actual remaining bytes must throw at the bound-check site — before
    /// allocating the buffer.  Mirrors the <c>ReadString</c> sibling guard in
    /// <c>VarintHelpers</c>.
    ///
    /// M-1 (F-11) unifies the length-prefix corruption path on the repo's typed corruption
    /// contract: a forged length prefix surfaces <see cref="InvalidDataException"/>, not the raw
    /// <see cref="EndOfStreamException"/> the bound previously leaked.
    ///
    /// Without the guard, Deserialize calls <c>new byte[payloadLength]</c> on corruption-
    /// controlled data; a 5-byte frame can request a ~2 GB allocation that exhausts heap
    /// before the read observes the short stream.  The test uses 1 000 000 bytes declared
    /// over 1 real byte — large enough to prove the guard fires, small enough not to OOM
    /// the test runner.
    /// </summary>
    [Fact]
    public void Deserialize_OversizedPayloadLength_ThrowsBeforeAllocating()
    {
        // Build a seekable MemoryStream whose ULEB128 length prefix declares 1,000,000 bytes
        // but whose actual content is only a few bytes.  Without the bound guard the Deserialize
        // path calls new byte[1_000_000] on an obviously corrupt frame.
        using var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, 1_000_000UL); // declared length >> actual bytes
        framed.WriteByte(0x01);                             // one junk byte so the stream is not empty
        framed.Position = 0;

        // The guard (stream.CanSeek && payloadLength > remaining) must fire with the typed
        // corruption exception.  Mutation-proof: we verify it fires at the Deserialize call site
        // (the "Declared payload length" message) rather than deeper in the bounded read.
        Action act = () => SerializableWrapper<int>.Deserialize(framed);
        act.Should().Throw<InvalidDataException>()
           .WithMessage("*Declared payload length*");
    }

    /// <summary>
    /// Exercises the write/read asymmetry between runtime-type dispatch (write) and
    /// static-type dispatch (read) when T is an interface type.
    /// SerializableWrapper&lt;IComparable&gt; holding an <c>int</c> value:
    ///   Serialize succeeds  — WriteValuePayload switches on the runtime type (int).
    ///   Deserialize must also succeed — ReadValuePayload must dispatch on the runtime
    ///   type encoded in the wire stream, not on typeof(T) == typeof(IComparable).
    /// </summary>
    [Fact]
    public void RoundTrip_InterfaceType_WriteThenRead_ReturnsOriginalValue()
    {
        // Arrange: wrap an int through the IComparable interface.
        // WriteValuePayload dispatches on the runtime type (int) via switch(Value),
        // so the write succeeds. The pre-fix ReadValuePayload dispatches on typeof(T)
        // == typeof(IComparable) and falls through to NotSupportedException — the bug.
        var wrapper = new SerializableWrapper<IComparable>(42);
        using var stream = new MemoryStream();

        // Act
        wrapper.Serialize(stream);
        stream.Position = 0;
        var result = SerializableWrapper<IComparable>.Deserialize(stream);

        // Assert: the deserialized value must equal the original int.
        result.Value.Should().Be(42);
    }

    // ── C5-5: SerializableWrapper format-version contracts ────────────────────────────────
    // The Deserialize path checks the tag-0 schema-format version byte. An unrecognized version
    // (any value other than 1) must surface UnsupportedFormatVersionException so callers get the
    // typed corruption signal rather than a raw low-level exception.

    /// <summary>
    /// A buffer whose tag-0 carries format version 99 (unrecognized) must surface
    /// <see cref="UnsupportedFormatVersionException"/> — the typed corruption contract.
    /// </summary>
    [Fact]
    public void Deserialize_UnrecognizedFormatVersion_ThrowsUnsupportedFormatVersionException()
    {
        // Build a hand-crafted tagged-wire frame with format version 99:
        //   outer length prefix  (varint)
        //   tag-0 combined varint (0x00 = tag 0, Varint wire type)
        //   format version       (varint = 99)
        //   tag-1 combined varint (value field, Varint)
        //   zigzag(42)
        using var payload = new MemoryStream();
        VarintHelpers.WriteVarUInt64(payload, 0UL);   // tag-0 combined (tag=0, wire=Varint)
        VarintHelpers.WriteVarUInt64(payload, 99UL);  // format version = 99 (unsupported)
        VarintHelpers.WriteTag(payload, 1, WireType.Varint);
        VarintHelpers.WriteZigZagInt64(payload, 42L);

        using var outer = new MemoryStream();
        VarintHelpers.WriteVarUInt64(outer, (ulong)payload.Length);
        payload.Position = 0;
        payload.CopyTo(outer);
        outer.Position = 0;

        Action act = () => SerializableWrapper<int>.Deserialize(outer);

        act.Should().Throw<UnsupportedFormatVersionException>(
            "an unrecognized format-version byte must surface the typed corruption exception, not a raw low-level exception");
    }

    /// <summary>
    /// A buffer missing the tag-0 schema-format header entirely (starts with the value tag
    /// directly) must also surface <see cref="UnsupportedFormatVersionException"/>.
    /// </summary>
    [Fact]
    public void Deserialize_MissingTag0Header_ThrowsUnsupportedFormatVersionException()
    {
        // Build a frame that skips the tag-0 version byte entirely.
        // tag-1 combined varint (value field, Varint) followed by zigzag(7).
        using var payload = new MemoryStream();
        VarintHelpers.WriteTag(payload, 1, WireType.Varint);
        VarintHelpers.WriteZigZagInt64(payload, 7L);

        using var outer = new MemoryStream();
        VarintHelpers.WriteVarUInt64(outer, (ulong)payload.Length);
        payload.Position = 0;
        payload.CopyTo(outer);
        outer.Position = 0;

        Action act = () => SerializableWrapper<int>.Deserialize(outer);

        act.Should().Throw<UnsupportedFormatVersionException>(
            "a payload that does not open with tag-0 version must surface the typed corruption exception");
    }

    // ── Decoded-value range and type-id consistency ──────────────────────────
    // Type ids are the persisted wire constants: 1 int, 3 string, 6 bool, 7 byte, 8 char.

    [Fact]
    public void Deserialize_IntPayloadOutsideInt32Range_ThrowsInvalidData()
    {
        using var framed = FrameWithVarintValue(typeId: 1, zigZag: true, rawValue: (1L << 40) | 5);

        Action act = () => SerializableWrapper<int>.Deserialize(framed);

        act.Should().Throw<InvalidDataException>(
            "a persisted int value wider than 32 bits is corrupt and must not narrow to a different int");
    }

    [Fact]
    public void Deserialize_BytePayloadAbove255_ThrowsInvalidData()
    {
        using var framed = FrameWithVarintValue(typeId: 7, zigZag: false, rawValue: 300);

        Action act = () => SerializableWrapper<byte>.Deserialize(framed);

        act.Should().Throw<InvalidDataException>(
            "a persisted byte value above 255 is corrupt and must not narrow to a different byte");
    }

    [Fact]
    public void Deserialize_CharPayloadAboveUInt16_ThrowsInvalidData()
    {
        using var framed = FrameWithVarintValue(typeId: 8, zigZag: false, rawValue: 0x10041);

        Action act = () => SerializableWrapper<char>.Deserialize(framed);

        act.Should().Throw<InvalidDataException>(
            "a persisted char value above U+FFFF is corrupt and must not narrow to a different char");
    }

    [Fact]
    public void Deserialize_BoolPayloadOtherThanZeroOrOne_ThrowsInvalidData()
    {
        using var framed = FrameWithVarintValue(typeId: 6, zigZag: false, rawValue: 2);

        Action act = () => SerializableWrapper<bool>.Deserialize(framed);

        act.Should().Throw<InvalidDataException>(
            "the writer emits only 0 or 1 for bool; any other value is corrupt");
    }

    [Fact]
    public void Deserialize_TypeIdInconsistentWithT_ThrowsInvalidData()
    {
        // A string-typed frame read as SerializableWrapper<int>: the persisted type id names a CLR
        // type that is not T, which is corrupt or foreign input under the typed decode contract.
        using var source = new MemoryStream();
        new SerializableWrapper<string>("x").Serialize(source);
        source.Position = 0;

        Action act = () => SerializableWrapper<int>.Deserialize(source);

        act.Should().Throw<InvalidDataException>(
            "a type id that does not match T must surface the typed corruption exception, not InvalidCastException");
    }

    [Fact]
    public void Deserialize_InRangeBoundaryValues_Roundtrip()
    {
        RoundTrip(int.MinValue).Should().Be(int.MinValue);
        RoundTrip(int.MaxValue).Should().Be(int.MaxValue);
        RoundTrip(byte.MaxValue).Should().Be(byte.MaxValue);
        RoundTrip(char.MaxValue).Should().Be(char.MaxValue);
        RoundTrip(true).Should().BeTrue();
        RoundTrip(false).Should().BeFalse();
    }

    private static T RoundTrip<T>(T value)
    {
        using var ms = new MemoryStream();
        new SerializableWrapper<T>(value).Serialize(ms);
        ms.Position = 0;
        return SerializableWrapper<T>.Deserialize(ms).Value;
    }

    private static MemoryStream FrameWithVarintValue(ulong typeId, bool zigZag, long rawValue)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(0x00); // tag 0, Varint
        VarintHelpers.WriteVarUInt64(payload, 1UL); // schema-format version
        VarintHelpers.WriteTag(payload, 2, WireType.Varint);
        VarintHelpers.WriteVarUInt64(payload, typeId);
        VarintHelpers.WriteTag(payload, 1, WireType.Varint);
        if (zigZag)
        {
            VarintHelpers.WriteZigZagInt64(payload, rawValue);
        }
        else
        {
            VarintHelpers.WriteVarUInt64(payload, (ulong)rawValue);
        }

        var framed = new MemoryStream();
        VarintHelpers.WriteVarUInt64(framed, (ulong)payload.Length);
        framed.Write(payload.ToArray());
        framed.Position = 0;
        return framed;
    }
}

/// <summary>
/// A generated tagged-wire record whose first field is a string, matching the 1.x field order
/// used by <see cref="OneXPayloadFailureModeTests"/>.
/// </summary>
[Boutquin.Storage.Domain.Attributes.StorageSerializable]
public partial record struct OneXNameFirstRecord(
    [property: Boutquin.Storage.Domain.Attributes.Tag(1)] string Name,
    [property: Boutquin.Storage.Domain.Attributes.Tag(2)] int Population);

/// <summary>
/// Pins what a 2.0.0 reader actually throws when handed 1.x-written bytes, and that the shipped
/// migration documentation names every one of those failure types. The 1.x wire carried no format
/// marker, so the length-prefix scoping and wire-type checks run before the tag-0 check and can
/// fail with a different exception type than <see cref="UnsupportedFormatVersionException"/>;
/// a consumer that catches only that type would miss the 1.x signal.
/// </summary>
public sealed class OneXPayloadFailureModeTests
{
    private static readonly Type[] s_documentedFailureTypes =
    [
        typeof(UnsupportedFormatVersionException),
        typeof(InvalidDataException),
        typeof(EndOfStreamException),
    ];

    [Fact]
    public void OneXWrapperBytes_SurfaceOnlyDocumentedFailureTypes_AndMoreThanOneKind()
    {
        // Exact 1.x BinaryWriter layouts: int = 4 LE bytes, long = 8 LE bytes.
        var observed = new[]
        {
            ThrownBy(() => SerializableWrapper<int>.Deserialize(new MemoryStream([1, 0, 0, 0]))),
            ThrownBy(() => SerializableWrapper<int>.Deserialize(new MemoryStream([0, 0, 0, 0]))),
            ThrownBy(() => SerializableWrapper<int>.Deserialize(new MemoryStream([0x2C, 0x01, 0, 0]))),
            ThrownBy(() => SerializableWrapper<long>.Deserialize(new MemoryStream([42, 0, 0, 0, 0, 0, 0, 0]))),
        };

        observed.Should().OnlyContain(t => s_documentedFailureTypes.Contains(t));
        observed.Distinct().Count().Should().BeGreaterThan(1,
            "1.x bytes do not reliably surface as UnsupportedFormatVersionException, which is why the guide must name the other types");
    }

    [Fact]
    public void OneXRecordBytes_WithValidLengthScope_ButNonTagZeroFirstField_ThrowsInvalidDataException()
    {
        // 1.x BinaryWriter layout of (string "New York", int 8336817): 7-bit length 8, UTF-8, int32 LE.
        // The length prefix (8) fits the stream, but the first "tag" byte 'N' (0x4E) carries an invalid
        // wire type, so the wire-type check fires before the tag-0 check.
        byte[] oneX = [8, .. "New York"u8, 0xB1, 0x35, 0x7F, 0x00];

        var thrown = ThrownBy(() => OneXNameFirstRecord.Deserialize(new MemoryStream(oneX)));

        thrown.Should().Be(typeof(InvalidDataException));
    }

    [Theory]
    [InlineData("docs/migration-1x-to-2x.md", "## Fail-fast detection")]
    [InlineData("docs/migration-1x-to-2x.md", "## What `UnsupportedFormatVersionException` looks like in practice")]
    [InlineData("docs/source-generator-type-reference.md", "## Schema-format version")]
    [InlineData("README.md", "- **Every property declares `[Tag(N)]`.**")]
    public void ShippedDocs_NameEveryFailureTypeA1xPayloadCanSurfaceAs(string docPath, string sectionStart)
    {
        var section = ReadSection(docPath, sectionStart);

        foreach (var type in s_documentedFailureTypes)
        {
            section.Should().Contain(type.Name, $"{docPath} \"{sectionStart}\" must name {type.Name}");
        }
    }

    private static Type ThrownBy(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex.GetType();
        }

        throw new InvalidOperationException("1.x-shaped bytes were accepted by the 2.0.0 reader.");
    }

    private static string ReadSection(string relativePath, string startMarker)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test runs inside the repository checkout");
        var text = File.ReadAllText(Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{relativePath} must contain the section {startMarker}");

        // A section ends at the next heading (or, for a README bullet, the next bullet).
        var rest = text[(start + startMarker.Length)..];
        var end = startMarker.StartsWith("- ", StringComparison.Ordinal)
            ? rest.IndexOf("\n- ", StringComparison.Ordinal)
            : rest.IndexOf("\n## ", StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }
}

/// <summary>The README schema-evolution example's original record (two tagged fields).</summary>
[Boutquin.Storage.Domain.Attributes.StorageSerializable]
public partial record struct ReadmeCityValueV1(
    [property: Boutquin.Storage.Domain.Attributes.Tag(1)] string Name,
    [property: Boutquin.Storage.Domain.Attributes.Tag(2)] int Population);

/// <summary>The README example after adding a field in the shape the README recommends.</summary>
[Boutquin.Storage.Domain.Attributes.StorageSerializable]
public partial record struct ReadmeCityValueV2(
    [property: Boutquin.Storage.Domain.Attributes.Tag(1)] string Name,
    [property: Boutquin.Storage.Domain.Attributes.Tag(2)] int Population,
    [property: Boutquin.Storage.Domain.Attributes.Tag(3)] string Timezone);

/// <summary>The same evolution with a nullable string, which the generated writer cannot emit at null.</summary>
[Boutquin.Storage.Domain.Attributes.StorageSerializable]
public partial record struct ReadmeCityValueNullableTimezone(
    [property: Boutquin.Storage.Domain.Attributes.Tag(1)] string Name,
    [property: Boutquin.Storage.Domain.Attributes.Tag(2)] int Population,
    [property: Boutquin.Storage.Domain.Attributes.Tag(3)] string? Timezone);

/// <summary>
/// Pins the README "add a new field" schema-evolution example: data written before the field
/// existed must be readable AND re-writable by the evolved record. A <c>string?</c> field reads
/// back as null for old data, but the generated writer refuses null strings, so the example must
/// use a non-nullable <c>string</c> (absent tag reads as the empty string).
/// </summary>
public sealed class ReadmeSchemaEvolutionExampleTests
{
    [Fact]
    public void AddedNonNullableStringField_OldDataReadsAsEmpty_AndRoundTripsOnRewrite()
    {
        using var old = new MemoryStream();
        new ReadmeCityValueV1("New York", 8_336_817).Serialize(old);
        old.Position = 0;

        var migrated = ReadmeCityValueV2.Deserialize(old);
        migrated.Timezone.Should().BeEmpty();

        using var rewritten = new MemoryStream();
        migrated.Serialize(rewritten);
        rewritten.Position = 0;
        ReadmeCityValueV2.Deserialize(rewritten).Should().Be(migrated);
    }

    [Fact]
    public void AddedNullableStringField_OldDataReadsAsNull_AndWritesBackUnchanged()
    {
        using var old = new MemoryStream();
        new ReadmeCityValueV1("New York", 8_336_817).Serialize(old);
        old.Position = 0;

        var migrated = ReadmeCityValueNullableTimezone.Deserialize(old);
        migrated.Timezone.Should().BeNull();

        using var rewritten = new MemoryStream();
        var rewrite = () => migrated.Serialize(rewritten);
        rewrite.Should().NotThrow("a null string? writes no tag, so the value old data produced is writable");
        rewritten.ToArray().Should().Equal(old.ToArray(), "the null field re-encodes as the absent tag the old writer left");
    }

    [Fact]
    public void ReadmeSchemaEvolutionExample_DoesNotClaimTheWriterRefusesANullString()
    {
        var readme = ReadRepoFile("README.md");
        var start = readme.IndexOf("Schema evolution on a `[StorageSerializable]`:", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = readme.IndexOf("To customize generated output", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        var section = System.Text.RegularExpressions.Regex.Replace(readme[start..end], @"\s+", " ");
        section.Should().NotContain("refuses a null string",
            "a null string? writes no tag, so an evolved record that defaults a new string? to null can be rewritten");
        section.Should().Contain("a null string? writes no tag");
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test runs inside the repository checkout");
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }
}
