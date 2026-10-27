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
using Boutquin.Storage.Domain.Interfaces.Compression;
using Boutquin.Storage.Infrastructure.Compression;
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class StandardCompressionAndJsonVectorTests
{
    [Theory]
    [InlineData(0, "68656C6C6F")]
    [InlineData(1, "5068656C6C6F")]
    [InlineData(2, "1F8B08000000000000FFCB48CDC9C9070086A6103605000000")]
    [InlineData(3, "0F028068656C6C6F03")]
    public void IndependentStandardBlocks_DecodeToKnownBytesAndRejectEveryTruncation(int kind, string hex)
    {
        // None: literal bytes. LZ4: literal-only token 5 << 4. GZip: CPython 3.15 gzip
        // mtime=0, zlib DEFLATE. Brotli: upstream brotli CLI 1.2.1, quality 5.
        ICompressionCodec codec = kind switch { 0 => new NoCompressionCodec(), 1 => new LZ4Codec(), 2 => new GZipCodec(), _ => new BrotliCodec() };
        var bytes = Convert.FromHexString(hex);
        var output = new byte[5];
        codec.Decompress(bytes, output, 5).Should().Be(5);
        output.Should().Equal(Encoding.ASCII.GetBytes("hello"));
        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = bytes[..length];
            Action decode = () => codec.Decompress(truncated, output, 5);
            decode.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public async Task IndependentlyFramedJson_PreservesLargeIntegersAndUnknownFieldsAcrossReadModifyWrite()
    {
        var payload = Encoding.UTF8.GetBytes("{\"Name\":\"é\",\"Count\":9007199254740993,\"future\":{\"x\":[1,2]}}");
        var frame = new byte[payload.Length + 8];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 1);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        var serializer = new JsonVersionedSerializer<JsonEvolutionRecord>();
        var registry = new InMemorySchemaRegistry(new AvroFieldLevelCompatibilityChecker());
        using var input = new MemoryStream(frame, false);
        var decoded = await serializer.DeserializeAsync(input, registry, "json").ConfigureAwait(true);
        decoded.Value.Name.Should().Be("é");
        decoded.Value.Count.Should().Be(9007199254740993L);
        decoded.Value.Extension!["future"].GetProperty("x").EnumerateArray().Select(n => n.GetInt32()).Should().Equal(1, 2);
        decoded.Value.Name = "changed";
        using var output = new MemoryStream();
        await serializer.SerializeAsync(output, decoded.Value, decoded.Version).ConfigureAwait(true);
        var result = output.ToArray();
        BinaryPrimitives.ReadInt32LittleEndian(result).Should().Be(1);
        BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(4)).Should().Be(result.Length - 8);
        using var json = JsonDocument.Parse(result.AsMemory(8));
        json.RootElement.GetProperty("Name").GetString().Should().Be("changed");
        json.RootElement.GetProperty("Count").GetInt64().Should().Be(9007199254740993L);
        json.RootElement.GetProperty("future").GetProperty("x").EnumerateArray().Select(n => n.GetInt32()).Should().Equal(1, 2);
    }

    public sealed record JsonEvolutionRecord
    {
        public string Name { get; set; } = "";
        public long Count { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extension { get; set; }
    }
}
