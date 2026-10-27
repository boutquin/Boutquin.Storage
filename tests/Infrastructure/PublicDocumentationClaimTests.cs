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
using System.Text.RegularExpressions;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins three claims the shipped documentation and the public-export release gate make about the
/// repository: which exception types a 1.x payload can surface as, which field type the README's
/// schema-evolution example may add, and that no exported C# file cites an internal spec section.
/// Each test reads the shipped file and asserts against behavior the code actually exhibits (or a
/// rule written down elsewhere), so a documentation edit that reintroduces the drift fails here.
/// </summary>
public sealed partial class PublicDocumentationClaimTests
{
    private static readonly Type[] s_oneXFailureTypes =
    [
        typeof(UnsupportedFormatVersionException),
        typeof(InvalidDataException),
        typeof(EndOfStreamException),
    ];

    [Fact]
    public void RandomOneXPayloads_OnlyThrowTypes_EveryShippedFailureModeSectionNames()
    {
        // 1.x BinaryWriter layouts of random values, fed to the 2.0 readers. The thrown set comes from the
        // readers, the expected set from the documents: neither shares a source with the other.
        var rng = new Random(20261018);
        var thrown = new HashSet<Type>();

        for (var i = 0; i < 300; i++)
        {
            byte[] bytes;
            Action read;
            switch (rng.Next(5))
            {
                case 0:
                    bytes = BitConverter.GetBytes(rng.Next(int.MinValue, int.MaxValue));
                    read = () => SerializableWrapper<int>.Deserialize(new MemoryStream(bytes));
                    break;
                case 1:
                    bytes = BitConverter.GetBytes(rng.NextInt64(long.MinValue, long.MaxValue));
                    read = () => SerializableWrapper<long>.Deserialize(new MemoryStream(bytes));
                    break;
                case 2:
                    bytes = BitConverter.GetBytes((short)rng.Next(short.MinValue, short.MaxValue));
                    read = () => SerializableWrapper<short>.Deserialize(new MemoryStream(bytes));
                    break;
                case 3:
                    bytes = BitConverter.GetBytes(rng.NextDouble());
                    read = () => SerializableWrapper<double>.Deserialize(new MemoryStream(bytes));
                    break;
                default:
                    {
                        var name = new string(Enumerable.Range(0, rng.Next(0, 12)).Select(_ => (char)rng.Next('a', 'z' + 1)).ToArray());
                        using var ms = new MemoryStream();
                        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                        {
                            writer.Write(name);
                            writer.Write(rng.Next());
                        }

                        bytes = ms.ToArray();
                        read = () => OneXNameFirstRecord.Deserialize(new MemoryStream(bytes));
                        break;
                    }
            }

            try
            {
                read();
                Assert.Fail("a 1.x-shaped payload was accepted by the 2.0.0 reader");
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                thrown.Add(ex.GetType());
            }
        }

        thrown.Should().BeSubsetOf(s_oneXFailureTypes);
        thrown.Count.Should().BeGreaterThan(1, "the 1.x signal is not a single exception type");

        var sections = new (string Path, string Start, string End)[]
        {
            ("docs/migration-1x-to-2x.md", "## Fail-fast detection", "\n## "),
            ("docs/migration-1x-to-2x.md", "## What `UnsupportedFormatVersionException` looks like in practice", "\n## "),
            ("docs/source-generator-type-reference.md", "## Schema-format version", "\n## "),
            ("README.md", "- **Every property declares `[Tag(N)]`.**", "\n- "),
        };

        foreach (var (path, start, end) in sections)
        {
            var section = ReadSection(path, start, end);
            foreach (var type in thrown)
            {
                section.Should().Contain(type.Name, $"{path} \"{start}\" must name {type.Name}, which a 1.x payload surfaced as");
            }
        }
    }

    [Fact]
    public void MigrationGuideCatchExample_HandlesEveryOneXFailureType()
    {
        var example = ReadSection("docs/migration-1x-to-2x.md", "catch (", "```");

        foreach (var type in s_oneXFailureTypes)
        {
            example.Should().Contain(type.Name, $"the guide's catch example must handle {type.Name}");
        }
    }

    [Fact]
    public void ReadmeEvolutionExample_AddsOnlyAFieldTypeTheGeneratedWriterCanRoundTrip()
    {
        var readme = ReadRepoFile("README.md");
        var start = readme.IndexOf("Schema evolution on a `[StorageSerializable]`:", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = readme.IndexOf("To customize generated output", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        var added = TimezoneFieldRegex().Match(readme[start..end]);
        added.Success.Should().BeTrue("the example adds a Tag(3) Timezone field");
        added.Groups["type"].Value.Should().Be("string", "the writer throws on a null string, so the added field must not be string?");

        // The behavior the choice rests on: old data reads as the empty string and survives a rewrite.
        using var old = new MemoryStream();
        new ReadmeCityValueV1("Lima", 10_000_000).Serialize(old);
        old.Position = 0;
        var migrated = ReadmeCityValueV2.Deserialize(old);
        using var rewritten = new MemoryStream();
        migrated.Serialize(rewritten);
        rewritten.Position = 0;
        ReadmeCityValueV2.Deserialize(rewritten).Should().Be(migrated);
        migrated.Timezone.Should().BeEmpty();
    }

    [Fact]
    public void WireFormatDocument_StatesThatANullNullableStringWritesNoTag()
    {
        var text = Regex.Replace(ReadRepoFile("docs/wire-format-2.0.md"), @"\s+", " ");

        text.Should().Contain("a `string?` property writes no tag when the value is null",
            "the reader-side default for a missing string tag is documented next to the writer-side contract that mirrors it");
        text.Should().NotContain("throws `InvalidOperationException` when asked to serialize a null string");
    }

    [Fact]
    public void ExportedCSharpFiles_CiteNoInternalSpecSection()
    {
        // The public-export gate forbids a section-number citation into the private spec tree. Public
        // literature citations (Raft paper, VLDB papers, RFCs) and the wire-format document's own
        // numbering are allowed, matching the gate's per-line allow-list.
        var sectionSign = ((char)0xA7).ToString();
        var root = RepoRoot();
        var allowed = PublicLiteratureRegex();
        var offenders = new List<string>();

        foreach (var top in new[] { "src", "tests", "benchmarks" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative == "tests/SourceGenerator/WireFormatDocFixtureTests.cs")
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (SectionCitation(sectionSign).IsMatch(lines[i]) && !allowed.IsMatch(lines[i]))
                    {
                        offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty("a public source file must state its rationale in prose, not cite a private spec section");
    }

    // The section sign followed by a digit or capital letter is a citation number; the sign in running prose is not.
    private static Regex SectionCitation(string sectionSign) => new(Regex.Escape(sectionSign) + @"\s*[0-9A-Z]");

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
        return end < 0 ? rest : rest[..end];
    }

    [GeneratedRegex(@"Tag\(3\)\]\s*(?<type>string\??)\s+Timezone")]
    private static partial Regex TimezoneFieldRegex();

    [GeneratedRegex(@"Raft|Ongaro|Shapiro|Pelkonen|Abadi|Pillai|Hellerstein|Demers|Karger|Knuth|et al\.|VLDB|Architecture of a Database System|RFC\s*[0-9]|DDIA|Figure\s*[0-9]|Leader Completeness|Log Matching|State Machine Safety|current-term guard|wire-format-2\.0\.md")]
    private static partial Regex PublicLiteratureRegex();
}
