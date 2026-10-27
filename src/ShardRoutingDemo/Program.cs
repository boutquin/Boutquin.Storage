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
using Boutquin.Storage.Distributed.Partitioning;
using Boutquin.Storage.Distributed.Sharding;

var directory = Directory.CreateTempSubdirectory("boutquin-shard-demo-");
try
{
    var snapshot = new ShardMapSnapshot(0,
    [
        new ShardAssignment("shard-0000", "node-a"),
        new ShardAssignment("shard-0001", "node-a"),
        new ShardAssignment("shard-0002", "node-b"),
        new ShardAssignment("shard-0003", "node-b"),
    ]);
    var store = new JsonFileShardAssignmentStore(Path.Combine(directory.FullName, "assignments.json"));
    if (!await store.TrySaveAsync(snapshot, expectedVersion: -1).ConfigureAwait(false))
    {
        throw new InvalidOperationException("Could not create the initial shard-map snapshot.");
    }

    var persisted = await store.LoadAsync().ConfigureAwait(false)
        ?? throw new InvalidOperationException("The persisted shard map was not readable.");
    var map = new ShardMap(store, persisted);
    var router = new ShardRouter<string>(new HashPartitioner<string>(4), map);
    var before = router.Route("customer-42");
    Console.WriteLine($"map v{before.MapVersion}: {before.ShardId} -> {before.NodeId}");

    var plan = new AutomaticRebalancer(["node-a", "node-b", "node-c"]).CreatePlan(map.Snapshot);
    Console.WriteLine($"plan from v{plan.BaseVersion}: {plan.Moves.Count} metadata moves");
    await map.ApplyPlanAsync(plan).ConfigureAwait(false);
    Console.WriteLine("The plan changed assignment metadata only; this demo did not migrate record data.");

    var index = new InMemoryShardedSecondaryIndex<int, string>();
    var local = new LocalSecondaryIndexRouter<int, string>(index);
    await local.UpsertAsync("city:Toronto", new ShardedIndexEntry<int, string>("shard-0000", 1, "Ada"))
        .ConfigureAwait(false);
    await local.UpsertAsync("city:Toronto", new ShardedIndexEntry<int, string>("shard-0002", 2, "Grace"))
        .ConfigureAwait(false);
    var localMatches = await local.QueryAsync("city:Toronto", ["shard-0000", "shard-0001", "shard-0002", "shard-0003"])
        .ConfigureAwait(false);

    var global = new GlobalSecondaryIndexRouter<int, string>(index, ["global-index-0", "global-index-1"]);
    foreach (var entry in localMatches)
    {
        await global.UpsertAsync("city:Toronto", entry).ConfigureAwait(false);
    }

    var globalMatches = await global.QueryAsync("city:Toronto").ConfigureAwait(false);
    Console.WriteLine($"local scatter-gather={localMatches.Count}; global term-routed (shard {global.GetIndexShardId("city:Toronto")})={globalMatches.Count}");
}
finally
{
    directory.Delete(recursive: true);
}
