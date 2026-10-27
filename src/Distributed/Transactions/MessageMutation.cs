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
namespace Boutquin.Storage.Distributed.Transactions;

/// <summary>An immutable outbound message with its stable consumer deduplication identity.</summary>
/// <param name="Id">Globally unique outbound identity within one message store.</param><param name="Topic">Nonblank destination label; no transport is implied.</param><param name="Payload">Non-null application text.</param>
public sealed record OutboxMessage(RequestId Id, string Topic, string Payload);

/// <summary>One atomic durable change: business state replacements, outbound messages, and a cached result.</summary>
/// <remarks>Copies all collections before admission. Null values delete keys. This models local effects only;
/// network calls and writes to another database cannot be made atomic by putting them in a handler.</remarks>
public sealed class MessageMutation
{
    /// <summary>Captures an owned immutable batch.</summary>
    /// <param name="writes">Nonblank ordinal keys; null values delete.</param><param name="messages">Outbound messages with distinct nonblank identities and topics.</param><param name="result">Non-null result retained for identical retries.</param>
    /// <exception cref="ArgumentException">A key, topic or identity is invalid or duplicated, or text is malformed UTF-16.</exception>
    /// <exception cref="ArgumentNullException">A collection, result, message or payload is null.</exception>
    public MessageMutation(IReadOnlyDictionary<string, string?> writes, IReadOnlyList<OutboxMessage> messages, string result)
    {
        ArgumentNullException.ThrowIfNull(messages); TransactionData.ValidateText(result, true);
        Writes = new ReadOnlyDictionary<string, string?>(TransactionData.Copy(writes));
        var copied = messages.ToArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in copied)
        {
            ArgumentNullException.ThrowIfNull(message); message.Id.Validate(); TransactionData.ValidateText(message.Topic); TransactionData.ValidateText(message.Payload, true);
            if (!ids.Add(message.Id.Value)) { throw new ArgumentException("Message identities must be unique within the batch.", nameof(messages)); }
        }
        Messages = Array.AsReadOnly(copied); Result = result;
    }
    /// <summary>Owned immutable business write set.</summary>
    public IReadOnlyDictionary<string, string?> Writes { get; }
    /// <summary>Owned immutable outbound messages in emission order.</summary>
    public IReadOnlyList<OutboxMessage> Messages { get; }
    /// <summary>Cached response returned without rerunning an identical request.</summary>
    public string Result { get; }
}
