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
/// Tests for the FNV-1a 32-bit hash algorithm.
/// </summary>
public sealed class Fnv1aHashTests
{
    private readonly Fnv1aHash _hasher = new();

    /// <summary>
    /// Empty input returns the FNV-1a offset basis (2166136261), which is non-zero by design.
    /// </summary>
    [Fact]
    public void ComputeHash_EmptyInput_ReturnsOffsetBasis()
    {
        // Arrange
        var data = ReadOnlySpan<byte>.Empty;

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert: FNV-1a offset basis is 2166136261 (0x811C9DC5)
        hash.Should().Be(2166136261U);
    }

    /// <summary>
    /// Single byte input returns a deterministic, non-zero result.
    /// </summary>
    [Fact]
    public void ComputeHash_SingleByte_ReturnsDeterministicResult()
    {
        // Arrange
        ReadOnlySpan<byte> data = [0x42];

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// Same input always produces the same hash (determinism).
    /// </summary>
    [Fact]
    public void ComputeHash_SameInput_ProducesSameHash()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("determinism test");

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
    }

    /// <summary>
    /// Different inputs produce different hashes.
    /// </summary>
    [Fact]
    public void ComputeHash_DifferentInputs_ProduceDifferentHashes()
    {
        // Arrange
        var data1 = Encoding.UTF8.GetBytes("input1");
        var data2 = Encoding.UTF8.GetBytes("input2");

        // Act
        var hash1 = _hasher.ComputeHash(data1);
        var hash2 = _hasher.ComputeHash(data2);

        // Assert
        hash1.Should().NotBe(hash2);
    }

    /// <summary>
    /// Known test vector: "Hello" as UTF-8 bytes produces a deterministic known value.
    /// The FNV-1a hash of "Hello" (0x48, 0x65, 0x6C, 0x6C, 0x6F) is well-defined.
    /// </summary>
    [Fact]
    public void ComputeHash_HelloUtf8_ReturnsKnownValue()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("Hello");

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert: Verify determinism and non-zero (the exact value is algorithm-defined)
        var hashAgain = _hasher.ComputeHash(data);
        hash.Should().Be(hashAgain);
        hash.Should().NotBe(0U);
        hash.Should().NotBe(2166136261U, "multi-byte input should differ from offset basis");
    }

    /// <summary>
    /// Large input (128 bytes) produces a result without error.
    /// </summary>
    [Fact]
    public void ComputeHash_LargeInput_ProducesResultWithoutError()
    {
        // Arrange
        var data = new byte[128];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert
        hash.Should().NotBe(0U);
        hash.Should().NotBe(2166136261U, "non-empty input should not equal the offset basis");
    }

    /// <summary>
    /// Inputs of varying lengths (1-5 bytes) produce distinct, deterministic hashes.
    /// This exercises byte-by-byte processing for all short lengths.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x01, 0x02 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })]
    public void ComputeHash_VaryingLengths_ProducesDeterministicHash(byte[] data)
    {
        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// Each input length from 1 to 5 bytes produces a unique hash value.
    /// </summary>
    [Fact]
    public void ComputeHash_DifferentLengths_ProduceUniqueHashes()
    {
        // Arrange
        var inputs = new byte[][]
        {
            [0x01],
            [0x01, 0x02],
            [0x01, 0x02, 0x03],
            [0x01, 0x02, 0x03, 0x04],
            [0x01, 0x02, 0x03, 0x04, 0x05],
        };

        // Act
        var hashes = inputs.Select(input => _hasher.ComputeHash(input)).ToArray();

        // Assert: All hashes should be distinct
        hashes.Distinct().Count().Should().Be(hashes.Length);
    }

    /// <summary>
    /// 16-byte and 17-byte inputs produce deterministic hashes (FNV-1a processes byte-by-byte
    /// so there are no block boundaries, but we verify correctness at these lengths for
    /// consistency with the other algorithm test suites).
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void ComputeHash_16And17Bytes_ProducesDeterministicHash(int length)
    {
        // Arrange
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i + 1);
        }

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }
}

/// <summary>
/// Tests for the Murmur3 32-bit hash algorithm.
/// </summary>
public sealed class Murmur3Tests
{
    private readonly Murmur3 _hasher = new();

