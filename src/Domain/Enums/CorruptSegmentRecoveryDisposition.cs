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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// How engine recovery treats a manifest-listed segment that fails its integrity check
/// (CRC32C/size mismatch, or the file missing entirely) at open.
/// </summary>
/// <remarks>
/// <para><b>Default is <see cref="FailClosed"/>:</b> the segment holds acknowledged, flushed
/// data the engine can no longer trust; silently excluding it would serve reads as if its
/// keys never existed — resurrecting older values and deletes from elder segments. For
/// authoritative data, the only safe disposition is to stop and let an operator decide.</para>
/// <para><b><see cref="ExcludeAndSurface"/> is for rebuildable caches:</b> when every record
/// can be reconstructed from an upstream source (e.g. a market-data cache tier re-fetching
/// from its provider), excluding the damaged segment and surfacing the exclusion lets the
/// consumer shrink its coverage accounting and re-ingest the affected span deliberately.
/// Each exclusion is reported through
/// <c>LsmStorageEngineOptions.OnCorruptSegmentExcluded</c> and trace-logged. The elder-value
/// resurrection hazard the default protects against is accepted by opting in — choose this
/// only when the store is NOT the source of truth.</para>
/// </remarks>
public enum CorruptSegmentRecoveryDisposition
{
    /// <summary>Throw at engine open, naming the damaged segment (the safe default).</summary>
    FailClosed = 0,

    /// <summary>
    /// Skip the damaged segment, report it via the exclusion callback, and open with the
    /// remaining segments. Only sound when the store's contents are rebuildable from an
    /// upstream source.
    /// </summary>
    ExcludeAndSurface = 1,
}
