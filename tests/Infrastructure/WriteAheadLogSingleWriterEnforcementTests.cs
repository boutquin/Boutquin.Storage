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
/// Single-writer enforcement tests for <see cref="WriteAheadLog{TKey, TValue}"/>: exactly one
/// live instance may own a WAL directory. Sibling of
/// <c>EventLogMultiWriterCoordinationTests</c> — same multi-writer stale-offset corruption
/// class (a second DI container in-process, or a second process, over one path), different
/// remedy: the WAL's long-lived append stream cannot coordinate with a peer (sequence numbers
/// are engine-assigned, rotation state is per-instance), so a second live instance fails fast
/// at construction instead of interleaving.
///
/// <para>Regression anchor (2026-07-14/16, MarketData ingestion log): two live appenders over
/// one file each pinned an independent append offset; a writer's frame landed at a stale
/// offset, burying the other writer's committed frame — readers then failed with mid-log
/// corruption. The WAL shares the vulnerable shape (long-lived <c>FileMode.Append</c> stream,
/// <c>FileShare.ReadWrite</c>); these tests pin the fail-fast guard that keeps the shape safe.</para>
/// </summary>
public sealed class WriteAheadLogSingleWriterEnforcementTests : IDisposable
{
    private readonly string _dir;

    public WriteAheadLogSingleWriterEnforcementTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wal-single-writer-tests", Guid.NewGuid().ToString("N"));
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
    public void SecondLiveInstance_OverSameDirectory_FailsFastAtConstruction()
    {
        using var first = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_dir);

        var constructSecond = () =>
            new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_dir);

        constructSecond.Should().Throw<IOException>(
                "a second live WAL over the same directory would pin a stale append offset and " +
                "overwrite the first instance's committed frames")
            .WithMessage("*already has a live writer*");
    }

    [Fact]
    public async Task NewInstance_AfterDispose_AcquiresTheDirectoryAndResumesAppends()
    {
        using (var first = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_dir))
        {
            await first.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), 1UL);
        }

        // Dispose released the writer lock, so the successor (a clean restart, or the next
        // container after a graceful shutdown) constructs, recovers, and appends normally.
        using (var second = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_dir))
        {
            var recovered = await second.RecoverAsync();
            recovered.Should().ContainSingle().Which.Key.Value.Should().Be(1);

            await second.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"), 2UL);
        }

        using var third = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_dir);
        var final = await third.RecoverAsync();
        final.Select(e => e.Key.Value).Should().Equal(1, 2);
    }

    [Fact]
    public async Task WriterLockSidecar_IsNeverMistakenForASegment()
    {
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_dir);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), 1UL);

        File.Exists(Path.Combine(_dir, "wal.lock")).Should().BeTrue(
            "the constructor materialises the sidecar writer-lock file");

        // Segment discovery (recovery, truncation, rotation) must never treat the sidecar as a
        // segment — its name sits outside the wal_NNNNNN.log shape by design.
        wal.GetAllSegmentPaths().Should().OnlyContain(p => Path.GetFileName(p) != "wal.lock");

        // Full truncation deletes every segment but leaves the sidecar in place (unlinking it
        // would hand a concurrent opener a lock on a dead inode).
        await wal.TruncateAsync();
        Directory.GetFiles(_dir, "wal_*.log").Should().BeEmpty();
        File.Exists(Path.Combine(_dir, "wal.lock")).Should().BeTrue();
    }
}
