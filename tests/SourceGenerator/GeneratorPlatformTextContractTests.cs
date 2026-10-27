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
namespace Boutquin.Storage.SourceGenerator.Tests;

public sealed class GeneratorPlatformTextContractTests
{
    [Fact]
    public Task SnapshotSerialization_NormalizesRoslynPathButPreservesLiteralBackslashes()
    {
        // Exercise Verify's real object-property serialization, where global string scrubbers
        // receive each value before its FilePath property name has been written.
        _ = TestHelpers.NormalizeSnapshotMetadata("");
        var tree = CSharpSyntaxTree.ParseText(" ", CSharpParseOptions.Default,
            path: @"Generator\Namespace\Value.g.cs", encoding: System.Text.Encoding.UTF8);
        return Verify(new { Tree = tree, Literal = @"C:\records\value" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedSource_UsesLfAndIsIdenticalForLfAndCrLfInput(bool columnar)
    {
        var source = columnar
            ? "using System;\nusing Boutquin.Storage.Domain.Attributes;\n[ColumnarStorable]\npublic partial record Bar { public DateOnly Date { get; init; } public long Volume { get; init; } }"
            : "using Boutquin.Storage.Domain.Attributes;\n[StorageSerializable]\npublic partial record struct Value([property: Tag(1)] long Number);";
        var first = Generate(source, columnar);
        var second = Generate(source.ReplaceLineEndings("\r\n"), columnar);
        first.Should().NotBeEmpty();
        first.Should().Equal(second, "newline convention in the user's syntax cannot change generated C#");
        foreach (var (_, text) in first)
        {
            text.Should().NotContain("\r", "generated artifact lengths and spans must be host-independent");
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SnapshotPathNormalization_ChangesOnlyRoslynFilePathMetadata(string newline)
    {
        var input = "  FilePath: Generator\\Namespace\\Value.g.cs," + newline
            + "  Source: var path = @\"C:\\records\\value\";" + newline;
        var expected = "  FilePath: Generator/Namespace/Value.g.cs," + newline
            + "  Source: var path = @\"C:\\records\\value\";" + newline;
        TestHelpers.NormalizeSnapshotMetadata(input).Should().Be(expected);
    }

    private static (string Hint, string Text)[] Generate(string source, bool columnar)
    {
        var compilation = TestHelpers.CreateCompilationFromSources([source]);
        IIncrementalGenerator generator = columnar ? new ColumnarSourceGenerator() : new StorageSourceGenerator();
        var driver = CSharpGeneratorDriver.Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        return driver.GetRunResult().Results.Single().GeneratedSources
            .Select(generated => (generated.HintName, generated.SourceText.ToString())).ToArray();
    }
}
