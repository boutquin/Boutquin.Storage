// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the <see cref="BulkKeyValueStoreWithBloomFilter{TKey, TValue}"/> class.
/// Each test follows the Arrange-Act-Assert pattern to ensure thorough and accurate testing.
/// </summary>
public sealed class BulkKeyValueStoreWithBloomFilterTests : IAsyncLifetime
{
    private readonly Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>> _mockInnerStore;
    private readonly Mock<IBloomFilter<SerializableWrapper<int>>> _mockBloomFilter;
    private BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>> _store;

    public BulkKeyValueStoreWithBloomFilterTests()
    {
        // Arrange: Initialize mocks for the inner store and Bloom filter.
        _mockInnerStore = new Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        _mockBloomFilter = new Mock<IBloomFilter<SerializableWrapper<int>>>();
    }

    /// <summary>
    /// Creation is async because <c>CreateAsync</c> seeds the Bloom filter from the inner
    /// store's existing keys (the loose mock returns an empty item set here).
    /// </summary>
    public async Task InitializeAsync() =>
        _store = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>
            .CreateAsync(_mockInnerStore.Object, _mockBloomFilter.Object);

    public Task DisposeAsync()
    {
        _store?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test to ensure that the SetAsync method correctly adds a key-value pair to the store and the Bloom filter.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAddKeyValuePair()
    {
        // Arrange: Define a key-value pair to add.
        var key = 1;
        var value = "value1";

        // Act: Call the SetAsync method.
        await _store.SetAsync(key, value);

        // Assert: Verify that the inner store's SetAsync method was called with the correct parameters.
        _mockInnerStore.Verify(store => store.SetAsync(key, value, It.IsAny<CancellationToken>()), Times.Once);

        // Assert: Verify that the Bloom filter's Add method was called with the correct key.
        _mockBloomFilter.Verify(filter => filter.Add(key), Times.Once);
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method returns the correct value when the key is present in the Bloom filter and the inner store.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnValueIfKeyExists()
    {
        // Arrange: Define a key-value pair and set up the mocks to return the value.
        var key = 1;
        var value = "value1";
        _mockBloomFilter.Setup(filter => filter.Contains(key)).Returns(true);
        _mockInnerStore.Setup(store => store.TryGetValueAsync(key, It.IsAny<CancellationToken>())).ReturnsAsync((value, true));

        // Act: Call the TryGetValueAsync method.
        var result = await _store.TryGetValueAsync(key);

        // Assert: Verify that the result contains the correct value and indicates that the key was found.
        result.Found.Should().BeTrue();
        result.Value.Should().Be(value);
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method returns false when the key is not present in the Bloom filter.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnFalseIfKeyNotInBloomFilter()
    {
        // Arrange: Define a key and set up the mocks to indicate that the key is not in the Bloom filter.
        var key = 1;
        _mockBloomFilter.Setup(filter => filter.Contains(key)).Returns(false);

        // Act: Call the TryGetValueAsync method.
        var result = await _store.TryGetValueAsync(key);

        // Assert: Verify that the result indicates that the key was not found and the value is default.
        result.Found.Should().BeFalse();
    }

    /// <summary>
    /// Test to ensure that the ContainsKeyAsync method returns true when the key is present in both the Bloom filter and the inner store.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnTrueIfKeyExists()
    {
        // Arrange: Define a key and set up the mocks to indicate that the key is in both the Bloom filter and the inner store.
        var key = 1;
        _mockBloomFilter.Setup(filter => filter.Contains(key)).Returns(true);
        _mockInnerStore.Setup(store => store.ContainsKeyAsync(key, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act: Call the ContainsKeyAsync method.
        var result = await _store.ContainsKeyAsync(key);

        // Assert: Verify that the result is true, indicating that the key exists.
        result.Should().BeTrue();
    }

    /// <summary>
    /// Test to ensure that the ContainsKeyAsync method returns false when the key is not present in the Bloom filter.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnFalseIfKeyNotInBloomFilter()
    {
        // Arrange: Define a key and set up the mocks to indicate that the key is not in the Bloom filter.
        var key = 1;
        _mockBloomFilter.Setup(filter => filter.Contains(key)).Returns(false);

        // Act: Call the ContainsKeyAsync method.
        var result = await _store.ContainsKeyAsync(key);

        // Assert: Verify that the result is false, indicating that the key does not exist.
        result.Should().BeFalse();
    }

    /// <summary>
    /// Test to ensure that the RemoveAsync method correctly removes a key-value pair from the store.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldRemoveKeyValuePair()
    {
        // Arrange: Define a key to remove.
        var key = 1;

        // Act: Call the RemoveAsync method.
        await _store.RemoveAsync(key);

        // Assert: Verify that the inner store's RemoveAsync method was called with the correct parameters.
        _mockInnerStore.Verify(store => store.RemoveAsync(key, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test to ensure that the ClearAsync method clears both the store and the Bloom filter.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldClearStoreAndBloomFilter()
    {
        // Act: Call the ClearAsync method.
        await _store.ClearAsync();

        // Assert: Verify that the inner store's ClearAsync method was called.
        _mockInnerStore.Verify(store => store.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Assert: Verify that the Bloom filter's Clear method was called.
        _mockBloomFilter.Verify(filter => filter.Clear(), Times.Once);
    }

    /// <summary>
    /// SetBulkAsync must add every stored key to the Bloom filter even when the source
    /// enumerable yields its items only once (a lazy/one-shot sequence). The decorator
    /// enumerated <c>items</c> twice — once for the inner store, once for the filter — so a
    /// one-shot sequence left keys durably stored but never bloom-added: a PERMANENT false
    /// negative, the exact violation the bloom contract forbids ("if the filter says NOT
    /// present, the key is guaranteed absent").
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_WithOneShotEnumerable_StillAddsEveryKeyToBloomFilter()
    {
        // The inner store consumes the sequence (as the real engines do).
        _mockInnerStore
            .Setup(store => store.SetBulkAsync(
                It.IsAny<IEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>, CancellationToken>(
                (items, _) => items.ToList())
            .Returns(Task.CompletedTask);

        var yielded = 0;
        IEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> OneShot()
        {
            // Yields the two items only on the first enumeration — the shape of a consumed
            // reader / streaming source.
            if (Interlocked.Exchange(ref yielded, 1) == 1)
            {
                yield break;
            }
            yield return new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1"));
            yield return new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("v2"));
        }

        await _store.SetBulkAsync(OneShot());

        _mockBloomFilter.Verify(f => f.Add(It.IsAny<SerializableWrapper<int>>()), Times.Exactly(2),
            "every key the inner store durably persisted must be added to the Bloom filter, " +
            "or membership checks return permanent false negatives");
    }

    /// <summary>
    /// Test to ensure that the SetBulkAsync method adds multiple key-value pairs to the store and the Bloom filter.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldAddMultipleKeyValuePairs()
    {
        // Arrange: Define a collection of key-value pairs to add.
        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(1, "value1"),
            new(2, "value2")
        };

        // Act: Call the SetBulkAsync method.
        await _store.SetBulkAsync(items);

        // Assert: Verify that the inner store's SetBulkAsync method was called with the correct parameters.
        _mockInnerStore.Verify(store => store.SetBulkAsync(items, It.IsAny<CancellationToken>()), Times.Once);

        // Assert: Verify that the Bloom filter's Add method was called for each key in the collection.
        _mockBloomFilter.Verify(filter => filter.Add(1), Times.Once);
        _mockBloomFilter.Verify(filter => filter.Add(2), Times.Once);
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method calls the inner store's CompactAsync method.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldCallInnerStoreCompact()
    {
        // Act: Call the CompactAsync method.
        await _store.CompactAsync();

        // Assert: Verify that the inner store's CompactAsync method was called.
        _mockInnerStore.Verify(store => store.CompactAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test to ensure that CreateAsync throws an ArgumentNullException if the inner store is null.
    /// </summary>
    [Fact]
    public async Task CreateAsync_ShouldThrowArgumentNullExceptionIfInnerStoreIsNull()
    {
        // Act & Assert: Expect an ArgumentNullException when the inner store is null.
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>.CreateAsync(null, _mockBloomFilter.Object));
    }

    /// <summary>
    /// Test to ensure that CreateAsync throws an ArgumentNullException if the Bloom filter is null.
    /// </summary>
    [Fact]
    public async Task CreateAsync_ShouldThrowArgumentNullExceptionIfBloomFilterIsNull()
    {
        // Act & Assert: Expect an ArgumentNullException when the Bloom filter is null.
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>.CreateAsync(_mockInnerStore.Object, null));
    }

    /// <summary>
    /// Test that the class is sealed to prevent unintended subclassing.
    /// </summary>
    [Fact]
    public void Class_ShouldBeSealed()
    {
        // Act
        var type = typeof(BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>);

        // Assert
        type.IsSealed.Should().BeTrue("the class should be sealed to prevent unintended subclassing");
    }

    /// <summary>
    /// Test that the class implements IDisposable.
    /// </summary>
    [Fact]
    public void Class_ShouldImplementIDisposable()
    {
        // Act & Assert
        _store.Should().BeAssignableTo<IDisposable>();
    }

    /// <summary>
    /// Test that SetAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task SetAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.SetAsync(1, "value1"));
    }

    /// <summary>
    /// Test that TryGetValueAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.TryGetValueAsync(1));
    }

    /// <summary>
    /// Test that ContainsKeyAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.ContainsKeyAsync(1));
    }

    /// <summary>
    /// Test that RemoveAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.RemoveAsync(1));
    }

    /// <summary>
    /// Test that ClearAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task ClearAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.ClearAsync());
    }

    /// <summary>
    /// Test that GetAllItemsAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.GetAllItemsAsync());
    }

    /// <summary>
    /// Test that SetBulkAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.SetBulkAsync(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>()));
    }

    /// <summary>
    /// Test that CompactAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task CompactAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.CompactAsync());
    }

    /// <summary>
    /// Test that concurrent SetAsync calls do not corrupt state (thread safety via SemaphoreSlim).
    /// </summary>
    [Fact]
    public async Task ConcurrentSetAsync_ShouldNotCorruptState()
    {
        // Arrange
        _mockInnerStore.Setup(s => s.SetAsync(It.IsAny<SerializableWrapper<int>>(), It.IsAny<SerializableWrapper<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act: fire 20 concurrent writes
        var tasks = Enumerable.Range(1, 20).Select(i =>
            _store.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}")));

        await Task.WhenAll(tasks);

        // Assert: all 20 writes should have been forwarded
        _mockInnerStore.Verify(
            s => s.SetAsync(It.IsAny<SerializableWrapper<int>>(), It.IsAny<SerializableWrapper<string>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(20));
    }

    /// <summary>
    /// Test that Dispose can be called multiple times without throwing.
    /// </summary>
    [Fact]
    public void Dispose_CalledMultipleTimes_ShouldNotThrow()
    {
        // Act & Assert
        var act = () =>
        {
            _store.Dispose();
            _store.Dispose();
            _store.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetAllItemsAsync_ShouldReturnAllItems_AfterSetAsync()
    {
        var k1 = new SerializableWrapper<int>(1);
        var k2 = new SerializableWrapper<int>(2);
        var k3 = new SerializableWrapper<int>(3);
        var v1 = new SerializableWrapper<string>("a");
        var v2 = new SerializableWrapper<string>("b");
        var v3 = new SerializableWrapper<string>("c");

        var expected = new List<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)>
        {
            (k1, v1), (k2, v2), (k3, v3),
        };
        _mockInnerStore
            .Setup(s => s.GetAllItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var items = (await _store.GetAllItemsAsync()).ToList();

        items.Count.Should().Be(3);
        items.Should().Contain((k1, v1));
        items.Should().Contain((k2, v2));
        items.Should().Contain((k3, v3));
    }

    [Fact]
    public async Task GetAllItemsAsync_ShouldReturnEmpty_WhenStoreIsEmpty()
    {
        _mockInnerStore
            .Setup(s => s.GetAllItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<(SerializableWrapper<int>, SerializableWrapper<string>)>());

        var items = (await _store.GetAllItemsAsync()).ToList();

        items.Should().BeEmpty();
    }

    /// <summary>
    /// Pins the thread-safe Dispose guard: the <c>_disposed</c> backing field must be <c>int</c>
    /// (not <c>bool</c>) so that <c>Interlocked.Exchange(ref _disposed, 1)</c> can atomically
    /// claim the dispose slot — the identical pattern used by all sibling disposables in this repo
    /// (<c>BloomFilter</c>, <c>CountingBloomFilter</c>, <c>StorageFile</c>, <c>WriteAheadLog</c>).
    /// A plain <c>bool</c> read-then-set is a TOCTOU: two concurrent callers can both read
    /// <c>false</c> before either writes <c>true</c> and both enter the cleanup body, potentially
    /// disposing <c>_lock</c> concurrently. An <c>int</c> field is a pre-condition for the
    /// atomic-exchange guard; its absence proves the fix has not been applied.
    /// Mutation check: temporarily changing the field back to <c>bool</c> makes this test RED.
    /// </summary>
    [Fact]
    public void Dispose_DisposedField_MustBeInt_ForAtomicInterlockedGuard()
    {
        // The fix is: replace `bool _disposed` with `int _disposed` + Interlocked.Exchange guard.
        // Verifying the field type pins that the atomic-claim guard can be applied and that
        // concurrent callers cannot both read `false` and both enter the cleanup body.
        var disposedField = typeof(BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>)
            .GetField("_disposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        disposedField.Should().NotBeNull("the _disposed backing field must exist");
        disposedField!.FieldType.Should().Be(typeof(int),
            "the _disposed field must be int (not bool) so Interlocked.Exchange can atomically claim the " +
            "dispose slot — the repo-wide convention for thread-safe Dispose guards (BloomFilter, WriteAheadLog, etc.)");
    }

    /// <summary>
    /// Concurrent Dispose stress: 64 threads all call Dispose concurrently; none must throw
    /// and all subsequent method calls on the store must see it as disposed.
    /// This complements <see cref="Dispose_DisposedField_MustBeInt_ForAtomicInterlockedGuard"/>:
    /// the structural field-type test pins the guard mechanism; this test pins the observable
    /// behaviour under high contention.
    /// </summary>
    [Fact]
    public async Task Dispose_CalledConcurrently_IsIdempotentAndDoesNotThrow()
    {
        const int threads = 64;
        using var barrier = new Barrier(threads);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Create a fresh store so the class-level _store isn't shared across threads.
        var innerStore = new Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        var bloomFilter = new Mock<IBloomFilter<SerializableWrapper<int>>>();
        var store = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>
            .CreateAsync(innerStore.Object, bloomFilter.Object);

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait(); // All threads start Dispose() at the same instant
            try { store.Dispose(); }
            catch (Exception ex) { exceptions.Add(ex); }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("concurrent Dispose() calls must never throw");

        // After all concurrent disposes complete, the store must be observed as disposed.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SetAsync(1, "v"));
    }

    /// <summary>
    /// Wrapping an inner store that ALREADY contains data must not hide that data:
    /// the Bloom filter's no-false-negative guarantee ("if the filter says absent, the key is
    /// definitely absent" — DDIA 2e Ch. 4) only holds when the filter covers every key in the
    /// store. A decorator built over a pre-populated store (process restart, recovery, engine
    /// written before wrapping) must therefore seed the filter from the inner store's existing
    /// keys, or the short-circuit read path returns "not found" for durably stored keys.
    /// </summary>
    [Fact]
    public async Task PrePopulatedInnerStore_KeyIsRetrievableThroughDecorator()
    {
        // Arrange: an inner store that durably holds key 1 (simulating pre-existing on-disk data),
        // and a REAL (empty) Bloom filter — exactly the state after a process restart.
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("pre-existing");

        var innerStore = new Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        innerStore.Setup(s => s.GetAllItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([(key, value)]);
        innerStore.Setup(s => s.TryGetValueAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((value, true));
        innerStore.Setup(s => s.ContainsKeyAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var realBloomFilter = new BloomFilter<SerializableWrapper<int>>(
            expectedElements: 100,
            falsePositiveProbability: 0.01,
            itemToBytes: k => BitConverter.GetBytes(k.Value));

        using var store = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>
            .CreateAsync(innerStore.Object, realBloomFilter);

        // Act: read the pre-existing key through the decorator.
        var result = await store.TryGetValueAsync(key);
        var contains = await store.ContainsKeyAsync(key);

        // Assert: the durably stored key must be visible — a false negative here would
        // violate the one guarantee the Bloom-filter read path rests on.
        result.Found.Should().BeTrue("a key present in the wrapped store must never be hidden by the filter");
        result.Value.Should().Be(value);
        contains.Should().BeTrue();
    }
}
