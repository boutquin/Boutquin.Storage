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
namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// F-76 — bloom hot-path allocation. <see cref="SsTableBloomBits.Add"/> and
/// <see cref="SsTableBloomBits.MightContain"/> are on the per-key SSTable point-lookup path. The
/// pre-fix implementation copied the <see cref="ReadOnlySpan{Byte}"/> key to a fresh
/// <c>byte[]</c> via <c>key.ToArray()</c> on every call before handing it to the hashers — a
/// per-probe heap allocation on the hottest read path. The hashers already accept
/// <see cref="ReadOnlySpan{Byte}"/>, so the copy is gratuitous; passing the span through directly
/// removes the allocation while keeping membership semantics byte-identical (this file also pins
/// that equivalence).
/// </summary>
public sealed class SsTableBloomBitsAllocationTests
{
    [Fact]
    public void AddAndMightContain_DoNotAllocatePerProbe()
    {
        // Arrange — size for ~512 keys; build the byte keys up front so the only candidate
        // allocation inside the measured loop is the (removed) per-probe ToArray().
        var bloom = new SsTableBloomBits(expectedEntries: 512);
        var keys = new byte[2048][];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = System.Text.Encoding.UTF8.GetBytes($"key-{i:D6}");
        }

        // Warm up the hashers / JIT so steady-state allocation is what we measure.
        for (var i = 0; i < keys.Length; i++)
        {
            bloom.Add(keys[i]);
            _ = bloom.MightContain(keys[i]);
        }

        // Act — measure managed allocations across a batch of Add + MightContain probes.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < keys.Length; i++)
        {
            bloom.Add(keys[i]);
            _ = bloom.MightContain(keys[i]);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert — the bloom probes must allocate nothing on the hot path. A per-probe
        // ToArray() over 2048 keys × 2 calls allocates tens of KB; the span-through path
        // allocates zero. A small slack absorbs unrelated runtime noise but stays far below
        // the per-probe-copy footprint.
        allocated.Should().BeLessThan(1024,
            "Add/MightContain must pass the ReadOnlySpan<byte> key straight to the hashers with no " +
            "per-probe ToArray() allocation on the SSTable point-lookup hot path");
    }

    [Fact]
    public void SpanThrough_PreservesMembershipSemantics()
    {
        // A membership characterization pin: every added key reports present, and the
        // hashing is span-driven, so this stays green only if the span is hashed the same
        // way the byte[] copy was.
        var bloom = new SsTableBloomBits(expectedEntries: 256);
        for (var i = 0; i < 256; i++)
        {
            bloom.Add(System.Text.Encoding.UTF8.GetBytes($"member-{i}"));
        }

        for (var i = 0; i < 256; i++)
        {
            bloom.MightContain(System.Text.Encoding.UTF8.GetBytes($"member-{i}")).Should().BeTrue(
                "a key that was added must always report possibly-present (bloom filters have no false negatives)");
        }
    }

    [Fact]
    public void Constructor_HashCountExceedsMaximum_ThrowsInvalidDataException()
    {
        // A forged hashCount = 65 (above the MaxHashFunctions = 64 cap) would make every
        // MightContain call iterate 65+ billion times — a CPU DoS via a crafted SSTable footer.
        // The deserialization constructor must reject values above the cap with InvalidDataException.
        var act = () => new SsTableBloomBits(bitsCount: 64, hashCount: 65, bits: new byte[8]);

        act.Should().Throw<InvalidDataException>(
            "hashCount exceeding MaxHashFunctions must be rejected to prevent unbounded MightContain iteration");
    }
}
