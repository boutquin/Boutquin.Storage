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
namespace Boutquin.Storage.Domain.Interfaces.ObjectStore;

/// <summary>
/// Represents a key-to-blob object store supporting CRUD operations.
///
/// <para>Object storage provides a flat (or hierarchical) namespace mapping opaque string keys
/// to arbitrary binary content. Unlike key-value stores that operate on serialized domain types,
/// object stores work at the stream level — callers control the byte layout entirely.</para>
///
/// <para><b>Typical uses:</b> batch job outputs, data lake snapshots, raw document caching,
/// durable intermediate results in ingestion pipelines.</para>
///
/// <para><b>Key semantics:</b> keys are opaque strings. Implementations may interpret path
/// separators (e.g., <c>/</c>) to create hierarchical layouts on disk.</para>
///
/// <para><b>Concurrency:</b> concurrent writes to the same key follow last-writer-wins semantics.
/// Implementations must guarantee no corruption (e.g., via atomic temp-file-and-rename).</para>
///
/// <para><b>Complexity:</b></para>
/// <para>- <b>ExistsAsync:</b> O(1) amortized for file-system implementations (stat call).</para>
/// <para>- <b>ReadAsync:</b> O(N) where N is the object size.</para>
/// <para>- <b>WriteAsync:</b> O(N) where N is the content size, plus atomic rename overhead.</para>
/// <para>- <b>DeleteAsync:</b> O(1) amortized for file-system implementations (unlink call).</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 10 — "Batch Processing": object storage as the interchange format for batch outputs;
/// Ch. 12 — "The Future of Data Systems": immutable append-to-object-store as a durable log sink.</para>
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Checks whether an object with the specified key exists in the store.
    /// </summary>
    /// <param name="key">The object key. Must not be null, empty, or whitespace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the object exists; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null, empty, or whitespace.</exception>
    ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Reads the content of the object with the specified key.
    /// </summary>
    /// <param name="key">The object key. Must not be null, empty, or whitespace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Stream"/> containing the object content, or <c>null</c> if the key does not exist.
    /// The caller is responsible for disposing the returned stream.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null, empty, or whitespace.</exception>
    ValueTask<Stream?> ReadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Writes content to the object with the specified key, creating or overwriting it.
    /// </summary>
    /// <param name="key">The object key. Must not be null, empty, or whitespace.</param>
    /// <param name="content">The content to write. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    ValueTask WriteAsync(string key, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Deletes the object with the specified key. No-op if the key does not exist.
    /// </summary>
    /// <param name="key">The object key. Must not be null, empty, or whitespace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null, empty, or whitespace.</exception>
    ValueTask DeleteAsync(string key, CancellationToken ct = default);
}
