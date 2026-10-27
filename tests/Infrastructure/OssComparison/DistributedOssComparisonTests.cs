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

// Adapted selected scenarios: etcd Authors (2015), Lightbend Inc. (2009-2022),
// Damian Gryski (2014), Google LLC (2016). See Distributed/provenance.md and retained licenses.
// Modified for Storage's APIs; no upstream engine is linked or run by these tests.
using System.Security.Cryptography;
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class DistributedOssComparisonTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Porcupine_RegisterModel_OverlappingWitnessAndStaleReadCounterexample(bool valid)
    {
        var writeValue = valid ? 100 : 200;
        HistoryOperation<int, int>[] history =
        [
            new(0, writeValue, 0, 0, 100, true),
            new(1, -1, writeValue, valid ? 25 : 10, valid ? 75 : 30, true),
            new(2, -1, 0, valid ? 30 : 40, valid ? 60 : 90, true)
        ];
        var verdict = LinearizabilityChecker.Check(history, 0,
            (state, input, output) => input < 0 ? (output == state, state) : (true, input));
        verdict.Should().Be(valid ? LinearizabilityResult.Valid : LinearizabilityResult.Invalid);
    }

    [Theory]
    [InlineData(null, "b", true)]
    [InlineData(null, "c", true)]
    [InlineData("b", "b", true)]
    [InlineData("c", "c", true)]
    [InlineData("b", "c", false)]
    [InlineData("c", "b", false)]
    public void Etcd_FollowerVote_AtMostOneCandidatePerTerm(string? priorVote, string candidate, bool granted)
    {
        var state = new InMemoryRaftPersistentState();
        state.SetTermAndVote(1, priorVote);
        var voter = new RaftNode<string>("a", persistentState: state);
        voter.Inbox.Enqueue(new RequestVoteRequest(1, candidate, -1, 0));
        voter.ProcessMessages();

        voter.Outbox.TryDequeue(out var reply).Should().BeTrue();
        reply.Message.Should().BeOfType<RequestVoteResponse>().Which.VoteGranted.Should().Be(granted);
        state.VotedFor.Should().Be(granted ? candidate : priorVote);
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(3, 0, false)]
    [InlineData(3, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(5, 0, false)]
    [InlineData(5, 1, false)]
    [InlineData(5, 2, true)]
    [InlineData(5, 3, true)]
    [InlineData(5, 4, true)]
    public async Task Etcd_LeaderAcknowledgeCommit_RequiresDistinctMajority(int size, int acknowledgments, bool committed)
    {
        var leader = new RaftNode<string>("a");
        var peers = Enumerable.Range(1, size - 1).Select(i => $"p{i}").ToArray();
        leader.PeerIds.AddRange(peers);
        leader.StartElection();
        foreach (var peer in peers)
        {
            leader.Inbox.Enqueue(new RequestVoteResponse(leader.CurrentTerm, true, peer));
        }
        leader.ProcessMessages();
        leader.State.Should().Be(RaftNodeState.Leader);
        await leader.ProposeAsync("entry").ConfigureAwait(true);
        foreach (var peer in peers.Take(acknowledgments))
        {
            // Duplication cannot manufacture another member's durable acknowledgment.
            leader.Inbox.Enqueue(new AppendEntriesResponse(leader.CurrentTerm, true, peer, 0));
            leader.Inbox.Enqueue(new AppendEntriesResponse(leader.CurrentTerm, true, peer, 0));
        }
        leader.ProcessMessages();
        leader.GetCommittedLog().Count.Should().Be(committed ? 1 : 0);
    }

    [Fact]
    public void Akka_GCounter_HistoriesMergeByComponent()
    {
        var left = Counter(7, 10);
        var right = Counter(4, 6);
        left.Value.Should().Be(17);
        right.Value.Should().Be(10);
        left.Merge(right).Value.Should().Be(17);
        right.Merge(left).Value.Should().Be(17);
    }

    private static GCounter Counter(int a, int b)
    {
        var result = new GCounter();
        for (var i = 0; i < a; i++) { result.Increment("a"); }
        for (var i = 0; i < b; i++) { result.Increment("b"); }
        return result;
    }

    [Fact]
    public void Akka_VectorClock_EqualExtendedAndConcurrentHistories()
    {
        var first = new VectorClock();
        var same = new VectorClock();
        foreach (var node in new[] { "1", "2", "1" })
        {
            first.Increment(node);
            same.Increment(node);
        }
        first.CompareCausally(same).Should().Be(VectorClockComparison.Equal);
        same.Increment("3");
        first.CompareCausally(same).Should().Be(VectorClockComparison.Before);
        same.CompareCausally(first).Should().Be(VectorClockComparison.After);
        var concurrent = new VectorClock();
        concurrent.Increment("4");
        concurrent.CompareCausally(first).Should().Be(VectorClockComparison.Concurrent);
    }

    [Fact]
    public void Akka_OrSet_OverlappingRemovedElementsDoNotEraseConcurrentAdds()
    {
        var left = new ORSet<string>();
        var right = new ORSet<string>();
        foreach (var value in new[] { "u1", "u2", "u3" }) { left.Add("a", value); }
        left.Remove("u1");
        left.Remove("u3");
        foreach (var value in new[] { "u1", "u2", "u3", "u4" }) { right.Add("b", value); }
        right.Remove("u3");
        left.Merge(right).Value.Should().BeEquivalentTo(new[] { "u1", "u2", "u4" });
        right.Merge(left).Value.Should().BeEquivalentTo(new[] { "u1", "u2", "u4" });
    }

    [Fact]
    public void GoJump_GuavaGoldenVectors()
    {
        int[] hundred = [0, 55, 62, 8, 45, 59, 86, 97, 82, 59, 73, 37, 17, 56, 86, 21, 90, 37, 38, 83];
        for (var key = 0; key < hundred.Length; key++)
        {
            JumpConsistentHash.GetBucket((ulong)key, 100).Should().Be(hundred[key]);
        }
        (ulong Key, int Buckets, int Expected)[] cases =
        [
            (10863919174838991, 11, 6), (2016238256797177309, 11, 3), (1673758223894951030, 11, 5),
            (2, 100001, 80343), (2201, 100001, 22152), (2202, 100001, 15018)
        ];
        foreach (var item in cases)
        {
            JumpConsistentHash.GetBucket(item.Key, item.Buckets).Should().Be(item.Expected);
        }
    }

    [Theory]
    [InlineData("", "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d")]
    [InlineData("L123456", "395aa064aa4c29f7010acfe3f25db9485bbd4b91897b6ad7ad547639252b4d56")]
    public void Transparency_Rfc6962_LeafVectors(string data, string expected)
    {
        MerkleTree.HashLeaf(Encoding.UTF8.GetBytes(data)).Should().Equal(Convert.FromHexString(expected));
    }

    [Fact]
    public void Transparency_Rfc6962_NodeAndDomainSeparation()
    {
        // Storage exposes the internal hash through proof verification rather than a raw hasher API.
        MerkleTree.VerifyProof("N123"u8.ToArray(), ["N456"u8.ToArray()],
            Convert.FromHexString("aa217fe888e47007fa15edab33c2b492a722cb106c64667fc2b044444de66bbb"), 0).Should().BeTrue();
        var hello = MerkleTree.HashLeaf("Hello"u8.ToArray());
        var world = MerkleTree.HashLeaf("World"u8.ToArray());
        var tree = new MerkleTree();
        tree.Build(["Hello"u8.ToArray(), "World"u8.ToArray()]);
        tree.RootHash.Should().NotEqual(MerkleTree.HashLeaf([.. hello, .. world]));
        var reversed = new MerkleTree();
        reversed.Build(["World"u8.ToArray(), "Hello"u8.ToArray()]);
        tree.RootHash.Should().NotEqual(reversed.RootHash);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(73)]
    [InlineData(2026)]
    public void OrSet_OperationHistory_MatchesIndependentObservedEventOracle(int seed)
    {
        var random = new Random(seed);
        ORSet<int>[] replicas = [new(), new(), new()];
        HashSet<int>[] known = [[], [], []];
        var adds = new Dictionary<int, int>();
        var removes = new Dictionary<int, int[]>();
        for (var step = 0; step < 192; step++)
        {
            var target = random.Next(3);
            var item = random.Next(7);
            switch (random.Next(3))
            {
                case 0:
                    replicas[target].Add($"n{target}", item);
                    adds.Add(step, item);
                    known[target].Add(step);
                    break;
                case 1:
                    replicas[target].Remove(item);
                    removes.Add(step, known[target].Where(id => adds.GetValueOrDefault(id, -1) == item).ToArray());
                    known[target].Add(step);
                    break;
                default:
                    var other = random.Next(3);
                    replicas[target] = (ORSet<int>)replicas[target].Merge(replicas[other]);
                    known[target].UnionWith(known[other]);
                    break;
            }
            for (var replica = 0; replica < 3; replica++)
            {
                var observedRemovals = known[replica].Where(removes.ContainsKey).SelectMany(id => removes[id]).ToHashSet();
                var expected = known[replica].Where(adds.ContainsKey).Except(observedRemovals).Select(id => adds[id]).ToHashSet();
                replicas[replica].Value.Should().BeEquivalentTo(expected, $"seed {seed}, step {step}, replica {replica}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void Merkle_PaddedTreeAndProofs_MatchRecursiveSha256Oracle(int count)
    {
        var blocks = Enumerable.Range(0, count).Select(i => Encoding.UTF8.GetBytes($"block-{i}")).ToArray();
        var leaves = blocks.Select(block => SHA256.HashData([0, .. block])).ToList();
        var padded = 1;
        while (padded < count) { padded *= 2; }
        while (leaves.Count < padded) { leaves.Add(SHA256.HashData(new byte[] { 2 })); }
        var expectedRoot = Root(leaves);
        var tree = new MerkleTree();
        tree.Build(blocks);
        tree.RootHash.Should().Equal(expectedRoot);
        for (var i = 0; i < count; i++)
        {
            var proof = tree.GetProof(i);
            MerkleTree.VerifyProof(SHA256.HashData([0, .. blocks[i]]), proof, expectedRoot, i).Should().BeTrue();
            MerkleTree.VerifyProof(SHA256.HashData([0, 99, .. blocks[i]]), proof, expectedRoot, i).Should().BeFalse();
        }
    }

    private static byte[] Root(IReadOnlyList<byte[]> leaves)
    {
        if (leaves.Count == 1) { return leaves[0]; }
        var half = leaves.Count / 2;
        return SHA256.HashData([1, .. Root(leaves.Take(half).ToArray()), .. Root(leaves.Skip(half).ToArray())]);
    }
}
