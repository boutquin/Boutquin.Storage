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
namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Provides a binary implementation of the <see cref="IEntrySerializer{TKey, TValue}"/> interface.
/// </summary>
public class BinaryEntrySerializer<TKey, TValue> : IEntrySerializer<TKey, TValue>
    where TKey : IComparable<TKey>, ISerializable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    public async Task WriteEntryAsync(Stream stream, TKey key, TValue value)
    {
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            key.Serialize(writer);
            value.Serialize(writer);
            writer.Flush();
        }
    }

    public (TKey Key, TValue Value)? ReadEntry(Stream stream)
    {
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            if (!CanRead(stream))
            {
                return null;
            }

            var key = TKey.Deserialize(reader);
            var value = TValue.Deserialize(reader);
            return (key, value);
        }
    }

    public bool CanRead(Stream stream)
    {
        return stream.Position < stream.Length;
    }
}
