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
namespace Boutquin.Storage.Domain.ValueObjects.LsmTree;

/// <summary>
/// Describes one manifest-listed segment excluded at engine recovery under
/// <see cref="Enums.CorruptSegmentRecoveryDisposition.ExcludeAndSurface"/>.
/// </summary>
/// <param name="FileName">The segment file name as listed in the <c>CURRENT</c> manifest.</param>
/// <param name="ManifestCrc32C">The CRC32C the manifest recorded at flush time.</param>
/// <param name="ManifestSizeBytes">The byte size the manifest recorded at flush time.</param>
/// <param name="ObservedCrc32C">The CRC32C recomputed at recovery; <c>0</c> when the file is missing.</param>
/// <param name="ObservedSizeBytes">The byte size observed at recovery; <c>0</c> when the file is missing.</param>
/// <param name="FileMissing">
/// <see langword="true"/> when the manifest-listed file was absent from disk entirely (as
/// opposed to present but failing its integrity check).
/// </param>
/// <remarks>
/// Consumers use the exclusion to shrink whatever coverage accounting sits above the engine
/// and to schedule a re-ingest of the affected span. The excluded file (when present) is left
/// on disk for forensics; once a subsequent flush rewrites the manifest without the excluded
/// row, the next recovery's orphan sweep removes it.
/// </remarks>
public sealed record CorruptSegmentExclusion(
    string FileName,
    uint ManifestCrc32C,
    long ManifestSizeBytes,
    uint ObservedCrc32C,
    long ObservedSizeBytes,
    bool FileMissing);
