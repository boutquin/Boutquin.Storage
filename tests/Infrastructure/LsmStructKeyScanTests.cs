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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// The engine's generic <c>TKey</c> carries no <c>class</c>/<c>struct</c> constraint, so for a
/// struct key <c>TKey?</c> is NOT <c>Nullable&lt;TKey&gt;</c> — <c>default</c> is the zero
/// struct and a <c>startKey is not null</c> sentinel is ALWAYS true. A scan that uses that
/// sentinel to decide between "full scan" and "block-seek from startKey" silently block-seeks
/// from <c>default(TKey)</c> on every no-startKey call, omitting every on-disk entry that
/// sorts below the zero struct. These tests pin scan exactness for a legal consumer key type
/// whose valid values sort below <c>default</c>.
/// </summary>
public sealed class LsmStructKeyScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmStructKey_{Guid.NewGuid():N}");

    public LsmStructKeyScanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A perfectly legal consumer key: a record struct over a signed int. Negative values are
    /// valid keys and sort below <c>default(Int32StructKey)</c> (= 0).
    /// </summary>
    private readonly record struct Int32StructKey(int Value) : IStorageKey<Int32StructKey>
    {
        public int CompareTo(Int32StructKey other) => Value.CompareTo(other.Value);

        public void Serialize(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, Value);
            stream.Write(buffer);
        }

        public static Int32StructKey Deserialize(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            stream.ReadExactly(buffer);
            return new Int32StructKey(BinaryPrimitives.ReadInt32LittleEndian(buffer));
        }
    }

    [Fact]
    public async Task GetAllItemsAsync_StructKeysBelowDefault_AreNotDropped()
    {
        using var engine = LsmEngineFactory.Build<Int32StructKey, SerializableWrapper<string>>(
            memTableCapacity: 1000, segmentFolder: _dir, segmentPrefix: "seg");

        int[] keys = [-5, -4, -3, -2, -1, 1, 2, 3];
        foreach (var k in keys)
        {
            await engine.SetAsync(new Int32StructKey(k), new SerializableWrapper<string>($"v{k}"));
        }

        // Seal everything into an on-disk segment so the scan exercises the SST source path.
        await engine.FlushAsync();
        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(1);

        var all = (await engine.GetAllItemsAsync()).Select(kv => kv.Key.Value).OrderBy(v => v).ToArray();

        all.Should().Equal(keys, "a full scan must return every stored key, including keys that sort below default(TKey)");
    }

    [Fact]
    public async Task GetRangeAsync_StructKeys_RangeCoveringNegatives_ReturnsThem()
    {
        using var engine = LsmEngineFactory.Build<Int32StructKey, SerializableWrapper<string>>(
            memTableCapacity: 1000, segmentFolder: _dir, segmentPrefix: "seg");

        // Note: default(TKey) itself (0) is rejected by the engine's Guard.AgainstNullOrDefault
        // API contract, so it is deliberately absent — negative keys remain fully legal.
        int[] keys = [-5, -3, -1, 1, 2];
        foreach (var k in keys)
        {
            await engine.SetAsync(new Int32StructKey(k), new SerializableWrapper<string>($"v{k}"));
        }

        await engine.FlushAsync();

        var range = new List<int>();
        await foreach (var kv in engine.GetRangeAsync(new Int32StructKey(-5), new Int32StructKey(2)))
        {
            range.Add(kv.Key.Value);
        }

        range.Should().Equal(keys, "a range query whose bounds cover negative struct keys must return them in order");
    }
}
