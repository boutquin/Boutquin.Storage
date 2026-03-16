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
/// Unit tests for the <see cref="SerializationException"/> class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SerializationExceptionTests
{
    /// <summary>
    /// Test to ensure that the message constructor correctly sets the exception message.
    /// </summary>
    [Fact]
    public void MessageConstructor_ShouldSetMessage()
    {
        // Arrange
        const string expectedMessage = "Serialization failed.";

        // Act
        var exception = new SerializationException(expectedMessage);

        // Assert
        exception.Message.Should().Be(expectedMessage);
    }

    /// <summary>
    /// Test to ensure that the message and inner exception constructor correctly sets both properties.
    /// </summary>
    [Fact]
    public void MessageAndInnerExceptionConstructor_ShouldSetBoth()
    {
        // Arrange
        const string expectedMessage = "Serialization failed.";
        var innerException = new InvalidOperationException("Inner error.");

        // Act
        var exception = new SerializationException(expectedMessage, innerException);

        // Assert
        exception.Message.Should().Be(expectedMessage);
        exception.InnerException.Should().BeSameAs(innerException);
    }

    /// <summary>
    /// Test to ensure that the inner exception is accessible from the exception instance.
    /// </summary>
    [Fact]
    public void InnerException_ShouldBeAccessible()
    {
        // Arrange
        var innerException = new InvalidOperationException("Root cause.");
        var exception = new SerializationException("Wrapper.", innerException);

        // Act
        var actual = exception.InnerException;

        // Assert
        actual.Should().NotBeNull();
        actual.Should().BeOfType<InvalidOperationException>();
        actual!.Message.Should().Be("Root cause.");
    }

    /// <summary>
    /// Test to ensure that the SerializationException class is sealed.
    /// </summary>
    [Fact]
    public void Exception_ShouldBeSealed()
    {
        // Act & Assert
        typeof(SerializationException).IsSealed.Should().BeTrue();
    }
}

/// <summary>
/// Unit tests for the <see cref="DeserializationException"/> class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class DeserializationExceptionTests
{
    /// <summary>
    /// Test to ensure that the message constructor correctly sets the exception message.
    /// </summary>
    [Fact]
    public void MessageConstructor_ShouldSetMessage()
    {
        // Arrange
        const string expectedMessage = "Deserialization failed.";

        // Act
        var exception = new DeserializationException(expectedMessage);

        // Assert
        exception.Message.Should().Be(expectedMessage);
    }

    /// <summary>
    /// Test to ensure that the message and inner exception constructor correctly sets both properties.
    /// </summary>
    [Fact]
    public void MessageAndInnerExceptionConstructor_ShouldSetBoth()
    {
        // Arrange
        const string expectedMessage = "Deserialization failed.";
        var innerException = new InvalidOperationException("Inner error.");

        // Act
        var exception = new DeserializationException(expectedMessage, innerException);

        // Assert
        exception.Message.Should().Be(expectedMessage);
        exception.InnerException.Should().BeSameAs(innerException);
    }

    /// <summary>
    /// Test to ensure that the inner exception is accessible from the exception instance.
    /// </summary>
    [Fact]
    public void InnerException_ShouldBeAccessible()
    {
        // Arrange
        var innerException = new InvalidOperationException("Root cause.");
        var exception = new DeserializationException("Wrapper.", innerException);

        // Act
        var actual = exception.InnerException;

        // Assert
        actual.Should().NotBeNull();
        actual.Should().BeOfType<InvalidOperationException>();
        actual!.Message.Should().Be("Root cause.");
    }

    /// <summary>
    /// Test to ensure that the DeserializationException class is sealed.
    /// </summary>
    [Fact]
    public void Exception_ShouldBeSealed()
    {
        // Act & Assert
        typeof(DeserializationException).IsSealed.Should().BeTrue();
    }
}
