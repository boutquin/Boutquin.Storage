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

using Boutquin.Storage.Infrastructure.Caching;

namespace Boutquin.Storage.Infrastructure.Tests.Caching;

public sealed class FreshnessCheckerTests : IDisposable
{
    private readonly string _tempDir;

    public FreshnessCheckerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"freshness-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void IsFresh_Force_ReturnsFalse()
    {
        var path = CreateTempFile(age: TimeSpan.FromMinutes(1));

        FreshnessChecker.IsFresh(path, TimeSpan.FromDays(7), force: true)
            .Should().BeFalse("force flag always forces re-download");
    }

    [Fact]
    public void IsFresh_NonPositiveMaxAge_ReturnsFalse()
    {
        var path = CreateTempFile(age: TimeSpan.FromMinutes(1));

        FreshnessChecker.IsFresh(path, TimeSpan.Zero)
            .Should().BeFalse("zero max age means always stale");

        FreshnessChecker.IsFresh(path, TimeSpan.FromDays(-1))
            .Should().BeFalse("negative max age means always stale");
    }

    [Fact]
    public void IsFresh_FileDoesNotExist_ReturnsFalse()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");

        FreshnessChecker.IsFresh(path, TimeSpan.FromDays(7))
            .Should().BeFalse("missing file is never fresh");
    }

    [Fact]
    public void IsFresh_RecentFile_ReturnsTrue()
    {
        var path = CreateTempFile(age: TimeSpan.FromMinutes(5));

        FreshnessChecker.IsFresh(path, TimeSpan.FromDays(7))
            .Should().BeTrue("file is newer than max age");
    }

    [Fact]
    public void IsFresh_StaleFile_ReturnsFalse()
    {
        var path = CreateTempFile(age: TimeSpan.FromDays(10));

        FreshnessChecker.IsFresh(path, TimeSpan.FromDays(7))
            .Should().BeFalse("file is older than max age");
    }

    private string CreateTempFile(TimeSpan age)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
