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
using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// WiscKey durability ordering is value-log-fsync BEFORE WAL-pointer-fsync. The recovery-side
/// enforcement of that invariant is the WAL-replay dangling-pointer guard: a replayed pointer
/// frame whose target offset lies past the value-log segment's durable EOF proves the ordering
/// was violated (the WAL frame survived a crash the value bytes did not), and replay must fail
/// loudly with a typed exception INSTEAD of seating a dangling pointer in the memtable — where
/// the loss would surface much later, as a read error on an unrelated code path.
/// </summary>
public sealed class LsmWalReplayDanglingPointerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmDanglingPtr_{Guid.NewGuid():N}");

    public LsmWalReplayDanglingPointerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    [Fact]
    public async Task Reopen_WalPointerPastValueLogEof_ThrowsDanglingValueLogPointerException()
    {
        var walDir = Path.Combine(_dir, "wal");
        var vlogOptions = new ValueLogOptions(ValueLogThresholdBytes: 64);
        var bigValue = new string('v', 500); // above the threshold → pointer path

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
            using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg",
                writeAheadLog: wal, valueLog: vlogOptions);

            await engine.SetAsync(K(1), V(bigValue)); // value-log frame + WAL pointer frame
            // Dispose without flush — key 1 exists as WAL pointer frame + value-log frame only.
        }

        // Simulate the crash-ordering violation the guard detects: the WAL pointer frame is
        // durable but the value-log frame is not. Truncate the segment back to its 8-byte
        // header (a clean EOF — the reopen scan sees an empty segment, not a torn tail).
        var vlogFile = Directory.GetFiles(_dir, "valuelog_*.log", SearchOption.AllDirectories).Single();
        using (var truncate = new FileStream(vlogFile, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            truncate.SetLength(8);
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        var reopen = () => LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg",
            writeAheadLog: wal2, valueLog: vlogOptions);

        // The guard must fail replay loudly — NOT seat the dangling pointer in the memtable.
        await reopen.Should().ThrowAsync<DanglingValueLogPointerException>(
            "a WAL pointer frame past the value-log durable EOF proves the vlog-fsync-before-WAL ordering " +
            "was violated; recovery must fail fast rather than defer the loss to a later read");
    }
}
