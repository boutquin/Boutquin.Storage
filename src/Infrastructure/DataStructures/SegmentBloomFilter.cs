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
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Infrastructure.DataStructures;

// One physical segment owns this object. Native SST state keeps its original descriptor;
// columnar and custom SST state use the bounded envelope below inside the footer CRC.
internal sealed class SegmentBloomFilter<TKey> : IDisposable where TKey : ISerializable<TKey>
{
    private const int MaxStateBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);
    private static readonly ConditionalWeakTable<IBloomFilter<TKey>, object> s_ownedFilters = new();
    private readonly SsTableBloomBits? _native;
    private readonly IBloomFilter<TKey>? _custom;
    private readonly IBloomFilterStateCodec<TKey>? _codec;
    private readonly Lock _gate = new();
    private bool _disposed;

    private SegmentBloomFilter(SsTableBloomBits? native, IBloomFilter<TKey>? custom = null, IBloomFilterStateCodec<TKey>? codec = null)
        => (_native, _custom, _codec) = (native, custom, codec);

    public int BitsCount => _native?.BitsCount ?? 0;
    public int HashCount => _native?.HashCount ?? 0;
    public byte[] Bits => _native?.Bits.ToArray() ?? [];
    public bool IsCustom => _custom is not null;

    public static SegmentBloomFilter<TKey> Create(SegmentBloomFilterOptions<TKey> options, int count)
    {
        if (!options.Enabled)
        {
            return new(null);
        }
        var capacity = options.ExpectedItems ?? Math.Max(1, count);
        if (options.Factory is { } factory)
        {
            var filter = factory(capacity, options.FalsePositiveProbability)
                ?? throw new InvalidOperationException("Bloom factory returned null.");
            Claim(filter);
            return new(null, filter, options.StateCodec);
        }
        return new(new SsTableBloomBits(capacity, options.FalsePositiveProbability));
    }

    public static SegmentBloomFilter<TKey> FromNative(int bitsCount, int hashCount, byte[] bits)
        => new(new SsTableBloomBits(bitsCount, hashCount == 0 ? 1 : hashCount, bits));

    public void Add(TKey key)
        => Add(key, _native is null ? [] : KeyBytes(key));

    public void Add(TKey key, ReadOnlySpan<byte> nativeKeyBytes)
    {
        if (_custom is not null)
        {
            _custom.Add(key);
        }
        else
        {
            _native?.Add(nativeKeyBytes);
        }
    }

    public bool Contains(TKey key)
        => Contains(key, _native is null ? [] : KeyBytes(key));

    public bool Contains(TKey key, ReadOnlySpan<byte> nativeKeyBytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _custom?.Contains(key) ?? _native?.MightContain(nativeKeyBytes) ?? true;
        }
    }

    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        stream.Write("BLM1"u8);
        stream.WriteByte(_custom is null ? (byte)0 : (byte)1);
        if (_custom is null)
        {
            WriteInt(stream, BitsCount);
            WriteInt(stream, HashCount);
            WriteBytes(stream, Bits);
        }
        else
        {
            WriteBytes(stream, s_strictUtf8.GetBytes(_codec!.CodecId));
            WriteInt(stream, _codec.Version);
            var state = _codec.Serialize(_custom) ?? throw new InvalidDataException("Bloom codec returned null state.");
            if (state.Length > MaxStateBytes)
            {
                throw new InvalidDataException("Custom Bloom state exceeds 16 MiB.");
            }
            WriteBytes(stream, state);
        }
        return stream.ToArray();
    }

    public static SegmentBloomFilter<TKey> Deserialize(ReadOnlyMemory<byte> bytes, SegmentBloomFilterOptions<TKey> options)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual("BLM1"u8))
        {
            throw new InvalidDataException("Unknown segment Bloom envelope.");
        }
        var kind = stream.ReadByte();
        if (kind == 0)
        {
            var bits = ReadInt(stream);
            var hashes = ReadInt(stream);
            var data = ReadBytes(stream, 256 * 1024 * 1024);
            RequireEnd(stream);
            return FromNative(bits, hashes, data);
        }
        if (kind != 1)
        {
            throw new InvalidDataException("Unknown segment Bloom filter kind.");
        }
        var id = s_strictUtf8.GetString(ReadBytes(stream, 256));
        var version = ReadInt(stream);
        if (string.IsNullOrWhiteSpace(id) || version < 1)
        {
            throw new InvalidDataException("Invalid Bloom codec identity or version.");
        }
        var state = ReadBytes(stream, MaxStateBytes);
        RequireEnd(stream);
        var codec = options.StateCodec;
        if (codec is null || id != codec.CodecId || version != codec.Version)
        {
            throw new UnsupportedBloomFilterCodecException(id, version);
        }
        var filter = codec.Deserialize(state) ?? throw new InvalidDataException("Bloom codec returned null filter.");
        Claim(filter);
        return new(null, filter, codec);
    }

    public static void ValidateState(ReadOnlyMemory<byte> state, SegmentBloomFilterOptions<TKey> options, IEnumerable<TKey> keys)
    {
        using var restored = Deserialize(state, options);
        foreach (var key in keys)
        {
            if (!restored.Contains(key))
            {
                throw new InvalidDataException("Persisted Bloom state rejects a key present in the segment.");
            }
        }
    }

    private static void Claim(IBloomFilter<TKey> filter)
    {
        lock (s_ownedFilters)
        {
            if (s_ownedFilters.TryGetValue(filter, out _))
            {
                throw new InvalidOperationException("Bloom factories and codecs must return a fresh filter for every segment; an instance was reused.");
            }
            s_ownedFilters.Add(filter, new object());
        }
    }

    private static byte[] KeyBytes(TKey key)
    {
        using var stream = new MemoryStream();
        key.Serialize(stream);
        return stream.ToArray();
    }

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> bytes)
    {
        WriteInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static byte[] ReadBytes(Stream stream, int maximum)
    {
        var length = ReadInt(stream);
        if (length < 0 || length > maximum || length > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Segment Bloom state length exceeds its bounded payload.");
        }
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RequireEnd(Stream stream)
    {
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Trailing bytes in segment Bloom state.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _custom?.Dispose();
            }
        }
    }
}
