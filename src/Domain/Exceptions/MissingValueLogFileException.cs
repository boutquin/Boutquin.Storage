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
/// A value-log file named by a live manifest entry is absent on disk. Typically indicates
/// external deletion between manifest writes — if the manifest had already tombstoned the
/// file and fsync'd the tombstone, the missing file would be expected and ignored.
/// </summary>
/// <remarks>
/// <para>Startup fails fast rather than silently
/// losing every pointer into the missing file. The operator runbook covers the valid
/// responses (restore the file, edit the manifest, or recover from backup).</para>
/// </remarks>
public sealed class MissingValueLogFileException(uint fileIndex, string message) : Exception(message)
{
    /// <summary>File index the manifest names but is missing on disk.</summary>
    public uint FileIndex { get; } = fileIndex;
}
