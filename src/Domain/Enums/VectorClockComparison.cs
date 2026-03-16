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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// Represents the result of comparing two vector clocks.
/// </summary>
/// <remarks>
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5 — "Replication",
/// section on detecting concurrent writes. Vector clocks capture the causal ordering between events:
/// two events are concurrent if neither happened-before the other.</para>
/// </remarks>
public enum VectorClockComparison
{
    /// <summary>
    /// The first clock happened before the second (all entries &lt;= and at least one &lt;).
    /// </summary>
    Before,

    /// <summary>
    /// The first clock happened after the second (all entries &gt;= and at least one &gt;).
    /// </summary>
    After,

    /// <summary>
    /// The clocks are concurrent (neither happened-before the other).
    /// </summary>
    Concurrent,

    /// <summary>
    /// The clocks are identical.
    /// </summary>
    Equal
}
