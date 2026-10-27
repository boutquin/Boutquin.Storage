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
using Boutquin.Storage.Infrastructure.ObjectStore;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Symlink-containment tests for <see cref="FileSystemObjectStore"/> that need MORE than one
/// symlink hop: a link whose target path itself crosses another symlinked directory, a relative
/// link target that climbs through a symlinked directory with <c>..</c>, a dangling link, and a
/// link cycle. Containment must be decided on the physically resolved path, not on a path that
/// merely had its last link followed.
/// </summary>
public sealed class FileSystemObjectStoreSymlinkChainTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;
    private readonly string _outside;
    private readonly FileSystemObjectStore _store;

    public FileSystemObjectStoreSymlinkChainTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "objstore-symlink-chain", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "root");
        _outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _store = new FileSystemObjectStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task TwoHopSymlink_ThroughAbsoluteTarget_RejectsWriteReadExistsAndDelete()
    {
        // root/c -> outside, root/a -> <root>/c/d: the second link's target crosses the first
        // link, so following only the FINAL link yields a string that looks like it is inside
        // the root while the bytes land in outside/d.
        var realRoot = ResolvedSandboxRoot();
        Directory.CreateDirectory(Path.Combine(_outside, "d"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "c"), _outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "a"), Path.Combine(realRoot, "c", "d"));

        var write = async () =>
        {
            await using var stream = new MemoryStream([1, 2, 3]);
            await _store.WriteAsync("a/file", stream).ConfigureAwait(false);
        };

        await write.Should().ThrowAsync<ArgumentException>(
            "a link chain that leaves the root must be rejected").ConfigureAwait(true);
        Directory.GetFileSystemEntries(Path.Combine(_outside, "d")).Should()
            .BeEmpty("nothing may be created outside the root through a two-hop link chain");

        // Plant an object outside the root and prove read/exists/delete refuse to reach it.
        var planted = Path.Combine(_outside, "d", "file@obj");
        await File.WriteAllBytesAsync(planted, [9, 9, 9]).ConfigureAwait(true);

        var exists = async () => await _store.ExistsAsync("a/file").ConfigureAwait(false);
        var read = async () => await _store.ReadAsync("a/file").ConfigureAwait(false);
        var delete = async () => await _store.DeleteAsync("a/file").ConfigureAwait(false);

        await exists.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        await read.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        await delete.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        File.Exists(planted).Should().BeTrue("a rejected delete must not remove the object outside the root");
    }

    [Fact]
    public async Task RelativeLinkTarget_ClimbingThroughSymlinkedDirectory_IsRejected()
    {
        // root/c -> outside/sub; root/a -> "c/.." (relative). Physically c/.. is the parent of
        // outside/sub, i.e. outside, whereas a lexical collapse of "c/.." lands back inside root.
        Directory.CreateDirectory(Path.Combine(_outside, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "c"), Path.Combine(_outside, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "a"), Path.Combine("c", ".."));

        var write = async () =>
        {
            await using var stream = new MemoryStream([1]);
            await _store.WriteAsync("a/escaped", stream).ConfigureAwait(false);
        };

        await write.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        File.Exists(Path.Combine(_outside, "escaped@obj")).Should().BeFalse();
    }

    [Fact]
    public async Task DanglingSymlinkToOutsideRoot_IsRejected_AndCreatesNothingOutside()
    {
        // The link target does not exist yet, so an existence-gated resolver skips the link
        // and the later directory creation follows it out of the root.
        Directory.CreateSymbolicLink(Path.Combine(_root, "dangling"), Path.Combine(_outside, "not-yet"));

        var write = async () =>
        {
            await using var stream = new MemoryStream([1]);
            await _store.WriteAsync("dangling/escaped", stream).ConfigureAwait(false);
        };

        await write.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        Directory.Exists(Path.Combine(_outside, "not-yet")).Should()
            .BeFalse("a dangling link must not let a write create directories outside the root");
    }

    [Fact]
    public async Task SymlinkChainStayingInsideRoot_IsAccepted_AndRoundTrips()
    {
        // Containment must not become a blanket symlink ban: links that resolve inside the root
        // (a link to a link to a real directory) keep working.
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "l1"), "real");
        Directory.CreateSymbolicLink(Path.Combine(_root, "l2"), "l1");

        await using (var stream = new MemoryStream([4, 5, 6]))
        {
            await _store.WriteAsync("l2/inside", stream).ConfigureAwait(true);
        }

        File.Exists(Path.Combine(_root, "real", "inside@obj")).Should().BeTrue();
        (await _store.ExistsAsync("l2/inside").ConfigureAwait(true)).Should().BeTrue();
        await using var read = await _store.ReadAsync("l2/inside").ConfigureAwait(true);
        read.Should().NotBeNull();
    }

    [Fact]
    public async Task SymlinkCycle_IsRejected_WithoutHanging()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "x"), "y");
        Directory.CreateSymbolicLink(Path.Combine(_root, "y"), "x");

        var exists = async () => await _store.ExistsAsync("x/key").ConfigureAwait(false);

        await exists.Should().ThrowAsync<Exception>(
            "a link cycle has no real path and must fail rather than loop or be treated as contained").ConfigureAwait(true);
    }

    // The sandbox may sit under a symlinked temp location (macOS /var -> /private/var). An absolute
    // link target spelled lexically through that symlink would be rejected for the wrong reason, so
    // targets that must reproduce the escape use the physical spelling.
    private string ResolvedSandboxRoot()
    {
        var path = Path.GetFullPath(_root);
        return OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + path
            : path;
    }
}
