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
    private readonly record struct Key(long Value) : IComparable<Key>
    {
        public int CompareTo(Key other)
        {
            return Value.CompareTo(other.Value);
        }
    }
    private readonly record struct City(string Name, IEnumerable<Attraction> Attractions);
    private readonly record struct Attraction(string Name);

    public static async Task Main(string[] args)
    {
        var store = new FileKeyValueStore<Key, City>(
            "database",
            key => key.Value.ToString(),
            str => new Key(long.Parse(str)),
            value => JsonSerializer.Serialize(value),
            str => JsonSerializer.Deserialize<City>(str));

        // db_set 123456 '{"name":"London","attractions":["Big Ben","London Eye"]}'
        await store.SetAsync(new Key(123456), new City("London",
        [
            new("Big Ben"),
            new("London Eye")
        ]));

        // db_set 42 '{"name":"San Francisco","attractions":["Golden Gate Bridge"]}'
        await store.SetAsync(new Key(42), new City("San Francisco",
        [
            new("Golden Gate Bridge")
        ]));

        // db_get 42
        var value = await store.TryGetValueAsync(new Key(42));
        if (value.Found)
        {
            Console.WriteLine(JsonSerializer.Serialize(value.Value));
            // Output: {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
        }

        // db_set 42 '{"name":"San Francisco","attractions":["Exploratorium"]}'
        await store.SetAsync(new Key(42), new City("San Francisco",
        [
            new("Exploratorium")
        ]));

        // db_get 42
        value = await store.TryGetValueAsync(new Key(42));
        if (value.Found)
        {
            Console.WriteLine(JsonSerializer.Serialize(value.Value));
            // Output: {"name":"San Francisco","attractions":["Exploratorium"]}
        }

        // cat database
        var items = await store.GetAllItems();
        foreach (var item in items)
        {
            Console.WriteLine($"{item.Key.Value}, {JsonSerializer.Serialize(item.Value)}");
        }
        // Output:
        // 123456, {"name":"London","attractions":["Big Ben","London Eye"]}
        // 42, {"name":"San Francisco","attractions":["Golden Gate Bridge"]}
        // 42, {"name":"San Francisco","attractions":["Exploratorium"]}
    }
}