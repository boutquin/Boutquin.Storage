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
/// A columnar time-series segment is shorter than its own metadata implies — either the
/// trailer reports a footer length that overruns the file, or a row-group index entry points
/// past end-of-stream. Distinguished from <see cref="CorruptSegmentException"/> so operators
/// can tell a mid-flush crash from bit rot.
/// </summary>
public sealed class TruncatedSegmentException(string message, long fileLength, long requiredOffset) : Exception(message)
{
    /// <summary>The observed file length in bytes, or <c>-1</c> if unavailable.</summary>
    public long FileLength { get; } = fileLength;

    /// <summary>The byte offset the reader needed to reach but could not.</summary>
    public long RequiredOffset { get; } = requiredOffset;
}
