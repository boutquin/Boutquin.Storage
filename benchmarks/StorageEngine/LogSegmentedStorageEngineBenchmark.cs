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
namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Benchmark class for testing the LogSegmentedStorageEngine implementation using BulkKeyValueStoreWithBloomFilter as the inner engine.
/// </summary>
/// <remarks>
/// This class is not sealed to allow BenchmarkDotNet to create proxy subclasses for executing benchmarks.
/// These proxies facilitate various operations such as instrumentation, profiling, and more. Hence, to ensure smooth execution of the benchmarks and to adhere to the requirements of the BenchmarkDotNet framework, this class must remain unsealed.
/// </remarks>
public class LogSegmentedStorageEngineBenchmark : StorageEngineBenchmark<SerializableWrapper<int>, SerializableWrapper<string>>
{
    /// <summary>
    /// Initializes a new instance of the LogSegmentedStorageEngineBenchmark class.
    /// Sets the store to an instance of LogSegmentedStorageEngine wrapped with a BulkKeyValueStoreWithBloomFilter.
    /// </summary>
    public LogSegmentedStorageEngineBenchmark()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var directory = Directory.GetCurrentDirectory();
        var prefix = "LogSegment_";
        long maxSegmentSize = 1024; // For example, 1 KB per segment file.
        Func<string, string, IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>, long, IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>> storageEngineFactory =
            (fileLocation, fileName, serializer, maxSize) => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(new StorageFile(fileLocation, fileName), serializer);

        SetStore(new LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            entrySerializer,
            directory,
            prefix,
            maxSegmentSize,
            storageEngineFactory));
    }
}
