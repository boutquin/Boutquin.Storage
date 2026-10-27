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
/// The reader encountered a persisted record or segment that declares a format version it
/// does not understand. Covers both the columnar time-series path (versioned segments) and
/// the Phase 2 tagged-wire path (versioned per-record schema-format byte at tag 0). The
/// message names the observed version; the surrounding context names the max supported.
/// </summary>
public sealed class UnsupportedFormatVersionException : Exception
{
    /// <summary>The format version declared in the segment or record.</summary>
    public uint ObservedVersion { get; }

    /// <summary>The maximum format version this reader supports (0 when not applicable).</summary>
    public uint MaxSupportedVersion { get; }

    public UnsupportedFormatVersionException(uint observedVersion, uint maxSupportedVersion)
        : base($"Columnar segment declares format version {observedVersion}, but this reader supports up to version {maxSupportedVersion}. Upgrade Boutquin.Storage to a version that supports format {observedVersion}.")
    {
        ObservedVersion = observedVersion;
        MaxSupportedVersion = maxSupportedVersion;
    }

    /// <summary>
    /// Construct with a caller-supplied message. Used by the Phase 2 tagged-wire reader when
    /// the per-record schema-format version byte at tag 0 is absent (1.x-written data) or
    /// carries an unrecognised value. 1.x-written data is not always recognised this way — it can
    /// also surface as <see cref="System.IO.InvalidDataException"/> or <see cref="System.IO.EndOfStreamException"/>, or
    /// be accepted when its leading bytes happen to look like a tag-0 prefix.
    /// </summary>
    public UnsupportedFormatVersionException(uint observedVersion, string message)
        : base(message)
    {
        ObservedVersion = observedVersion;
        MaxSupportedVersion = 0;
    }

    /// <summary>
    /// Construct with a caller-supplied message and the underlying I/O failure — used when a
    /// framed read fails partway (truncated record), so the corruption surfaces as the
    /// domain's fail-closed type while the stream-level cause stays observable.
    /// </summary>
    public UnsupportedFormatVersionException(uint observedVersion, string message, Exception innerException)
        : base(message, innerException)
    {
        ObservedVersion = observedVersion;
        MaxSupportedVersion = 0;
    }
}
