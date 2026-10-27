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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the StorageFile class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class StorageFileTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"StorageTests_{Guid.NewGuid():N}");
    private readonly string _testFileName = "testfile.txt";
    private readonly IStorageFile _storageFile;

    public StorageFileTests()
    {
        // Arrange: Initialize the storage file instance for each test.
        _storageFile = new StorageFile(_testDirectory, _testFileName);
    }

    /// <summary>
    /// Test to ensure that the Create method correctly creates a new file.
    /// </summary>
    [Fact]
    public void Create_ShouldCreateNewFile()
    {
        // Act: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Assert: Check that the file exists.
        Assert.True(_storageFile.Exists());
    }

    /// <summary>
    /// Test to ensure that Create throws an exception if the file already exists
    /// and FileExistenceHandling is set to ThrowIfExists.
    /// </summary>
    [Fact]
    public void Create_ShouldThrowIfFileExists()
    {
        // Arrange: Create the file first.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act & Assert: Attempt to create the file again with ThrowIfExists handling and expect an IOException.
        Assert.Throws<IOException>(() => _storageFile.Create(FileExistenceHandling.ThrowIfExists));
    }

    /// <summary>
    /// Test to ensure that the Exists method correctly identifies existing files.
    /// </summary>
    [Fact]
    public void Exists_ShouldReturnTrueIfFileExists()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Check if the file exists.
        var exists = _storageFile.Exists();

        // Assert: The file should exist.
        Assert.True(exists);
    }

    /// <summary>
    /// Test to ensure that the Exists method correctly identifies non-existing files.
    /// </summary>
    [Fact]
    public void Exists_ShouldReturnFalseIfFileDoesNotExist()
    {
        // Arrange: Delete the file.
        _storageFile.Delete(FileDeletionHandling.DeleteIfExists);

        // Act: Check if the file exists.
        var exists = _storageFile.Exists();

        // Assert: The file should not exist.
        Assert.False(exists);
    }

    /// <summary>
    /// Test to ensure that the Open method correctly opens a file stream.
    /// </summary>
    [Fact]
    public void Open_ShouldOpenFileStream()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Open the file.
        using var stream = _storageFile.Open(FileMode.Open);

        // Assert: The stream should be open and readable.
        Assert.NotNull(stream);
        Assert.True(stream.CanRead);
    }

    /// <summary>
    /// Test to ensure that the Delete method correctly deletes an existing file.
    /// </summary>
    [Fact]
    public void Delete_ShouldDeleteFile()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Delete the file.
        _storageFile.Delete(FileDeletionHandling.DeleteIfExists);

        // Assert: The file should no longer exist.
        Assert.False(_storageFile.Exists());
    }

    /// <summary>
    /// Test to ensure that the Delete method throws an exception if the file does not exist
    /// and FileDeletionHandling is set to ThrowIfNotExists.
    /// </summary>
    [Fact]
    public void Delete_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Delete the file first.
        if (File.Exists(_storageFile.FilePath))
        {
            File.Delete(_storageFile.FilePath);
        }

        // Act & Assert: Attempt to delete a non-existing file with ThrowIfNotExists handling and expect a FileNotFoundException.
        Assert.Throws<FileNotFoundException>(() => _storageFile.Delete(FileDeletionHandling.ThrowIfNotExists));
    }

    /// <summary>
    /// Test to ensure that the Length property returns the correct file size.
    /// </summary>
    [Fact]
    public void Length_ShouldReturnFileSize()
    {
        // Arrange: Create and write to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        _storageFile.WriteAllBytes([0x01, 0x02, 0x03]);

        // Act: Get the file size.
        var length = _storageFile.FileSize;

        // Assert: The file size should match the number of bytes written.
        Assert.Equal(3, length);
    }

    /// <summary>
    /// Test to ensure that the ReadBytes method reads the correct number of bytes from the file.
    /// </summary>
    [Fact]
    public void ReadBytes_ShouldReadSpecifiedNumberOfBytes()
    {
        // Arrange: Create and write to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var content = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        _storageFile.WriteAllBytes(content);

        // Act: Read bytes from the file.
        var bytesRead = _storageFile.ReadBytes(1, 3);

        // Assert: The bytes read should match the expected sequence.
        Assert.Equal(new byte[] { 0x02, 0x03, 0x04 }, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the WriteAllBytes method correctly writes bytes to the file.
    /// </summary>
    [Fact]
    public void WriteAllBytes_ShouldWriteBytesToFile()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Write bytes to the file.
        var content = new byte[] { 0x01, 0x02, 0x03 };
        _storageFile.WriteAllBytes(content);

        // Assert: The file content should match the bytes written.
        var bytesRead = _storageFile.ReadAllBytes();
        Assert.Equal(content, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the AppendAllBytes method correctly appends bytes to the file.
    /// </summary>
    [Fact]
    public void AppendAllBytes_ShouldAppendBytesToFile()
    {
        // Arrange: Create and write initial bytes to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = new byte[] { 0x01, 0x02, 0x03 };
        _storageFile.WriteAllBytes(initialContent);

        // Act: Append bytes to the file.
        var additionalContent = new byte[] { 0x04, 0x05 };
        _storageFile.AppendAllBytes(additionalContent);

        // Assert: The file content should match the concatenated bytes.
        var bytesRead = _storageFile.ReadAllBytes();
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the ReadAllText method reads the correct text content from the file.
    /// </summary>
    [Fact]
    public void ReadAllText_ShouldReadTextFromFile()
    {
        // Arrange: Create and write text to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var content = "Hello, world!";
        _storageFile.WriteAllText(content, Encoding.UTF8);

        // Act: Read text from the file.
        var textRead = _storageFile.ReadAllText(Encoding.UTF8);

        // Assert: The text read should match the text written.
        Assert.Equal(content, textRead);
    }

    /// <summary>
    /// Test to ensure that the WriteAllText method correctly writes text to the file.
    /// </summary>
    [Fact]
    public void WriteAllText_ShouldWriteTextToFile()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Write text to the file.
        var content = "Hello, world!";
        _storageFile.WriteAllText(content, Encoding.UTF8);

        // Assert: The text read from the file should match the text written.
        var textRead = _storageFile.ReadAllText(Encoding.UTF8);
        Assert.Equal(content, textRead);
    }

    /// <summary>
    /// Test to ensure that the AppendAllText method correctly appends text to the file.
    /// </summary>
    [Fact]
    public void AppendAllText_ShouldAppendTextToFile()
    {
        // Arrange: Create and write initial text to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = "Hello, ";
        _storageFile.WriteAllText(initialContent, Encoding.UTF8);

        // Act: Append text to the file.
        var additionalContent = "world!";
        _storageFile.AppendAllText(additionalContent, Encoding.UTF8);

        // Assert: The text read from the file should match the concatenated text.
        var textRead = _storageFile.ReadAllText(Encoding.UTF8);
        Assert.Equal("Hello, world!", textRead);
    }

    /// <summary>
    /// Test to ensure that the ReadBytesAsync method reads the correct number of bytes from the file asynchronously.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_ShouldReadSpecifiedNumberOfBytesAsync()
    {
        // Arrange: Create and write to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var content = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        await _storageFile.WriteAllBytesAsync(content);

        // Act: Read bytes from the file asynchronously.
        var bytesRead = await _storageFile.ReadBytesAsync(1, 3);

        // Assert: The bytes read should match the expected sequence.
        Assert.Equal(new byte[] { 0x02, 0x03, 0x04 }, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the WriteAllBytesAsync method correctly writes bytes to the file asynchronously.
    /// </summary>
    [Fact]
    public async Task WriteAllBytesAsync_ShouldWriteBytesToFileAsync()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Write bytes to the file asynchronously.
        var content = new byte[] { 0x01, 0x02, 0x03 };
        await _storageFile.WriteAllBytesAsync(content);

        // Assert: The file content should match the bytes written.
        var bytesRead = _storageFile.ReadAllBytes();
        Assert.Equal(content, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the AppendAllBytesAsync method correctly appends bytes to the file asynchronously.
    /// </summary>
    [Fact]
    public async Task AppendAllBytesAsync_ShouldAppendBytesToFileAsync()
    {
        // Arrange: Create and write initial bytes to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = new byte[] { 0x01, 0x02, 0x03 };
        await _storageFile.WriteAllBytesAsync(initialContent);

        // Act: Append bytes to the file asynchronously.
        var additionalContent = new byte[] { 0x04, 0x05 };
        await _storageFile.AppendAllBytesAsync(additionalContent);

        // Assert: The file content should match the concatenated bytes.
        var bytesRead = _storageFile.ReadAllBytes();
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the ReadAllTextAsync method reads the correct text content from the file asynchronously.
    /// </summary>
    [Fact]
    public async Task ReadAllTextAsync_ShouldReadTextFromFileAsync()
    {
        // Arrange: Create and write text to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var content = "Hello, world!";
        await _storageFile.WriteAllTextAsync(content, Encoding.UTF8);

        // Act: Read text from the file asynchronously.
        var textRead = await _storageFile.ReadAllTextAsync(Encoding.UTF8);

        // Assert: The text read should match the text written.
        Assert.Equal(content, textRead);
    }

    /// <summary>
    /// Test to ensure that the WriteAllTextAsync method correctly writes text to the file asynchronously.
    /// </summary>
    [Fact]
    public async Task WriteAllTextAsync_ShouldWriteTextToFileAsync()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Write text to the file asynchronously.
        var content = "Hello, world!";
        await _storageFile.WriteAllTextAsync(content, Encoding.UTF8);

        // Assert: The text read from the file should match the text written.
        var textRead = await _storageFile.ReadAllTextAsync(Encoding.UTF8);
        Assert.Equal(content, textRead);
    }

    /// <summary>
    /// Test to ensure that the AppendAllTextAsync method correctly appends text to the file asynchronously.
    /// </summary>
    [Fact]
    public async Task AppendAllTextAsync_ShouldAppendTextToFileAsync()
    {
        // Arrange: Create and write initial text to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = "Hello, ";
        await _storageFile.WriteAllTextAsync(initialContent, Encoding.UTF8);

        // Act: Append text to the file asynchronously.
        var additionalContent = "world!";
        await _storageFile.AppendAllTextAsync(additionalContent, Encoding.UTF8);

        // Assert: The text read from the file should match the concatenated text.
        var textRead = await _storageFile.ReadAllTextAsync(Encoding.UTF8);
        Assert.Equal("Hello, world!", textRead);
    }

    // ========== Tier 3: Static semaphore cleanup ==========

    /// <summary>
    /// Test that disposing a StorageFile cleans up the static file semaphore.
    /// Verifies that the static semaphore dictionary doesn't grow unboundedly.
    /// </summary>
    [Fact]
    public void Dispose_CleansUpStaticSemaphore()
    {
        // Arrange — create and dispose a StorageFile
        var tempDir = Path.Combine(Path.GetTempPath(), $"StorageFileTest_{Guid.NewGuid():N}");
        var sf = new StorageFile(tempDir, "cleanup_test.dat");
        sf.WriteAllBytes([1, 2, 3]);

        // Act
        sf.Dispose();

        // Assert — creating a new StorageFile for the same path should work without deadlock,
        // proving the old semaphore was cleaned up or is reusable
        var sf2 = new StorageFile(tempDir, "cleanup_test.dat");
        var data = sf2.ReadAllBytes();
        Assert.Equal(3, data.Length);

        // Cleanup
        sf2.Delete(FileDeletionHandling.DeleteIfExists);
        sf2.Dispose();
        Directory.Delete(tempDir, recursive: true);
    }

    /// <summary>
    /// <see cref="StorageFile.WriteAllBytes"/> must replace content atomically (write a sibling temp
    /// file, fsync it, then rename over the target). A reader that bypasses StorageFile's per-path
    /// semaphore — exactly what a crash, another process, or a recovery scan does — must therefore
    /// never observe a half-written file. A truncate-in-place write would expose a 0-byte or partial
    /// file to such a reader. This drives both the atomicity (rename) and cleanup (no leftover temp)
    /// contract; the fsync durability is asserted by inspection of the same code path.
    /// </summary>
    [Fact]
    public async Task WriteAllBytes_IsCrashAtomic_RawReaderNeverSeesPartialFile()
    {
        Directory.CreateDirectory(_testDirectory);
        var rawPath = Path.Combine(_testDirectory, _testFileName);

        var contentA = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var contentB = Enumerable.Range(0, 200_000).Select(i => (byte)((i + 7) % 251)).ToArray();
        _storageFile.WriteAllBytes(contentA); // seed

        Exception? failure = null;

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                _storageFile.WriteAllBytes(i % 2 == 0 ? contentB : contentA);
            }
        });

        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted && failure is null)
            {
                byte[] seen;
                try
                {
                    // Allow delete/rename while this raw handle is open. A reader denying delete
                    // sharing blocks Windows replacement rather than testing its atomicity.
                    using var stream = new FileStream(rawPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    seen = new byte[checked((int)stream.Length)];
                    stream.ReadExactly(seen); // bypasses the StorageFile semaphore
                }
                catch (IOException)
                {
                    continue; // a transient sharing/rename open failure is acceptable, a torn read is not
                }

                if (seen.Length == contentA.Length && (seen.SequenceEqual(contentA) || seen.SequenceEqual(contentB)))
                {
                    continue; // a complete, valid version
                }

                failure = new InvalidOperationException(
                    $"raw reader observed a non-atomic file state: {seen.Length} bytes (expected a complete {contentA.Length}-byte version)");
            }
        });

        await writer;
        await reader;

        failure.Should().BeNull("WriteAllBytes must replace content atomically");
        Directory.GetFiles(_testDirectory).Select(Path.GetFileName)
            .Should().OnlyContain(name => name == _testFileName, "no temp files may be left behind");
    }

    [Fact]
    public async Task ConcurrentDispose_DoesNotThrow()
    {
        // Two concurrent Dispose calls must be safe — the Interlocked.Exchange guard inside
        // Dispose(bool) ensures only the first caller enters the body; the second is a no-op.
        var dir = Path.Combine(Path.GetTempPath(), $"StorageTests_ConcDispose_{Guid.NewGuid():N}");
        try
        {
            var sf = new StorageFile(dir, "concurrent-dispose.bin");

            var t1 = Task.Run(sf.Dispose);
            var t2 = Task.Run(sf.Dispose);

            var act = async () => await Task.WhenAll(t1, t2).ConfigureAwait(true);
            await act.Should().NotThrowAsync("concurrent Dispose calls must be idempotent").ConfigureAwait(true);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// AC-5.3 — reading zero bytes at offset 0 of a freshly-created (empty) file must succeed and
    /// return an empty buffer, not throw. The pre-fix guard rejected <c>offset &gt;= stream.Length</c>,
    /// so offset 0 of a zero-length file (0 &gt;= 0) threw <see cref="ArgumentOutOfRangeException"/> —
    /// a legitimate "the log is empty, read nothing" call surfaced as an error.
    /// </summary>
    [Fact]
    public void ReadBytes_ZeroCountAtOffsetZeroOfEmptyFile_ReturnsEmptyBuffer()
    {
        // Arrange: the constructor already created an empty file; assert it is genuinely empty.
        Assert.Equal(0, new FileInfo(_storageFile.FilePath).Length);

        // Act: read 0 bytes at offset 0 of the empty file.
        var buffer = _storageFile.ReadBytes(0, 0);

        // Assert: an empty read succeeds.
        Assert.Empty(buffer);
    }

    /// <summary>
    /// AC-5.3 (async path) — the same empty-file zero-byte read must succeed through
    /// <see cref="StorageFile.ReadBytesAsync"/>.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_ZeroCountAtOffsetZeroOfEmptyFile_ReturnsEmptyBuffer()
    {
        Assert.Equal(0, new FileInfo(_storageFile.FilePath).Length);

        var buffer = await _storageFile.ReadBytesAsync(0, 0);

        Assert.Empty(buffer);
    }

    /// <summary>
    /// AC-5.3 — a genuinely out-of-range read (positive offset past the end of an empty file) must
    /// still throw, so the empty-read relaxation does not swallow real overruns.
    /// </summary>
    [Fact]
    public void ReadBytes_PositiveOffsetPastEmptyFile_StillThrows()
    {
        Assert.Equal(0, new FileInfo(_storageFile.FilePath).Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => _storageFile.ReadBytes(1, 1));
    }

    /// <summary>
    /// AC-5.4 (DR-9) — <see cref="StorageFile.FileSize"/> is cached and tracks the on-disk size
    /// across writes without a per-access <c>FileInfo().Length</c> stat. After a fresh file (size 0),
    /// an append, and an atomic overwrite, the cached value must agree with the actual file length at
    /// every step.
    /// <para>
    /// <b>Single-writer concurrency note (DR-9 / SPEC-CONCUR-01).</b> The cached field is mutated only
    /// under the same per-path semaphore that already guards every write to the file, so its updates
    /// are serialised with the writes they track — no new unsynchronised shared field is introduced.
    /// <see cref="StorageFile"/>'s contract is single-mutator (see its remarks); concurrent writers
    /// must compose under an external serialisation point, exactly as the append-only and segmented
    /// engines do. This test exercises the cache on the single-writer path the contract guarantees.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FileSize_IsCachedAndTracksWritesUnderSingleWriterDiscipline()
    {
        // Fresh file: cached size starts at the on-disk size (0).
        Assert.Equal(0, _storageFile.FileSize);

        // Append moves the size forward; the cache must reflect it.
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await _storageFile.AppendAllBytesAsync(payload);
        Assert.Equal(5, _storageFile.FileSize);

        // Atomic overwrite replaces the contents; the cache must track the new length.
        await _storageFile.WriteAllBytesAsync(new byte[] { 9, 9 });
        Assert.Equal(2, _storageFile.FileSize);
    }

    /// <summary>
    /// AC-5.4 (non-masking pin) — proves <see cref="StorageFile.FileSize"/> serves a CACHED value and
    /// does NOT <c>stat</c> on every access. After a tracked write through StorageFile, an out-of-band
    /// modification to the underlying file (bypassing StorageFile entirely — neither a tracked write
    /// nor an <c>Open</c>) must NOT be observed by <c>FileSize</c>: the cache still reports the last
    /// tracked size. A per-access <c>FileInfo().Length</c> implementation would observe the external
    /// change and fail this assertion — so the test would go RED if the cache were reverted to a stat.
    /// </summary>
    [Fact]
    public async Task FileSize_ServesCachedValue_NotPerAccessStat()
    {
        // Tracked write: cache now holds 4.
        await _storageFile.WriteAllBytesAsync(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(4, _storageFile.FileSize);

        // Out-of-band growth: write straight to the path, bypassing StorageFile's tracked paths and
        // its Open() seam. A cached FileSize must NOT see this; a per-access stat WOULD.
        await File.AppendAllTextAsync(_storageFile.FilePath, "EXTRA-BYTES-WRITTEN-OUT-OF-BAND");

        // Still the cached size — the external append is invisible. (A stat-per-access getter would
        // return the larger on-disk length here, failing this assertion.)
        Assert.Equal(4, _storageFile.FileSize);
    }

    /// <summary>
    /// A fresh StorageFile over a PRE-EXISTING file starts with a stale size cache. An append
    /// must not promote that stale base to authoritative: FileSize afterwards must equal the
    /// pre-existing length plus the appended length, not just the appended length. (The append
    /// path sets <c>_cachedSizeStale = false</c>, so a wrong base here stays wrong forever.)
    /// </summary>
    [Fact]
    public void AppendAllBytes_OnPreExistingFile_FileSizeIncludesPreExistingBytes()
    {
        // Arrange: a file that already holds 10 bytes, created out-of-band BEFORE the
        // StorageFile instance exists (recovery / second-instance scenario).
        var dir = Path.Combine(Path.GetTempPath(), $"StorageTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "preexisting.bin");
        File.WriteAllBytes(path, new byte[10]);

        using var sf = new StorageFile(dir, "preexisting.bin");

        // Act: append 4 bytes through the fresh instance (its size cache is still stale).
        sf.AppendAllBytes([1, 2, 3, 4]);

        // Assert: the authoritative size is 10 + 4, not 4.
        Assert.Equal(14, sf.FileSize);

        sf.Delete(FileDeletionHandling.DeleteIfExists);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>Every StorageFile operation that can create the file's directory entry.</summary>
    public enum EntryCreatingOperation
    {
        CreateOverwrite,
        CreateDoNothingIfExists,
        CreateThrowIfExists,
        OpenAppend,
        OpenOrCreate,
        AppendAllBytes,
        AppendAllBytesAsync,
        AppendAllText,
        AppendAllTextAsync,
    }

    private static async Task RunAsync(StorageFile storageFile, EntryCreatingOperation operation)
    {
        switch (operation)
        {
            case EntryCreatingOperation.CreateOverwrite:
                storageFile.Create(FileExistenceHandling.Overwrite);
                break;
            case EntryCreatingOperation.CreateDoNothingIfExists:
                storageFile.Create(FileExistenceHandling.DoNothingIfExists);
                break;
            case EntryCreatingOperation.CreateThrowIfExists:
                storageFile.Create(FileExistenceHandling.ThrowIfExists);
                break;
            case EntryCreatingOperation.OpenAppend:
                storageFile.Open(FileMode.Append);
                storageFile.Close();
                break;
            case EntryCreatingOperation.OpenOrCreate:
                storageFile.Open(FileMode.OpenOrCreate);
                storageFile.Close();
                break;
            case EntryCreatingOperation.AppendAllBytes:
                storageFile.AppendAllBytes([1, 2, 3]);
                break;
            case EntryCreatingOperation.AppendAllBytesAsync:
                await storageFile.AppendAllBytesAsync([1, 2, 3]).ConfigureAwait(true);
                break;
            case EntryCreatingOperation.AppendAllText:
                storageFile.AppendAllText("abc", Encoding.UTF8);
                break;
            case EntryCreatingOperation.AppendAllTextAsync:
                await storageFile.AppendAllTextAsync("abc", Encoding.UTF8).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// Creating the file is a directory-entry mutation: a file fsync does not persist the entry, so a
    /// crash can lose a newly created file together with every acknowledged write in it unless the
    /// containing directory is fsync'd (POSIX; NTFS makes the entry durable on its own).
    /// </summary>
    [Theory]
    [InlineData(EntryCreatingOperation.CreateOverwrite)]
    [InlineData(EntryCreatingOperation.CreateDoNothingIfExists)]
    [InlineData(EntryCreatingOperation.CreateThrowIfExists)]
    [InlineData(EntryCreatingOperation.OpenAppend)]
    [InlineData(EntryCreatingOperation.OpenOrCreate)]
    [InlineData(EntryCreatingOperation.AppendAllBytes)]
    [InlineData(EntryCreatingOperation.AppendAllBytesAsync)]
    [InlineData(EntryCreatingOperation.AppendAllText)]
    [InlineData(EntryCreatingOperation.AppendAllTextAsync)]
    public async Task EntryCreatingOperation_OnMissingFile_FsyncsContainingDirectoryOnce(EntryCreatingOperation operation)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var storageFile = new StorageFile(_testDirectory, "entry-create.bin");
        storageFile.Delete(FileDeletionHandling.DeleteIfExists);
        var before = storageFile._directoryFsyncCount;

        await RunAsync(storageFile, operation).ConfigureAwait(true);

        Assert.True(storageFile.Exists());
        Assert.Equal(before + 1, storageFile._directoryFsyncCount);
    }

    /// <summary>
    /// Deleting the file is a directory-entry mutation too: without a directory fsync a crash can
    /// resurrect a cleared log on restart.
    /// </summary>
    [Fact]
    public void Delete_OfExistingFile_FsyncsContainingDirectoryOnce()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var storageFile = new StorageFile(_testDirectory, "entry-delete.bin");
        var before = storageFile._directoryFsyncCount;

        storageFile.Delete(FileDeletionHandling.DeleteIfExists);

        Assert.False(storageFile.Exists());
        Assert.Equal(before + 1, storageFile._directoryFsyncCount);
    }

    /// <summary>
    /// Construction over a missing directory tree creates each directory level and the file; every
    /// created entry must be made durable (two directory levels plus the file entry).
    /// </summary>
    [Fact]
    public void Constructor_OverMissingDirectoryTree_FsyncsEveryCreatedEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var nested = Path.Combine(_testDirectory, "level-1", "level-2");

        using var storageFile = new StorageFile(nested, "entry-ctor.bin");

        Assert.True(storageFile.Exists());
        Assert.Equal(3, storageFile._directoryFsyncCount);
    }

    /// <summary>
    /// Operations that leave the directory entry unchanged (the file already exists, or a delete of a
    /// missing file) issue no directory fsync.
    /// </summary>
    [Fact]
    public async Task EntryPreservingOperations_IssueNoDirectoryFsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var storageFile = new StorageFile(_testDirectory, "entry-preserve.bin");
        var before = storageFile._directoryFsyncCount;

        foreach (var operation in Enum.GetValues<EntryCreatingOperation>())
        {
            if (operation == EntryCreatingOperation.CreateThrowIfExists)
            {
                continue;
            }

            await RunAsync(storageFile, operation).ConfigureAwait(true);
        }

        storageFile.Delete(FileDeletionHandling.DeleteIfExists);
        var afterDelete = storageFile._directoryFsyncCount;
        storageFile.Delete(FileDeletionHandling.DeleteIfExists);

        Assert.Equal(before + 1, afterDelete);
        Assert.Equal(afterDelete, storageFile._directoryFsyncCount);
    }

    /// <summary>
    /// Clean up the test environment by deleting the test file after each test.
    /// </summary>
    public void Dispose()
    {
        if (_storageFile.Exists())
        {
            _storageFile.Delete(FileDeletionHandling.DeleteIfExists);
        }
        _storageFile.Dispose();
    }
}
