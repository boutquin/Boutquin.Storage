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
namespace Boutquin.Storage.Infrastructure.Algorithms;

/// <summary>
/// Table-based CRC32C (Castagnoli) — single shared implementation used by SSTable block
/// framing (Phase C2), columnar footer / row-group CRCs (Phase E3), and segment-file CRC
/// (Phase A4). Castagnoli polynomial <c>0x1EDC6F41</c> reversed (<c>0x82F63B78</c>); this
/// is distinct from the Ethernet/zip CRC32 polynomial used by the WAL.
/// </summary>
/// <remarks>
/// <para>Standard reflected-input / reflected-output scheme; lookup table built once at
/// type initialisation (~1 KiB heap). <c>System.IO.Hashing</c> v9 ships <c>Crc32</c>
/// (Ethernet) but not <c>Crc32C</c>, so we roll our own.</para>
/// </remarks>
internal static class Crc32C
{
    private const uint Polynomial = 0x82F63B78u; // Castagnoli reversed
    private static readonly uint[] s_table = BuildTable();

    public static uint HashToUInt32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < data.Length; i++)
        {
            crc = (crc >> 8) ^ s_table[(crc ^ data[i]) & 0xFF];
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// Incremental hash computation for callers that must feed several spans without
    /// materialising a combined buffer (e.g., framed blocks with a separate header and
    /// payload).
    /// </summary>
    public static uint HashToUInt32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < a.Length; i++)
        {
            crc = (crc >> 8) ^ s_table[(crc ^ a[i]) & 0xFF];
        }
        for (var i = 0; i < b.Length; i++)
        {
            crc = (crc >> 8) ^ s_table[(crc ^ b[i]) & 0xFF];
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// Starting value for incremental CRC state. Pass as <c>state</c> to
    /// <see cref="Append"/> before the first chunk.
    /// </summary>
    public const uint InitialState = 0xFFFFFFFFu;

    /// <summary>
    /// Mixes <paramref name="data"/> into an in-progress CRC state. Call once per
    /// chunk; each call is O(data.Length). Finish with <see cref="Finalize"/>.
    /// </summary>
    public static void Append(ReadOnlySpan<byte> data, ref uint state)
    {
        for (var i = 0; i < data.Length; i++)
        {
            state = (state >> 8) ^ s_table[(state ^ data[i]) & 0xFF];
        }
    }

    /// <summary>Produces the final CRC value from a completed incremental state.</summary>
    public static uint Finalize(uint state) => state ^ 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ Polynomial : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
