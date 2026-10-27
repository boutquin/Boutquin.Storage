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
#nullable disable

using Wal = global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// A reclamation (<c>TruncateCompletedSegmentsAsync</c> or <c>TruncateAsync</c>) that unlinks the
/// current append segment and then fails to make the unlink durable (the directory fsync throws)
/// must not leave the writer believing the deleted segment's header is still resolved. The caller
/// (the LSM flush path) treats a reclamation failure as best-effort and keeps appending, so every
/// append acknowledged afterwards must land in a segment that carries a valid file header and is
/// recovered on the next open — never in a headerless file recovery rejects.
/// </summary>
public sealed class WalReclamationDirectoryFsyncFailureTests : IDisposable
{
    private static readonly byte[] s_magic = [(byte)'B', (byte)'S', (byte)'W', (byte)'L'];

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"WalReclamationDirFsync_{Guid.NewGuid():N}");

    public WalReclamationDirectoryFsyncFailureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string Segment(int index) => Path.Combine(_dir, $"wal_{index:D6}.log");

    /// <summary>
    /// Covering the whole current segment deletes it; the directory fsync after the unlink fails.
    /// Appends 3 and 4 are acknowledged afterwards and must survive a reopen.
    /// </summary>
    [Theory]
    [InlineData("truncate-completed")]
    [InlineData("truncate")]
    public async Task Reclamation_DirectoryFsyncFailsAfterCurrentSegmentUnlinked_LaterAcknowledgedAppendsAreRecovered(
        string reclamation)
    {
        using (var wal = new Wal(_dir))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"), 1UL).ConfigureAwait(true);
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("two"), 2UL).ConfigureAwait(true);

            var failNext = true;
            wal._faultInjector = step =>
            {
                if (step == "deletion-directory-fsync" && failNext)
                {
                    failNext = false;
                    throw new IOException("injected directory fsync failure (EIO)");
                }
            };

            var reclaim = reclamation == "truncate"
                ? Record.ExceptionAsync(() => wal.TruncateAsync())
                : Record.ExceptionAsync(() => wal.TruncateCompletedSegmentsAsync(2UL));
            (await reclaim.ConfigureAwait(true)).Should().BeOfType<IOException>(
                "precondition: the reclamation surfaces the injected directory fsync failure");
            File.Exists(Segment(1)).Should().BeFalse("precondition: the unlink itself landed before the fsync failed");

            await wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("three"), 3UL).ConfigureAwait(true);
            await wal.AppendAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("four"), 4UL).ConfigureAwait(true);
        }

        using (var reopened = new Wal(_dir))
        {
            IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>> entries = null;
            var caught = await Record.ExceptionAsync(async () => entries = await reopened.RecoverAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            caught.Should().BeNull("appends acknowledged after a failed reclamation are fsync'd history and must recover");
            entries.Select(e => e.Key.Value).Should().Equal(3, 4);
        }

        foreach (var segment in Directory.GetFiles(_dir, "wal_*.log"))
        {
            (await File.ReadAllBytesAsync(segment).ConfigureAwait(true)).AsSpan(0, 4).ToArray().Should().Equal(
                s_magic, "every segment the writer appends into starts with the WAL file header");
        }
    }
}
