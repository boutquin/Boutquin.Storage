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
using Boutquin.Storage.Domain.Helpers;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the <see cref="BulkKeyValueStoreWithBloomFilter{TKey, TValue}"/> class.
/// Each test follows the Arrange-Act-Assert pattern to ensure thorough and accurate testing.
/// </summary>
public sealed class BulkKeyValueStoreWithBloomFilterTests
{
    private readonly Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>> _mockInnerStore;
    private readonly Mock<IBloomFilter<SerializableWrapper<int>>> _mockBloomFilter;
    private readonly BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>> _store;

    public BulkKeyValueStoreWithBloomFilterTests()
    {
        // Arrange: Initialize mocks for the inner store and Bloom filter.
        _mockInnerStore = new Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        _mockBloomFilter = new Mock<IBloomFilter<SerializableWrapper<int>>>();

        // Arrange: Create an instance of BulkKeyValueStoreWithBloomFilter using the mocks.
        _store = new BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>(_mockInnerStore.Object, _mockBloomFilter.Object);
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
    /// Test to ensure that the constructor throws an ArgumentNullException if the inner store is null.
    /// </summary>
    [Fact]
    public void Constructor_ShouldThrowArgumentNullExceptionIfInnerStoreIsNull()
    {
        // Act & Assert: Expect an ArgumentNullException when the inner store is null.
        Assert.Throws<ArgumentNullException>(() => new BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>(null, _mockBloomFilter.Object));
    }

    /// <summary>
    /// Test to ensure that the constructor throws an ArgumentNullException if the Bloom filter is null.
    /// </summary>
    [Fact]
    public void Constructor_ShouldThrowArgumentNullExceptionIfBloomFilterIsNull()
    {
        // Act & Assert: Expect an ArgumentNullException when the Bloom filter is null.
        Assert.Throws<ArgumentNullException>(() => new BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>(_mockInnerStore.Object, null));
    }
}