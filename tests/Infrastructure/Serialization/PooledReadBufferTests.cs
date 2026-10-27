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
using System.Buffers;

namespace Boutquin.Storage.Infrastructure.Tests.Serialization;

// Corrupt-input allocation bounds for the generated tagged-wire reader's pooled buffer.
// Every declared length on the read path is attacker/corruption-controlled data; renting
// before any plausibility check turns a 5-byte corrupt frame into a ~2 GB allocation.
public sealed class PooledReadBufferTests
{
    [Fact]
    public void RentAndRead_RoundTrips()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        using var ms = new MemoryStream(data);

        using var buf = PooledReadBuffer.RentAndRead(ms, data.Length);
        using var view = buf.AsStream();

        var read = new byte[data.Length];
        view.ReadExactly(read);
        read.Should().Equal(data);
    }

    [Fact]
    public void RentAndRead_DeclaredLengthBeyondSeekableRemaining_ThrowsWithoutHugeAllocation()
    {
        // A corrupt frame declaring int.MaxValue with 5 real bytes must fail BEFORE renting
        // ~2 GB. Seekable streams expose the remaining byte count, so the bound is checkable
        // up front. Allocation is measured to pin the "before renting" property — without the
        // bound, ArrayPool.Rent(int.MaxValue) allocates the full buffer first and the same
        // EndOfStreamException only surfaces afterwards.
        using var ms = new MemoryStream([1, 2, 3, 4, 5]);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => PooledReadBuffer.RentAndRead(ms, int.MaxValue);
        act.Should().Throw<EndOfStreamException>();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(1_000_000,
            "the declared-length bound must reject the corrupt frame before renting a ~2 GB buffer");
    }

    [Fact]
    public void RentAndRead_FailedReadOnNonSeekableStream_ReturnsArrayToPool()
    {
        // Non-seekable streams cannot pre-check remaining bytes, so the failure surfaces from
        // ReadExact AFTER renting — the rented array must be returned on that path, or every
        // truncated frame in a corrupt-segment scan permanently leaks a pool slot.
        const int size = 4096;
        var probe = ArrayPool<byte>.Shared.Rent(size);
        ArrayPool<byte>.Shared.Return(probe);

        using var truncated = new NonSeekableStream(new byte[5]);
        var act = () => PooledReadBuffer.RentAndRead(truncated, size);
        act.Should().Throw<EndOfStreamException>();

        // The pool's thread-local slot returns the most recently returned array of this size:
        // if RentAndRead returned its rented array (which was `probe`), we get `probe` back;
        // if it leaked, we get a fresh array.
        var after = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            after.Should().BeSameAs(probe,
                "the failure path must return the rented array to the pool instead of leaking it");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(after);
        }
    }

    [Fact]
    public void RentAndRead_ForgedLengthOnNonSeekableStream_FailsTypedWithBoundedAllocation()
    {
        // The declared length is corruption-controlled data on EVERY stream kind. Seekable
        // streams bound it against the remaining bytes before renting; a non-seekable stream
        // (network, pipe, decompression) must not pre-rent the full attacker-declared length —
        // a 10-byte frame declaring int.MaxValue would otherwise drive a ~2 GiB allocation
        // before a single payload byte is read (the BSSG021 defect class, in the sanctioned
        // helper). The read must grow allocation only as bytes actually arrive.
        using var tiny = new NonSeekableStream(new byte[10]);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => PooledReadBuffer.RentAndRead(tiny, int.MaxValue);
        act.Should().Throw<EndOfStreamException>(
            "the stream ends long before the forged declared length");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(1024 * 1024,
            "allocation must be bounded by the bytes the stream actually supplied, not the forged declared length");
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
