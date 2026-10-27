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
using Boutquin.Storage.ArchitectureTests.Helpers;
using FluentAssertions;
using NetArchTest.Rules;

namespace Boutquin.Storage.ArchitectureTests;

/// <summary>
/// Architecture guardrails for the assembly carve that created
/// <c>Boutquin.Storage.Distributed</c> — the in-process, educational reference home for
/// Raft, replication, MVCC / SSI, CRDTs, gossip, Merkle, and partitioning primitives.
/// Enforces that the assembly is (a) one-way-dependent (Domain and Infrastructure must
/// not reference Distributed), and (b) the sole home of its namespace roots (no orphan
/// types left in
/// <c>Infrastructure.{Consensus,Replication,Transactions,Partitioning,DistributedSystems}</c>
/// or <c>Infrastructure.DataStructures.MerkleTree</c>). The carve rationale — educational
/// reference vs production-strength — is stated in
/// <see href="../../src/Distributed/README.md"><c>src/Distributed/README.md</c></see>.
/// </summary>
public sealed class DistributedAssemblyTests : BaseArchitectureTest
{
    private const string CoverletInstrumentationNamespace = "Coverlet.Core.Instrumentation";

    [Fact]
    public void Domain_ShouldNotDependOnDistributed()
    {
        // The assembly carve placed the distributed-primitive contracts in Distributed
        // alongside their implementations rather than in Domain. The reverse dependency —
        // Domain referencing Distributed — would reintroduce the "stable contract,
        // unstable implementation" lock-in that the carve exists to avoid.
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .DoNotResideInNamespace(CoverletInstrumentationNamespace)
            .Should()
            .NotHaveDependencyOnAny("Boutquin.Storage.Distributed")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"Domain must not depend on Distributed — the assembly carve is one-way: [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void Infrastructure_ShouldNotDependOnDistributed()
    {
        // Distributed → Infrastructure is allowed (the partitioning implementations
        // default to Murmur3 from Infrastructure.Algorithms when no IHashAlgorithm is
        // injected). Infrastructure → Distributed is not — it would make the
        // production-strength storage layer depend on in-process reference code and
        // pull carved symbols back into Boutquin.Storage.Infrastructure's public API.
        var result = Types.InAssembly(InfrastructureAssembly)
            .That()
            .DoNotResideInNamespace(CoverletInstrumentationNamespace)
            .Should()
            .NotHaveDependencyOnAny("Boutquin.Storage.Distributed")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"Infrastructure must not depend on Distributed — the assembly carve is one-way: [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void CarvedNamespaces_ShouldNotAppearInInfrastructureAssembly()
    {
        // The six namespace roots the assembly carve moved must not have any live type
        // left inside Infrastructure.dll. If the move is partial or a later PR resurrects
        // a class under the old namespace, this test catches it.
        var orphans = InfrastructureAssembly.GetTypes()
            .Where(t => t.Namespace is { } ns &&
                (ns == "Boutquin.Storage.Infrastructure.Consensus"
                    || ns.StartsWith("Boutquin.Storage.Infrastructure.Consensus.", StringComparison.Ordinal)
                    || ns == "Boutquin.Storage.Infrastructure.Replication"
                    || ns.StartsWith("Boutquin.Storage.Infrastructure.Replication.", StringComparison.Ordinal)
                    || ns == "Boutquin.Storage.Infrastructure.Transactions"
                    || ns.StartsWith("Boutquin.Storage.Infrastructure.Transactions.", StringComparison.Ordinal)
                    || ns == "Boutquin.Storage.Infrastructure.Partitioning"
                    || ns.StartsWith("Boutquin.Storage.Infrastructure.Partitioning.", StringComparison.Ordinal)
                    || ns == "Boutquin.Storage.Infrastructure.DistributedSystems"
                    || ns.StartsWith("Boutquin.Storage.Infrastructure.DistributedSystems.", StringComparison.Ordinal)))
            .Where(t => !t.Namespace!.StartsWith(CoverletInstrumentationNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        orphans.Should().BeEmpty(
            "no type should remain under Infrastructure.{Consensus,Replication,Transactions,Partitioning,DistributedSystems} — the assembly carve is one-way");
    }

    [Fact]
    public void InfrastructureMerkleTree_ShouldNotExistOnDataStructures()
    {
        // The assembly carve moved MerkleTree out of Infrastructure.DataStructures into
        // Distributed.Merkle — the other DataStructures members (BTree, BPlusTree,
        // BloomFilter, CountingBloomFilter, RedBlackTree, SkipListMemTable) stay.
        var merkle = InfrastructureAssembly.GetType("Boutquin.Storage.Infrastructure.DataStructures.MerkleTree");
        merkle.Should().BeNull(
            "MerkleTree lives in Boutquin.Storage.Distributed.Merkle, not Infrastructure.DataStructures");
    }

    [Fact]
    public void CarvedInterfaces_ShouldNotAppearOnDomainInterfacesRoot()
    {
        // The carved contracts moved out of Boutquin.Storage.Domain.Interfaces (flat).
        // None of these names should resolve on the Domain assembly anymore.
        string[] carvedInterfaces =
        [
            "IRaftNode`1", "IRaftCluster`1",
            "IReplicationLog`2", "ISingleLeaderReplication`2", "IQuorumReplication`2",
            "IMvccStore`2", "ISnapshotIsolationStore`2",
            "IConsistentHashRing`1", "IPartitioner`1", "IRangePartitioner`1", "IRendezvousHash`1",
            "ICrdt`1", "IGCounter", "IGSet`1", "IORSet`1", "IPNCounter",
            "IVectorClock", "ILamportTimestamp", "IGossipProtocol`2",
            "IMerkleTree",
        ];

        var stragglers = carvedInterfaces
            .Select(n => DomainAssembly.GetType($"Boutquin.Storage.Domain.Interfaces.{n}"))
            .Where(t => t is not null)
            .Select(t => t!.FullName)
            .ToList();

        stragglers.Should().BeEmpty(
            "carved Distributed contracts must not resolve under Boutquin.Storage.Domain.Interfaces");
    }

    [Fact]
    public void DistributedAssembly_ShouldContainCarvedContracts()
    {
        // Positive check — belt-and-braces: assert the carved surface actually lives in
        // the new assembly. A misnamed namespace or missed move would leave the
        // interface resolvable from neither Domain nor Distributed.
        var raft = DistributedAssembly.GetType(
            "Boutquin.Storage.Distributed.Interfaces.Consensus.IRaftNode`1");
        raft.Should().NotBeNull(
            "IRaftNode<TCommand> must live in Distributed.Interfaces.Consensus after the carve");

        var merkleImpl = DistributedAssembly.GetType(
            "Boutquin.Storage.Distributed.Merkle.MerkleTree");
        merkleImpl.Should().NotBeNull(
            "MerkleTree must live in Distributed.Merkle after the carve");

        var shardMap = DistributedAssembly.GetType(
            "Boutquin.Storage.Distributed.Interfaces.Sharding.IShardMap");
        shardMap.Should().NotBeNull(
            "the versioned shard-map contract must live in Distributed.Interfaces.Sharding");

        var shardRouter = DistributedAssembly.GetType(
            "Boutquin.Storage.Distributed.Sharding.ShardRouter`1");
        shardRouter.Should().NotBeNull(
            "the shard router must live in Distributed.Sharding");

        DistributedAssembly.GetType("Boutquin.Storage.Distributed.Interfaces.Sync.ISyncEngine`2")
            .Should().NotBeNull("sync contracts belong to the Distributed assembly");
        DistributedAssembly.GetType("Boutquin.Storage.Distributed.Sync.SyncEngine`2")
            .Should().NotBeNull("the reference sync engine belongs to the Distributed assembly");
    }
}
