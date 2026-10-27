# Testing distributed systems with deterministic faults

The reusable simulation framework lives in `Boutquin.Storage.Distributed.Simulation`.
Its contract, `ISimulationClock`, lives in `Distributed.Interfaces.Simulation`.
Both remain in the non-packable Distributed assembly alongside the reference Raft,
replication, gossip, and CRDT components. The production storage assemblies do not
depend on this framework.

## Run the examples

```sh
dotnet run --project src/SimulationSample/Boutquin.Storage.SimulationSample.csproj
dotnet test tests/Infrastructure/Boutquin.Storage.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Simulation'
dotnet run -c Release --project benchmarks/Simulation/Boutquin.Storage.Simulation.Benchmarks.csproj -- --filter '*' --job Dry
```

The demo elects an actual Raft leader, submits a command, and checks its committed
log after delayed and duplicated messages. It also demonstrates that an unsynced
single-leader follower can produce a non-linearizable read, then verifies catchup.

The benchmark contrasts immediate reliable delivery with delayed, lost, duplicated
traffic at 1,000 and 10,000 sends. It measures queue/allocation trade-offs, not
production throughput. A Dry job is only an executable smoke check; use ordinary
BenchmarkDotNet jobs and disclose the generated environment header for performance claims.

## A reusable node adapter

```csharp
var simulation = new SimulatedNodeRuntime(
    seed: 42,
    policy: new FaultInjectionPolicy(
        dropProbability: 0.1, duplicateProbability: 0.25, maximumDelay: 20));

simulation.AddNode("source", _ => { });
simulation.AddNode("replica", message => ApplyImmutableMessage(message));
simulation.Invariants.Add("nonnegative balance", () => balance >= 0);
simulation.Network.Send("source", "replica", immutableCommand);
simulation.Schedule("replica", 5, OnVirtualTimeout);
simulation.RunUntil(100);
```

Replace the three application-specific names with your component's synchronous
entry points. Components may return already-completed tasks; asynchronous external
I/O, task scheduling, and wall-clock sleeps are outside the deterministic model.
Do not pass async lambdas to Action handlers: async-void continuations escape the
event queue.

Each timer or message handler executes atomically on one driver. The clock uses a
priority queue ordered by (deadline, insertion sequence), so equal deadlines are
stable and callbacks may schedule more callbacks for the same tick. An event budget
detects zero-delay livelock. Recursive clock advancement is rejected. Callback
exceptions propagate with the clock left at that event; unexecuted events remain queued.

A seed controls the explicitly implemented xorshift32 stream, independently of
System.Random version changes. Reproduction requires the same seed, policy, ordered
inputs, and application callbacks. Seeds 1, 8,675,309, and uint.MaxValue are fixed
transport regressions; Raft runs also use 42, 19,088,743, and 3,735,928,559.
PropertyTestGenerators can generate workload choices independently of network faults.

## Fault semantics and the trade-off

| Mechanism | Behavior | Boundary |
|---|---|---|
| Delay | Each surviving copy draws 0 through maximumDelay ticks | Virtual units, no real-time latency claim |
| Loss | Drops the entire send before duplication | No automatic retry |
| Duplication | Creates one extra independently delayed copy | Handlers must implement their own idempotency |
| Reordering | Independent copy deadlines can invert send order | Equal deadlines retain queue insertion order |
| Partition | Symmetric endpoint pair drops sends and in-flight delivery | Healing does not resurrect lost packets |
| Pause | Delivered messages and expired timers wait in node FIFO | Models a stall, not loss of process memory |

Use `Network.SetPartition(a, b, true)` to partition and false to heal.
Use `Pause(id)` and `Resume(id)` to stall a process. Resume requeues pending work
at the current tick; it does not invoke the handler inline. New network policies
affect future sends only. Payload references are borrowed: take an immutable
snapshot before sending and do not mutate it later.

This trades realism for reproducibility and inexpensive exploration. It does not
model threads racing inside handlers, OS process crashes, disk faults, sockets,
serialization, Byzantine corruption, or recovery persistence. Existing contention,
crash-injection and persistence tests remain necessary and are retained.

## Actual component integration

`RaftSimulation<T>` uses the existing `RaftNode<T>` queues and handlers, including
term/vote handling and majority commit logic. Register fresh follower nodes, then
call `Elect(id, delay)`. After a direct `ProposeAsync`, call `Pump()`; arrivals
automatically pump response queues. Election timeouts and retry proposals are explicit.
The adapter adds no consensus algorithm and promises no progress during arbitrary loss.

The return from a proposal is not a substitute for inspecting its committed log.
A history must acknowledge a write only once the node's committed log contains it.
Leader-local reads in a stable seeded demonstration do not establish a general Raft
linearizable-read protocol under leadership changes.

The component invariant tests express four families using the same runtime:

- Raft: one leader per term, log matching, committed-prefix agreement across
  2,000 workload steps per seed with delayed/lost/duplicated messages and process pauses.
- Observed-remove set: replaying an old snapshot cannot resurrect removed tags;
  an unseen concurrent add still survives.
- Consistent hash ring: forced hash collisions and repeated remove/add actions
  preserve unique live owners.
- Gossip: delayed duplicate snapshots never decrease the accepted owner version;
  a final reliable update reaches the latest version.

The original algorithm-specific suites remain valuable for exact adversarial
protocol states and true multithreaded contention. Simulation extends their coverage;
it does not turn a finite seed set into a proof over every execution.

## Histories and bounded linearizability

`JepsenLiteHarness<TInput,TOutput>` schedules invocations on registered clients.
The operation-start delegate receives a one-shot response callback; invoke it only
when the real response is known. Delayed callbacks model overlapping operations,
and missing callbacks represent uncertain timeouts.

`HistoryRecorder` assigns strictly increasing event ordinals, including responses
and invocations sharing a virtual tick. A definitive rejection must have no effects;
use `Reject` only for that case. Leave an operation pending if an effect may have
occurred. Snapshots preserve pending operations.

`LinearizabilityChecker.Check` performs bounded exhaustive backtracking. It tries
only operations whose real-time predecessors have already been placed and calls
a pure sequential model with (state, input, output). The model returns legality and
an immutable successor state. Mutating shared model state invalidates the search.

- Valid: at least one legal sequential explanation respects real-time ordering.
- Invalid: every permitted ordering was explored and rejected.
- Inconclusive: an operation is pending, more than 256 completed operations exist,
  or the transition budget was exhausted.

The search is factorial in the worst case, with linear auxiliary space. Partition
large histories by an independently justified key boundary, not arbitrary windows
that discard cross-window effects. This Jepsen-inspired in-process harness is not
the Jepsen tool and does not certify production availability or linearizability.

