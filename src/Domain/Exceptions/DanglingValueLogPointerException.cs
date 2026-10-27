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
/// Startup WAL replay observed a durable <c>ValueLogPointer</c> whose referenced value-log
/// entry is missing or truncated. Violates the value-log durability ordering invariant — the
/// WAL frame should never outlive its target entry — so the engine fails fast.
/// </summary>
/// <remarks>
/// <para>The dual-fsync protocol fsyncs the value-log segment BEFORE the WAL frame
/// that points into it. A surviving pointer with a missing target therefore indicates
/// external tampering (file deletion, disk replacement, snapshot restore from an
/// inconsistent pair) rather than a crash this spec's design can tolerate.</para>
/// </remarks>
public sealed class DanglingValueLogPointerException : Exception
{
    /// <summary>File index the dangling pointer referenced.</summary>
    public uint FileIndex { get; }

    /// <summary>Offset within that file.</summary>
    public ulong Offset { get; }

    /// <summary>Seqno of the WAL frame carrying the dangling pointer.</summary>
    public ulong SequenceNumber { get; }

    public DanglingValueLogPointerException(uint fileIndex, ulong offset, ulong sequenceNumber, string message)
        : base(message)
    {
        FileIndex = fileIndex;
        Offset = offset;
        SequenceNumber = sequenceNumber;
    }

    public DanglingValueLogPointerException(uint fileIndex, ulong offset, ulong sequenceNumber, string message, Exception innerException)
        : base(message, innerException)
    {
        FileIndex = fileIndex;
        Offset = offset;
        SequenceNumber = sequenceNumber;
    }
}
