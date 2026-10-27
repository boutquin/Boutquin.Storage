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
namespace Boutquin.Storage.Domain.Interfaces.Compression;

/// <summary>
/// Resolves an <see cref="ICompressionCodec"/> from the stable one-byte CodecId persisted
/// in the SSTable footer. The default implementation ships LZ4, GZip, Brotli, and the
/// passthrough None codec; consumers register custom codecs before opening an SSTable
/// produced with them.
/// </summary>
/// <remarks>
/// Registering a codec whose <see cref="ICompressionCodec.CodecId"/> collides with an
/// existing registration throws — CodecIds are the single source of truth for wire-level
/// codec identity and must never be rebound.
/// </remarks>
public interface ICompressionCodecRegistry
{
    /// <summary>
    /// Registers <paramref name="codec"/>. Throws <see cref="ArgumentException"/> if a
    /// codec with the same <see cref="ICompressionCodec.CodecId"/> is already registered.
    /// </summary>
    void Register(ICompressionCodec codec);

    /// <summary>
    /// Returns the codec registered against <paramref name="codecId"/>, or <c>null</c> if
    /// no codec is registered for that byte. Callers surface the unknown CodecId via
    /// <c>UnsupportedCompressionCodecException</c>.
    /// </summary>
    ICompressionCodec? Resolve(byte codecId);

    /// <summary>
    /// Snapshot of every registered codec at call time — order is unspecified.
    /// </summary>
    IReadOnlyCollection<ICompressionCodec> Registered { get; }
}
