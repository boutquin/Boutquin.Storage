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
using Boutquin.Storage.Distributed.Partitioning;

namespace Boutquin.Storage.Distributed.Sharding;

/// <summary>Uses deterministic salt suffixes to spread a logical hot key.</summary>
public sealed class SaltedHotKeyStrategy<TKey> : IHotKeyMitigator<TKey>
{
    /// <inheritdoc />
    public string GetWriteKey(TKey key, int saltCount, ulong requestHash)
    {
        var readKeys = GetReadKeys(key, saltCount);
        return readKeys[JumpConsistentHash.GetBucket(requestHash, saltCount)];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetReadKeys(TKey key, int saltCount)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(saltCount, 1);

        // Canonical text shared with HashPartitioner: equal keys must derive the same salt set, or a
        // read built from one spelling of a key misses every salt the write used.
        var text = CanonicalKeyText.Of(key);
        var encodedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return Enumerable.Range(0, saltCount)
            .Select(salt => $"{encodedKey}:salt-{salt}")
            .ToArray();
    }
}
