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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>Encodes and restores a custom segment Bloom filter without replaying stored keys.</summary>
/// <typeparam name="TKey">The same key representation used by the filter factory and segment.</typeparam>
/// <remarks>
/// The identity and version must identify a deterministic state format and hashing/key semantics.
/// Implementations are borrowed, must support concurrent calls, and must validate untrusted state.
/// A restored filter must preserve membership exactly and support concurrent reads; it is owned
/// and disposed by the segment. Filter disposal must be idempotent and nonthrowing.
/// Never reconstruct state by reading segment data or replaying Add.
/// </remarks>
public interface IBloomFilterStateCodec<TKey>
{
    /// <summary>Stable, nonempty UTF-8 codec identity (at most 256 encoded bytes).</summary>
    string CodecId { get; }

    /// <summary>Positive state-format version; a mismatched version fails the segment open.</summary>
    int Version { get; }

    /// <summary>Captures all state required for membership checks, including hash seeds.</summary>
    /// <param name="filter">Borrowed filter created by the configured factory.</param>
    /// <returns>A self-contained state payload, at most 16 MiB.</returns>
    byte[] Serialize(IBloomFilter<TKey> filter);

    /// <summary>Restores a fresh filter directly from persisted state.</summary>
    /// <param name="state">Bounded, checksum-verified bytes; the codec must still validate its own structure.</param>
    /// <returns>A new filter whose ownership transfers to the segment reader.</returns>
    /// <exception cref="InvalidDataException">The state cannot be decoded safely.</exception>
    IBloomFilter<TKey> Deserialize(ReadOnlyMemory<byte> state);
}
