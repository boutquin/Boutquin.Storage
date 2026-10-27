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

using System.Text.Json;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins that a payload declared as <see cref="object"/> is acknowledged only when it reads back as the
/// type that was appended: an object that reloads as a bare JSON document has lost its type or its state.
/// </summary>
public sealed class SyncJournalObjectPayloadRoundTripTests
{
    [Fact]
    public async Task GCounterDeclaredAsObject_IsRefusedAndLeavesTheJournalUnchanged()
    {
        var directory = Directory.CreateTempSubdirectory("sync-object-gcounter-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<object>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            await replica.AppendLocalAsync(JsonSerializer.SerializeToElement("kept")).ConfigureAwait(true);
            var before = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            var counter = new GCounter();
            counter.Increment("a");

            var append = () => replica.AppendLocalAsync(counter);

            await append.Should().ThrowAsync<NotSupportedException>(
                "an object member holding a GCounter reloads as a JSON document and loses its merge semantics").ConfigureAwait(true);
            (await File.ReadAllBytesAsync(path).ConfigureAwait(true)).Should().Equal(before);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PrivateStateTypeDeclaredAsObject_IsRefusedInsteadOfLosingItsCounter()
    {
        var directory = Directory.CreateTempSubdirectory("sync-object-lamport-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<object>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var timestamp = new LamportTimestamp("node-a", 42);

            var append = () => replica.AppendLocalAsync(timestamp);

            await append.Should().ThrowAsync<NotSupportedException>(
                "the persisted payload would carry the node identifier but not the counter").ConfigureAwait(true);
            File.Exists(path).Should().BeFalse("a refused first append must not create the journal");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("boolean")]
    public async Task ScalarPayloadsDeclaredAsObject_AreRefused(string kind)
    {
        var directory = Directory.CreateTempSubdirectory("sync-object-scalar-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<object>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);

            object scalar = kind switch { "string" => "text", "number" => 7L, _ => true };
            var append = () => replica.AppendLocalAsync(scalar);

            await append.Should().ThrowAsync<NotSupportedException>(
                "a scalar declared as object reloads as JsonElement, changing its runtime type").ConfigureAwait(true);
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JsonElementDeclaredAsObject_RetainsItsTypeAndValueOnReopen()
    {
        var directory = Directory.CreateTempSubdirectory("sync-object-json-element-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<object>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            await replica.AppendLocalAsync(JsonSerializer.SerializeToElement(7)).ConfigureAwait(true);

            var reopened = await JsonFileSyncReplica<object>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var restored = (await reopened.ReadAllAsync().ConfigureAwait(true)).Should().ContainSingle().Subject.Value;

            restored.Should().BeOfType<JsonElement>().Subject.GetInt32().Should().Be(7);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ObjectMemberNestedInAChangeRecord_IsRefusedWhenItsStateWouldBeLost()
    {
        var directory = Directory.CreateTempSubdirectory("sync-object-nested-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<ObjectEnvelope>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            await replica.AppendLocalAsync(new ObjectEnvelope("scalar", JsonSerializer.SerializeToElement(5L))).ConfigureAwait(true);

            var append = () => replica.AppendLocalAsync(new ObjectEnvelope("clock", new LamportTimestamp("node-a", 42)));

            await append.Should().ThrowAsync<NotSupportedException>().ConfigureAwait(true);
            (await replica.ReadAllAsync().ConfigureAwait(true)).Should().ContainSingle("the refused append must not reach the journal");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    public sealed record ObjectEnvelope(string Label, object Payload);
}
