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
namespace Boutquin.Storage.Domain.Interfaces.Schemas;

/// <summary>A registry-versioned frame containing a standard Protocol Buffers message.</summary>
/// <typeparam name="T">A schema-generated message type.</typeparam>
/// <remarks>Protobuf evolves by stable field numbers, not registry-directed name resolution.
/// The registry validates a writer version while the message parser resolves fields and retains
/// unknown fields. The frame is local to this library; its payload uses the external Protobuf wire format.</remarks>
public interface IProtobufSerializer<T>
{
    /// <summary>Writes one versioned frame, preserving unknown fields carried by the message.</summary>
    /// <param name="stream">Borrowed writable output stream.</param>
    /// <param name="value">A message that is not concurrently mutated during serialization.</param>
    /// <param name="schemaVersion">The registered positive writer-schema version.</param>
    /// <param name="ct">Cancellation may leave a partial frame; callers must discard that output.</param>
    /// <returns>Completion when bytes reach the stream; flushing and durability belong to its owner.</returns>
    ValueTask SerializeAsync(Stream stream, T value, SchemaVersion schemaVersion, CancellationToken ct = default);

    /// <summary>Reads exactly one frame and resolves its writer version against the registry.</summary>
    /// <param name="stream">Borrowed readable stream, left at the next frame after success.</param>
    /// <param name="registry">Registry whose subject defines this log's compatible message family.</param>
    /// <param name="subject">Nonblank registry subject; callers own the correct subject/message pairing.</param>
    /// <param name="ct">Cancellation can consume a partial frame; restart from a known frame boundary.</param>
    /// <returns>The registered writer version and decoded message, retaining unknown fields.</returns>
    ValueTask<(SchemaVersion Version, T Value)> DeserializeAsync(Stream stream, ISchemaRegistry registry,
        string subject, CancellationToken ct = default);
}
