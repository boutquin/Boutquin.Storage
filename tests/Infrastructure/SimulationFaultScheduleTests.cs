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

public sealed class SimulationFaultScheduleTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(8675309u)]
    [InlineData(4294967295u)]
    public void FaultSchedule_ReplaysExactly_WithLossDuplicationDelayAndReordering(uint seed)
    {
        var first = Trace(seed);
        first.Should().Equal(Trace(seed));
        first.Count.Should().BeGreaterThan(0).And.BeLessThan(2000);
        first.Select(item => item.Id).Should().NotBeInAscendingOrder();
        first.GroupBy(item => item.Id).Should().Contain(group => group.Count() == 2);
        first.Select(item => item.Id).Distinct().Count().Should().BeLessThan(1000);
        first.Should().Contain(item => item.Tick > 0);
    }

    [Fact]
    public void Clock_BudgetAndCallbackFailurePreserveRemainingEvents()
    {
        var clock = new DeterministicSimulationClock();
        var count = 0;
        clock.Schedule(1, () => count++);
        clock.Schedule(1, () => count++);
        clock.Invoking(c => c.AdvanceTo(10, 1)).Should().Throw<InvalidOperationException>();
        clock.Now.Should().Be(1);
        clock.AdvanceTo(10);
        count.Should().Be(2);
        clock.Schedule(1, () => throw new InvalidOperationException("injected"));
        clock.Schedule(2, () => count++);
        clock.Invoking(c => c.AdvanceTo(20)).Should().Throw<InvalidOperationException>();
        clock.Now.Should().Be(11);
        clock.AdvanceTo(20);
        count.Should().Be(3);
    }

    [Fact]
    public void Clock_RejectsReentrantAdvanceAndOverflow()
    {
        var clock = new DeterministicSimulationClock();
        clock.Schedule(1, () => clock.AdvanceTo(2));
        clock.Invoking(c => c.AdvanceTo(3)).Should().Throw<InvalidOperationException>();
        clock.AdvanceTo(long.MaxValue);
        clock.Invoking(c => c.Schedule(1, () => { })).Should().Throw<OverflowException>();
        clock.Invoking(c => c.Schedule(-1, () => { })).Should().Throw<ArgumentOutOfRangeException>();
        clock.Invoking(c => c.AdvanceTo(long.MaxValue, 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void FaultPolicy_RejectsInvalidProbabilities(double probability)
    {
        var drop = () => new FaultInjectionPolicy(probability);
        var duplicate = () => new FaultInjectionPolicy(0, probability);
        drop.Should().Throw<ArgumentOutOfRangeException>();
        duplicate.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Pause_RetainsTimersAndResumeDoesNotRunThemInline()
    {
        var runtime = new SimulatedNodeRuntime(1);
        runtime.AddNode("node", _ => { });
        var value = 0;
        runtime.Pause("node");
        runtime.Schedule("node", 1, () => value++);
        runtime.RunUntil(10);
        runtime.Resume("node");
        value.Should().Be(0);
        runtime.RunUntil(10);
        value.Should().Be(1);
        runtime.Invariants.Add("injected failure", () => value == 0);
        runtime.Invoking(r => r.RunUntil(11)).Should().Throw<InvalidOperationException>()
            .WithMessage("*injected failure*");
    }

    [Fact]
    public void Partition_DropsQueuedPacketsAndHealingRestoresTraffic()
    {
        var runtime = new SimulatedNodeRuntime(19, new FaultInjectionPolicy(0, 0, 20));
        var delivered = 0;
        runtime.AddNode("a", _ => delivered++);
        runtime.AddNode("b", _ => delivered++);
        runtime.Network.Send("a", "b", 1);
        runtime.Network.SetPartition("a", "b", true);
        runtime.Network.Send("b", "a", 1);
        runtime.RunUntil(30);
        delivered.Should().Be(0);
        runtime.Network.SetPartition("a", "b", false);
        runtime.Network.Send("b", "a", 1);
        runtime.RunUntil(60);
        delivered.Should().Be(1);
        runtime.Network.Policy = new FaultInjectionPolicy(1);
        runtime.Network.Send("a", "b", 1);
        runtime.RunUntil(100);
        delivered.Should().Be(1);
    }

    [Fact]
    public void Generator_KnownAnswerPinsRuntimeIndependentRecurrence()
    {
        var generator = new PropertyTestGenerators(1);
        generator.NextUInt32().Should().Be(270369u);
        generator.NextUInt32().Should().Be(67634689u);
        generator.NextUInt32().Should().Be(2647435461u);
        generator.Invoking(g => g.Next(0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void History_PendingAndBudgetExhaustionAreInconclusive_RejectionsHaveNoEffects()
    {
        var recorder = new HistoryRecorder<int, int>();
        var id = recorder.Invoke(9);
        LinearizabilityChecker.Check(recorder.Snapshot(), 0, Register).Should().Be(LinearizabilityResult.Inconclusive);
        recorder.Reject(id);
        id = recorder.Invoke(-1);
        recorder.Complete(id, 0);
        LinearizabilityChecker.Check(recorder.Snapshot(), 0, Register).Should().Be(LinearizabilityResult.Valid);
        recorder.Invoking(r => r.Complete(id, 0)).Should().Throw<InvalidOperationException>();
        id = recorder.Invoke(1);
        recorder.Complete(id, 1);
        LinearizabilityChecker.Check(recorder.Snapshot(), 0, Register, 1).Should().Be(LinearizabilityResult.Inconclusive);
    }

    [Fact]
    public void Checker_BacktracksInsteadOfGreedilyRejectingConcurrentOperations()
    {
        var history = new HistoryRecorder<int, int>();
        var read = history.Invoke(-1);
        var write = history.Invoke(1);
        history.Complete(write, 1);
        history.Complete(read, 1);
        LinearizabilityChecker.Check(history.Snapshot(), 0, Register).Should().Be(LinearizabilityResult.Valid);
        var invalid = new[] { new HistoryOperation<int, int>(1, 1, 1, 5, 4, true) };
        var act = () => LinearizabilityChecker.Check(invalid, 0, Register);
        act.Should().Throw<ArgumentException>();
    }

    private static (bool Allowed, int State) Register(int state, int input, int output)
        => input < 0 ? (output == state, state) : (output == input, input);

    private static List<(long Tick, int Id)> Trace(uint seed)
    {
        var runtime = new SimulatedNodeRuntime(seed, new FaultInjectionPolicy(0.3, 0.5, 100));
        var trace = new List<(long, int)>();
        runtime.AddNode("a", _ => { });
        runtime.AddNode("b", message => trace.Add((runtime.Clock.Now, (int)message)));
        for (var i = 0; i < 1000; i++)
        {
            runtime.Network.Send("a", "b", i);
        }
        runtime.RunUntil(100);
        return trace;
    }
}