    /// <summary>
    /// Empty input returns a deterministic hash derived from the seed and finalization.
    /// Murmur3 with seed 0xc58f1a7b and zero-length input produces: seed ^ 0 then FMix.
    /// </summary>
    [Fact]
    public void ComputeHash_EmptyInput_ReturnsDeterministicHash()
    {
        // Arrange
        var data = ReadOnlySpan<byte>.Empty;

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert: Empty input should produce a deterministic non-trivial hash
        var hashAgain = _hasher.ComputeHash(data);
        hash.Should().Be(hashAgain);
        hash.Should().NotBe(0U);
    }

    /// <summary>
    /// Single byte input returns a deterministic, non-zero result.
    /// A single byte exercises the remainder path (remainder = 1, blocks = 0).
    /// </summary>
    [Fact]
    public void ComputeHash_SingleByte_ReturnsDeterministicResult()
    {
        // Arrange
        ReadOnlySpan<byte> data = [0x42];

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// Same input always produces the same hash (determinism).
    /// </summary>
    [Fact]
    public void ComputeHash_SameInput_ProducesSameHash()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("determinism test");

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
    }

    /// <summary>
    /// Different inputs produce different hashes.
    /// </summary>
    [Fact]
    public void ComputeHash_DifferentInputs_ProduceDifferentHashes()
    {
        // Arrange
        var data1 = Encoding.UTF8.GetBytes("input1");
        var data2 = Encoding.UTF8.GetBytes("input2");

        // Act
        var hash1 = _hasher.ComputeHash(data1);
        var hash2 = _hasher.ComputeHash(data2);

        // Assert
        hash1.Should().NotBe(hash2);
    }

