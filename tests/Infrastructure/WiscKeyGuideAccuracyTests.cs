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
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins what the WiscKey value-log guides say about bytes and knobs this repository can execute.
/// Each test drives the real writer, manifest or engine first and then decodes or reflects over the
/// result using only the layout, table or rule the shipped text states, so the prose cannot drift
/// from the code (the frame diagram once documented a 64-bit length that no writer emits, and the
/// configuration table once listed a knob that no longer exists).
/// </summary>
public sealed partial class WiscKeyGuideAccuracyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"WiscKeyGuide_{Guid.NewGuid():N}");

    public WiscKeyGuideAccuracyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a scratch directory.
        }
    }

    // ───────────────────────── entry-frame layout ─────────────────────────

    private static readonly Dictionary<string, int> s_widths = new(StringComparer.Ordinal)
    {
        ["u8"] = 1,
        ["u16"] = 2,
        ["u32"] = 4,
        ["u64"] = 8,
    };

    /// <summary>The first frame of a segment the real writer produced, plus the inputs that went in.</summary>
    private async Task<(byte[] Segment, byte[] Payload, ulong Seqno)> WriteOneFrameAsync(int payloadLength, ulong seqno)
    {
        var payload = new byte[payloadLength];
        new Random(unchecked((int)seqno)).NextBytes(payload);

        var directory = Path.Combine(_dir, $"vl_{Guid.NewGuid():N}");
        await using var manifest = new ValueLogManifest(Path.Combine(directory, "valuelog.manifest"));
        await using (var writer = await ValueLogWriter.OpenAsync(directory, manifest, ValueLogOptions.Default).ConfigureAwait(true))
        {
            await writer.AppendAsync(payload, seqno).ConfigureAwait(true);
        }

        return (File.ReadAllBytes(ValueLogWriter.ResolveSegmentPath(directory, 0)), payload, seqno);
    }

    /// <summary>
    /// Decodes the first frame of <paramref name="segment"/> with an ordered <c>name:type</c> field list
    /// (<c>payload</c> carries no type: it spans the decoded <c>length</c> bytes). Decoding never throws
    /// on a wrong layout; it reports what it read so the caller can assert on it.
    /// </summary>
    private static (Dictionary<string, ulong> Fields, byte[]? Payload, long End) DecodeFirstFrame(
        byte[] segment, IReadOnlyList<(string Name, string? Type)> layout)
    {
        var fields = new Dictionary<string, ulong>(StringComparer.Ordinal);
        byte[]? payload = null;
        long pos = 8; // after the 8-byte segment file header

        foreach (var (name, type) in layout)
        {
            if (type is null)
            {
                var length = fields["length"];
                if (length > (ulong)(segment.Length - pos))
                {
                    return (fields, null, -1);
                }

                payload = segment.AsSpan((int)pos, (int)length).ToArray();
                pos += (long)length;
                continue;
            }

            var width = s_widths[type];
            if (pos + width > segment.Length)
            {
                return (fields, payload, -1);
            }

            var span = segment.AsSpan((int)pos, width);
            fields[name] = width switch
            {
                1 => span[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(span),
                _ => BinaryPrimitives.ReadUInt64LittleEndian(span),
            };
            pos += width;
        }

        return (fields, payload, pos);
    }

    private static List<(string Name, string? Type)> ParseFrameDiagram(string diagram) =>
        [.. diagram.Split('|').Select(part =>
        {
            var pieces = part.Trim().Split(':');
            return (pieces[0], pieces.Length > 1 ? pieces[1] : null);
        })];

    /// <summary>Asserts a layout decodes the writer's first frame field for field.</summary>
    private static void AssertDecodesWriterFrame(
        string source, IReadOnlyList<(string Name, string? Type)> layout, byte[] segment, byte[] payload, ulong seqno)
    {
        layout.Select(f => f.Name).Should().StartWith(["length"], $"{source} must lead with the length");

        var (fields, decodedPayload, end) = DecodeFirstFrame(segment, layout);

        fields["length"].Should().Be((ulong)payload.Length,
            $"{source} must decode the length the writer wrote ({payload.Length}); a wider or misplaced field reads garbage");
        fields.Keys.Single(k => k.Contains("kind", StringComparison.Ordinal)).Should().NotBeNull();
        fields[fields.Keys.Single(k => k.Contains("kind", StringComparison.Ordinal))].Should().Be(ValueLogFrame.KindPayload);
        fields[fields.Keys.Single(k => k.Contains("seqno", StringComparison.Ordinal))].Should().Be(seqno);
        decodedPayload.Should().Equal(payload);
        end.Should().Be(segment.Length, $"{source} must account for every byte of the frame");

        var lengthBytes = segment.AsSpan(8, 4);
        var headerRest = segment.AsSpan(8 + 8, 9);
        var headerCrcField = fields.Keys.Single(k => k.Contains("header", StringComparison.Ordinal));
        fields[headerCrcField].Should().Be(Crc32C.HashToUInt32(lengthBytes, headerRest),
            $"{source} names a header checksum over length, kind and seqno");

        var payloadCrcField = fields.Keys.Last();
        fields[payloadCrcField].Should().Be(Crc32C.HashToUInt32(payload),
            $"{source} names a payload checksum as its trailing field");
    }

    [Theory]
    [InlineData(1, 1UL)]
    [InlineData(300, 7UL)]
    [InlineData(4099, 0x0102030405060708UL)]
    public async Task ArchitectureFrameDiagram_DecodesTheFrameTheWriterEmitted(int payloadLength, ulong seqno)
    {
        var (segment, payload, _) = await WriteOneFrameAsync(payloadLength, seqno).ConfigureAwait(true);

        var line = ReadRepoFile("ARCHITECTURE.md")
            .Split('\n')
            .Single(l => l.Contains("valuelog_NNNNNN.log", StringComparison.Ordinal) && l.Contains("entry frames", StringComparison.Ordinal));
        var diagram = EntryFramesRegex().Match(line);
        diagram.Success.Should().BeTrue("ARCHITECTURE.md must keep a bracketed entry-frame field list");

        AssertDecodesWriterFrame("ARCHITECTURE.md", ParseFrameDiagram(diagram.Groups["fields"].Value), segment, payload, seqno);
    }

    [Fact]
    public async Task ArchitectureReadPath_NamesTheHeaderAndPayloadChecksumsTheReaderVerifies()
    {
        var (segment, _, _) = await WriteOneFrameAsync(64, 3).ConfigureAwait(true);

        // Execute the claim first: flipping a header byte and a payload byte are each refused by the reader.
        foreach (var offset in new[] { 8 + 9, 8 + ValueLogFrame.EntryHeaderSize })
        {
            var directory = Path.Combine(_dir, $"vlread_{Guid.NewGuid():N}");
            await using var manifest = new ValueLogManifest(Path.Combine(directory, "valuelog.manifest"));
            ValueLogPointer pointer;
            await using (var writer = await ValueLogWriter.OpenAsync(directory, manifest, ValueLogOptions.Default).ConfigureAwait(true))
            {
                pointer = await writer.AppendAsync(new byte[64], 3).ConfigureAwait(true);
            }

            var path = ValueLogWriter.ResolveSegmentPath(directory, 0);
            var bytes = File.ReadAllBytes(path);
            bytes[offset] ^= 0x01;
            File.WriteAllBytes(path, bytes);

            using var reader = new ValueLogReader(directory, ValueLogOptions.Default);
            var read = () => reader.Read(pointer);
            read.Should().Throw<CorruptValueLogPointerException>($"a flipped byte at segment offset {offset} must be refused");
        }

        segment.Length.Should().BeGreaterThan(0);

        var readPath = ReadSection("ARCHITECTURE.md", "ValueLogReader.Read(pointer)", "Segment files are named");
        readPath.Should().Contain("header CRC32C", "the reader verifies the header checksum before trusting the length");
        readPath.Should().Contain("payload CRC32C", "the reader verifies the payload checksum after reading the payload");
    }

    [Fact]
    public void ArchitectureDocument_NamesEveryCheckedLogChecksumAsCrc32C()
    {
        // Every durable log in this repository checksums with CRC32C (Castagnoli); a bare "CRC32" label is the
        // IEEE polynomial no writer here uses.
        ReadRepoFile("ARCHITECTURE.md").Should().NotMatchRegex(@"CRC32(?!C)",
            "ARCHITECTURE.md must name the Castagnoli polynomial the logs actually use");
    }

    [Theory]
    [InlineData(1, 1UL)]
    [InlineData(300, 7UL)]
    [InlineData(4099, 0x0102030405060708UL)]
    public async Task WiscKeyGuideFrameTable_DecodesTheFrameTheWriterEmitted(int payloadLength, ulong seqno)
    {
        var (segment, payload, _) = await WriteOneFrameAsync(payloadLength, seqno).ConfigureAwait(true);

        var table = ReadRepoFile("docs/wisckey-value-log.md");
        var start = table.IndexOf("### Entry frame", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var section = table[start..table.IndexOf("CRC32C uses the Castagnoli", start, StringComparison.Ordinal)];

        var layout = new List<(string Name, string? Type)>();
        foreach (System.Text.RegularExpressions.Match row in FrameTableRowRegex().Matches(section))
        {
            var size = row.Groups["size"].Value;
            layout.Add((row.Groups["name"].Value, size == "N" ? null : "u" + (int.Parse(size, CultureInfo.InvariantCulture) * 8).ToString(CultureInfo.InvariantCulture)));
        }

        layout.Should().HaveCount(6, "the guide documents six frame fields");
        AssertDecodesWriterFrame("docs/wisckey-value-log.md", layout, segment, payload, seqno);
    }

    // ───────────────────────── manifest record layout ─────────────────────────

    [Fact]
    public async Task WiscKeyGuideManifestTable_DecodesTheRecordsTheManifestEmitted()
    {
        var path = Path.Combine(_dir, "valuelog.manifest");
        await using (var manifest = new ValueLogManifest(path))
        {
            await manifest.RecordAddAsync(0).ConfigureAwait(true);
            await manifest.RecordSealAsync(0, new ValueLogManifest.SegmentSeal(1234, 5, 0xDEADBEEF)).ConfigureAwait(true);
            await manifest.RecordAddAsync(1).ConfigureAwait(true);
            await manifest.RecordDeleteAsync(0).ConfigureAwait(true);
        }

        var guide = ReadRepoFile("docs/wisckey-value-log.md");
        var start = guide.IndexOf("### Manifest", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var section = guide[start..guide.IndexOf("## Configuration", start, StringComparison.Ordinal)];

        var rows = ManifestRowRegex().Matches(section)
            .Select(m => (Offset: int.Parse(m.Groups["off"].Value, CultureInfo.InvariantCulture),
                          Size: int.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture),
                          Name: m.Groups["name"].Value))
            .ToList();

        var recordSize = rows.Sum(r => r.Size);
        recordSize.Should().Be(ValueLogManifest.RecordSize, "the guide's record table must add up to the record the manifest writes");
        section.Should().NotMatchRegex(@"(?<!\d)9-byte", "the record is no longer nine bytes");

        var bytes = File.ReadAllBytes(path);
        bytes.Length.Should().Be(ValueLogManifest.HeaderSize + (4 * recordSize));

        ulong Field(int record, string name)
        {
            var row = rows.Single(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            var span = bytes.AsSpan(ValueLogManifest.HeaderSize + (record * recordSize) + row.Offset, row.Size);
            return row.Size switch
            {
                1 => span[0],
                4 => BinaryPrimitives.ReadUInt32LittleEndian(span),
                _ => BinaryPrimitives.ReadUInt64LittleEndian(span),
            };
        }

        Enumerable.Range(0, 4).Select(i => (byte)Field(i, "kind")).Should().Equal(
            ValueLogManifest.KindAdd, ValueLogManifest.KindSeal, ValueLogManifest.KindAdd, ValueLogManifest.KindDelete);
        Enumerable.Range(0, 4).Select(i => (uint)Field(i, "fileIndex")).Should().Equal(0u, 0u, 1u, 0u);
        Field(1, "sealedLength").Should().Be(1234UL);
        Field(1, "frameCount").Should().Be(5UL);
        Field(1, "segmentCrc32c").Should().Be(0xDEADBEEFUL);

        var crcRow = rows.Single(r => string.Equals(r.Name, "crc32c", StringComparison.Ordinal));
        for (var i = 0; i < 4; i++)
        {
            var record = bytes.AsSpan(ValueLogManifest.HeaderSize + (i * recordSize), recordSize);
            Field(i, "crc32c").Should().Be(Crc32C.HashToUInt32(record[..crcRow.Offset]),
                "the final field is a CRC32C over every field before it");
        }

        section.Should().Contain("0x02", "the guide must name the seal record kind");
    }

    // ───────────────────────── options table ─────────────────────────

    [Fact]
    public void WiscKeyGuideConfigurationTable_ListsExactlyTheOptionsTheRecordDeclares()
    {
        var guide = ReadRepoFile("docs/wisckey-value-log.md");
        var start = guide.IndexOf("## Configuration", StringComparison.Ordinal);
        var section = guide[start..guide.IndexOf("## Tuning guide", start, StringComparison.Ordinal)];

        var documented = ConfigRowRegex().Matches(section)
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["default"].Value, StringComparer.Ordinal);

        var real = typeof(ValueLogOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        documented.Keys.Should().BeEquivalentTo(real,
            "the table is presented as the complete option set: no removed knob, no undocumented one");

        foreach (var property in typeof(ValueLogOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var actual = property.GetValue(ValueLogOptions.Default)!;
            var text = documented[property.Name].Replace("`", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            if (actual is Enum)
            {
                text.Should().Be(actual.ToString(), $"the documented default of {property.Name}");
                continue;
            }

            var multiplier = text.EndsWith("MiB", StringComparison.Ordinal) ? 1024L * 1024 : 1L;
            var number = long.Parse(text.Replace("MiB", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture) * multiplier;
            number.Should().Be(Convert.ToInt64(actual, CultureInfo.InvariantCulture), $"the documented default of {property.Name}");
        }

        guide.Should().NotContain("LZ4", "the value log has no compression: the tuning guide must not promise savings from it");
    }

    [Fact]
    public async Task WiscKeyGuide_StatesWhatValidateCrcOnReadDisables()
    {
        // Execute the claim: with Never, a flipped payload byte is served rather than refused.
        var directory = Path.Combine(_dir, $"vlcrc_{Guid.NewGuid():N}");
        var options = ValueLogOptions.Default with { ValidateCrcOnRead = Boutquin.Storage.Domain.Enums.CrcValidationMode.Never };
        ValueLogPointer pointer;
        await using (var manifest = new ValueLogManifest(Path.Combine(directory, "valuelog.manifest")))
        {
            await using var writer = await ValueLogWriter.OpenAsync(directory, manifest, options).ConfigureAwait(true);
            pointer = await writer.AppendAsync(new byte[32], 9).ConfigureAwait(true);
        }

        var path = ValueLogWriter.ResolveSegmentPath(directory, 0);
        var bytes = File.ReadAllBytes(path);
        bytes[8 + ValueLogFrame.EntryHeaderSize] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using (var never = new ValueLogReader(directory, options))
        {
            never.Read(pointer)[0].Should().Be(0xFF, "CrcValidationMode.Never serves the damaged payload");
        }

        using (var always = new ValueLogReader(directory, ValueLogOptions.Default))
        {
            var read = () => always.Read(pointer);
            read.Should().Throw<CorruptValueLogPointerException>();
        }

        ReadRepoFile("docs/wisckey-value-log.md").Should().Contain("| `ValidateCrcOnRead`");
        Squash(ReadSection("docs/wisckey-value-log.md", "### `CorruptValueLogPointerException` on a hot read", "### `MissingValueLogFileException`"))
            .Should().Contain("ValidateCrcOnRead", "the hot-read runbook entry presumes checksum validation is on");
    }

    // ───────────────────────── MaxEntryBytes scope ─────────────────────────

    private static int SerializedLength(string value)
    {
        using var stream = new MemoryStream();
        new SerializableWrapper<string>(value).Serialize(stream);
        return (int)stream.Length;
    }

    /// <summary>
    /// The rule the guides state: a value at or below <c>ValueLogThresholdBytes</c> stays inline and is not
    /// subject to <c>MaxEntryBytes</c>; a value above the threshold goes to the log, which refuses it when it
    /// exceeds the cap. Expected outcomes come from that sentence, not from the engine.
    /// </summary>
    [Theory]
    [InlineData(150, 256, 100, false)] // inline: above the cap, at or below the threshold
    [InlineData(256, 256, 100, false)] // exactly the threshold: still inline
    [InlineData(400, 256, 100, true)]  // above threshold and above cap: refused
    [InlineData(400, 256, 1000, false)] // above threshold, within cap: stored in the log
    [InlineData(400, 256, 400, false)] // exactly the cap: accepted
    [InlineData(401, 256, 400, true)]  // one over the cap: refused
    public async Task SetAsync_AppliesMaxEntryBytesOnlyToValuesRoutedToTheLog(
        int serializedBytes, int threshold, long maxEntryBytes, bool refused)
    {
        // The wrapper adds a few framing bytes around the characters; search for the character count whose
        // serialized form is exactly the length under test.
        var chars = Enumerable.Range(Math.Max(1, serializedBytes - 16), 17)
            .Select(n => new string('v', n))
            .FirstOrDefault(candidate => SerializedLength(candidate) == serializedBytes)
            ?? throw new InvalidOperationException(
                "no character count serializes to " + serializedBytes + ": "
                + string.Join(",", Enumerable.Range(Math.Max(1, serializedBytes - 16), 17).Select(n => n + "=" + SerializedLength(new string('v', n)))));

        var folder = Path.Combine(_dir, $"eng_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var valueLog = new ValueLogOptions(ValueLogThresholdBytes: threshold, MaxEntryBytes: maxEntryBytes);
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                MemTableCapacity: 16,
                SegmentFolder: folder,
                SegmentPrefix: "seg",
                SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
                CompactionStrategy: null,
                ValueLog: valueLog));

        var key = new SerializableWrapper<int>(1);
        var set = () => engine.SetAsync(key, new SerializableWrapper<string>(chars));

        if (refused)
        {
            await set.Should().ThrowAsync<ValueTooLargeException>();
            (await engine.TryGetValueAsync(key).ConfigureAwait(true)).Found.Should().BeFalse("a refused write is never acknowledged");
            return;
        }

        await set.Should().NotThrowAsync();
        await engine.FlushAsync().ConfigureAwait(true);
        var (value, found) = await engine.TryGetValueAsync(key).ConfigureAwait(true);
        found.Should().BeTrue();
        value.Value.Should().Be(chars);
    }

    [Fact]
    public void MaxEntryBytesDocumentation_StatesItAppliesOnlyAboveTheThreshold()
    {
        var options = Squash(ReadRepoFile("src/Domain/ValueObjects/ValueLog/ValueLogOptions.cs"));
        var param = options[options.IndexOf("<param name=\"MaxEntryBytes\">", StringComparison.Ordinal)..];
        param = param[..param.IndexOf("</param>", StringComparison.Ordinal)];
        param.Should().Contain("ValueLogThresholdBytes", "the cap's scope is defined by the threshold");
        param.Should().Contain("at or below", "values that stay inline are not subject to the cap");

        var exception = Squash(ReadRepoFile("src/Domain/Exceptions/ValueTooLargeException.cs"));
        exception.Should().Contain("value log", "the exception is raised by the value-log append, not by every SetAsync");
        exception.Should().Contain("ValueLogThresholdBytes");

        var row = Squash(ReadRepoFile("docs/wisckey-value-log.md")
            .Split('\n')
            .Single(l => l.StartsWith("| `MaxEntryBytes`", StringComparison.Ordinal)));
        row.Should().Contain("ValueLogThresholdBytes", "the guide's table states the same scope as the record");

        var runbook = Squash(ReadSection("docs/wisckey-value-log.md", "### `ValueTooLargeException` on `Set`", "## Engine integration"));
        runbook.Should().Contain("ValueLogThresholdBytes", "the runbook explains which values the cap bounds");
    }

    // ───────────────────────── helpers ─────────────────────────

    private static string Squash(string text) =>
        Regex.Replace(Regex.Replace(text, @"(?m)^\s*///", " "), @"\s+", " ");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test runs inside the repository checkout");
        return directory!.FullName;
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string ReadSection(string relativePath, string startMarker, string endMarker)
    {
        var text = ReadRepoFile(relativePath);
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{relativePath} must contain {startMarker}");
        var rest = text[(start + startMarker.Length)..];
        var end = rest.IndexOf(endMarker, StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    [GeneratedRegex(@"entry frames \[(?<fields>[^\]]+)\]")]
    private static partial Regex EntryFramesRegex();

    [GeneratedRegex(@"(?m)^\+(?:\d+(?:\+N)?)\s+(?<size>\d+|N)\s+(?<name>[a-z_]+)\s")]
    private static partial Regex FrameTableRowRegex();

    [GeneratedRegex(@"(?m)^\s+(?<off>\d+)\s+(?<size>\d+)\s+(?<name>[A-Za-z0-9]+)\s+(?:0x|LE)")]
    private static partial Regex ManifestRowRegex();

    [GeneratedRegex(@"(?m)^\|\s*`(?<name>[A-Za-z]+)`\s*\|\s*(?<default>[^|]+?)\s*\|")]
    private static partial Regex ConfigRowRegex();
}
