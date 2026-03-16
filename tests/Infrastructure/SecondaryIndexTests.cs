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
/// Unit tests for the SecondaryIndex class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SecondaryIndexTests
{
    // Helper: index users by department (string → string)
    private static SecondaryIndex<SerializableWrapper<int>, string, SerializableWrapper<string>> CreateDepartmentIndex()
    {
        return new SecondaryIndex<SerializableWrapper<int>, string, SerializableWrapper<string>>(
            value => new SerializableWrapper<string>(value.Split(':')[0])); // "engineering:Alice" → "engineering"
    }

    /// <summary>
    /// Test that indexing a value and looking it up works.
    /// </summary>
    [Fact]
    public async Task Index_AndLookup_ReturnsCorrectPrimaryKey()
    {
        // Arrange
        var index = CreateDepartmentIndex();
        var primaryKey = new SerializableWrapper<int>(1);

        // Act
        await index.IndexAsync(primaryKey, "engineering:Alice");
        var results = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();

        // Assert
        Assert.Single(results);
        Assert.Equal(1, results[0].Value);
    }

    /// <summary>
    /// Test that lookup returns multiple primary keys for same index key.
    /// </summary>
    [Fact]
    public async Task Lookup_ReturnsMultiplePrimaryKeys_ForSameIndexKey()
    {
        // Arrange
        var index = CreateDepartmentIndex();

        // Act
        await index.IndexAsync(new SerializableWrapper<int>(1), "engineering:Alice");
        await index.IndexAsync(new SerializableWrapper<int>(2), "engineering:Bob");
        var results = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();

        // Assert
        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Test that lookup on non-existent index key returns empty.
    /// </summary>
    [Fact]
    public async Task Lookup_NonExistentIndexKey_ReturnsEmpty()
    {
        // Arrange
        var index = CreateDepartmentIndex();

        // Act
        var results = (await index.LookupAsync(new SerializableWrapper<string>("marketing"))).ToList();

        // Assert
        Assert.Empty(results);
    }

    /// <summary>
    /// Test that remove removes primary key from index.
    /// </summary>
    [Fact]
    public async Task Remove_RemovesPrimaryKeyFromIndex()
    {
        // Arrange
        var index = CreateDepartmentIndex();
        var primaryKey = new SerializableWrapper<int>(1);
        await index.IndexAsync(primaryKey, "engineering:Alice");

        // Act
        await index.RemoveAsync(primaryKey, "engineering:Alice");
        var results = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();

        // Assert
        Assert.Empty(results);
    }

    /// <summary>
    /// Test that clear empties the index.
    /// </summary>
    [Fact]
    public async Task Clear_EmptiesIndex()
    {
        // Arrange
        var index = CreateDepartmentIndex();
        await index.IndexAsync(new SerializableWrapper<int>(1), "engineering:Alice");
        await index.IndexAsync(new SerializableWrapper<int>(2), "marketing:Carol");

        // Act
        await index.ClearAsync();

        // Assert
        var eng = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();
        var mkt = (await index.LookupAsync(new SerializableWrapper<string>("marketing"))).ToList();
        Assert.Empty(eng);
        Assert.Empty(mkt);
    }

    /// <summary>
    /// Test that null key throws ArgumentNullException.
    /// </summary>
    [Fact]
    public async Task NullArguments_ThrowArgumentNullException()
    {
        // Arrange
        var index = CreateDepartmentIndex();

        // Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.IndexAsync(null!, "value"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.IndexAsync(new SerializableWrapper<int>(1), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => index.LookupAsync(null!));
    }

    /// <summary>
    /// Test that removing a non-existent primary key is a no-op.
    /// </summary>
    [Fact]
    public async Task Remove_NonExistentPrimaryKey_IsNoOp()
    {
        // Arrange
        var index = CreateDepartmentIndex();
        await index.IndexAsync(new SerializableWrapper<int>(1), "engineering:Alice");

        // Act — remove a primary key that was never indexed
        await index.RemoveAsync(new SerializableWrapper<int>(999), "engineering:Nobody");

        // Assert — original entry still exists
        var results = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();
        Assert.Single(results);
    }

    /// <summary>
    /// Test that duplicate primary keys under same index key are deduplicated.
    /// </summary>
    [Fact]
    public async Task Index_DuplicatePrimaryKey_IsIdempotent()
    {
        // Arrange
        var index = CreateDepartmentIndex();
        var pk = new SerializableWrapper<int>(1);

        // Act — index same primary key twice with same derived key
        await index.IndexAsync(pk, "engineering:Alice");
        await index.IndexAsync(pk, "engineering:Alice");

        // Assert — HashSet deduplicates, should still be 1
        var results = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();
        Assert.Single(results);
    }

    /// <summary>
    /// Test that removing last primary key for an index key cleans up the entry.
    /// </summary>
    [Fact]
    public async Task Remove_LastPrimaryKey_CleansUpIndexKey()
    {
        // Arrange
        var index = CreateDepartmentIndex();
        await index.IndexAsync(new SerializableWrapper<int>(1), "engineering:Alice");
        await index.IndexAsync(new SerializableWrapper<int>(2), "engineering:Bob");

        // Act — remove both
        await index.RemoveAsync(new SerializableWrapper<int>(1), "engineering:Alice");
        await index.RemoveAsync(new SerializableWrapper<int>(2), "engineering:Bob");

        // Assert — empty results (entry cleaned up)
        var results = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();
        Assert.Empty(results);
    }

    /// <summary>
    /// Test that multiple index keys work independently.
    /// </summary>
    [Fact]
    public async Task MultipleIndexKeys_WorkIndependently()
    {
        // Arrange
        var index = CreateDepartmentIndex();
        await index.IndexAsync(new SerializableWrapper<int>(1), "engineering:Alice");
        await index.IndexAsync(new SerializableWrapper<int>(2), "marketing:Bob");
        await index.IndexAsync(new SerializableWrapper<int>(3), "engineering:Carol");

        // Act
        var eng = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();
        var mkt = (await index.LookupAsync(new SerializableWrapper<string>("marketing"))).ToList();

        // Assert
        Assert.Equal(2, eng.Count);
        Assert.Single(mkt);
    }

    /// <summary>
    /// Test concurrent index and lookup operations.
    /// </summary>
    [Fact]
    public async Task ConcurrentOperations_MaintainConsistency()
    {
        // Arrange
        var index = CreateDepartmentIndex();
        var tasks = new List<Task>();

        // Act — concurrently index 50 items
        for (var i = 0; i < 50; i++)
        {
            var pk = new SerializableWrapper<int>(i);
            var value = $"engineering:User{i}";
            tasks.Add(index.IndexAsync(pk, value));
        }
        await Task.WhenAll(tasks);

        // Assert — all 50 should be indexed
        var results = (await index.LookupAsync(new SerializableWrapper<string>("engineering"))).ToList();
        Assert.Equal(50, results.Count);
    }

    /// <summary>
    /// Test that constructor throws for null key extractor.
    /// </summary>
    [Fact]
    public void Constructor_NullKeyExtractor_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SecondaryIndex<SerializableWrapper<int>, string, SerializableWrapper<string>>(null!));
    }
}
