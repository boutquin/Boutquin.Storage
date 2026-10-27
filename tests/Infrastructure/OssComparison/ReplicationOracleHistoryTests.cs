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

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class ReplicationOracleHistoryTests
{
    [Theory]
    [InlineData(17)]
    [InlineData(73)]
    [InlineData(2026)]
    public void CausalReplication_ReorderedDuplicateExchange_MatchesIndependentOperationDag(int seed)
    {
        string[] names = ["a", "b", "c"];
        var replicas = new MultiLeaderReplicaSet<int, int>(names, new VersionVectorResolver<int>());
        HashSet<int>[] known = [[], [], []];
        var parents = new Dictionary<int, HashSet<int>>();
        var operations = new Dictionary<int, (int Key, int Value)>();
        var random = new Random(seed);
        for (var step = 0; step < 160; step++)
        {
            var target = random.Next(names.Length);
            if (random.Next(3) != 0)
            {
                var key = random.Next(9);
                parents.Add(step, new HashSet<int>(known[target]));
                operations.Add(step, (key, step + 1));
                replicas.Write(names[target], key, step + 1);
                known[target].Add(step);
            }
            else
            {
                var source = random.Next(names.Length);
                foreach (var operation in replicas.Capture(names[source]).Reverse())
                {
                    replicas.Deliver(names[target], operation);
                    replicas.Deliver(names[target], operation);
                }
                known[target].UnionWith(known[source]);
            }
            for (var replica = 0; replica < names.Length; replica++)
            {
                for (var key = 0; key < 9; key++)
                {
                    var candidates = known[replica].Where(id => operations[id].Key == key).ToArray();
                    // A candidate survives exactly when no observed later write to this key names it
                    // in its transitive causal past. This oracle uses event ancestry, not vector clocks.
                    var expected = candidates.Where(id => !candidates.Any(later => parents[later].Contains(id)))
                        .Select(id => operations[id].Value).ToArray();
                    replicas.Read(names[replica], key).Values.Should().BeEquivalentTo(expected,
                        $"seed {seed}, step {step}, replica {replica}, key {key}");
                }
            }
        }
    }
}
