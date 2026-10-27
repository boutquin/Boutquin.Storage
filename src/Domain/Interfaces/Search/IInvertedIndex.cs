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
namespace Boutquin.Storage.Domain.Interfaces.Search;

/// <summary>Maps analyzed terms to documents and supports Boolean and ranked retrieval.</summary>
public interface IInvertedIndex<TDocumentId> where TDocumentId : notnull
{
    /// <summary>Adds or replaces a document.</summary>
    void Add(TDocumentId documentId, ReadOnlyMemory<char> text);

    /// <summary>Removes a document.</summary>
    bool Remove(TDocumentId documentId);

    /// <summary>Returns documents containing <paramref name="term"/>.</summary>
    IReadOnlyList<TDocumentId> Lookup(string term);

    /// <summary>Returns documents containing every supplied term.</summary>
    IReadOnlyList<TDocumentId> SearchAll(IEnumerable<string> terms);

    /// <summary>Matches analyzed query tokens in order with a bounded total number of intervening tokens.</summary>
    /// <param name="query">Text analyzed using the same pipeline as indexed documents.</param>
    /// <param name="maxSkippedTokens">Total unmatched analyzed tokens allowed between the first and last match; zero requires adjacency.</param>
    /// <returns>Matching identifiers in insertion order; an empty analyzed query returns no matches.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSkippedTokens"/> is negative.</exception>
    /// <remarks>Repeated query terms require distinct positions. Removed stop words do not occupy positions;
    /// proximity is measured after analysis, not in the original characters or words.</remarks>
    IReadOnlyList<TDocumentId> SearchPhrase(ReadOnlyMemory<char> query, int maxSkippedTokens = 0);

    /// <summary>Returns the highest-scoring documents for the analyzed query.</summary>
    IReadOnlyList<(TDocumentId DocumentId, double Score)> Search(ReadOnlyMemory<char> query, int maxResults);
}
