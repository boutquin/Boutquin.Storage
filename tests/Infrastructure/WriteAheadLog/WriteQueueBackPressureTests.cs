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
#nullable disable

using System.Reflection;

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// Back-pressure tests for <see cref="WriteAheadLog{TKey, TValue}"/>'s group-commit
/// write queue. The write queue must be a BOUNDED channel so that when the flusher loop
/// falls behind under sustained write pressure, producers asynchronously wait
/// (back-pressure) instead of queuing without limit — an unbounded queue plus the rented
/// <c>ArrayPool</c> frame each request pins is a memory-exhaustion hazard under sustained
/// overload with high fan-in concurrency.
/// </summary>
public sealed class WriteQueueBackPressureTests : IDisposable
{
    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"WriteQueueBackPressureTests_{Guid.NewGuid():N}");

    public WriteQueueBackPressureTests() => Directory.CreateDirectory(_walDirectory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_walDirectory))
            {
                Directory.Delete(_walDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// The WAL options expose a positive, configurable write-queue capacity by default — the
    /// existence of the bound is the structural guarantee that the queue cannot grow without
    /// limit.
    /// </summary>
    [Fact]
    public void WriteAheadLogOptions_HasBoundedWriteQueueCapacity_ByDefault()
    {
        var opts = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions();
        opts.WriteQueueCapacity.Should().BeGreaterThan(
            0, "the write queue must be bounded by default so producers apply back-pressure when the flusher falls behind");
    }

    /// <summary>
    /// Out-of-range capacities are rejected at construction (fail-fast), matching the validation
    /// discipline applied to the other group-commit knobs.
    /// </summary>
    [Fact]
    public void Constructor_RejectsNonPositiveWriteQueueCapacity()
    {
        var act = () => new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(
                _walDirectory,
                new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions
                {
                    WriteQueueCapacity = 0,
                });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The constructed write queue is a BOUNDED channel — verified structurally by reflecting on
    /// the private channel field's runtime type. An unbounded channel never blocks producers and
    /// is the defect this guards against.
    /// </summary>
    [Fact]
    public void WriteQueue_IsBoundedChannel()
    {
        using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(
                _walDirectory,
                new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions
                {
                    WriteQueueCapacity = 4,
                });

        var channelField = typeof(global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>)
            .GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic);
        channelField.Should().NotBeNull("the group-commit write queue field must exist");

        var channel = channelField.GetValue(wal);
        channel.Should().NotBeNull("group commit is the only durability mode in v2.0.0");

        // BoundedChannel<T> is the internal type Channel.CreateBounded returns; an unbounded
        // channel's type name contains "Unbounded". Assert on the type-name shape (the concrete
        // type is internal to System.Threading.Channels).
        channel.GetType().Name.Should().Contain(
            "Bounded", "the write queue must be a bounded channel so producers apply back-pressure");
        channel.GetType().Name.Should().NotContain(
            "Unbounded", "an unbounded write queue can grow without limit under sustained write pressure");
    }

    /// <summary>
    /// Functional non-regression: with a small bounded write-queue capacity, more appends than
    /// the capacity still all succeed and recover. Back-pressure (FullMode.Wait) must make
    /// producers wait for space, never drop frames — durability is preserved.
    /// </summary>
    [Fact]
    public async Task SmallCapacity_AllAppendsSucceedAndRecover_NoFrameDropped()
    {
        const int appendCount = 200; // ≫ a small capacity → forces the producer to wait repeatedly.

        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(
                _walDirectory,
                new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions
                {
                    WriteQueueCapacity = 2,
                }))
        {
            var tasks = Enumerable.Range(0, appendCount)
                .Select(i => wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"v_{i}"),
                    (ulong)(i + 1)))
                .ToArray();

            await Task.WhenAll(tasks);
        }

        using var reader = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory);
        var entries = await reader.RecoverAsync();

        entries.Should().HaveCount(
            appendCount, "FullMode.Wait back-pressure makes producers wait for space — it must never drop a frame");
        entries.Select(e => e.Key.Value).Should().BeEquivalentTo(Enumerable.Range(0, appendCount));
    }
}
