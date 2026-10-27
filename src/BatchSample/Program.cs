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

using System.Globalization;
using System.Text.Json;

using Boutquin.Storage.Infrastructure.Batch;
using Boutquin.Storage.Infrastructure.Events;

var directory = Path.Combine(Path.GetTempPath(), "batch-demo-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
ShuffleFileSet<int>? partitions = null;
try
{
    var sorter = new ExternalMergeSorter<int>(directory, Comparer<int>.Default,
        value => JsonSerializer.SerializeToUtf8Bytes(value), bytes => JsonSerializer.Deserialize<int>(bytes.Span),
        maxItemsInMemory: 8, mergeFanIn: 3);
    var outputLog = new InMemoryEventLog<KeyValuePair<string, int>>();
    var view = new InMemoryReadModelStore<string, int>();
    var graph = new BatchJobGraph([
        new BatchTask("aggregate-and-publish", ["sort-and-shuffle"], async ct =>
        {
            for (var partition = 0; partition < partitions!.PartitionCount; partition++)
            {
                var rows = new List<int>();
                await foreach (var row in partitions.ReadPartitionAsync(partition, ct).ConfigureAwait(false)) { rows.Add(row); }
                var groups = new MiniDataFrame<int>(rows).GroupBy(value => value % 3,
                    (key, values) => new KeyValuePair<string, int>(key.ToString(CultureInfo.InvariantCulture), values.Sum()));
                foreach (var group in groups.Rows)
                {
                    var position = await outputLog.AppendAsync(group, ct).ConfigureAwait(false);
                    await view.SetAsync(group.Key, group.Value, position, ct).ConfigureAwait(false);
                }
            }
        }),
        new BatchTask("sort-and-shuffle", [], async ct =>
        {
            partitions = await ShuffleFileSet<int>.CreateAsync(sorter.SortAsync(Input(), ct), directory,
                new HashShufflePartitioner<int>(value => JsonSerializer.SerializeToUtf8Bytes(value % 3)), 4,
                value => JsonSerializer.SerializeToUtf8Bytes(value), bytes => JsonSerializer.Deserialize<int>(bytes.Span), ct: ct).ConfigureAwait(false);
        }),
    ]);
    var order = await new BatchJobRunner().RunAsync(graph).ConfigureAwait(false);
    var snapshot = await view.SnapshotAsync().ConfigureAwait(false);
    if (snapshot.Count != 3 || snapshot.Values.Sum() != 19900) { throw new InvalidOperationException("Derived aggregate mismatch."); }
    Console.WriteLine("DAG: " + string.Join(" -> ", order));
    Console.WriteLine("Materialized sums: " + string.Join(", ", snapshot.OrderBy(row => row.Key).Select(row => $"{row.Key}={row.Value}")));
    Console.WriteLine("200 input rows, runs of eight, three-way merges; derived aggregate total=19900.");
}
finally
{
    partitions?.Dispose();
    Directory.Delete(directory, recursive: true);
}

static async IAsyncEnumerable<int> Input()
{
    for (var value = 199; value >= 0; value--) { yield return value; }
    await Task.CompletedTask.ConfigureAwait(false);
}
