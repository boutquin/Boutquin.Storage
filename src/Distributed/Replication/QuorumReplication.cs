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
namespace Boutquin.Storage.Distributed.Replication;

/// <summary>
/// A Dynamo-style quorum replication system where writes go to W replicas and reads from R replicas,
/// with W + R &gt; N guaranteeing overlap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Leader/follower and quorum replication
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — sync-vs-async replication modes, sloppy-quorum with hinted handoff, snapshot-stream catchup APIs, and version-vector / Lamport conflict resolution — are not included in this baseline type. Use <see cref="LeaderlessReplicaSet{TKey, TValue}"/> for the richer in-process leaderless model. For production use, prefer <a href="https://getakka.net">Akka.NET cluster</a>, <a href="https://orleans.dev">Orleans</a> silos, or a dedicated replicated-log service.
/// </para>
///
/// <para>
/// <b>How it works:</b> Each replica maintains its own key-value store with versioned values.
/// Writes increment a global version counter and send the new version to the first W available replicas
/// (in deterministic ordinal order), succeeding if at least W respond. Reads contact exactly R replicas
/// in the same ordinal order, stopping as soon as R responses are collected, and return the value with
/// the highest version seen. The W + R &gt; N invariant guarantees at least one of the R read replicas
/// received the latest write. Read repair propagates the best version to all available replicas.
/// </para>
///
/// <para>
/// <b>The W + R &gt; N overlap guarantee holds only at full availability.</b> The overlap argument
/// assumes a write reached W replicas AND a subsequent read contacts R replicas drawn from the same
/// N-replica set. When availability degrades, the guarantee is preserved by REFUSING the operation,
/// not by silently weakening it: a write with fewer than W reachable replicas throws
/// <see cref="QuorumNotMetException"/> (reject-before-mutate), and a read with fewer than R reachable
/// replicas throws the same (reject-before-return). This is the deliberate CP trade-off (DDIA 2e
/// Ch. 6 — &quot;Limitations of quorum consistency&quot;): under a partition that drops the available
/// count below W or R the system sacrifices availability for that operation rather than returning a
/// value the overlap argument no longer covers. The guarantee therefore reads &quot;read-after-write
/// holds whenever the operation is PERMITTED&quot; — at full availability, or under degraded-but-
/// quorum-meeting availability (≥ W writers and ≥ R readers still drawn from the same set). It does
/// NOT extend to a sloppy quorum writing to substitute nodes outside the home set; sloppy-quorum
/// with hinted handoff is provided separately by <see cref="LeaderlessReplicaSet{TKey, TValue}"/>.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> All public methods are synchronized via <see cref="SemaphoreSlim"/>.
/// </para>
///
/// <para>
/// <b>Replica selection order:</b> available replicas are iterated in deterministic ordinal order
/// by replica id. This gives writes and reads a stable, reproducible "preference list" rather
/// than the non-deterministic enumeration order of a <see cref="HashSet{T}"/>.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class QuorumReplication<TKey, TValue> : IQuorumReplication<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private readonly Dictionary<string, Dictionary<TKey, (TValue Value, long Version)>> _replicas = [];
    private readonly HashSet<string> _availableReplicas = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _globalVersion;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuorumReplication{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="n">Total number of replicas.</param>
    /// <param name="w">Write quorum size.</param>
    /// <param name="r">Read quorum size.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if W or R exceeds N, or if W + R does not exceed N.
    /// </exception>
    public QuorumReplication(int n, int w, int r)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(w, n);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(r, n);

        if (w + r <= n)
        {
            throw new ArgumentOutOfRangeException(
                nameof(w),
                $"W ({w}) + R ({r}) must be greater than N ({n}) to guarantee quorum overlap.");
        }

        N = n;
        W = w;
        R = r;

        // Create N replicas with generated IDs
        for (var i = 0; i < n; i++)
        {
            var replicaId = $"replica-{i}";
            _replicas[replicaId] = [];
            _availableReplicas.Add(replicaId);
        }
    }

    /// <inheritdoc />
    public int N { get; }

    /// <inheritdoc />
    public int W { get; }

    /// <inheritdoc />
    public int R { get; }

    /// <inheritdoc />
    public async Task WriteAsync(TKey key, TValue value, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Strict-quorum reject-before-mutate (DDIA p. 230). The pre-check and the fan-out
            // write must execute under the same lock that guards replica-set membership; the
            // gate already does that — `SetReplicaAvailability` takes the same gate, so a
            // concurrent drop cannot squeeze between the count check and the loop below.
            // This strict baseline never substitutes spares. LeaderlessReplicaSet models
            // explicit sloppy quorum and retained hints separately.
            if (_availableReplicas.Count < W)
            {
                throw new QuorumNotMetException("Write", _availableReplicas.Count, W);
            }

            var version = ++_globalVersion;
            var writtenCount = 0;

            // Why write to only the first W replicas? In the book's model the coordinator
            // sends every write to all n replicas in parallel and w determines how many
            // acknowledgements it WAITS for (DDIA 2e Ch. 6, "Using quorums for reading and
            // writing") — the n-w slowest/unreachable replicas simply may not have applied the
            // write yet. This synchronous in-process simulation models that asynchrony by
            // applying the write to only the ordinal-first W available replicas: the remaining
            // replicas stand in for the nodes whose acks never arrived, which is what makes
            // staleness — and read-repair — observable. The pre-check above guarantees we will
            // reach W; remaining replicas are caught up by read-repair on the next read.
            //
            // Ordinal sort gives us a deterministic preference list — HashSet iteration order
            // is not stable, so two calls with the same available-set could pick different Ws.
            foreach (var replicaId in _availableReplicas.OrderBy(id => id, StringComparer.Ordinal))
            {
                _replicas[replicaId][key] = (value, version);
                writtenCount++;

                if (writtenCount >= W)
                {
                    break;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Read-repair off the critical path.</b> The first half of this method runs
    /// under <c>_gate</c> long enough to take a consistent snapshot of the available replicas
    /// and select the freshest visible value. The repair fan-out — pushing the freshest value
    /// to every replica that does not already carry it — is collected as a list inside the
    /// gate and dispatched via <see cref="Task.Run(System.Action)"/> *after* the gate is released. The reader
    /// returns to the caller as soon as the freshest value is selected; the repair completes
    /// in the background. DDIA p. 236 ("Read repair and anti-entropy") explicitly frames
    /// read-repair as a background, asynchronous correctness mechanism — repairs may be lost
    /// on process exit (acceptable per the "anti-entropy will catch it eventually" framing;
    /// anti-entropy is modeled separately by LeaderlessReplicaSet).
    /// </para>
    /// <para>
    /// <b>Read quorum.</b> Throws <see cref="QuorumNotMetException"/> when fewer than
    /// <see cref="R"/> replicas are reachable, mirroring the symmetric reject-before-return
    /// shape that <see cref="WriteAsync"/> uses for the write quorum. Returning a silent
    /// <c>(default, false, 0)</c> would conflate "key not present in this consistent slice"
    /// with "the slice could not be assembled at all," and a caller observing the false would
    /// take it as authoritative absence rather than a transient infrastructure failure.
    /// </para>
    /// </remarks>
    public async Task<(TValue Value, bool Found, long Version)> ReadAsync(TKey key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        // Repair list captured inside the gate; fan-out happens AFTER the gate is released.
        List<string>? repairTargets = null;
        TValue bestValue;
        long bestVersion;
        bool found;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Strict-quorum reject-before-return (mirrors WriteAsync's reject-before-mutate).
            // The pre-check and the read loop execute under the same gate that guards
            // SetReplicaAvailability, so a concurrent drop cannot squeeze between the count
            // check and the read.
            if (_availableReplicas.Count < R)
            {
                throw new QuorumNotMetException("Read", _availableReplicas.Count, R);
            }

            var readCount = 0;
            bestValue = default!;
            bestVersion = 0L;
            found = false;

            // Ordinal sort for deterministic replica traversal (HashSet is unordered).
            // Stop after R responses — W + R > N guarantees the R-window overlaps the W-window,
            // so the latest write is always visible within the first R replicas.
            var orderedReplicas = _availableReplicas.OrderBy(id => id, StringComparer.Ordinal).ToList();
            foreach (var replicaId in orderedReplicas)
            {
                if (readCount >= R)
                {
                    break;
                }

                readCount++;

                if (_replicas[replicaId].TryGetValue(key, out var entry))
                {
                    found = true;
                    if (entry.Version > bestVersion)
                    {
                        bestValue = entry.Value;
                        bestVersion = entry.Version;
                    }
                }
            }

            // Snapshot the repair targets inside the gate so we have a stable replica set
            // even if a SetReplicaAvailability call mutates _availableReplicas after we exit.
            if (found)
            {
                repairTargets = [];
                foreach (var replicaId in orderedReplicas)
                {
                    if (!_replicas[replicaId].TryGetValue(key, out var entry) || entry.Version < bestVersion)
                    {
                        repairTargets.Add(replicaId);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        // Background read-repair fan-out. The reader has already selected the freshest
        // value; repairs propagate it to stale replicas off the critical path so concurrent
        // readers are not serialised behind the repair (DDIA p. 236 — read-repair is a
        // background, asynchronous correctness mechanism).
        //
        // Note on `ct`: the caller's cancellation token already terminated the *read*; the
        // repair fan-out should still complete so eventual consistency converges for other
        // readers. Per DDIA's "anti-entropy will catch it eventually" framing, repairs may be
        // lost on process exit.
        if (repairTargets is not null && repairTargets.Count > 0)
        {
            // Fire-and-forget — but observe failures via a fault-only continuation so a
            // background exception (concurrent dispose, replica-set mutation race) surfaces
            // in diagnostics instead of being silently swallowed by the unobserved-Task
            // finalizer. The reader has already returned to the caller; the continuation
            // logs and discards.
            var repairTask = RunReadRepairAsync(this, repairTargets, key, bestValue, bestVersion);
            LastReadRepairTaskForTesting = repairTask;
            _ = repairTask
                .ContinueWith(
                    static t => System.Diagnostics.Trace.TraceWarning(
                        "Boutquin.Storage: QuorumReplication background read-repair faulted: " +
                        (t.Exception?.GetBaseException()?.ToString() ?? "<unknown>")),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        return (bestValue, found, bestVersion);
    }

    // ── Test-only observability ──────────────────────────────────────────────────────────
    // Read-repair is unobservable through the public read API BY CONSTRUCTION: observing it
    // would need an R-window disjoint from every write set, i.e. N − W ≥ R ⟺ W + R ≤ N,
    // which the constructor forbids. These internal seams (same convention as
    // ConsistentHashRing.ComputeHashForTesting) let tests await the fire-and-forget repair
    // deterministically, probe a single replica's stored version under the gate, and drive
    // the repair path directly to pin its no-downgrade guard.

    /// <summary>The most recently dispatched background read-repair task; null before any read scheduled one.</summary>
    internal Task? LastReadRepairTaskForTesting { get; private set; }

    internal (bool Present, long Version) GetReplicaVersionForTesting(string replicaId, TKey key)
    {
        _gate.Wait();
        try
        {
            return _replicas[replicaId].TryGetValue(key, out var entry)
                ? (true, entry.Version)
                : (false, 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal Task RunReadRepairForTesting(List<string> targets, TKey key, TValue value, long version)
        => RunReadRepairAsync(this, targets, key, value, version);

    private static async Task RunReadRepairAsync(
        QuorumReplication<TKey, TValue> self,
        List<string> targets,
        TKey key,
        TValue value,
        long version)
    {
        // Yield to ensure we never block the caller's continuation chain — the repair runs
        // on a worker thread so the reader returns immediately.
        await Task.Yield();

        // The read returned to the caller before the repair was scheduled; the caller (or
        // another thread) may have disposed the replicator in the interim. Skip silently — the
        // caller's intent (drop the replicator) is unambiguous and the fault continuation would
        // only log noise. The gate is never disposed, so the wait cannot throw; the re-check
        // under the gate covers a Dispose() that ran while this repair was queued.
        if (self._disposed != 0)
        {
            return;
        }

        await self._gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (self._disposed != 0)
            {
                return;
            }

            foreach (var replicaId in targets)
            {
                // Re-check membership: SetReplicaAvailability may have removed the replica
                // after we captured the target list, in which case skip.
                if (!self._availableReplicas.Contains(replicaId))
                {
                    continue;
                }

                // Idempotent: re-check version because a concurrent write may have already
                // overwritten this replica with a newer version.
                if (!self._replicas[replicaId].TryGetValue(key, out var entry) ||
                    entry.Version < version)
                {
                    self._replicas[replicaId][key] = (value, version);
                }
            }
        }
        finally
        {
            self._gate.Release();
        }
    }

    /// <inheritdoc />
    public void SetReplicaAvailability(string replicaId, bool isAvailable)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_replicas.ContainsKey(replicaId))
            {
                throw new ArgumentException($"Unknown replica: {replicaId}", nameof(replicaId));
            }

            if (isAvailable)
            {
                _availableReplicas.Add(replicaId);
            }
            else
            {
                _availableReplicas.Remove(replicaId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the instance disposed. Idempotent — concurrent disposers are gated by
    /// <see cref="Interlocked.Exchange(ref int, int)"/>. After dispose, every public method throws
    /// <see cref="ObjectDisposedException"/>, including a call already queued on the gate: it wakes
    /// when the current holder releases, observes the disposal and throws. An operation that held the
    /// gate when Dispose ran completes normally (a write already applied to W replicas is not reported
    /// as failed), and a pending background read-repair skips silently.
    /// </summary>
    /// <remarks>
    /// The gate <see cref="SemaphoreSlim"/> is deliberately never disposed. It allocates no kernel
    /// handle unless <see cref="SemaphoreSlim.AvailableWaitHandle"/> is read, which this type never
    /// does, and disposing it under a holder would make the holder's release throw after its mutation
    /// ran and strand every queued caller forever.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
