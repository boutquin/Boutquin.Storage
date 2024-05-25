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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Defines an interface for serializing and deserializing key-value entries.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TValue">The type of the value.</typeparam>
public interface IEntrySerializer<TKey, TValue>
    where TKey : IComparable<TKey>
    where TValue : ISerializable<TValue>
{
    Task WriteEntryAsync(Stream stream, TKey key, TValue value);

    (TKey Key, TValue Value)? ReadEntry(Stream stream);

    bool CanRead(Stream stream);
}