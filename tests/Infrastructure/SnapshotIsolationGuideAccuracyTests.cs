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
/// Pins the lost-update paragraph of the public snapshot-isolation guide against executed
/// store behaviour, so the guide cannot credit a store with a conflict check it lacks.
/// </summary>
public sealed class SnapshotIsolationGuideAccuracyTests
{
    /// <summary>
    /// The public snapshot-isolation guide names the stores whose commit-time
    /// first-committer-wins check prevents a lost update. Every store it names must actually
    /// reject the second of two concurrent read-modify-write committers; the bare MVCC store
    /// commits both (plain MVCC has no commit-time conflict check).
    /// </summary>
    [Fact]
    public async Task LostUpdateGuide_NamesOnlyStoresWhoseCommitRejectsTheSecondWriter()
    {
        var sentence = FirstCommitterWinsSentence();
        var named = System.Text.RegularExpressions.Regex.Matches(sentence, "`(I[A-Za-z]+Store)`")
            .Select(m => m.Groups[1].Value).ToList();
        named.Should().NotBeEmpty("the guide must name the store that prevents lost updates");

        foreach (var name in named)
        {
            (await SecondConcurrentIncrementCommitsAsync(name).ConfigureAwait(true)).Should().BeFalse(
                $"the guide credits `{name}` with first-committer-wins, so its commit must reject the second concurrent increment");
        }
    }

    /// <summary>
    /// The guide's statement about the bare MVCC store must match what it executes: it is
    /// allowed to name `IMvccStore` only as a store that performs no commit-time conflict
    /// check, and two concurrent read-modify-write transactions on it really both commit.
    /// </summary>
    [Fact]
    public async Task LostUpdateGuide_StatesBareMvccStoreHasNoConflictCheck_AndItDoesNotReject()
    {
        var text = ReadGuide();
        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+");
        var noCheck = sentences
            .Where(s => s.Contains("performs no", StringComparison.Ordinal)
                && s.Contains("conflict check", StringComparison.Ordinal))
            .SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s, "`(I[A-Za-z]+Store)`")
                .Select(m => m.Groups[1].Value))
            .ToList();
        noCheck.Should().Contain("IMvccStore", "the guide must say the bare MVCC store has no conflict check");

        foreach (var name in noCheck)
        {
            (await SecondConcurrentIncrementCommitsAsync(name).ConfigureAwait(true)).Should().BeTrue(
                $"the guide says `{name}` performs no conflict check, so its commit must accept the second concurrent increment");
        }
    }

    private static async Task<bool> SecondConcurrentIncrementCommitsAsync(string interfaceName)
    {
        switch (interfaceName)
        {
            case "IMvccStore":
                {
                    using var store = new MvccStore<string, int>();
                    var seed = store.BeginTransaction();
                    await store.WriteAsync("n", 0, seed).ConfigureAwait(true);
                    await store.CommitAsync(seed).ConfigureAwait(true);
                    var a = store.BeginTransaction();
                    var b = store.BeginTransaction();
                    var va = (await store.ReadAsync("n", a).ConfigureAwait(true)).Value;
                    var vb = (await store.ReadAsync("n", b).ConfigureAwait(true)).Value;
                    await store.WriteAsync("n", va + 1, a).ConfigureAwait(true);
                    await store.WriteAsync("n", vb + 1, b).ConfigureAwait(true);
                    await store.CommitAsync(a).ConfigureAwait(true);
                    return await store.CommitAsync(b).ConfigureAwait(true);
                }
            case "ISnapshotIsolationStore":
                {
                    using var store = new SnapshotIsolationStore<string, int>();
                    var seed = store.BeginTransaction();
                    await store.WriteAsync("n", 0, seed).ConfigureAwait(true);
                    await store.CommitAsync(seed).ConfigureAwait(true);
                    var a = store.BeginTransaction();
                    var b = store.BeginTransaction();
                    var va = (await store.ReadAsync("n", a).ConfigureAwait(true)).Value;
                    var vb = (await store.ReadAsync("n", b).ConfigureAwait(true)).Value;
                    await store.WriteAsync("n", va + 1, a).ConfigureAwait(true);
                    await store.WriteAsync("n", vb + 1, b).ConfigureAwait(true);
                    await store.CommitAsync(a).ConfigureAwait(true);
                    return await store.CommitAsync(b).ConfigureAwait(true);
                }
            default:
                throw new InvalidOperationException($"No executable mapping for `{interfaceName}` named by the guide.");
        }
    }

    private static string FirstCommitterWinsSentence()
    {
        var sentences = System.Text.RegularExpressions.Regex.Split(ReadGuide(), @"(?<=[.!?])\s+");
        return sentences.Single(s => s.Contains("first-committer-wins", StringComparison.Ordinal));
    }

    private static string ReadGuide()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }
        directory.Should().NotBeNull("the test runs inside the repository checkout");
        var text = File.ReadAllText(Path.Combine(directory!.FullName, "docs", "snapshot-isolation.md"));
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
    }
}
