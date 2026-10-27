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
using System.Collections.Immutable;

using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Samples;

/// <summary>Runs aggregate and searchable projections, restart recovery, and deterministic rebuild.</summary>
internal static class ProjectionDemo
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "storage-projection-demo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new InMemoryEventLog<DocumentChange>();
            await log.AppendAsync(new DocumentChange("one", "durable storage", 3)).ConfigureAwait(false);
            await log.AppendAsync(new DocumentChange("two", "stream processing", 4)).ConfigureAwait(false);
            var checkpoints = new FileCheckpointStore(directory);
            var counter = new ProjectionRunner<DocumentChange, int>("counter", log, new Counter(), checkpoints,
                state => JsonSerializer.SerializeToUtf8Bytes(state), bytes => JsonSerializer.Deserialize<int>(bytes));
            var initial = await counter.RunAsync().ConfigureAwait(false);
            await log.AppendAsync(new DocumentChange("one", "searchable storage", 2)).ConfigureAwait(false);
            var restarted = new ProjectionRunner<DocumentChange, int>("counter", log, new Counter(),
                new FileCheckpointStore(directory), state => JsonSerializer.SerializeToUtf8Bytes(state),
                bytes => JsonSerializer.Deserialize<int>(bytes));
            var recovered = await restarted.RunAsync().ConfigureAwait(false);
            var rebuilt = await new ProjectionRebuilder<DocumentChange, int>(log, new Counter())
                .RebuildAsync().ConfigureAwait(false);
            if (initial.State != 7 || recovered.State != 9 || recovered != rebuilt)
            {
                throw new InvalidOperationException("Counter replay did not match its recovered checkpoint.");
            }

            var documents = new ProjectionRunner<DocumentChange, ImmutableDictionary<string, string>>(
                "documents", log, new Documents(), checkpoints,
                state => JsonSerializer.SerializeToUtf8Bytes(state),
                bytes => JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(bytes)
                    ?? throw new InvalidDataException("Missing document state."));
            var model = await documents.RunAsync().ConfigureAwait(false);
            var matches = model.State.Where(pair => pair.Value.Contains("storage", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
            if (!matches.SequenceEqual(["one"]))
            {
                throw new InvalidOperationException("Searchable read model did not apply the replacement.");
            }
            Console.WriteLine($"Counter: live={recovered.State}, rebuilt={rebuilt.State}, offset={recovered.Position}");
            Console.WriteLine($"Search 'storage': {string.Join(", ", matches)}");
            Console.WriteLine("Projection demo passed: checkpoint restart and replay agree.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed record DocumentChange(string Id, string Text, int Amount);

    private sealed class Counter : IProjection<DocumentChange, int>
    {
        public int InitialState => 0;
        public int Apply(int state, DocumentChange @event) => checked(state + @event.Amount);
    }

    private sealed class Documents : IProjection<DocumentChange, ImmutableDictionary<string, string>>
    {
        public ImmutableDictionary<string, string> InitialState => ImmutableDictionary<string, string>.Empty;
        public ImmutableDictionary<string, string> Apply(ImmutableDictionary<string, string> state, DocumentChange @event)
            => state.SetItem(@event.Id, @event.Text);
    }
}
