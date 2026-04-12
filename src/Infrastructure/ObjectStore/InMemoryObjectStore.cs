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
using Boutquin.Storage.Domain.Interfaces.ObjectStore;

namespace Boutquin.Storage.Infrastructure.ObjectStore;

/// <summary>
/// Volatile, concurrent-safe in-memory object store for testing and composition.
///
/// <para>Stores object content as byte arrays in a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Each <see cref="ReadAsync"/> call returns an independent <see cref="MemoryStream"/> copy,
/// so concurrent readers do not share stream positions.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 10 — "Batch Processing": object storage as the interchange format for batch outputs.</para>
/// </summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        return new ValueTask<bool>(_store.ContainsKey(key));
    }

    /// <inheritdoc />
    public ValueTask<Stream?> ReadAsync(string key, CancellationToken ct = default)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();

        if (!_store.TryGetValue(key, out var data))
        {
            return new ValueTask<Stream?>((Stream?)null);
        }

        // Return independent copy so readers don't share position
        Stream stream = new MemoryStream(data.ToArray(), writable: false);
        return new ValueTask<Stream?>(stream);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(string key, Stream content, CancellationToken ct = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct).ConfigureAwait(false);
        _store[key] = ms.ToArray();
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        _store.TryRemove(key, out _);
        return default;
    }

    private static void ValidateKey(string key) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
}
