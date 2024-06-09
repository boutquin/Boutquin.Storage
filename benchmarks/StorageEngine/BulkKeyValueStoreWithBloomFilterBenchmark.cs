﻿// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Benchmark class for testing the BulkKeyValueStoreWithBloomFilter implementation using AppendOnlyFileStorageEngineWithIndex as the inner engine.
/// </summary>
/// <remarks>
/// This class is not sealed because sealing it causes the benchmark to error out.
/// BenchmarkDotNet requires the benchmark classes to be inheritable to correctly
/// execute and gather results. Sealing this class results in runtime errors during 
/// the benchmarking process as BenchmarkDotNet attempts to create proxy subclasses 
/// for executing benchmarks. These proxies facilitate various operations such as 
/// instrumentation, profiling, and more. Hence, to ensure smooth execution of the 
/// benchmarks and to adhere to the requirements of the BenchmarkDotNet framework, 
/// this class must remain unsealed. 
/// </remarks>
public class BulkKeyValueStoreWithBloomFilterBenchmark : StorageEngineBenchmark<SerializableWrapper<int>, SerializableWrapper<string>>
{
    /// <summary>
    /// Initializes a new instance of the BulkKeyValueStoreWithBloomFilterBenchmark class.
    /// Sets the store to an instance of BulkKeyValueStoreWithBloomFilter.
    /// </summary>
    public BulkKeyValueStoreWithBloomFilterBenchmark()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var innerStore = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            new StorageFile(Directory.GetCurrentDirectory(), "BulkKeyValueStoreWithBloomFilter.db"), 
            entrySerializer);

        var expectedElements = 1000;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<SerializableWrapper<int>>(expectedElements, falsePositiveProbability);

        SetStore(new BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>(
            innerStore, 
            bloomFilter));
    }
}