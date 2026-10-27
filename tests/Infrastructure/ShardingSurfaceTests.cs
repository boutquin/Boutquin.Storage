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
namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class ShardingSurfaceTests
{
    [Fact]
    public void DistributedAssembly_ExposesTheShardingOperationsSurface()
    {
        string[] typeNames =
        [
            "Interfaces.Sharding.IShardMap",
            "Interfaces.Sharding.IShardAssignmentStore",
            "Interfaces.Sharding.IShardRouter`1",
            "Interfaces.Sharding.IRebalancer",
            "Interfaces.Sharding.IHotKeyMitigator`1",
            "Interfaces.Sharding.IShardedSecondaryIndex`2",
            "Sharding.ShardMap",
            "Sharding.JsonFileShardAssignmentStore",
            "Sharding.ShardRouter`1",
            "Sharding.ManualRebalancer",
            "Sharding.AutomaticRebalancer",
            "Sharding.SaltedHotKeyStrategy`1",
            "Sharding.LocalSecondaryIndexRouter`2",
            "Sharding.GlobalSecondaryIndexRouter`2",
            "Sharding.InMemoryShardedSecondaryIndex`2",
        ];

        var assembly = typeof(HashPartitioner<>).Assembly;
        var missing = typeNames
            .Where(name => assembly.GetType($"Boutquin.Storage.Distributed.{name}") is not { IsPublic: true })
            .ToArray();

        missing.Should().BeEmpty();
    }
}
