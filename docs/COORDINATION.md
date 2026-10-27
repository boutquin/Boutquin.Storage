# Quorum-backed coordination and fencing

`RaftBackedCoordinationService` applies actual committed `RaftNode<CoordinationCommand>`
log entries. Registers, CAS, watches, IDs, leases and fencing tokens share that state
machine. Contracts live under `Distributed.Interfaces.Coordination`; implementations
remain in the non-packable `Boutquin.Storage.Distributed` assembly. They are
in-process reference components, consumed by project reference.

## Execute the examples

```sh
dotnet run --project src/CoordinationSample/Boutquin.Storage.CoordinationSample.csproj -- 20260917
dotnet test tests/Infrastructure/Boutquin.Storage.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~CoordinationServiceTests'
dotnet run -c Release --project benchmarks/Coordination/Boutquin.Storage.Coordination.Benchmarks.csproj -- --filter '*' --job Dry
```

The sample assigns a shard to an ephemeral lease owner, partitions the old Raft
leader, elects a replacement, checks increasing IDs, waits for lease expiry,
replays the ownership watch, and assigns the shard to a new owner. Its external
resource accepts the new fencing token and rejects a delayed write from the old
owner. It then verifies that the isolated former coordinator cannot return a
successful read. An optional unsigned seed changes the delayed/lost/duplicated
message schedule; assertions cause a failing process exit if a property breaks.

The four benchmark cases compare 100 committed read barriers and 100 register
writes with one versus three members. Each iteration creates a fresh cluster,
so initialization and allocation are included. Dry is an execution smoke check,
not a statistically meaningful performance result.

## Construct a driven cluster

```csharp
var runtime = new SimulatedNodeRuntime(42);
var transport = new RaftSimulation<CoordinationCommand>(runtime);
var nodes = Enumerable.Range(0, 3)
    .Select(i => new RaftNode<CoordinationCommand>($"n{i}")).ToArray();
foreach (var node in nodes)
    transport.AddNode(node);

var services = nodes.Select(node =>
    new RaftBackedCoordinationService(node, runtime, transport.Pump)).ToArray();
transport.Elect("n0");
runtime.RunUntil(10);

Task<CoordinationValue> pending =
    services[0].SetAsync("/config", "enabled", requestId: "client-1:operation-1");
runtime.RunUntil(30);
CoordinationValue committed = await pending;
```

Import the `Distributed.Consensus`, `Distributed.Coordination`, and
`Distributed.Simulation` namespaces. Dispose every service when done. It owns
its pending requests and watch channels, but borrows the runtime, node and transport.

Do not await an unfinished operation before driving virtual time: the driver is
what delivers protocol messages and resolves commit waits. Public calls, disposal,
and runtime advancement must be serialized by the host. A one-tick poll applies
committed entries and resolves callers. The heartbeat interval defaults to five
ticks and retransmits through the existing Raft AppendEntries path, including
commit-index propagation. Elections are explicit; membership is fixed. A stale candidate
cannot win a vote from a more up-to-date survivor; the sample then campaigns that
survivor instead.

## Linearizability and failure semantics

Every read and watch registration appends a current-term barrier. The caller
receives the result only when its own command appears in that replica's committed
prefix and has been applied. A local leader flag or an old cached quorum is never
enough. This also commits inherited prior-term entries before a new leader serves
a successful read.

Every mutation takes a globally unique `requestId`. Retrying identical arguments
returns the original committed outcome, including a failed CAS or busy lease
acquisition. Reusing the ID with different arguments fails. Results are remembered
through log replay and leader changes. A retry response is the original outcome,
not a fresh read of current state; an originally successful lease may since have expired.

The default client deadline is 100 virtual ticks. A minority leader cannot obtain
fresh commits and times out. Followers reject new admission. A timeout or
cancellation **does not undo an already admitted command**: it may commit after
healing or a leader change. Retain that operation as pending in a linearizability
history, and retry its original ID before allocating a new mutation identity.

Committed mutations emit unique increasing change revisions. CAS compares the
value's revision; zero means the path must be absent. A same-value rewrite changes
the revision, so a stale CAS cannot succeed after an ABA cycle. Null replacement
text deletes the matching value. A read also returns the global change cursor,
which lets a client watch an absent path without losing intervening changes.

