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
/// The SSTable reader encountered a CodecId in the footer that no registered codec
/// handles. The writer used a codec the reader doesn't know about; register the matching
/// implementation before opening the SSTable, or accept that the segment is unreadable
/// by this build.
/// </summary>
public sealed class UnsupportedCompressionCodecException : Exception
{
    /// <summary>The unrecognised CodecId byte observed in the SSTable footer.</summary>
    public byte CodecId { get; }

    public UnsupportedCompressionCodecException(byte codecId)
        : base($"SSTable footer declares compression codec 0x{codecId:X2}, but no codec is registered for that ID. Register the matching ICompressionCodec before opening this SSTable.")
    {
        CodecId = codecId;
    }

    public UnsupportedCompressionCodecException(byte codecId, string message)
        : base(message)
    {
        CodecId = codecId;
    }
}
