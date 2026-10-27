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

public sealed class DanglingValueLogPointerExceptionTests
{
    [Fact]
    public void DanglingValueLogPointerException_Constructor_SetsAllProperties()
    {
        var ex = new DanglingValueLogPointerException(3u, 512ul, 42ul, "dangling");

        ex.FileIndex.Should().Be(3u);
        ex.Offset.Should().Be(512ul);
        ex.SequenceNumber.Should().Be(42ul);
        ex.Message.Should().Be("dangling");
    }

    [Fact]
    public void DanglingValueLogPointerException_WithInnerException_SetsBoth()
    {
        var inner = new IOException("inner");
        var ex = new DanglingValueLogPointerException(1u, 0ul, 1ul, "msg", inner);

        ex.InnerException.Should().BeSameAs(inner);
        ex.InnerException!.Message.Should().Be("inner");
    }

    [Fact]
    public void DanglingValueLogPointerException_ShouldBeSealed()
    {
        typeof(DanglingValueLogPointerException).IsSealed.Should().BeTrue();
    }

    [Fact]
    public void DanglingValueLogPointerException_ShouldBeException()
    {
        var ex = new DanglingValueLogPointerException(0u, 0ul, 0ul, "x");
        ex.Should().BeAssignableTo<Exception>();
    }
}

public sealed class WriteAheadLogCorruptExceptionTests
{
    [Fact]
    public void WriteAheadLogCorruptException_MessageConstructor_ShouldSetMessage()
    {
        var ex = new WriteAheadLogCorruptException("corrupt");
        ex.Message.Should().Be("corrupt");
    }

    [Fact]
    public void WriteAheadLogCorruptException_MessageAndInnerException_ShouldSetBoth()
    {
        var inner = new InvalidOperationException("root");
        var ex = new WriteAheadLogCorruptException("outer", inner);

        ex.Message.Should().Be("outer");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void WriteAheadLogCorruptException_InnerException_ShouldBeAccessible()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = new WriteAheadLogCorruptException("wrapper", inner);

        ex.InnerException.Should().NotBeNull();
        ex.InnerException.Should().BeOfType<InvalidOperationException>();
        ex.InnerException!.Message.Should().Be("root cause");
    }

    [Fact]
    public void WriteAheadLogCorruptException_ShouldBeSealed()
    {
        typeof(WriteAheadLogCorruptException).IsSealed.Should().BeTrue();
    }

    // EXCEPTION-CONTRACT-WAL-001: SegmentPath + LastGoodOffset diagnostic context.

    [Fact]
    public void WriteAheadLogCorruptException_CapturesSegmentPathAndOffset()
    {
        var ex = new WriteAheadLogCorruptException("corrupt record", "/tmp/seg-0001.wal", 4096L);
        ex.SegmentPath.Should().Be("/tmp/seg-0001.wal");
        ex.LastGoodOffset.Should().Be(4096L);
        ex.Message.Should().Contain("corrupt record");
    }

    [Fact]
    public void WriteAheadLogCorruptException_DefaultsOffsetToMinusOne()
    {
        var ex = new WriteAheadLogCorruptException("bad", "/tmp/x.wal");
        ex.SegmentPath.Should().Be("/tmp/x.wal");
        ex.LastGoodOffset.Should().Be(-1L);
    }

    [Fact]
    public void WriteAheadLogCorruptException_GenericCtors_DefaultSegmentPathToEmpty()
    {
        var ex1 = new WriteAheadLogCorruptException("no path");
        ex1.SegmentPath.Should().Be(string.Empty);
        ex1.LastGoodOffset.Should().Be(-1L);

        var inner = new InvalidOperationException("cause");
        var ex2 = new WriteAheadLogCorruptException("no path either", inner);
        ex2.SegmentPath.Should().Be(string.Empty);
        ex2.LastGoodOffset.Should().Be(-1L);
    }

    [Fact]
    public void WriteAheadLogCorruptException_WithInnerException_CapturesAllFive()
    {
        var inner = new IOException("disk error");
        var ex = new WriteAheadLogCorruptException("outer", "/tmp/seg-0002.wal", 8192L, inner);
        ex.SegmentPath.Should().Be("/tmp/seg-0002.wal");
        ex.LastGoodOffset.Should().Be(8192L);
        ex.Message.Should().Contain("outer");
        ex.InnerException.Should().BeSameAs(inner);
    }
}

public sealed class CorruptBlockExceptionTests
{
    [Fact]
    public void CorruptBlockException_CrcConstructor_SetsExpectedAndActualCrc()
    {
        var ex = new CorruptBlockException(0xDEADBEEFu, 0xCAFEBABEu);

        ex.ExpectedCrc.Should().Be(0xDEADBEEFu);
        ex.ActualCrc.Should().Be(0xCAFEBABEu);
        ex.Message.Should().Contain("0xDEADBEEF");
    }

