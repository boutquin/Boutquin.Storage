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
using Boutquin.Storage.Domain.Enums;
using Boutquin.Storage.Domain.ValueObjects;
using Boutquin.Storage.Infrastructure.Schemas;

using Google.Protobuf.Reflection;

using V1 = Boutquin.Storage.Samples.Proto.V1.Person;
using V2 = Boutquin.Storage.Samples.Proto.V2.Person;
using V3 = Boutquin.Storage.Samples.Proto.V3.Person;

namespace Boutquin.Storage.Samples;

/// <summary>Replays separately generated message versions through the registry and Protobuf wire.</summary>
public static class ProtoEvolutionDemo
{
    /// <summary>Checks additions, a rename, deprecation, reservation, and old-reader preservation.</summary>
    public static async Task RunAsync()
    {
        var schemas = new[]
        {
            new TaggedWireSchema([new(1, "name", "string", true), new(2, "score", "int", true)]),
            new TaggedWireSchema([new(1, "display_name", "string", true), new(2, "score", "int", true), new(3, "email", "string", true)]),
            new TaggedWireSchema([new(1, "display_name", "string", true), new(3, "email", "string", true)], [2]),
        };
        var descriptors = new[] { V1.Descriptor, V2.Descriptor, V3.Descriptor };
        var registry = new InMemorySchemaRegistry(new TaggedWireCompatibilityChecker());
        for (var i = 0; i < schemas.Length; i++)
        {
            var external = new FileDescriptorSet();
            external.File.Add(descriptors[i].File.ToProto());
            if (!ProtobufTagCompatibilityChecker.Check(schemas[i], external, "Person").IsCompatible)
            {
                throw new InvalidOperationException("Sample tag schema disagrees with the generated Protobuf descriptor.");
            }
            await registry.RegisterAsync("person", schemas[i], CompatibilityMode.Full).ConfigureAwait(false);
        }

        var oldWriter = new ProtoSerializer<V1>(V1.Parser);
        var middleWriter = new ProtoSerializer<V2>(V2.Parser);
        var currentReader = new ProtoSerializer<V3>(V3.Parser);
        using var log = new MemoryStream();
        await oldWriter.SerializeAsync(log, new V1 { Name = "Ada", Score = 7 }, new SchemaVersion(1)).ConfigureAwait(false);
        await middleWriter.SerializeAsync(log, new V2 { DisplayName = "Ada Lovelace", Email = "ada@example.test" }, new SchemaVersion(2)).ConfigureAwait(false);
        await currentReader.SerializeAsync(log, new V3 { DisplayName = "Ada", Email = "ada@example.test" }, new SchemaVersion(3)).ConfigureAwait(false);
        log.Position = 0;
        for (var expected = 1; expected <= 3; expected++)
        {
            var record = await currentReader.DeserializeAsync(log, registry, "person").ConfigureAwait(false);
            if (record.Version.Value != expected || record.Value.DisplayName.Length == 0
                || record.Value.Email != (expected == 1 ? "" : "ada@example.test"))
            {
                throw new InvalidOperationException("Versioned replay changed a stable tag.");
            }
            Console.WriteLine($"writer v{record.Version.Value}: {record.Value.DisplayName}, email='{record.Value.Email}'");
        }

        // An old binary reader edits its known field while retaining the new writer's email.
        using var incoming = new MemoryStream();
        await middleWriter.SerializeAsync(incoming, new V2 { DisplayName = "Before", Email = "kept@example.test" }, new SchemaVersion(2)).ConfigureAwait(false);
        incoming.Position = 0;
        var old = await oldWriter.DeserializeAsync(incoming, registry, "person").ConfigureAwait(false);
        old.Value.Name = "After";
        using var rewritten = new MemoryStream();
        await oldWriter.SerializeAsync(rewritten, old.Value, old.Version).ConfigureAwait(false);
        rewritten.Position = 0;
        var restored = (await currentReader.DeserializeAsync(rewritten, registry, "person").ConfigureAwait(false)).Value;
        if (restored.DisplayName != "After" || restored.Email != "kept@example.test")
        {
            throw new InvalidOperationException("Unknown fields were lost through the old reader.");
        }
        Console.WriteLine("Protobuf evolution passed: addition, rename, deprecated/reserved tag, and unknown-field replay.");
    }
}
