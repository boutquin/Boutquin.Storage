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
/// Tests for <see cref="ConcurrentKeyValueStore{TKey, TValue}"/>.
/// Covers CRUD operations, concurrent safety, and edge cases.
/// </summary>
public sealed class ConcurrentKeyValueStoreTests
{
    [Fact]
    public async Task SetAndGet_RoundTrip()
    {
        // Arrange
        var store = new ConcurrentKeyValueStore<int, string>();

        // Act
        await store.SetAsync(1, "hello").ConfigureAwait(true);
        var (value, found) = await store.TryGetValueAsync(1).ConfigureAwait(true);

        // Assert
        found.Should().BeTrue();
        value.Should().Be("hello");
    }

    [Fact]
    public async Task TryGetValue_MissingKey_ReturnsNotFound()
    {
        var store = new ConcurrentKeyValueStore<int, string>();

        var (_, found) = await store.TryGetValueAsync(42).ConfigureAwait(true);

        found.Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingValue()
    {
        var store = new ConcurrentKeyValueStore<int, string>();
        await store.SetAsync(1, "first").ConfigureAwait(true);
        await store.SetAsync(1, "second").ConfigureAwait(true);

        var (value, found) = await store.TryGetValueAsync(1).ConfigureAwait(true);
        found.Should().BeTrue();
        value.Should().Be("second");
    }

    [Fact]
    public async Task ContainsKeyAsync_TrueAfterSet_FalseAfterRemove()
    {
        var store = new ConcurrentKeyValueStore<int, string>();

        (await store.ContainsKeyAsync(1).ConfigureAwait(true)).Should().BeFalse();

        await store.SetAsync(1, "value").ConfigureAwait(true);
        (await store.ContainsKeyAsync(1).ConfigureAwait(true)).Should().BeTrue();

        await store.RemoveAsync(1).ConfigureAwait(true);
        (await store.ContainsKeyAsync(1).ConfigureAwait(true)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_MissingKey_NoOp()
    {
        var store = new ConcurrentKeyValueStore<int, string>();

        var act = () => store.RemoveAsync(999);
        await act.Should().NotThrowAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        var store = new ConcurrentKeyValueStore<int, string>();
        await store.SetAsync(1, "a").ConfigureAwait(true);
        await store.SetAsync(2, "b").ConfigureAwait(true);
        await store.SetAsync(3, "c").ConfigureAwait(true);

        await store.ClearAsync().ConfigureAwait(true);

        (await store.ContainsKeyAsync(1).ConfigureAwait(true)).Should().BeFalse();
        (await store.ContainsKeyAsync(2).ConfigureAwait(true)).Should().BeFalse();
        (await store.ContainsKeyAsync(3).ConfigureAwait(true)).Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentSets_AllKeysPresent_NoCorruption()
    {
        // Arrange
        var store = new ConcurrentKeyValueStore<int, string>();
        var tasks = new Task[100];

        // Act — 100 concurrent writers, each writing a unique key
        for (var i = 0; i < tasks.Length; i++)
        {
            var key = i;
            tasks[i] = Task.Run(async () =>
            {
                await store.SetAsync(key, $"value-{key}").ConfigureAwait(false);
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Assert — all keys present
        for (var i = 0; i < 100; i++)
        {
            var (value, found) = await store.TryGetValueAsync(i).ConfigureAwait(true);
            found.Should().BeTrue();
            value.Should().Be($"value-{i}");
        }
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_NoExceptions()
    {
        // Arrange
        var store = new ConcurrentKeyValueStore<int, string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Seed some data
        for (var i = 0; i < 50; i++)
        {
            await store.SetAsync(i, $"seed-{i}").ConfigureAwait(true);
        }

        // Act — concurrent reads and writes
        var writerTask = Task.Run(async () =>
        {
            var counter = 50;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await store.SetAsync(counter % 200, $"val-{counter}").ConfigureAwait(false);
                    counter++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    exceptions.Add(ex);
                }
            }
        });

        var readerTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    for (var i = 0; i < 200; i++)
                    {
                        await store.TryGetValueAsync(i).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(writerTask, readerTask).ConfigureAwait(true);

        // Assert
        exceptions.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentSameKey_LastWriterWins()
    {
        var store = new ConcurrentKeyValueStore<string, int>();
        var tasks = new Task[50];

        for (var i = 0; i < tasks.Length; i++)
        {
            var val = i;
            tasks[i] = Task.Run(async () =>
            {
                await store.SetAsync("contended-key", val).ConfigureAwait(false);
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Assert — key exists with some valid value (last writer wins)
        var (value, found) = await store.TryGetValueAsync("contended-key").ConfigureAwait(true);
        found.Should().BeTrue();
        value.Should().BeInRange(0, 49);
    }

    [Fact]
    public async Task NullKey_ThrowsArgumentNullException()
    {
        var store = new ConcurrentKeyValueStore<string, string>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.SetAsync(null!, "value")).ConfigureAwait(true);
    }

    [Fact]
    public async Task NullValue_ThrowsArgumentNullException()
    {
        var store = new ConcurrentKeyValueStore<string, string>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.SetAsync("key", null!)).ConfigureAwait(true);
    }
}
