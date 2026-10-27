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
/// Tests for <see cref="TaggedWireVersionedSerializer{T}"/> — version-framed tagged-wire serialization
/// resolved against a schema registry. Reuses the <c>BridgePerson</c> record from the bridge tests.
/// </summary>
public sealed class TaggedWireVersionedSerializerTests
{
    private static InMemorySchemaRegistry RegistryWithPersonV1()
    {
        var registry = new InMemorySchemaRegistry(new TaggedWireCompatibilityChecker());
        _ = registry.RegisterAsync(
            "person", TaggedWireSchema.FromType<BridgePerson>(), CompatibilityMode.Backward).AsTask().GetAwaiter().GetResult();
        return registry;
    }

    [Fact]
    public async Task SerializeThenDeserialize_RoundTripsValueAndVersion()
    {
        var registry = RegistryWithPersonV1();
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();
        var original = new BridgePerson("Ada", 36);

        using var stream = new MemoryStream();
        await serializer.SerializeAsync(stream, original, new SchemaVersion(1));
        stream.Position = 0;
        var (version, value) = await serializer.DeserializeAsync(stream, registry, "person");

        version.Value.Should().Be(1);
        value.Should().Be(original);
    }

    [Fact]
    public async Task Deserialize_VersionNotInRegistry_ThrowsUnsupportedFormatVersion()
    {
        var registry = RegistryWithPersonV1(); // only version 1 exists
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();

        using var stream = new MemoryStream();
        await serializer.SerializeAsync(stream, new BridgePerson("Ada", 36), new SchemaVersion(7));
        stream.Position = 0;
        var act = async () => await serializer.DeserializeAsync(stream, registry, "person");

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>();
    }

    // Corrupt version headers must surface as UnsupportedFormatVersionException — the
    // fail-closed corruption-typing convention — not as ArgumentOutOfRangeException (version
    // varint decodes to 0, rejected by the SchemaVersion constructor) or OverflowException
    // (varint exceeds int.MaxValue, rejected by the checked cast).
    [Fact]
    public async Task Deserialize_ZeroVersionVarint_ThrowsUnsupportedFormatVersion()
    {
        var registry = RegistryWithPersonV1();
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();

        // Varint 0x00 = version 0 followed by arbitrary payload bytes.
        using var stream = new MemoryStream([0x00, 0x02, 0x01]);
        var act = async () => await serializer.DeserializeAsync(stream, registry, "person");

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>();
    }

    [Fact]
    public async Task Deserialize_VersionVarintBeyondInt32_ThrowsUnsupportedFormatVersion()
    {
        var registry = RegistryWithPersonV1();
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();

        // ULEB128 for 2^32 + 1 = 0x81 0x80 0x80 0x80 0x10 — exceeds int.MaxValue.
        using var stream = new MemoryStream([0x81, 0x80, 0x80, 0x80, 0x10, 0x02, 0x01]);
        var act = async () => await serializer.DeserializeAsync(stream, registry, "person");

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>();
    }

    // AC-8.3 (F-14): truncated stream — EndOfStreamException from ReadVarUInt64 must be
    // wrapped as UnsupportedFormatVersionException, not escape as a raw EndOfStreamException.
    [Fact]
    public async Task Deserialize_TruncatedStream_ThrowsUnsupportedFormatVersionException()
    {
        var registry = RegistryWithPersonV1();
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();

        // Empty stream: ReadVarUInt64 will throw EndOfStreamException. Without a try/catch
        // around the call at :79, the raw EndOfStreamException escapes.
        using var stream = new MemoryStream([]);
        var act = async () => await serializer.DeserializeAsync(stream, registry, "person");

        // Must be UnsupportedFormatVersionException, NOT EndOfStreamException.
        await act.Should().ThrowAsync<UnsupportedFormatVersionException>();
    }

