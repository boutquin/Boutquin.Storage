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
/// CDC resume-position coherence: <c>ReadChangesAsync(position)</c> filters by the backing
/// EVENT-LOG position, so the records it yields must carry that same log position in
/// <see cref="ChangeRecord{TKey, TValue}.Position"/> — a consumer checkpoints the position of
/// the last record it processed and passes it back to resume. If the yielded position were
/// whatever the producer happened to stamp at append time, the checkpoint and the resume
/// filter would speak different coordinate systems and the consumer could silently skip or
/// re-process changes.
/// </summary>
public sealed class CdcPositionContractTests
{
    [Fact]
    public async Task EventLogCdcSource_YieldsTheLogPosition_NotTheProducerStampedOne()
    {
        var log = new InMemoryEventLog<ChangeRecord<string, int>>();
        var now = DateTimeOffset.UtcNow;

        // The producer stamps positions from a different (wrong) coordinate system.
        await log.AppendAsync(new ChangeRecord<string, int>(100, ChangeOperation.Insert, "k1", 1, now));
        await log.AppendAsync(new ChangeRecord<string, int>(200, ChangeOperation.Insert, "k2", 2, now));

        var source = new EventLogCdcSource<string, int>(log);
        var positions = new List<long>();
        await foreach (var record in source.ReadChangesAsync(0))
        {
            positions.Add(record.Position);
        }

        // The log position is the resume coordinate ReadChangesAsync filters by, so yielded
        // records must carry it — not the producer-stamped value.
        positions.Should().Equal(0L, 1L);
    }

    [Fact]
    public async Task ObjectStoreCdcSource_YieldsTheLogPosition_NotTheProducerStampedOne()
    {
        var log = new InMemoryEventLog<ChangeRecord<string, byte[]>>();
        var now = DateTimeOffset.UtcNow;

        await log.AppendAsync(new ChangeRecord<string, byte[]>(999, ChangeOperation.Insert, "a.json", [1], now));
        await log.AppendAsync(new ChangeRecord<string, byte[]>(998, ChangeOperation.Delete, "a.json", null, now));

        var source = new ObjectStoreCdcSource<string>(log);
        var positions = new List<long>();
        await foreach (var record in source.ReadChangesAsync(0))
        {
            positions.Add(record.Position);
        }

        positions.Should().Equal(0L, 1L);
    }
}
