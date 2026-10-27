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
namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// Low 3 bits of the tag-and-wire-type varint. Carries enough information for a reader
/// that doesn't recognize a tag to skip the payload without knowing its semantic type.
/// Values mirror the Protobuf family.
/// </summary>
public enum WireType : byte
{
    /// <summary>ULEB128 payload. Skip by reading bytes until the continuation bit clears.</summary>
    Varint = 0,

    /// <summary>2-byte fixed-width payload.</summary>
    Fixed16 = 1,

    /// <summary>4-byte fixed-width payload.</summary>
    Fixed32 = 2,

    /// <summary>8-byte fixed-width payload.</summary>
    Fixed64 = 3,

    /// <summary>Varint byte-count prefix followed by N payload bytes.</summary>
    LengthPrefixed = 4,

    /// <summary>16-byte fixed-width payload (Int128/UInt128/Guid/decimal canonical encoding).</summary>
    Fixed128 = 5,
}
