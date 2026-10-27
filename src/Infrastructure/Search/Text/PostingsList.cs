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

/// <summary>Maintains a duplicate-free insertion-ordered postings list.</summary>
public sealed class PostingsList<TDocumentId> : IPostingsList<TDocumentId> where TDocumentId : notnull
{
    private readonly List<TDocumentId> _documents = [];
    private readonly HashSet<TDocumentId> _membership = [];

    /// <inheritdoc />
    public int Count => _documents.Count;

    /// <inheritdoc />
    public IReadOnlyList<TDocumentId> Documents => _documents.ToArray();

    /// <inheritdoc />
    public bool Add(TDocumentId documentId)
    {
        if (!_membership.Add(documentId))
        {
            return false;
        }

        _documents.Add(documentId);
        return true;
    }

    /// <inheritdoc />
    public bool Remove(TDocumentId documentId)
    {
        if (!_membership.Remove(documentId))
        {
            return false;
        }

        _documents.Remove(documentId);
        return true;
    }

    /// <inheritdoc />
    public bool Contains(TDocumentId documentId) => _membership.Contains(documentId);
}
