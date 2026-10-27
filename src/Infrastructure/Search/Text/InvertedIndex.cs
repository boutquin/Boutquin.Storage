// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
using Boutquin.Storage.Domain.Interfaces.Search;

namespace Boutquin.Storage.Infrastructure.Search.Text;

/// <summary>Provides in-memory exact-term, phrase/proximity, Boolean AND, and ranked full-text retrieval.</summary>
public sealed class InvertedIndex<TDocumentId> : IInvertedIndex<TDocumentId> where TDocumentId : notnull
{
    private readonly object _gate = new();
    private readonly ITextAnalyzer _analyzer;
    private readonly ITextScorer _scorer;
    private readonly Dictionary<string, PostingsList<TDocumentId>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<TDocumentId, Dictionary<string, int[]>> _documents = [];
    private readonly Dictionary<TDocumentId, long> _sequence = [];
    private long _nextSequence;

    /// <summary>Initializes an index with the supplied analysis and scoring strategies.</summary>
    public InvertedIndex(ITextAnalyzer analyzer, ITextScorer? scorer = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _analyzer = analyzer;
        _scorer = scorer ?? new TfIdfScorer();
    }

    /// <inheritdoc />
    public void Add(TDocumentId documentId, ReadOnlyMemory<char> text)
    {
        var terms = _analyzer.Analyze(text);
        var positions = terms.Select(static (term, position) => (term, position))
            .GroupBy(static item => item.term, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key,
                static group => group.Select(static item => item.position).ToArray(), StringComparer.Ordinal);

        lock (_gate)
        {
            RemoveCore(documentId, preserveSequence: true);
            if (!_sequence.ContainsKey(documentId))
            {
                _sequence[documentId] = _nextSequence++;
            }

            _documents[documentId] = positions;
            foreach (var term in positions.Keys)
            {
                if (!_postings.TryGetValue(term, out var list))
                {
                    list = new PostingsList<TDocumentId>();
                    _postings[term] = list;
                }

                list.Add(documentId);
            }
        }
    }

    /// <inheritdoc />
    public bool Remove(TDocumentId documentId)
    {
        lock (_gate)
        {
            return RemoveCore(documentId, preserveSequence: false);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TDocumentId> Lookup(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        var analyzed = _analyzer.Analyze(term.AsMemory());
        if (analyzed.Count != 1)
        {
            return [];
        }

        lock (_gate)
        {
            return _postings.TryGetValue(analyzed[0], out var list)
                ? list.Documents.OrderBy(id => _sequence[id]).ToArray()
                : [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TDocumentId> SearchAll(IEnumerable<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var requested = terms.ToArray();
        if (requested.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            HashSet<TDocumentId>? matches = null;
            foreach (var term in requested)
            {
                var analyzed = _analyzer.Analyze(term.AsMemory());
                if (analyzed.Count != 1 || !_postings.TryGetValue(analyzed[0], out var list))
                {
                    return [];
                }

                matches = matches is null ? [.. list.Documents] : matches.Intersect(list.Documents).ToHashSet();
                if (matches.Count == 0)
                {
                    return [];
                }
            }

            return matches!.OrderBy(id => _sequence[id]).ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(TDocumentId DocumentId, double Score)> Search(ReadOnlyMemory<char> query, int maxResults)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
        var queryTerms = _analyzer.Analyze(query).Distinct(StringComparer.Ordinal).ToArray();
        lock (_gate)
        {
            var scores = new Dictionary<TDocumentId, double>();
            foreach (var term in queryTerms)
            {
                if (!_postings.TryGetValue(term, out var list))
                {
                    continue;
                }

                foreach (var documentId in list.Documents)
                {
                    var contribution = _scorer.Score(_documents[documentId][term].Length, list.Count, _documents.Count);
                    scores[documentId] = scores.GetValueOrDefault(documentId) + contribution;
                }
            }

            return scores.OrderByDescending(static pair => pair.Value)
                .ThenBy(pair => _sequence[pair.Key])
                .Take(maxResults)
                .Select(static pair => (pair.Key, pair.Value))
                .ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TDocumentId> SearchPhrase(ReadOnlyMemory<char> query, int maxSkippedTokens = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSkippedTokens);
        var requested = _analyzer.Analyze(query).ToArray();
        if (requested.Length == 0)
        {
            return [];
        }
        lock (_gate)
        {
            if (!_postings.TryGetValue(requested[0], out var candidates))
            {
                return [];
            }
            return candidates.Documents
                .Where(id => MatchesPhrase(_documents[id], requested, maxSkippedTokens))
                .OrderBy(id => _sequence[id]).ToArray();
        }
    }

    private static bool MatchesPhrase(Dictionary<string, int[]> positions, string[] requested, int maxSkippedTokens)
    {
        if (requested.Any(term => !positions.ContainsKey(term)))
        {
            return false;
        }
        foreach (var start in positions[requested[0]])
        {
            var previous = start;
            var matched = true;
            for (var token = 1; token < requested.Length; token++)
            {
                var occurrences = positions[requested[token]];
                // The earliest next occurrence minimizes the span for this start. Advancing
                // strictly also prevents one occurrence from satisfying two repeated terms.
                var next = Array.BinarySearch(occurrences, previous + 1);
                next = next < 0 ? ~next : next;
                if (next == occurrences.Length)
                {
                    matched = false;
                    break;
                }
                previous = occurrences[next];
            }
            if (matched && previous - start - (requested.Length - 1) <= maxSkippedTokens)
            {
                return true;
            }
        }
        return false;
    }

    private bool RemoveCore(TDocumentId documentId, bool preserveSequence)
    {
        if (!_documents.Remove(documentId, out var positions))
        {
            return false;
        }

        foreach (var term in positions.Keys)
        {
            var list = _postings[term];
            list.Remove(documentId);
            if (list.Count == 0)
            {
                _postings.Remove(term);
            }
        }

        if (!preserveSequence)
        {
            _sequence.Remove(documentId);
        }

        return true;
    }
}
