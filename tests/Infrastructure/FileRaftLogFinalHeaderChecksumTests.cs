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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// The final frame of a <see cref="FileRaftLog{TCommand}"/> whose header fully landed but fails
/// its checksum. Each append writes and fsyncs one whole frame, so an interrupted append leaves
/// a strict prefix of the header followed by zero bytes: its last header byte (the final byte of
/// the header checksum) is zero. A header whose every byte landed and still fails its checksum
/// therefore cannot be a torn append — it is damage to an entry <see cref="FileRaftLog{TCommand}.Append"/>
/// already fsync'd and acknowledged, and recovery must fail loud rather than erase it. This holds
/// whatever the entry's command bytes are, including an empty or all-zero command, where nothing
/// but zeros follows the damaged header.
/// </summary>
public sealed class FileRaftLogFinalHeaderChecksumTests : IDisposable
{
    private const int HeaderSize = 25;
    private const int TermOffset = 5;
    private const int CommandLengthOffset = 13;
    private const int CommandCrcOffset = 17;
    private const int HeaderCrcLastByte = HeaderSize - 1;
    private const int EndMarkerSize = 1;

    private readonly string _dir;

    public FileRaftLogFinalHeaderChecksumTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "raft-final-header-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a stray temp dir is harmless.
        }
    }

    private string LogPath => Path.Combine(_dir, "log.bin");

    /// <summary>
    /// Appends <paramref name="commands"/> through the log's own writer and returns the start
    /// offset of the final frame, read from the file length before its append.
    /// </summary>
    private long WriteEntries(params byte[][] commands)
    {
        long finalStart = 0;
        using var log = new FileRaftLog<byte[]>(LogPath, b => b, b => b);
        for (var i = 0; i < commands.Length; i++)
        {
            finalStart = new FileInfo(LogPath).Length;
            log.Append((i + 1, commands[i]));
        }

        return finalStart;
    }

    public static TheoryData<string, int> CompleteHeaderDamage => new()
    {
        // An empty command: the damaged header is the last thing in the file.
        { "empty", TermOffset },
        { "empty", CommandLengthOffset },
        { "empty", CommandCrcOffset + 1 },
        { "empty", HeaderCrcLastByte },
        // A command of zero bytes, as BitConverter encodes the int 0.
        { "int-zero", TermOffset + 1 },
        { "int-zero", HeaderCrcLastByte },
    };

    [Theory]
    [MemberData(nameof(CompleteHeaderDamage))]
    public void CompleteFinalHeaderFailingItsChecksum_FailsLoudAndIsNotTruncated(string command, int damagedByte)
    {
        var finalCommand = command == "empty" ? [] : BitConverter.GetBytes(0);
        var finalStart = WriteEntries("op-1"u8.ToArray(), finalCommand);
        var bytes = File.ReadAllBytes(LogPath);
        bytes[(int)finalStart + HeaderCrcLastByte].Should().NotBe(0,
            "the fixture needs a header whose every byte landed, including the last checksum byte");
        bytes[(int)finalStart + damagedByte] ^= 0x01;
        File.WriteAllBytes(LogPath, bytes);

        var act = () => new FileRaftLog<byte[]>(LogPath, b => b, b => b);

        act.Should().Throw<InvalidDataException>(
            "a header whose every byte landed cannot be an interrupted append; failing its checksum is damage to an acknowledged entry");
        File.ReadAllBytes(LogPath).Should().Equal(bytes,
            "a fail-loud recovery must not truncate the acknowledged final entry away");
    }

    [Theory]
    // Only the term-and-length part of the header landed.
    [InlineData(CommandCrcOffset)]
    // Every header byte but the checksum landed.
    [InlineData(HeaderCrcLastByte - 3)]
    // All but the last checksum byte landed.
    [InlineData(HeaderCrcLastByte)]
    public void HeaderPrefixOfAnEmptyCommandFrame_FollowedByZeros_IsATornTail(int landedBytes)
    {
        var finalStart = WriteEntries("op-1"u8.ToArray(), []);
        var bytes = File.ReadAllBytes(LogPath);
        // An interrupted append lands a prefix of the frame: everything after the landed header
        // bytes — the rest of the header and the frame's end marker — is still zero.
        Array.Clear(bytes, (int)finalStart + landedBytes, bytes.Length - (int)finalStart - landedBytes);
        File.WriteAllBytes(LogPath, bytes);

        using (var reopened = new FileRaftLog<byte[]>(LogPath, b => b, b => b))
        {
            reopened.Count.Should().Be(1, "a header prefix followed by zeros is an interrupted append and is dropped");
            reopened.GetEntry(0).Command.Should().Equal("op-1"u8.ToArray());
            reopened.Append((3, "op-3"u8.ToArray()));
        }

        new FileInfo(LogPath).Length.Should().Be(finalStart + HeaderSize + "op-3".Length + EndMarkerSize,
            "recovery truncates the torn header back to the last good frame before the next append");
    }
}
