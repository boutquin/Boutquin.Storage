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
namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// Represents the location of a key-value pair within a file.
/// </summary>
/// <remarks>
/// <para>This struct is used to store the offset and length of an entry in a file-based storage engine. The offset indicates the position within the file where the entry starts, and the count indicates the number of bytes that the entry occupies. This information is critical for efficiently locating and retrieving entries within a file, especially in append-only file storage engines.</para>
/// <para>Example usage:</para>
/// <code>
/// // Create a new FileLocation
/// var location = new FileLocation(1024, 128);
///
/// // Access the offset and count
/// int entryOffset = location.Offset;
/// int entryCount = location.Count;
///
/// // Print the file location details
/// Console.WriteLine($"Offset: {entryOffset}, Count: {entryCount}");
/// </code>
/// <para>This struct is immutable and designed to be used in scenarios where precise file positioning is required for efficient data retrieval.</para>
/// </remarks>
/// <param name="Offset">The offset within the file where the entry starts.</param>
/// <param name="Count">The number of bytes that the entry occupies in the file.</param>
public readonly record struct FileLocation(int Offset, int Count);
