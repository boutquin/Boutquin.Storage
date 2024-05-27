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

namespace Boutquin.Storage.Domain.Exceptions;

/// <summary>
/// Exception thrown when there is an error during serialization.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SerializationException"/> class is used to signal errors that occur during 
/// the serialization process. This exception is typically thrown when an object cannot be 
/// serialized to a specific format (e.g., binary, CSV) due to issues such as unsupported types,
/// invalid data, or other serialization constraints.
/// </para>
/// <para>
/// This exception can be used in various serialization scenarios including but not limited to:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>Binary serialization using <see cref="BinaryWriter"/>.</description>
/// </item>
/// <item>
/// <description>Text serialization using <see cref="StreamWriter"/>.</description>
/// </item>
/// <item>
/// <description>Custom serialization formats such as CSV.</description>
/// </item>
/// </list>
/// <para>
/// When a <see cref="SerializationException"/> is thrown, it provides a message that describes the error,
/// and optionally an inner exception that caused the current exception. This can help in diagnosing
/// and troubleshooting serialization issues by providing detailed information about the root cause.
/// </para>
/// </remarks>
/// <example>
/// The following example demonstrates how to throw and catch a <see cref="SerializationException"/>.
/// <code>
/// try
/// {
///     // Attempt to serialize an unsupported type
///     var unsupportedObject = new object();
///     var serializer = new BinaryWriter(stream);
///     // This will throw a SerializationException
///     SerializeUnsupportedType(serializer, unsupportedObject);
/// }
/// catch (SerializationException ex)
/// {
///     Console.WriteLine($"Serialization error: {ex.Message}");
/// }
/// </code>
/// </example>
public class SerializationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SerializationException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public SerializationException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SerializationException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference
    /// if no inner exception is specified.</param>
    public SerializationException(string message, Exception innerException) : base(message, innerException) { }
}