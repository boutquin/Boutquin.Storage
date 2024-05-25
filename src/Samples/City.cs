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

/// <summary>
/// Represents a city with a name and attractions.
/// </summary>
public readonly record struct City(string Name, IEnumerable<Attraction> Attractions) : ISerializable<City>
{
    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Name);
        writer.Write(Attractions.Count());
        foreach (var attraction in Attractions)
        {
            attraction.Serialize(writer);
        }
    }

    public static City Deserialize(BinaryReader reader)
    {
        var name = reader.ReadString();
        var attractionCount = reader.ReadInt32();
        var attractions = new List<Attraction>();
        for (int i = 0; i < attractionCount; i++)
        {
            attractions.Add(Attraction.Deserialize(reader));
        }
        return new City(name, attractions);
    }
}