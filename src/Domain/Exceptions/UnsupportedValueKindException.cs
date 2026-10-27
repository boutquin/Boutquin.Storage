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
/// The reader observed a <c>ValueKind</c> byte outside the declared range. Codes
/// <c>0x02–0xFF</c> are reserved for future format extensions; encountering one now
/// indicates either a forward-incompatible writer or segment corruption.
/// </summary>
public sealed class UnsupportedValueKindException : Exception
{
    /// <summary>The unrecognised byte code observed in the block entry.</summary>
    public byte ObservedCode { get; }

    public UnsupportedValueKindException(byte observedCode)
        : base($"SSTable block entry carries an unsupported ValueKind byte 0x{observedCode:X2}. Codes 0x02-0xFF are reserved; the segment may be corrupt or produced by a forward-incompatible writer.")
    {
        ObservedCode = observedCode;
    }

    public UnsupportedValueKindException(byte observedCode, string message)
        : base(message)
    {
        ObservedCode = observedCode;
    }
}
