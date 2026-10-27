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
using Boutquin.Storage.Domain.Interfaces.Schemas;

using Google.Protobuf;

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>Encodes generated Protobuf messages with bounded, concatenation-safe registry framing.</summary>
/// <typeparam name="T">A generated Protocol Buffers message implementing its typed message contract.</typeparam>
/// <remarks><para>The frame is [positive schema version: LE int32][payload byte length: LE int32]
/// followed by a standard Protobuf payload. Zero-length messages are valid. The envelope is a local
/// storage convention, not the Protobuf delimited-message or Confluent wire format.</para>
/// <para>Unknown fields are always retained, even if the supplied parser was configured to discard
/// them. Stable tags permit renamed fields and additive evolution. Registry membership validates
/// the version; callers choose a compatible subject/parser and enforce tag compatibility on registration.</para>
/// <para>Messages whose encoded payload reads back differently (including text with an unpaired
/// UTF-16 surrogate) are refused before any frame bytes are written.</para>
/// <para>Time and buffering are O(payload bytes), bounded by the configured limit. Nonseekable input
/// grows a buffer only as bytes arrive, so a forged length cannot request a matching allocation.
/// Instances can be shared; streams and mutable messages need exclusive ownership during a call.</para>
/// <para>Reference: Protocol Buffers C# generated-code guide, binary parsing and unknown fields.</para></remarks>
public sealed class ProtoSerializer<T> : IProtobufSerializer<T> where T : IMessage<T>
{
    private readonly MessageParser<T> _parser;
    private readonly int _maxMessageBytes;

    /// <summary>Creates a serializer with an explicit maximum frame-payload size.</summary>
    /// <param name="parser">Generated message parser; borrowed, with unknown-field preservation enforced.</param>
    /// <param name="maxMessageBytes">Maximum payload bytes, default 16 MiB; must be positive.</param>
    /// <exception cref="ArgumentNullException">The parser is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is not positive.</exception>
    public ProtoSerializer(MessageParser<T> parser, int maxMessageBytes = 16 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        _parser = parser.WithDiscardUnknownFields(false);
        _maxMessageBytes = maxMessageBytes;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The stream or value is null.</exception>
    /// <exception cref="ArgumentException">The message exceeds the configured limit, version is zero, or the payload would read back differently.</exception>
    /// <exception cref="IOException">The stream fails during a write; the frame may be incomplete.</exception>
    public async ValueTask SerializeAsync(Stream stream, T value, SchemaVersion schemaVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();
        if (schemaVersion.Value <= 0)
        {
            throw new ArgumentException("A positive registered schema version is required.", nameof(schemaVersion));
        }
        if (value.CalculateSize() > _maxMessageBytes)
        {
            throw new ArgumentException("The message exceeds the configured Protobuf payload limit.", nameof(value));
        }
        var payload = value.ToByteArray();
        // Protobuf replaces unpaired surrogates with U+FFFD. Compare the exact encoded payload
        // with the original message before acknowledging bytes the parser would change on replay.
        if (!value.Equals(_parser.ParseFrom(payload)))
        {
            throw new ArgumentException("The Protobuf payload does not read back as the message written.", nameof(value));
        }
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, schemaVersion.Value);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The stream or registry is null.</exception>
    /// <exception cref="ArgumentException">The subject is blank.</exception>
    /// <exception cref="UnsupportedFormatVersionException">The frame is truncated, malformed, oversized,
    /// or names an unregistered writer version.</exception>
    /// <exception cref="IOException">The underlying stream fails during a read.</exception>
    public async ValueTask<(SchemaVersion Version, T Value)> DeserializeAsync(Stream stream, ISchemaRegistry registry,
        string subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ct.ThrowIfCancellationRequested();
        var header = new byte[8];
        int version = 0;
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            version = BinaryPrimitives.ReadInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (version <= 0 || length < 0 || length > _maxMessageBytes)
            {
                throw new InvalidDataException("Invalid Protobuf frame version or payload length.");
            }
            var schemaVersion = new SchemaVersion(version);
            _ = await registry.GetAsync(subject, schemaVersion, ct).ConfigureAwait(false);
            if (stream.CanSeek && length > stream.Length - stream.Position)
            {
                throw new InvalidDataException("The Protobuf payload length exceeds the remaining stream.");
            }
            // Fixed scratch capacity bounds the allocation even when the stream cannot report its
            // remaining length. Only bytes actually read grow the payload buffer.
            using var payload = new MemoryStream();
            var scratch = new byte[8192];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(scratch.AsMemory(0, Math.Min(scratch.Length, remaining)), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidDataException("The Protobuf frame ends before its declared payload length.");
                }
                payload.Write(scratch, 0, read);
                remaining -= read;
            }
            ct.ThrowIfCancellationRequested();
            return (schemaVersion, _parser.ParseFrom(payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or InvalidProtocolBufferException or KeyNotFoundException)
        {
            throw new UnsupportedFormatVersionException(unchecked((uint)version),
                "Protobuf record has an invalid frame, malformed payload, or unregistered schema version.", ex);
        }
    }
}
