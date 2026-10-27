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

/// <summary>A segment requires an unavailable custom Bloom state codec; supply the matching codec and reopen.</summary>
/// <remarks>This is a configuration failure, not corruption. Recovery must not quarantine valid data for this failure.</remarks>
public sealed class UnsupportedBloomFilterCodecException : Exception
{
    /// <summary>Creates the failure for a missing codec identity and version.</summary>
    /// <param name="codecId">Persisted codec identity.</param>
    /// <param name="version">Persisted state version.</param>
    public UnsupportedBloomFilterCodecException(string codecId, int version)
        : base($"Segment requires Bloom codec '{codecId}' version {version}. Supply the matching state codec before reopening.")
    {
        CodecId = codecId;
        Version = version;
    }

    /// <summary>Persisted codec identity required to read the filter.</summary>
    public string CodecId { get; }
    /// <summary>Persisted state-format version required to read the filter.</summary>
    public int Version { get; }
}
