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
namespace Boutquin.Storage.Infrastructure.Replication;

/// <summary>
/// A Dynamo-style quorum replication system where writes go to W replicas and reads from R replicas,
/// with W + R &gt; N guaranteeing overlap.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it works:</b> Each replica maintains its own key-value store with versioned values.
/// Writes increment a global version counter and send the new version to all available replicas,
/// succeeding if at least W respond. Reads query all available replicas, succeeding if at least R respond,
/// and return the value with the highest version. Read repair updates stale replicas with the latest value.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> All public methods are synchronized via <see cref="SemaphoreSlim"/>.
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
    public async Task<bool> WriteAsync(TKey key, TValue value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var version = Interlocked.Increment(ref _globalVersion);
            var writtenCount = 0;

            // Why write to only W replicas instead of all? In Dynamo-style systems, the
            // coordinator selects W replicas from the preference list. Writing to all would
            // make every write equivalent to W=N, defeating the purpose of tunable consistency.
            // Partial writes allow read repair to propagate values to stale replicas on read.
            foreach (var replicaId in _availableReplicas)
            {
                _replicas[replicaId][key] = (value, version);
                writtenCount++;

                if (writtenCount >= W)
                {
                    break;
                }
            }

            return writtenCount >= W;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found, long Version)> ReadAsync(TKey key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var readCount = 0;
            var bestValue = default(TValue)!;
            var bestVersion = 0L;
            var found = false;

            foreach (var replicaId in _availableReplicas)
            {
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

            if (readCount < R)
            {
                return (default!, false, 0);
            }

            // Read repair: update stale replicas with the latest version
            if (found)
            {
                foreach (var replicaId in _availableReplicas)
                {
                    if (!_replicas[replicaId].TryGetValue(key, out var entry) || entry.Version < bestVersion)
                    {
                        _replicas[replicaId][key] = (bestValue, bestVersion);
                    }
                }
            }

            return (bestValue, found, bestVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void SetReplicaAvailability(string replicaId, bool isAvailable)
    {
        _gate.Wait();
        try
        {
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
}
