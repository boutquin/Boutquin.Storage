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
using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Reads and writes the <c>CURRENT</c> manifest — the durable commit point for the set of
/// segments owned by an <see cref="LsmStorageEngine{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on LevelDB/RocksDB's <c>CURRENT</c> file. The manifest lists one row per segment,
/// written in scan order (oldest → newest). Each row carries the segment's filename plus
/// integrity metadata:
/// </para>
/// <list type="bullet">
/// <item><description><c>Crc32C</c> (Castagnoli / iSCSI variant) over the full segment file's
/// bytes at flush time — recovery recomputes and compares; a mismatch fails the open
/// (throws <see cref="InvalidDataException"/>, the <c>FailClosed</c> default disposition)
/// unless the engine opts into the <c>ExcludeAndSurface</c> disposition, which instead logs
/// the mismatch and skips the segment.</description></item>
/// <item><description><c>SizeBytes</c> captures the file length at flush time; combined with
/// CRC it guards against truncation and bit-rot, and feeds level-size accounting for
/// compaction without requiring a format revision.</description></item>
/// </list>
/// <para>
/// A write is atomic: the payload is staged in a sibling temp file, then
/// <see cref="DurableFile.AtomicReplace"/> renames it over <c>CURRENT</c> and fsyncs the
/// directory on POSIX so the pointer change survives a crash.
/// </para>
/// <para>
/// <b>Wire format.</b> UTF-8 text, one row per segment. Columns separated by a tab:
/// <c>{filename}\t{crc32c_hex}\t{size_bytes_decimal}\t{level_decimal}</c>. Every row
/// carries all four columns — v2.0.0 ships a single manifest format. Rows missing the
/// tab separators raise <see cref="InvalidDataException"/>; there is no fallback reader
/// for legacy filename-only rows.
/// </para>
/// <para>
/// <b>Recovery.</b> When <see cref="Read"/> returns <see langword="null"/> (no manifest
/// found), the directory is treated as a clean fresh start <i>only</i> when it also contains
/// no segment files; the engine writes an initial manifest on the next flush or compaction.
/// Segment files present on disk without a <c>CURRENT</c> manifest fail the open (throws
/// <see cref="InvalidDataException"/>) rather than being scanned — fail-closed, so a
/// truncated or partially-deleted manifest can never silently resurrect a stale segment set.
/// </para>
/// </remarks>
internal static class SegmentManifest
{
    /// <summary>The well-known manifest filename.</summary>
    internal const string FileName = "CURRENT";

    /// <summary>The largest level both the engine and the manifest wire format support.</summary>
    internal const int MaxLevel = 64;

    /// <summary>
    /// Per-segment metadata persisted in the manifest. Every row carries filename +
    /// Crc32C + SizeBytes + Level; there are no optional columns and no legacy shapes.
    /// </summary>
    internal readonly record struct Entry(string FileName, uint Crc32C, long SizeBytes, int Level = 0);

    /// <summary>
    /// Returns <see langword="true"/> if a manifest exists in <paramref name="segmentFolder"/>.
    /// </summary>
    public static bool Exists(string segmentFolder) =>
        File.Exists(Path.Combine(segmentFolder, FileName));

