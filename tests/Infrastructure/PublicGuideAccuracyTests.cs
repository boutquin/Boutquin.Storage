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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Boutquin.Storage.Domain.Attributes;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// 1.x layout of <c>(int Population, string Name)</c>: the integer comes first, as <c>BinaryWriter</c> wrote it.
/// </summary>
[StorageSerializable]
public partial record struct GuideAccuracyPopulationFirstRecord(
    [property: Tag(1)] int Population,
    [property: Tag(2)] string Name);

/// <summary>1.x layout of <c>(string Name, int Population)</c>: the string comes first.</summary>
[StorageSerializable]
public partial record struct GuideAccuracyNameFirstRecord(
    [property: Tag(1)] string Name,
    [property: Tag(2)] int Population);

/// <summary>The README's first schema version.</summary>
[StorageSerializable]
public partial record struct GuideAccuracyCityV1(
    [property: Tag(1)] string Name,
    [property: Tag(2)] int Population);

/// <summary>The schema version the README recommends: the added string field is non-nullable.</summary>
[StorageSerializable]
public partial record struct GuideAccuracyCityV2(
    [property: Tag(1)] string Name,
    [property: Tag(2)] int Population,
    [property: Tag(3)] string Timezone);

/// <summary>The same schema version with the added string field declared nullable.</summary>
[StorageSerializable]
public partial record struct GuideAccuracyCityV2Nullable(
    [property: Tag(1)] string Name,
    [property: Tag(2)] int Population,
    [property: Tag(3)] string? Timezone);

