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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Pins <see cref="WindowReadStream"/> — the read-only, seekable window over a sub-range of a
/// base stream that lets the columnar reader pull only the projected column spans of one block
/// straight from the segment file, skipping unprojected spans on disk. The
/// <see cref="WindowReadStream.BytesRead"/> counter is the byte-accounting seam the
/// projection-pushdown test asserts against.
/// </summary>
public sealed class WindowReadStreamTests
{
    private static byte[] Ramp(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)i;
        }
        return bytes;
    }

    [Fact]
    public void Window_ExposesOnlyTheSubRange_AsLengthAndContent()
    {
        var baseBytes = Ramp(100);
        using var baseStream = new MemoryStream(baseBytes, writable: false);
        var window = new WindowReadStream(baseStream, offset: 30, length: 20);

        window.Length.Should().Be(20);
        window.Position.Should().Be(0);

        var buf = new byte[20];
        window.ReadExactly(buf, 0, 20);

        buf.Should().Equal(baseBytes.AsSpan(30, 20).ToArray());
        window.Position.Should().Be(20);
    }

    [Fact]
    public void Ctor_LengthWhoseEndOverflowsLong_IsRejected()
    {
        using var baseStream = new MemoryStream(Ramp(100), writable: false);

        var act = () => new WindowReadStream(baseStream, offset: 8, length: long.MaxValue);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "offset + length wraps past long.MaxValue, so the window would claim bytes the base stream does not hold");
    }

    [Fact]
    public void Window_Read_StopsAtWindowEnd_NeverReadingBaseBeyondTheSpan()
    {
        var baseBytes = Ramp(100);
        using var baseStream = new MemoryStream(baseBytes, writable: false);
        var window = new WindowReadStream(baseStream, offset: 90, length: 10);

        var buf = new byte[50];
        var read = window.Read(buf, 0, 50);

        read.Should().Be(10, "the window is only 10 bytes even though the caller asked for 50");
        window.Read(buf, 0, 50).Should().Be(0, "the window is exhausted");
    }

    [Fact]
    public void Window_SeekFromBegin_MapsRelativeToWindowStart()
    {
        var baseBytes = Ramp(100);
        using var baseStream = new MemoryStream(baseBytes, writable: false);
        var window = new WindowReadStream(baseStream, offset: 40, length: 30);

        window.Seek(10, SeekOrigin.Begin);
        window.Position.Should().Be(10);

        var buf = new byte[5];
        window.ReadExactly(buf, 0, 5);
        buf.Should().Equal(baseBytes.AsSpan(50, 5).ToArray());
    }

    [Fact]
    public void Window_SeekFromEnd_MapsToWindowEnd()
    {
        var baseBytes = Ramp(100);
        using var baseStream = new MemoryStream(baseBytes, writable: false);
        var window = new WindowReadStream(baseStream, offset: 40, length: 30);

        window.Seek(-4, SeekOrigin.End);
        window.Position.Should().Be(26);

        var buf = new byte[4];
        window.ReadExactly(buf, 0, 4);
        buf.Should().Equal(baseBytes.AsSpan(66, 4).ToArray());
    }

    [Fact]
    public void Window_BytesRead_CountsOnlyBytesDeliveredThroughTheWindow()
    {
        var baseBytes = Ramp(100);
        using var baseStream = new MemoryStream(baseBytes, writable: false);
        var window = new WindowReadStream(baseStream, offset: 0, length: 100);

        // Read 10 bytes near the front, seek past the middle (no read), read 10 near the end.
        var buf = new byte[10];
        window.ReadExactly(buf, 0, 10);
        window.Seek(80, SeekOrigin.Begin);
        window.ReadExactly(buf, 0, 10);

        window.BytesRead.Should().Be(20,
            "only the 20 bytes actually delivered count — the 70 skipped bytes between the two reads do not");
    }

    [Fact]
    public void Window_DoesNotDisposeBaseStream()
    {
        var baseStream = new MemoryStream(Ramp(10), writable: false);
        var window = new WindowReadStream(baseStream, 0, 10);

        window.Dispose();

        // The base stream must remain usable — the window borrows it.
        var act = () => _ = baseStream.Length;
        act.Should().NotThrow();
        baseStream.Dispose();
    }

    [Fact]
    public void Window_WithoutPhysicalDescriptor_UsesStablePerWindowIdentity()
    {
        using var baseStream = new MemoryStream(Ramp(10), writable: false);
        using var first = new WindowReadStream(baseStream, 0, 5);
        using var second = new WindowReadStream(baseStream, 5, 5);

        var firstIdentity = ReadPhysicalSegmentIdentity(first);
        var secondIdentity = ReadPhysicalSegmentIdentity(second);

        firstIdentity.Should().NotBeNullOrWhiteSpace();
        firstIdentity.Should().NotBe(secondIdentity,
            "windows over an unlabelled non-file stream have no shared physical source to deduplicate");
        ReadPhysicalSegmentIdentity(first).Should().Be(firstIdentity,
            "one window's identity must stay stable for its lifetime");
    }

    [Fact]
    public void Window_RejectsOutOfRangeWindow()
    {
        using var baseStream = new MemoryStream(Ramp(10), writable: false);

        ((Action)(() => new WindowReadStream(baseStream, offset: 5, length: 10)))
            .Should().Throw<ArgumentOutOfRangeException>("the window runs past the base stream's end");
    }

    private static string ReadPhysicalSegmentIdentity(Stream stream)
    {
        var identityInterface = stream.GetType().GetInterface(
            "Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar.IPhysicalSegmentIdentity");
        identityInterface.Should().NotBeNull("window streams must expose a public physical identity contract");

        var property = identityInterface!.GetProperty("PhysicalSegmentIdentity");
        property.Should().NotBeNull("the public physical identity contract must expose its immutable descriptor");

        return property!.GetValue(stream).Should().BeOfType<string>().Subject;
    }
}
