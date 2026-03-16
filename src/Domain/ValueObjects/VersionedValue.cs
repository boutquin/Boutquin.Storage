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
/// Represents a versioned value in an MVCC store, tagged with the transaction ID that created it.
///
/// <para>Each write creates a new <see cref="VersionedValue{TValue}"/> rather than overwriting the previous one.
/// Readers see a consistent snapshot by selecting the appropriate version based on their transaction's
/// visibility rules.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 7 —
/// multi-version concurrency control keeps old versions so that readers never block writers.</para>
/// </summary>
/// <typeparam name="TValue">The type of the stored value.</typeparam>
/// <param name="Value">The stored value.</param>
/// <param name="TransactionId">The transaction ID that created this version.</param>
/// <param name="IsDeleted">Whether this version represents a deletion (tombstone).</param>
public readonly record struct VersionedValue<TValue>(TValue Value, long TransactionId, bool IsDeleted);
