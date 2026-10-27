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
namespace Boutquin.Storage.Domain.Exceptions;

/// <summary>
/// A value-log file began with a header whose magic (<c>'B','S','V','L'</c>) or
/// <c>format_version</c> was not recognised by the running engine. Indicates either a
/// forward-incompatible writer or a non-value-log file dropped into the directory.
/// </summary>
/// <remarks>
/// <para>v2.0.0 accepts only <c>format_version = 1</c>. A future content-addressable
/// variant would land at <c>format_version = 2</c> with a dual-read path added in the same
/// release — a dual-read path would be added in the same release for a future content-addressable variant.</para>
/// </remarks>
public sealed class UnrecognisedValueLogFormatException(string filePath, string message) : Exception(message)
{
    /// <summary>Path to the file that failed the header check.</summary>
    public string FilePath { get; } = filePath;
}
