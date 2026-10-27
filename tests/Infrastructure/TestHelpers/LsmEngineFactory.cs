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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.TestHelpers;

/// <summary>
/// Test-only factory that mirrors the pre-WiscKey-SSTable positional <c>LsmStorageEngine</c>
/// constructor shape. Wraps the canonical
/// <see cref="LsmStorageEngineOptions{TKey, TValue}"/> ctor so test call sites can migrate to
/// the single-ctor rule without rewriting the 90 positional-parameter sites. Not a back-compat
/// shim on production code — lives in <c>tests/</c> scaffolding only.
/// </summary>
internal static class LsmEngineFactory
{
    public static LsmStorageEngine<TKey, TValue> Build<TKey, TValue>(
        int memTableCapacity,
        string segmentFolder,
        string segmentPrefix,
        ISegmentFormat<TKey, TValue>? segmentFormat = null,
        IWriteAheadLog<TKey, TValue>? writeAheadLog = null,
        ICompactionStrategy<TKey>? compactionStrategy = null,
        ValueLogOptions? valueLog = null,
        int maxImmutableMemtables = 2)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => new(new LsmStorageEngineOptions<TKey, TValue>(
            memTableCapacity,
            segmentFolder,
            segmentPrefix,
            segmentFormat ?? new SstSegmentFormat<TKey, TValue>(),
            writeAheadLog,
            compactionStrategy,
            valueLog,
            BlockCache: null,
            MaxImmutableMemtables: maxImmutableMemtables));

    // Recovery path: replays WAL and opens value log asynchronously.
    // Use this when re-opening an engine over existing on-disk state.
    public static Task<LsmStorageEngine<TKey, TValue>> BuildAsync<TKey, TValue>(
        int memTableCapacity,
        string segmentFolder,
        string segmentPrefix,
        ISegmentFormat<TKey, TValue>? segmentFormat = null,
        IWriteAheadLog<TKey, TValue>? writeAheadLog = null,
        ICompactionStrategy<TKey>? compactionStrategy = null,
        ValueLogOptions? valueLog = null,
        int maxImmutableMemtables = 2,
        CancellationToken cancellationToken = default)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => LsmStorageEngine<TKey, TValue>.OpenAsync(
            new LsmStorageEngineOptions<TKey, TValue>(
                memTableCapacity,
                segmentFolder,
                segmentPrefix,
                segmentFormat ?? new SstSegmentFormat<TKey, TValue>(),
                writeAheadLog,
                compactionStrategy,
                valueLog,
                BlockCache: null,
                MaxImmutableMemtables: maxImmutableMemtables),
            cancellationToken);
}
