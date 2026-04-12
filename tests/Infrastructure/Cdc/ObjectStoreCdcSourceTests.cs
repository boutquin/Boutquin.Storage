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
/// Tests for <see cref="ObjectStoreCdcSource{TKey}"/>.
/// </summary>
public sealed class ObjectStoreCdcSourceTests
{
    [Fact]
    public async Task WriteObjects_ReadChanges_InsertRecordsEmitted()
    {
        // Arrange — simulate object store writes as change records
        var eventLog = new InMemoryEventLog<ChangeRecord<string, byte[]>>();
        var cdcSource = new ObjectStoreCdcSource<string>(eventLog);

        var now = DateTimeOffset.UtcNow;
        var content = Encoding.UTF8.GetBytes("hello");
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(0, ChangeOperation.Insert, "data/file1.json", content, now)).ConfigureAwait(true);

        // Act
        var changes = new List<ChangeRecord<string, byte[]>>();
        await foreach (var record in cdcSource.ReadChangesAsync(0).ConfigureAwait(true))
        {
            changes.Add(record);
        }

        // Assert
        changes.Should().ContainSingle();
        changes[0].Operation.Should().Be(ChangeOperation.Insert);
        changes[0].Key.Should().Be("data/file1.json");
        changes[0].Value.Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task DeleteObject_DeleteRecordEmitted()
    {
        // Arrange
        var eventLog = new InMemoryEventLog<ChangeRecord<string, byte[]>>();
        var cdcSource = new ObjectStoreCdcSource<string>(eventLog);

        var now = DateTimeOffset.UtcNow;
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(0, ChangeOperation.Insert, "key-1", [1, 2], now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(1, ChangeOperation.Delete, "key-1", null, now)).ConfigureAwait(true);

        // Act
        var changes = new List<ChangeRecord<string, byte[]>>();
        await foreach (var record in cdcSource.ReadChangesAsync(0).ConfigureAwait(true))
        {
            changes.Add(record);
        }

        // Assert
        changes.Should().HaveCount(2);
        changes[1].Operation.Should().Be(ChangeOperation.Delete);
        changes[1].Value.Should().BeNull();
    }

    [Fact]
    public async Task ReadChangesFromPositionN_SkipsEarlierChanges()
    {
        // Arrange
        var eventLog = new InMemoryEventLog<ChangeRecord<string, byte[]>>();
        var cdcSource = new ObjectStoreCdcSource<string>(eventLog);

        var now = DateTimeOffset.UtcNow;
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(0, ChangeOperation.Insert, "a", [1], now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(1, ChangeOperation.Insert, "b", [2], now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(2, ChangeOperation.Insert, "c", [3], now)).ConfigureAwait(true);

        // Act
        var changes = new List<ChangeRecord<string, byte[]>>();
        await foreach (var record in cdcSource.ReadChangesAsync(1).ConfigureAwait(true))
        {
            changes.Add(record);
        }

        // Assert
        changes.Should().HaveCount(2);
        changes[0].Key.Should().Be("b");
        changes[1].Key.Should().Be("c");
    }

    [Fact]
    public async Task MultipleKeys_InterleavedChanges_MaintainGlobalOrdering()
    {
        // Arrange
        var eventLog = new InMemoryEventLog<ChangeRecord<string, byte[]>>();
        var cdcSource = new ObjectStoreCdcSource<string>(eventLog);

        var now = DateTimeOffset.UtcNow;
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(0, ChangeOperation.Insert, "AAPL.json", [1], now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(1, ChangeOperation.Insert, "GOOG.json", [2], now)).ConfigureAwait(true);
        await eventLog.AppendAsync(new ChangeRecord<string, byte[]>(2, ChangeOperation.Update, "AAPL.json", [3], now)).ConfigureAwait(true);

        // Act
        var changes = new List<ChangeRecord<string, byte[]>>();
        await foreach (var record in cdcSource.ReadChangesAsync(0).ConfigureAwait(true))
        {
            changes.Add(record);
        }

        // Assert
        changes.Should().HaveCount(3);
        changes[0].Key.Should().Be("AAPL.json");
        changes[1].Key.Should().Be("GOOG.json");
        changes[2].Key.Should().Be("AAPL.json");
    }
}
