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
namespace Boutquin.Storage.BenchMark.Hashing;

using BenchmarkDotNet.Attributes;

using Infrastructure.Algorithms;
using Domain.Interfaces;

/// <summary>
/// Benchmark class for comparing hash algorithms.
/// </summary>
[MemoryDiagnoser]
public class HashAlgorithmBenchmark
{
    // Input data to be hashed (a GUID converted to a byte array)
    private static readonly byte[] Data = Guid.NewGuid().ToByteArray();

    // Instances of the hash algorithms to be benchmarked
    private readonly IHashAlgorithm fnvHasher = new Fnv1aHash();
    private readonly IHashAlgorithm xxHasher = new XxHash32();
    private readonly IHashAlgorithm murmurHasher = new Murmur3();

    /// <summary>
    /// Benchmarks the FNV-1a hash algorithm by hashing the GUID 500 times.
    /// </summary>
    /// <returns>The final computed hash value.</returns>
    [Benchmark]
    public uint Fnv1aHash()
    {
        uint hash = 0;
        for (int i = 0; i < 500; i++)
        {
            hash = fnvHasher.ComputeHash(Data);
        }
        return hash;
    }

    /// <summary>
    /// Benchmarks the xxHash algorithm by hashing the GUID 500 times.
    /// </summary>
    /// <returns>The final computed hash value.</returns>
    [Benchmark]
    public uint XxHash32()
    {
        uint hash = 0;
        for (int i = 0; i < 500; i++)
        {
            hash = xxHasher.ComputeHash(Data);
        }
        return hash;
    }

    /// <summary>
    /// Benchmarks the Murmur3 hash algorithm by hashing the GUID 500 times.
    /// </summary>
    /// <returns>The final computed hash value.</returns>
    [Benchmark]
    public uint Murmur3Hash()
    {
        uint hash = 0;
        for (int i = 0; i < 500; i++)
        {
            hash = murmurHasher.ComputeHash(Data);
        }
        return hash;
    }

}