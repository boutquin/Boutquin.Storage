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
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Boutquin.Storage.Distributed.Transactions;

/// <summary>A stable caller-supplied identity retained across retries and recovery.</summary>
/// <remarks>The default struct is invalid. IDs have an ordinal, store-wide namespace and must never be reused for different intent.</remarks>
public readonly record struct RequestId
{
    /// <summary>Creates a nonblank request identity.</summary>
    /// <param name="value">Stable ordinal identifier.</param>
    /// <exception cref="ArgumentException">The identity is null, blank, or malformed UTF-16.</exception>
    [JsonConstructor]
    public RequestId(string value) { TransactionData.ValidateText(value); Value = value; }
    /// <summary>Caller identity; default(RequestId) has no valid value.</summary>
    public string Value { get; }
    internal void Validate() => TransactionData.ValidateText(Value);
}

/// <summary>Classifies a participant's durable vote or already-final outcome.</summary>
public enum PrepareResult
{
    /// <summary>The intent is durable and its keys remain locked.</summary>
    Prepared,
    /// <summary>The participant rejected preparation and cannot commit this identity.</summary>
    Rejected,
    /// <summary>This identity was already committed with identical intent.</summary>
    Committed
}
/// <summary>Classifies an irrevocable commit or abort outcome at a participant or coordinator.</summary>
public enum CommitResult
{
    /// <summary>All requested writes committed.</summary>
    Committed,
    /// <summary>No requested writes committed.</summary>
    Aborted
}
/// <summary>Classifies a participant's final response to abort.</summary>
public enum AbortResult
{
    /// <summary>The intent is durably aborted, including idempotent repeats.</summary>
    Aborted,
    /// <summary>An irrevocable commit prevents abort.</summary>
    AlreadyCommitted
}

/// <summary>An immutable point-key write set targeting one participant.</summary>
/// <remarks>Copies and sorts caller data. Null values delete keys. Empty write sets are allowed; duplicate participant IDs in a plan are not.</remarks>
public sealed class TransactionIntent
{
    /// <summary>Captures one resource's intended writes without retaining mutable caller collections.</summary>
    /// <param name="resourceId">Nonblank registered participant name.</param>
    /// <param name="writes">Non-null map with nonblank ordinal keys; null values delete.</param>
    /// <exception cref="ArgumentException">Resource or key is blank, or durable text is malformed UTF-16.</exception>
    /// <exception cref="ArgumentNullException">Writes is null.</exception>
    public TransactionIntent(string resourceId, IReadOnlyDictionary<string, string?> writes)
    {
        TransactionData.ValidateText(resourceId);
        ResourceId = resourceId;
        Writes = new ReadOnlyDictionary<string, string?>(TransactionData.Copy(writes));
    }
    /// <summary>Stable participant name.</summary>
    public string ResourceId { get; }
    /// <summary>Owned immutable, ordinally sorted write set.</summary>
    public IReadOnlyDictionary<string, string?> Writes { get; }
}

internal static class TransactionData
{
    private static readonly System.Text.UTF8Encoding s_strictUtf8 = new(false, true);
    internal static void ValidateText(string text, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!allowEmpty) { ArgumentException.ThrowIfNullOrWhiteSpace(text); }
        // JSON replaces lone UTF-16 surrogates. Reject them before identity comparison or logging
        // so distinct caller inputs cannot alias and acknowledged values round-trip exactly.
        _ = s_strictUtf8.GetByteCount(text);
    }
    internal static SortedDictionary<string, string?> Copy(IReadOnlyDictionary<string, string?> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var copy = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in writes) { ValidateText(pair.Key); if (pair.Value is not null) { ValidateText(pair.Value, true); } copy.Add(pair.Key, pair.Value); }
        return copy;
    }
    internal static string Fingerprint(object value) => JsonSerializer.Serialize(value);
    internal static void Apply(Dictionary<string, string> values, IReadOnlyDictionary<string, string?> writes)
    {
        foreach (var pair in writes) { if (pair.Value is null) { values.Remove(pair.Key); } else { values[pair.Key] = pair.Value; } }
    }
}
