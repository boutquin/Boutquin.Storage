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

using System.Text.Json;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests.Analytics;

public sealed class MaterializedAggregateImmutabilityTests
{
    [Fact]
    public void InitialState_CannotBeChangedThroughThePublishedDictionary()
    {
        var view = View(new InMemoryEventLog<KeyValuePair<string, decimal>>(), new InMemoryCheckpointStore());
        TryMutate(view.Current.State, "Injected", 999m);
        view.Current.Position.Should().Be(-1);
        view.Current.State.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rebuild_PublishesImmutableEmptyAndPopulatedSnapshots(bool withEvent)
    {
        var log = new InMemoryEventLog<KeyValuePair<string, decimal>>();
        if (withEvent)
        {
            await log.AppendAsync(new("North", 6m)).ConfigureAwait(true);
        }
        var view = View(log, new InMemoryCheckpointStore());
        var rebuilt = await view.RebuildAsync().ConfigureAwait(true);
        TryMutate(rebuilt.State, "North", 999m);
        view.Current.Position.Should().Be(withEvent ? 0 : -1);
        if (withEvent)
        {
            view.Current.State["north"].Should().Be(6m);
        }
        else
        {
            view.Current.State.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task DecodedState_IsCopiedAndKeepsComparerEvenWhenNoEventsRemain()
    {
        var log = new InMemoryEventLog<KeyValuePair<string, decimal>>();
        var store = new InMemoryCheckpointStore();
        await log.AppendAsync(new("North", 6m)).ConfigureAwait(true);
        await View(log, store).RefreshAsync().ConfigureAwait(true);
        Dictionary<string, decimal>? retainedDecoded = null;
        IReadOnlyDictionary<string, decimal>? retainedEncoded = null;
        var reopened = new MaterializedAggregateView<string, decimal>("immutable", log, store,
            (sum, value) => sum + value,
            state =>
            {
                retainedEncoded = state;
                return JsonSerializer.SerializeToUtf8Bytes(state);
            },
            bytes => retainedDecoded = JsonSerializer.Deserialize<Dictionary<string, decimal>>(bytes)
                ?? throw new InvalidDataException(), StringComparer.OrdinalIgnoreCase);
        var restored = await reopened.RefreshAsync().ConfigureAwait(true);
        retainedDecoded.Should().NotBeNull();
        retainedDecoded!["North"] = 999m;
        retainedDecoded["Injected"] = 1m;
        TryMutate(restored.State, "North", 500m);
        reopened.Current.State.Should().HaveCount(1);
        reopened.Current.State.TryGetValue("north", out var restoredAmount).Should().BeTrue();
        restoredAmount.Should().Be(6m);
        await log.AppendAsync(new("north", 4m)).ConfigureAwait(true);
        var updated = await reopened.RefreshAsync().ConfigureAwait(true);
        TryMutate(retainedEncoded!, "North", 1000m);
        updated.State.Should().HaveCount(1);
        updated.State["NORTH"].Should().Be(10m);
        restored.State["north"].Should().Be(6m);
    }

    private static MaterializedAggregateView<string, decimal> View(
        InMemoryEventLog<KeyValuePair<string, decimal>> log, InMemoryCheckpointStore store)
        => new("immutable", log, store, (sum, value) => sum + value,
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<Dictionary<string, decimal>>(bytes) ?? throw new InvalidDataException(),
            StringComparer.OrdinalIgnoreCase);

    private static void TryMutate(IReadOnlyDictionary<string, decimal> state, string key, decimal value)
    {
        if (state is IDictionary<string, decimal> dictionary)
        {
            try
            {
                dictionary[key] = value;
            }
            catch (NotSupportedException)
            {
                // Immutable dictionary implementations may expose a throwing mutable interface.
            }
        }
    }
}
