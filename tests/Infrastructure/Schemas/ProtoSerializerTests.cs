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

using Google.Protobuf.WellKnownTypes;

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

public sealed class ProtoSerializerTests
{
    [Fact]
    public async Task FramedRecords_AreConcatenationSafe_AndBodiesAreStandardProtobuf()
    {
        var registry = await Registry().ConfigureAwait(true);
        var serializer = new ProtoSerializer<StringValue>(StringValue.Parser);
        using var stream = new MemoryStream();
        var first = new StringValue { Value = "hello" };
        await serializer.SerializeAsync(stream, first, new SchemaVersion(1)).ConfigureAwait(true);
        var firstLength = stream.Position;
        await serializer.SerializeAsync(stream, new StringValue { Value = "world" }, new SchemaVersion(1)).ConfigureAwait(true);
        Assert.Equal(first, StringValue.Parser.ParseFrom(stream.ToArray().AsSpan(8, (int)firstLength - 8)));
        stream.Position = 0;
        Assert.Equal("hello", (await serializer.DeserializeAsync(stream, registry, "message").ConfigureAwait(true)).Value.Value);
        Assert.Equal(firstLength, stream.Position);
        Assert.Equal("world", (await serializer.DeserializeAsync(stream, registry, "message").ConfigureAwait(true)).Value.Value);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task UnknownFields_SurviveOldReaderReadModifyWrite()
    {
        var registry = await Registry().ConfigureAwait(true);
        var newer = new ProtoSerializer<StringValue>(StringValue.Parser);
        var older = new ProtoSerializer<Empty>(Empty.Parser.WithDiscardUnknownFields(true));
        using var input = new MemoryStream();
        await newer.SerializeAsync(input, new StringValue { Value = "preserve me" }, new SchemaVersion(1)).ConfigureAwait(true);
        input.Position = 0;
        var oldValue = await older.DeserializeAsync(input, registry, "message").ConfigureAwait(true);
        using var output = new MemoryStream();
        await older.SerializeAsync(output, oldValue.Value, oldValue.Version).ConfigureAwait(true);
        output.Position = 0;
        Assert.Equal("preserve me", (await newer.DeserializeAsync(output, registry, "message").ConfigureAwait(true)).Value.Value);
    }

    [Fact]
    public async Task EmptyMessage_IsValid_AndUnregisteredVersionFailsClosed()
    {
        var registry = await Registry().ConfigureAwait(true);
        var serializer = new ProtoSerializer<Empty>(Empty.Parser);
        using var input = new MemoryStream();
        await serializer.SerializeAsync(input, new Empty(), new SchemaVersion(2)).ConfigureAwait(true);
        input.Position = 0;
        await Assert.ThrowsAsync<UnsupportedFormatVersionException>(async () =>
            await serializer.DeserializeAsync(input, registry, "message").ConfigureAwait(true)).ConfigureAwait(true);
        input.SetLength(0);
        input.Position = 0;
        await serializer.SerializeAsync(input, new Empty(), new SchemaVersion(1)).ConfigureAwait(true);
        input.Position = 0;
        Assert.NotNull((await serializer.DeserializeAsync(input, registry, "message").ConfigureAwait(true)).Value);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(1, 20)]
    public async Task InvalidOrTruncatedLength_IsRejectedBeforePayloadAllocation(int version, int length)
    {
        var registry = await Registry().ConfigureAwait(true);
        var serializer = new ProtoSerializer<Empty>(Empty.Parser, maxMessageBytes: 64);
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, version);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), length);
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<UnsupportedFormatVersionException>(async () =>
            await serializer.DeserializeAsync(input, registry, "message").ConfigureAwait(true)).ConfigureAwait(true);
    }

    [Fact]
    public async Task WriteLimitAndCancellation_LeaveOutputUntouched()
    {
        var serializer = new ProtoSerializer<StringValue>(StringValue.Parser, maxMessageBytes: 1);
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await serializer.SerializeAsync(stream, new StringValue { Value = "too large" }, new SchemaVersion(1)).ConfigureAwait(true)).ConfigureAwait(true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await serializer.SerializeAsync(stream, new StringValue(), new SchemaVersion(1), cancellation.Token).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task GeneratedSchemas_EvolveAndReplayThroughRegisteredVersions()
        => await Boutquin.Storage.Samples.ProtoEvolutionDemo.RunAsync().ConfigureAwait(true);

    [Fact]
    public async Task RegistryRejectsTagTypeChangesAndReuseAfterRetirement()
    {
        var registry = new InMemorySchemaRegistry(new TaggedWireCompatibilityChecker());
        var initial = new TaggedWireSchema([new(1, "name", "string", true), new(2, "score", "int", true)]);
        await registry.RegisterAsync("person", initial, CompatibilityMode.Full).ConfigureAwait(true);
        var retired = new TaggedWireSchema([new(1, "display_name", "string", true)], [2]);
        await registry.RegisterAsync("person", retired, CompatibilityMode.Full).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registry.RegisterAsync("person", initial, CompatibilityMode.Full).ConfigureAwait(true)).ConfigureAwait(true);
        var incompatible = new TaggedWireSchema([new(1, "name", "int", true)], [2]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registry.RegisterAsync("person", incompatible, CompatibilityMode.Full).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [Fact]
    public async Task NonseekableTruncationAndMalformedBody_AreRejected()
    {
        var registry = await Registry().ConfigureAwait(true);
        var serializer = new ProtoSerializer<Empty>(Empty.Parser);
        using var truncated = new NonseekableStream([1, 0, 0, 0, 20, 0, 0, 0, 8]);
        await Assert.ThrowsAsync<UnsupportedFormatVersionException>(async () =>
            await serializer.DeserializeAsync(truncated, registry, "message").ConfigureAwait(true)).ConfigureAwait(true);
        using var malformed = new MemoryStream([1, 0, 0, 0, 1, 0, 0, 0, 255]);
        await Assert.ThrowsAsync<UnsupportedFormatVersionException>(async () =>
            await serializer.DeserializeAsync(malformed, registry, "message").ConfigureAwait(true)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("low")]
    public async Task IllFormedText_IsRefusedBeforeAnyByteIsWritten(string kind)
    {
        var serializer = new ProtoSerializer<StringValue>(StringValue.Parser);
        using var stream = new MemoryStream();
        var text = kind == "high" ? "key\uD800" : "key\uDC00";

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await serializer.SerializeAsync(stream, new StringValue { Value = text }, new SchemaVersion(1))
                .ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(0, stream.Length);
        var valid = new StringValue { Value = "key😀" };
        await serializer.SerializeAsync(stream, valid, new SchemaVersion(1)).ConfigureAwait(true);
        Assert.Equal(valid, StringValue.Parser.ParseFrom(stream.ToArray().AsSpan(8)));
    }

    [Fact]
    public async Task NestedIllFormedText_IsRefusedBeforeAnyByteIsWritten()
    {
        var serializer = new ProtoSerializer<ListValue>(ListValue.Parser);
        using var stream = new MemoryStream();
        var value = new ListValue();
        value.Values.Add(Google.Protobuf.WellKnownTypes.Value.ForString("nested\uD800"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await serializer.SerializeAsync(stream, value, new SchemaVersion(1)).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(0, stream.Length);
    }

    private sealed class NonseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    private static async Task<InMemorySchemaRegistry> Registry()
    {
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        await registry.RegisterAsync("message", new SimpleSchema(new SchemaField("value", SchemaFieldType.String, true)),
            CompatibilityMode.Full).ConfigureAwait(true);
        return registry;
    }
}
