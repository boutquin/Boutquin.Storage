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
namespace Boutquin.Storage.Infrastructure.Tests.Serialization;

/// <summary>
/// Unit tests for <see cref="PooledMemoryStream"/> — the ArrayPool-backed scratch buffer
/// used by the source-generated tagged-wire emitter.
/// </summary>
public sealed class PooledMemoryStreamTests
{
    /// <summary>
    /// Sequentially writes more bytes than the initial capacity to verify the auto-grow path
    /// preserves data across grow boundaries.
    /// </summary>
    [Fact]
    public void Write_BeyondInitialCapacity_PreservesAllBytes()
    {
        using var stream = new PooledMemoryStream(initialCapacity: 16);

        var payload = new byte[1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        stream.Write(payload, 0, payload.Length);

        stream.Length.Should().Be(payload.Length);
        stream.WrittenSpan.ToArray().Should().Equal(payload);
    }

    /// <summary>
    /// <see cref="PooledMemoryStream.WriteByte"/> appends a single byte and updates Length —
    /// matches the LSM block emitter's per-byte hot path.
    /// </summary>
    [Fact]
    public void WriteByte_AppendsAndAdvancesLength()
    {
        using var stream = new PooledMemoryStream();
        stream.WriteByte(0xAB);
        stream.WriteByte(0xCD);

        stream.Length.Should().Be(2);
        stream.WrittenSpan.ToArray().Should().Equal([0xAB, 0xCD]);
    }

    /// <summary>
    /// <see cref="PooledMemoryStream.Write(ReadOnlySpan{byte})"/> overload takes a span and
    /// appends every byte — used when the caller already has a stack-allocated span.
    /// </summary>
    [Fact]
    public void Write_SpanOverload_AppendsBytes()
    {
        using var stream = new PooledMemoryStream();
        Span<byte> span = [1, 2, 3, 4, 5];
        stream.Write(span);

        stream.Length.Should().Be(5);
        stream.WrittenSpan.ToArray().Should().Equal([1, 2, 3, 4, 5]);
    }

    /// <summary>
    /// <see cref="PooledMemoryStream.WriteTo"/> copies every written byte to the destination
    /// stream in a single pass — replaces <c>ToArray() + Write()</c> on the legacy
    /// <see cref="MemoryStream"/> pattern.
    /// </summary>
    [Fact]
    public void WriteTo_CopiesAllBytesToDestination()
    {
        using var stream = new PooledMemoryStream();
        stream.Write([7, 8, 9, 10], 0, 4);

        var destination = new MemoryStream();
        stream.WriteTo(destination);

        destination.ToArray().Should().Equal([7, 8, 9, 10]);
    }

    /// <summary>
    /// Read / Seek / SetLength / get_Position write-only contract is enforced — callers
    /// receive <see cref="NotSupportedException"/> instead of accidentally treating the
    /// stream as random-access.
    /// </summary>
    [Fact]
    public void ReadAndSeekAndSetLength_AreNotSupported()
    {
        using var stream = new PooledMemoryStream();
        var buffer = new byte[4];

        Action read = () => _ = stream.Read(buffer, 0, buffer.Length);
        Action seek = () => stream.Seek(0, SeekOrigin.Begin);
        Action setLength = () => stream.SetLength(10);
        Action setPosition = () => stream.Position = 0;

        read.Should().Throw<NotSupportedException>();
        seek.Should().Throw<NotSupportedException>();
        setLength.Should().Throw<NotSupportedException>();
        setPosition.Should().Throw<NotSupportedException>();
    }

    /// <summary>
    /// After <c>PooledMemoryStream.Dispose()</c>, every Write* call raises
    /// <see cref="ObjectDisposedException"/> — guards against use-after-return-to-pool.
    /// </summary>
    [Fact]
    public void Write_AfterDispose_Throws()
    {
        var stream = new PooledMemoryStream();
        stream.Dispose();

        Action write = () => stream.Write(new byte[1], 0, 1);
        Action writeByte = () => stream.WriteByte(0);

        write.Should().Throw<ObjectDisposedException>();
        writeByte.Should().Throw<ObjectDisposedException>();
    }

    /// <summary>
    /// The constructor rejects non-positive initial capacities — matches the documented
    /// "≥ 1 byte" contract.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveInitialCapacity(int capacity)
    {
        Action act = () => new PooledMemoryStream(capacity);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// CanRead / CanSeek are false; CanWrite is true while live and false after dispose —
    /// surfaces the write-only character through the <see cref="Stream"/> contract so
    /// adapters that introspect the stream behave correctly.
    /// </summary>
    [Fact]
    public void CanRead_CanSeek_AreFalse_CanWrite_TogglesOnDispose()
    {
        var stream = new PooledMemoryStream();
        stream.CanRead.Should().BeFalse();
        stream.CanSeek.Should().BeFalse();
        stream.CanWrite.Should().BeTrue();

        stream.Dispose();
        stream.CanWrite.Should().BeFalse();
    }

    [Fact]
    public async Task Write_PayloadBeyondOneGiB_Completes()
    {
        // EnsureCapacity's doubling loop overflows int for required in (2^30, 2^31): 2^30 * 2
        // wraps negative, then to 0, then resets to the default capacity - an INFINITE LOOP on
        // the first record larger than 1 GiB. Run under a watchdog so the unfixed code fails
        // the test rather than hanging the runner.
        var task = Task.Run(() =>
        {
            using var ms = new PooledMemoryStream();
            var chunk = new byte[1 << 30]; // exactly 1 GiB - capacity lands on 2^30
            ms.Write(chunk, 0, chunk.Length);
            ms.WriteByte(1); // required = 2^30 + 1 - triggers the doubling overflow
            return ms.Length;
        });

        var length = await task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);
        length.Should().Be((1L << 30) + 1,
            "a payload beyond 1 GiB must grow the buffer (clamped to Array.MaxLength), not spin forever in the capacity loop");
    }
}
