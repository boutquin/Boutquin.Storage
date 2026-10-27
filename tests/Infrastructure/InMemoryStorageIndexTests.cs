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
/// This class contains unit tests for the InMemoryStorageIndex class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class InMemoryStorageIndexTests
{
    /// <summary>
    /// Test to ensure that the SetAsync method correctly adds a key-value pair to the index.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAddKeyValuePair()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<int, string>();

        // Act: Add a key-value pair to the index.
        await index.SetAsync(1, "value1");

        // Assert: Check that the key-value pair was added correctly.
        var (value, found) = await index.TryGetValueAsync(1);
        Assert.True(found);
        Assert.Equal("value1", value);
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method returns false if the key does not exist.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnFalseIfKeyDoesNotExist()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<int, string>();

        // Act: Try to retrieve a value by a key that does not exist.
        var (value, found) = await index.TryGetValueAsync(1);

        // Assert: Check that the key was not found.
        Assert.False(found);
        Assert.Null(value);
    }

    /// <summary>
    /// Test to ensure that the ContainsKeyAsync method returns true if the key exists in the index.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnTrueIfKeyExists()
    {
        // Arrange: Create a new InMemoryStorageIndex and add a key-value pair.
        var index = new InMemoryStorageIndex<int, string>();
        await index.SetAsync(1, "value1");

        // Act: Check if the key exists.
        var exists = await index.ContainsKeyAsync(1);

        // Assert: Check that the key exists.
        Assert.True(exists);
    }

    /// <summary>
    /// Test to ensure that the ContainsKeyAsync method returns false if the key does not exist.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnFalseIfKeyDoesNotExist()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<int, string>();

        // Act: Check if the key exists.
        var exists = await index.ContainsKeyAsync(1);

        // Assert: Check that the key does not exist.
        Assert.False(exists);
    }

    /// <summary>
    /// Test to ensure that the RemoveAsync method correctly removes a key-value pair from the index.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldRemoveKeyValuePair()
    {
        // Arrange: Create a new InMemoryStorageIndex and add a key-value pair.
        var index = new InMemoryStorageIndex<int, string>();
        await index.SetAsync(1, "value1");

        // Act: Remove the key-value pair from the index.
        await index.RemoveAsync(1);

        // Assert: Check that the key-value pair was removed.
        var (value, found) = await index.TryGetValueAsync(1);
        Assert.False(found);
        Assert.Null(value);
    }

    /// <summary>
    /// Test to ensure that the ClearAsync method clears all key-value pairs from the index.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldClearAllKeyValuePairs()
    {
        // Arrange: Create a new InMemoryStorageIndex and add multiple key-value pairs.
        var index = new InMemoryStorageIndex<int, string>();
        await index.SetAsync(1, "value1");
        await index.SetAsync(2, "value2");

        // Act: Clear all key-value pairs from the index.
        await index.ClearAsync();

        // Assert: Check that the index is empty.
        var (value1, found1) = await index.TryGetValueAsync(1);
        var (value2, found2) = await index.TryGetValueAsync(2);
        Assert.False(found1);
        Assert.False(found2);
        Assert.Null(value1);
        Assert.Null(value2);
    }

    /// <summary>
    /// Test to ensure that SetAsync throws an exception if the key is null.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldThrowExceptionIfKeyIsNull()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<string, string>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.SetAsync(null, "value1"));
    }

    /// <summary>
    /// Test to ensure that SetAsync throws an exception if the value is null.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldThrowExceptionIfValueIsNull()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<int, string>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.SetAsync(1, null));
    }

    /// <summary>
    /// Test to ensure that TryGetValueAsync throws an exception if the key is null.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldThrowExceptionIfKeyIsNull()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<string, string>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.TryGetValueAsync(null));
    }

    /// <summary>
    /// Test to ensure that ContainsKeyAsync throws an exception if the key is null.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldThrowExceptionIfKeyIsNull()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<string, string>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.ContainsKeyAsync(null));
    }

    /// <summary>
    /// Test to ensure that RemoveAsync throws an exception if the key is null.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldThrowExceptionIfKeyIsNull()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<string, string>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.RemoveAsync(null));
    }

    /// <summary>
    /// Test to ensure that ClearAsync throws an exception if the cancellation is requested.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldThrowExceptionIfCancellationIsRequested()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<int, string>();
        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act & Assert: Check that an OperationCanceledException is thrown.
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.ClearAsync(cancellationTokenSource.Token));
    }

    /// <summary>
    /// Test to ensure that the AgainstNullOrDefault guard throws an exception if the value is null.
    /// </summary>
    [Fact]
    public void AgainstNullOrDefault_ShouldThrowExceptionIfValueIsNull()
    {
        // Arrange: Create an expression with a null value.
        string value = null;

        // Act & Assert: Check that an ArgumentNullException is thrown.
        Assert.Throws<ArgumentNullException>(() => Guard.AgainstNullOrDefault(() => value));
    }

    /// <summary>
    /// Test to ensure that the AgainstNullOrDefault guard throws an exception if the value is default for a value type.
    /// </summary>
    [Fact]
    public void AgainstNullOrDefault_ShouldThrowExceptionIfValueIsDefaultForValueType()
    {
        // Arrange: Create an expression with a default int value.
        int value = default;

        // Act & Assert: Check that an ArgumentException is thrown.
        Assert.Throws<ArgumentException>(() => Guard.AgainstNullOrDefault(() => value));
    }

    /// <summary>
    /// Test to ensure that the AgainstNullOrDefault guard does not throw an exception if the value is valid.
    /// </summary>
    [Fact]
    public void AgainstNullOrDefault_ShouldNotThrowExceptionIfValueIsValid()
    {
        // Arrange: Create an expression with a valid value.
        var intValue = 42;
        var stringValue = "valid";

        // Act & Assert: Check that no exceptions are thrown.
        Guard.AgainstNullOrDefault(() => intValue);
        Guard.AgainstNullOrDefault(() => stringValue);
    }

    /// <summary>
    /// Test to ensure that the AgainstNullOrDefault guard throws an exception if the key is null.
    /// </summary>
    [Fact]
    public async Task SetAsync_WithAgainstNullOrDefault_ShouldThrowExceptionIfKeyIsNull()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<string, string>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.SetAsync(null, "value1"));
    }

    /// <summary>
    /// Test to ensure that the AgainstNullOrDefault guard throws an exception if the value is null.
    /// </summary>
    [Fact]
    public async Task SetAsync_WithAgainstNullOrDefault_ShouldThrowExceptionIfValueIsNull()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<int, string>();

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.SetAsync(1, null));
    }

    /// <summary>
    /// Test to ensure that the AgainstNullOrDefault guard throws an exception if the value is default for a value type.
    /// </summary>
    [Fact]
    public async Task SetAsync_WithAgainstNullOrDefault_ShouldThrowExceptionIfValueIsDefaultForValueType()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<int, int>();

        // Act & Assert: Check that an ArgumentException is thrown.
        await Assert.ThrowsAsync<ArgumentException>(() => index.SetAsync(1, default));
    }

    /// <summary>
    /// Test to ensure that the AgainstNullOrDefault guard throws an exception if the key is default for a value type.
    /// </summary>
    [Fact]
    public async Task SetAsync_WithAgainstNullOrDefault_ShouldThrowExceptionIfKeyIsDefaultForValueType()
    {
        // Arrange: Create a new InMemoryStorageIndex.
        var index = new InMemoryStorageIndex<int, string>();

        // Act & Assert: Check that an ArgumentException is thrown.
        await Assert.ThrowsAsync<ArgumentException>(() => index.SetAsync(default, "value1"));
    }

    /// <summary>
    /// AC-7.2: A value-type key that implements IEquatable&lt;TKey&gt; must satisfy the typed-equality
    /// constraint on <see cref="InMemoryStorageIndex{TKey,TValue}"/>.
    /// Verifies that a struct implementing both IComparable&lt;TKey&gt; and IEquatable&lt;TKey&gt;
    /// can be used as TKey and that typed equality is respected during TryGetValueAsync lookups.
    /// </summary>
    [Fact]
    public async Task ValueTypeKey_ImplementingIEquatable_CanBeUsedAndLooksUpByValueEquality()
    {
        // Arrange: use EquatableIntKey — a struct that satisfies both IComparable<T> and IEquatable<T>.
        var index = new InMemoryStorageIndex<EquatableIntKey, string>();
        var key = new EquatableIntKey(42);

        // Act
        await index.SetAsync(key, "hello");

        // Assert: a different instance with the same value must resolve to the stored entry.
        var sameKey = new EquatableIntKey(42);
        var (value, found) = await index.TryGetValueAsync(sameKey);
        Assert.True(found, "Index must find the value using value equality on the struct key.");
        Assert.Equal("hello", value);
    }

    /// <summary>
    /// AC-7.2: Verifies that cancellation tokens are honoured by SetAsync and TryGetValueAsync
    /// on InMemoryStorageIndex with an already-cancelled token.
    /// </summary>
    [Fact]
    public async Task SetAsync_AndTryGetValueAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        var index = new InMemoryStorageIndex<int, string>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => index.SetAsync(1, "v", cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => index.TryGetValueAsync(1, cts.Token));
    }

    /// <summary>
    /// AC-7.7: InMemoryFileIndex smoke test — stores and retrieves a FileLocation by key,
    /// confirming it satisfies the IEquatable&lt;TKey&gt; constraint on its base class.
    /// </summary>
    [Fact]
    public async Task InMemoryFileIndex_StoresAndRetrievesFileLocation()
    {
        var index = new InMemoryFileIndex<int>();
        var location = new FileLocation(Offset: 1024L, Count: 256L);

        await index.SetAsync(42, location);

        var (retrieved, found) = await index.TryGetValueAsync(42);
        Assert.True(found);
        Assert.Equal(location, retrieved);
    }
}
