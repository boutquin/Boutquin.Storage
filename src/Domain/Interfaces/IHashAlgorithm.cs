// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// Interface for hash algorithms.
/// Provides a contract for implementing different hash algorithms.
/// </summary>
public interface IHashAlgorithm
{
    /// <summary>
    /// Computes the hash for the given input data.
    /// </summary>
    /// <param name="data">The input data as a read-only span of bytes.</param>
    /// <returns>
    /// The computed 32-bit hash value.
    /// </returns>
    /// <remarks>
    /// The implementation of this method should ensure that it processes the
    /// input data efficiently and produces a well-distributed hash value
    /// for use in hash-based data structures or algorithms such as hash tables,
    /// Bloom filters, and checksums.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown if the input data is null.
    /// </exception>
    /// <example>
    /// Example usage:
    /// <code>
    /// IHashAlgorithm hasher = new Fnv1aHash();
    /// ReadOnlySpan&lt;byte&gt; data = new byte[] { 1, 2, 3, 4, 5 };
    /// uint hash = hasher.ComputeHash(data);
    /// Console.WriteLine($"Computed hash: {hash}");
    /// </code>
    /// </example>
    uint ComputeHash(ReadOnlySpan<byte> data);
}