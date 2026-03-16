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
using Boutquin.Storage.Infrastructure.Replication;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for the ReplicationLog class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class ReplicationLogTests
{
    // ========== Tier 3: Monotonicity validation ==========

    /// <summary>
    /// Test that AppendAsync rejects out-of-order sequence numbers.
    /// </summary>
    [Fact]
    public async Task AppendAsync_RejectsOutOfOrderSequenceNumber()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();
        await log.AppendAsync("key1", "value1", 10).ConfigureAwait(true);

        // Act & Assert — sequence number 5 is less than the latest (10)
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => log.AppendAsync("key2", "value2", 5)).ConfigureAwait(true);
    }

    /// <summary>
    /// Test that AppendAsync rejects duplicate sequence numbers.
    /// </summary>
    [Fact]
    public async Task AppendAsync_RejectsDuplicateSequenceNumber()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();
        await log.AppendAsync("key1", "value1", 10).ConfigureAwait(true);

        // Act & Assert — sequence number 10 equals the latest
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => log.AppendAsync("key2", "value2", 10)).ConfigureAwait(true);
    }

    /// <summary>
    /// Test that AppendAsync accepts strictly increasing sequence numbers.
    /// </summary>
    [Fact]
    public async Task AppendAsync_AcceptsStrictlyIncreasingSequenceNumbers()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();

        // Act — append with increasing sequence numbers
        await log.AppendAsync("key1", "value1", 1).ConfigureAwait(true);
        await log.AppendAsync("key2", "value2", 2).ConfigureAwait(true);
        await log.AppendAsync("key3", "value3", 5).ConfigureAwait(true);

        // Assert
        Assert.Equal(5, log.GetLatestSequenceNumber());
    }

    // ========== Tier 3: Binary search for GetEntriesAfterAsync ==========

    /// <summary>
    /// Test that GetEntriesAfterAsync returns correct entries with non-contiguous sequence numbers.
    /// This verifies binary search finds the right starting point.
    /// </summary>
    [Fact]
    public async Task GetEntriesAfterAsync_WithNonContiguousSequences_ReturnsCorrectEntries()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();
        await log.AppendAsync("a", "v1", 10).ConfigureAwait(true);
        await log.AppendAsync("b", "v2", 20).ConfigureAwait(true);
        await log.AppendAsync("c", "v3", 30).ConfigureAwait(true);
        await log.AppendAsync("d", "v4", 40).ConfigureAwait(true);
        await log.AppendAsync("e", "v5", 50).ConfigureAwait(true);

        // Act — get entries after sequence number 20
        var entries = await log.GetEntriesAfterAsync(20).ConfigureAwait(true);

        // Assert — should return entries 30, 40, 50
        Assert.Equal(3, entries.Count);
        Assert.Equal(30, entries[0].SequenceNumber);
        Assert.Equal(40, entries[1].SequenceNumber);
        Assert.Equal(50, entries[2].SequenceNumber);
    }

    /// <summary>
    /// Test that GetEntriesAfterAsync with sequence number 0 returns all entries.
    /// </summary>
    [Fact]
    public async Task GetEntriesAfterAsync_WithZero_ReturnsAllEntries()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();
        await log.AppendAsync("a", "v1", 1).ConfigureAwait(true);
        await log.AppendAsync("b", "v2", 2).ConfigureAwait(true);
        await log.AppendAsync("c", "v3", 3).ConfigureAwait(true);

        // Act
        var entries = await log.GetEntriesAfterAsync(0).ConfigureAwait(true);

        // Assert
        Assert.Equal(3, entries.Count);
    }

    /// <summary>
    /// Test that GetEntriesAfterAsync with sequence beyond latest returns empty.
    /// </summary>
    [Fact]
    public async Task GetEntriesAfterAsync_BeyondLatest_ReturnsEmpty()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();
        await log.AppendAsync("a", "v1", 1).ConfigureAwait(true);
        await log.AppendAsync("b", "v2", 2).ConfigureAwait(true);

        // Act
        var entries = await log.GetEntriesAfterAsync(100).ConfigureAwait(true);

        // Assert
        Assert.Empty(entries);
    }

    /// <summary>
    /// Test that GetLatestSequenceNumber returns 0 on empty log.
    /// </summary>
    [Fact]
    public void GetLatestSequenceNumber_EmptyLog_ReturnsZero()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();

        // Assert
        Assert.Equal(0, log.GetLatestSequenceNumber());
    }

    /// <summary>
    /// Test that GetEntriesAfterAsync on empty log returns empty.
    /// </summary>
    [Fact]
    public async Task GetEntriesAfterAsync_EmptyLog_ReturnsEmpty()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();

        // Act
        var entries = await log.GetEntriesAfterAsync(0).ConfigureAwait(true);

        // Assert
        Assert.Empty(entries);
    }

    /// <summary>
    /// Test that entries preserve key-value pairs correctly.
    /// </summary>
    [Fact]
    public async Task AppendAsync_PreservesKeyValuePairs()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();
        await log.AppendAsync("user:1", "Alice", 1).ConfigureAwait(true);
        await log.AppendAsync("user:2", "Bob", 2).ConfigureAwait(true);

        // Act
        var entries = await log.GetEntriesAfterAsync(0).ConfigureAwait(true);

        // Assert
        Assert.Equal("user:1", entries[0].Key);
        Assert.Equal("Alice", entries[0].Value);
        Assert.Equal("user:2", entries[1].Key);
        Assert.Equal("Bob", entries[1].Value);
    }

    /// <summary>
    /// Test that GetEntriesAfterAsync with exact match returns entries strictly after.
    /// </summary>
    [Fact]
    public async Task GetEntriesAfterAsync_ExactMatchSequence_ReturnsOnlyAfter()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();
        await log.AppendAsync("a", "v1", 10).ConfigureAwait(true);
        await log.AppendAsync("b", "v2", 20).ConfigureAwait(true);
        await log.AppendAsync("c", "v3", 30).ConfigureAwait(true);

        // Act — ask for entries after 10 (should NOT include entry with seq 10)
        var entries = await log.GetEntriesAfterAsync(10).ConfigureAwait(true);

        // Assert
        Assert.Equal(2, entries.Count);
        Assert.Equal(20, entries[0].SequenceNumber);
        Assert.Equal(30, entries[1].SequenceNumber);
    }

    /// <summary>
    /// Test binary search with sequence number between existing entries.
    /// </summary>
    [Fact]
    public async Task GetEntriesAfterAsync_BetweenExistingSequences_ReturnsCorrectSubset()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();
        await log.AppendAsync("a", "v1", 100).ConfigureAwait(true);
        await log.AppendAsync("b", "v2", 200).ConfigureAwait(true);
        await log.AppendAsync("c", "v3", 300).ConfigureAwait(true);
        await log.AppendAsync("d", "v4", 400).ConfigureAwait(true);

        // Act — sequence 150 is between 100 and 200
        var entries = await log.GetEntriesAfterAsync(150).ConfigureAwait(true);

        // Assert — should return entries 200, 300, 400
        Assert.Equal(3, entries.Count);
        Assert.Equal(200, entries[0].SequenceNumber);
    }

    /// <summary>
    /// Test that AppendAsync with first entry at sequence 0 is rejected.
    /// </summary>
    [Fact]
    public async Task AppendAsync_FirstEntryAtZero_IsRejected()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();

        // Act & Assert — sequence 0 <= latest (0), so should throw
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => log.AppendAsync("key", "value", 0)).ConfigureAwait(true);
    }

    /// <summary>
    /// Test large sequence number gaps.
    /// </summary>
    [Fact]
    public async Task AppendAsync_LargeGaps_AcceptsCorrectly()
    {
        // Arrange
        var log = new ReplicationLog<string, string>();

        // Act — large gaps between sequence numbers
        await log.AppendAsync("a", "v1", 1).ConfigureAwait(true);
        await log.AppendAsync("b", "v2", 1_000_000).ConfigureAwait(true);
        await log.AppendAsync("c", "v3", long.MaxValue - 1).ConfigureAwait(true);

        // Assert
        Assert.Equal(long.MaxValue - 1, log.GetLatestSequenceNumber());
        var entries = await log.GetEntriesAfterAsync(1).ConfigureAwait(true);
        Assert.Equal(2, entries.Count);
    }
}
