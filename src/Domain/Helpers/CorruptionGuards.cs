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
namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// Validate-then-convert guards for narrowing BCL conversions performed on untrusted
/// (decoded on-disk / wire) values. A bare <see cref="DateOnly.FromDayNumber(int)"/> (or any
/// other range-narrowing BCL constructor) called directly on a decoded value leaks a raw
/// <see cref="ArgumentOutOfRangeException"/> out of a public read API when the input is forged
/// or bit-rotted — breaking the repository's contract that corrupt input surfaces a TYPED
/// corruption exception. These guards range-check first so each decode site can either convert
/// safely or raise its own contextually-correct typed corruption exception.
/// </summary>
/// <remarks>
/// <para>The <c>DeltaOfDeltaVlqCodec</c> already inlines this discipline (<c>ValidateDayNumber</c>
/// guards every <c>FromDayNumber</c>); this helper hoists that established convention into a single
/// reusable surface so every hand-written decoder and every source-generator-emitted deserializer
/// shares one bounds definition rather than re-deriving <c>[0, 3 652 058]</c> at each call site.</para>
/// <para><see cref="IsValidDayNumber"/> is the primitive: a site holding richer corruption context
/// (a byte offset, a file index) checks it and throws its own typed exception. <see cref="ToDateOnly"/>
/// is the convenience throwing form for sites whose convention is <see cref="InvalidDataException"/>
/// (codecs, source-generator emitters).</para>
/// </remarks>
public static class CorruptionGuards
{
    /// <summary>
    /// True when <paramref name="dayNumber"/> is a valid <see cref="DateOnly"/> day number — i.e.
    /// within <c>[DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber]</c> = <c>[0, 3 652 058]</c>.
    /// Accepts <see cref="long"/> so a 64-bit decoded value (varint, ticks-shaped field) can be
    /// range-checked without a narrowing cast that would itself overflow.
    /// </summary>
    public static bool IsValidDayNumber(long dayNumber) =>
        dayNumber >= DateOnly.MinValue.DayNumber && dayNumber <= DateOnly.MaxValue.DayNumber;

    /// <summary>
    /// Range-checks <paramref name="dayNumber"/> and converts it to a <see cref="DateOnly"/>,
    /// throwing <see cref="InvalidDataException"/> (the codec / emitter corruption convention)
    /// instead of letting <see cref="DateOnly.FromDayNumber(int)"/> raise a raw
    /// <see cref="ArgumentOutOfRangeException"/> on a forged out-of-range value.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// <paramref name="dayNumber"/> is outside <c>[0, 3 652 058]</c>.
    /// </exception>
    public static DateOnly ToDateOnly(long dayNumber)
    {
        if (!IsValidDayNumber(dayNumber))
        {
            throw new InvalidDataException(
                $"Decoded day-number {dayNumber} is outside the DateOnly range "
                + $"[{DateOnly.MinValue.DayNumber}, {DateOnly.MaxValue.DayNumber}]; input is corrupt.");
        }

        return DateOnly.FromDayNumber((int)dayNumber);
    }

    /// <summary>
    /// True when <paramref name="ticks"/> is a valid <see cref="TimeOnly"/> tick count — i.e.
    /// within <c>[0, TimeOnly.MaxValue.Ticks]</c> = <c>[0, 863 999 999 999]</c> (one tick short of
    /// 24 hours). Accepts <see cref="long"/> because the decoded value is a signed 64-bit field.
    /// </summary>
    public static bool IsValidTimeOnlyTicks(long ticks) =>
        ticks >= 0 && ticks <= TimeOnly.MaxValue.Ticks;

    /// <summary>
    /// Range-checks <paramref name="ticks"/> and converts it to a <see cref="TimeOnly"/>,
    /// throwing <see cref="InvalidDataException"/> (the codec / emitter corruption convention)
    /// instead of letting <see cref="TimeOnly(long)"/> raise a raw
    /// <see cref="ArgumentOutOfRangeException"/> on a forged out-of-range tick count.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// <paramref name="ticks"/> is outside <c>[0, 863 999 999 999]</c>.
    /// </exception>
    public static TimeOnly ToTimeOnly(long ticks)
    {
        if (!IsValidTimeOnlyTicks(ticks))
        {
            throw new InvalidDataException(
                $"Decoded tick count {ticks} is outside the TimeOnly range "
                + $"[0, {TimeOnly.MaxValue.Ticks}]; input is corrupt.");
        }

        return new TimeOnly(ticks);
    }

