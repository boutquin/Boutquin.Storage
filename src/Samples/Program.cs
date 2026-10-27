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

using Boutquin.Storage.Infrastructure.DataStructures;
using Boutquin.Storage.Infrastructure.StorageWithBloomFilter;

namespace Boutquin.Storage.Samples;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args is ["analytics"])
        {
            await AnalyticsDemo.RunAsync().ConfigureAwait(false);
            return;
        }

        if (args is ["projections"])
        {
            await ProjectionDemo.RunAsync().ConfigureAwait(false);
            return;
        }

        if (args is ["search"])
        {
            SearchDemo.Run();
            return;
        }

        if (args is ["protobuf"])
        {
            await ProtoEvolutionDemo.RunAsync().ConfigureAwait(false);
            return;
        }

        if (args is ["streaming"])
        {
            await StreamingDemo.RunAsync().ConfigureAwait(false);
            return;
        }

        _ = new InMemoryFileIndex<Key>();
        var expectedElements = 10;
        var falsePositiveProbability = 0.01; // 1% false positive rate

        using var bloomFilter = BloomFilter.ForSerializable<Key>(expectedElements, falsePositiveProbability);

        using var storageFile = new StorageFile(Directory.GetCurrentDirectory(), "AppendOnlyFileStorageEngine.db");
        var innerstore = new AppendOnlyFileStorageEngine<Key, City>(
            storageFile,
            new BinaryEntrySerializer<Key, City>());

        using var store = await BulkKeyValueStoreWithBloomFilter<Key, City>.CreateAsync(
            innerstore,
            bloomFilter).ConfigureAwait(false);

        await store.ClearAsync().ConfigureAwait(false);

        // db_set 123456 '{"name":"London","attractions":["Big Ben","London Eye"]}'
        await store.SetAsync(new Key(123456), new City("London",
        [
            new((string)"Big Ben"),
            new((string)"London Eye")
        ])).ConfigureAwait(false);

        // db_set 42 '{"name":"San Francisco","attractions":["Golden Gate Bridge"]}'
        await store.SetAsync(new Key(42), new City("San Francisco",
        [
            new((string)"Golden Gate Bridge")
        ])).ConfigureAwait(false);

        // db_get 42
        Console.WriteLine("db_get 42");
        var value = await store.TryGetValueAsync(new Key(42)).ConfigureAwait(false);
        if (value.Found)
        {
            Console.WriteLine(JsonSerializer.Serialize(value.Value));
            // Output: {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
        }
        Console.WriteLine();

        // db_set 42 '{"name":"San Francisco","attractions":["Exploratorium"]}'
        await store.SetAsync(new Key(42), new City("San Francisco",
        [
            new((string)"Exploratorium")
        ])).ConfigureAwait(false);

        // db_get 42
        Console.WriteLine("db_get 42");
        value = await store.TryGetValueAsync(new Key(42)).ConfigureAwait(false);
        if (value.Found)
        {
            Console.WriteLine(JsonSerializer.Serialize(value.Value));
            // Output: {"name":"San Francisco","attractions":["Exploratorium"]}
        }
        Console.WriteLine();

        // cat database
        Console.WriteLine("cat database");
        var items = await store.GetAllItemsAsync().ConfigureAwait(false);
        foreach (var item in items)
        {
            Console.WriteLine($"{item.Key.Value}, {JsonSerializer.Serialize(item.Value)}");
        }
        Console.WriteLine();
        // Output:
        // 123456, {"name":"London","attractions":["Big Ben","London Eye"]}
        // 42, {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
        // 42, {"name":"San Francisco","attractions":["Exploratorium"]}

        await store.CompactAsync().ConfigureAwait(false);
        // cat database
        Console.WriteLine("cat database --after compaction");
        var compacted = await store.GetAllItemsAsync().ConfigureAwait(false);
        foreach (var item in compacted)
        {
            Console.WriteLine($"{item.Key.Value}, {JsonSerializer.Serialize(item.Value)}");
        }
        Console.WriteLine();
        // Output:
        // 123456, {"name":"London","attractions":["Big Ben","London Eye"]}
        // 42, {"name":"San Francisco","attractions":["Exploratorium"]}

        // db_get 42
        Console.WriteLine("db_get 42 --after compaction");
        value = await store.TryGetValueAsync(new Key(42)).ConfigureAwait(false);
        if (value.Found)
        {
            Console.WriteLine(JsonSerializer.Serialize(value.Value));
            // Output: {"name":"San Francisco","attractions":["Exploratorium"]}
        }
        Console.WriteLine();

    }
}
