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
using System.Globalization;

namespace Boutquin.Storage.Infrastructure.Tests;

// Write keys are ordinal: two keys a culture comparer equates (precomposed vs decomposed
// A-ring) are distinct keys, and journal replay must keep them distinct regardless of culture.
public sealed class DurableTransactionalResourceRecoveryTests : IDisposable
{
    private const string Precomposed = "Å";
    private const string Decomposed = "Å";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "durable-participant-" + Guid.NewGuid().ToString("N"));
    private string FilePath(string name) => Path.Combine(_directory, name + ".log");
    private static Dictionary<string, string?> CultureEqualWrites => new() { [Precomposed] = "precomposed", [Decomposed] = "decomposed" };

    [Fact]
    public async Task CommittedWrites_WithCultureEqualOrdinalDistinctKeys_SurviveReopen()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            string.Compare(Precomposed, Decomposed, StringComparison.CurrentCulture).Should().Be(0, "the test needs keys the culture comparer equates");
            using (var resource = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
            {
                (await resource.PrepareAsync(new RequestId("t1"), CultureEqualWrites).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
                (await resource.CommitAsync(new RequestId("t1")).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
            }
            using var recovered = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
            (await recovered.ReadAsync(Precomposed).ConfigureAwait(true)).Should().Be("precomposed");
            (await recovered.ReadAsync(Decomposed).ConfigureAwait(true)).Should().Be("decomposed");
            (await recovered.PrepareAsync(new RequestId("t1"), CultureEqualWrites).ConfigureAwait(true)).Should().Be(PrepareResult.Committed);
        }
        finally { CultureInfo.CurrentCulture = priorCulture; }
    }

    [Fact]
    public async Task PreparedIntent_WithCultureEqualOrdinalDistinctKeys_ReplaysEveryLockAndAcceptsIdenticalRetry()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            using (var resource = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
            { (await resource.PrepareAsync(new RequestId("t1"), CultureEqualWrites).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared); }
            using var recovered = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
            var identicalRetry = () => recovered.PrepareAsync(new RequestId("t1"), CultureEqualWrites);
            (await identicalRetry.Should().NotThrowAsync().ConfigureAwait(true)).Which.Should().Be(PrepareResult.Prepared);
            (await recovered.PrepareAsync(new RequestId("other"), new Dictionary<string, string?> { [Precomposed] = "x" }).ConfigureAwait(true))
                .Should().Be(PrepareResult.Rejected, "the replayed prepare still locks the precomposed key");
            (await recovered.PrepareAsync(new RequestId("third"), new Dictionary<string, string?> { [Decomposed] = "x" }).ConfigureAwait(true))
                .Should().Be(PrepareResult.Rejected, "the replayed prepare still locks the decomposed key");
            (await recovered.CommitAsync(new RequestId("t1")).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
            (await recovered.ReadAsync(Precomposed).ConfigureAwait(true)).Should().Be("precomposed");
            (await recovered.ReadAsync(Decomposed).ConfigureAwait(true)).Should().Be("decomposed");
        }
        finally { CultureInfo.CurrentCulture = priorCulture; }
    }

    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }
}
