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
using Boutquin.Storage.Domain.Interfaces.ObjectStore;
using Boutquin.Storage.Infrastructure.ObjectStore;

namespace Boutquin.Storage.Infrastructure.Tests.ObjectStore;

/// <summary>
/// Tests for <see cref="FileSystemObjectStore"/>.
/// Each test uses an isolated temp directory to prevent cross-test interference.
/// </summary>
public sealed class FileSystemObjectStoreTests : IDisposable
{
    private readonly string _rootDir;
    private readonly FileSystemObjectStore _store;

    public FileSystemObjectStoreTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "objstore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _store = new FileSystemObjectStore(_rootDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAndRead_RoundTrip_ReturnsOriginalContent()
    {
        // Arrange
        var key = "test-key.dat";
        var expected = "Hello, Object Store!"u8.ToArray();

        // Act
        await using (var writeStream = new MemoryStream(expected))
        {
            await _store.WriteAsync(key, writeStream).ConfigureAwait(true);
        }

        await using var readStream = await _store.ReadAsync(key).ConfigureAwait(true);

        // Assert
        readStream.Should().NotBeNull();
        var actual = new byte[expected.Length];
        var bytesRead = await readStream!.ReadAsync(actual).ConfigureAwait(true);
        bytesRead.Should().Be(expected.Length);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueAfterWrite_FalseBeforeAndAfterDelete()
    {
        // Arrange
        var key = "exists-test.dat";

        // Assert — before write
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeFalse();

        // Act — write
        await using (var stream = new MemoryStream([1, 2, 3]))
        {
            await _store.WriteAsync(key, stream).ConfigureAwait(true);
        }

        // Assert — after write
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();

        // Act — delete
        await _store.DeleteAsync(key).ConfigureAwait(true);

        // Assert — after delete
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_OnMissingKey_DoesNotThrow()
    {
        // Act & Assert — should be a no-op
        var act = () => _store.DeleteAsync("nonexistent-key").AsTask();
        await act.Should().NotThrowAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task HierarchicalKeys_CreatesNestedDirectories()
    {
        // Arrange
        var key = "data/2026/AAPL.json";
        var content = "{\"price\": 150.00}"u8.ToArray();

        // Act
        await using (var stream = new MemoryStream(content))
        {
            await _store.WriteAsync(key, stream).ConfigureAwait(true);
        }

        // Assert — file exists at nested path. The upper-case leaf segment carries its exact
        // case mask ("AAPL" = upper-case positions 0-3 → digits f,0,0) so a case-insensitive
        // volume cannot fold it onto a case-distinct key such as "data/2026/aapl.json"; every
        // leaf also carries the "@obj" marker that keeps object files apart from directories.
        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();
        var filePath = Path.Combine(_rootDir, "data", "2026", "AAPL.json@f00@obj");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentWrites_SameKey_NoCorruption()
    {
        var key = "concurrent.dat";
        var tasks = new Task[10];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contents = Enumerable.Range(0, tasks.Length)
            .Select(i => $"writer-{i}:" + new string((char)('a' + i), 1024 * (i + 1))).ToArray();

        for (var i = 0; i < tasks.Length; i++)
        {
            var data = Encoding.UTF8.GetBytes(contents[i]);
            var writer = new FileSystemObjectStore(_rootDir);
            tasks[i] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(true);
                await using var stream = new MemoryStream(data);
                await writer.WriteAsync(key, stream).ConfigureAwait(true);
            });
        }

        start.SetResult();
        await Task.WhenAll(tasks).ConfigureAwait(true);

        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();
        await using (var readStream = await _store.ReadAsync(key).ConfigureAwait(true))
        {
            readStream.Should().NotBeNull();
            using var ms = new MemoryStream();
            await readStream!.CopyToAsync(ms).ConfigureAwait(true);
            contents.Should().Contain(Encoding.UTF8.GetString(ms.ToArray()),
                "publication must choose one complete writer payload, never a torn or mixed result");
        }

        using var finalContent = new MemoryStream("final-writer"u8.ToArray());
        await _store.WriteAsync(key, finalContent).ConfigureAwait(true);
        await using var finalRead = await new FileSystemObjectStore(_rootDir).ReadAsync(key).ConfigureAwait(true);
        using var finalBytes = new MemoryStream();
        await finalRead!.CopyToAsync(finalBytes).ConfigureAwait(true);
        Encoding.UTF8.GetString(finalBytes.ToArray()).Should().Be("final-writer");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrEmptyKey_ThrowsArgumentException(string? key)
    {
        // Act & Assert
        var act = () => _store.ExistsAsync(key!).AsTask();
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadAsync_MissingKey_ReturnsNull()
    {
        // Act
        var result = await _store.ReadAsync("no-such-key.dat").ConfigureAwait(true);

        // Assert
        result.Should().BeNull();
    }

    // The character whitelist permits '..' segments and a leading '/', either of which
    // escapes the root. These keys must be rejected on every operation that touches the
    // filesystem — a successful read/write/delete here would mean directory traversal.
    [Theory]
    [InlineData("../escape.dat")]            // climb one level out of the root
    [InlineData("a/../../b.dat")]            // net upward walk after a descent
    [InlineData("/etc/passwd")]              // absolute key — Path.Combine would discard the root
    [InlineData("../../../../etc/passwd")]   // deep climb to a sensitive absolute target
    [InlineData("foo/../../bar")]            // escape via an interior '..'
    public async Task TraversalKey_IsRejected_OnEveryOperation(string key)
    {
        var content = new byte[] { 0xDE, 0xAD };

        var exists = () => _store.ExistsAsync(key).AsTask();
        await exists.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);

        var read = () => _store.ReadAsync(key).AsTask();
        await read.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);

        var write = async () =>
        {
            await using var stream = new MemoryStream(content);
            await _store.WriteAsync(key, stream).ConfigureAwait(false);
        };
        await write.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);

        var delete = () => _store.DeleteAsync(key).AsTask();
        await delete.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task TraversalWrite_DoesNotCreateFileOutsideRoot()
    {
        // A sentinel target one level above the root. Even before the fix the file should
        // never appear, so this is a direct, filesystem-observable assertion of containment.
        var parent = Directory.GetParent(_rootDir)!.FullName;
        var sentinel = Path.Combine(parent, $"escaped-{Guid.NewGuid():N}.dat");

        try
        {
            var act = async () =>
            {
                await using var stream = new MemoryStream([0x01]);
                await _store.WriteAsync($"../{Path.GetFileName(sentinel)}", stream).ConfigureAwait(false);
            };

            await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
            File.Exists(sentinel).Should().BeFalse("a traversal key must never write outside the root");
        }
        finally
        {
            if (File.Exists(sentinel))
            {
                File.Delete(sentinel);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_KeyThroughSymlinkedDirectory_IsRejected_AndWritesNothingOutsideRoot()
    {
        // Lexical canonicalization (Path.GetFullPath) does not follow symlinks, so an ancestor
        // directory that is a symlink pointing outside the root passes the lexical containment check
        // yet resolves outside. Containment must resolve the real path.
        var root = Path.Combine(_rootDir, "symroot");
        var outside = Path.Combine(_rootDir, "symoutside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        var store = new FileSystemObjectStore(root);

        var write = async () =>
        {
            await using var stream = new MemoryStream([0x01, 0x02, 0x03]);
            await store.WriteAsync("link/escaped.bin", stream).ConfigureAwait(false);
        };

        await write.Should().ThrowAsync<ArgumentException>(
            "a key whose ancestor is a symlink out of the root must be rejected").ConfigureAwait(true);
        File.Exists(Path.Combine(outside, "escaped.bin")).Should()
            .BeFalse("nothing may be written outside the root via a symlinked ancestor");
    }

    // AC-8.4 (F-21): WriteAsync with a non-readable stream silently produces an empty file
    // because content.CopyToAsync yields 0 bytes without error. Must throw ArgumentException.
    [Fact]
    public async Task WriteAsync_NonReadableStream_ThrowsArgumentException()
    {
        // A FileStream opened write-only is not readable.
        var tempFile = Path.Combine(_rootDir, "write-only.tmp");
        await using var writeOnly = new FileStream(tempFile, FileMode.Create, FileAccess.Write);

        var act = async () => await _store.WriteAsync("some-key.dat", writeOnly).ConfigureAwait(true);

        await act.Should().ThrowAsync<ArgumentException>(
            "a write-only stream cannot be read by WriteAsync; it must be rejected before silently producing an empty object").ConfigureAwait(true);
    }

    /// <summary>
    /// Keys are OPAQUE identifiers, so distinct keys must map to distinct objects. A "."
    /// segment or an empty segment ("//") is collapsed by the filesystem path resolution,
    /// silently aliasing "a/./b" and "a//b" onto "a/b" — three distinct keys sharing one
    /// file's Exists/Delete/last-writer-wins state (and diverging from the in-memory
    /// sibling, which stores them as three entries). Reject the degenerate segments.
    /// </summary>
    [Theory]
    [InlineData("a/./b.dat")]
    [InlineData("a//b.dat")]
    [InlineData("./a.dat")]
    public async Task WriteAsync_DotOrEmptySegment_ThrowsArgumentException(string key)
    {
        using var payload = new MemoryStream("x"u8.ToArray());
        var act = async () => await _store.WriteAsync(key, payload).ConfigureAwait(true);

        await act.Should().ThrowAsync<ArgumentException>(
            "'.'/empty segments alias distinct opaque keys onto one file").ConfigureAwait(true);
    }

    // Guard against over-eager rejection: dots that are NOT a standalone '..' segment are
    // legal key characters and must round-trip normally.
    [Fact]
    public async Task DottedKey_ThatIsNotATraversalSegment_RoundTrips()
    {
        var key = "a..b/c..d/..hidden.json";
        var expected = "{\"ok\":true}"u8.ToArray();

        await using (var stream = new MemoryStream(expected))
        {
            await _store.WriteAsync(key, stream).ConfigureAwait(true);
        }

        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeTrue();
        await using var readStream = await _store.ReadAsync(key).ConfigureAwait(true);
        readStream.Should().NotBeNull();
        var actual = new byte[expected.Length];
        await readStream!.ReadAsync(actual).ConfigureAwait(true);
        actual.Should().BeEquivalentTo(expected);
    }

    // AC-8.8 (test gap): cancellation-token coverage for FileSystemObjectStore.
    [Fact]
    public async Task WriteAsync_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _store.WriteAsync("key.dat", new MemoryStream([0x01]), cts.Token)
                                          .ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task ExistsAsync_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _store.ExistsAsync("key.dat", cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadAsync_CancelledToken_ThrowsOperationCanceled()
    {
        // Write first so the file exists before testing cancellation on read.
        await _store.WriteAsync("key.dat", new MemoryStream([0x01])).ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _store.ReadAsync("key.dat", cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task DeleteAsync_CancelledToken_ThrowsOperationCanceled()
    {
        // Write first so the file exists before testing cancellation on delete.
        await _store.WriteAsync("key.dat", new MemoryStream([0x01])).ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _store.DeleteAsync("key.dat", cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    // Keys are opaque and case-sensitive (the in-memory sibling keys an ordinal dictionary), so
    // case-distinct keys must stay independent objects even on a case-INSENSITIVE volume
    // (default macOS APFS, NTFS), where a literal key-to-path mapping folds them onto one file.
    [Theory]
    [InlineData("report.bin", "Report.bin")]       // leaf segment differs only in case
    [InlineData("data/x.bin", "Data/x.bin")]       // directory segment differs only in case
    [InlineData("a", "A/b")]                       // a file and a directory that fold together
    public async Task CaseDistinctKeys_KeepIndependentObjects(string first, string second)
    {
        await _store.WriteAsync(first, new MemoryStream("FIRST"u8.ToArray())).ConfigureAwait(true);
        await _store.WriteAsync(second, new MemoryStream("SECOND"u8.ToArray())).ConfigureAwait(true);

        (await ReadAllAsync(first).ConfigureAwait(true)).Should()
            .Be("FIRST", "writing a case-distinct key must not overwrite another key's object");
        (await ReadAllAsync(second).ConfigureAwait(true)).Should().Be("SECOND");

        await _store.DeleteAsync(second).ConfigureAwait(true);

        (await _store.ExistsAsync(first).ConfigureAwait(true)).Should()
            .BeTrue("deleting a case-distinct key must not delete another key's object");
        (await ReadAllAsync(first).ConfigureAwait(true)).Should().Be("FIRST");
        (await _store.ExistsAsync(second).ConfigureAwait(true)).Should().BeFalse();
    }

    // Keys are opaque identifiers: a key and a key that has it as a path prefix must coexist,
    // exactly as they do in the in-memory sibling.
    [Theory]
    [InlineData("a", "a/b")]
    [InlineData("a/b", "a")]
    [InlineData("x/part-0", "x")]
    [InlineData("a/b", "a/b/c")]
    public async Task PrefixRelatedKeys_CoexistInEitherWriteOrder_MatchingInMemorySibling(string first, string second)
    {
        var memory = new InMemoryObjectStore();
        foreach (IObjectStore store in new IObjectStore[] { _store, memory })
        {
            await store.WriteAsync(first, new MemoryStream("FIRST"u8.ToArray())).ConfigureAwait(true);
            await store.WriteAsync(second, new MemoryStream("SECOND"u8.ToArray())).ConfigureAwait(true);

            (await store.ExistsAsync(first).ConfigureAwait(true)).Should().BeTrue(store.GetType().Name);
            (await store.ExistsAsync(second).ConfigureAwait(true)).Should().BeTrue(store.GetType().Name);

            await store.DeleteAsync(second).ConfigureAwait(true);
            (await store.ExistsAsync(second).ConfigureAwait(true)).Should().BeFalse();
            (await store.ExistsAsync(first).ConfigureAwait(true)).Should().BeTrue();

            // The shorter key must stay writable after the longer one is deleted (no stale
            // directory left squatting on its name).
            await store.DeleteAsync(first).ConfigureAwait(true);
            await store.WriteAsync(first, new MemoryStream("AGAIN"u8.ToArray())).ConfigureAwait(true);
            await store.WriteAsync(second, new MemoryStream("AGAIN2"u8.ToArray())).ConfigureAwait(true);
            (await store.ExistsAsync(first).ConfigureAwait(true)).Should().BeTrue();

            await store.DeleteAsync(first).ConfigureAwait(true);
            await store.DeleteAsync(second).ConfigureAwait(true);
        }
    }

    private async Task<string?> ReadAllAsync(string key)
    {
        await using var stream = await _store.ReadAsync(key).ConfigureAwait(true);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(true);
    }
}
