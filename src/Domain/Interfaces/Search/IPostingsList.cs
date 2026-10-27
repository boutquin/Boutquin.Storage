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

/// <summary>Tracks the documents containing one term.</summary>
public interface IPostingsList<TDocumentId> where TDocumentId : notnull
{
    /// <summary>Gets the number of documents in the list.</summary>
    int Count { get; }

    /// <summary>Gets a stable snapshot of the document identifiers.</summary>
    IReadOnlyList<TDocumentId> Documents { get; }

    /// <summary>Adds a document identifier.</summary>
    bool Add(TDocumentId documentId);

    /// <summary>Removes a document identifier.</summary>
    bool Remove(TDocumentId documentId);

    /// <summary>Returns whether the document is present.</summary>
    bool Contains(TDocumentId documentId);
}
