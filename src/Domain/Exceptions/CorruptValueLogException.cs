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
/// The engine refused to start because a mid-file CRC mismatch was detected while scanning
/// a value-log segment during recovery. Trailing partial frames (crash mid-append) are
/// truncated silently; an error in the middle of an otherwise-intact file indicates bitrot
/// in a region that MUST remain intact, and the engine fails fast instead of silently
/// dropping reachable entries.
/// </summary>
/// <remarks>
/// <para>Distinct from <see cref="CorruptValueLogPointerException"/> — that fault surfaces
/// on a live read after recovery has completed. This exception is raised exclusively by the
/// startup recovery path.</para>
/// </remarks>
public sealed class CorruptValueLogException : Exception
{
    /// <summary>File index of the value-log segment whose scan failed.</summary>
    public uint FileIndex { get; }

    /// <summary>Byte offset of the corrupt frame's <c>length</c> field within that file.</summary>
    public ulong Offset { get; }

    public CorruptValueLogException(uint fileIndex, ulong offset, string message)
        : base(message)
    {
        FileIndex = fileIndex;
        Offset = offset;
    }

    public CorruptValueLogException(uint fileIndex, ulong offset, string message, Exception innerException)
        : base(message, innerException)
    {
        FileIndex = fileIndex;
        Offset = offset;
    }
}
