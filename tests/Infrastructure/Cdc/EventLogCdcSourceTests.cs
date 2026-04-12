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
/// Tests for <see cref="EventLogCdcSource{TKey, TValue}"/>.
/// </summary>
public sealed class EventLogCdcSourceTests
{
    [Fact]
    public async Task AppendChanges_ReadChanges_CorrectOperationsAndOrdering()
    {
        // Arrange
        var eventLog = new InMemoryEventLog<ChangeRecord<string, string>>();
        var cdcSource = new EventLogCdcSource<string, string>(eventLog);

        var now = DateTimeOffset.UtcNow;
        await eventLog.AppendAsync(new ChangeRecord<string, string>(0, ChangeOperation.Insert, "key-1", "val-1", now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, string>(1, ChangeOperation.Update, "key-1", "val-2", now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, string>(2, ChangeOperation.Delete, "key-1", null, now)).ConfigureAwait(true);

        // Act
        var changes = new List<ChangeRecord<string, string>>();
        await foreach (var record in cdcSource.ReadChangesAsync(0).ConfigureAwait(true))
        {
            changes.Add(record);
        }

        // Assert
        changes.Should().HaveCount(3);
        changes[0].Operation.Should().Be(ChangeOperation.Insert);
        changes[1].Operation.Should().Be(ChangeOperation.Update);
        changes[2].Operation.Should().Be(ChangeOperation.Delete);
        changes[2].Value.Should().BeNull();
    }

    [Fact]
    public async Task ReadChangesFromPositionN_SkipsEarlierChanges()
    {
        // Arrange
        var eventLog = new InMemoryEventLog<ChangeRecord<string, int>>();
        var cdcSource = new EventLogCdcSource<string, int>(eventLog);

        var now = DateTimeOffset.UtcNow;
        await eventLog.AppendAsync(new ChangeRecord<string, int>(0, ChangeOperation.Insert, "a", 1, now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, int>(1, ChangeOperation.Insert, "b", 2, now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, int>(2, ChangeOperation.Insert, "c", 3, now)).ConfigureAwait(true);

        // Act
        var changes = new List<ChangeRecord<string, int>>();
        await foreach (var record in cdcSource.ReadChangesAsync(2).ConfigureAwait(true))
        {
            changes.Add(record);
        }

        // Assert
        changes.Should().ContainSingle();
        changes[0].Key.Should().Be("c");
    }

    [Fact]
    public async Task MultipleKeys_InterleavedChanges_MaintainGlobalOrdering()
    {
        // Arrange
        var eventLog = new InMemoryEventLog<ChangeRecord<string, string>>();
        var cdcSource = new EventLogCdcSource<string, string>(eventLog);

        var now = DateTimeOffset.UtcNow;
        await eventLog.AppendAsync(new ChangeRecord<string, string>(0, ChangeOperation.Insert, "AAPL", "150", now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, string>(1, ChangeOperation.Insert, "GOOG", "2800", now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, string>(2, ChangeOperation.Update, "AAPL", "155", now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, string>(3, ChangeOperation.Delete, "GOOG", null, now)).ConfigureAwait(true);

        // Act
        var changes = new List<ChangeRecord<string, string>>();
        await foreach (var record in cdcSource.ReadChangesAsync(0).ConfigureAwait(true))
        {
            changes.Add(record);
        }

        // Assert — global ordering maintained
        changes.Should().HaveCount(4);
        changes[0].Key.Should().Be("AAPL");
        changes[1].Key.Should().Be("GOOG");
        changes[2].Key.Should().Be("AAPL");
        changes[3].Key.Should().Be("GOOG");
        changes[3].Operation.Should().Be(ChangeOperation.Delete);
    }
}
