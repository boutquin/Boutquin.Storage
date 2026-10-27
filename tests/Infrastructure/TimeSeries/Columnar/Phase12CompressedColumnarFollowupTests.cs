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
using System.Buffers.Binary;
using System.Reflection;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Collection marker that forces <see cref="Phase12CompressedColumnarFollowupTests"/> to run
/// with no other collection in flight. That class reflection-mutates the process-global
/// <c>CodecRegistry</c> (flipping a tag's <c>IsImplemented</c> flag for the duration of one test
/// and restoring it on dispose). xUnit runs test classes in parallel by default, so without this
/// serialization any columnar test reading the registry concurrently could observe the flipped
/// flag and fail intermittently with a spurious "reserved but not implemented" error.
/// <c>DisableParallelization</c> keeps the mutation window from overlapping any other collection.
/// </summary>
[CollectionDefinition("CodecRegistryMutation", DisableParallelization = true)]
public sealed class CodecRegistryMutationCollection;

/// <summary>
/// Phase-12 follow-up — targets <see cref="CompressedColumnarSegmentFormat{TRecord}"/>
/// branches that the original Batch B push left uncovered: the catch-arm at L268 that
/// wraps non-<see cref="ColumnDecodeException"/> codec failures, the
/// <see cref="TruncatedSegmentException"/> raised by the internal <c>ReadExactly</c>
/// helper at L858–861 when the stream ends mid-column, and the
/// <c>!registered.IsImplemented</c> arm of <c>ValidateEncodingSupport</c> (L612–616)
/// which fires for any registry tag flagged as reserved-but-not-implemented.
/// </summary>
[Collection("CodecRegistryMutation")]
public sealed class Phase12CompressedColumnarFollowupTests
{
    private static CodecBarInt32 MakeInt32Bar(int n) => new()
    {
        Date = new DateOnly(2026, 1, 1).AddDays(n),
        Value = n * 1000,
    };

    // ── 1. Codec-failure wrap: non-ColumnDecodeException → ColumnDecodeException ──

