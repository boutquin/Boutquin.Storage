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
namespace Boutquin.Storage.Distributed.Sharding;

/// <summary>Routes through a fixed partitioner and an independently managed shard map.</summary>
public sealed class ShardRouter<TKey> : IShardRouter<TKey>
    where TKey : IComparable<TKey>
{
    private readonly IPartitioner<TKey> _partitioner;
    private readonly IShardMap _shardMap;
    private readonly Func<int, string> _shardIdFactory;

    /// <summary>Initializes a router.</summary>
    public ShardRouter(IPartitioner<TKey> partitioner, IShardMap shardMap, Func<int, string>? shardIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(partitioner);
        ArgumentNullException.ThrowIfNull(shardMap);

        _partitioner = partitioner;
        _shardMap = shardMap;
        _shardIdFactory = shardIdFactory ?? (partition => $"shard-{partition:D4}");
    }

    /// <inheritdoc />
    public ShardRoute Route(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var shardId = _shardIdFactory(_partitioner.GetPartition(key));
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        return _shardMap.Resolve(shardId);
    }
}
