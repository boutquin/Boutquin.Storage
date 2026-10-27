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
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class DeterministicSimulationTests
{
    [Fact]
    public void Clock_OrdersTiesAndRejectsBackwardTime()
    {
        var clock = new DeterministicSimulationClock();
        var seen = new List<int>();
        clock.Schedule(2, () => seen.Add(1));
        clock.Schedule(1, () => seen.Add(2));
        clock.Schedule(2, () => seen.Add(3));
        clock.AdvanceTo(2);
        seen.Should().Equal(2, 1, 3);
        clock.Invoking(c => c.AdvanceTo(1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Network_PauseDuplicateAndPartitionHaveObservableEffects()
    {
        var runtime = new SimulatedNodeRuntime(7, new FaultInjectionPolicy(0, 1, 0));
        var seen = new List<int>();
        runtime.AddNode("a", _ => { });
        runtime.AddNode("b", message => seen.Add((int)message));
        runtime.Pause("b");
        runtime.Network.Send("a", "b", 42);
        runtime.RunUntil(10);
        seen.Should().BeEmpty();
        runtime.Resume("b");
        runtime.RunUntil(10);
        seen.Should().Equal(42, 42);
        runtime.Network.SetPartition("a", "b", true);
        runtime.Network.Send("a", "b", 99);
        runtime.RunUntil(20);
        seen.Should().Equal(42, 42);
    }

    [Fact]
    public void Checker_RejectsStaleReadAndAcceptsOverlappingRead()
    {
        var history = new HistoryRecorder<int, int>();
        var write = history.Invoke(5);
        history.Complete(write, 5);
        var read = history.Invoke(-1);
        history.Complete(read, 0);
        LinearizabilityChecker.Check(history.Snapshot(), 0, Register).Should().Be(LinearizabilityResult.Invalid);
        var concurrent = new HistoryRecorder<int, int>();
        write = concurrent.Invoke(5);
        read = concurrent.Invoke(-1);
        concurrent.Complete(write, 5);
        concurrent.Complete(read, 0);
        LinearizabilityChecker.Check(concurrent.Snapshot(), 0, Register).Should().Be(LinearizabilityResult.Valid);
    }

    private static (bool Allowed, int State) Register(int state, int input, int output)
        => input < 0 ? (output == state, state) : (output == input, input);
}
