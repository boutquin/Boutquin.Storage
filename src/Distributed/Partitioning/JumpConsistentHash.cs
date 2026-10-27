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
namespace Boutquin.Storage.Distributed.Partitioning;

/// <summary>
/// Maps a 64-bit key hash to a sequential bucket using Jump Consistent Hash.
/// </summary>
/// <remarks>
/// This is the integer algorithm published by Lamping and Veach in
/// <see href="https://arxiv.org/abs/1406.2294">A Fast, Minimal Memory, Consistent Hash Algorithm</see>.
/// It uses constant memory and expected O(log n) time. Buckets must be numbered contiguously from
/// zero; arbitrary node removal requires a separate stable shard-assignment layer.
/// </remarks>
public static class JumpConsistentHash
{
    /// <summary>Returns a bucket in the half-open range <c>[0, bucketCount)</c>.</summary>
    /// <param name="keyHash">A stable 64-bit hash of the key.</param>
    /// <param name="bucketCount">The positive number of sequential buckets.</param>
    /// <returns>The selected bucket.</returns>
    public static int GetBucket(ulong keyHash, int bucketCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);

        long previous = -1;
        long candidate = 0;
        while (candidate < bucketCount)
        {
            previous = candidate;
            keyHash = unchecked((keyHash * 2_862_933_555_777_941_757UL) + 1);
            candidate = (long)((previous + 1) * ((double)(1L << 31) / ((keyHash >> 33) + 1)));
        }

        return (int)previous;
    }
}
