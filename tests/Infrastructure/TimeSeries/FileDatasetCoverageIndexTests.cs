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
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

public sealed partial class FileDatasetCoverageIndexTests : IDisposable
{
    private readonly string _manifestPath;

    public FileDatasetCoverageIndexTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "boutquin-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _manifestPath = Path.Combine(dir, "coverage.json");
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_manifestPath)!;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static SerializableWrapper<string> D(string s) => new(s);
    private static DateOnly DD(int y, int m, int d) => new(y, m, d);

    [Fact]
    public async Task NoFileYet_Get_ReturnsNull()
    {
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        var coverage = await idx.GetAsync(D("AAPL"));

        coverage.Should().BeNull();
    }

    [Fact]
    public async Task Register_PersistsToFile_ReloadSees()
    {
        var idx1 = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        await idx1.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31));

        var idx2 = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        (await idx2.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));
    }

    [Fact]
    public async Task Manifest_CarriesSchemaVersionOne()
    {
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        await idx.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31));

        var json = await File.ReadAllTextAsync(_manifestPath);

        // JSON property naming uses System.Text.Json default (PascalCase) with schemaVersion = 1.
        // Any whitespace variation around the colon is acceptable.
        var normalised = MyRegex().Replace(json, string.Empty);
        normalised.Should().Contain("\"schemaVersion\":1");
    }

    [Fact]
    public async Task CorruptManifest_Get_ReturnsNull()
    {
        await File.WriteAllTextAsync(_manifestPath, "{not valid json");

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        (await idx.GetAsync(D("AAPL"))).Should().BeNull();
    }

    [Fact]
    public async Task UnknownSchemaVersion_Get_ReturnsNull()
    {
        await File.WriteAllTextAsync(_manifestPath, "{\"schemaVersion\": 99, \"datasets\": []}");

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        (await idx.GetAsync(D("AAPL"))).Should().BeNull();
    }

    [Fact]
    public async Task Rebuild_ReplacesOnDiskState()
    {
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        await idx.RegisterAsync(D("stale"), DD(1999, 1, 1), DD(1999, 12, 31));

        await idx.RebuildAsync(Scan(
            (D("AAPL"), DD(2020, 1, 1)),
            (D("AAPL"), DD(2020, 12, 31))));

        (await idx.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));
        (await idx.GetAsync(D("stale"))).Should().BeNull();

        var reloaded = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        (await reloaded.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));
    }

    [Fact]
    public async Task Set_Null_RemovesDatasetEntry_AndPersists()
    {
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        await idx.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31));

        await idx.SetAsync(D("AAPL"), null);

        (await idx.GetAsync(D("AAPL"))).Should().BeNull();
        var reloaded = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        (await reloaded.GetAsync(D("AAPL"))).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentRegisters_PersistConsistently()
    {
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        var tasks = Enumerable.Range(0, 32).Select(i =>
            idx.RegisterAsync(
                D("AAPL"),
                DD(2020, 1, 1).AddDays(i),
                DD(2020, 1, 1).AddDays(i + 1)));

        await Task.WhenAll(tasks);

        var coverage = await idx.GetAsync(D("AAPL"));
        coverage.Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 1, 1).AddDays(32)));
    }

    [Fact]
    public async Task GetAsync_AfterReload_FindsEntryRegisteredByEqualKey()
    {
        // Regression: TryLoadLocked stored rows under entry.Key (the raw base64 string from the
        // JSON), but all other access paths (GetAsync, RegisterAsync, SetAsync, RebuildAsync)
        // store under EncodeKey(dataset). When the two encodings differ — e.g. due to whitespace
        // or padding variations — a loaded entry was permanently invisible to GetAsync.
        // The fix re-keys loaded rows through EncodeKey(ds).
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        await idx.RegisterAsync(D("MSFT"), DD(2021, 1, 1), DD(2021, 12, 31)).ConfigureAwait(true);

        // Reload from disk — this exercises the TryLoadLocked code path.
        var reloaded = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        var coverage = await reloaded.GetAsync(D("MSFT")).ConfigureAwait(true);

        // Before the fix: coverage was null because the stored key (entry.Key = raw base64)
        // did not match EncodeKey(D("MSFT")) on the lookup path.
        coverage.Should().NotBeNull("a key registered and persisted must be findable after reload");
        coverage!.Value.From.Should().Be(DD(2021, 1, 1));
        coverage.Value.To.Should().Be(DD(2021, 12, 31));
    }

    private static async IAsyncEnumerable<(SerializableWrapper<string> Dataset, DateOnly Date)> Scan(
        params (SerializableWrapper<string>, DateOnly)[] items)
    {
        foreach (var (ds, date) in items)
        {
            yield return (ds, date);
            await Task.Yield();
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
