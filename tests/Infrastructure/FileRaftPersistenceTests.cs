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
/// Pins the file-backed completions of the Raft persistence seam:
/// <see cref="FileRaftLog{TCommand}"/> and <see cref="FileRaftPersistentState"/>. These are
/// the binary-compatible substitutes the in-memory defaults promised — supplied through the
/// same <see cref="RaftNode{TCommand}"/> ctor parameters. The contract under test:
/// <list type="bullet">
///   <item><description><b>Crash recovery.</b> <c>currentTerm</c> / <c>votedFor</c> / log
///   entries survive a simulated process restart (write → drop the in-memory instance →
///   reconstruct from the file-backed primitives → assert an identical trace).</description>
///   </item>
///   <item><description><b>fsync durability.</b> Every mutation is observable to a freshly
///   opened reader before the next mutation — the proxy for "flushed before the next RPC"
///   (Raft paper §5.2).</description></item>
///   <item><description><b>Torn-write recovery.</b> A torn final append is detected and
///   truncated, never silently accepted (SPEC-SAFE-01).</description></item>
///   <item><description><b>Path validation.</b> Empty/whitespace paths and null serializers
///   are rejected at construction (SPEC-SAFE-01).</description></item>
///   <item><description><b>Substitutability.</b> A <see cref="RaftNode{TCommand}"/> driven
///   with the file-backed seams produces a committed-log trace identical to the in-memory
///   default.</description></item>
/// </list>
/// </summary>
public sealed class FileRaftPersistenceTests : IDisposable
{
    private readonly string _dir;

