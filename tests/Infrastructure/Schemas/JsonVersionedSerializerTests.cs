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
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

/// <summary>
/// Tests for <see cref="JsonVersionedSerializer{T}"/>.
/// </summary>
public sealed class JsonVersionedSerializerTests
{
    private sealed record TestPayload(string Name, int Age);

    // For testing old → new evolution: new version has optional Email field
    private sealed record TestPayloadV2(string Name, int Age, string? Email = null);

    // For testing requireExtensionData — has a [JsonExtensionData] member
    private sealed record TestPayloadWithExtensionData(string Name, int Age)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }

    // The interface's stated DDIA use case is version-tagged RECORDS (plural) in a database;
    // the sibling TaggedWireVersionedSerializer is concatenation-safe. Without per-record
    // framing, JsonSerializer.DeserializeAsync reads to end-of-stream, so a second record on
    // the same stream makes even the FIRST unreadable.
    [Fact]
    public async Task TwoRecords_OnOneStream_BothRoundTrip()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);
        var version = new SchemaVersion(1);
        var first = new TestPayload("Alice", 30);
        var second = new TestPayload("Bob", 25);

        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, first, version).ConfigureAwait(true);
        await serializer.SerializeAsync(ms, second, version).ConfigureAwait(true);

        ms.Position = 0;
        var (_, readFirst) = await serializer.DeserializeAsync(ms, registry, "test").ConfigureAwait(true);
        var (_, readSecond) = await serializer.DeserializeAsync(ms, registry, "test").ConfigureAwait(true);

        readFirst.Should().Be(first);
        readSecond.Should().Be(second);
    }

    // Corrupt version headers must surface as UnsupportedFormatVersionException — the
    // fail-closed corruption-typing convention — not as ArgumentOutOfRangeException leaking
    // from the SchemaVersion constructor.
    [Fact]
    public async Task Deserialize_ZeroVersionHeader_ThrowsUnsupportedFormatVersion()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);

        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, new TestPayload("Alice", 30), new SchemaVersion(1)).ConfigureAwait(true);
        // Corrupt the 4-byte LE version header to zero.
        var buf = ms.GetBuffer();
        buf[0] = 0; buf[1] = 0; buf[2] = 0; buf[3] = 0;

        ms.Position = 0;
        var act = async () => await serializer.DeserializeAsync(ms, registry, "test").ConfigureAwait(true);

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>().ConfigureAwait(true);
    }

    // The declared payload length is the same untrusted header surface as the version: a
    // corrupt length claiming ~2 GiB must fail closed BEFORE the payload buffer is allocated
    // (class-C "declared length trusted before allocation" hazard), and a truncated body must
    // surface as the corruption exception, not a raw EndOfStreamException.
    [Fact]
    public async Task Deserialize_PayloadLengthExceedingRemainingBytes_FailsClosedWithoutAllocating()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);

        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, new TestPayload("Alice", 30), new SchemaVersion(1)).ConfigureAwait(true);
        // Corrupt the 4-byte LE length header to int.MaxValue — far beyond the bytes present.
        var buf = ms.GetBuffer();
        buf[4] = 0xFF; buf[5] = 0xFF; buf[6] = 0xFF; buf[7] = 0x7F;

        ms.Position = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = async () => await serializer.DeserializeAsync(ms, registry, "test").ConfigureAwait(true);

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>(
            "a declared payload length beyond the remaining bytes is corruption").ConfigureAwait(true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(64 * 1024 * 1024,
            "the corrupt declared length must be rejected before the payload buffer is allocated");
    }

    [Fact]
    public async Task RoundTrip_WithVersionHeader()
    {
        // Arrange
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);
        var version = new SchemaVersion(1);
        var payload = new TestPayload("Alice", 30);

        // Act — serialize
        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, payload, version).ConfigureAwait(true);

        // Act — deserialize with registry/subject (JSON ignores them — self-describing)
        ms.Position = 0;
        var (deserializedVersion, deserializedValue) = await serializer.DeserializeAsync(ms, registry, "test").ConfigureAwait(true);

        // Assert
        deserializedVersion.Should().Be(version);
        deserializedValue.Should().Be(payload);
    }

    [Fact]
    public async Task Deserialize_OldVersionPayload_WithNewerReader()
    {
        // Arrange — serialize with v1 schema (no Email field)
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var v1Serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);
        var v1Version = new SchemaVersion(1);
        var v1Payload = new TestPayload("Bob", 25);

        using var ms = new MemoryStream();
        await v1Serializer.SerializeAsync(ms, v1Payload, v1Version).ConfigureAwait(true);

        // Act — deserialize with v2 reader (has optional Email)
        ms.Position = 0;
        var v2Serializer = new JsonVersionedSerializer<TestPayloadV2>(requireExtensionData: false);
        var (version, value) = await v2Serializer.DeserializeAsync(ms, registry, "test").ConfigureAwait(true);

        // Assert — Email defaults to null
        version.Value.Should().Be(1);
        value.Name.Should().Be("Bob");
        value.Age.Should().Be(25);
        value.Email.Should().BeNull();
    }

    [Fact]
    public async Task VersionHeader_Is4BytesLittleEndian()
    {
        // Arrange
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);
        var version = new SchemaVersion(42);
        var payload = new TestPayload("Test", 1);

        // Act
        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, payload, version).ConfigureAwait(true);

        // Assert — first 4 bytes are 42 in LE
        var bytes = ms.ToArray();
        bytes.Length.Should().BeGreaterThan(4);
        var versionFromBytes = BitConverter.ToInt32(bytes, 0);
        if (!BitConverter.IsLittleEndian)
        {
            // BinaryPrimitives writes LE regardless of platform
            var span = bytes.AsSpan(0, 4);
            span.Reverse();
            versionFromBytes = BitConverter.ToInt32(bytes, 0);
        }

        versionFromBytes.Should().Be(42);
    }

    /// <summary>
    /// The Figure 5-1 guard end-to-end (DDIA 2e Ch. 5: unknown fields must survive an
    /// old-reader read-modify-write): a field only the NEWER schema knows must survive being
    /// read into the older extension-data-carrying type and re-serialized. Only the
    /// constructor gating was pinned before; this pins the actual round trip.
    /// </summary>
    [Fact]
    public async Task ExtensionData_UnknownField_SurvivesOldReaderRoundTrip()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var v2Writer = new JsonVersionedSerializer<TestPayloadV2>(requireExtensionData: false);
        var oldReader = new JsonVersionedSerializer<TestPayloadWithExtensionData>();

        // A v2 writer stores a record carrying a field (Email) the old type does not declare.
        using var stored = new MemoryStream();
        await v2Writer.SerializeAsync(stored, new TestPayloadV2("Alice", 30, "alice@example.com"), new SchemaVersion(2)).ConfigureAwait(true);

        // Old reader reads it — the unknown field lands in extension data …
        stored.Position = 0;
        var (_, oldView) = await oldReader.DeserializeAsync(stored, registry, "test").ConfigureAwait(true);
        oldView.Extensions.Should().ContainKey("Email", "the unknown field must be preserved, not discarded");

        // … and a re-serialize (the read-modify-write) must carry it back out.
        using var rewritten = new MemoryStream();
        await oldReader.SerializeAsync(rewritten, oldView, new SchemaVersion(2)).ConfigureAwait(true);
        rewritten.Position = 0;
        var (_, roundTripped) = await v2Writer.DeserializeAsync(rewritten, registry, "test").ConfigureAwait(true);

        roundTripped.Email.Should().Be("alice@example.com",
            "an old reader's rewrite must not lose the newer writer's field (DDIA 2e Fig. 5-1)");
    }

    // A nested object type keeps unknown members only when it has its own extension member:
    // System.Text.Json never routes a nested object's unknown field to the root's dictionary.
    private sealed record AddressV1(string City);

    private sealed record PersonWithRootExtensionOnly(string Name, AddressV1 Address)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }

    private sealed record AddressWithExtension(string City)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }

    private sealed record PersonWithExtensionAtEveryLevel(string Name, IReadOnlyList<AddressWithExtension> Addresses)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }

    private sealed record StaticExtensionMember(string Name)
    {
        [JsonExtensionData]
        public static Dictionary<string, JsonElement>? Extensions { get; set; }
    }

    private sealed record IgnoredExtensionMember(string Name)
    {
        [JsonExtensionData]
        [JsonIgnore]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }

    [Fact]
    public void RequireExtensionData_NestedObjectTypeWithoutExtensionMember_IsRejected()
    {
        var create = () => new JsonVersionedSerializer<PersonWithRootExtensionOnly>();

        create.Should().Throw<InvalidOperationException>(
            "an old reader's rewrite would erase a field a newer writer added to the nested address")
            .WithMessage("*AddressV1*");
    }

    [Fact]
    public void RequireExtensionData_ExtensionMemberAtEveryReachableLevel_IsAccepted()
    {
        var create = () => new JsonVersionedSerializer<PersonWithExtensionAtEveryLevel>();

        create.Should().NotThrow();
    }

    [Theory]
    [InlineData(typeof(StaticExtensionMember))]
    [InlineData(typeof(IgnoredExtensionMember))]
    public void RequireExtensionData_MemberTheSerializerNeverPopulates_IsRejected(Type payloadType)
    {
        var serializerType = typeof(JsonVersionedSerializer<>).MakeGenericType(payloadType);

        var create = () => Activator.CreateInstance(serializerType, null, true);

        create.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<InvalidOperationException>("a static or ignored member never receives unknown fields");
    }

    [JsonDerivedType(typeof(ShapeWithUnprotectedStyle), "unprotected")]
    private abstract record Shape
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }

    private sealed record ShapeWithUnprotectedStyle(AddressV1 Style) : Shape;

    [JsonDerivedType(typeof(ProtectedShape), "protected")]
    private abstract record ProtectedShapeBase
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }

    private sealed record ProtectedShape(AddressWithExtension Style) : ProtectedShapeBase;

    [Fact]
    public void RequireExtensionData_ObjectReachedThroughDerivedType_IsRejected()
    {
        var create = () => new JsonVersionedSerializer<Shape>();

        create.Should().Throw<InvalidOperationException>().WithMessage("*AddressV1*");
    }

    [Fact]
    public async Task RequireExtensionData_ProtectedDerivedGraph_PreservesNestedUnknownFields()
    {
        var serializer = new JsonVersionedSerializer<ProtectedShapeBase>();
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        using var input = new MemoryStream(FrameJsonBody(1,
            """{"$type":"protected","Style":{"City":"Paris","Width":3}}"""));
        var (_, value) = await serializer.DeserializeAsync(input, registry, "shape").ConfigureAwait(true);
        using var output = new MemoryStream();

        await serializer.SerializeAsync(output, value, new SchemaVersion(1)).ConfigureAwait(true);

        using var rewritten = JsonDocument.Parse(output.ToArray().AsMemory(8));
        rewritten.RootElement.GetProperty("Style").GetProperty("Width").GetInt32().Should().Be(3);
    }

    // Body corruption must surface as the same fail-closed domain type as header corruption
    // and truncation — a record whose declared-length payload arrives intact but holds garbage
    // bytes is the same corruption class, and a raw JsonException escaping the boundary makes
    // the caller's corruption handling format-specific.
    [Fact]
    public async Task Deserialize_GarbageJsonBody_ThrowsUnsupportedFormatVersionException()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);

        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, new TestPayload("Alice", 30), new SchemaVersion(1)).ConfigureAwait(true);
        var bytes = ms.ToArray();

        // Garbage the JSON tail — header and length prefix stay intact, so the full declared
        // payload is read and only the body parse fails.
        for (var i = bytes.Length - 4; i < bytes.Length; i++)
        {
            bytes[i] = 0xFF;
        }

        using var corrupt = new MemoryStream(bytes);
        var act = async () => await serializer.DeserializeAsync(corrupt, registry, "test");

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>(
            "a garbage body is stream corruption and must not leak a raw JsonException");
    }

    // The write path rejects null values, so a well-framed body holding the JSON literal null can
    // only come from corruption or a foreign stream — the same fail-closed class as every other
    // corrupt body, not an InvalidOperationException.
    [Fact]
    public async Task Deserialize_JsonNullBody_ThrowsUnsupportedFormatVersionException()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);

        var body = "null"u8.ToArray();
        var bytes = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), body.Length);
        body.CopyTo(bytes, 8);

        using var forged = new MemoryStream(bytes);
        var act = async () => await serializer.DeserializeAsync(forged, registry, "test").ConfigureAwait(true);

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>(
            "a null body is stream corruption and must surface the serializer's typed corruption exception")
            .ConfigureAwait(true);
    }

    // Accepting a null value writes the JSON literal "null" — a record the read path can only
    // fail on ("Deserialization returned null"). A write-succeeds/read-fails round trip is a
    // contract hole; reject at the write boundary like the entry serializers do.
    [Fact]
    public async Task SerializeAsync_NullValue_ThrowsArgumentNullException()
    {
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);
        using var ms = new MemoryStream();

        var act = async () => await serializer.SerializeAsync(ms, null!, new SchemaVersion(1));

        await act.Should().ThrowAsync<ArgumentNullException>(
            "a value that can never be read back must be rejected when written, not when read");
    }

    // default(SchemaVersion) carries Value 0; DeserializeAsync refuses a version header below 1,
    // so writing it would persist a record no reader can return.
    [Fact]
    public async Task SerializeAsync_DefaultSchemaVersion_IsRejectedBeforeAnyByteIsWritten()
    {
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);
        using var ms = new MemoryStream();

        var act = async () => await serializer.SerializeAsync(ms, new TestPayload("Ada", 36), default).ConfigureAwait(true);

        await act.Should().ThrowAsync<ArgumentException>(
            "a version header the read path rejects must be refused at the write boundary").ConfigureAwait(true);
        ms.Length.Should().Be(0, "a refused record must leave no partial header on the stream");
    }

    private sealed record PayloadWithVersion(string Name, SchemaVersion Inner);

    // The same write-accepts/read-rejects shape inside the body: a payload embedding
    // default(SchemaVersion) serializes, but its decode fails on the SchemaVersion init accessor.
    [Fact]
    public async Task SerializeAsync_PayloadThatCannotBeReadBack_IsRejectedBeforeAnyByteIsWritten()
    {
        var serializer = new JsonVersionedSerializer<PayloadWithVersion>(requireExtensionData: false);
        using var ms = new MemoryStream();

        var act = async () => await serializer.SerializeAsync(ms, new PayloadWithVersion("Ada", default), new SchemaVersion(1)).ConfigureAwait(true);

        await act.Should().ThrowAsync<ArgumentException>(
            "a body the read path cannot decode must be refused at the write boundary").ConfigureAwait(true);
        ms.Length.Should().Be(0, "a refused record must leave no bytes on the stream");
    }

    // Frames a raw JSON body as [LE int32 version][LE int32 length][body] — the serializer's own
    // wire layout — so a test can present bytes the writer would refuse to produce.
    private static byte[] FrameJsonBody(int version, string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var framed = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), version);
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), body.Length);
        body.CopyTo(framed.AsSpan(8));
        return framed;
    }

    // A one-bit flip ("Value":1 → "Value":0) turns a stored body into well-formed JSON whose
    // decode fails in the SchemaVersion init accessor, outside System.Text.Json. The writer
    // refuses such bodies, so on read they can only be corruption: the reader must surface the
    // typed corruption exception, not the accessor's ArgumentOutOfRangeException.
    [Fact]
    public async Task Deserialize_BodyEmbeddingZeroSchemaVersion_ThrowsUnsupportedFormatVersionException()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<PayloadWithVersion>(requireExtensionData: false);
        using var stream = new MemoryStream(FrameJsonBody(1, """{"Name":"Ada","Inner":{"Value":0}}"""));

        var act = async () => await serializer.DeserializeAsync(stream, registry, "test").ConfigureAwait(true);

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>(
            "a body the writer could never have produced is corruption, not a caller argument error").ConfigureAwait(true);
    }

    private sealed record PositivePayload(int Count)
    {
        public int Count { get; } = Count >= 1 ? Count : throw new ArgumentOutOfRangeException(nameof(Count));
    }

    // The same corruption class through a validating constructor: the body is schema-valid JSON,
    // but the type rejects the decoded value. A caller quarantining records on the typed
    // corruption exception must not receive an argument exception naming a constructor parameter.
    [Fact]
    public async Task Deserialize_BodyRejectedByTypeConstructor_ThrowsUnsupportedFormatVersionException()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<PositivePayload>(requireExtensionData: false);
        using var stream = new MemoryStream(FrameJsonBody(1, """{"Count":0}"""));

        var act = async () => await serializer.DeserializeAsync(stream, registry, "test").ConfigureAwait(true);

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>(
            "a decoded body the type rejects is corruption, not a caller argument error").ConfigureAwait(true);
    }

    [Fact]
    public void Constructor_RequireExtensionData_TypeWithoutExtensionData_Throws()
    {
        // TestPayload has no [JsonExtensionData] member — construction should throw
        var act = () => new JsonVersionedSerializer<TestPayload>(requireExtensionData: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JsonExtensionData*");
    }

    [Fact]
    public void Constructor_RequireExtensionData_TypeWithExtensionData_Succeeds()
    {
        // TestPayloadWithExtensionData has a [JsonExtensionData] member — no throw
        var act = () => new JsonVersionedSerializer<TestPayloadWithExtensionData>(requireExtensionData: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_RequireExtensionData_False_DoesNotThrowEvenWithoutExtensionData()
    {
        // Explicit opt-out never inspects T for extension data.
        var act = () => new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_Default_IsSafeByDefault_RequiresExtensionData()
    {
        // Finding #12 — the default flipped to requireExtensionData: true, so a type without a
        // [JsonExtensionData] member fails fast at construction rather than silently dropping
        // unknown fields on a read-modify-write (DDIA Ch. 5 forward-compat data-loss hazard).
        var act = () => new JsonVersionedSerializer<TestPayload>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JsonExtensionData*");
    }

    // AC-8.3 (F-15): truncated header — ReadExactlyAsync at :122 throws EndOfStreamException
    // when the stream has fewer than 8 header bytes. Without a try/catch around it,
    // the raw EndOfStreamException escapes instead of UnsupportedFormatVersionException.
    [Fact]
    public async Task Deserialize_TruncatedHeader_ThrowsUnsupportedFormatVersionException()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);

        // Only 3 bytes — ReadExactlyAsync needs 8 bytes for the header.
        using var stream = new MemoryStream([0x01, 0x00, 0x00]);
        var act = async () => await serializer.DeserializeAsync(stream, registry, "test").ConfigureAwait(true);

        // Must be UnsupportedFormatVersionException, NOT EndOfStreamException.
        await act.Should().ThrowAsync<UnsupportedFormatVersionException>().ConfigureAwait(true);
    }

    // The remaining-bytes bound that protects the seekable allocation is gated behind
    // stream.CanSeek. On a NON-seekable stream that bound never fires, so a forged ~2 GiB
    // declared length reaches `new byte[payloadLength]` and rents the full declared size
    // before any read observes the short stream (class-C "declared length trusted before
    // allocation"). The fix reads the payload bounded against bytes actually supplied,
    // surfacing the typed corruption exception without pre-allocating the forged size.
    [Fact]
    public async Task Deserialize_NonSeekableForgedPayloadLength_FailsClosedWithoutAllocating()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);

        // Build a frame: valid version header (1), forged length header (int.MaxValue),
        // followed by only a few real payload bytes.
        var frame = new byte[8 + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), int.MaxValue);
        using var stream = new NonSeekableStream(frame);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = async () => await serializer.DeserializeAsync(stream, registry, "test").ConfigureAwait(true);

        await act.Should().ThrowAsync<UnsupportedFormatVersionException>(
            "a forged declared payload length on a non-seekable stream is corruption").ConfigureAwait(true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(64 * 1024 * 1024,
            "the forged declared length must not pre-allocate the full declared size on a non-seekable stream");
    }

    // A non-seekable stream carrying a well-formed, complete record must still round-trip —
    // the bounded read must not break the happy path.
    [Fact]
    public async Task Deserialize_NonSeekableValidRecord_RoundTrips()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        var serializer = new JsonVersionedSerializer<TestPayload>(requireExtensionData: false);
        var version = new SchemaVersion(1);
        var payload = new TestPayload("Alice", 30);

        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, payload, version).ConfigureAwait(true);
        using var stream = new NonSeekableStream(ms.ToArray());

        var (deserializedVersion, deserializedValue) =
            await serializer.DeserializeAsync(stream, registry, "test").ConfigureAwait(true);

        deserializedVersion.Should().Be(version);
        deserializedValue.Should().Be(payload);
    }

    // A non-seekable read-only stream over a fixed byte array — exercises the CanSeek == false
    // branch that the seekable remaining-bytes bound cannot protect.
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override int ReadByte() => _inner.ReadByte();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