    [Fact]
    public void CorruptBlockException_MessageConstructor_SetsMessage()
    {
        var ex = new CorruptBlockException("block is corrupt");
        ex.Message.Should().Be("block is corrupt");
    }

    [Fact]
    public void CorruptBlockException_ShouldBeSealed()
    {
        typeof(CorruptBlockException).IsSealed.Should().BeTrue();
    }
}

public sealed class CorruptValueLogExceptionTests
{
    [Fact]
    public void CorruptValueLogException_Constructor_SetsAllProperties()
    {
        var ex = new CorruptValueLogException(2u, 4096ul, "corrupt vlog");

        ex.FileIndex.Should().Be(2u);
        ex.Offset.Should().Be(4096ul);
        ex.Message.Should().Be("corrupt vlog");
    }

    [Fact]
    public void CorruptValueLogException_ShouldBeSealed()
    {
        typeof(CorruptValueLogException).IsSealed.Should().BeTrue();
    }
}

public sealed class UnsupportedFormatVersionExceptionTests
{
    [Fact]
    public void UnsupportedFormatVersionException_VersionConstructor_SetsVersionProperties()
    {
        var ex = new UnsupportedFormatVersionException(5u, 2u);

        ex.ObservedVersion.Should().Be(5u);
        ex.MaxSupportedVersion.Should().Be(2u);
        ex.Message.Should().Contain("5");
    }

    [Fact]
    public void UnsupportedFormatVersionException_MessageConstructor_SetsMessage()
    {
        var ex = new UnsupportedFormatVersionException(0u, "unsupported version");

        ex.Message.Should().Be("unsupported version");
        ex.ObservedVersion.Should().Be(0u);
    }

    [Fact]
    public void UnsupportedFormatVersionException_ShouldBeSealed()
    {
        typeof(UnsupportedFormatVersionException).IsSealed.Should().BeTrue();
    }
}

public sealed class CorruptSegmentExceptionTests
{
    [Fact]
    public void CorruptSegmentException_Constructor_SetsMessageAndOffset()
    {
        var ex = new CorruptSegmentException("bad segment", 1024L);

        ex.Message.Should().Be("bad segment");
        ex.Offset.Should().Be(1024L);
    }

    [Fact]
    public void CorruptSegmentException_WithInnerException_SetsBoth()
    {
        var inner = new IOException("disk error");
        var ex = new CorruptSegmentException("corrupt", 512L, inner);

        ex.Message.Should().Be("corrupt");
        ex.Offset.Should().Be(512L);
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void CorruptSegmentException_ShouldBeSealed()
    {
        typeof(CorruptSegmentException).IsSealed.Should().BeTrue();
    }
}

public sealed class UnsupportedValueKindExceptionTests
{
    [Fact]
    public void UnsupportedValueKindException_SingleByteConstructor_SetsCodeAndDefaultMessage()
    {
        var ex = new UnsupportedValueKindException(0x05);

        ex.ObservedCode.Should().Be(0x05);
        ex.Message.Should().Contain("0x05");
    }

    [Fact]
    public void UnsupportedValueKindException_MessageConstructor_SetsCodeAndCustomMessage()
    {
        var ex = new UnsupportedValueKindException(0xFF, "custom message");

        ex.ObservedCode.Should().Be(0xFF);
        ex.Message.Should().Be("custom message");
    }

    [Fact]
    public void UnsupportedValueKindException_ShouldBeSealed()
    {
        typeof(UnsupportedValueKindException).IsSealed.Should().BeTrue();
    }
}

public sealed class ColumnNotFoundExceptionTests
{
    // ───── Fix 7: AvailableColumns defensive copy ────────────────────────

    [Fact]
    public void ColumnNotFoundException_MutatingSourceList_DoesNotAffectAvailableColumns()
    {
        var source = new List<string> { "Alpha", "Beta" };
        var ex = new ColumnNotFoundException("Gamma", 1u, source);

        // Snapshot taken at construction — mutating the source must not be visible.
        source.Add("Delta");
        source[0] = "Mutated";

        ex.AvailableColumns.Should().BeEquivalentTo(new[] { "Alpha", "Beta" },
            because: "AvailableColumns must be a defensive copy taken at construction time");
    }

    [Fact]
    public void ColumnNotFoundException_AvailableColumns_ReflectsConstructionTimeState()
    {
        var ex = new ColumnNotFoundException("Missing", 2u, ["X", "Y", "Z"]);
        ex.AvailableColumns.Should().Equal("X", "Y", "Z");
    }
}