    /// <summary>
    /// True when the four little-endian <c>int</c> words decoded for a <see cref="decimal"/> form a
    /// valid <see cref="decimal(int[])"/> bit layout. Only the flags word (<paramref name="flags"/>,
    /// the fourth word) carries forbidden states: bits 16–23 are the scale (must be <c>0..28</c>),
    /// bit 31 is the sign, and every other bit MUST be zero. The 96-bit mantissa words
    /// (<c>lo</c>/<c>mid</c>/<c>hi</c>) are unconstrained, so they are not parameters here.
    /// </summary>
    /// <remarks>
    /// <see cref="decimal(int[])"/> (and the span overload) throw a raw <see cref="ArgumentException"/>
    /// when the flags word sets a reserved bit or a scale &gt; 28. This primitive lets a decode site
    /// holding richer corruption context throw its own typed exception instead of leaking that raw
    /// BCL exception out of a public read API. <see cref="ToDecimal(System.ReadOnlySpan{int})"/> is
    /// the convenience throwing form for sites whose convention is <see cref="InvalidDataException"/>.
    /// </remarks>
    public static bool IsValidDecimalBits(int flags) =>
        // Reserved bits are everything except the scale byte (bits 16-23 = 0x00FF0000) and the sign
        // bit (bit 31 = 0x80000000). Their complement (0x7F00FFFF) must be all-zero, AND the scale
        // (low byte of the high 16 bits) must not exceed the decimal cap of 28.
        (flags & 0x7F00FFFF) == 0 && ((flags >> 16) & 0xFF) <= 28;

    /// <summary>
    /// Range-checks the four decoded <c>int</c> words of a <see cref="decimal"/> and constructs it,
    /// throwing <see cref="InvalidDataException"/> (the codec / emitter corruption convention)
    /// instead of letting <see cref="decimal(System.ReadOnlySpan{int})"/> raise a raw
    /// <see cref="ArgumentException"/> when the flags word carries a reserved bit or a scale &gt; 28.
    /// </summary>
    /// <param name="bits">Exactly four little-endian words: <c>lo</c>, <c>mid</c>, <c>hi</c>, flags.</param>
    /// <exception cref="InvalidDataException">
    /// <paramref name="bits"/> is not four words, or its flags word is not a valid decimal layout.
    /// </exception>
    public static decimal ToDecimal(ReadOnlySpan<int> bits)
    {
        if (bits.Length != 4)
        {
            throw new InvalidDataException(
                $"Decoded decimal requires exactly 4 int words; found {bits.Length}. Input is corrupt.");
        }

        if (!IsValidDecimalBits(bits[3]))
        {
            throw new InvalidDataException(
                $"Decoded decimal flags word 0x{bits[3]:X8} sets a reserved bit or a scale > 28; "
                + "input is corrupt.");
        }

        return new decimal(bits);
    }

    /// <summary>
    /// The DateTimeOffset offset cap: <see cref="DateTimeOffset"/> rejects any offset whose magnitude
    /// exceeds 14 hours = 840 minutes.
    /// </summary>
    private const long MaxDateTimeOffsetMinutes = 14 * 60;

    /// <summary>
    /// True when <paramref name="utcTicks"/> is a valid <see cref="DateTime"/> tick count
    /// (<c>[DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks]</c> = <c>[0, 3 155 378 975 999 999 999]</c>)
    /// AND <paramref name="offsetMinutes"/> is a whole-minute offset within the
    /// <see cref="DateTimeOffset"/> cap of ±14 hours. Both arguments are <see cref="long"/> so a
    /// 64-bit decoded field is range-checked without a narrowing cast that would itself overflow.
    /// </summary>
    public static bool IsValidDateTimeOffsetParts(long utcTicks, long offsetMinutes) =>
        utcTicks >= DateTime.MinValue.Ticks
        && utcTicks <= DateTime.MaxValue.Ticks
        && offsetMinutes >= -MaxDateTimeOffsetMinutes
        && offsetMinutes <= MaxDateTimeOffsetMinutes;