    /// <summary>
    /// The catch arm at L268 of <c>ReadRowGroupsAsync</c> wraps any exception type that is
    /// neither <see cref="ColumnDecodeException"/> nor <see cref="OperationCanceledException"/>
    /// in a <see cref="ColumnDecodeException"/> carrying the column name, byte offset, and
    /// the underlying message. Triggered by zeroing out the column header on disk: the
    /// <see cref="DeltaVlqCodec"/> reads a varint from the start of its payload and surfaces
    /// an <see cref="EndOfStreamException"/> / <see cref="ArgumentException"/> when the
    /// header is absent — neither of which is a <see cref="ColumnDecodeException"/>, so the
    /// wrap arm fires.
    /// </summary>
    [Fact]
    public async Task ReadRowGroupsAsync_NonColumnDecodeException_WrappedAsColumnDecodeException()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 10).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        var bytes = stream.ToArray();

        // Locate the second column ("Value", DeltaVlq) by reading its descriptor offset and
        // length from the footer, then zero-fill its bytes on disk. The decode routines
        // read varint-prefixed metadata at the start of the payload — zero bytes produce a
        // zero-count read and a NotSupportedException / IndexOutOfRangeException downstream
        // (whichever comes first), neither of which is a ColumnDecodeException.
        var (valueOffset, valueLength) = LocateValueColumn(bytes);
        for (var i = 0; i < valueLength; i++)
        {
            bytes[(int)(valueOffset + i)] = 0;
        }

        using var corrupt = new MemoryStream(bytes);
        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                corrupt, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ConfigureAwait(true))
            {
            }
        };

        var ex = (await act.Should().ThrowAsync<ColumnDecodeException>().ConfigureAwait(true))
            .Which;
        ex.ColumnName.Should().Be("Value",
            "the wrap arm at L270–273 must carry the corrupted column's name");
        ex.InnerException.Should().NotBeNull(
            "the wrap arm preserves the underlying codec exception as InnerException for diagnostics");
        ex.InnerException.Should().NotBeOfType<ColumnDecodeException>(
            "only NON-ColumnDecodeException failures are wrapped — re-wrapping would lose the original");
    }

    // ── 2. ReadExactly truncation mid-column ────────────────────────────────────

    /// <summary>
    /// <c>ReadExactly</c> at L850–865 of <c>CompressedColumnarSegmentFormat</c> throws
    /// <see cref="TruncatedSegmentException"/> at L858–861 when the underlying stream ends
    /// before the requested column-byte count is satisfied. Triggered here by tampering the
    /// footer's first column-descriptor <c>ByteLength</c> to claim more bytes than the
    /// segment carries: <c>FooterReader.Read</c> succeeds (the footer payload itself is
    /// intact), then the per-column ReadExactly seeks to <c>ByteOffset</c> and tries to
    /// read the inflated length — hitting EOF mid-column.
    /// </summary>
    [Fact]
    public async Task ReadRowGroupsAsync_FooterClaimsLongerColumnThanStreamHas_ThrowsTruncatedSegmentException()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 10).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        var bytes = stream.ToArray();

        // Read the original footer, inflate the FIRST column's ByteLength by 1024 bytes
        // (well past the stream end), then re-emit the footer + trailer in place. The
        // segment-format ReadExactly seeks to ByteOffset and reads ByteLength bytes — the
        // last 1024 are absent, so L858–861 fires.
        var footer = ReadFooter(bytes);
        var inflated = footer.Columns[0] with { ByteLength = footer.Columns[0].ByteLength + 1024 };
        var newColumns = new List<ColumnDescriptor>(footer.Columns) { [0] = inflated };
        var tampered = new SegmentFooter(
            footer.FormatVersion,
            newColumns,
            footer.RowGroups,
            footer.ZoneMaps,
            footer.BitmapIndexes,
            footer.RowGroupCrcs,
            footer.RowGroupByteOffsets);

        var (footerStart, _) = LocateFooterPayloadBoundsForRewrite(bytes);
        using var rebuilt = new MemoryStream();
        rebuilt.Write(bytes, 0, footerStart);
        Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.FooterWriter.Write(rebuilt, tampered);
        rebuilt.Position = 0;

        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                rebuilt, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ConfigureAwait(true))
            {
            }
        };

        await act.Should().ThrowAsync<TruncatedSegmentException>().ConfigureAwait(true);
    }

    // ── 3. Reserved-encoding-tag rejection (registry tampering) ─────────────────

    /// <summary>
    /// <c>ValidateEncodingSupport</c> at L610–617 throws <see cref="UnsupportedEncodingException"/>
    /// with a "reserved but not implemented" message whenever a footer references a tag
    /// whose <see cref="CodecRegistry"/> entry has <c>IsImplemented = false</c>. In the
    /// shipped registry every tag is implemented, so this arm is unreachable from clean
    /// segments — but the contract exists for forward-compat: a later release may RESERVE
    /// a new tag (writer side) before the codec ships (reader side), and this guard rails
    /// segments written by such a future writer encountering an old reader. Triggered here
    /// by reflection-flipping a registered descriptor's <c>IsImplemented</c> flag, then
    /// reading a clean segment that uses that tag.
    /// </summary>
    [Fact]
    public async Task ValidateEncodingSupport_RegistryTagFlaggedNotImplemented_ThrowsReservedMessage()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 5).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        // The Value column on CodecBarInt32 uses ColumnEncodingTag.DeltaVlq. Reflection-
        // flip its CodecRegistry descriptor to IsImplemented = false for the duration of
        // this test, then restore it afterwards.
        using var swap = new IsImplementedSwap(ColumnEncodingTag.DeltaVlq, isImplemented: false);

        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ConfigureAwait(true))
            {
            }
        };

        var ex = (await act.Should().ThrowAsync<UnsupportedEncodingException>().ConfigureAwait(true))
            .Which;
        ex.Message.Should().Contain("reserved but not implemented",
            "the L612–616 arm carries the 'reserved but not implemented' phrasing — distinct from " +
            "the L600–608 'unknown encoding tag' message that fires when the tag is missing entirely");
        ex.EncodingTag.Should().Be((byte)ColumnEncodingTag.DeltaVlq);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers — Value column locator and registry-flag swap.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static (long Offset, long Length) LocateValueColumn(byte[] bytes)
    {
        using var probe = new MemoryStream(bytes);
        var footer = Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.FooterReader.Read(probe);
        var value = footer.Columns.First(c => c.Name == "Value");
        return (value.ByteOffset, value.ByteLength);
    }

    private static SegmentFooter ReadFooter(byte[] bytes)
    {
        using var probe = new MemoryStream(bytes);
        return Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.FooterReader.Read(probe);
    }

    /// <summary>
    /// Returns the byte offset where the footer payload begins (i.e., immediately after
    /// the column-data region). Used by tests that need to overwrite the footer with a
    /// tampered variant.
    /// </summary>
    private static (int FooterStart, int FooterPayloadLength) LocateFooterPayloadBoundsForRewrite(byte[] bytes)
    {
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailerStart, 4));
        var footerPayloadEnd = trailerStart - 4;
        var footerPayloadStart = footerPayloadEnd - footerLength;
        return (footerPayloadStart, footerLength);
    }

    /// <summary>
    /// Disposable that flips a <see cref="CodecRegistry"/> descriptor's <c>IsImplemented</c>
    /// flag for the lifetime of the swap and restores it on Dispose. The registry's
    /// dictionary is private static readonly; reflection rewrites the field in-place. Because the
    /// mutation is process-global, the enclosing class is pinned to the
    /// <c>CodecRegistryMutation</c> collection with <c>DisableParallelization = true</c> so no
    /// other test reads the registry while this swap is alive — xUnit runs test classes in
    /// parallel by default, so the swap window must be serialized assembly-wide.
    /// </summary>
    private sealed class IsImplementedSwap : IDisposable
    {
        private readonly ColumnEncodingTag _tag;
        private readonly CodecDescriptor _original;
        private readonly System.Collections.IDictionary _dict;

        public IsImplementedSwap(ColumnEncodingTag tag, bool isImplemented)
        {
            _tag = tag;

            var registryType = typeof(CodecRegistry);
            var dictField = registryType.GetField("s_descriptors", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("CodecRegistry.s_descriptors not found via reflection.");
            var dictObj = dictField.GetValue(null)
                ?? throw new InvalidOperationException("CodecRegistry.s_descriptors is null.");
            _dict = (System.Collections.IDictionary)dictObj;

            _original = (CodecDescriptor)_dict[tag]!;
            _dict[tag] = _original with { IsImplemented = isImplemented };
        }

        public void Dispose() => _dict[_tag] = _original;
    }
}