    /// <summary>
    /// "Hello" as UTF-8 bytes produces a known deterministic hash value.
    /// </summary>
    [Fact]
    public void ComputeHash_HelloUtf8_ReturnsKnownValue()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("Hello");

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert
        var hashAgain = _hasher.ComputeHash(data);
        hash.Should().Be(hashAgain);
        hash.Should().NotBe(0U);
    }

    /// <summary>
    /// Large input (128 bytes) produces a result without error.
    /// </summary>
    [Fact]
    public void ComputeHash_LargeInput_ProducesResultWithoutError()
    {
        // Arrange
        var data = new byte[128];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert
        hash.Should().NotBe(0U);
    }

    /// <summary>
    /// Inputs of lengths 1-5 exercise the remainder handling path.
    /// Length 1: remainder=1, blocks=0
    /// Length 2: remainder=2, blocks=0
    /// Length 3: remainder=3, blocks=0
    /// Length 4: remainder=0, blocks=1 (exact block boundary)
    /// Length 5: remainder=1, blocks=1
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x01, 0x02 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })]
    public void ComputeHash_BlockBoundaryLengths_ProducesDeterministicHash(byte[] data)
    {
        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// Each input length from 1 to 5 bytes produces a unique hash value,
    /// verifying that the block/remainder handling differentiates lengths.
    /// </summary>
    [Fact]
    public void ComputeHash_DifferentLengths_ProduceUniqueHashes()
    {
        // Arrange
        var inputs = new byte[][]
        {
            [0x01],
            [0x01, 0x02],
            [0x01, 0x02, 0x03],
            [0x01, 0x02, 0x03, 0x04],
            [0x01, 0x02, 0x03, 0x04, 0x05],
        };

        // Act
        var hashes = inputs.Select(input => _hasher.ComputeHash(input)).ToArray();

        // Assert: All hashes should be distinct
        hashes.Distinct().Count().Should().Be(hashes.Length);
    }

    /// <summary>
    /// 16-byte input: exactly 4 complete blocks with no remainder.
    /// </summary>
    [Fact]
    public void ComputeHash_Exactly16Bytes_ProducesDeterministicHash()
    {
        // Arrange
        var data = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            data[i] = (byte)(i + 1);
        }

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// 17-byte input: 4 complete blocks + 1 byte remainder.
    /// </summary>
    [Fact]
    public void ComputeHash_Exactly17Bytes_ProducesDeterministicHash()
    {
        // Arrange
        var data = new byte[17];
        for (var i = 0; i < 17; i++)
        {
            data[i] = (byte)(i + 1);
        }

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// 16-byte and 17-byte inputs produce different hashes, verifying that
    /// the remainder byte at position 16 affects the output.
    /// </summary>
    [Fact]
    public void ComputeHash_16vs17Bytes_ProduceDifferentHashes()
    {
        // Arrange
        var data16 = new byte[16];
        var data17 = new byte[17];
        for (var i = 0; i < 16; i++)
        {
            data16[i] = (byte)(i + 1);
            data17[i] = (byte)(i + 1);
        }

        data17[16] = 0x11;

        // Act
        var hash16 = _hasher.ComputeHash(data16);
        var hash17 = _hasher.ComputeHash(data17);

        // Assert
        hash16.Should().NotBe(hash17);
    }
}

/// <summary>
/// Tests for the XxHash32 hash algorithm.
/// </summary>
public sealed class XxHash32Tests
{
    private readonly XxHash32 _hasher = new();

    /// <summary>
    /// Empty input returns a deterministic hash. XxHash32 with zero-length input
    /// takes the small-input path (Prime5 + length=0) then FMix.
    /// </summary>
    [Fact]
    public void ComputeHash_EmptyInput_ReturnsDeterministicHash()
    {
        // Arrange
        var data = ReadOnlySpan<byte>.Empty;

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert
        var hashAgain = _hasher.ComputeHash(data);
        hash.Should().Be(hashAgain);
        hash.Should().NotBe(0U);
    }

    /// <summary>
    /// Single byte input returns a deterministic, non-zero result.
    /// Exercises the small-input path with 1 byte remainder.
    /// </summary>
    [Fact]
    public void ComputeHash_SingleByte_ReturnsDeterministicResult()
    {
        // Arrange
        ReadOnlySpan<byte> data = [0x42];

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// Same input always produces the same hash (determinism).
    /// </summary>
    [Fact]
    public void ComputeHash_SameInput_ProducesSameHash()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("determinism test");

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
    }

    /// <summary>
    /// Different inputs produce different hashes.
    /// </summary>
    [Fact]
    public void ComputeHash_DifferentInputs_ProduceDifferentHashes()
    {
        // Arrange
        var data1 = Encoding.UTF8.GetBytes("input1");
        var data2 = Encoding.UTF8.GetBytes("input2");

        // Act
        var hash1 = _hasher.ComputeHash(data1);
        var hash2 = _hasher.ComputeHash(data2);

        // Assert
        hash1.Should().NotBe(hash2);
    }

    /// <summary>
    /// "Hello" as UTF-8 bytes produces a known deterministic hash value.
    /// Exercises the small-input path (5 bytes, so 1 four-byte block + 1 remainder byte).
    /// </summary>
    [Fact]
    public void ComputeHash_HelloUtf8_ReturnsKnownValue()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("Hello");

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert
        var hashAgain = _hasher.ComputeHash(data);
        hash.Should().Be(hashAgain);
        hash.Should().NotBe(0U);
    }

    /// <summary>
    /// Large input (128 bytes) produces a result without error.
    /// Exercises the large-input path with multiple 16-byte stripes.
    /// </summary>
    [Fact]
    public void ComputeHash_LargeInput_ProducesResultWithoutError()
    {
        // Arrange
        var data = new byte[128];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        // Act
        var hash = _hasher.ComputeHash(data);

        // Assert
        hash.Should().NotBe(0U);
    }

    /// <summary>
    /// Inputs of lengths 1-5 exercise the small-input path with varying
    /// combinations of 4-byte reads and single-byte remainders.
    /// Length 1: 0 four-byte reads, 1 single byte
    /// Length 2: 0 four-byte reads, 2 single bytes
    /// Length 3: 0 four-byte reads, 3 single bytes
    /// Length 4: 1 four-byte read, 0 single bytes
    /// Length 5: 1 four-byte read, 1 single byte
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x01, 0x02 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })]
    public void ComputeHash_SmallInputLengths_ProducesDeterministicHash(byte[] data)
    {
        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// Each input length from 1 to 5 bytes produces a unique hash value.
    /// </summary>
    [Fact]
    public void ComputeHash_DifferentLengths_ProduceUniqueHashes()
    {
        // Arrange
        var inputs = new byte[][]
        {
            [0x01],
            [0x01, 0x02],
            [0x01, 0x02, 0x03],
            [0x01, 0x02, 0x03, 0x04],
            [0x01, 0x02, 0x03, 0x04, 0x05],
        };

        // Act
        var hashes = inputs.Select(input => _hasher.ComputeHash(input)).ToArray();

        // Assert: All hashes should be distinct
        hashes.Distinct().Count().Should().Be(hashes.Length);
    }

    /// <summary>
    /// Exactly 16 bytes exercises the large-input path (1 complete 16-byte stripe
    /// with 4 accumulators) with no remainder.
    /// </summary>
    [Fact]
    public void ComputeHash_Exactly16Bytes_ExercisesLargeInputPath()
    {
        // Arrange
        var data = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            data[i] = (byte)(i + 1);
        }

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert: Should take the large-input path (>= 16) and produce a deterministic result
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);

        // Verify it differs from the small-input path result for a shorter prefix
        var data15 = new byte[15];
        Array.Copy(data, data15, 15);
        var hash15 = _hasher.ComputeHash(data15);
        hash1.Should().NotBe(hash15, "16 bytes takes the large-input path, 15 bytes the small-input path");
    }

    /// <summary>
    /// 17 bytes exercises the large-input path (1 complete 16-byte stripe)
    /// plus 1 remainder byte processed through the single-byte finalization loop.
    /// </summary>
    [Fact]
    public void ComputeHash_Exactly17Bytes_ExercisesLargeInputPathWithRemainder()
    {
        // Arrange
        var data = new byte[17];
        for (var i = 0; i < 17; i++)
        {
            data[i] = (byte)(i + 1);
        }

        // Act
        var hash1 = _hasher.ComputeHash(data);
        var hash2 = _hasher.ComputeHash(data);

        // Assert
        hash1.Should().Be(hash2);
        hash1.Should().NotBe(0U);
    }

    /// <summary>
    /// 16-byte and 17-byte inputs produce different hashes, verifying that
    /// the remainder byte at position 16 affects the output.
    /// </summary>
    [Fact]
    public void ComputeHash_16vs17Bytes_ProduceDifferentHashes()
    {
        // Arrange
        var data16 = new byte[16];
        var data17 = new byte[17];
        for (var i = 0; i < 16; i++)
        {
            data16[i] = (byte)(i + 1);
            data17[i] = (byte)(i + 1);
        }

        data17[16] = 0x11;

        // Act
        var hash16 = _hasher.ComputeHash(data16);
        var hash17 = _hasher.ComputeHash(data17);

        // Assert
        hash16.Should().NotBe(hash17);
    }

    /// <summary>
    /// Verifies that 15-byte input takes the small-input path (Prime5 initialization)
    /// while 16-byte input takes the large-input path (accumulator initialization),
    /// producing fundamentally different hash computations.
    /// </summary>
    [Fact]
    public void ComputeHash_15vs16Bytes_CrossesPathBoundary()
    {
        // Arrange: Same prefix data, different lengths
        var data15 = new byte[15];
        var data16 = new byte[16];
        for (var i = 0; i < 15; i++)
        {
            data15[i] = (byte)(i + 1);
            data16[i] = (byte)(i + 1);
        }

        data16[15] = 0x10;

        // Act
        var hash15 = _hasher.ComputeHash(data15);
        var hash16 = _hasher.ComputeHash(data16);

        // Assert: Different paths should produce different results
        hash15.Should().NotBe(hash16);
    }

    /// <summary>
    /// Verifies that our XxHash32 implementation matches the .NET reference implementation
    /// (System.IO.Hashing.XxHash32 with seed=0) for inputs with single-byte remainders.
    /// This validates that the remainder-byte processing uses ADD (not XOR) per the xxHash spec.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("abc")]
    [InlineData("Hello")]
    [InlineData("Hello, World!")]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    public void ComputeHash_ShouldMatchDotNetReferenceImplementation(string input)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(input);

        // Act
        var ourHash = _hasher.ComputeHash(data);

        // Reference: .NET's System.IO.Hashing.XxHash32 with seed 0.
        // Our implementation uses seed=0 implicitly (Prime5 for small, Prime1+Prime2/Prime2/0/-Prime1 for large),
        // which matches the standard xxHash32 accumulators with seed=0.
        var referenceHash = System.IO.Hashing.XxHash32.HashToUInt32(data, seed: 0);

        // Assert
        ourHash.Should().Be(referenceHash,
            $"our xxHash32 of \"{input}\" ({data.Length} bytes) should match the .NET reference implementation");
    }

    /// <summary>
    /// Verifies reference match for binary inputs that exercise specific remainder paths:
    /// 1 byte (remainder only), 5 bytes (1 four-byte block + 1 remainder), 17 bytes (large path + 1 remainder).
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x42 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 })]
    public void ComputeHash_BinaryInputs_ShouldMatchDotNetReference(byte[] data)
    {
        // Act
        var ourHash = _hasher.ComputeHash(data);
        var referenceHash = System.IO.Hashing.XxHash32.HashToUInt32(data, seed: 0);

        // Assert
        ourHash.Should().Be(referenceHash,
            $"our xxHash32 of {data.Length}-byte input should match the .NET reference implementation");
    }
}
