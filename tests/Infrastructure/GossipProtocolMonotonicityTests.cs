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

/// <summary>
/// Phase C: per-key version monotonicity for <see cref="GossipProtocol{TNodeId, TState}"/>
/// under concurrent <c>ProcessSyncMessage</c>. With the pre-fix <c>AddOrUpdate</c> factory,
/// a racing retry could install an older version over a newer one; the CAS-loop replacement
/// prevents this by binding the version comparison and the CAS to the same snapshot.
/// </summary>
public sealed class GossipProtocolMonotonicityTests
{
    [Fact]
    public void ProcessSyncMessage_UnderContention_FinalVersionEqualsHighestIncoming()
    {
        var gossip = new GossipProtocol<string, string>();
        const int threads = 16;
        const int messagesPerThread = 2_000;

        Parallel.For(0, threads, tid =>
        {
            for (var i = 0; i < messagesPerThread; i++)
            {
                var version = (long)tid * messagesPerThread + i + 1;
                var msg = new Dictionary<string, (string State, long Version)>
                {
                    ["nodeX"] = ($"state-{version}", version),
                };
                gossip.ProcessSyncMessage(msg);
            }
        });

        var finalVersion = gossip.GetClusterState()["nodeX"].Version;
        var highestPossible = (long)threads * messagesPerThread;
        finalVersion.Should().Be(highestPossible,
            "after all threads finish, the stored version must be the maximum incoming version; " +
            "a retrograde transition during the race would leave a lower value");
    }

    [Fact]
    public void ProcessSyncMessage_LowerVersionNeverOverwritesHigher_Sequential()
    {
        var gossip = new GossipProtocol<string, string>();

        var high = new Dictionary<string, (string State, long Version)>
        {
            ["k"] = ("high", 100),
        };
        gossip.ProcessSyncMessage(high);

        var low = new Dictionary<string, (string State, long Version)>
        {
            ["k"] = ("low", 50),
        };
        gossip.ProcessSyncMessage(low);

        var stored = gossip.GetClusterState()["k"];
        stored.State.Should().Be("high");
        stored.Version.Should().Be(100);
    }
}
