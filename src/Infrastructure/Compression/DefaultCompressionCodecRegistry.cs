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
using Boutquin.Storage.Domain.Interfaces.Compression;

namespace Boutquin.Storage.Infrastructure.Compression;

/// <summary>
/// Default <see cref="ICompressionCodecRegistry"/> pre-populated with the four shipped
/// codecs (None, LZ4, GZip, Brotli). Consumers extend by calling <see cref="Register"/>
/// with their own <see cref="ICompressionCodec"/> implementations before opening an
/// SSTable that uses them.
/// </summary>
public sealed class DefaultCompressionCodecRegistry : ICompressionCodecRegistry
{
    private readonly Dictionary<byte, ICompressionCodec> _codecs = [];

    public DefaultCompressionCodecRegistry()
    {
        Register(new NoCompressionCodec());
        Register(new LZ4Codec());
        Register(new GZipCodec());
        Register(new BrotliCodec());
    }

    public void Register(ICompressionCodec codec)
    {
        Guard.AgainstNullOrDefault(() => codec);

        if (_codecs.TryGetValue(codec.CodecId, out var existing))
        {
            throw new ArgumentException(
                $"A codec with CodecId 0x{codec.CodecId:X2} is already registered (Name={existing.Name}); codec IDs are the wire identity and must not be rebound.",
                nameof(codec));
        }
        _codecs[codec.CodecId] = codec;
    }

    public ICompressionCodec? Resolve(byte codecId)
    {
        return _codecs.TryGetValue(codecId, out var codec) ? codec : null;
    }

    public IReadOnlyCollection<ICompressionCodec> Registered => _codecs.Values;
}
