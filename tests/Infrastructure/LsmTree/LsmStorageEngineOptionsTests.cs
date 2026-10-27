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

using Boutquin.Storage.Domain.ValueObjects.LsmTree;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Validates that <see cref="LsmStorageEngineOptions{TKey,TValue}"/> enforces
/// its construction-time invariants.
/// </summary>
public sealed class LsmStorageEngineOptionsTests
{
    private static readonly ISegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>
        s_format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();

    // ───── MaxImmutableMemtables lower-bound guard (Fix 4) ────────────────

    [Fact]
    public void LsmStorageEngineOptions_MaxImmutableMemtablesAtOne_DoesNotThrow()
    {
        var act = () => new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 100,
            SegmentFolder: Path.GetTempPath(),
            SegmentPrefix: "seg",
            SegmentFormat: s_format,
            MaxImmutableMemtables: 1);
        act.Should().NotThrow();
    }

    [Fact]
    public void LsmStorageEngineOptions_MaxImmutableMemtablesAboveOne_DoesNotThrow()
    {
        var act = () => new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 100,
            SegmentFolder: Path.GetTempPath(),
            SegmentPrefix: "seg",
            SegmentFormat: s_format,
            MaxImmutableMemtables: 5);
        act.Should().NotThrow();
    }

    [Fact]
    public void LsmStorageEngineOptions_MaxImmutableMemtablesZero_ThrowsArgumentOutOfRange()
    {
        var act = () => new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 100,
            SegmentFolder: Path.GetTempPath(),
            SegmentPrefix: "seg",
            SegmentFormat: s_format,
            MaxImmutableMemtables: 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("MaxImmutableMemtables");
    }

    [Fact]
    public void LsmStorageEngineOptions_MaxImmutableMemtablesNegative_ThrowsArgumentOutOfRange()
    {
        var act = () => new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 100,
            SegmentFolder: Path.GetTempPath(),
            SegmentPrefix: "seg",
            SegmentFormat: s_format,
            MaxImmutableMemtables: -1);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("MaxImmutableMemtables");
    }
}
