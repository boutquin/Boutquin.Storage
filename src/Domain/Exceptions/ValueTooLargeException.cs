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
/// A <c>SetAsync</c> call carried a value, longer than <c>ValueLogOptions.ValueLogThresholdBytes</c>
/// and so routed to the value log, whose length exceeded <c>ValueLogOptions.MaxEntryBytes</c>
/// (64 MiB by default). The writer rejects the append before any I/O so the value log never
/// contains an oversize entry.
/// </summary>
/// <remarks>
/// <para>A value at or below <c>ValueLogThresholdBytes</c> stays inline and never reaches the value
/// log, so it is not subject to the cap and cannot raise this exception.</para>
/// <para>The cap protects against pathological single-entry values that would bloat a
/// segment file beyond <c>ValueLogOptions.SegmentMaxBytes</c> in one write and defeat the
/// rotation policy. Operators tuning for larger payloads raise <c>MaxEntryBytes</c>
/// alongside <c>SegmentMaxBytes</c>.</para>
/// </remarks>
public sealed class ValueTooLargeException(long valueLength, long maxEntryBytes) : Exception($"Value length {valueLength:N0} bytes exceeds MaxEntryBytes {maxEntryBytes:N0}.")
{
    /// <summary>The observed payload length, in bytes.</summary>
    public long ValueLength { get; } = valueLength;

    /// <summary>The configured <c>MaxEntryBytes</c> cap.</summary>
    public long MaxEntryBytes { get; } = maxEntryBytes;
}
