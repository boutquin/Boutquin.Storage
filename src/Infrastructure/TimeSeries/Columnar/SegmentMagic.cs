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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// On-disk layout constants for the Boutquin columnar time-series segment format.
/// Intended for use by readers, writers, and tests that hand-craft corrupt streams.
/// </summary>
/// <remarks>
/// <para>The magic bytes <c>BSCT</c> — <b>B</b>outquin <b>S</b>torage <b>C</b>olumnar
/// <b>T</b>ime-series — appear twice in every segment: once at byte 0 (header) and once
/// in the last 4 bytes of the file (trailer). The trailer marker is validated before any
/// other byte of the file is trusted, so a non-Boutquin file raises
/// <c>InvalidSegmentException</c> without the reader ever seeking into arbitrary offsets.</para>
/// <para>Format version starts at <see cref="CurrentFormatVersion"/> = 1 (Phase 2 plain baseline).
/// Monotonically increasing; readers reject unknown versions with
/// <c>UnsupportedFormatVersionException</c>.</para>
/// </remarks>
internal static class SegmentMagic
{
    /// <summary>Header / trailer magic: 4 bytes <c>'B','S','C','T'</c> (ASCII).</summary>
    public static ReadOnlySpan<byte> Bytes => [(byte)'B', (byte)'S', (byte)'C', (byte)'T'];

    /// <summary>Magic as a 32-bit little-endian integer for single-word reads/writes.</summary>
    public const uint AsUInt32 = 0x54_43_53_42u; // 'T','C','S','B' read LE == 'B','S','C','T'

    /// <summary>The current segment-format version this build produces.</summary>
    public const uint CurrentFormatVersion = 1u;

    /// <summary>Size of the fixed segment header in bytes (magic + version).</summary>
    public const int HeaderSize = 8;

    /// <summary>Size of the fixed segment trailer in bytes (footer length + trailer magic).</summary>
    public const int TrailerSize = 8;

    /// <summary>Size of the footer CRC32C suffix in bytes.</summary>
    public const int FooterCrcSize = 4;
}