    // Finding E (f922f064): the version varint read AND the registry lookup both wrap their
    // failures into the typed corruption exception, but the body decode (T.Deserialize) was
    // unguarded — a torn or corrupt tagged-wire body leaked the raw EndOfStreamException /
    // InvalidDataException the generated reader throws. A truncated body is the same
    // corruption class as a truncated version varint and must surface the same fail-closed
    // type, matching the JSON sibling whose truncated body also wraps.
    [Fact]
    public async Task Deserialize_TornBody_ThrowsUnsupportedFormatVersionException()
    {
        var registry = RegistryWithPersonV1();
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();

        // Serialize a valid record, then truncate the body — keep the full version varint
        // (so the version read + registry lookup both succeed) but lop off the trailing body
        // bytes so T.Deserialize hits end-of-stream mid-decode.
        using var full = new MemoryStream();
        await serializer.SerializeAsync(full, new BridgePerson("Ada", 36), new SchemaVersion(1));
        var bytes = full.ToArray();
        // Drop the last 3 body bytes — version varint (1 byte) survives; the body is torn.
        var torn = bytes.AsSpan(0, bytes.Length - 3).ToArray();

        using var stream = new MemoryStream(torn);
        var act = async () => await serializer.DeserializeAsync(stream, registry, "person");

        // Must be UnsupportedFormatVersionException, NOT a raw EndOfStreamException /
        // InvalidDataException escaping the serializer boundary.
        await act.Should().ThrowAsync<UnsupportedFormatVersionException>();
    }

    // A forged in-body varint past int.MaxValue makes the generated reader's checked((int)…)
    // narrowing throw OverflowException — the same corruption class as a forged version varint
    // or a torn body, and the same low-level decode artifact the boundary contract exists to
    // contain. It must surface as the typed corruption exception, not a raw OverflowException.
    [Fact]
    public async Task Deserialize_ForgedBodyLengthBeyondInt32_ThrowsUnsupportedFormatVersionException()
    {
        var registry = RegistryWithPersonV1();
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();

        // [0x01] = registered version 1; then the record's self-framing payload-length varint
        // forged to 2^32 (0x80 0x80 0x80 0x80 0x10) — checked((int)…) overflows.
        using var stream = new MemoryStream([0x01, 0x80, 0x80, 0x80, 0x80, 0x10]);
        var act = async () => await serializer.DeserializeAsync(stream, registry, "person");

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>(
            "a forged length past int.MaxValue is stream corruption and must not leak a raw OverflowException");
    }

    // A null reference-type value must be rejected at the API boundary with the standard typed
    // guard — not surface later as a NullReferenceException from value.Serialize (the entry
    // serializers already guard; the versioned serializers must match).
    [Fact]
    public async Task SerializeAsync_NullReferenceValue_ThrowsArgumentNullException()
    {
        var serializer = new TaggedWireVersionedSerializer<RefPayload>();
        using var stream = new MemoryStream();

        var act = async () => await serializer.SerializeAsync(stream, null!, new SchemaVersion(1));

        await act.Should().ThrowAsync<ArgumentNullException>(
            "a null value must be a typed boundary rejection, not an NRE from value.Serialize");
    }

    // default(SchemaVersion) carries Value 0, which DeserializeAsync rejects as a corrupt header.
    // Writing it would acknowledge a record no read can return, so the writer must refuse it
    // before any byte reaches the stream — as the JSON and Protobuf siblings do.
    [Fact]
    public async Task SerializeAsync_DefaultSchemaVersion_IsRejectedBeforeAnyByteIsWritten()
    {
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();
        using var ms = new MemoryStream();

        var act = async () => await serializer.SerializeAsync(ms, new BridgePerson("Ada", 36), default).ConfigureAwait(true);

        await act.Should().ThrowAsync<ArgumentException>(
            "a version header the read path rejects must be refused at the write boundary").ConfigureAwait(true);
        ms.Length.Should().Be(0, "a refused record must leave no partial header on the stream");
    }

