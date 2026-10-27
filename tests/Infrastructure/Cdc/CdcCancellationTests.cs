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
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;

namespace Boutquin.Storage.Infrastructure.Tests.Cdc;

/// <summary>
/// AC-8.8 — Cancellation-token test gaps: CDC sources must propagate pre-cancelled tokens to callers.
/// </summary>
public sealed class CdcCancellationTests
{
    [Fact]
    public async Task EventLogCdcSource_ReadChangesAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var log = new InMemoryEventLog<ChangeRecord<string, string>>();
        var now = DateTimeOffset.UtcNow;
        await log.AppendAsync(new ChangeRecord<string, string>(0, ChangeOperation.Insert, "k", "v", now))
                 .ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel

        var source = new EventLogCdcSource<string, string>(log);

        var act = async () =>
        {
            await foreach (var _ in source.ReadChangesAsync(0, cts.Token).ConfigureAwait(true))
            {
                // Should not yield; token is cancelled before enumeration begins.
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task ObjectStoreCdcSource_ReadChangesAsync_CancelledToken_ThrowsOperationCanceled()
    {
        // ObjectStoreCdcSource<TKey> is typed IChangeDataCaptureSource<TKey, byte[]> — the value
        // type is fixed to byte[] (object content). We test with string keys.
        var log = new InMemoryEventLog<ChangeRecord<string, byte[]>>();
        var now = DateTimeOffset.UtcNow;
        await log.AppendAsync(new ChangeRecord<string, byte[]>(0, ChangeOperation.Insert, "k", [0x01], now))
                 .ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel

        var source = new ObjectStoreCdcSource<string>(log);

        var act = async () =>
        {
            await foreach (var _ in source.ReadChangesAsync(0, cts.Token).ConfigureAwait(true))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }
}
