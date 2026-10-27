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

using Wal = global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// F-34 — WiscKey <c>AppendPointerAsync</c> recovery round-trip. A WiscKey-integrated LSM logs a
/// large value as a 12-byte <see cref="ValueLogPointer"/> (file index + offset) in the WAL instead
/// of the inline value, so a crash mid-flush can replay the pointer rather than the payload. The
/// frame is <c>(Set, ValueLogPointer)</c>; recovery must round-trip the pointer's exact bytes — a
/// dropped or mis-decoded pointer dangles the recovered memtable entry against the value log. This
/// closes the WAL durability gap for the pointer frame kind (the inline and delete frame kinds are
/// covered elsewhere).
/// </summary>
public sealed class AppendPointerRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"WalAppendPointer_{Guid.NewGuid():N}");

    public AppendPointerRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task AppendPointerAsync_ThenRecover_RoundTripsTheTwelveBytePointer()
    {
        var key = new SerializableWrapper<int>(42);
        var pointer = new ValueLogPointer(FileIndex: 7u, Offset: 0xDEADBEEFUL);

        using (var wal = new Wal(_dir))
        {
            await wal.AppendPointerAsync(key, pointer, sequenceNumber: 11UL).ConfigureAwait(true);
        }

        using var reader = new Wal(_dir);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);

        entries.Should().ContainSingle("exactly one pointer frame was appended");
        var entry = entries[0];
        entry.Kind.Should().Be(EntryKind.Set);
        entry.ValueKind.Should().Be(ValueKind.ValueLogPointer,
            "the frame must recover as a value-log-pointer frame, not an inline value frame");
        entry.Pointer.Should().NotBeNull("a ValueLogPointer frame must recover a non-null pointer");
        entry.Pointer!.Value.FileIndex.Should().Be(7u, "the pointer's file index must survive byte-for-byte");
        entry.Pointer!.Value.Offset.Should().Be(0xDEADBEEFUL, "the pointer's offset must survive byte-for-byte");
        entry.InlineValue.Should().BeNull("a pointer frame carries no inline value");
        entry.SequenceNumber.Should().Be(11UL);
    }

    [Fact]
    public async Task AppendPointer_InterleavedWithInlineAndDelete_AllRecoverWithCorrectKinds()
    {
        var ptr1 = new ValueLogPointer(1u, 100UL);
        var ptr2 = new ValueLogPointer(2u, 999_999UL);

        using (var wal = new Wal(_dir))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("inline"), 1UL).ConfigureAwait(true);
            await wal.AppendPointerAsync(new SerializableWrapper<int>(2), ptr1, 2UL).ConfigureAwait(true);
            await wal.AppendDeleteAsync(new SerializableWrapper<int>(3), 3UL).ConfigureAwait(true);
            await wal.AppendPointerAsync(new SerializableWrapper<int>(4), ptr2, 4UL).ConfigureAwait(true);
        }

        using var reader = new Wal(_dir);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);

        entries.Should().HaveCount(4);
        entries[0].ValueKind.Should().Be(ValueKind.Inline);
        entries[0].InlineValue!.Value.Should().Be("inline");

        entries[1].ValueKind.Should().Be(ValueKind.ValueLogPointer);
        entries[1].Pointer!.Value.Should().Be(ptr1);

        entries[2].Kind.Should().Be(EntryKind.Delete);
        entries[2].IsTombstone.Should().BeTrue();

        entries[3].ValueKind.Should().Be(ValueKind.ValueLogPointer);
        entries[3].Pointer!.Value.Should().Be(ptr2);
    }
}
