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
using Boutquin.Storage.Distributed.Consensus;
using Boutquin.Storage.Distributed.Interfaces.Coordination;
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Distributed.Coordination;

/// <summary>Applies quorum-committed Raft commands to registers, leases, fencing sequences and watches.</summary>
/// <remarks><para><b>Scope:</b> Non-packable, in-process reference. The caller drives one shared virtual runtime;
/// no wall-clock tasks, automatic elections, production persistence or real-time lease guarantees are supplied.
/// Successful reads and watch registrations commit a current-term barrier, so an isolated former leader cannot
/// serve stale successful reads. Rejected/timed-out admission is not a guarantee that a command cannot commit later.</para>
/// <para><b>Replay:</b> Each node applies its committed prefix once by index and deduplicates mutations by request
/// ID and arguments. All logs, dedup outcomes and watch events are retained in memory; there is no compaction.
/// Work per poll includes copying the committed prefix through the existing Raft API.</para>
/// <para><b>Clock:</b> All cluster members must borrow the same runtime and virtual clock. Command timestamps
/// advance replicated logical time; only committed expiration removes ephemeral values. This assumption cannot
/// be replaced by unsynchronized machine clocks. Lease deadlines use the greater of request time and previously applied logical time; delays can consume lifetime.</para>
/// <para><b>Reference:</b> Kleppmann, DDIA, consistency and consensus: linearizable registers, fencing and leases.</para></remarks>
public sealed class RaftBackedCoordinationService : ICoordinationService
{
    private sealed record Pending(CoordinationCommand Command, Func<bool> Completed,
        Action<CoordinationOutcome> Complete, Action<Exception> Fail, CancellationTokenRegistration Cancellation);

    private readonly RaftNode<CoordinationCommand> _node;
    private readonly SimulatedNodeRuntime _runtime;
    private readonly Action _pump;
    private readonly long _timeout;
    private readonly long _heartbeat;
    private readonly CoordinationStateMachine _state = new();
    private readonly Dictionary<CoordinationCommand, CoordinationOutcome> _outcomes = [];
    private readonly List<Pending> _pending = [];
    private readonly List<CoordinationEvent> _events = [];
    private readonly List<CoordinationWatch> _watches = [];
    private int _applied;
    private bool _disposed;
    private CoordinationCommand? _expiration;
    private long _expirationTerm;

