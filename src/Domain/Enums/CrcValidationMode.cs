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
/// Controls when the value-log reader verifies the CRC32C trailer of a frame.
/// </summary>
/// <remarks>
/// <para><b>Always</b> (default) — every <c>ValueLogReader.Read</c>
/// call re-reads and validates the CRC. Use this on commodity disks or any environment where
/// silent-corruption risk is non-negligible.</para>
/// <para><b>Never</b> — CRC bytes are skipped entirely. Maximises throughput on trusted NVMe
/// media (e.g., a read-replica in a controlled data-centre environment) at the cost of silent
/// corruption going undetected. Caller assumes responsibility for data integrity.</para>
/// </remarks>
public enum CrcValidationMode : byte
{
    /// <summary>Validate CRC on every read (default).</summary>
    Always = 0,

    /// <summary>Never validate CRC. Caller accepts silent-corruption risk.</summary>
    Never = 1,
}
