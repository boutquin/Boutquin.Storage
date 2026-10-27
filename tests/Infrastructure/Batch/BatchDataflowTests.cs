// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Runtime.CompilerServices;
using System.Text.Json;

using Boutquin.Storage.Infrastructure.Batch;

namespace Boutquin.Storage.Infrastructure.Tests.Batch;

public sealed class BatchDataflowTests
{
    [Fact]
    public async Task SortSpillsMultipleMergePasses_RetainsDuplicates_AndCleansItsFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var data = Enumerable.Range(0, 700).Select(i => (i * 17) % 41).ToArray();
            var sorter = new ExternalMergeSorter<int>(directory, Comparer<int>.Default,
                value => JsonSerializer.SerializeToUtf8Bytes(value), bytes => JsonSerializer.Deserialize<int>(bytes.Span),
                maxItemsInMemory: 7, mergeFanIn: 3);
            var actual = new List<int>();
            await foreach (var value in sorter.SortAsync(Values(data)).ConfigureAwait(true))
            {
                actual.Add(value);
            }
            Assert.Equal(data.Order(), actual);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task EarlyStop_Cancellation_AndCodecFailure_CleanOwnedScratchOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sentinel = Path.Combine(directory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unrelated").ConfigureAwait(true);
        try
        {
            var sorter = new ExternalMergeSorter<int>(directory, Comparer<int>.Default,
                BitConverter.GetBytes, bytes => BitConverter.ToInt32(bytes.Span), maxItemsInMemory: 2, mergeFanIn: 2);
            await foreach (var _ in sorter.SortAsync(Values([4, 3, 2, 1])).ConfigureAwait(true)) { break; }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in sorter.SortAsync(Values([1]), cancellation.Token).ConfigureAwait(true)) { }
            }).ConfigureAwait(true);
            var broken = new ExternalMergeSorter<int>(directory, Comparer<int>.Default,
                _ => throw new InvalidOperationException("codec"), bytes => 0, maxItemsInMemory: 2);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in broken.SortAsync(Values([1])).ConfigureAwait(true)) { }
            }).ConfigureAwait(true);
            Assert.Equal([sentinel], Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ShuffleCoLocatesKeys_AndPartitionsFeedGroupingAndDuplicatePreservingJoin()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var partitioner = new HashShufflePartitioner<int>(value => BitConverter.GetBytes(value % 3));
            using (var files = await ShuffleFileSet<int>.CreateAsync(Values([1, 2, 4, 5, 7]), directory,
                partitioner, 4, BitConverter.GetBytes, bytes => BitConverter.ToInt32(bytes.Span)).ConfigureAwait(true))
            {
                var all = new List<int>();
                for (var partition = 0; partition < files.PartitionCount; partition++)
                {
                    await foreach (var row in files.ReadPartitionAsync(partition).ConfigureAwait(true))
                    {
                        Assert.Equal(partition, partitioner.GetPartition(row, 4));
                        all.Add(row);
                    }
                }
                Assert.Equal([1, 2, 4, 5, 7], all.Order());
                var frame = new MiniDataFrame<int>(all);
                Assert.Equal([7, 12], frame.GroupBy(value => value % 3, (_, values) => values.Sum()).Rows.Order());
                var joined = new MiniDataFrame<int>([1, 4]).Join(new MiniDataFrame<int>([7, 10]),
                    value => value % 3, value => value % 3, (left, right) => left + right);
                Assert.Equal([8, 11, 11, 14], joined.Rows.Order());
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DagValidatesBeforeEffects_AndStopsAfterFailure()
    {
        var effects = new List<string>();
        var first = new BatchTask("read", [], _ => { effects.Add("read"); return ValueTask.CompletedTask; });
        var second = new BatchTask("aggregate", ["read"], _ => { effects.Add("aggregate"); return ValueTask.CompletedTask; });
        var third = new BatchTask("write", ["aggregate"], _ => { effects.Add("write"); return ValueTask.CompletedTask; });
        var runner = new BatchJobRunner();
        Assert.Equal(["read", "aggregate", "write"], await runner.RunAsync(new BatchJobGraph([third, first, second])).ConfigureAwait(true));
        Assert.Equal(["read", "aggregate", "write"], effects);
        effects.Clear();
        Assert.Throws<ArgumentException>(() => new BatchJobGraph([new BatchTask("bad", ["missing"], _ => ValueTask.CompletedTask)]));
        Assert.Throws<ArgumentException>(() => new BatchJobGraph([
            new BatchTask("a", ["b"], _ => ValueTask.CompletedTask), new BatchTask("b", ["a"], _ => ValueTask.CompletedTask)]));
        var fails = new BatchTask("aggregate", ["read"], _ => throw new IOException("sink failed"));
        await Assert.ThrowsAsync<IOException>(async () => await runner.RunAsync(new BatchJobGraph([first, fails, third])).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(["read"], effects);
    }

    [Fact]
    public async Task MidInputCancellation_AndOversizedRecord_CleanSpilledRuns()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var sorter = new ExternalMergeSorter<int>(directory, Comparer<int>.Default, BitConverter.GetBytes,
                bytes => BitConverter.ToInt32(bytes.Span), maxItemsInMemory: 2);
            async IAsyncEnumerable<int> CancelAfterSpill()
            {
                yield return 2; yield return 1;
                cancellation.Cancel();
                yield return 3;
                await Task.CompletedTask.ConfigureAwait(true);
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in sorter.SortAsync(CancelAfterSpill(), cancellation.Token).ConfigureAwait(true)) { }
            }).ConfigureAwait(true);
            var bounded = new ExternalMergeSorter<int>(directory, Comparer<int>.Default, BitConverter.GetBytes,
                bytes => BitConverter.ToInt32(bytes.Span), maxRecordBytes: 1);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (var _ in bounded.SortAsync(Values([1])).ConfigureAwait(true)) { }
            }).ConfigureAwait(true);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SeededSortMatchesReferenceIncludingEmptyInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var seed = 0; seed < 10; seed++)
            {
                var random = new Random(seed);
                var values = Enumerable.Range(0, seed * 30).Select(_ => random.Next(-20, 21)).ToArray();
                var sorter = new ExternalMergeSorter<int>(directory, Comparer<int>.Default, BitConverter.GetBytes,
                    bytes => BitConverter.ToInt32(bytes.Span), maxItemsInMemory: 5, mergeFanIn: 2);
                var actual = new List<int>();
                await foreach (var value in sorter.SortAsync(Values(values)).ConfigureAwait(true)) { actual.Add(value); }
                Assert.Equal(values.Order(), actual);
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async IAsyncEnumerable<int> Values(IEnumerable<int> values,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var value in values) { ct.ThrowIfCancellationRequested(); yield return value; }
        await Task.CompletedTask.ConfigureAwait(true);
    }
}
