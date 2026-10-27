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
using Boutquin.Storage.Distributed.Replication;
using Boutquin.Storage.Domain.Interfaces.Cdc;

namespace Boutquin.Storage.Distributed.Cdc;

/// <summary>Reads the leader's retained CDC history independently of follower log truncation.</summary>
/// <typeparam name="TKey">Comparable immutable keys.</typeparam>
/// <typeparam name="TValue">Immutable values.</typeparam>
/// <remarks>All direct leader writes are captured. The reference leader has no delete API and is volatile;
/// CDC is likewise volatile and retains history for its lifetime. Finish operations before disposing the leader.</remarks>
public sealed class SingleLeaderCdcSource<TKey, TValue> : ISnapshotCdcSource<TKey, TValue> where TKey : IComparable<TKey>
{
    private readonly SingleLeaderReplication<TKey, TValue> _leader;
    /// <summary>Attaches to an existing leader, including its retained prior writes.</summary>
    /// <param name="leader">The source leader.</param>
    public SingleLeaderCdcSource(SingleLeaderReplication<TKey, TValue> leader)
    {
        ArgumentNullException.ThrowIfNull(leader); _leader = leader;
    }
    /// <inheritdoc />
    public ValueTask<CdcSnapshot<TKey, TValue>> CaptureSnapshotAsync(CancellationToken ct = default) => _leader.CaptureCdcSnapshotAsync(ct);
    /// <inheritdoc />
    public async IAsyncEnumerable<ChangeRecord<TKey, TValue>> ReadChangesAsync(long position,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var records = await _leader.CaptureCdcChangesAsync(position, ct).ConfigureAwait(false);
        foreach (var record in records) { ct.ThrowIfCancellationRequested(); yield return record; }
    }
}
