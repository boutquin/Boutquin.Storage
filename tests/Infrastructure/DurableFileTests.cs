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
/// Tests for <see cref="DurableFile.AtomicReplace"/>.
/// </summary>
/// <remarks>
/// Windows skips the directory-fsync barrier; POSIX uses the native fsync call. These tests
/// exercise observable publication and replacement, including concurrent writers. They do not
/// establish physical power-loss durability on either platform.
/// </remarks>
public sealed class DurableFileTests : IDisposable
{
    private readonly string _dir;

    public DurableFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "durable-file-tests", Guid.NewGuid().ToString("N"));
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
    public void AtomicReplace_WhenTargetDoesNotExist_RenamesSourceToTarget()
    {
        var source = Path.Combine(_dir, "source.tmp");
        var target = Path.Combine(_dir, "target.dat");
        File.WriteAllText(source, "hello");

        DurableFile.AtomicReplace(source, target);

        File.Exists(source).Should().BeFalse("source is consumed by the rename");
        File.Exists(target).Should().BeTrue();
        File.ReadAllText(target).Should().Be("hello");
    }

    [Fact]
    public void AtomicReplace_WhenTargetExists_OverwritesTarget()
    {
        var source = Path.Combine(_dir, "source.tmp");
        var target = Path.Combine(_dir, "target.dat");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");

        DurableFile.AtomicReplace(source, target);

        File.Exists(source).Should().BeFalse();
        File.ReadAllText(target).Should().Be("new");
    }

    [Fact]
    public void AtomicReplace_WhenSourceDoesNotExist_Throws()
    {
        var source = Path.Combine(_dir, "missing.tmp");
        var target = Path.Combine(_dir, "target.dat");

        var act = () => DurableFile.AtomicReplace(source, target);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void AtomicReplace_PreservesBinaryContent()
    {
        var source = Path.Combine(_dir, "source.tmp");
        var target = Path.Combine(_dir, "target.dat");
        var payload = new byte[4096];
        new Random(42).NextBytes(payload);
        File.WriteAllBytes(source, payload);

        DurableFile.AtomicReplace(source, target);

        var read = File.ReadAllBytes(target);
        read.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void WindowsRenameInformation_MatchesNativeAbiAndPreservesUtf16CodeUnits()
    {
        var targets = new[] { @"C:\storage\target.dat", @"\\?\C:\storage\é-😀.dat", @"C:\storage\" + (char)0xD800 + ".dat" };
        foreach (var target in targets)
        {
            var bytes = DurableFile.BuildWindowsRenameInformation(target);
            var rootOffset = IntPtr.Size == 8 ? 8 : 4;
            var nameLengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = nameLengthOffset + sizeof(uint);
            Assert.Equal(3u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            Assert.All(bytes.AsSpan(rootOffset, IntPtr.Size).ToArray(), value => Assert.Equal(0, value));
            Assert.Equal((uint)(target.Length * sizeof(char)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(nameLengthOffset)));
            for (var i = 0; i < target.Length; i++)
            {
                Assert.Equal((ushort)target[i], System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(nameOffset + i * sizeof(char), sizeof(char))));
            }
            Assert.All(bytes.AsSpan(nameOffset + target.Length * sizeof(char)).ToArray(), value => Assert.Equal(0, value));
        }
    }

    [Theory]
    [InlineData("pinned.dat", false)]
    [InlineData("é-😀.dat", false)]
    [InlineData("long.dat", true)]
    public void AtomicReplace_WithPinnedOldReader_PreservesOldHandleAndPublishesNewVersions(string fileName, bool longPath)
    {
        var folder = _dir;
        if (longPath)
        {
            for (var i = 0; i < 8; i++)
            {
                folder = Path.Combine(folder, new string('d', 40) + i);
            }
            Directory.CreateDirectory(folder);
            Assert.True(folder.Length > 260);
        }
        var target = Path.Combine(folder, fileName);
        var original = Enumerable.Repeat((byte)0x11, 4096).ToArray();
        File.WriteAllBytes(target, original);
        using var pinned = new FileStream(target, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        for (var generation = 1; generation <= 4; generation++)
        {
            var source = Path.Combine(folder, $"generation-{generation}.tmp");
            var next = Enumerable.Repeat((byte)(generation + 0x20), 4096).ToArray();
            File.WriteAllBytes(source, next);

            DurableFile.AtomicReplace(source, target);

            Assert.False(File.Exists(source));
            Assert.Equal(next, File.ReadAllBytes(target));
            pinned.Position = 0;
            var previous = new byte[original.Length];
            pinned.ReadExactly(previous);
            Assert.Equal(original, previous);
        }
    }

    [Fact]
    public async Task AtomicReplace_ConcurrentWriters_AllPublishCompleteVersions()
    {
        var target = Path.Combine(_dir, "concurrent.dat");
        var versions = Enumerable.Range(0, 24)
            .Select(i => Enumerable.Repeat((byte)(i + 1), 32_768).ToArray()).ToArray();
        var sources = versions.Select((bytes, index) =>
        {
            var path = Path.Combine(_dir, $"source-{index}.tmp");
            File.WriteAllBytes(path, bytes);
            return path;
        }).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = sources.Select((source, index) => Task.Run(async () =>
        {
            await start.Task.ConfigureAwait(true);
            var destination = index % 2 == 0 ? target
                : Path.Combine(_dir, ".", OperatingSystem.IsWindows() ? "CONCURRENT.DAT" : "concurrent.dat");
            DurableFile.AtomicReplace(source, destination);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(writers).ConfigureAwait(true);

        var actual = await File.ReadAllBytesAsync(target).ConfigureAwait(true);
        Assert.Contains(versions, version => version.SequenceEqual(actual));
        Assert.All(sources, source => Assert.False(File.Exists(source), source));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AtomicReplace_WhenSourceAndTargetAreSame_Throws()
    {
        var path = Path.Combine(_dir, "x.dat");
        File.WriteAllText(path, "content");

        var act = () => DurableFile.AtomicReplace(path, path);

        // Either ArgumentException (explicit guard) or IOException from the OS rename —
        // both satisfy the contract: same-path replace is not a supported operation.
        act.Should().Throw<Exception>().Which.Should().Match(e => e is ArgumentException || e is IOException);
    }
}
