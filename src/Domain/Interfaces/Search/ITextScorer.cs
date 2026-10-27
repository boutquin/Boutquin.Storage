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

/// <summary>Scores a term occurrence in a document collection.</summary>
public interface ITextScorer
{
    /// <summary>Computes a relevance contribution for one term.</summary>
    double Score(int termFrequency, int documentFrequency, int documentCount);
}
