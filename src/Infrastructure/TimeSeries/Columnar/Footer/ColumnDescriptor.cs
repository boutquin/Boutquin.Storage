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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

/// <summary>
/// Footer-level metadata for one column: its name, on-disk byte span, encoding tag, codec
/// version, and CLR field type. Describes enough for a reader to seek into the segment and
/// decode the column without reflecting on the record type.
/// </summary>
/// <param name="Name">
/// The column's name as emitted by the record's property. Matched case-sensitively against
/// <c>ColumnProjection.FieldNames</c>.
/// </param>
/// <param name="ByteOffset">
/// Absolute byte offset in the segment where this column's data starts. The span occupies
/// the following <paramref name="ByteLength"/> bytes contiguously.
/// </param>
/// <param name="ByteLength">Length of the column's on-disk data in bytes.</param>
/// <param name="EncodingTag">
/// Which codec wrote this column's bytes. Matches the <see cref="ColumnEncodingTag"/> value
/// stored in the footer; readers dispatch to the corresponding codec on decode.
/// </param>
/// <param name="CodecVersion">
/// Monotonic per-codec version. Allows a codec to evolve its on-disk representation; readers
/// reject unknown (codec, version) pairs.
/// </param>
/// <param name="FieldType">The CLR type code (see <see cref="ColumnFieldTypeCode"/>).</param>
internal readonly record struct ColumnDescriptor(
    string Name,
    long ByteOffset,
    long ByteLength,
    ColumnEncodingTag EncodingTag,
    byte CodecVersion,
    ColumnFieldTypeCode FieldType);
