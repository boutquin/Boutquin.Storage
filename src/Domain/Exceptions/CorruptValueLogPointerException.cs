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
/// A hot read dereferenced a <c>ValueLogPointer</c> whose entry-frame CRC32C failed to
/// validate. Distinct from <see cref="CorruptValueLogException"/>: this fault surfaces on a
/// live read, the SSTable block entry itself is intact, and the containing value-log file's
/// earlier frames may still be readable.
/// </summary>
/// <remarks>
/// <para>Raised by the value-log reader when a random-access <c>RandomAccess.Read</c> of the
/// entry frame yields bytes whose computed CRC32C does not match the trailer. The engine
/// does not self-quarantine the SSTable entry — doing so would mutate snapshot-visible state
/// — so the exception propagates to the caller and the operator runbook in
/// <c>docs/wisckey-value-log.md</c> prescribes recovery.</para>
/// </remarks>
public sealed class CorruptValueLogPointerException : Exception
{
    /// <summary>File index of the value-log segment that failed to validate.</summary>
    public uint FileIndex { get; }

    /// <summary>Byte offset of the entry frame's <c>length</c> field within that file.</summary>
    public ulong Offset { get; }

    /// <summary>The CRC32C recorded in the entry frame trailer.</summary>
    public uint ExpectedCrc { get; }

    /// <summary>The CRC32C computed from the observed frame bytes.</summary>
    public uint ActualCrc { get; }

    public CorruptValueLogPointerException(uint fileIndex, ulong offset, uint expectedCrc, uint actualCrc)
        : base($"Value-log entry at (fileIndex={fileIndex}, offset={offset}) failed CRC check (expected=0x{expectedCrc:X8}, actual=0x{actualCrc:X8}).")
    {
        FileIndex = fileIndex;
        Offset = offset;
        ExpectedCrc = expectedCrc;
        ActualCrc = actualCrc;
    }

    public CorruptValueLogPointerException(uint fileIndex, ulong offset, string message)
        : base(message)
    {
        FileIndex = fileIndex;
        Offset = offset;
    }
}
