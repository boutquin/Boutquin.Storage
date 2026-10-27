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
/// A columnar time-series segment failed integrity validation — typically a CRC32C mismatch
/// in the footer. Raised by <c>IColumnarSegmentFormat&lt;TRecord&gt;</c> readers when the
/// on-disk bytes cannot be trusted for a safe decode. Partial reads are never permitted;
/// recovery is the LSM segment-replacement contract's responsibility.
/// </summary>
/// <remarks>
/// Carries the byte offset where the integrity check failed so operators can correlate
/// corruption with disk sectors, compaction events, or copy failures.
/// </remarks>
public sealed class CorruptSegmentException : Exception
{
    /// <summary>The byte offset in the segment where corruption was detected.</summary>
    public long Offset { get; }

    public CorruptSegmentException(string message, long offset)
        : base(message)
    {
        Offset = offset;
    }

    public CorruptSegmentException(string message, long offset, Exception innerException)
        : base(message, innerException)
    {
        Offset = offset;
    }
}
