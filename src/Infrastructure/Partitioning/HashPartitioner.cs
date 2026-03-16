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
namespace Boutquin.Storage.Infrastructure.Partitioning;

/// <summary>
/// A hash-based partitioner that distributes keys uniformly across partitions using a hash function.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it works:</b> The key is converted to bytes via its <see cref="object.ToString"/> representation,
/// hashed using the configured <see cref="IHashAlgorithm"/>, and the hash is mapped to a partition via
/// modular arithmetic. This ensures uniform distribution regardless of key ordering.
/// </para>
///
/// <para>
/// <b>Why hash partitioning?</b> Unlike range partitioning, hash partitioning distributes keys uniformly
/// even when keys arrive in sorted order (e.g., timestamps). The trade-off is that range queries become
/// inefficient — a range scan must touch all partitions.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is thread-safe for concurrent reads. All state is immutable after construction.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class HashPartitioner<TKey> : IPartitioner<TKey>
    where TKey : IComparable<TKey>
{
    private readonly IHashAlgorithm _hashAlgorithm;

    /// <summary>
    /// Initializes a new instance of the <see cref="HashPartitioner{TKey}"/> class.
    /// </summary>
    /// <param name="partitionCount">The number of partitions. Must be at least 1.</param>
    /// <param name="hashAlgorithm">
    /// The hash algorithm to use. Defaults to <see cref="Murmur3"/> if not specified.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="partitionCount"/> is less than 1.</exception>
    public HashPartitioner(int partitionCount, IHashAlgorithm? hashAlgorithm = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);

        PartitionCount = partitionCount;
        _hashAlgorithm = hashAlgorithm ?? new Murmur3();
    }

    /// <inheritdoc />
    public int PartitionCount { get; }

    /// <inheritdoc />
    public int GetPartition(TKey key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key?.ToString() ?? string.Empty);
        var hash = _hashAlgorithm.ComputeHash(keyBytes);

        // Use unsigned modulo to avoid negative partition indices
        return (int)(hash % (uint)PartitionCount);
    }
}
