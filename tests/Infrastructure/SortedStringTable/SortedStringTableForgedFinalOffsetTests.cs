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
namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// F-75 — forged-final-offset boundary. The data section is the half-open interval
/// <c>[0, dataEnd)</c>; <c>dataEnd</c> is the exclusive past-the-end marker the last block's
/// synthetic <c>nextOffset</c> uses. A genuine final block therefore starts strictly
/// <c>&lt; dataEnd</c>. A footer that declares the FINAL real block at <c>offset == dataEnd</c>
/// is forged: a point lookup selecting that block would compute <c>blockLen = dataEnd -
/// offset == 0</c> against an empty (or out-of-section) span — a forged footer that the
/// boundary check must reject at open. The boundary is the difference between <c>offset &gt;
/// dataEnd</c> (which lets the equal-to-dataEnd forgery through) and <c>offset &gt;= dataEnd</c>
/// (which rejects it).
/// </summary>
public sealed class SortedStringTableForgedFinalOffsetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sst-finaloffset-{Guid.NewGuid():N}");

    public SortedStringTableForgedFinalOffsetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Reopen_ForgedFinalBlockOffsetEqualsDataEnd_ThrowsAtOpen()
    {
        // Arrange — write a real multi-block SSTable (small blockSize forces several blocks).
        const string fileName = "forged-final-offset.dat";
        var path = Path.Combine(_dir, fileName);
        {
            var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _dir, fileName, blockSizeBytes: 64);
            writer.Write(Enumerable.Range(0, 64)
                .Select(i => (
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"value_{i}_padding_to_force_block_splits"),
                    0UL))
                .ToList());
        }

        var original = File.ReadAllBytes(path);

        SsTableFooter.TryReadFromEnd(original, out var footer).Should().BeTrue();
        footer!.BlockIndex.Count.Should().BeGreaterThan(1,
            "the test needs at least two blocks so the final block offset is non-trivial");

        var trailerBody = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            original.AsSpan(original.Length - 4, 4));
        var dataEnd = original.Length - 4 - (long)trailerBody;

        // Forge the FINAL block's offset to exactly dataEnd. This is the boundary the
        // unfixed `offset > dataEnd` check lets through (it only rejects strictly-greater),
        // even though a real final block must start strictly inside the data section.
        var forgedIndex = footer.BlockIndex.ToList();
        var lastIdx = forgedIndex.Count - 1;
        forgedIndex[lastIdx] = forgedIndex[lastIdx] with { Offset = dataEnd };

        var forgedFooter = footer with { BlockIndex = forgedIndex };
        var forgedBytes = forgedFooter.Serialize();

        var tampered = new byte[dataEnd + forgedBytes.Length];
        Array.Copy(original, 0, tampered, 0, (int)dataEnd);
        Array.Copy(forgedBytes, 0, tampered, (int)dataEnd, forgedBytes.Length);
        File.WriteAllBytes(path, tampered);

        // Act + Assert — the forged final offset == dataEnd must be rejected at footer-load
        // with the file's typed corruption exception.
        var reopen = () => new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, fileName);

        reopen.Should().Throw<InvalidDataException>(
            "a forged final block offset equal to dataEnd is corrupt: a real final block starts " +
            "strictly inside the [0, dataEnd) data section, so offset must be < dataEnd, not <= dataEnd");
    }
}
