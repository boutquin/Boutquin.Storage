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
/// Pins the source-generator diagnostic range every documentation surface advertises to the rule set
/// the analyzer release tables record. The tables (shipped plus unshipped) are the ground truth: a new
/// rule lands there first, and any document that still names an older upper bound fails here.
/// </summary>
public sealed partial class DiagnosticRangeDocumentationTests
{
    private static readonly string[] s_publicDocuments = ["README.md", "ARCHITECTURE.md", "CHANGELOG.md"];

    // Private instruction files are export-excluded from the public release tree, so they are checked
    // only where they exist.
    private static readonly string[] s_instructionDocuments = ["CLAUDE.md", "AGENTS.md"];

    [Fact]
    public void ReleaseTables_RecordAContiguousRuleSet_WithNoUndocumentedGap()
    {
        var ids = RecordedRuleNumbers();

        ids.Should().Contain(1);
        ids.Max().Should().BeGreaterThanOrEqualTo(26, "the table must not be silently truncated");
        var missing = Enumerable.Range(1, ids.Max()).Except(ids).ToArray();
        missing.Should().Equal([9], "BSSG009 is the one reserved identifier; any other gap means a range claim would be wrong");
    }

    [Fact]
    public void PublicDocuments_AdvertiseTheRangeTheReleaseTablesRecord()
    {
        var highest = RecordedRuleNumbers().Max();

        foreach (var path in s_publicDocuments)
        {
            var stale = StaleRangeClaims(ReadRepoFile(path), highest);
            stale.Should().BeEmpty($"{path} must name BSSG{highest:D3} as the top of the diagnostic range");
            RangeEndRegex().Matches(ReadRepoFile(path)).Should().NotBeEmpty($"{path} is expected to state the diagnostic range");
        }
    }

    [Fact]
    public void InstructionFiles_AdvertiseTheRangeTheReleaseTablesRecord()
    {
        var highest = RecordedRuleNumbers().Max();

        foreach (var path in s_instructionDocuments)
        {
            if (!File.Exists(RepoPath(path)))
            {
                continue;
            }

            var text = ReadRepoFile(path);
            StaleRangeClaims(text, highest).Should().BeEmpty($"{path} must name BSSG{highest:D3} as the top of the diagnostic range");
            RangeEndRegex().Matches(text).Should().NotBeEmpty($"{path} is expected to state the diagnostic range");
            ReleasesThroughRegex().Matches(text).Should().NotBeEmpty($"{path} is expected to state which analyzer releases are recorded");
        }
    }

    // Every "BSSGnnn–BSSGmmm" upper bound and every "through BSSGmmm" bound must equal the highest recorded rule.
    private static List<string> StaleRangeClaims(string text, int highest)
    {
        var stale = new List<string>();
        foreach (var match in RangeEndRegex().Matches(text).Concat(ReleasesThroughRegex().Matches(text)))
        {
            var end = int.Parse(match.Groups["end"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (end != highest)
            {
                stale.Add(match.Value);
            }
        }

        return stale;
    }

    private static SortedSet<int> RecordedRuleNumbers()
    {
        var numbers = new SortedSet<int>();
        foreach (var table in new[] { "src/SourceGenerator/AnalyzerReleases.Shipped.md", "src/SourceGenerator/AnalyzerReleases.Unshipped.md" })
        {
            foreach (System.Text.RegularExpressions.Match row in RuleRowRegex().Matches(ReadRepoFile(table)))
            {
                numbers.Add(int.Parse(row.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return numbers;
    }

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

    private static string RepoPath(string relativePath) =>
        Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(RepoPath(relativePath));

    // A range whose end names a rule: "BSSG001–BSSG026" or "BSSG010–BSSG026" (en dash or hyphen). The lookaheads skip
    // the two descriptive sub-ranges that legitimately end below the top: the segment before the reserved BSSG009
    // ("BSSG001–BSSG008, BSSG010–…") and the tag-validation block ("BSSG010–BSSG013 occupy a contiguous block").
    [GeneratedRegex(@"BSSG\d{3}[–-]BSSG(?<end>\d{3})(?! occupy)(?!, BSSG010)")]
    private static partial Regex RangeEndRegex();

    [GeneratedRegex(@"Analyzer releases through BSSG(?<end>\d{3})")]
    private static partial Regex ReleasesThroughRegex();

    [GeneratedRegex(@"^BSSG(?<id>\d{3})\s*\|", RegexOptions.Multiline)]
    private static partial Regex RuleRowRegex();
}
