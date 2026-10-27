// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.ValueLog;

/// <summary>
/// M-4 AC-4.7 — the reader's offset bound is checked against the unsigned value
/// (<c>pointer.Offset &gt; (ulong)long.MaxValue</c>), not a post-cast <c>(long)offset &lt; 0</c>.
/// A forged offset above <c>long.MaxValue</c> must surface a typed
/// <see cref="CorruptValueLogPointerException"/> rather than wrapping to a negative long and being
/// mishandled (F-83).
/// </summary>
public sealed class ValueLogReaderOverflowTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;
    private ValueLogWriter? _writer;

    public ValueLogReaderOverflowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync();
        }
        if (_manifest is not null)
        {
            await _manifest.DisposeAsync();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Read_ForgedOffsetAboveLongMaxValue_ThrowsTypedCorruptPointer()
    {
        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default);
        // A real entry so segment file 0 exists and is openable.
        await _writer.AppendAsync(Encoding.UTF8.GetBytes("real"), 1);

        var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        using var _r = reader;

        // Offset just above long.MaxValue — casting to long wraps negative; the unsigned bound must
        // reject it with a typed exception.
        var forged = new ValueLogPointer(0u, (ulong)long.MaxValue + 1);

        var act = () => reader.Read(forged);

        act.Should().Throw<CorruptValueLogPointerException>(
            "an offset above long.MaxValue is a corrupt pointer, surfaced as a typed exception");
    }
}
