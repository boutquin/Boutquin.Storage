// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
        Assert.True(wrapper1.CompareTo(wrapper1) == 0);
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
        int originalValue = 42;

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
}