    /// <summary>Attaches a state machine and virtual poller to an already registered Raft node.</summary>
    /// <param name="node">Borrowed Raft node carrying coordination commands.</param>
    /// <param name="runtime">Borrowed shared cluster runtime; the node ID must already be registered.</param>
    /// <param name="pump">Borrowed synchronous transport flush, normally RaftSimulation.Pump.</param>
    /// <param name="operationTimeoutTicks">Positive client wait bound; timeout does not undo admission. A bound past the
    /// virtual clock's horizon (for example <see cref="long.MaxValue"/>) waits until that horizon.</param>
    /// <param name="heartbeatTicks">Positive retransmission/commit-propagation interval.</param>
    /// <exception cref="ArgumentNullException">A collaborator is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A time bound is nonpositive.</exception>
    /// <exception cref="KeyNotFoundException">The node is not registered with the runtime.</exception>
    /// <remarks>Replays the existing committed prefix and schedules a one-tick poll. Disposal stops this poller,
    /// pending requests and watches; it never disposes borrowed nodes, persistence, or the runtime.</remarks>
    public RaftBackedCoordinationService(RaftNode<CoordinationCommand> node, SimulatedNodeRuntime runtime,
        Action pump, long operationTimeoutTicks = 100, long heartbeatTicks = 5)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationTimeoutTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heartbeatTicks);
        _node = node;
        _runtime = runtime;
        _pump = pump;
        _timeout = operationTimeoutTicks;
        _heartbeat = heartbeatTicks;
        ApplyCommitted();
        _runtime.Schedule(node.NodeId, 1, Poll);
    }

    /// <inheritdoc />
    public Task<CoordinationRead> ReadAsync(string path, CancellationToken ct = default)
    {
        ValidatePath(path);
        return Submit<CoordinationRead>(new CoordinationCommand(CoordinationOperation.Read, Now, path), ct);
    }

    /// <inheritdoc />
    public Task<CoordinationValue> SetAsync(string path, string value, string requestId, long? leaseId = null, CancellationToken ct = default)
    {
        ValidateMutation(path, requestId, leaseId);
        ArgumentNullException.ThrowIfNull(value);
        return Submit<CoordinationValue>(new CoordinationCommand(CoordinationOperation.Set, Now, path, value, requestId, leaseId: leaseId), ct);
    }

    /// <inheritdoc />
    public Task<CompareExchangeResult> CompareExchangeAsync(string path, long expectedRevision, string? value,
        string requestId, long? leaseId = null, CancellationToken ct = default)
    {
        ValidateMutation(path, requestId, leaseId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return Submit<CompareExchangeResult>(new CoordinationCommand(CoordinationOperation.CompareExchange, Now,
            path, value, requestId, expectedRevision, leaseId), ct);
    }

    /// <inheritdoc />
    public Task<CoordinationWatch> WatchAsync(string path, long afterRevision = 0, CancellationToken ct = default)
    {
        ValidatePath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        return Submit(new CoordinationCommand(CoordinationOperation.Read, Now, path), ct, _ => CreateWatch(path, afterRevision));
    }

    /// <inheritdoc />
    public Task<CoordinationLease?> AcquireLeaseAsync(string resource, string owner, long durationTicks, string requestId, CancellationToken ct = default)
    {
        ValidateMutation(resource, requestId, null);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateDuration(durationTicks);
        return Submit<CoordinationLease?>(new CoordinationCommand(CoordinationOperation.AcquireLease, Now,
            resource, requestId: requestId, owner: owner, duration: durationTicks), ct);
    }

    /// <inheritdoc />
    public Task<bool> RenewLeaseAsync(long leaseId, long durationTicks, string requestId, CancellationToken ct = default)
    {
        ValidateLease(leaseId, requestId);
        ValidateDuration(durationTicks);
        return Submit<bool>(new CoordinationCommand(CoordinationOperation.RenewLease, Now,
            requestId: requestId, leaseId: leaseId, duration: durationTicks), ct);
    }

    /// <inheritdoc />
    public Task<bool> ReleaseLeaseAsync(long leaseId, string requestId, CancellationToken ct = default)
    {
        ValidateLease(leaseId, requestId);
        return Submit<bool>(new CoordinationCommand(CoordinationOperation.ReleaseLease, Now, requestId: requestId, leaseId: leaseId), ct);
    }

    /// <inheritdoc />
    public Task<CoordinationLease?> ReadLeaseAsync(string resource, CancellationToken ct = default)
    {
        ValidatePath(resource);
        return Submit<CoordinationLease?>(new CoordinationCommand(CoordinationOperation.ReadLease, Now, resource), ct);
    }

    /// <inheritdoc />
    public Task<long> AllocateSequenceAsync(string scope, string requestId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return Submit<long>(new CoordinationCommand(CoordinationOperation.Allocate, Now, scope, requestId: requestId), ct);
    }

    private long Now => _runtime.Clock.Now;

    private Task<T> Submit<T>(CoordinationCommand command, CancellationToken ct)
        => Submit(command, ct, outcome => (T)outcome.Value!);

    private Task<T> Submit<T>(CoordinationCommand command, CancellationToken ct, Func<CoordinationOutcome, T> convert)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Schedule the deadline before anything is registered or retained, so no admission failure can leave an
        // orphaned pending entry. A wait bound past the clock's horizon (long.MaxValue means "no client deadline")
        // saturates at that horizon instead of overflowing the deadline arithmetic.
        _runtime.Clock.Schedule(Math.Min(_timeout, long.MaxValue - Now), () =>
            completion.TrySetException(new TimeoutException("No committed coordination response before the virtual deadline; the outcome may be uncertain.")));
        var cancellation = ct.Register(() => completion.TrySetCanceled(ct));
        var pending = new Pending(command, () => completion.Task.IsCompleted,
            outcome =>
            {
                if (outcome.Error is { } error)
                {
                    completion.TrySetException(new InvalidOperationException(error));
                }
                else
                {
                    var result = convert(outcome);
                    if (!completion.TrySetResult(result) && result is CoordinationWatch watch)
                    {
                        // Concurrent cancellation can win between the readiness check and conversion.
                        // Do not retain a subscription that no caller can receive or dispose.
                        watch.Dispose();
                    }
                }
            },
            error => completion.TrySetException(error), cancellation);
        _pending.Add(pending);
        _runtime.Schedule(_node.NodeId, 0, () =>
        {
            if (completion.Task.IsCompleted || _disposed)
            {
                return;
            }
            if (_node.State != RaftNodeState.Leader)
            {
                completion.TrySetException(new InvalidOperationException("This coordination node is not the Raft leader."));
                return;
            }
            Propose(command);
        });
        return completion.Task;
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }
        ApplyCommitted();
        foreach (var request in _pending.ToArray())
        {
            if (!request.Completed() && _outcomes.TryGetValue(request.Command, out var outcome))
            {
                request.Complete(outcome);
            }
            if (request.Completed())
            {
                request.Cancellation.Dispose();
                _pending.Remove(request);
            }
        }
        if (_node.State == RaftNodeState.Leader)
        {
            if (_expiration is not null &&
                (_expirationTerm != _node.CurrentTerm || _outcomes.ContainsKey(_expiration)))
            {
                _expiration = null;
            }
            if (_expiration is null && _state.NextExpiry is { } expiry && expiry <= Now)
            {
                _expiration = new CoordinationCommand(CoordinationOperation.Expire, Now);
                _expirationTerm = _node.CurrentTerm;
                Propose(_expiration);
            }
            if (Now % _heartbeat == 0)
            {
                _node.ReplicatePendingEntries();
                _pump();
            }
        }
        _runtime.Schedule(_node.NodeId, 1, Poll);
    }

    private void Propose(CoordinationCommand command)
    {
        // Raft's in-process admission currently returns an already-completed task. Fail loudly if that
        // contract changes: blocking an asynchronous transport here would deadlock the virtual driver.
        var admission = _node.ProposeAsync(command);
        if (!admission.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("Raft admission must complete synchronously on the simulation driver.");
        }
        _pump();
    }

    private void ApplyCommitted()
    {
        var log = _node.GetCommittedLog();
        while (_applied < log.Count)
        {
            var command = log[_applied].Command;
            var outcome = _state.Apply(command, Publish);
            _outcomes[command] = outcome;
            _applied++;
        }
    }

    private void Publish(CoordinationEvent change)
    {
        _events.Add(change);
        foreach (var watch in _watches)
        {
            if (watch.Path == change.Path)
            {
                watch.Publish(change);
            }
        }
    }

    private CoordinationWatch CreateWatch(string path, long afterRevision)
    {
        var watch = new CoordinationWatch(path, afterRevision, RemoveWatch);
        foreach (var change in _events.Where(change => change.Path == path && change.Revision > afterRevision))
        {
            watch.Publish(change);
        }
        _watches.Add(watch);
        return watch;
    }

    private void RemoveWatch(CoordinationWatch watch) => _watches.Remove(watch);

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Coordination paths must be absolute logical paths.", nameof(path));
        }
    }

    private static void ValidateMutation(string path, string requestId, long? leaseId)
    {
        ValidatePath(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (leaseId.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseId.Value);
        }
    }

    private static void ValidateLease(long leaseId, string requestId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
    }

    private void ValidateDuration(long durationTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationTicks);
        // A deadline past the virtual clock's horizon is an out-of-bounds argument, not an arithmetic fault:
        // report it through the documented ArgumentOutOfRangeException rather than an OverflowException.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(durationTicks, long.MaxValue - Now);
    }

    /// <summary>Stops polling, fails pending requests and completes watches without disposing borrowed collaborators.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var request in _pending)
        {
            request.Fail(new ObjectDisposedException(nameof(RaftBackedCoordinationService)));
            request.Cancellation.Dispose();
        }
        _pending.Clear();
        foreach (var watch in _watches.ToArray())
        {
            watch.Dispose();
        }
    }
}
