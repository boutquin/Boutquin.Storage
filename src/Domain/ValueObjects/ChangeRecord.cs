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
namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// A single change event in a CDC stream.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 11 — "Stream Processing": change records as the unit of a CDC stream.</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <param name="Position">The position in the change stream.</param>
/// <param name="Operation">The type of change.</param>
/// <param name="Key">The affected key.</param>
/// <param name="Value">The new value (null for deletes).</param>
/// <param name="Timestamp">When the change occurred.</param>
public sealed record ChangeRecord<TKey, TValue>(
    long Position,
    ChangeOperation Operation,
    TKey Key,
    TValue? Value,
    DateTimeOffset Timestamp);
