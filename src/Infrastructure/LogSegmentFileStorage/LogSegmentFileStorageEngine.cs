// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Boutquin.Storage.Domain.Interfaces;
using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.LogSegmentFileStorage
{
    /// <summary>
    /// Manages log segment files for an append-only storage engine.
    /// </summary>
    /// <typeparam name="TKey">The type of the keys in the store.</typeparam>
    /// <typeparam name="TValue">The type of the values in the store.</typeparam>
    public class LogSegmentFileStorageEngine<TKey, TValue> : ILogSegmentFileStorageEngine<TKey, TValue>
        where TKey : ISerializable<TKey>, IComparable<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        private readonly LinkedList<ILogSegmentFile<TKey, TValue>> _segments;
        private readonly string _folder;
        private readonly string _prefix;
        private readonly ICompactableBulkStorageEngine<TKey, TValue> _storageEngine;
        private readonly long _maxSegmentSize;
        private ILogSegmentFile<TKey, TValue> _currentSegment;

        /// <summary>
        /// Initializes a new instance of the <see cref="LogSegmentFileStorageEngine{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="folder">The folder where segment files are stored.</param>
        /// <param name="prefix">The prefix for segment file names.</param>
        /// <param name="storageEngine">The storage engine for handling entries and compaction.</param>
        /// <param name="maxSegmentSize">The maximum size of each segment file in bytes.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="folder"/>, <paramref name="prefix"/>, or <paramref name="storageEngine"/> is null.</exception>
        public LogSegmentFileStorageEngine(
            string folder,
            string prefix,
            ICompactableBulkStorageEngine<TKey, TValue> storageEngine,
            long maxSegmentSize)
        {
            _segments = new LinkedList<ILogSegmentFile<TKey, TValue>>();
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
            _storageEngine = storageEngine ?? throw new ArgumentNullException(nameof(storageEngine));
            _maxSegmentSize = maxSegmentSize;

            // Initialize the first segment
            _currentSegment = CreateNewSegment();
            _segments.AddLast(_currentSegment);
        }

        private string GenerateSegmentFilePath()
        {
            return Path.Combine(_folder, $"{_prefix}_{Guid.NewGuid()}.log");
        }

        private ILogSegmentFile<TKey, TValue> CreateNewSegment()
        {
            var segmentFilePath = GenerateSegmentFilePath();
            var storageFile = new StorageFile(segmentFilePath);
            storageFile.Create(FileExistenceHandling.Overwrite);
            return new LogSegmentFile<TKey, TValue>(storageFile, _storageEngine, _maxSegmentSize);
        }

        /// <inheritdoc/>
        public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
        {
            if (_currentSegment.SegmentSize > _maxSegmentSize)
            {
                _currentSegment = CreateNewSegment();
                _segments.AddLast(_currentSegment);
            }
            await _currentSegment.SetAsync(key, value, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
        {
            foreach (var segment in _segments.Reverse())
            {
                var (value, found) = await segment.TryGetValueAsync(key, cancellationToken);
                if (found)
                {
                    return (value, true);
                }
            }
            return (default, false);
        }

        /// <inheritdoc/>
        public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
        {
            foreach (var segment in _segments.Reverse())
            {
                if (await segment.ContainsKeyAsync(key, cancellationToken))
                {
                    return true;
                }
            }
            return false;
        }

        /// <inheritdoc/>
        public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
        {
            foreach (var segment in _segments)
            {
                await segment.RemoveAsync(key, cancellationToken);
            }
        }

        /// <inheritdoc/>
        public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
        {
            Guard.AgainstEmptyOrNullEnumerable(() => items);
            cancellationToken.ThrowIfCancellationRequested();

            var itemsList = items.ToList();
            foreach (var segment in _segments)
            {
                if (_currentSegment.SegmentSize > _maxSegmentSize)
                {
                    _currentSegment = CreateNewSegment();
                    _segments.AddLast(_currentSegment);
                }
                await segment.SetBulkAsync(itemsList, cancellationToken);
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
        {
            var allItems = new List<(TKey Key, TValue Value)>();
            foreach (var segment in _segments)
            {
                var items = await segment.GetAllItemsAsync(cancellationToken);
                allItems.AddRange(items);
            }
            return allItems;
        }

        /// <inheritdoc/>
        public async Task ClearAsync(CancellationToken cancellationToken = default)
        {
            foreach (var segment in _segments)
            {
                await segment.ClearAsync(cancellationToken);
            }
            _segments.Clear();
            _currentSegment = CreateNewSegment();
            _segments.AddLast(_currentSegment);
        }

        /// <inheritdoc/>
        public async Task CompactAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Step 1: Compact each segment
            foreach (var segment in _segments)
            {
                await segment.CompactAsync(cancellationToken);
            }

            // Step 2: Merge small segments
            await MergeSmallSegmentsAsync(cancellationToken);
        }

        private async Task MergeSmallSegmentsAsync(CancellationToken cancellationToken)
        {
            var smallSegments = _segments.Where(s => s.SegmentSize < _maxSegmentSize).ToList();
            var mergedSegments = new LinkedList<ILogSegmentFile<TKey, TValue>>();
            var currentMergedSegment = CreateNewSegment();
            mergedSegments.AddLast(currentMergedSegment);

            foreach (var segment in smallSegments)
            {
                var segmentItems = await segment.GetAllItemsAsync(cancellationToken);

                foreach (var item in segmentItems)
                {
                    await SetAsync(item.Key, item.Value, cancellationToken);
                }

                await segment.ClearAsync(cancellationToken);
            }
        }
    }
}
