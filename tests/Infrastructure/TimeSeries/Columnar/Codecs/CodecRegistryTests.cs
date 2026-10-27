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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar.Codecs;

public sealed class CodecRegistryTests
{
    [Theory]
    [InlineData((byte)ColumnEncodingTag.PlainRawBytes)]
    [InlineData((byte)ColumnEncodingTag.BitmapRle)]
    [InlineData((byte)ColumnEncodingTag.Dictionary)]
    [InlineData((byte)ColumnEncodingTag.DeltaVlq)]
    [InlineData((byte)ColumnEncodingTag.DeltaOfDeltaVlq)]
    [InlineData((byte)ColumnEncodingTag.ScaledIntegerBitPacked)]
    [InlineData((byte)ColumnEncodingTag.GorillaXor)]
    public void ImplementedTags_LookupSucceedsAndIsImplemented(byte tagByte)
    {
        var tag = (ColumnEncodingTag)tagByte;
        CodecRegistry.TryGet(tag, out var desc).Should().BeTrue();
        desc.Tag.Should().Be(tag);
        desc.IsImplemented.Should().BeTrue();
        desc.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GorillaXor_IsImplementedInPhase3b()
    {
        // In Phase 3a the Gorilla tag was reserved with IsImplemented = false so a
        // Phase-3a reader encountering a forward-compat segment would refuse rather than
        // decode silently-wrong bytes. Phase 3b ships the codec, so the registry flips
        // the flag — any segment with this tag now decodes via GorillaXorCodec.
        CodecRegistry.TryGet(ColumnEncodingTag.GorillaXor, out var desc).Should().BeTrue();
        desc.Tag.Should().Be(ColumnEncodingTag.GorillaXor);
        desc.IsImplemented.Should().BeTrue();
        desc.CurrentVersion.Should().Be(GorillaXorCodec.Version);
    }

    [Fact]
    public void UnknownTag_LookupFails()
    {
        CodecRegistry.TryGet((ColumnEncodingTag)200, out _).Should().BeFalse();
    }

    [Fact]
    public void All_ReturnsEveryDeclaredTag()
    {
        var tags = CodecRegistry.All.Select(d => d.Tag).ToHashSet();
        tags.Should().Contain(
        [
            ColumnEncodingTag.PlainRawBytes,
            ColumnEncodingTag.BitmapRle,
            ColumnEncodingTag.Dictionary,
            ColumnEncodingTag.DeltaVlq,
            ColumnEncodingTag.DeltaOfDeltaVlq,
            ColumnEncodingTag.ScaledIntegerBitPacked,
            ColumnEncodingTag.GorillaXor,
        ]);
    }
}
