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
using Boutquin.Storage.Distributed.Sync;

var directory = Directory.CreateTempSubdirectory("boutquin-sync-demo-");
try
{
    var phonePath = Path.Combine(directory.FullName, "phone.json");
    var laptopPath = Path.Combine(directory.FullName, "laptop.json");
    var phone = await JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(
        phonePath, new FixedDeviceReplicaIdProvider("phone")).ConfigureAwait(false);
    var laptop = await JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(
        laptopPath, new FixedDeviceReplicaIdProvider("laptop")).ConfigureAwait(false);
    await phone.AppendLocalAsync(new("title", "offline phone edit", 7, false)).ConfigureAwait(false);
    await laptop.AppendLocalAsync(new("title", "offline laptop edit", 7, false)).ConfigureAwait(false);
    await laptop.AppendLocalAsync(new("status", "ready", 8, false)).ConfigureAwait(false);

    phone = await JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(
        phonePath, new FixedDeviceReplicaIdProvider("phone")).ConfigureAwait(false);
    laptop = await JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(
        laptopPath, new FixedDeviceReplicaIdProvider("laptop")).ConfigureAwait(false);
    var engine = new SyncEngine<KeyValueSyncChange<string, string>, IReadOnlyDictionary<string, string>>(
        new LastWriterWinsConflictResolver<string, string>());
    var session = await engine.SynchronizeAsync(phone, laptop).ConfigureAwait(false);
    var state = await engine.MaterializeAsync(phone).ConfigureAwait(false);
    Console.WriteLine($"exchanged phone={session.LeftSent}, laptop={session.RightSent}");
    Console.WriteLine($"title={state["title"]}; status={state["status"]}");
    Console.WriteLine("equal timestamps resolve by stable replica id; retries use durable checkpoints");
}
finally
{
    directory.Delete(recursive: true);
}