    public FileRaftPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "raft-file-tests-" + Guid.NewGuid().ToString("N"));
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

    private string PathFor(string name) => Path.Combine(_dir, name);

    private static byte[] Ser(string s) => Encoding.UTF8.GetBytes(s);

    private static string Deser(byte[] b) => Encoding.UTF8.GetString(b);

    // ───────────────────────────── FileRaftPersistentState ─────────────────────────────

    [Fact]
    public void FilePersistentState_MissingFile_DefaultsToZeroTermAndNullVote()
    {
        var state = new FileRaftPersistentState(PathFor("state.bin"));

        state.CurrentTerm.Should().Be(0);
        state.VotedFor.Should().BeNull();
    }

    [Fact]
    public void FilePersistentState_TermAndVote_SurviveReopen()
    {
        var path = PathFor("state.bin");

        var first = new FileRaftPersistentState(path);
        first.CurrentTerm = 7;
        first.VotedFor = "candidate-A";

        // Simulated process restart — a fresh instance reads the durable file.
        var reopened = new FileRaftPersistentState(path);

        reopened.CurrentTerm.Should().Be(7);
        reopened.VotedFor.Should().Be("candidate-A");
    }

    [Fact]
    public void FilePersistentState_EachMutation_IsImmediatelyDurable()
    {
        var path = PathFor("state.bin");
        var writer = new FileRaftPersistentState(path);

        writer.CurrentTerm = 1;
        new FileRaftPersistentState(path).CurrentTerm.Should().Be(1);

        writer.VotedFor = "self";
        new FileRaftPersistentState(path).VotedFor.Should().Be("self");

        // Stepping down to a higher term clears the vote — the clear must also be durable.
        writer.CurrentTerm = 2;
        writer.VotedFor = null;
        var afterClear = new FileRaftPersistentState(path);
        afterClear.CurrentTerm.Should().Be(2);
        afterClear.VotedFor.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FilePersistentState_EmptyPath_Throws(string path)
    {
        var act = () => new FileRaftPersistentState(path);

        act.Should().Throw<ArgumentException>();
    }

    // ───────────────────────────── FileRaftLog ─────────────────────────────

    [Fact]
    public void FileRaftLog_AppendIndexRange_MatchesInMemoryContract()
    {
        using var log = new FileRaftLog<string>(PathFor("log.bin"), Ser, Deser);
        log.Count.Should().Be(0);

        log.Append((1, "a"));
        log.Append((1, "b"));
        log.Append((2, "c"));

        log.Count.Should().Be(3);
        log.GetEntry(0).Should().Be((1L, "a"));
        log.GetEntry(2).Should().Be((2L, "c"));
        log.GetRange(1, 2).Should().Equal((1L, "b"), (2L, "c"));
    }

    [Fact]
    public void FileRaftLog_OutOfRange_Throws()
    {
        using var log = new FileRaftLog<int>(PathFor("log.bin"), BitConverter.GetBytes, b => BitConverter.ToInt32(b));
        log.Append((1, 10));

        ((Action)(() => log.GetEntry(5))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => log.TruncateFrom(-1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => log.GetRange(0, 5))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FileRaftLog_Entries_SurviveReopen()
    {
        var path = PathFor("log.bin");

        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            log.Append((1, "op-2"));
            log.Append((3, "op-3"));
        }

        // Simulated restart.
        using var reopened = new FileRaftLog<string>(path, Ser, Deser);

        reopened.Count.Should().Be(3);
        reopened.GetEntry(0).Should().Be((1L, "op-1"));
        reopened.GetEntry(1).Should().Be((1L, "op-2"));
        reopened.GetEntry(2).Should().Be((3L, "op-3"));
    }

    [Fact]
    public void FileRaftLog_AppendAfterReopen_ContinuesFromTail()
    {
        var path = PathFor("log.bin");

        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
        }

        using var reopened = new FileRaftLog<string>(path, Ser, Deser);
        reopened.Append((2, "op-2"));

        reopened.Count.Should().Be(2);
        reopened.GetEntry(1).Should().Be((2L, "op-2"));
    }

    [Fact]
    public void FileRaftLog_TruncateFrom_RemovesTail_AndPersists()
    {
        var path = PathFor("log.bin");

        using (var log = new FileRaftLog<int>(path, BitConverter.GetBytes, b => BitConverter.ToInt32(b)))
        {
            log.Append((1, 10));
            log.Append((1, 20));
            log.Append((2, 30));

            log.TruncateFrom(1);

            log.Count.Should().Be(1);
            log.GetEntry(0).Should().Be((1L, 10));
        }

        using var reopened = new FileRaftLog<int>(path, BitConverter.GetBytes, b => BitConverter.ToInt32(b));
        reopened.Count.Should().Be(1, "the truncation must be durable across a restart");
        reopened.GetEntry(0).Should().Be((1L, 10));
    }

    [Fact]
    public void FileRaftLog_TornFinalAppend_IsTruncatedNotAccepted()
    {
        var path = PathFor("log.bin");
        long goodLength;

        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            log.Append((1, "op-2"));
            goodLength = new FileInfo(path).Length;
        }

        // Simulate a torn final append: a process crash mid-write leaves a partial frame
        // (the first bytes of a real frame, fewer than a complete header) after the last good entry.
        var otherPath = PathFor("other.bin");
        using (var other = new FileRaftLog<string>(otherPath, Ser, Deser))
        {
            other.Append((1, "op-3"));
        }
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            fs.Write(File.ReadAllBytes(otherPath), 0, 3);
        }
        new FileInfo(path).Length.Should().BeGreaterThan(goodLength, "the torn bytes were appended");

        using var reopened = new FileRaftLog<string>(path, Ser, Deser);

        reopened.Count.Should().Be(2, "the torn final frame must be dropped, not accepted");
        reopened.GetEntry(1).Should().Be((1L, "op-2"));
        new FileInfo(path).Length.Should().Be(goodLength,
            "recovery must physically truncate the torn tail back to the last good frame");
    }

    [Fact]
    public void FileRaftLog_CorruptFinalFrameChecksumWithLandedLastByte_FailsLoud()
    {
        var path = PathFor("log.bin");

        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            log.Append((1, "op-2"));
        }

        // Flip the last command byte of the final frame. Its header stays valid, so recovery reads
        // the whole frame, the command checksum fails and the frame ends exactly at end-of-file.
        // The last byte is non-zero, so it landed: an interrupted append leaves a command prefix
        // followed by zeros. This is damage to an fsync'd, acknowledged entry, not a torn write,
        // so reopening must fail loud and leave the file untouched.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(-1, SeekOrigin.End);
            var b = fs.ReadByte();
            fs.Seek(-1, SeekOrigin.Current);
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        var damaged = File.ReadAllBytes(path);
        damaged[^1].Should().NotBe(0, "the damaged last byte must have landed for this case");

        var reopen = () => new FileRaftLog<string>(path, Ser, Deser);

        reopen.Should().Throw<InvalidDataException>("a landed final command that fails its checksum is acknowledged-entry damage");
        File.ReadAllBytes(path).Should().Equal(damaged, "a fail-loud recovery must not truncate the acknowledged entry away");
    }

    /// <summary>
    /// <c>IRaftLog.GetRange</c> requires <c>startIndex + count</c> not to exceed
    /// <c>Count</c>. A count near <see cref="int.MaxValue"/> must be refused with the documented
    /// <see cref="ArgumentOutOfRangeException"/> on both siblings — the guard must not overflow
    /// into a passing check and reach a 2^31-element allocation or a bare ArgumentException.
    /// </summary>
    [Theory]
    [InlineData(1, int.MaxValue)]
    [InlineData(2, int.MaxValue - 1)]
    [InlineData(2, int.MaxValue)]
    public void GetRange_CountOverflowingStartIndex_ThrowsArgumentOutOfRange_OnBothSiblings(int startIndex, int count)
    {
        using var fileLog = new FileRaftLog<string>(PathFor("log.bin"), Ser, Deser);
        var memoryLog = new InMemoryRaftLog<string>();
        fileLog.Append((1, "op-1"));
        fileLog.Append((1, "op-2"));
        memoryLog.Append((1, "op-1"));
        memoryLog.Append((1, "op-2"));

        var fileAct = () => fileLog.GetRange(startIndex, count);
        var memoryAct = () => memoryLog.GetRange(startIndex, count);

        fileAct.Should().ThrowExactly<ArgumentOutOfRangeException>("the range runs past Count");
        memoryAct.Should().ThrowExactly<ArgumentOutOfRangeException>("the range runs past Count");
    }

    [Fact]
    public void FileRaftLog_FinalCommandWhoseTrailingBytesNeverLanded_IsTruncatedAsTornTail()
    {
        var path = PathFor("log.bin");
        long goodLength;

        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            goodLength = new FileInfo(path).Length;
            log.Append((1, "op-2"));
        }

        // The file length reached disk but the last two command bytes did not: a prefix of the
        // command followed by zeros to end-of-file, the shape an interrupted append leaves.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(-2, SeekOrigin.End);
            fs.Write(new byte[2]);
        }

        using var reopened = new FileRaftLog<string>(path, Ser, Deser);

        reopened.Count.Should().Be(1, "the torn final append is dropped");
        reopened.GetEntry(0).Should().Be((1L, "op-1"));
        new FileInfo(path).Length.Should().Be(goodLength);
    }

    [Fact]
    public void FileRaftLog_CorruptNonTailFrameChecksum_ThrowsNotSilentlyTruncated()
    {
        var path = PathFor("log.bin");
        long firstFrameLength;

        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            firstFrameLength = new FileInfo(path).Length;
            log.Append((1, "op-2"));
            log.Append((2, "op-3"));
        }

        // Flip a command byte inside the FIRST (non-tail) frame. Its header stays valid, so
        // recovery reads the whole frame and its command checksum fails — but two valid frames
        // follow it. That is mid-file corruption (disk rot), not a torn tail
        // (each Append fsyncs a single frame, so only the tail can be torn). Recovery must NOT
        // truncate-and-erase the committed frames after it: silently dropping committed Raft
        // entries is a State-Machine-Safety violation. It must fail closed instead.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            // Frame 0's command starts right after its header; flip the command's first byte.
            fs.Seek(FrameHeaderSize, SeekOrigin.Begin);
            var b = fs.ReadByte();
            fs.Seek(-1, SeekOrigin.Current);
            fs.WriteByte((byte)(b ^ 0xFF));
        }
        var corruptedLength = new FileInfo(path).Length;

        var act = () => new FileRaftLog<string>(path, Ser, Deser);

        act.Should().Throw<InvalidDataException>(
            "mid-file CRC corruption with committed frames following it is not a torn tail and must fail closed, not silently drop committed entries");
        new FileInfo(path).Length.Should().Be(corruptedLength,
            "a corruption fail-close must not physically truncate/erase the committed frames");

        // Guard against an unused-local warning if the assertion shape ever changes; documents
        // that the first frame is a real, complete frame the test is corrupting.
        firstFrameLength.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FileRaftLog_TornFinalAppendWhoseCommandEmbedsAFrame_IsTruncatedNotRejected()
    {
        // Command bytes are caller-controlled, so a structured command can itself contain a
        // complete, CRC-valid log frame (here: the exact on-disk bytes of another log's entry).
        var innerPath = PathFor("inner.bin");
        using (var inner = new FileRaftLog<byte[]>(innerPath, b => b, b => b))
        {
            inner.Append((7, Ser("inner")));
        }
        var embeddedFrame = File.ReadAllBytes(innerPath);

        var path = PathFor("log.bin");
        long firstFrameEnd;
        using (var log = new FileRaftLog<byte[]>(path, b => b, b => b))
        {
            log.Append((1, Ser("op-1")));
            firstFrameEnd = new FileInfo(path).Length;
            log.Append((1, embeddedFrame));
        }

        // Crash mid-append: the final frame lost its last 4 command bytes. Its header is valid and
        // its command is short at end-of-file, so this is a torn tail even though a CRC-valid
        // frame sits inside the torn frame's own command bytes.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.SetLength(fs.Length - sizeof(uint));
        }

        var reopen = () => new FileRaftLog<byte[]>(path, b => b, b => b);
        using var reopened = reopen.Should().NotThrow(
            "a torn final append is recoverable; nested frame bytes inside it are not a following frame").Subject;

        reopened.Count.Should().Be(1, "the torn final append must be dropped and the committed prefix returned");
        reopened.GetEntry(0).Term.Should().Be(1L);
        reopened.GetEntry(0).Command.Should().Equal(Ser("op-1"));
        new FileInfo(path).Length.Should().Be(firstFrameEnd,
            "recovery must truncate the torn tail back to the last good frame");
    }

    [Theory]
    // The partial frame kept only its magic and version, then a committed frame follows.
    [InlineData("op-2-with-a-much-longer-command-payload", 5)]
    // The partial frame kept part of its header, then a committed frame follows.
    [InlineData("op-2", 14)]
    public void FileRaftLog_PartialFrameFollowedByACommittedFrame_ThrowsNotSilentlyTruncated(
        string tornCommand, int keptBytes)
    {
        var path = PathFor("log.bin");
        long firstFrameEnd;
        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            firstFrameEnd = new FileInfo(path).Length;
            log.Append((1, tornCommand));
        }
        var tornFrame = File.ReadAllBytes(path)[(int)firstFrameEnd..];

        // Rebuild the file as: op-1, the first keptBytes bytes of the torn frame, then a complete
        // op-3 frame. The partial frame's header region now fails its checksum with non-zero
        // bytes after it, so it is damage, not a torn tail.
        var thirdPath = PathFor("third.bin");
        using (var third = new FileRaftLog<string>(thirdPath, Ser, Deser))
        {
            third.Append((2, "op-3"));
        }
        var image = File.ReadAllBytes(path)[..^tornFrame.Length]
            .Concat(tornFrame[..keptBytes])
            .Concat(File.ReadAllBytes(thirdPath))
            .ToArray();
        File.WriteAllBytes(path, image);

        var act = () => new FileRaftLog<string>(path, Ser, Deser);

        act.Should().Throw<InvalidDataException>(
            "a committed frame after an unreadable one is mid-file corruption and must not be erased");
        new FileInfo(path).Length.Should().Be(image.Length,
            "a corruption fail-close must not physically truncate the committed frames");
    }

    [Fact]
    public void FileRaftLog_RefusedOpen_ReleasesTheFile()
    {
        var path = PathFor("log.bin");
        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            log.Append((1, "op-2"));
        }
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(FrameVersionOffset, SeekOrigin.Begin);
            fs.WriteByte(0xFF);
        }

        var open = () => new FileRaftLog<string>(path, Ser, Deser);
        open.Should().Throw<InvalidDataException>();

        // An operator inspects or repairs the file after a refused open; the log opens it with
        // FileShare.None, so a handle left behind by the failed constructor would lock them out.
        var inspect = () => File.ReadAllBytes(path);
        inspect.Should().NotThrow("a constructor that throws must not keep the log file open");
    }

    [Fact]
    public void FileRaftLog_NonTailLengthPrefixOverrunningEof_ThrowsNotSilentlyTruncated()
    {
        var path = PathFor("log.bin");

        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            log.Append((1, "op-2"));
            log.Append((2, "op-3"));
        }

        // Rot the FIRST frame's command length so its declared extent runs past end-of-file. The
        // committed frames after it are intact: this is mid-file corruption of a length field,
        // which the header checksum detects, so recovery must fail closed rather than read the
        // overrun as a torn tail and erase them.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(FrameCommandLengthOffset + 3, SeekOrigin.Begin);
            fs.WriteByte(0x10);
        }
        var corruptedLength = new FileInfo(path).Length;

        var act = () => new FileRaftLog<string>(path, Ser, Deser);

        act.Should().Throw<InvalidDataException>(
            "a corrupt non-tail length prefix with committed frames behind it is not a torn tail");
        new FileInfo(path).Length.Should().Be(corruptedLength,
            "a corruption fail-close must not physically truncate the committed frames");
    }

    [Fact]
    public void FileRaftLog_NonTailLengthPlusTermRot_ThrowsNotSilentlyTruncated()
    {
        var path = PathFor("log.bin");
        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "op-1"));
            log.Append((1, "op-2"));
            log.Append((2, "op-3"));
            log.Append((2, "op-4"));
        }

        // Rot two fields of the FIRST frame: its command length now overstates the frame far past
        // end-of-file, and one term byte is flipped too. The header checksum covers both, so
        // recovery must fail closed rather than truncate the committed frames away.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, 1_000_000);
            fs.Seek(FrameCommandLengthOffset, SeekOrigin.Begin);
            fs.Write(length);
            fs.Seek(FrameTermOffset + 3, SeekOrigin.Begin);
            var b = fs.ReadByte();
            fs.Seek(-1, SeekOrigin.Current);
            fs.WriteByte((byte)(b ^ 0x5A));
        }
        var corruptedLength = new FileInfo(path).Length;

        var act = () => new FileRaftLog<string>(path, Ser, Deser);

        act.Should().Throw<InvalidDataException>(
            "committed frames chaining to end-of-file behind multi-byte rot are not a torn tail");
        new FileInfo(path).Length.Should().Be(corruptedLength,
            "a corruption fail-close must not physically truncate the committed frames");
    }

    [Fact]
    public void FileRaftLog_CallerMutatesCommandAfterAppend_MirrorStillServesTheDurableCommand()
    {
        var path = PathFor("log.bin");
        var command = new byte[] { 1, 2, 3, 4 };
        using var log = new FileRaftLog<byte[]>(path, b => b, b => b);
        log.Append((1, command));

        // The caller reuses its buffer after the append returns. The disk frame holds 1,2,3,4,
        // so the live mirror — which the leader replicates from — must keep serving exactly that.
        command[0] = 99;

        log.GetEntry(0).Command.Should().Equal([1, 2, 3, 4]);
        log.GetRange(0, 1)[0].Command.Should().Equal([1, 2, 3, 4]);
    }

    [Fact]
    public void FileRaftLog_ReaderMutatesCommandFromGetEntry_MirrorStillServesTheDurableCommand()
    {
        var path = PathFor("log.bin");
        using var log = new FileRaftLog<byte[]>(path, b => b, b => b);
        log.Append((1, [1, 2, 3, 4]));

        // A consumer mutates the command it was handed. The disk frame holds 1,2,3,4, so every
        // later read — the leader replicates from these — must keep serving exactly that.
        log.GetEntry(0).Command[0] = 99;

        log.GetEntry(0).Command.Should().Equal([1, 2, 3, 4]);
        log.GetRange(0, 1)[0].Command.Should().Equal([1, 2, 3, 4]);
    }

    [Fact]
    public void FileRaftLog_ReaderMutatesCommandFromGetRange_MirrorStillMatchesDisk()
    {
        var path = PathFor("log.bin");
        using (var log = new FileRaftLog<byte[]>(path, b => b, b => b))
        {
            log.Append((1, [7, 7, 7]));
            log.Append((1, [8]));

            // GetRange feeds RaftNode.GetCommittedLog and the AppendEntries payload; a consumer
            // that mutates a command it received must not change what the log serves next.
            log.GetRange(0, 2)[0].Command[0] = 42;

            log.GetRange(0, 2)[0].Command.Should().Equal([7, 7, 7]);
            log.GetEntry(0).Command.Should().Equal([7, 7, 7]);
        }

        using var reopened = new FileRaftLog<byte[]>(path, b => b, b => b);
        reopened.GetEntry(0).Command.Should().Equal([7, 7, 7], "the durable frame was never changed");
    }

    // Version-1 frame layout: [magic 4][version 1][term 8][command length 4][command CRC 4]
    // [header CRC 4][command]. The header checksum covers every header field.
    private const int FrameVersionOffset = 4;
    private const int FrameTermOffset = 5;
    private const int FrameCommandLengthOffset = 13;
    private const int FrameHeaderSize = 25;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FileRaftLog_EmptyPath_Throws(string path)
    {
        var act = () => new FileRaftLog<string>(path, Ser, Deser);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FileRaftLog_NullSerializer_Throws()
    {
        ((Action)(() => new FileRaftLog<string>(PathFor("log.bin"), null!, Deser)))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => new FileRaftLog<string>(PathFor("log.bin"), Ser, null!)))
            .Should().Throw<ArgumentNullException>();
    }

    // ───────────────────────────── Substitutability ─────────────────────────────

    [Fact]
    public async Task RaftNode_FileBackedSeams_DriveStateMachineIdenticallyToInMemory()
    {
        var defaultNode = new RaftNode<string>("default");

        using var fileLog = new FileRaftLog<string>(PathFor("node-log.bin"), Ser, Deser);
        var fileState = new FileRaftPersistentState(PathFor("node-state.bin"));
        var injectedNode = new RaftNode<string>("injected", log: fileLog, persistentState: fileState);

        StartElection(defaultNode);
        StartElection(injectedNode);

        defaultNode.State.Should().Be(RaftNodeState.Leader);
        injectedNode.State.Should().Be(RaftNodeState.Leader);

        await defaultNode.ProposeAsync("op-1");
        await defaultNode.ProposeAsync("op-2");
        await injectedNode.ProposeAsync("op-1");
        await injectedNode.ProposeAsync("op-2");

        injectedNode.GetCommittedLog().Should().Equal(defaultNode.GetCommittedLog(),
            "the file-backed seam must produce a state-machine trace identical to the in-memory default");

        // The file-backed primitives must have captured the same term + log the node drove.
        fileState.CurrentTerm.Should().Be(injectedNode.CurrentTerm);
        fileLog.Count.Should().Be(2);
    }

    /// <summary>
    /// A crafted state file with a <c>voteLength</c> large enough to cause
    /// <c>voteStart + voteLength + sizeof(uint)</c> to overflow Int32 must be rejected with a
    /// typed <see cref="InvalidDataException"/> rather than silently tripping the consistency
    /// check by accident.  The long-arithmetic bound mirrors the pattern used in
    /// <see cref="FileRaftLog{TCommand}"/> replay and is the fix under test.
    /// </summary>
    [Fact]
    public void FilePersistentState_OversizedVoteLength_ThrowsInvalidDataException()
    {
        var path = PathFor("state-absurd-vote-len.bin");
        WriteCrcValidStateFile(path, voteLength: int.MaxValue);

        var act = () => new FileRaftPersistentState(path);

        // The length-bound check must fire before any span/array allocation attempt.
        act.Should().Throw<InvalidDataException>();
    }

    /// <summary>
    /// The writer's only null-vote sentinel is <c>-1</c>; any other negative <c>voteLength</c>
    /// is corruption, not a sentinel.  The pre-fix reader treated <em>every</em> negative
    /// length as null-vote with no consistency check at all, silently loading a damaged
    /// record as healthy — these inputs pin the rejection.
    /// </summary>
    [Theory]
    [InlineData(-2)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void FilePersistentState_NegativeNonSentinelVoteLength_ThrowsInvalidDataException(
        int corruptVoteLength)
    {
        var path = PathFor($"state-negative-vote-len-{corruptVoteLength}.bin");
        WriteCrcValidStateFile(path, corruptVoteLength);

        var act = () => new FileRaftPersistentState(path);

        act.Should().Throw<InvalidDataException>(
            "a negative voteLength other than the -1 sentinel is corruption and must fail closed");
    }

    /// <summary>
    /// A null-vote (<c>-1</c>) record has no vote payload, so its file length is exactly
    /// term + voteLen + crc.  Trailing bytes mean the length field and the contents disagree;
    /// the pre-fix reader accepted such a file (the CRC covers whatever is on disk, and the
    /// null-vote branch did no length check), silently ignoring the extra bytes.
    /// </summary>
    [Fact]
    public void FilePersistentState_NullVoteSentinelWithTrailingBytes_ThrowsInvalidDataException()
    {
        var path = PathFor("state-null-vote-trailing.bin");
        WriteCrcValidStateFile(path, voteLength: -1, trailingGarbage: 3);

        var act = () => new FileRaftPersistentState(path);

        act.Should().Throw<InvalidDataException>(
            "a null-vote record with trailing bytes is length-inconsistent and must fail closed");
    }

    /// <summary>
    /// Writes a CRC-valid state file with the given <paramref name="voteLength"/> field and no
    /// vote payload, optionally followed by <paramref name="trailingGarbage"/> extra bytes that
    /// are covered by the CRC — so the corrupt-length path, not the CRC path, is exercised.
    /// Layout: <c>[term: 8 LE][voteLength: 4 LE][garbage…][crc32c: 4 LE]</c>.
    /// </summary>
    private static void WriteCrcValidStateFile(string path, int voteLength, int trailingGarbage = 0)
    {
        var payloadSize = sizeof(long) + sizeof(int) + trailingGarbage;
        var payload = new byte[payloadSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0), 3L);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(long)), voteLength);
        for (var i = sizeof(long) + sizeof(int); i < payloadSize; i++)
        {
            payload[i] = 0xAB;
        }

        var crc = Crc32C.HashToUInt32(payload); // Crc32C is InternalsVisibleTo the test project

        var buffer = new byte[payloadSize + sizeof(uint)];
        payload.CopyTo(buffer, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(payloadSize), crc);
        File.WriteAllBytes(path, buffer);
    }

    private static void StartElection<TCommand>(RaftNode<TCommand> node)
    {
        var method = typeof(RaftNode<TCommand>).GetMethod(
            "StartElection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(node, null);
    }
}