## Typed registers, IDs and tokens

`LinearizableRegister<TKey,TValue>` hex-encodes default System.Text.Json key bytes
under a dedicated absolute prefix and serializes values before admission. Use
immutable keys with stable, distinct JSON encoding and keep each prefix exclusive
to one key/value contract. Keys and values must round-trip exactly, including their
runtime types. The facade refuses lossy encodings before admission, including text
with an unpaired UTF-16 surrogate and object-declared scalars that decode as
`JsonElement`. Its CAS accepts the snapshot revision and a replacement value.

`LinearizableIdGenerator` and `FencingTokenService` allocate from the same committed
global sequence used by lease acquisitions. Values can have gaps, but cannot be
reused by a distinct successful allocation. Identical retries return the original
value. Non-overlapping successful calls observe increasing values; overlapping
responses may arrive out of order.

A fencing token is not a credential. The protected resource must remember its
highest accepted token and reject lower ones. Equal-token writes can belong to
the same current owner. Fencing protects against a stale owner's write **after the
resource has observed a newer token**; lease expiry alone cannot force an external
resource to reject a token it has never learned is obsolete.

## Leases and ephemeral paths

Acquire an exclusive resource with `AcquireLeaseAsync(resource, owner, duration,
requestId)`. A busy resource returns null. Renewal retains the identity and token
and never revives an expired lease. Release ends ownership immediately at commit.
Lease IDs act as bearer handles in this reference model; no authentication or ACL
layer is supplied.

Pass an active lease ID to Set or CAS to create an ephemeral path. The state
machine validates the lease at apply time. On release or expiration it removes
all attached paths and publishes a `LeaseEnded` change for each. Persistent paths
remain independent. The leader's poller proposes expiration commands even without
new client traffic; expiration cannot become observable without a quorum commit.

All nodes share **one monotonic virtual clock**. Commands carry the request's
timestamp, and applying them advances replicated logical time to the maximum of
that timestamp and prior committed time. Acquisitions and renewals add their
duration to this replicated time, so a reordered request can start later than its
original timestamp. A deadline beyond the largest representable tick saturates at
`long.MaxValue`, so every committed acquisition or renewal applies on every replica.
Expiry is inclusive:
a deadline at tick T is inactive when a committed command observes T or later.
Paths removed by a lease are sorted ordinally, making change revisions identical
on every replica. Poll pauses and transport delays consume lease lifetime;
a response does not promise a minimum remaining lifetime. Check the absolute
deadline before relying on a lease, and enforce tokens at the external resource.

These assumptions do not transfer to independent machine clocks. This service
does not supply bounded clock drift, process-pause bounds, durable timers, or
production lease safety across OS restarts.

## Watches and resumption

Watch registration commits a barrier, then atomically registers and replays all
retained path events strictly after the supplied cursor. The filter applies to
both replay and future events. Reapplying a committed prefix or retrying a mutation
does not publish another event. Consume via TryRead or ReadAllAsync; dispose the
subscription when finished.

A watch belongs to its local replica. It can stall during a partition or process
pause; it does not move automatically to a new leader. Reconnect to the reachable
leader using the last consumed revision. The new replica reconstructs watch
history from its committed log. A slow consumer uses an unbounded in-memory
channel rather than silently dropping events.

## Trade-offs and evidence boundary

The simple committed-barrier approach trades read throughput for an explicit,
testable quorum proof. It grows the log on every read. Logs, deduplication outcomes,
applied-result maps and watch history are retained without compaction, and the
existing Raft committed-log API copies its prefix on each poll. This favors clear
reference behavior over production space or throughput bounds.

The simulation tests exercise actual handlers under seeded delay, drop, duplicate,
partition and pause faults; sequential histories are checked against a register
model. Dedicated cases cover competing CAS, ABA, read rejection without quorum,
uncertain commit after timeout, idempotent replay, failover, lease renewal/expiry,
ephemeral cleanup, watch cursors and typed serialization.

The model survives **Raft leader changes and reattaching a service to retained
in-process node/log state**. It does not claim survival of machine/process loss:
the opaque coordination command has no persisted wire format, no snapshots or
disk-backed replay layer. There are no sockets, automatic membership changes,
authentication, production watch retention limits, or real-clock lease claims.
