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
/// The dataset-coverage manifest at a given path could not be parsed — it contains invalid
/// JSON, an unrecognised schema version, or could not be read due to an I/O error.
/// </summary>
/// <remarks>
/// <para>
/// The coverage manifest is advisory (range reads remain correct after loss), so total
/// unavailability is preferable to silently overwriting recoverable data. When this
/// exception is raised by <c>FileDatasetCoverageIndex</c>, the on-disk manifest is left
/// untouched. Callers should recover by calling <c>RebuildAsync</c>, which clears the
/// corrupt-flag and persists fresh state derived from a full scan.
/// </para>
/// </remarks>
public sealed class CorruptCoverageManifestException : Exception
{
    /// <summary>The absolute path of the manifest file that could not be loaded.</summary>
    public string ManifestPath { get; }

    public CorruptCoverageManifestException(string manifestPath, string message)
        : base(message)
    {
        ManifestPath = manifestPath;
    }

    public CorruptCoverageManifestException(string manifestPath, string message, Exception innerException)
        : base(message, innerException)
    {
        ManifestPath = manifestPath;
    }
}
