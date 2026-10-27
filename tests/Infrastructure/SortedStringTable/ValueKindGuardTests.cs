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
using System.Reflection;

namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// Bug-hunt-style routing-invariant guard tests for
/// <see cref="Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable{TKey, TValue}"/>'s
/// <c>ValueKind</c> dispatch — analogous to the
/// <c>CompressedColumnarSegmentFormat.DecodeColumn</c> non-restartable encoding guard.
/// <para>
/// The routing layer is the LSM-engine flush path: it constructs
/// <see cref="SegmentWriteEntry{TKey, TValue}"/> values and stamps each with a
/// <c>ValueKind</c> drawn from a finite enum. The dispatch layer is the SSTable writer:
/// <c>ValidateEntryInvariant</c> enumerates the supported kinds, then <c>WriteToDisk</c>
/// switches on them again. If a future <c>ValueKind</c> (e.g., a compressed-inline arm)
/// is added upstream without updating both dispatch sites in lockstep, the new value
/// silently falls through. With a generic "unknown" message a developer has to reason
/// about what changed; with a message that names the routing invariant the diagnostic
/// points at the drift immediately.
/// </para>
/// </summary>
public sealed class ValueKindGuardTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(), $"SsTableValueKindGuard_{Guid.NewGuid():N}");

    public ValueKindGuardTests() => Directory.CreateDirectory(_testDirectory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // ── 1. ValidateEntryInvariant — primary routing guard ──────────────────

    /// <summary>
    /// An entry stamped with an out-of-range <see cref="ValueKind"/> (constructed via the
    /// primary record-struct ctor — bypassing the <see cref="SegmentWriteEntry{TKey, TValue}.Inline"/>
    /// / <see cref="SegmentWriteEntry{TKey, TValue}.Pointer"/> factories) must surface a
    /// fail-loud diagnostic that calls out the LSM-engine ↔ SSTable-writer routing
    /// invariant by name. A generic "unknown ValueKind" message buries the drift; the
    /// fix names it explicitly.
    /// </summary>
    [Fact]
    public void Write_EntryWithUnsupportedValueKind_ThrowsRoutingInvariantViolation()
    {
        var sst = new Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<
            SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "value_kind_guard.dat");

        // Construct an out-of-range ValueKind via the primary ctor — neither the Inline()
        // nor Pointer() factory will allow this, so we go around them. (ValueKind is a
        // byte-typed enum with declared values 0x00 and 0x01; 0xFF is reserved for
        // "fail loud" testing only.)
        var bogusKind = (ValueKind)0xFF;
        var entry = new SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>(
            Key: new SerializableWrapper<int>(42),
            SequenceNumber: 1UL,
            Kind: bogusKind,
            InlineValue: new SerializableWrapper<string>("ignored"),
            PointerBytes: ReadOnlyMemory<byte>.Empty);

        Action act = () => sst.Write([entry]);

        var ex = act.Should().Throw<ArgumentException>().Which;

        // Routing-invariant naming: the diagnostic must point the reader at the drift,
        // not just say "unknown." This is the BuildConstraints / DecodeColumn pattern.
        var matchesRoutingInvariant =
            ex.Message.Contains("drift", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("routing", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("enumeration", StringComparison.OrdinalIgnoreCase);
        matchesRoutingInvariant.Should().BeTrue(
            "the message must call out the LSM-engine ↔ SSTable-writer drift, not just " +
            "'unknown' — a future engineer who hits this needs the diagnostic to point at " +
            "WHICH contract the new ValueKind violated. Got: " + ex.Message);

        ex.Message.Should().Contain("ValueKind",
            "the message must name the offending field");

        ex.Message.Should().Contain("Inline",
            "the message must enumerate the supported kinds so the reader sees the contrast");
        ex.Message.Should().Contain("ValueLogPointer",
            "the message must enumerate the supported kinds so the reader sees the contrast");
    }

    // ── 2. WriteToDisk dispatch default — dead-arm-by-construction guard ──

    /// <summary>
    /// The dispatch default at <c>SortedStringTable.WriteToDisk</c>'s ValueKind switch is
    /// dead by construction: <c>ValidateEntryInvariant</c> runs first and catches every
    /// bad <see cref="ValueKind"/> per-entry. Reaching the dispatch default means the
    /// validator and the writer have drifted internally — that's an
    /// <see cref="InvalidOperationException"/> condition (engine inconsistency), not an
    /// <see cref="ArgumentException"/> (caller error).
    /// <para>
    /// This test pins the new contract by invoking <c>WriteToDisk</c> directly via
    /// reflection, bypassing the validator. The production code must surface a
    /// routing-invariant <see cref="InvalidOperationException"/> rather than a generic
    /// "unknown" <see cref="ArgumentException"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void WriteToDisk_BypassingValidator_ThrowsInvalidOperationExceptionWithDriftMessage()
    {
        var sst = new Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<
            SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "writetodisk_guard.dat");

        var bogusKind = (ValueKind)0xFE;
        var entry = new SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>(
            Key: new SerializableWrapper<int>(1),
            SequenceNumber: 1UL,
            Kind: bogusKind,
            InlineValue: new SerializableWrapper<string>("ignored"),
            PointerBytes: ReadOnlyMemory<byte>.Empty);

        var entries = new List<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> { entry };

        // Reflection: invoke WriteToDisk directly to bypass ValidateEntryInvariant.
        var sstType = sst.GetType();
        var writeToDisk = sstType.GetMethod(
            "WriteToDisk",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WriteToDisk not found via reflection.");

        Action act = () => writeToDisk.Invoke(
            sst,
            [entries, new Dictionary<SerializableWrapper<int>, ulong>()]);

        var ex = act.Should().Throw<TargetInvocationException>()
            .Which.InnerException;
        ex.Should().BeOfType<InvalidOperationException>(
            "the dispatch default is dead by construction — reaching it means the validator " +
            "and the writer have drifted; that's an internal inconsistency, not caller error");
        var driftMatch = ex.Message.Contains("drift", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("ValidateEntryInvariant", StringComparison.Ordinal);
        driftMatch.Should().BeTrue(
            "the diagnostic must point at the validator/writer-drift relationship — same " +
            "shape as the DecodeColumn non-restartable guard. Got: " + ex.Message);
    }
}
