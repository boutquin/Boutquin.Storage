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
/// Metadata about a codec registered with the <see cref="CodecRegistry"/>. Exposes the
/// on-disk <see cref="ColumnEncodingTag"/>, the current implementation version, and whether
/// the codec is ready for use by the segment format.
/// </summary>
/// <param name="Tag">The encoding tag written into the footer for columns using this codec.</param>
/// <param name="Name">Human-readable short name used in logs and error messages.</param>
/// <param name="CurrentVersion">
/// The codec version this build ships. Incremented when the on-disk bytes for this tag change
/// shape. Readers reject <c>(tag, version)</c> pairs whose version exceeds this value.
/// </param>
/// <param name="IsImplemented">
/// <see langword="true"/> when the codec is available for write/read. <see langword="false"/>
/// when the tag is reserved but the implementation has not yet landed — i.e. a future tag added
/// to the append-only byte-space ahead of its codec. A tag that is reserved but not implemented
/// causes readers to raise <c>UnsupportedEncodingException</c> rather than decode silently-wrong
/// bytes. Every tag currently in <c>CodecRegistry</c> is implemented.
/// </param>
internal readonly record struct CodecDescriptor(
    ColumnEncodingTag Tag,
    string Name,
    byte CurrentVersion,
    bool IsImplemented);
