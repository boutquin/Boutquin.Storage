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

/// <summary>
/// Fix-1 / Fix-2: corrupt-manifest fail-closed behaviour.
/// When the on-disk manifest is corrupt, mutating operations (RegisterAsync / SetAsync)
/// must throw <see cref="CorruptCoverageManifestException"/> and leave the on-disk bytes
/// untouched. RebuildAsync is the explicit recovery path and must clear the corrupt flag.
/// </summary>
public sealed class FileDatasetCoverageIndexCorruptManifestTests : IDisposable
{
    private readonly string _manifestPath;

    public FileDatasetCoverageIndexCorruptManifestTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "boutquin-storage-tests-corrupt", Guid.NewGuid().ToString("N"));
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

    // ── Fix 1: RegisterAsync on corrupt manifest throws and leaves bytes untouched ──

    [Fact]
    public async Task CorruptManifest_RegisterAsync_ThrowsCorruptException()
    {
        // Arrange — write garbage bytes to the manifest path.
        await File.WriteAllTextAsync(_manifestPath, "{not valid json");
        var originalBytes = await File.ReadAllBytesAsync(_manifestPath);

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        // Act + Assert — RegisterAsync must throw CorruptCoverageManifestException.
        await idx.Awaiting(x => x.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31)))
            .Should().ThrowAsync<CorruptCoverageManifestException>()
            .WithMessage("*coverage.json*");
    }

    [Fact]
    public async Task CorruptManifest_RegisterAsync_DoesNotOverwriteOnDiskBytes()
    {
        // Arrange — write garbage bytes, capture them.
        var garbled = System.Text.Encoding.UTF8.GetBytes("{not valid json");
        await File.WriteAllBytesAsync(_manifestPath, garbled);

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        // Act — swallow the expected exception.
        try
        {
            await idx.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31));
        }
        catch (CorruptCoverageManifestException)
        {
            // expected
        }

        // Assert — on-disk bytes must be bit-for-bit identical to the original garbage.
        var afterBytes = await File.ReadAllBytesAsync(_manifestPath);
        afterBytes.Should().Equal(garbled,
            "RegisterAsync must not overwrite a corrupt manifest");
    }

    [Fact]
    public async Task CorruptManifest_SetAsync_ThrowsCorruptException()
    {
        await File.WriteAllTextAsync(_manifestPath, "{garbage}");

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        await idx.Awaiting(x => x.SetAsync(D("AAPL"), new DateRange(DD(2020, 1, 1), DD(2020, 12, 31))))
            .Should().ThrowAsync<CorruptCoverageManifestException>();
    }

    [Fact]
    public async Task CorruptManifest_GetAsync_ReturnsNull_WithoutThrowing()
    {
        // GetAsync is a read operation — it must not throw on corruption, just return null.
        await File.WriteAllTextAsync(_manifestPath, "{not valid json");

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        var coverage = await idx.GetAsync(D("AAPL"));
        coverage.Should().BeNull();
    }

    // ── Fix 1: RebuildAsync is the explicit recovery path ──

    [Fact]
    public async Task CorruptManifest_RebuildAsync_ClearsCorruptFlag_AndOperationsWork()
    {
        // Arrange — corrupt manifest so RegisterAsync would fail.
        await File.WriteAllTextAsync(_manifestPath, "{not valid json");
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        // Verify it is indeed broken first.
        await idx.Awaiting(x => x.RegisterAsync(D("X"), DD(2020, 1, 1), DD(2020, 12, 31)))
            .Should().ThrowAsync<CorruptCoverageManifestException>();

        // Act — rebuild from a scan clears the corrupt flag.
        await idx.RebuildAsync(Scan((D("AAPL"), DD(2020, 1, 1)), (D("AAPL"), DD(2020, 6, 30))));

        // Assert — coverage now accessible.
        (await idx.GetAsync(D("AAPL")))
            .Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 6, 30)));

        // Assert — subsequent RegisterAsync no longer throws.
        await idx.Awaiting(x => x.RegisterAsync(D("MSFT"), DD(2021, 1, 1), DD(2021, 12, 31)))
            .Should().NotThrowAsync();

        (await idx.GetAsync(D("MSFT")))
            .Should().Be(new DateRange(DD(2021, 1, 1), DD(2021, 12, 31)));
    }

    [Fact]
    public async Task CorruptManifest_RebuildAsync_PersistsFreshManifest_ReloadSees()
    {
        await File.WriteAllTextAsync(_manifestPath, "completely garbage bytes %%%");
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        await idx.RebuildAsync(Scan((D("AAPL"), DD(2023, 5, 1))));

        // A fresh index reading the same path should see the rebuilt state.
        var idx2 = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        (await idx2.GetAsync(D("AAPL")))
            .Should().Be(new DateRange(DD(2023, 5, 1), DD(2023, 5, 1)));
    }

    // ── Fix 1 / unknown schema version — treated as corrupt ──

    [Fact]
    public async Task UnknownSchemaVersion_RegisterAsync_ThrowsCorruptException()
    {
        await File.WriteAllTextAsync(_manifestPath, """{"schemaVersion":99,"datasets":[]}""");

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        await idx.Awaiting(x => x.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31)))
            .Should().ThrowAsync<CorruptCoverageManifestException>()
            .WithMessage("*schema version*");
    }

    [Fact]
    public async Task CorruptManifest_ExceptionCarriesManifestPath()
    {
        await File.WriteAllTextAsync(_manifestPath, "{bad}");

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        var ex = await Assert.ThrowsAsync<CorruptCoverageManifestException>(
            () => idx.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31)));

        ex.ManifestPath.Should().Be(_manifestPath);
    }

    // ── AC-9.1 (F-28): a single un-decodable dataset entry must fail the whole load CLOSED ──
    // A schema-valid manifest whose datasets list carries one good + one base64-garbage key was
    // previously loaded by silently DROPPING the garbage row (continue past the decode failure).
    // _state then held only the good row, so the next RegisterAsync/SetAsync persisted that lossy
    // subset over the original manifest — silent data loss. The index must fail closed instead:
    // a write throws CorruptCoverageManifestException and never overwrites the on-disk bytes.

    private static string GoodKeyBase64(string ticker)
    {
        using var ms = new MemoryStream();
        D(ticker).Serialize(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private string WriteOneGoodOneGarbageManifest()
    {
        // schemaVersion 1, two entries: one decodable key, one un-decodable (not valid base64).
        var json =
            $$"""
            {
              "schemaVersion": 1,
              "datasets": [
                { "key": "{{GoodKeyBase64("AAPL")}}", "from": "2020-01-01", "to": "2020-12-31" },
                { "key": "!!!not-valid-base64!!!", "from": "2021-01-01", "to": "2021-12-31" }
              ]
            }
            """;
        File.WriteAllText(_manifestPath, json);
        return json;
    }

    [Fact]
    public async Task UndecodableEntry_RegisterAsync_ThrowsCorruptException()
    {
        WriteOneGoodOneGarbageManifest();

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        await idx.Awaiting(x => x.RegisterAsync(D("MSFT"), DD(2022, 1, 1), DD(2022, 12, 31)))
            .Should().ThrowAsync<CorruptCoverageManifestException>(
                "a manifest entry that fails to decode means the on-disk state cannot be trusted; "
                + "the index must fail closed rather than silently drop the un-decodable row");
    }

    [Fact]
    public async Task UndecodableEntry_RegisterAsync_DoesNotSilentlyPrune_OnDiskBytesUntouched()
    {
        var originalJson = WriteOneGoodOneGarbageManifest();
        var originalBytes = await File.ReadAllBytesAsync(_manifestPath);

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        // Act — attempt a write; swallow the expected fail-closed exception.
        try
        {
            await idx.RegisterAsync(D("MSFT"), DD(2022, 1, 1), DD(2022, 12, 31));
        }
        catch (CorruptCoverageManifestException)
        {
            // expected — fail closed
        }

        // Assert — the on-disk manifest is bit-for-bit unchanged: the garbage row was NOT pruned,
        // and the good row was NOT re-persisted without it. Silent data loss is the bug under test.
        var afterBytes = await File.ReadAllBytesAsync(_manifestPath);
        afterBytes.Should().Equal(originalBytes,
            "a write must not silently persist _state minus the un-decodable rows");

        // Belt-and-suspenders: the un-decodable key's payload must still be present on disk.
        var afterText = System.Text.Encoding.UTF8.GetString(afterBytes);
        afterText.Should().Contain("!!!not-valid-base64!!!",
            "the un-decodable entry must not be silently dropped from the manifest");
        afterText.Should().Be(originalJson);
    }

    // ── AC-9.2 (F-29): access-failure family routes through the SAME fail-closed path ──
    // TryLoadLocked previously caught only IOException. UnauthorizedAccessException and
    // PathTooLongException are NOT subclasses of IOException, so a permission/path failure
    // reading the manifest escaped raw instead of producing the documented fail-closed result.

    [Fact]
    public async Task UnreadableManifest_RegisterAsync_FailsClosed_NotRawAccessException()
    {
        // Arrange — a real manifest the process cannot read (chmod 000). On this (non-Windows)
        // platform File.ReadAllText then throws UnauthorizedAccessException, not IOException.
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX permission model; the chmod trick does not apply on Windows.
        }

        await File.WriteAllTextAsync(_manifestPath, """{"schemaVersion":1,"datasets":[]}""");
        File.SetUnixFileMode(_manifestPath, UnixFileMode.None); // 000 — no read for anyone

        try
        {
            var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

            // The access failure must be funneled into the documented fail-closed contract
            // (CorruptCoverageManifestException), NOT escape as a raw UnauthorizedAccessException.
            var act = idx.Awaiting(x => x.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31)));

            await act.Should().ThrowAsync<CorruptCoverageManifestException>(
                "an unreadable manifest is an access failure that must route through the same "
                + "fail-closed path as IOException, not surface a raw access exception");
        }
        finally
        {
            // Restore read access so Dispose can clean up the temp directory.
            File.SetUnixFileMode(_manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ── Missing / null / inverted date fields fail closed ──
    // A parseable row whose "from" or "to" is absent used to load as DateOnly's default
    // (0001-01-01), and an inverted row loaded as an empty-but-present range. Either becomes
    // the cached coverage the store serves instead of scanning the engine (a decade with no
    // data reports no gaps). Such a row is corruption: GetAsync must not serve it, and a write
    // must neither merge into it nor overwrite the recoverable bytes.

    [Theory]
    [InlineData("missing-from")]
    [InlineData("missing-to")]
    [InlineData("null-from")]
    [InlineData("inverted")]
    public async Task InvalidDateFields_FailClosed_NotServedAndNotOverwritten(string shape)
    {
        var good = $$"""{ "key": "{{GoodKeyBase64("MSFT")}}", "from": "2020-01-01", "to": "2020-12-31" }""";
        var bad = shape switch
        {
            "missing-from" => $$"""{ "key": "{{GoodKeyBase64("AAPL")}}", "to": "2024-01-10" }""",
            "missing-to" => $$"""{ "key": "{{GoodKeyBase64("AAPL")}}", "from": "2024-01-01" }""",
            "null-from" => $$"""{ "key": "{{GoodKeyBase64("AAPL")}}", "from": null, "to": "2024-01-10" }""",
            _ => $$"""{ "key": "{{GoodKeyBase64("AAPL")}}", "from": "2024-01-10", "to": "2024-01-01" }""",
        };
        var json = $$"""{ "schemaVersion": 1, "datasets": [ {{good}}, {{bad}} ] }""";
        await File.WriteAllTextAsync(_manifestPath, json);
        var originalBytes = await File.ReadAllBytesAsync(_manifestPath);

        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        (await idx.GetAsync(D("AAPL"))).Should().BeNull(
            "a row with an absent, null or inverted date is corrupt and must not be served as coverage");

        await idx.Awaiting(x => x.RegisterAsync(D("AAPL"), DD(2024, 1, 11), DD(2024, 1, 12)))
            .Should().ThrowAsync<CorruptCoverageManifestException>()
            .WithMessage("*coverage.json*");

        (await File.ReadAllBytesAsync(_manifestPath)).Should().Equal(originalBytes,
            "a failed-closed write must leave the recoverable manifest bytes untouched");
    }

    // The write side must not persist a range the load side refuses: an inverted range written
    // through RegisterAsync/SetAsync would turn the next reopen into a fail-closed manifest.
    [Fact]
    public async Task InvertedRange_RegisterAndSet_RejectedBeforePersist()
    {
        var idx = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);

        await idx.Awaiting(x => x.RegisterAsync(D("AAPL"), DD(2024, 1, 10), DD(2024, 1, 1)))
            .Should().ThrowAsync<ArgumentException>();
        await idx.Awaiting(x => x.SetAsync(D("AAPL"), new DateRange(DD(2024, 1, 10), DD(2024, 1, 1))))
            .Should().ThrowAsync<ArgumentException>();

        File.Exists(_manifestPath).Should().BeFalse("nothing valid was written");

        // A valid write afterwards still persists and reloads.
        await idx.RegisterAsync(D("AAPL"), DD(2024, 1, 1), DD(2024, 1, 10));
        var reopened = new FileDatasetCoverageIndex<SerializableWrapper<string>>(_manifestPath);
        (await reopened.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2024, 1, 1), DD(2024, 1, 10)));
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
}
