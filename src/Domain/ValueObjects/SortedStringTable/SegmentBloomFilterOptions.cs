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
namespace Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

/// <summary>Immutable per-segment Bloom sizing, disable behavior, and custom persistence configuration.</summary>
/// <typeparam name="TKey">The physical segment key, including the date for time-series records.</typeparam>
/// <remarks>
/// Each flush or compaction creates a separate filter. Native sizing uses the actual entry count
/// unless an expected capacity is supplied. Disabling affects new writes; reopening always honors
/// the filter stored on disk. A custom factory requires a state codec; a codec alone enables decoding.
/// Options, factory and codec are borrowed; created/restored filters belong to Storage.
/// Bloom filters only reject individual point lookups and never certify an entire range absent.
/// </remarks>
public sealed record SegmentBloomFilterOptions<TKey>
{
    /// <summary>Creates validated segment filter settings; construction does not call the factory or touch disk.</summary>
    /// <param name="enabled">Whether new segments contain a filter.</param>
    /// <param name="expectedItems">Positive expected capacity, or null to size from each segment's entries.</param>
    /// <param name="falsePositiveProbability">Target probability strictly between zero and one.</param>
    /// <param name="factory">Optional factory receiving resolved capacity and probability; must return a fresh empty filter.</param>
    /// <param name="stateCodec">Codec for factory results and reopened custom state. Must have stable identity and version.</param>
    /// <exception cref="ArgumentOutOfRangeException">Capacity or probability is invalid or exceeds supported native sizing.</exception>
    /// <exception cref="ArgumentException">Factory/codec pairing or codec identity is invalid.</exception>
    public SegmentBloomFilterOptions(
        bool enabled = true,
        int? expectedItems = null,
        double falsePositiveProbability = 0.01,
        Func<int, double, IBloomFilter<TKey>>? factory = null,
        IBloomFilterStateCodec<TKey>? stateCodec = null)
    {
        if (expectedItems is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedItems));
        }
        if (!double.IsFinite(falsePositiveProbability) || falsePositiveProbability <= 0 || falsePositiveProbability >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(falsePositiveProbability));
        }
        if (factory is not null && stateCodec is null)
        {
            throw new ArgumentException("A custom factory requires a state codec.", nameof(stateCodec));
        }
        if (enabled && factory is null)
        {
            var count = expectedItems ?? 1;
            var bits = Math.Max(8, Math.Ceiling(-count * Math.Log(falsePositiveProbability) / Math.Pow(Math.Log(2), 2)));
            var hashes = Math.Max(1, Math.Round(bits / count * Math.Log(2)));
            if (bits > int.MaxValue - 7 || hashes > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(falsePositiveProbability), "Native Bloom sizing exceeds the supported bit count or 64 hash functions.");
            }
        }
        if (stateCodec is not null && (string.IsNullOrWhiteSpace(stateCodec.CodecId)
            || Encoding.UTF8.GetByteCount(stateCodec.CodecId) > 256 || stateCodec.Version < 1))
        {
            throw new ArgumentException("A codec needs a nonempty identity of at most 256 UTF-8 bytes and a positive version.", nameof(stateCodec));
        }
        Enabled = enabled;
        ExpectedItems = expectedItems;
        FalsePositiveProbability = falsePositiveProbability;
        Factory = factory;
        StateCodec = stateCodec;
    }

    /// <summary>Whether newly written segments use a Bloom filter.</summary>
    public bool Enabled { get; }
    /// <summary>Explicit capacity, or null for the actual number of segment entries.</summary>
    public int? ExpectedItems { get; }
    /// <summary>Target false-positive probability for a filter filled to its expected capacity.</summary>
    public double FalsePositiveProbability { get; }
    /// <summary>Borrowed factory, invoked once per new physical segment; never invoked on reopen.</summary>
    public Func<int, double, IBloomFilter<TKey>>? Factory { get; }
    /// <summary>Borrowed custom-state codec; null selects Storage's built-in byte-keyed Bloom implementation.</summary>
    public IBloomFilterStateCodec<TKey>? StateCodec { get; }
}
