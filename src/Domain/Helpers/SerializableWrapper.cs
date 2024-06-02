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
namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// Generic wrapper class for serializing and deserializing built-in types.
/// </summary>
/// <typeparam name="T">The type to be serialized and deserialized.</typeparam>
public class SerializableWrapper<T> : ISerializable<SerializableWrapper<T>>, IComparable<SerializableWrapper<T>>
{
    /// <summary>
    /// Gets or sets the value of the wrapper.
    /// </summary>
    public T Value { get; set; }

    /// <summary>
    /// Initializes a new instance of the SerializableWrapper class.
    /// </summary>
    public SerializableWrapper() { }

    /// <summary>
    /// Initializes a new instance of the SerializableWrapper class with the specified value.
    /// </summary>
    /// <param name="value">The value to wrap.</param>
    public SerializableWrapper(T value)
    {
        Value = value;
    }

    /// <summary>
    /// Serializes the wrapped value to a stream.
    /// </summary>
    /// <param name="stream">The stream to serialize to.</param>
    public void Serialize(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        if (Value is int intValue)
        {
            writer.Write(intValue);
        }
        else if (Value is string stringValue)
        {
            writer.Write(stringValue);
        }
        else if (Value is long longValue)
        {
            writer.Write(longValue);
        }
        else if (Value is float floatValue)
        {
            writer.Write(floatValue);
        }
        else if (Value is double doubleValue)
        {
            writer.Write(doubleValue);
        }
        else if (Value is bool boolValue)
        {
            writer.Write(boolValue);
        }
        else if (Value is byte byteValue)
        {
            writer.Write(byteValue);
        }
        else if (Value is char charValue)
        {
            writer.Write(charValue);
        }
        else
        {
            throw new NotSupportedException($"Type {typeof(T)} is not supported for serialization.");
        }
    }

    /// <summary>
    /// Deserializes the wrapped value from a stream.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized SerializableWrapper object.</returns>
    public static SerializableWrapper<T> Deserialize(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        object value;

        if (typeof(T) == typeof(int))
        {
            value = reader.ReadInt32();
        }
        else if (typeof(T) == typeof(string))
        {
            value = reader.ReadString();
        }
        else if (typeof(T) == typeof(long))
        {
            value = reader.ReadInt64();
        }
        else if (typeof(T) == typeof(float))
        {
            value = reader.ReadSingle();
        }
        else if (typeof(T) == typeof(double))
        {
            value = reader.ReadDouble();
        }
        else if (typeof(T) == typeof(bool))
        {
            value = reader.ReadBoolean();
        }
        else if (typeof(T) == typeof(byte))
        {
            value = reader.ReadByte();
        }
        else if (typeof(T) == typeof(char))
        {
            value = reader.ReadChar();
        }
        else
        {
            throw new NotSupportedException($"Type {typeof(T)} is not supported for deserialization.");
        }

        return new SerializableWrapper<T>((T)value);
    }

    /// <summary>
    /// Compares the current object with another object of the same type.
    /// </summary>
    /// <param name="other">An object to compare with this object.</param>
    /// <returns>A value that indicates the relative order of the objects being compared.</returns>
    public int CompareTo(SerializableWrapper<T> other)
    {
        return Comparer<T>.Default.Compare(Value, other.Value);
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current object.
    /// </summary>
    /// <param name="obj">The object to compare with the current object.</param>
    /// <returns>true if the specified object is equal to the current object; otherwise, false.</returns>
    public override bool Equals(object obj)
    {
        if (obj is SerializableWrapper<T> other)
        {
            return EqualityComparer<T>.Default.Equals(Value, other.Value);
        }

        return false;
    }

    /// <summary>
    /// Serves as the default hash function.
    /// </summary>
    /// <returns>A hash code for the current object.</returns>
    public override int GetHashCode()
    {
        return EqualityComparer<T>.Default.GetHashCode(Value);
    }

    /// <summary>
    /// Returns a string that represents the current object.
    /// </summary>
    /// <returns>A string that represents the current object.</returns>
    public override string ToString()
    {
        return Value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Implicit conversion from T to SerializableWrapper&lt;T&gt;.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    public static implicit operator SerializableWrapper<T>(T value) => new(value);

    /// <summary>
    /// Implicit conversion from SerializableWrapper&lt;T&gt; to T.
    /// </summary>
    /// <param name="wrapper">The wrapper to convert.</param>
    public static implicit operator T(SerializableWrapper<T> wrapper) => wrapper.Value;
}