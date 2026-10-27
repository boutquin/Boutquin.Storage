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
using Boutquin.Storage.Infrastructure.Tests.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Concurrency-safety pins for <see cref="InMemoryTimeSeriesStore{TDatasetKey, TRecord}"/>:
/// an acknowledged append must stay readable when it races a <c>DeleteRangeAsync</c> that
/// empties (and therefore drops) the dataset's slot. The slot reference is resolved outside the
/// per-slot lock, so a writer or a second deleter can hold a slot that has already been removed
/// from the dataset map; these tests race exactly that window.
/// </summary>
public sealed class TimeSeriesStoreConcurrencySafetyTests
{
    private const int Iterations = 5000;

    private static readonly DateOnly s_deletedDate = new(2024, 1, 1);
    private static readonly DateOnly s_appendedDate = new(2024, 6, 1);

    private static async Task<bool> IsReadableAsync(
        InMemoryTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord> store,
        SerializableWrapper<string> ds,
        DateOnly date)
    {
        await foreach (var _ in store.GetRangeAsync(ds, date, date).ConfigureAwait(true))
        {
            return true;
        }

        return false;
    }

    [Fact]
    public async Task AppendAsync_RacingSlotEmptyingDeleteRange_AcknowledgedAppendStaysReadable()
    {
        var lost = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord>();
            var ds = new SerializableWrapper<string>("AAPL");
            await store.AppendAsync(ds, [new TimeSeriesTestRecord(s_deletedDate, 1)]).ConfigureAwait(true);

            using var gate = new ManualResetEventSlim(false);
            var delete = Task.Run(async () =>
            {
                gate.Wait();
                await store.DeleteRangeAsync(ds, s_deletedDate, s_deletedDate).ConfigureAwait(false);
            });
            var append = Task.Run(async () =>
            {
                gate.Wait();
                await store.AppendAsync(ds, [new TimeSeriesTestRecord(s_appendedDate, 2)]).ConfigureAwait(false);
            });
            gate.Set();
            await Task.WhenAll(delete, append).ConfigureAwait(true);

            if (!await IsReadableAsync(store, ds, s_appendedDate).ConfigureAwait(true))
            {
                lost++;
            }
        }

        lost.Should().Be(0, "the appended date lies outside the deleted range, so a successful append must remain readable");
    }

    [Fact]
    public async Task DeleteRangeAsync_TwoDeletersRacingAppend_DoNotDropTheReplacementSlot()
    {
        var lost = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord>();
            var ds = new SerializableWrapper<string>("AAPL");
            await store.AppendAsync(ds, [new TimeSeriesTestRecord(s_deletedDate, 1)]).ConfigureAwait(true);

            using var gate = new ManualResetEventSlim(false);
            var deleteA = Task.Run(async () =>
            {
                gate.Wait();
                await store.DeleteRangeAsync(ds, s_deletedDate, s_deletedDate).ConfigureAwait(false);
            });
            var deleteB = Task.Run(async () =>
            {
                gate.Wait();
                await store.DeleteRangeAsync(ds, s_deletedDate, s_deletedDate).ConfigureAwait(false);
            });
            var append = Task.Run(async () =>
            {
                gate.Wait();
                await store.AppendAsync(ds, [new TimeSeriesTestRecord(s_appendedDate, 2)]).ConfigureAwait(false);
            });
            gate.Set();
            await Task.WhenAll(deleteA, deleteB, append).ConfigureAwait(true);

            if (!await IsReadableAsync(store, ds, s_appendedDate).ConfigureAwait(true))
            {
                lost++;
            }
        }

        lost.Should().Be(0, "a deleter holding an already-dropped slot must not remove the slot a concurrent append created");
    }
}
