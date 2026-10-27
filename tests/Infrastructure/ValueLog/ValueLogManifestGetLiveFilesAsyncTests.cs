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

using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.ValueLog;

/// <summary>
/// M-4 AC-4.2 — the blocking <c>LiveFiles</c> property (which called <c>_lock.Wait()</c> on a
/// getter, a sync-over-async hazard on a property access) is replaced by the awaitable
/// <c>GetLiveFilesAsync(CancellationToken)</c>, which acquires the manifest lock via
/// <c>await _lock.WaitAsync</c>. These tests pin the new accessor's correctness against the
/// reconstructed live set.
/// </summary>
public sealed class ValueLogManifestGetLiveFilesAsyncTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ValueLogManifestGetLiveFilesAsyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlmg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "valuelog.manifest");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GetLiveFilesAsync_OnNewManifest_IsEmpty()
    {
        await using var manifest = new ValueLogManifest(_path);

        var live = await manifest.GetLiveFilesAsync();

        live.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLiveFilesAsync_ReflectsAddsAndDeletes()
    {
        await using var manifest = new ValueLogManifest(_path);
        await manifest.RecordAddAsync(1);
        await manifest.RecordAddAsync(2);
        await manifest.RecordDeleteAsync(1);
        await manifest.RecordAddAsync(3);

        var live = await manifest.GetLiveFilesAsync();

        live.Should().HaveCount(2);
        live.Should().Contain(2u);
        live.Should().Contain(3u);
        live.Should().NotContain(1u);
    }

    [Fact]
    public async Task GetLiveFilesAsync_ReturnsDefensiveSnapshot_NotLiveView()
    {
        await using var manifest = new ValueLogManifest(_path);
        await manifest.RecordAddAsync(5);

        var snapshot = await manifest.GetLiveFilesAsync();
        await manifest.RecordAddAsync(6); // mutate after snapshot

        // The earlier snapshot must NOT observe the later mutation (defensive copy).
        snapshot.Should().Contain(5u);
        snapshot.Should().NotContain(6u);

        var fresh = await manifest.GetLiveFilesAsync();
        fresh.Should().Contain(5u).And.Contain(6u);
    }
}
