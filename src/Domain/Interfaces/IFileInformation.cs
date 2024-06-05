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
/// Provides file information for storage engines.
/// </summary>
public interface IFileInformation
{
    /// <summary>
    /// Gets the full file path, including the location and file name.
    /// </summary>
    string FilePath => Path.Combine(FileLocation, FileName);

    /// <summary>
    /// Gets the size of the file in bytes.
    /// </summary>
    long FileSize { get; }

    /// <summary>
    /// Gets the name of the file.
    /// </summary>
    string FileName { get; }

    /// <summary>
    /// Gets the location (directory path) of the file.
    /// </summary>
    string FileLocation { get; }
}