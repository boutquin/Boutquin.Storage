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
namespace Boutquin.Storage.Distributed.Replication;

/// <summary>A transportable write captured from a replica, safe to delay, duplicate, or reorder.</summary>
/// <typeparam name="TKey">The immutable dictionary key.</typeparam>
/// <typeparam name="TValue">The borrowed immutable payload.</typeparam>
/// <remarks>Only the owning replica set creates operations. This is an in-process trusted message, not a
/// serialized or authenticated wire format. Delivery buffers the operation until all dependencies arrive.</remarks>
public sealed class ReplicationOperation<TKey, TValue> where TKey : notnull
{
    internal ReplicationOperation(TKey key, ReplicationVersion<TValue> version)
    {
        Key = key;
        Version = version;
    }
    /// <summary>The key affected by this write.</summary>
    public TKey Key { get; }
    /// <summary>The payload, identity, and causal dependencies.</summary>
    public ReplicationVersion<TValue> Version { get; }
}
