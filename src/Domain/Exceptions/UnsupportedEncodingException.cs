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
namespace Boutquin.Storage.Domain.Exceptions;

/// <summary>
/// A columnar segment references a per-column encoding tag the reader does not know, or a
/// codec version newer than the reader supports. Distinguished from
/// <see cref="UnsupportedFormatVersionException"/>, which fires on the <em>segment</em>
/// format version; this one fires on a per-column codec the segment format could otherwise
/// parse.
/// </summary>
/// <remarks>
/// <para>Partial forward-compat is explicitly refused: if the reader cannot decode every
/// column it was asked to project, it refuses the whole segment rather than risk silently
/// dropping data from the unreadable columns.</para>
/// <para>Message carries <c>(column, codec name, codec version, reader max supported version)</c>
/// so operators can correlate the failure with a writer version upgrade.</para>
/// </remarks>
public sealed class UnsupportedEncodingException(
    string columnName,
    byte encodingTag,
    byte codecVersion,
    string message) : Exception(message)
{
    /// <summary>The column whose encoding could not be decoded.</summary>
    public string ColumnName { get; } = columnName;

    /// <summary>The encoding tag byte as written in the footer.</summary>
    public byte EncodingTag { get; } = encodingTag;

    /// <summary>The codec version byte the segment declared for this column.</summary>
    public byte CodecVersion { get; } = codecVersion;
}
