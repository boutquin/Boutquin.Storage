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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Failure-path contracts of <see cref="LogSegmentedStorageEngine{TKey, TValue}"/>: an interrupted
/// clear, a rejected bulk write, and two engines sharing one folder and prefix must each leave the
/// store in a state it actually held — never a resurrected superseded value, a half-applied batch,
/// or another instance's deleted segment.
/// </summary>
public sealed class LogSegmentedStorageEngineRecoveryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"LogSegRecovery_{Guid.NewGuid():N}");

    /// <summary>
    /// A segment whose <see cref="ClearAsync"/> can fault before deleting its file
    /// (<paramref name="failBeforeDelete"/>) or run an action after deleting it
    /// (<paramref name="afterDelete"/>, e.g. cancelling the caller's token), so an interruption
    /// lands between two segment clears.
    /// </summary>
    private sealed class InterruptingClearSegment(
        IStorageFile file,
        IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> serializer,
        Func<bool> failBeforeDelete,
        Action afterDelete)
        : AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(file, serializer)
    {
        public override async Task ClearAsync(CancellationToken cancellationToken = default)
        {
            if (failBeforeDelete())
            {
                throw new IOException("injected clear fault");
            }

            await base.ClearAsync(cancellationToken).ConfigureAwait(true);
            afterDelete();
        }
    }

    private LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine(
        long maxSegmentSize, Func<bool> failBeforeDelete = null, Action afterDelete = null)
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        failBeforeDelete ??= () => false;
        afterDelete ??= () => { };
        return new LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            serializer, _folder, "seg", maxSegmentSize,
            (location, name, s, _) => new InterruptingClearSegment(new StorageFile(location, name), s, failBeforeDelete, afterDelete));
    }

    /// <summary>
    /// Writes key 1 as "old-value" then "new-value" with a segment limit small enough that the
    /// two writes land in different segments (old in the older segment, new in the newer one).
    /// </summary>
    private static async Task WriteOverwrittenKeyAsync(
        LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> engine)
    {
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("old-value")).ConfigureAwait(true);
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("new-value")).ConfigureAwait(true);
        var (value, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1)).ConfigureAwait(true);
        found.Should().BeTrue();
        value.Value.Should().Be("new-value");
    }

    /// <summary>
    /// The caller's token is cancelled after the first segment clear. Whatever the call returns,
    /// key 1 must never read back as its overwritten "old-value": the store either still holds
    /// the latest value or is cleared.
    /// </summary>
    [Fact]
    public async Task ClearAsync_CancelledBetweenSegmentClears_NeverResurrectsOverwrittenValue()
    {
        using var cts = new CancellationTokenSource();
        var armed = false;
        var engine = NewEngine(16, afterDelete: () =>
        {
            if (armed)
            {
                cts.Cancel();
            }
        });
        await WriteOverwrittenKeyAsync(engine).ConfigureAwait(true);

        armed = true;
        try
        {
            await engine.ClearAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // An interrupted clear may surface the cancellation; the state check below is the contract.
        }

        armed = false;

        var (value, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1)).ConfigureAwait(true);
        (found ? value.Value : null).Should().NotBe("old-value", "an interrupted clear must not expose a superseded value");
    }

    /// <summary>
    /// The second segment clear faults with an I/O error before deleting its file. Key 1 must not read back as its
    /// overwritten "old-value", and the engine must accept and serve new writes afterwards.
    /// </summary>
    [Fact]
    public async Task ClearAsync_FaultOnSecondSegmentClear_NeverResurrectsOverwrittenValue()
    {
        var armed = false;
        var clearsAfterArm = 0;
        var engine = NewEngine(16, failBeforeDelete: () => armed && ++clearsAfterArm == 2);
        await WriteOverwrittenKeyAsync(engine).ConfigureAwait(true);

        armed = true;
        try
        {
            await engine.ClearAsync().ConfigureAwait(true);
        }
        catch (IOException)
        {
            // The injected fault may surface; the state check below is the contract.
        }

        armed = false;

        var (value, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1)).ConfigureAwait(true);
        (found ? value.Value : null).Should().NotBe("old-value", "a faulted clear must not expose a superseded value");

        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("after")).ConfigureAwait(true);
        var (after, afterFound) = await engine.TryGetValueAsync(new SerializableWrapper<int>(2)).ConfigureAwait(true);
        afterFound.Should().BeTrue();
        after.Value.Should().Be("after");
    }

    /// <summary>
    /// A batch whose last item is invalid is rejected before any of it is written: none of the
    /// valid leading items may be readable afterwards.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_InvalidItemAfterValidOnes_PersistsNothing()
    {
        var engine = NewEngine(1 << 20);
        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b")),
            new(new SerializableWrapper<int>(3), null),
        };

        var act = () => engine.SetBulkAsync(items);
        await act.Should().ThrowAsync<ArgumentNullException>().ConfigureAwait(true);

        (await engine.ContainsKeyAsync(new SerializableWrapper<int>(1)).ConfigureAwait(true)).Should().BeFalse("a rejected batch must not apply its valid prefix");
        (await engine.GetAllItemsAsync().ConfigureAwait(true)).Should().BeEmpty();
    }

    /// <summary>
    /// Two engines constructed back-to-back on the same folder and prefix must own distinct
    /// segment files: clearing one must not delete the other's acknowledged data. Repeated so
    /// the same-millisecond construction case (the common one) is exercised.
    /// </summary>
    [Fact]
    public async Task TwoEnginesOnSameFolderAndPrefix_ClearOfOne_KeepsTheOthersData()
    {
        for (var trial = 0; trial < 25; trial++)
        {
            var a = NewEngine(1 << 20);
            var b = NewEngine(1 << 20);
            await a.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a")).ConfigureAwait(true);

            await b.ClearAsync().ConfigureAwait(true);

            var (value, found) = await a.TryGetValueAsync(new SerializableWrapper<int>(1)).ConfigureAwait(true);
            found.Should().BeTrue($"trial {trial}: another instance's clear must not delete this instance's segment");
            value.Value.Should().Be("a");
            await a.ClearAsync().ConfigureAwait(true);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }
}
