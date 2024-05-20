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
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// Specifies how to handle the existence of a file during creation.
/// </summary>
public enum FileExistenceHandling
{
    /// <summary>
    /// Overwrite the existing file.
    /// </summary>
    Overwrite,

    /// <summary>
    /// Throw an exception if the file already exists.
    /// </summary>
    Throw,

    /// <summary>
    /// Skip the creation if the file already exists.
    /// </summary>
    Skip
}
