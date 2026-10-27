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
using Boutquin.Storage.Domain.Interfaces.Cdc;

namespace Boutquin.Storage.Infrastructure.Cdc;

/// <summary>Finite CDC catch-up with acknowledged-position resume and replay-safe sink boundaries.</summary>
public static class CdcProjectionPump
{
    /// <summary>Applies available records in source order, returning the first unconsumed position.</summary>
    /// <typeparam name="TKey">Source key type.</typeparam>
    /// <typeparam name="TValue">Source value type.</typeparam>
    /// <param name="source">Ordered source; compacted sources may have gaps.</param>
    /// <param name="position">Inclusive starting position.</param>
    /// <param name="sink">Replay-safe sink. Failure leaves earlier effects committed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next cursor; persist it only after this call succeeds. On failure replay from the previous cursor.</returns>
    public static async ValueTask<long> CatchUpAsync<TKey, TValue>(IChangeDataCaptureSource<TKey, TValue> source,
        long position, IProjectionSink<ChangeRecord<TKey, TValue>> sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        await foreach (var change in source.ReadChangesAsync(position, ct).ConfigureAwait(false))
        {
            if (change.Position < position)
            {
                throw new InvalidDataException("The source returned a regressing position.");
            }

            await sink.ApplyAsync(change, ct).ConfigureAwait(false);
            position = checked(change.Position + 1);
        }
        return position;
    }

    /// <summary>Bootstraps a fresh target from one snapshot and then catches up from its paired cursor.</summary>
    /// <typeparam name="TKey">Source key type.</typeparam>
    /// <typeparam name="TValue">Source value type.</typeparam>
    /// <param name="source">Source with atomic state/cursor capture.</param>
    /// <param name="sink">Fresh or reset replay-safe sink; this method does not remove unrelated pre-existing state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The next cursor after catch-up. Repeat the entire bootstrap after an interrupted first attempt.</returns>
    public static async ValueTask<long> BootstrapAsync<TKey, TValue>(ISnapshotCdcSource<TKey, TValue> source,
        IProjectionSink<ChangeRecord<TKey, TValue>> sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        var snapshot = await source.CaptureSnapshotAsync(ct).ConfigureAwait(false);
        foreach (var change in snapshot.Records)
        {
            await sink.ApplyAsync(change, ct).ConfigureAwait(false);
        }

        return await CatchUpAsync(source, snapshot.NextPosition, sink, ct).ConfigureAwait(false);
    }
}