/// <summary>
/// Pins the claims the shipped guides and READMEs make about behavior this repository can execute:
/// how reliably a 2.0.0 reader recognises 1.x bytes, what happens to a snapshot that is dropped
/// without disposal, which analyzer rules exist, and which field type the schema-evolution example
/// may add. Each test runs the behavior first and then reads the shipped text against what it saw,
/// so neither the code nor the prose can drift without the other.
/// </summary>
public sealed partial class PublicGuideAccuracyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"GuideAccuracy_{Guid.NewGuid():N}");

    public PublicGuideAccuracyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a scratch directory.
        }
    }

    // ───────────────────────── snapshot lifecycle ─────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> AcquireAndDropAsync(
        LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> engine)
    {
        var snapshot = await engine.AcquireSnapshotAsync().ConfigureAwait(false);
        return new WeakReference(snapshot);
    }

    [Fact]
    public async Task SnapshotLifecycleText_DescribesAnUndisposedSnapshotAsPinnedUntilEngineDisposal_BecauseNothingReclaimsIt()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: 2, segmentFolder: _dir, segmentPrefix: "seg");
        await engine.SetAsync(new(1), new(1)).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);

        // Behavior: drop three snapshots without disposing them, dispose one explicitly.
        var leaked = new List<WeakReference>();
        for (var i = 0; i < 3; i++)
        {
            leaked.Add(await AcquireAndDropAsync(engine).ConfigureAwait(true));
        }

        var explicitlyDisposed = await engine.AcquireSnapshotAsync().ConfigureAwait(true);
        var registry = typeof(LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>)
            .GetField("_snapshotRegistry", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        var count = registry.GetType().GetProperty("Count")!;
        ((int)count.GetValue(registry)!).Should().Be(4);

        await explicitlyDisposed.DisposeAsync().ConfigureAwait(true);
        for (var i = 0; i < 5; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        leaked.Should().OnlyContain(w => !w.IsAlive, "the snapshot objects themselves were collected");
        ((int)count.GetValue(registry)!).Should().Be(3,
            "collecting an undisposed snapshot leaves its registration; only DisposeAsync removes one");

        var declaresFinalizer = typeof(LsmStorageEngine<,>).Assembly.GetTypes()
            .Where(t => t.Name.StartsWith("LsmSnapshot", StringComparison.Ordinal))
            .Any(t => t.GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) is not null);
        declaresFinalizer.Should().BeFalse(
            "no snapshot type has a finalizer; if one is added, the lifecycle text below must be rewritten to describe it");

        // Prose: neither the guide nor the interface remarks may credit a finalizer, and both must say how long a
        // leaked snapshot's pins and tombstone watermark last.
        var guide = Squash(ReadRepoFile("docs/snapshot-isolation.md"));
        var remarks = Squash(ReadRepoFile("src/Domain/Interfaces/ISnapshot.cs"));

        foreach (var (name, text) in new[] { ("docs/snapshot-isolation.md", guide), ("src/Domain/Interfaces/ISnapshot.cs", remarks) })
        {
            text.Should().NotMatchRegex("(?i)final(is|iz)er", $"{name} must not describe a finalizer the snapshot types lack");
            text.Should().Contain("until the engine is disposed", $"{name} must state how long an undisposed snapshot stays registered");
        }
    }

    // ───────────────────────── 1.x detection ─────────────────────────

    private static readonly Type[] s_oneXFailureTypes =
    [
        typeof(UnsupportedFormatVersionException),
        typeof(InvalidDataException),
        typeof(EndOfStreamException),
    ];

    // The surfaces that tell a consumer how a 2.0.0 reader reacts to 1.x bytes.
    private static readonly (string Path, string Start, string End)[] s_oneXSurfaces =
    [
        ("docs/migration-1x-to-2x.md", "## Fail-fast detection", "\n## "),
        ("docs/migration-1x-to-2x.md", "## What `UnsupportedFormatVersionException` looks like in practice", "\n## "),
        ("docs/migration-1x-to-2x.md", "## Wrapper bytes", "\n## "),
        ("docs/source-generator-type-reference.md", "## Schema-format version", "\n## "),
        ("README.md", "- **Every property declares `[Tag(N)]`.**", "\n- "),
        ("docs/nuget-readme.md", "Migration requires exporting", "\n\n"),
    ];

    private static byte[] OneXRecordBytes(int population, string name)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(population);
            writer.Write(name);
        }

        return ms.ToArray();
    }

    [Fact]
    public void OneXPayloadsThatOpenWithATagZeroPrefix_AreAcceptedWithoutAnException_SoEveryDetectionClaimIsBestEffort()
    {
        var rng = new Random(20261019);
        var accepted = 0;

        for (var i = 0; i < 200; i++)
        {
            // A 1.x record whose int is `02 00 01 xx`: the 2.0.0 reader reads the first byte as a length prefix of 2 and
            // the next two as tag 0 = schema-format version 1, and the record ends there with every field defaulted.
            var population = 2 | (1 << 16) | (rng.Next(0, 128) << 24);
            var name = new string(Enumerable.Range(0, rng.Next(0, 8)).Select(_ => (char)rng.Next('a', 'z' + 1)).ToArray());
            var bytes = OneXRecordBytes(population, name);

            var read = GuideAccuracyPopulationFirstRecord.Deserialize(new MemoryStream(bytes));

            read.Population.Should().Be(0);
            read.Name.Should().BeEmpty();
            accepted++;
        }

        // The same hole through the wrapper: 1.x long bytes `04 00 01 08 v 00 00 00` decode to a different value.
        var oneXLong = 0x04L | (1L << 16) | (8L << 24) | ((long)rng.Next(0, 128) << 32);
        var wrapperRead = SerializableWrapper<long>.Deserialize(new MemoryStream(BitConverter.GetBytes(oneXLong)));
        wrapperRead.Value.Should().NotBe(oneXLong);
        accepted.Should().Be(200);

        foreach (var (path, start, end) in s_oneXSurfaces)
        {
            var section = ReadSection(path, start, end);
            section.Should().Contain("best-effort",
                $"{path} \"{start}\": a 1.x payload can be accepted without any exception, so the text must not promise detection");
            section.Should().Contain("out-of-band",
                $"{path} \"{start}\" must send the consumer to an out-of-band marker as the reliable 1.x signal");
        }
    }

    [Fact]
    public void RealisticOneXLayouts_ThrowOnlyTypesTheGuidesName_OrAreAcceptedAndDocumentedAsSuch()
    {
        var rng = new Random(20261020);
        var thrown = new HashSet<Type>();

        for (var i = 0; i < 400; i++)
        {
            Action read;
            switch (rng.Next(7))
            {
                case 0:
                    var i32 = BitConverter.GetBytes(rng.Next(int.MinValue, int.MaxValue));
                    read = () => SerializableWrapper<int>.Deserialize(new MemoryStream(i32));
                    break;
                case 1:
                    var i64 = BitConverter.GetBytes(rng.NextInt64());
                    read = () => SerializableWrapper<long>.Deserialize(new MemoryStream(i64));
                    break;
                case 2:
                    var i16 = BitConverter.GetBytes((short)rng.Next(short.MinValue, short.MaxValue));
                    read = () => SerializableWrapper<short>.Deserialize(new MemoryStream(i16));
                    break;
                case 3:
                    var dbl = BitConverter.GetBytes(rng.NextDouble() * 1e6);
                    read = () => SerializableWrapper<double>.Deserialize(new MemoryStream(dbl));
                    break;
                case 4:
                    var text = new string(Enumerable.Range(0, rng.Next(0, 12)).Select(_ => (char)rng.Next('a', 'z' + 1)).ToArray());
                    using (var ms = new MemoryStream())
                    {
                        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                        {
                            w.Write(text);
                        }

                        var str = ms.ToArray();
                        read = () => SerializableWrapper<string>.Deserialize(new MemoryStream(str));
                    }

                    break;
                case 5:
                    var popFirst = OneXRecordBytes(rng.Next(), "Lima" + rng.Next(0, 99));
                    read = () => GuideAccuracyPopulationFirstRecord.Deserialize(new MemoryStream(popFirst));
                    break;
                default:
                    using (var ms = new MemoryStream())
                    {
                        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                        {
                            w.Write("City" + rng.Next(0, 9999));
                            w.Write(rng.Next());
                        }

                        var nameFirst = ms.ToArray();
                        read = () => GuideAccuracyNameFirstRecord.Deserialize(new MemoryStream(nameFirst));
                    }

                    break;
            }

            try
            {
                read();
            }
            catch (Exception ex)
            {
                thrown.Add(ex.GetType());
            }
        }

        thrown.Should().BeSubsetOf(s_oneXFailureTypes);
        thrown.Count.Should().BeGreaterThan(1, "the 1.x signal is not a single exception type");

        foreach (var (path, start, end) in s_oneXSurfaces.Take(5))
        {
            var section = ReadSection(path, start, end);
            foreach (var type in thrown)
            {
                section.Should().Contain(type.Name, $"{path} \"{start}\" must name {type.Name}, which a 1.x payload surfaced as");
            }
        }

        var example = ReadSection("docs/migration-1x-to-2x.md", "catch (", "```");
        foreach (var type in s_oneXFailureTypes)
        {
            example.Should().Contain(type.Name, $"the guide's catch example must handle {type.Name}");
        }
    }

    // ───────────────────────── schema evolution example ─────────────────────────

    [Fact]
    public void SchemaEvolutionExamples_AddedStringField_ReadsOldDataAndWritesItBack_NullableOrNot()
    {
        // Non-nullable added field: an absent tag reads as the empty string and writes back.
        using var old = new MemoryStream();
        new GuideAccuracyCityV1("Lima", 10_000_000).Serialize(old);
        old.Position = 0;

        var migrated = GuideAccuracyCityV2.Deserialize(old);
        migrated.Timezone.Should().BeEmpty("an absent string tag reads as the empty string");
        using var rewritten = new MemoryStream();
        migrated.Serialize(rewritten);
        rewritten.Position = 0;
        GuideAccuracyCityV2.Deserialize(rewritten).Should().Be(migrated);

        // Nullable added field: an absent tag reads as null, and null writes as an absent tag
        // (the `T?` / `Child?` contract), so the value old data produced can be written back.
        old.Position = 0;
        var migratedNullable = GuideAccuracyCityV2Nullable.Deserialize(old);
        migratedNullable.Timezone.Should().BeNull("an absent nullable string tag reads as null");
        using var rewrittenNullable = new MemoryStream();
        var writeBack = () => migratedNullable.Serialize(rewrittenNullable);
        writeBack.Should().NotThrow("a value the generated reader produced must be writable by the generated writer");
        rewrittenNullable.ToArray().Should().Equal(old.ToArray(),
            "a null string? writes no tag, so the record re-encodes to exactly the old bytes");
        rewrittenNullable.Position = 0;
        GuideAccuracyCityV2Nullable.Deserialize(rewrittenNullable).Should().Be(migratedNullable);

        using var withValue = new MemoryStream();
        var populated = new GuideAccuracyCityV2Nullable("Lima", 10_000_000, "America/Lima");
        populated.Serialize(withValue);
        withValue.Position = 0;
        GuideAccuracyCityV2Nullable.Deserialize(withValue).Should().Be(populated);

        // Prose: the wire-format guide states the nullable contract the code has.
        var guide = ReadSection("docs/wire-format-2.0.md", "### Missing-tag defaults for non-nullable strings", "## 4.");
        guide.Should().Contain("writes no tag when the value is null");
        guide.Should().NotContain("throws `InvalidOperationException` when asked to serialize a null string");
    }

    // ───────────────────────── analyzer rule range ─────────────────────────

    [Fact]
    public void AnalyzerRuleRangeInReadmeAndTypeReference_ReachesTheHighestShippedOrPendingRule()
    {
        var root = RepoRoot();
        var releaseIds = new[] { "AnalyzerReleases.Shipped.md", "AnalyzerReleases.Unshipped.md" }
            .Select(f => Path.Combine(root, "src", "SourceGenerator", f))
            .Where(File.Exists)
            .SelectMany(File.ReadAllLines)
            .Select(line => RuleIdRegex().Match(line))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        releaseIds.Should().NotBeEmpty();
        var highest = releaseIds.Max();
        highest.Should().BeGreaterThanOrEqualTo(26, "BSSG023..BSSG026 were added after BSSG022");

        foreach (var path in new[] { "README.md", "docs/source-generator-type-reference.md", "docs/nuget-readme.md", "docs/migration-1x-to-2x.md" })
        {
            var text = ReadRepoFile(path);
            foreach (System.Text.RegularExpressions.Match range in RuleRangeRegex().Matches(text))
            {
                int.Parse(range.Groups["end"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    .Should().Be(highest, $"{path} states a closed diagnostic range \"{range.Value}\" that stops short of BSSG{highest:000}");
            }
        }

        ReadRepoFile("README.md").Should().Contain($"BSSG001–BSSG{highest:000}", "the README must state the diagnostic range");
        var table = ReadRepoFile("docs/source-generator-type-reference.md");
        for (var n = 23; n <= highest; n++)
        {
            table.Should().Contain($"| BSSG{n:000} |", $"the type reference must list BSSG{n:000}");
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    // Collapses whitespace and strips XML-doc line prefixes so a sentence wrapped across comment lines reads as one.
    private static string Squash(string text) =>
        Regex.Replace(Regex.Replace(text, @"(?m)^\s*///", " "), @"\s+", " ");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test runs inside the repository checkout");
        return directory!.FullName;
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string ReadSection(string relativePath, string startMarker, string endMarker)
    {
        var text = ReadRepoFile(relativePath);
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{relativePath} must contain {startMarker}");
        var rest = text[(start + startMarker.Length)..];
        var end = rest.IndexOf(endMarker, StringComparison.Ordinal);
        return Regex.Replace(end < 0 ? rest : rest[..end], @"\s+", " ");
    }

    [GeneratedRegex(@"^BSSG(?<n>\d{3})\s*\|")]
    private static partial Regex RuleIdRegex();

    [GeneratedRegex(@"BSSG001\s*[–-]\s*BSSG(?<end>\d{3})")]
    private static partial Regex RuleRangeRegex();
}