    /// <summary>
    /// Reads the manifest from <paramref name="segmentFolder"/>, returning the segment
    /// filenames in scan order, or <see langword="null"/> if no manifest exists.
    /// </summary>
    public static IReadOnlyList<string>? Read(string segmentFolder)
    {
        var entries = ReadEntries(segmentFolder);
        if (entries is null)
        {
            return null;
        }

        var result = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            result.Add(entry.FileName);
        }
        return result;
    }

    /// <summary>
    /// Reads the manifest with per-segment metadata (filename, CRC32C, size, level).
    /// Returns <see langword="null"/> if no manifest file exists. Every row must carry
    /// all four tab-separated columns; malformed rows raise <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyList<Entry>? ReadEntries(string segmentFolder)
    {
        var path = Path.Combine(segmentFolder, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        List<string> lines;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            lines = DecodeRows(file);
        }

        var result = new List<Entry>(lines.Count);
        for (var lineNo = 0; lineNo < lines.Count; lineNo++)
        {
            var trimmed = lines[lineNo].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var fields = trimmed.Split('\t');
            if (fields.Length != 4)
            {
                throw new InvalidDataException(
                    $"Segment manifest row {lineNo + 1} is malformed: expected exactly " +
                    $"4 tab-separated columns ('filename\\tcrc32c_hex\\tsize_bytes\\tlevel'), " +
                    $"got {fields.Length} column(s). Line: '{trimmed}'.");
            }

            var fileName = fields[0];
            // The filename is used at recovery to read a segment and at compaction to delete
            // it. A row carrying a directory separator, a '..' component, or an absolute/rooted
            // path would escape the segment folder — a path-traversal read/delete primitive.
            // Reject anything that is not a bare filename as manifest corruption.
            if (fileName.Length == 0
                || fileName.Contains('/', StringComparison.Ordinal)
                || fileName.Contains('\\', StringComparison.Ordinal)
                || Path.IsPathRooted(fileName)
                || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Segment manifest row {lineNo + 1} has an invalid segment filename " +
                    $"'{fileName}': expected a bare filename with no directory separators, " +
                    $"no '..' component, and no absolute path.");
            }

            if (!uint.TryParse(fields[1], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var crc))
            {
                throw new InvalidDataException(
                    $"Segment manifest row {lineNo + 1} has non-hex CRC32C column: '{fields[1]}'.");
            }
            if (!long.TryParse(fields[2],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var size))
            {
                throw new InvalidDataException(
                    $"Segment manifest row {lineNo + 1} has non-integer size column: '{fields[2]}'.");
            }
            if (!int.TryParse(fields[3],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var level) || level < 0)
            {
                throw new InvalidDataException(
                    $"Segment manifest row {lineNo + 1} has invalid level column: '{fields[3]}'.");
            }

            if (level > MaxLevel)
            {
                throw new InvalidDataException(
                    $"Segment manifest level {level} exceeds maximum ({MaxLevel}).");
            }

            result.Add(new Entry(fileName, crc, size, level));
        }

        return result;
    }

    /// <summary>
    /// Atomically rewrites the manifest with the supplied per-segment metadata.
    /// </summary>
    public static void WriteEntries(string segmentFolder, IReadOnlyList<Entry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentFolder);
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            if (entry.Level is < 0 or > MaxLevel)
            {
                throw new ArgumentException(
                    $"Segment level {entry.Level} must be between 0 and {MaxLevel}.", nameof(entries));
            }
            if (!IsRoundTrippableName(entry.FileName))
            {
                throw new ArgumentException(
                    $"Segment filename '{entry.FileName}' cannot be recorded in the manifest: the reader would " +
                    "reject it or read it back differently (leading whitespace or byte-order mark, a control " +
                    "character such as tab or a line break, an unpaired surrogate, or a path component).",
                    nameof(entries));
            }
        }

        var finalPath = Path.Combine(segmentFolder, FileName);
        var tempPath = finalPath + $".tmp.{Guid.NewGuid():N}";

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, s_rowEncoding))
            {
                foreach (var entry in entries)
                {
                    // Format: {filename}\t{crc32c_hex}\t{size_bytes_decimal}\t{level_decimal}.
                    // Lowercase hex keeps the manifest diff-friendly; the reader accepts either case.
                    writer.WriteLine(
                        $"{entry.FileName}\t{entry.Crc32C:x8}\t{entry.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}\t{entry.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            DurableFile.AtomicReplace(tempPath, finalPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
            throw;
        }
    }

    // The one row encoding the writer uses and the round-trip check encodes with: UTF-8 without a
    // preamble, replacing anything it cannot encode (an unpaired surrogate) — StreamWriter's default.
    private static readonly UTF8Encoding s_rowEncoding = new(encoderShouldEmitUTF8Identifier: false);

    // The one row decoding the reader uses and the round-trip check decodes with. Byte-order-mark
    // detection is on, so a leading EF BB BF (the UTF-8 encoding of U+FEFF) is consumed as a preamble.
    private static List<string> DecodeRows(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// Whether <paramref name="name"/> survives a manifest write → read round trip verbatim and passes
    /// the reader's bare-filename check. Rather than enumerate what the reader alters, the check encodes
    /// a first row carrying the name exactly as <see cref="WriteEntries"/> does and decodes it exactly as
    /// <see cref="ReadEntries"/> does, so the writer and reader cannot drift on preamble detection,
    /// encoder replacement, trimming or line splitting. Control characters and path components are
    /// refused outright. The engine applies the same rule to its segment prefix, so it never produces a
    /// filename this manifest cannot record.
    /// </summary>
    internal static bool IsRoundTrippableName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c) || c == '/' || c == '\\')
            {
                return false;
            }
        }

        var row = $"{name}\t00000000\t0\t0{Environment.NewLine}";
        using var encoded = new MemoryStream(s_rowEncoding.GetBytes(row));
        var decoded = DecodeRows(encoded);
        if (decoded.Count != 1)
        {
            return false;
        }

        var fields = decoded[0].Trim().Split('\t');
        return fields.Length == 4
            && string.Equals(fields[0], name, StringComparison.Ordinal)
            && !Path.IsPathRooted(name)
            && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes the CRC32C (Castagnoli, iSCSI variant) over the entire bytes of
    /// <paramref name="filePath"/>. Returns the CRC and the file length so callers can
    /// record both in a manifest <see cref="Entry"/> without a second stat.
    /// </summary>
    public static (uint Crc32C, long SizeBytes) ComputeFileCrc32C(string filePath)
    {
        // Castagnoli CRC32C lives in Infrastructure.Algorithms.Crc32C — System.IO.Hashing v9
        // exposes Crc32 (Ethernet) but not the iSCSI/SSE4.2 Castagnoli variant, so we maintain
        // our own table-based implementation shared across SSTable blocks (Phase C2),
        // columnar footers (Phase E3), and segment files (Phase A4).
        // P4.7 — accumulate CRC incrementally rather than double-buffering into a
        // MemoryStream. The Append/Finalize pair drives the same table-based CRC32C as
        // HashToUInt32 but feeds each 64 KiB chunk directly into the running state, keeping
        // peak memory O(1) regardless of file size.
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var crcState = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.InitialState;
            long total = 0;
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                Boutquin.Storage.Infrastructure.Algorithms.Crc32C.Append(buffer.AsSpan(0, read), ref crcState);
                total += read;
            }
            return (Boutquin.Storage.Infrastructure.Algorithms.Crc32C.Finalize(crcState), total);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
