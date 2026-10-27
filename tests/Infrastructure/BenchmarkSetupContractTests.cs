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
using System.Reflection;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins that the StorageEngine benchmark harness can execute its own <c>GlobalSetup</c> and
/// measured methods against the stores' current contracts: the in-memory structures reject the
/// default key, and the append-only engines are single-mutator (overlapping writers fail fast).
/// A harness that throws in setup produces no measurements at all, so the failure would otherwise
/// surface only as a "produced no measurements" abort of a full benchmark run.
/// </summary>
/// <remarks>
/// The benchmark project is an executable that this test project does not reference, so the
/// built assembly is loaded by path (it is produced by building the solution). A missing assembly
/// fails the test with an explicit message rather than skipping, because a skip would hide the
/// very regression this class guards.
/// </remarks>
public sealed class BenchmarkSetupContractTests : IDisposable
{
    private const string BenchmarkNamespace = "Boutquin.Storage.BenchMark";
    private const int Items = 100;

    private readonly string _dir;

    public BenchmarkSetupContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmark-setup-contract-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task DataStructureBenchmark_SetupAndInsertMethods_RunWithoutThrowing()
    {
        var benchmark = CreateBenchmark("DataStructureBenchmark");
        benchmark.GetType().GetField("ItemCount")!.SetValue(benchmark, Items);

        await InvokeAsync(benchmark, "Setup");
        foreach (var method in new[] { "BTreeInsert", "BPlusTreeInsert", "RedBlackTreeInsert", "SkipListInsert" })
        {
            await InvokeAsync(benchmark, method);
        }

        // Search runs against the structures Setup populated; a Setup that stored fewer keys than
        // ItemCount would make these measure misses, so the populated state is part of the contract.
        foreach (var method in new[] { "BTreeSearch", "BPlusTreeSearch", "RedBlackTreeSearch", "SkipListSearch" })
        {
            await InvokeAsync(benchmark, method);
        }
    }

    [Theory]
    [InlineData("AppendOnlyFileStorageEngineBenchmark", false)]
    [InlineData("AppendOnlyFileStorageEngineWithIndexBenchmark", true)]
    public async Task AppendOnlyBenchmarks_SetupAndWrite_RunAgainstSingleMutatorEngine(string typeName, bool withIndex)
    {
        var benchmark = CreateBenchmark(typeName);
        benchmark.GetType().GetField("ItemCount")!.SetValue(benchmark, Items);

        // Redirect the store into this test's directory: the benchmark constructors target the
        // process working directory, which a unit test must not write to.
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var file = new StorageFile(_dir, typeName + ".db");
        IStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> store = withIndex
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
                file, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(file, serializer);
        benchmark.GetType().GetMethod("SetStore")!.Invoke(benchmark, [store]);

        await InvokeAsync(benchmark, "Setup");
        await InvokeAsync(benchmark, "Write");
        await InvokeAsync(benchmark, "Read");
        await InvokeAsync(benchmark, "SearchExisting");
        await InvokeAsync(benchmark, "SearchNonExisting");
    }

    private static object CreateBenchmark(string typeName)
    {
        var type = LoadBenchmarkAssembly().GetType($"{BenchmarkNamespace}.{typeName}", throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }

    private static async Task InvokeAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        try
        {
            if (method.Invoke(target, null) is Task task)
            {
                await task;
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the harness's own exception rather than the reflection wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static Assembly LoadBenchmarkAssembly()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Boutquin.Storage.slnx")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull("the repository root (Boutquin.Storage.slnx) must be an ancestor of the test output");

        var binDir = Path.Combine(root!.FullName, "benchmarks", "StorageEngine", "bin");
        var candidates = Directory.Exists(binDir)
            ? Directory.GetFiles(binDir, "Boutquin.Storage.BenchMark.dll", SearchOption.AllDirectories)
            : [];
        candidates.Should().NotBeEmpty("the benchmark project must be built (build the solution) before this test runs");

        var newest = candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
        return Assembly.LoadFrom(newest);
    }
}