    // The generated body writer refuses ill-formed UTF-16 (a lone surrogate). The refusal must
    // leave the caller's stream untouched: a stray version header would be read as the start of
    // the next record and corrupt every record appended after it.
    [Fact]
    public async Task SerializeAsync_BodyRejectedByWriter_LeavesNoBytesAndNextRecordReadsBack()
    {
        var registry = RegistryWithPersonV1();
        var serializer = new TaggedWireVersionedSerializer<BridgePerson>();
        using var ms = new MemoryStream();

        var act = async () => await serializer
            .SerializeAsync(ms, new BridgePerson("\uD800", 1), new SchemaVersion(1)).ConfigureAwait(true);

        await act.Should().ThrowAsync<ArgumentException>(
            "the generated writer refuses a lone surrogate").ConfigureAwait(true);
        ms.Length.Should().Be(0, "a refused value must leave no version header on the stream");

        await serializer.SerializeAsync(ms, new BridgePerson("Ada", 36), new SchemaVersion(1)).ConfigureAwait(true);
        ms.Position = 0;
        var (version, value) = await serializer.DeserializeAsync(ms, registry, "person").ConfigureAwait(true);
        version.Value.Should().Be(1);
        value.Should().Be(new BridgePerson("Ada", 36));
    }

    // A body whose bytes decode but whose type rejects the decoded value (a validating
    // constructor or init accessor) can only come from corruption or a foreign stream: the
    // writer could never have produced it. It is the same fail-closed class as a torn body and
    // must surface as the typed corruption exception, not as the type's argument exception.
    [Fact]
    public async Task Deserialize_BodyRejectedByTypeConstructor_ThrowsUnsupportedFormatVersionException()
    {
        var registry = RegistryWithPersonV1(); // tagged wire validates only that the version exists
        var serializer = new TaggedWireVersionedSerializer<PositiveCount>();

        using var full = new MemoryStream();
        await serializer.SerializeAsync(full, new PositiveCount(5), new SchemaVersion(1)).ConfigureAwait(true);
        var bytes = full.ToArray();
        bytes[^1] = 0; // corrupt the body byte to a value the constructor rejects

        using var stream = new MemoryStream(bytes);
        var act = async () => await serializer.DeserializeAsync(stream, registry, "person").ConfigureAwait(true);

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>(
            "a decoded body the type rejects is stream corruption, not a caller argument error").ConfigureAwait(true);
    }

    // The body wrap types decode failures only. A fault of the stream itself (a device error)
    // is not evidence that the record is corrupt and must propagate unchanged, so a caller does
    // not quarantine a healthy record because its disk hiccupped.
    [Fact]
    public async Task Deserialize_StreamFaultDuringBody_PropagatesIOException()
    {
        var registry = RegistryWithPersonV1();
        var serializer = new TaggedWireVersionedSerializer<PositiveCount>();
        using var stream = new FaultAfterFirstByteStream([0x01, 0x05]);

        var act = async () => await serializer.DeserializeAsync(stream, registry, "person").ConfigureAwait(true);

        (await act.Should().ThrowAsync<IOException>(
            "a device fault is not record corruption").ConfigureAwait(true))
            .Which.Should().NotBeOfType<EndOfStreamException>();
    }

    private sealed class PositiveCount : ISerializable<PositiveCount>
    {
        public PositiveCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            Count = count;
        }

        public int Count { get; }

        public void Serialize(Stream stream) => stream.WriteByte((byte)Count);

        public static PositiveCount Deserialize(Stream stream)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException();
            }

            return new PositiveCount(b);
        }
    }

    // Serves the first byte (the version varint), then faults every later read with a device error.
    private sealed class FaultAfterFirstByteStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= 1)
            {
                throw new IOException("Simulated device fault.");
            }

            if (buffer.Length == 0)
            {
                return 0;
            }

            buffer[0] = data[_position++];
            return 1;
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 0 ? -1 : one[0];
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RefPayload : ISerializable<RefPayload>
    {
        public void Serialize(Stream stream) => stream.WriteByte(1);

        public static RefPayload Deserialize(Stream stream)
        {
            _ = stream.ReadByte();
            return new RefPayload();
        }
    }
}
