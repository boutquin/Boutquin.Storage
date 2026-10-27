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
/// Unit tests for the CountingBloomFilter class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class CountingBloomFilterTests
{
    /// <summary>
    /// Test that Add then Contains returns true.
    /// </summary>
    [Fact]
    public void Add_ThenContains_ReturnsTrue()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);

        // Act
        filter.Add("key1");

        // Assert
        Assert.True(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that Contains returns false for a non-added element.
    /// </summary>
    [Fact]
    public void Contains_ReturnsFalse_ForNonAddedElement()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);

        // Assert
        Assert.False(filter.Contains("nonexistent"));
    }

    /// <summary>
    /// Test that Remove decrements and eventually returns false for Contains.
    /// </summary>
    [Fact]
    public void Remove_Decrements_AndEventuallyReturnsFalse()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        filter.Add("key1");

        // Act
        filter.Remove("key1");

        // Assert
        Assert.False(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that Remove on a non-existent item is a no-op (counters stay at 0).
    /// </summary>
    [Fact]
    public void Remove_NonExistentItem_IsNoOp()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);

        // Act — should not throw or corrupt state
        filter.Remove("nonexistent");

        // Assert — filter still works correctly
        filter.Add("key1");
        Assert.True(filter.Contains("key1"));
    }

    /// <summary>
    /// Removing keys that were NEVER added must not produce false negatives for genuine
    /// members — the no-false-negatives guarantee is the load-bearing contract a storage
    /// engine relies on to skip segments. An unguarded Remove decrements whatever positive
    /// counters the ghost key collides with, draining a member's counters to zero. The
    /// standard mitigation: only decrement when ALL k counters are positive (the ghost then
    /// fails the membership test and the Remove is a no-op).
    /// </summary>
    [Fact]
    public void Remove_ManyNeverAddedKeys_DoesNotCreateFalseNegativeForMember()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        filter.Add("member");

        // Act — speculative removals of keys that were never added.
        for (var i = 0; i < 2000; i++)
        {
            filter.Remove($"ghost-{i}");
        }

        // Assert
        Assert.True(filter.Contains("member"),
            "a genuine member must never read as absent because never-added keys were removed");
    }

    /// <summary>
    /// Test that Clear resets all counters.
    /// </summary>
    [Fact]
    public void Clear_ResetsAllCounters()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        filter.Add("key1");
        filter.Add("key2");

        // Act
        filter.Clear();

        // Assert
        Assert.False(filter.Contains("key1"));
        Assert.False(filter.Contains("key2"));
    }

    /// <summary>
    /// Test that multiple adds require multiple removes.
    /// </summary>
    [Fact]
    public void MultipleAdds_RequireMultipleRemoves()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        filter.Add("key1");
        filter.Add("key1");
        filter.Add("key1");

        // Act — remove once
        filter.Remove("key1");

        // Assert — still present after 1 remove (was added 3 times)
        Assert.True(filter.Contains("key1"));

        // Act — remove twice more
        filter.Remove("key1");
        filter.Remove("key1");

        // Assert — now gone
        Assert.False(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that null item throws ArgumentNullException for all methods.
    /// </summary>
    [Fact]
    public void NullItem_ThrowsArgumentNullException()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);

        // Assert
        Assert.Throws<ArgumentNullException>(() => filter.Add(null!));
        Assert.Throws<ArgumentNullException>(() => filter.Contains(null!));
        Assert.Throws<ArgumentNullException>(() => filter.Remove(null!));
        Assert.Throws<ArgumentNullException>(() => filter.GetCount(null!));
    }

    /// <summary>
    /// Test that GetCount returns correct count after multiple adds and removes.
    /// </summary>
    [Fact]
    public void GetCount_ReturnsCorrectCount()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);

        // Act & Assert — initial count should be 0
        Assert.Equal(0, filter.GetCount("key1"));

        // Act — add 3 times
        filter.Add("key1");
        filter.Add("key1");
        filter.Add("key1");

        // Assert
        Assert.Equal(3, filter.GetCount("key1"));

        // Act — remove once
        filter.Remove("key1");

        // Assert
        Assert.Equal(2, filter.GetCount("key1"));
    }

    /// <summary>
    /// Test that counters don't go below zero on excess removes.
    /// </summary>
    [Fact]
    public void Remove_ExcessRemoves_CounterFloorsAtZero()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        filter.Add("key1");

        // Act — remove twice (only added once)
        filter.Remove("key1");
        filter.Remove("key1");

        // Assert — counter should be at 0, not negative
        Assert.Equal(0, filter.GetCount("key1"));
        Assert.False(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that after removing, re-adding works correctly.
    /// </summary>
    [Fact]
    public void Remove_ThenReadd_WorksCorrectly()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        filter.Add("key1");
        filter.Remove("key1");

        // Act — re-add the key
        filter.Add("key1");

        // Assert
        Assert.True(filter.Contains("key1"));
        Assert.Equal(1, filter.GetCount("key1"));
    }

    /// <summary>
    /// Test that clear then re-add works correctly.
    /// </summary>
    [Fact]
    public void Clear_ThenReadd_WorksCorrectly()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        filter.Add("key1");
        filter.Add("key1");
        filter.Clear();

        // Act
        filter.Add("key2");

        // Assert
        Assert.False(filter.Contains("key1"));
        Assert.Equal(0, filter.GetCount("key1"));
        Assert.True(filter.Contains("key2"));
        Assert.Equal(1, filter.GetCount("key2"));
    }

    /// <summary>
    /// Test that multiple independent items don't interfere with each other's counts.
    /// </summary>
    [Fact]
    public void MultipleItems_IndependentCounts()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(1000, 0.01, Encoding.UTF8.GetBytes);

        // Act
        filter.Add("alpha");
        filter.Add("alpha");
        filter.Add("beta");
        filter.Add("beta");
        filter.Add("beta");
        filter.Remove("alpha");

        // Assert
        Assert.Equal(1, filter.GetCount("alpha"));
        Assert.Equal(3, filter.GetCount("beta"));
        Assert.True(filter.Contains("alpha"));
        Assert.True(filter.Contains("beta"));
    }

    /// <summary>
    /// Test that empty string works as an item.
    /// </summary>
    [Fact]
    public void EmptyString_WorksAsItem()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);

        // Act
        filter.Add(string.Empty);

        // Assert
        Assert.True(filter.Contains(string.Empty));
        Assert.Equal(1, filter.GetCount(string.Empty));
    }

    /// <summary>
    /// Test that the <see cref="CountingBloomFilter.ForSerializable{T}(int, double)"/> factory
    /// distinguishes distinct keys by serializer-derived bytes — not by <c>ToString()</c>.
    /// </summary>
    [Fact]
    public void ForSerializable_DistinguishesDistinctKeys()
    {
        // Arrange
        var filter = CountingBloomFilter.ForSerializable<SerializableWrapper<int>>(100, 0.01);

        // Act
        filter.Add(new SerializableWrapper<int>(1));
        filter.Add(new SerializableWrapper<int>(1));

        // Assert
        Assert.Equal(2, filter.GetCount(new SerializableWrapper<int>(1)));
        Assert.Equal(0, filter.GetCount(new SerializableWrapper<int>(424242)));
    }

    /// <summary>
    /// Test false positive rate stays reasonable at capacity.
    /// </summary>
    [Fact]
    public void FalsePositiveRate_StaysReasonable()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(500, 0.05, Encoding.UTF8.GetBytes);

        // Act — fill to capacity
        for (var i = 0; i < 500; i++)
        {
            filter.Add($"added-{i}");
        }

        // Check FP rate
        var falsePositives = 0;
        var testCount = 5000;
        for (var i = 0; i < testCount; i++)
        {
            if (filter.Contains($"not-added-{i}"))
            {
                falsePositives++;
            }
        }

        var actualFpRate = (double)falsePositives / testCount;

        // Assert — allow 2x target as upper bound
        Assert.True(actualFpRate < 0.10,
            $"False positive rate {actualFpRate:P2} exceeds 2x target 5%");
    }

    // ── Constructor / null-input / disposal coverage ─────────────────────────

    [Fact]
    public void Constructor_ZeroExpectedElements_Throws()
    {
        Action act = () => _ = new CountingBloomFilter<string>(0, 0.01, Encoding.UTF8.GetBytes);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("expectedElements");
    }

    [Fact]
    public void Constructor_NegativeExpectedElements_Throws()
    {
        Action act = () => _ = new CountingBloomFilter<string>(-5, 0.01, Encoding.UTF8.GetBytes);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("expectedElements");
    }

    [Fact]
    public void Constructor_FppZero_Throws()
    {
        Action act = () => _ = new CountingBloomFilter<string>(100, 0.0, Encoding.UTF8.GetBytes);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("falsePositiveProbability");
    }

    [Fact]
    public void Constructor_FppOne_Throws()
    {
        Action act = () => _ = new CountingBloomFilter<string>(100, 1.0, Encoding.UTF8.GetBytes);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("falsePositiveProbability");
    }

    [Fact]
    public void Constructor_NullItemToBytes_Throws()
    {
        Action act = () => _ = new CountingBloomFilter<string>(100, 0.01, (Func<string, byte[]>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Add_NullItem_Throws()
    {
        var f = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        Action act = () => f.Add(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Contains_NullItem_Throws()
    {
        var f = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        Action act = () => f.Contains(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Remove_NullItem_Throws()
    {
        var f = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        Action act = () => f.Remove(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetCount_NullItem_Throws()
    {
        var f = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        Action act = () => f.GetCount(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AfterDispose_AllMethodsThrowObjectDisposed()
    {
        var f = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        f.Dispose();

        ((Action)(() => f.Add("x"))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => f.Contains("x"))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => f.Remove("x"))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => f.GetCount("x"))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => f.Clear())).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var f = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        f.Dispose();
        ((Action)f.Dispose).Should().NotThrow();
    }

    [Fact]
    public void Clear_ResetsCounters()
    {
        var f = new CountingBloomFilter<string>(100, 0.01, Encoding.UTF8.GetBytes);
        f.Add("alpha");
        f.Add("beta");
        f.Contains("alpha").Should().BeTrue();
        f.Clear();
        f.Contains("alpha").Should().BeFalse();
        f.Contains("beta").Should().BeFalse();
    }

    // ── AC-7.1: Array.MaxLength overflow guard ───────────────────────────────

    /// <summary>
    /// AC-7.1: expectedElements large enough to require a counter array exceeding
    /// Array.MaxLength must throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    [Fact]
    public void Constructor_ExpectedElementsExceedingArrayMaxLength_ThrowsArgumentOutOfRange()
    {
        Action act = () => _ = new CountingBloomFilter<string>(300_000_000, 0.01, Encoding.UTF8.GetBytes);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("expectedElements");
    }

    // ── AC-7.7: custom-IHashAlgorithm constructor path ───────────────────────

    /// <summary>
    /// AC-7.7: The custom-hash constructor path wires the supplied algorithms into
    /// the actual Add/Contains flow.
    /// </summary>
    [Fact]
    public void CustomHashAlgorithmCtor_WiresAlgorithmsIntoAddContains()
    {
        var allZeroHash = new ConstantHashAlgorithm(0u);
        var filter = new CountingBloomFilter<string>(100, 0.01, allZeroHash, allZeroHash, Encoding.UTF8.GetBytes);

        filter.Add("any-key");

        // Constant-zero hash always maps to counter position 0. "other-key" hashed
        // by the same constant function also maps to position 0 → always Contains.
        filter.Contains("other-key").Should().BeTrue("constant-zero hash always maps to counter 0");
    }

    /// <summary>
    /// The counting filter documents "the same double-hashing approach" as
    /// <see cref="BloomFilter{T}"/>, so identical (n, p) inputs must derive identical
    /// parameters — including the practical k ≤ 30 cap the plain filter applies. Divergence
    /// here means the two sizing implementations have drifted apart (they are meant to share
    /// one formula).
    /// </summary>
    [Theory]
    [InlineData(1000, 0.01)]
    [InlineData(1000, 1e-10)] // beyond the k cap: uncapped k would be 33
    [InlineData(50, 0.5)]
    public void HashFunctionCount_MatchesBloomFilter_ForIdenticalInputs(int expectedElements, double falsePositiveProbability)
    {
        using var plain = new BloomFilter<string>(expectedElements, falsePositiveProbability, Encoding.UTF8.GetBytes);
        var counting = new CountingBloomFilter<string>(expectedElements, falsePositiveProbability, Encoding.UTF8.GetBytes);

        counting.HashFunctionCount.Should().Be(plain.HashFunctionCount,
            "both filters advertise the same sizing math, including the practical k ≤ 30 ceiling");
        counting.HashFunctionCount.Should().BeLessThanOrEqualTo(30);
    }
}
