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
namespace Boutquin.Storage.Domain.Helpers;

public static class SerializationHelper
{
    public static void Serialize<T>(T obj, BinaryWriter writer)
    {
        if (obj == null)
            throw new ArgumentNullException(nameof(obj));

        var properties = typeof(T).GetProperties();
        foreach (var property in properties)
        {
            var value = property.GetValue(obj);
            if (value != null)
            {
                writer.Write(value.ToString());
            }
        }
    }

    public static T Deserialize<T>(BinaryReader reader) where T : new()
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));

        var obj = new T();
        var properties = typeof(T).GetProperties();
        foreach (var property in properties)
        {
            var propertyType = property.PropertyType;
            var value = Convert.ChangeType(reader.ReadString(), propertyType);
            property.SetValue(obj, value);
        }

        return obj;
    }
}
