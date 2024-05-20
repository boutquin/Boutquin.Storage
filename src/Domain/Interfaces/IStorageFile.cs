﻿// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// Interface for basic file operations.
/// </summary>
public interface IStorageFile
{
    /// <summary>
    /// Creates a new file, optionally handling the existence of an existing file.
    /// </summary>
    /// <param name="existenceHandling">Specifies how to handle the existence of an existing file.</param>
    void Create(FileExistenceHandling existenceHandling);

    /// <summary>
    /// Checks if the file exists at the specified location.
    /// </summary>
    /// <returns>True if the file exists; otherwise, false.</returns>
    bool Exists();

    /// <summary>
    /// Opens the file for reading or writing.
    /// </summary>
    /// <returns>A file stream for the opened file.</returns>
    Stream Open();

    /// <summary>
    /// Deletes the file.
    /// </summary>
    void Delete();

    /// <summary>
    /// Gets the file size.
    /// </summary>
    /// <returns>The file size in bytes.</returns>
    long GetFileSize();

    /// <summary>
    /// Gets the filename.
    /// </summary>
    /// <returns>The filename.</returns>
    string GetFileName();

    /// <summary>
    /// Gets the location of the file.
    /// </summary>
    /// <returns>The file location.</returns>
    string GetFileLocation();

    /// <summary>
    /// Reads the entire file content as a byte array.
    /// </summary>
    /// <returns>The file content as a byte array.</returns>
    byte[] ReadAllBytes();

    /// <summary>
    /// Writes a byte array to the file.
    /// </summary>
    /// <param name="content">The byte array to write.</param>
    void WriteAllBytes(byte[] content);

    /// <summary>
    /// Appends a byte array to the end of the file.
    /// </summary>
    /// <param name="content">The byte array to append.</param>
    void AppendAllBytes(byte[] content);
}
