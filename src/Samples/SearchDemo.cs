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
using Boutquin.Storage.Infrastructure.Search.Vector;

namespace Boutquin.Storage.Samples;

/// <summary>Demonstrates keyword and semantic retrieval over the same documents.</summary>
public static class SearchDemo
{
    /// <summary>Runs the search demonstration.</summary>
    public static void Run()
    {
        var documents = new Dictionary<int, string>
        {
            [1] = "durable log structured storage",
            [2] = "approximate nearest neighbor vector search",
            [3] = "full text inverted index"
        };

        var text = new InvertedIndex<int>(
            new TextAnalyzer(new WhitespaceTokenizer(), new LowercaseNormalizer()));
        var vectors = new HnswVectorIndex<int>(3, new CosineDistance());
        foreach (var document in documents)
        {
            text.Add(document.Key, document.Value.AsMemory());
        }

        vectors.Upsert(1, [0.9F, 0.1F, 0.0F]);
        vectors.Upsert(2, [0.0F, 1.0F, 0.1F]);
        vectors.Upsert(3, [0.1F, 0.2F, 0.9F]);

        Console.WriteLine("Keyword search: text index");
        foreach (var result in text.Search("text index".AsMemory(), 3))
        {
            Console.WriteLine($"  {result.DocumentId}: {documents[result.DocumentId]} ({result.Score:F3})");
        }

        var phrase = text.SearchPhrase("log structured".AsMemory());
        var proximity = text.SearchPhrase("full index".AsMemory(), maxSkippedTokens: 2);
        if (!phrase.SequenceEqual([1]) || !proximity.SequenceEqual([3]))
        {
            throw new InvalidOperationException("Phrase or ordered proximity retrieval failed.");
        }
        Console.WriteLine($"Phrase log structured -> {string.Join(',', phrase)}; proximity full index -> {string.Join(',', proximity)}");

        Console.WriteLine("Semantic search: retrieval systems");
        foreach (var result in vectors.Search([0.05F, 0.35F, 0.8F], 3))
        {
            Console.WriteLine($"  {result.Id}: {documents[result.Id]} (distance {result.Distance:F3})");
        }
    }
}
