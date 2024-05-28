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
namespace Boutquin.Storage.Domain.Enums
{
    /// <summary>
    /// Specifies how to handle opening a file.
    /// </summary>
    public enum FileOpenHandling
    {
        /// <summary>
        /// Open the file if it exists.
        /// </summary>
        OpenIfExists,

        /// <summary>
        /// Throw an exception if the file does not exist.
        /// </summary>
        ThrowIfNotExists,

        /// <summary>
        /// Create the file if it does not exist.
        /// </summary>
        CreateIfNotExists
    }
}