// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
namespace Boutquin.Storage.Samples;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var index = new InMemoryFileIndex<Key>();
        var store = new AppendOnlyFileStorageEngine<Key, City>(
        new StorageFile("AppendOnlyFileStorageEngine.db"),
        new BinaryEntrySerializer<Key, City>());
        var store1 = new AppendOnlyFileStorageEngineWithIndex<Key, City>(
            new StorageFile("AppendOnlyFileStorageEngineWithIndex.db"),
            new BinaryEntrySerializer<Key, City>(),
            index);

        await store.ClearAsync();

        // db_set 123456 '{"name":"London","attractions":["Big Ben","London Eye"]}'
        await store.SetAsync(new Key(123456), new City("London",
        [
            new((string)"Big Ben"),
            new((string)"London Eye")
        ]));

        // db_set 42 '{"name":"San Francisco","attractions":["Golden Gate Bridge"]}'
        await store.SetAsync(new Key(42), new City("San Francisco",
        [
            new((string)"Golden Gate Bridge")
        ]));

        // db_get 42
        Console.WriteLine("db_get 42");
        var value = await store.TryGetValueAsync(new Key(42));
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
        ]));

        // db_get 42
        Console.WriteLine("db_get 42");
        value = await store.TryGetValueAsync(new Key(42));
        if (value.Found)
        {
            Console.WriteLine(JsonSerializer.Serialize(value.Value));
            // Output: {"name":"San Francisco","attractions":["Exploratorium"]}
        }
        Console.WriteLine();

        // cat database
        Console.WriteLine("cat database");
        var items = await store.GetAllItemsAsync();
        foreach (var item in items)
        {
            Console.WriteLine($"{item.Key.Value}, {JsonSerializer.Serialize(item.Value)}");
        }
        Console.WriteLine();
        // Output:
        // 123456, {"name":"London","attractions":["Big Ben","London Eye"]}
        // 42, {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
        // 42, {"name":"San Francisco","attractions":["Exploratorium"]}

        await store.CompactAsync();
        // cat database
        Console.WriteLine("cat database --after compaction");
        var compacted = await store.GetAllItemsAsync();
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
        value = await store.TryGetValueAsync(new Key(42));
        if (value.Found)
        {
            Console.WriteLine(JsonSerializer.Serialize(value.Value));
            // Output: {"name":"San Francisco","attractions":["Exploratorium"]}
        }
        Console.WriteLine();

    }
}