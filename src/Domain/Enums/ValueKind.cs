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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// Per-entry value-kind discriminator persisted in the v2 SSTable block-entry format.
/// </summary>
/// <remarks>
/// <para><b>Theory:</b>
/// DDIA 2e p. 130 describes key-value separation (WiscKey): large values live in a
/// dedicated value log while the SSTable holds only a fixed-width pointer. A one-byte
/// discriminator in every block entry lets the engine route per-entry based on where the
/// payload lives.</para>
/// <para><b>Persisted layout:</b>
/// One byte per block entry, immediately after the 8-byte sequence number and before the
/// value length. Encoded values <c>0x02–0xFF</c> are reserved; a reader observing any
/// unreserved code throws <c>UnsupportedValueKindException</c>.</para>
/// <para><b>Reference:</b>
/// Kleppmann, <i>Designing Data-Intensive Applications</i> 2e, p. 130, "Key-value
/// separation" (DDIA 2e p. 130).</para>
/// </remarks>
public enum ValueKind : byte
{
    /// <summary>
    /// Value bytes follow inline in the block entry. Emitted when the payload length is at
    /// or below <c>ValueLogOptions.ValueLogThresholdBytes</c>.
    /// </summary>
    Inline = 0x00,

    /// <summary>
    /// Value slot carries a 12-byte <c>(fileIndex:u32, offset:u64)</c> pointer into a
    /// companion value-log segment file. Emitted when the payload length exceeds
    /// <c>ValueLogOptions.ValueLogThresholdBytes</c>; the engine dereferences the pointer
    /// through <c>ValueLogReader</c> on the read path.
    /// </summary>
    ValueLogPointer = 0x01,
}
