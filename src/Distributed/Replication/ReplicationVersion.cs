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

/// <summary>An immutable version envelope identifying one causally ordered write.</summary>
/// <typeparam name="T">An immutable or caller-owned value that must not change after writing.</typeparam>
/// <remarks>Lamport timestamps preserve causal order, not wall-clock recency. Origin and sequence uniquely
/// identify writes within the owning replica set. Context includes this write and all its dependencies.</remarks>
public sealed class ReplicationVersion<T>
{
    internal ReplicationVersion(T value, string origin, long sequence, long timestamp, SessionToken context)
    {
        Value = value;
        Origin = origin;
        Sequence = sequence;
        LamportTimestamp = timestamp;
        Context = context;
    }
    /// <summary>The borrowed immutable payload.</summary>
    public T Value { get; }
    /// <summary>The originating replica identifier.</summary>
    public string Origin { get; }
    /// <summary>The consecutive, positive per-origin write number.</summary>
    public long Sequence { get; }
    /// <summary>The logical timestamp used by deterministic last-writer-wins resolution.</summary>
    public long LamportTimestamp { get; }
    /// <summary>The causal frontier including this write.</summary>
    public SessionToken Context { get; }
}