    /// <summary>
    /// Range-checks a decoded UTC tick count and whole-minute offset and constructs the
    /// <see cref="DateTimeOffset"/> at that offset, throwing <see cref="InvalidDataException"/> (the
    /// codec / emitter corruption convention) instead of letting <see cref="DateTime(long)"/> or the
    /// <see cref="DateTimeOffset"/> constructor raise a raw <see cref="ArgumentOutOfRangeException"/>
    /// on a forged out-of-range tick count or an offset beyond the ±14h cap. This is the helper for
    /// the <c>OrderedByteHelpers</c> convention that stores the instant as a UTC tick count.
    /// </summary>
    /// <param name="utcTicks">The instant as a UTC tick count.</param>
    /// <param name="offsetMinutes">The offset from UTC in whole minutes (±840).</param>
    /// <exception cref="InvalidDataException">
    /// <paramref name="utcTicks"/> is outside the <see cref="DateTime"/> tick range,
    /// <paramref name="offsetMinutes"/> is outside ±14 hours, or the pair's local time is out of range.
    /// </exception>
    public static DateTimeOffset ToDateTimeOffsetFromUtc(long utcTicks, long offsetMinutes)
    {
        if (!IsValidDateTimeOffsetParts(utcTicks, offsetMinutes))
        {
            throw new InvalidDataException(
                $"Decoded DateTimeOffset parts (utcTicks={utcTicks}, offsetMinutes={offsetMinutes}) "
                + $"are outside the DateTime tick range [0, {DateTime.MaxValue.Ticks}] / the ±14h "
                + "offset cap; input is corrupt.");
        }

        // Both parts are in range, but the local wall-clock time (UTC + offset) can still leave the
        // DateTime range — a cross-field constraint no valid writer violates. Map it to the same
        // typed corruption exception as the ToDateTimeOffsetFromLocal sibling.
        try
        {
            return new DateTimeOffset(utcTicks, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException(
                $"Decoded DateTimeOffset parts (utcTicks={utcTicks}, offsetMinutes={offsetMinutes}) "
                + "place the local time outside the DateTime range; input is corrupt.",
                ex);
        }
    }

    /// <summary>
    /// Range-checks a decoded LOCAL (unspecified-kind) wall-clock tick count and whole-minute offset
    /// and constructs the <see cref="DateTimeOffset"/>, throwing <see cref="InvalidDataException"/>
    /// (the codec / emitter corruption convention) instead of letting <see cref="DateTime(long)"/> or
    /// the <see cref="DateTimeOffset(DateTime, TimeSpan)"/> constructor raise a raw
    /// <see cref="ArgumentOutOfRangeException"/>. This is the helper for the tagged-wire emitter
    /// convention that stores <c>value.DateTime.Ticks</c> (the local wall-clock ticks) plus the offset.
    /// </summary>
    /// <remarks>
    /// Besides the independent part ranges, the <see cref="DateTimeOffset(DateTime, TimeSpan)"/>
    /// constructor also rejects a (localTicks, offset) pair whose UTC-equivalent instant falls outside
    /// the <see cref="DateTime"/> range — a cross-field constraint a pure per-field check cannot fully
    /// express. The construction is therefore wrapped so that residual edge re-throws as the same typed
    /// <see cref="InvalidDataException"/> rather than a raw <see cref="ArgumentOutOfRangeException"/>.
    /// </remarks>
    /// <param name="localTicks">The local wall-clock instant as a <see cref="DateTime"/> tick count.</param>
    /// <param name="offsetMinutes">The offset from UTC in whole minutes (±840).</param>
    /// <exception cref="InvalidDataException">
    /// <paramref name="localTicks"/> is outside the <see cref="DateTime"/> tick range,
    /// <paramref name="offsetMinutes"/> is outside ±14 hours, or the pair's UTC instant is out of range.
    /// </exception>
    public static DateTimeOffset ToDateTimeOffsetFromLocal(long localTicks, long offsetMinutes)
    {
        if (!IsValidDateTimeOffsetParts(localTicks, offsetMinutes))
        {
            throw new InvalidDataException(
                $"Decoded DateTimeOffset parts (localTicks={localTicks}, offsetMinutes={offsetMinutes}) "
                + $"are outside the DateTime tick range [0, {DateTime.MaxValue.Ticks}] / the ±14h "
                + "offset cap; input is corrupt.");
        }

        try
        {
            return new DateTimeOffset(
                new DateTime(localTicks, DateTimeKind.Unspecified),
                TimeSpan.FromMinutes(offsetMinutes));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Cross-field: localTicks − offset underflows/overflows the DateTime UTC range even though
            // each part is independently in range. Map the raw BCL exception to the typed corruption signal.
            throw new InvalidDataException(
                $"Decoded DateTimeOffset (localTicks={localTicks}, offsetMinutes={offsetMinutes}) has a "
                + "UTC-equivalent instant outside the DateTime range; input is corrupt.", ex);
        }
    }
}
