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
namespace Boutquin.Storage.Domain.ValueObjects.ValueLog;

/// <summary>
/// Knobs controlling value-log dispatch and garbage collection for an
/// <c>LsmStorageEngine</c> instance. Every field carries a production-grade default; the
/// consumer typically only tunes <see cref="ValueLogThresholdBytes"/>.
/// </summary>
/// <param name="ValueLogThresholdBytes">Values strictly longer than this (in bytes) are
/// written to the value log; values at or below stay inline in the SSTable block entry.
/// Must be at least 64 — below this the 12-byte pointer overhead exceeds the inline saving —
/// and at most 128 MiB, half the 256 MiB SSTable block ceiling, so an inline value plus its key
/// always fits a block the reader accepts.
/// DDIA 2e p. 130 motivates the split: small values amortise cheaply through compaction,
/// large values dominate write amp.</param>
/// <param name="SegmentMaxBytes">The writer seals the current segment and rotates to a fresh
/// <c>valuelog_NNNNNN.log</c> when the next frame would not fit within this size.</param>
/// <param name="ReadHandleCacheSize">Upper bound on the pooled <c>SafeFileHandle</c>s the
/// reader keeps open across live value-log files. LRU eviction when the pool is full.</param>
/// <param name="MaxEntryBytes">Largest single value the value-log writer accepts. It applies to
/// the values <c>SetAsync</c> routes to the log — those strictly longer than
/// <paramref name="ValueLogThresholdBytes"/> — and a longer one throws
/// <c>ValueTooLargeException</c> before any append. A value at or below
/// <paramref name="ValueLogThresholdBytes"/> stays inline and is not subject to this cap, so a
/// threshold above the cap leaves values between the two accepted; keep the threshold at or below
/// the cap to bound every value.</param>
/// <param name="ValidateCrcOnRead">Controls when the reader verifies a frame's header and payload
/// CRC32C. Default is <see cref="CrcValidationMode.Always"/>; set to
/// <see cref="CrcValidationMode.Never"/> for trusted-disk environments where the
/// validation overhead is not justified — a damaged frame is then served as stored.</param>
/// <remarks>
/// <para><b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive
/// Applications</i>, 2nd ed. (O'Reilly, 2026), p. 130.</para>
/// <para><b>Garbage collection.</b> v2.0.0 ships whole-file reclamation only — the GC
/// walks every closed value-log segment, checks whether any pointer in the engine's LSM
/// still references it, and unlinks the file if none do. Fraction-triggered or
/// size-triggered GC is out of scope for v2.0.0.</para>
/// <para><b>Block-level value-log compression.</b> An earlier revision carried a <c>Compression</c> field
/// on this record that was never wired to any read or write path. v2.0.0 ships
/// without per-entry value-log compression; the field has been removed under the
/// greenfield premise. Block-level compression for the value-log is a post-GA additive
/// feature.</para>
/// </remarks>
public sealed record ValueLogOptions(
    int ValueLogThresholdBytes = 1024,
    long SegmentMaxBytes = 256L * 1024 * 1024,
    int ReadHandleCacheSize = 64,
    long MaxEntryBytes = 64L * 1024 * 1024,
    CrcValidationMode ValidateCrcOnRead = CrcValidationMode.Always)
{
    private const int MinThresholdBytes = 64;

    // Half the SSTable reader's 256 MiB block ceiling: an inline value travels into a block with
    // its key and framing, so a larger threshold would let an acknowledged write flush into a
    // block that reads back as corrupt.
    private const int MaxThresholdBytes = 128 * 1024 * 1024;

    // Each bound is enforced twice by the same validator: in the field initializer (the primary
    // constructor path) and in the init accessor (the path `with` expressions and object
    // initializers take, which never runs the initializer). Validating only the initializer lets
    // `Default with { ReadHandleCacheSize = 0 }` through unchecked.

    /// <summary>Overrides the auto-generated property to enforce the lower-bound invariant.
    /// A threshold below 64 is net-negative: the 12-byte pointer written to the SSTable
    /// exceeds the value bytes saved from inline storage.</summary>
    public int ValueLogThresholdBytes
    {
        get;
        init => field = ValidateThreshold(value, nameof(ValueLogThresholdBytes));
    } = ValidateThreshold(ValueLogThresholdBytes, nameof(ValueLogThresholdBytes));

    /// <summary>Overrides the auto-generated property to require at least one open handle slot.
    /// Zero or negative would silently bump to 1 inside the reader, masking the configuration error.</summary>
    public int ReadHandleCacheSize
    {
        get;
        init => field = ValidateReadHandleCacheSize(value, nameof(ReadHandleCacheSize));
    } = ValidateReadHandleCacheSize(ReadHandleCacheSize, nameof(ReadHandleCacheSize));

    /// <summary>Overrides the auto-generated property to require a positive entry-size cap. A
    /// non-positive cap would reject every payload before the first append.</summary>
    public long MaxEntryBytes
    {
        get;
        init => field = ValidateMaxEntryBytes(value, nameof(MaxEntryBytes));
    } = ValidateMaxEntryBytes(MaxEntryBytes, nameof(MaxEntryBytes));

    /// <summary>Overrides the auto-generated property to require a positive segment-size cap.
    /// A non-positive cap would force perpetual segment rotation.</summary>
    public long SegmentMaxBytes
    {
        get;
        init => field = ValidateSegmentMaxBytes(value, nameof(SegmentMaxBytes));
    } = ValidateSegmentMaxBytes(SegmentMaxBytes, nameof(SegmentMaxBytes));

    private static int ValidateThreshold(int value, string paramName) =>
        value < MinThresholdBytes
            ? throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"ValueLogThresholdBytes must be at least {MinThresholdBytes} bytes — below this "
                + "the 12-byte ValueLogPointer written to the SSTable exceeds the inline-value saving.")
            : value > MaxThresholdBytes
                ? throw new ArgumentOutOfRangeException(
                    paramName,
                    value,
                    $"ValueLogThresholdBytes must be at most {MaxThresholdBytes} bytes — a larger inline value "
                    + "cannot fit the SSTable block ceiling the reader enforces.")
                : value;

    private static int ValidateReadHandleCacheSize(int value, string paramName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "ReadHandleCacheSize must be at least 1 — the reader requires at least one slot to satisfy any read.");

    private static long ValidateMaxEntryBytes(long value, string paramName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "MaxEntryBytes must be positive — a non-positive cap rejects every payload.");

    private static long ValidateSegmentMaxBytes(long value, string paramName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "SegmentMaxBytes must be positive — a non-positive cap forces perpetual segment rotation.");

    /// <summary>The GA default — used by any engine that does not customise its
    /// <c>LsmStorageEngineOptions.ValueLog</c>.</summary>
    public static ValueLogOptions Default { get; } = new();
}
