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
/// RX3 — SSTABLE-POINTLOOKUP-BLOCKLEN-ALLOC. The point-lookup path in
/// <see cref="SortedStringTable{TKey, TValue}"/> computes <c>blockLen = nextOffset -
/// candidateOffset</c> from two footer block offsets and does <c>new byte[blockLen]</c> before any
/// bound. The footer CRC covers only the footer body and is forgeable, so a footer whose block
/// offsets are non-monotonic or out of bounds passes the CRC check and drives an over-allocation
/// (a colossal <c>blockLen</c>) or an <see cref="OverflowException"/> (a negative one). The fix
/// validates block offsets at footer-load (non-negative, strictly increasing, &lt;= dataEnd), so a
/// forged offset surfaces the typed corruption exception this file uses
/// (<see cref="InvalidDataException"/>) at open time — before any block read can allocate.
/// </summary>
public sealed class SortedStringTableBlockLenBoundTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sst-blocklen-{Guid.NewGuid():N}");

    public SortedStringTableBlockLenBoundTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Reopen_ForgedBlockOffsetExceedsDataEnd_ThrowsAtOpen_BeforeAllocating()
    {
        // Arrange — write a real multi-block SSTable (small blockSize forces several blocks).
        const string fileName = "forged-blocklen.dat";
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

        // Read the genuine footer, then forge it: replace block index entry [1]'s offset with a
        // value far past the data section. The CRC is recomputed by Serialize(), so the forged
        // footer passes VerifyBodyCrc — exactly the body-only/forgeable-CRC premise. A point lookup
        // that selects block 0 then computes blockLen = forgedOffset - offset0 (colossal) and, on
        // the unfixed code, allocates new byte[blockLen].
        SsTableFooter.TryReadFromEnd(original, out var footer).Should().BeTrue();
        footer!.BlockIndex.Count.Should().BeGreaterThan(1,
            "the test needs at least two blocks so a non-final block's successor offset can be forged");

        var trailerBody = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            original.AsSpan(original.Length - 4, 4));
        var dataEnd = original.Length - 4 - (long)trailerBody;

        var forgedIndex = footer.BlockIndex.ToList();
        forgedIndex[1] = forgedIndex[1] with { Offset = int.MaxValue }; // > dataEnd, non-monotonic blow-up

        var forgedFooter = footer with { BlockIndex = forgedIndex };
        var forgedBytes = forgedFooter.Serialize();

        // Splice: keep the genuine data section [0, dataEnd), append the forged footer.
        var tampered = new byte[dataEnd + forgedBytes.Length];
        Array.Copy(original, 0, tampered, 0, (int)dataEnd);
        Array.Copy(forgedBytes, 0, tampered, (int)dataEnd, forgedBytes.Length);
        File.WriteAllBytes(path, tampered);

        // Act + Assert — the forged out-of-bounds block offset must be rejected at footer-load with
        // the file's typed corruption exception, BEFORE any block-bytes allocation.
        var reopen = () => new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, fileName);

        reopen.Should().Throw<InvalidDataException>(
            "a forged block offset that overruns the data section must be rejected at open, before allocating a block buffer");
    }
}
