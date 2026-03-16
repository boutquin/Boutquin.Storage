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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// A grow-only counter CRDT. Each node maintains its own counter; the total value is the sum of all node counters.
/// Merge takes the element-wise maximum.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5.</para>
/// </summary>
public interface IGCounter : ICrdt<long>
{
    /// <summary>
    /// Increments the counter for the specified node.
    /// </summary>
    /// <param name="nodeId">The identifier of the node performing the increment.</param>
    void Increment(string nodeId);
}
