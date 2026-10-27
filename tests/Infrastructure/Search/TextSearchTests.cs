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
using Boutquin.Storage.Infrastructure.Search.Text;

namespace Boutquin.Storage.Infrastructure.Tests.Search;

public sealed class TextSearchTests
{
    [Fact]
    public void Analyzer_ShouldTokenizeNormalizeAndRemoveStopWords()
    {
        var analyzer = new TextAnalyzer(
            new WhitespaceTokenizer(),
            new LowercaseNormalizer(),
            new StopWordNormalizer(["the"]));

        analyzer.Analyze("The QUICK fox".AsMemory()).Should().Equal("quick", "fox");
    }

    [Fact]
    public void NGramTokenizer_ShouldProduceOverlappingGrams()
    {
        new NGramTokenizer(3).Tokenize("search".AsMemory())
            .Should().Equal("sea", "ear", "arc", "rch");
    }

    [Fact]
    public void Index_ShouldSupportLookupAndIntersectionAndDeletion()
    {
        var index = CreateIndex();
        index.Add(10, "Fast vector search".AsMemory());
        index.Add(20, "Fast text search".AsMemory());
        index.Add(30, "Slow text scan".AsMemory());

        index.Lookup("FAST").Should().Equal(10, 20);
        index.SearchAll(["fast", "text"]).Should().Equal(20);
        index.Remove(20).Should().BeTrue();
        index.Lookup("text").Should().Equal(30);
        index.Remove(999).Should().BeFalse();
    }

    [Fact]
    public void Add_ShouldReplaceExistingDocumentWithoutChangingTieOrder()
    {
        var index = CreateIndex();
        index.Add(1, "alpha old".AsMemory());
        index.Add(2, "alpha new".AsMemory());
        index.Add(1, "alpha new".AsMemory());

        index.Lookup("old").Should().BeEmpty();
        index.Search("alpha".AsMemory(), 2).Select(static result => result.DocumentId).Should().Equal(1, 2);
    }

    [Fact]
    public void RankedSearch_ShouldPreferHigherTermFrequency()
    {
        var index = CreateIndex();
        index.Add(1, "storage search".AsMemory());
        index.Add(2, "search search search".AsMemory());

        var results = index.Search("search".AsMemory(), 2);

        results.Select(static result => result.DocumentId).Should().Equal(2, 1);
        results[0].Score.Should().BeGreaterThan(results[1].Score);
    }

    [Fact]
    public void EmptyBooleanQuery_ShouldReturnNoDocuments()
    {
        var index = CreateIndex();
        index.Add(1, "anything".AsMemory());

        index.SearchAll([]).Should().BeEmpty();
    }

    [Fact]
    public void PhraseSearch_ShouldPreserveOrderMultiplicityAndTotalGapBudget()
    {
        var index = CreateIndex();
        index.Add(1, "a b c".AsMemory());
        index.Add(2, "a x b y c".AsMemory());
        index.Add(3, "c b a".AsMemory());
        index.Add(4, "a a b".AsMemory());
        index.Add(5, "a b".AsMemory());

        index.SearchPhrase("A B C".AsMemory()).Should().Equal(1);
        index.SearchPhrase("a b c".AsMemory(), 1).Should().Equal(1);
        index.SearchPhrase("a b c".AsMemory(), 2).Should().Equal(1, 2);
        index.SearchPhrase("a a".AsMemory()).Should().Equal(4);
        index.SearchPhrase("a".AsMemory()).Should().Equal(1, 2, 3, 4, 5);
        index.SearchPhrase("".AsMemory()).Should().BeEmpty();
        Action invalid = () => index.SearchPhrase("a".AsMemory(), -1);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PhraseSearch_ShouldRefreshPositionsOnReplacementAndDeletion()
    {
        var index = CreateIndex();
        index.Add(1, "a b".AsMemory());
        index.Add(2, "a b".AsMemory());
        index.Add(1, "b a".AsMemory());
        index.SearchPhrase("a b".AsMemory()).Should().Equal(2);
        index.Add(1, "a b".AsMemory());
        index.SearchPhrase("a b".AsMemory()).Should().Equal(1, 2);
        index.Remove(1);
        index.SearchPhrase("a b".AsMemory()).Should().Equal(2);
    }

    [Fact]
    public void PhraseSearch_ShouldMeasurePositionsInTheAnalyzedTokenStream()
    {
        var index = new InvertedIndex<int>(new TextAnalyzer(
            new WhitespaceTokenizer(), new LowercaseNormalizer(), new StopWordNormalizer(["the"])));
        index.Add(1, "Find the Cat".AsMemory());
        index.SearchPhrase("FIND CAT".AsMemory()).Should().Equal(1);
        index.SearchPhrase("the".AsMemory()).Should().BeEmpty();
    }

    [Fact]
    public void ProximitySearch_ShouldMatchIndependentSubsequenceOracle()
    {
        var random = new Random(20260917);
        var index = CreateIndex();
        var documents = Enumerable.Range(0, 100)
            .Select(_ => Enumerable.Range(0, random.Next(0, 16))
                .Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray()).ToArray();
        for (var id = 0; id < documents.Length; id++)
        {
            index.Add(id, string.Join(' ', documents[id]).AsMemory());
        }
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var query = Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray();
            var gaps = random.Next(4);
            var expected = Enumerable.Range(0, documents.Length)
                .Where(id => HasSubsequence(documents[id], query, gaps));
            index.SearchPhrase(string.Join(' ', query).AsMemory(), gaps).Should().Equal(expected);
        }
    }

    private static bool HasSubsequence(string[] document, string[] query, int gaps)
    {
        // Exhaustive position combinations form an independent oracle for the production search.
        return Match(0, 0, -1);

        bool Match(int token, int nextPosition, int firstPosition)
        {
            if (token == query.Length)
            {
                return nextPosition - firstPosition - query.Length <= gaps;
            }
            for (var position = nextPosition; position < document.Length; position++)
            {
                if (document[position] == query[token] &&
                    Match(token + 1, position + 1, firstPosition < 0 ? position : firstPosition))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static InvertedIndex<int> CreateIndex() =>
        new(new TextAnalyzer(new WhitespaceTokenizer(), new LowercaseNormalizer()));
}
