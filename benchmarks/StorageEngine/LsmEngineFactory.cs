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
using Boutquin.Storage.Infrastructure.LsmTree;

namespace Boutquin.Storage.BenchMark;

internal static class LsmEngineFactory
{
    public static LsmStorageEngine<TKey, TValue> Build<TKey, TValue>(
        int memTableCapacity,
        string segmentFolder,
        string segmentPrefix,
        ISegmentFormat<TKey, TValue>? segmentFormat = null,
        IWriteAheadLog<TKey, TValue>? writeAheadLog = null,
        ICompactionStrategy<TKey>? compactionStrategy = null,
        ValueLogOptions? valueLog = null)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => new(new LsmStorageEngineOptions<TKey, TValue>(
            memTableCapacity, segmentFolder, segmentPrefix,
            segmentFormat ?? new SstSegmentFormat<TKey, TValue>(),
            writeAheadLog, compactionStrategy, valueLog));
}
