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
/// A v2 SSTable block failed its trailing CRC32C check. Per
/// v2.0.0 block-integrity policy, the reader discards the block and
/// propagates this exception so recovery can quarantine the surrounding segment while
/// leaving sibling blocks readable.
/// </summary>
public sealed class CorruptBlockException : Exception
{
    /// <summary>The CRC32C value recorded in the block frame.</summary>
    public uint ExpectedCrc { get; }

    /// <summary>The CRC32C value computed over the block header + payload at read time.</summary>
    public uint ActualCrc { get; }

    public CorruptBlockException(uint expectedCrc, uint actualCrc)
        : base($"SSTable block failed CRC32C verification. Expected 0x{expectedCrc:X8}, computed 0x{actualCrc:X8}.")
    {
        ExpectedCrc = expectedCrc;
        ActualCrc = actualCrc;
    }

    public CorruptBlockException(string message)
        : base(message)
    {
        ExpectedCrc = 0;
        ActualCrc = 0;
    }
}
