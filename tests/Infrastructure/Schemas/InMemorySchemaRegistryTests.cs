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
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

/// <summary>
/// Tests for <see cref="InMemorySchemaRegistry"/>.
/// </summary>
public sealed class InMemorySchemaRegistryTests
{
    private readonly InMemorySchemaRegistry _registry;

    public InMemorySchemaRegistryTests()
    {
        _registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
    }

    [Fact]
    public async Task RegisterFirstSchema_ReturnsVersion1()
    {
        // Arrange
        var schema = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));

        // Act
        var version = await _registry.RegisterAsync("test-subject", schema, CompatibilityMode.Backward).ConfigureAwait(true);

        // Assert
        version.Value.Should().Be(1);
    }

    [Fact]
    public async Task RegisterCompatibleEvolution_ReturnsVersion2()
    {
        // Arrange
        var v1 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));
        var v2 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Age", SchemaFieldType.Int32, IsOptional: true)); // Optional new field = backward compatible

        // Act
        await _registry.RegisterAsync("subject", v1, CompatibilityMode.Backward).ConfigureAwait(true);
        var version = await _registry.RegisterAsync("subject", v2, CompatibilityMode.Backward).ConfigureAwait(true);

        // Assert
        version.Value.Should().Be(2);
    }

    [Fact]
    public async Task RegisterIncompatibleEvolution_Throws()
    {
        // Arrange
        var v1 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));
        // Adding a REQUIRED field is backward-incompatible: a new reader cannot fill it when
        // reading old data (DDIA 2e Ch. 5; finding #4). (Removing a field, by contrast, is always
        // backward-compatible — the new reader ignores it.)
        var v2 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Email", SchemaFieldType.String, IsOptional: false));

        await _registry.RegisterAsync("subject", v1, CompatibilityMode.Backward).ConfigureAwait(true);

        // Act & Assert
        var act = () => _registry.RegisterAsync("subject", v2, CompatibilityMode.Backward).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task GetByVersion_ReturnsCorrectSchema()
    {
        // Arrange
        var v1 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));
        var v2 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Age", SchemaFieldType.Int32, IsOptional: true));

        await _registry.RegisterAsync("subject", v1, CompatibilityMode.None).ConfigureAwait(true);
        await _registry.RegisterAsync("subject", v2, CompatibilityMode.None).ConfigureAwait(true);

        // Act
        var result1 = await _registry.GetAsync("subject", new SchemaVersion(1)).ConfigureAwait(true);
        var result2 = await _registry.GetAsync("subject", new SchemaVersion(2)).ConfigureAwait(true);

        // Assert
        result1.Fields.Should().HaveCount(1);
        result2.Fields.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetMissingVersion_Throws()
    {
        var schema = new SimpleSchema(new SchemaField("X", SchemaFieldType.String, IsOptional: false));
        await _registry.RegisterAsync("subject", schema, CompatibilityMode.None).ConfigureAwait(true);

        var act = () => _registry.GetAsync("subject", new SchemaVersion(99)).AsTask();
        await act.Should().ThrowAsync<KeyNotFoundException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task GetMissingSubject_Throws()
    {
        var act = () => _registry.GetAsync("nonexistent", new SchemaVersion(1)).AsTask();
        await act.Should().ThrowAsync<KeyNotFoundException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task TransitiveCompatibility_V3IncompatibleWithV1_Throws()
    {
        // Arrange — backward compatibility must hold transitively against EVERY prior version,
        // not just the immediate predecessor (DDIA 2e Ch. 5; finding #4 corrected the asymmetry).
        // v1 has only Name.
        var v1 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));

        // v2 adds an OPTIONAL Email — backward-compatible with v1 (a new reader fills the default).
        var v2 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Email", SchemaFieldType.String, IsOptional: true));

        // v3 makes Email REQUIRED. Relative to v1, Email is a NEW required field — backward-
        // incompatible, and the transitive check must catch it even when the immediate
        // predecessor looks closer. (Under the retained-field optionality rule, v3 is ALSO
        // backward-incompatible with v2 directly: optional→required on a retained field is the
        // same hazard as adding a required field. Either path must reject the registration —
        // this test pins the transitive-check requirement with v1.)
        var v3 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Email", SchemaFieldType.String, IsOptional: false));

        await _registry.RegisterAsync("subject", v1, CompatibilityMode.Backward).ConfigureAwait(true);
        await _registry.RegisterAsync("subject", v2, CompatibilityMode.Backward).ConfigureAwait(true);

        // Act & Assert — transitive check catches v3 incompatibility with v1
        var act = () => _registry.RegisterAsync("subject", v3, CompatibilityMode.Backward).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);
    }

    private static SimpleSchema OneField(int i)
        => new(new SchemaField($"f{i}", SchemaFieldType.Int32, IsOptional: true));

    [Fact]
    public async Task GetAsync_ConcurrentWithRegister_NeverObservesTornList()
    {
        // RegisterAsync mutates a List<ISchema> under the registry lock; GetAsync must take the same
        // lock. A lock-free read of the list while a concurrent Add resizes its backing array (or
        // increments Count before the new element is written) can throw or return a torn/null entry.
        // This stress test hammers both paths against the moving tail to surface the race; once
        // GetAsync is correctly locked the access is fully serialized and the test is deterministic.
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        const string subject = "s";
        await registry.RegisterAsync(subject, OneField(0), CompatibilityMode.None).ConfigureAwait(true);

        var inflight = 1; // the version the writer is about to add (the racy tail slot)
        Exception? failure = null;

        var writer = Task.Run(async () =>
        {
            for (var i = 2; i <= 50000; i++)
            {
                Volatile.Write(ref inflight, i); // publish the slot BEFORE it is fully written
                await registry.RegisterAsync(subject, OneField(i), CompatibilityMode.None).ConfigureAwait(false);
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!writer.IsCompleted && failure is null)
            {
                var v = Volatile.Read(ref inflight);
                try
                {
                    var schema = await registry.GetAsync(subject, new SchemaVersion(v)).ConfigureAwait(false);
                    // A correctly-locked read returns a fully-constructed schema; a torn lock-free
                    // read can return a null element (List increments Count before storing the item).
                    if (schema is null)
                    {
                        failure = new InvalidOperationException($"GetAsync returned a torn null for version {v}.");
                    }
                }
                catch (KeyNotFoundException)
                {
                    // Legitimate: the in-flight version isn't registered yet. Not a race signal.
                }
                catch (Exception ex)
                {
                    // ArgumentOutOfRange / IndexOutOfRange from a torn List<> read.
                    failure = ex;
                }
            }
        })).ToArray();

        await writer.ConfigureAwait(true);
        await Task.WhenAll(readers).ConfigureAwait(true);

        failure.Should().BeNull(
            "concurrent GetAsync must take the registry lock; a lock-free read can tear during a List<> resize");
    }

    // AC-8.8 (test gap): cancellation-token coverage for InMemorySchemaRegistry.
    [Fact]
    public async Task RegisterAsync_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var schema = new SimpleSchema(new SchemaField("Name", SchemaFieldType.String, IsOptional: false));
        var act = async () => await _registry.RegisterAsync("s", schema, CompatibilityMode.Backward, cts.Token)
                                             .ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task GetAsync_CancelledToken_ThrowsOperationCanceled()
    {
        // Register first so the registry has a subject.
        var schema = new SimpleSchema(new SchemaField("Name", SchemaFieldType.String, IsOptional: false));
        var version = await _registry.RegisterAsync("s", schema, CompatibilityMode.Backward).ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _registry.GetAsync("s", version, cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    /// <summary>
    /// The registration paths for <see cref="CompatibilityMode.Forward"/> and
    /// <see cref="CompatibilityMode.Full"/> were previously untested — only Backward/None were
    /// exercised. Pins that the mode is actually threaded through to the checker: under
    /// Forward, a narrowing candidate registers (old wider readers keep working) while a
    /// widening candidate is rejected; under Full, any width change is rejected.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_ForwardMode_AllowsNarrowing_RejectsWidening()
    {
        var registry = new InMemorySchemaRegistry(new TaggedWireCompatibilityChecker());
        var longSchema = new TaggedWireSchema([new TaggedWireField(1, "Value", "long", false)], []);
        var intSchema = new TaggedWireSchema([new TaggedWireField(1, "Value", "int", false)], []);

        await registry.RegisterAsync("s", longSchema, CompatibilityMode.Forward).ConfigureAwait(true);

        // Narrowing (long → int) is forward-safe: old long readers decode every new int value.
        var v2 = await registry.RegisterAsync("s", intSchema, CompatibilityMode.Forward).ConfigureAwait(true);
        v2.Value.Should().Be(2);

        // Re-widening is rejected: the transitive check against the int prior fails Forward.
        var act = async () => await registry.RegisterAsync("s", longSchema, CompatibilityMode.Forward).ConfigureAwait(true);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "an old int reader would overflow on new long values").ConfigureAwait(true);
    }

    [Fact]
    public async Task RegisterAsync_FullMode_RejectsAnyWidthChange()
    {
        var registry = new InMemorySchemaRegistry(new TaggedWireCompatibilityChecker());
        var longSchema = new TaggedWireSchema([new TaggedWireField(1, "Value", "long", false)], []);
        var intSchema = new TaggedWireSchema([new TaggedWireField(1, "Value", "int", false)], []);

        await registry.RegisterAsync("s", longSchema, CompatibilityMode.Full).ConfigureAwait(true);

        var act = async () => await registry.RegisterAsync("s", intSchema, CompatibilityMode.Full).ConfigureAwait(true);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "Full requires both directions and narrowing breaks the backward direction").ConfigureAwait(true);
    }
}
