// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.Interfaces.Batch;

namespace Boutquin.Storage.Infrastructure.Batch;

/// <summary>Partitions stable key bytes using deterministic FNV-1a hashing.</summary>
/// <typeparam name="T">Row type.</typeparam>
/// <remarks>Reference batch partitioner, not a security hash. Key encoding must be canonical
/// across producers. Hash partitioning co-locates keys but does not bound a skewed partition's size.</remarks>
public sealed class HashShufflePartitioner<T> : IShufflePartitioner<T>
{
    private readonly Func<T, ReadOnlyMemory<byte>> _key;
    /// <summary>Creates a partitioner using caller-defined canonical key bytes.</summary>
    /// <param name="key">Deterministic key encoder.</param>
    public HashShufflePartitioner(Func<T, ReadOnlyMemory<byte>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
    }
    /// <inheritdoc />
    public int GetPartition(T item, int partitionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        var hash = 14695981039346656037UL;
        foreach (var value in _key(item).Span) { hash = unchecked((hash ^ value) * 1099511628211UL); }
        return (int)(hash % (uint)partitionCount);
    }
}
