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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

/// <summary>
/// Maps a <see cref="ColumnEncodingTag"/> to the <see cref="CodecDescriptor"/> describing
/// the codec responsible for that tag. Used by the columnar segment reader/writer to validate
/// tags and to produce actionable error messages when a segment references an unknown tag or
/// a future codec version.
/// </summary>
/// <remarks>
/// <para><b>Implemented codecs:</b> all six are registered with <c>IsImplemented = true</c> —
/// <see cref="BitmapRleCodec"/>, <see cref="DictionaryCodec"/>, <see cref="DeltaVlqCodec"/>,
/// <see cref="DeltaOfDeltaVlqCodec"/>, <see cref="ScaledIntegerBitPackedCodec"/>, and
/// <see cref="GorillaXorCodec"/> (mapped from <see cref="ColumnEncodingTag.GorillaXor"/>).
/// <see cref="ColumnEncodingTag.PlainRawBytes"/> is the seventh registered entry, a raw passthrough.</para>
/// <para><b>Why reserve unimplemented tags:</b> the encoding-tag byte-space is append-only.
/// Were a future tag reserved here ahead of its codec (with <c>IsImplemented = false</c>), that
/// reservation would commit the library to never reuse the value for a different codec — the
/// contract that makes segment forward-compat rejection safe. No tag is currently in that
/// reserved-but-unimplemented state.</para>
/// </remarks>
internal static class CodecRegistry
{
    private static readonly IReadOnlyDictionary<ColumnEncodingTag, CodecDescriptor> s_descriptors =
        new Dictionary<ColumnEncodingTag, CodecDescriptor>
        {
            [ColumnEncodingTag.PlainRawBytes] = new(
                ColumnEncodingTag.PlainRawBytes,
                "PlainRawBytes",
                CurrentVersion: 0,
                IsImplemented: true),
            [ColumnEncodingTag.BitmapRle] = new(
                ColumnEncodingTag.BitmapRle,
                "BitmapRle",
                CurrentVersion: BitmapRleCodec.Version,
                IsImplemented: true),
            [ColumnEncodingTag.Dictionary] = new(
                ColumnEncodingTag.Dictionary,
                "Dictionary",
                CurrentVersion: DictionaryCodec.Version,
                IsImplemented: true),
            [ColumnEncodingTag.DeltaVlq] = new(
                ColumnEncodingTag.DeltaVlq,
                "DeltaVlq",
                CurrentVersion: DeltaVlqCodec.Version,
                IsImplemented: true),
            [ColumnEncodingTag.DeltaOfDeltaVlq] = new(
                ColumnEncodingTag.DeltaOfDeltaVlq,
                "DeltaOfDeltaVlq",
                CurrentVersion: DeltaOfDeltaVlqCodec.Version,
                IsImplemented: true),
            [ColumnEncodingTag.ScaledIntegerBitPacked] = new(
                ColumnEncodingTag.ScaledIntegerBitPacked,
                "ScaledIntegerBitPacked",
                CurrentVersion: ScaledIntegerBitPackedCodec.Version,
                IsImplemented: true),
            [ColumnEncodingTag.GorillaXor] = new(
                ColumnEncodingTag.GorillaXor,
                "GorillaXor",
                CurrentVersion: GorillaXorCodec.Version,
                IsImplemented: true),
        };

    /// <summary>
    /// Attempts to look up a codec descriptor for the supplied tag. Returns <see langword="true"/>
    /// with the descriptor on hit; <see langword="false"/> with a default descriptor on miss.
    /// Callers treat "miss" as an invalid on-disk tag.
    /// </summary>
    public static bool TryGet(ColumnEncodingTag tag, out CodecDescriptor descriptor)
        => s_descriptors.TryGetValue(tag, out descriptor);

    /// <summary>Returns every registered tag in declaration order.</summary>
    public static IReadOnlyCollection<CodecDescriptor> All => s_descriptors.Values.ToArray();
}
