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
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests.Streaming;

/// <summary>
/// Integrity semantics for <see cref="FileCheckpointStore"/>. The ICheckpointStore contract
/// says implementations must guarantee no corruption: a bit-flipped offset silently resumed
/// from is a wrong restart position, and a truncated checkpoint treated as "no checkpoint"
/// is a silent restart-from-zero — both must fail loudly instead.
/// </summary>
public sealed class FileCheckpointStoreIntegrityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("checkpoint-integrity-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CheckpointFile() => Directory.GetFiles(_dir, "*.checkpoint").Should().ContainSingle().Subject;

    [Fact]
    public async Task LoadAsync_BitFlippedCheckpoint_ThrowsInsteadOfWrongResume()
    {
        var store = new FileCheckpointStore(_dir);
        await store.SaveAsync("processor-1", offset: 12345, state: new byte[] { 1, 2, 3 });

        var path = CheckpointFile();
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[2] ^= 0xFF; // flip a bit inside the offset field
        await File.WriteAllBytesAsync(path, bytes);

        var act = async () => await store.LoadAsync("processor-1");

        await act.Should().ThrowAsync<InvalidDataException>(
            "a corrupted offset silently resumed from would skip or re-process events undetectably");
    }

    [Fact]
    public async Task LoadAsync_TruncatedCheckpoint_ThrowsInsteadOfNull()
    {
        var store = new FileCheckpointStore(_dir);
        await store.SaveAsync("processor-1", offset: 42, state: new byte[] { 9, 9 });

        var path = CheckpointFile();
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..4]); // torn write / truncation

        var act = async () => await store.LoadAsync("processor-1");

        await act.Should().ThrowAsync<InvalidDataException>(
            "a truncated checkpoint reported as 'no checkpoint' silently restarts the processor from zero");
    }

    [Fact]
    public async Task SaveAsync_ProcessorIdWithPathSeparator_IsRejected_NotAliased()
    {
        var store = new FileCheckpointStore(_dir);

        // 'a/b' used to be silently mapped to the same file as 'a_b' — two distinct
        // processors sharing (and clobbering) one checkpoint.
        var act = async () => await store.SaveAsync("a/b", offset: 1, state: Array.Empty<byte>());

        await act.Should().ThrowAsync<ArgumentException>(
            "ids must map to files injectively; separator substitution aliases distinct processors");
    }
}
