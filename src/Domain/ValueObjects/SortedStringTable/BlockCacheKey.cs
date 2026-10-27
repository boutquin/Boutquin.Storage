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
namespace Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

/// <summary>
/// Composite cache identity for a single SSTable block: the segment-id of the SST file that
/// owns the block plus the byte offset of the block's first byte in that file.
/// </summary>
/// <param name="SegmentId">Identifier of the open SST file that owns the block, unique among
/// every file sharing the cache for as long as the cache lives. The SST segment format issues
/// one process-wide identity per open file instance rather than reusing the file name's counter,
/// which repeats across folders and after a cleared store is reopened. The `0`-default sentinel
/// marks "not participating in caching."</param>
/// <param name="BlockOffset">Byte offset of the block's first byte in the segment file.
/// Together with <paramref name="SegmentId"/>, uniquely identifies the block in the cache.</param>
/// <remarks>
/// Sized at 16 bytes (two longs); equality is structural by <see cref="SegmentId"/> and
/// <see cref="BlockOffset"/>. Reference: DDIA 2e Ch. 4 p. 79–82 — block-cache identity is the
/// (file-id, offset) pair, identical to LevelDB / RocksDB block-cache key shape.
/// </remarks>
public readonly record struct BlockCacheKey(long SegmentId, long BlockOffset);
