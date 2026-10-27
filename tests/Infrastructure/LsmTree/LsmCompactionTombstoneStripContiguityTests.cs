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
#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Finding #2 — compaction may strip a tombstone only when the picked segment set covers a
/// contiguous prefix <c>[0..k]</c>. Gating on "is segment 0 picked?" alone is unsafe: a
/// non-contiguous pick (e.g. <c>{0, 2}</c>, which size-tiered and leveled strategies routinely
/// produce) leaves an older unpicked segment that can still carry a shadowed value, so stripping
/// the tombstone resurrects it (DDIA 2e p. 124 — a tombstone may be dropped only after it has
/// shadowed every older occurrence).
/// </summary>
public sealed class LsmCompactionTombstoneStripContiguityTests
{
    private static bool Strippable(params int[] picked)
        => LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .IsContiguousFromBottom([.. picked]);

    [Fact]
    public void SingletonBottom_IsStrippable()
        => Strippable(0).Should().BeTrue();

    [Fact]
    public void ContiguousPrefix_IsStrippable()
    {
        Strippable(0, 1).Should().BeTrue();
        Strippable(0, 1, 2).Should().BeTrue();
        Strippable(0, 1, 2, 3).Should().BeTrue();
    }

    [Fact]
    public void NonContiguousPickIncludingBottom_IsNotStrippable()
    {
        // {0, 2} — index 1 is unpicked and older than the tombstone in index 2.
        Strippable(0, 2).Should().BeFalse();
        Strippable(0, 1, 3).Should().BeFalse();
        Strippable(0, 3).Should().BeFalse();
    }

    [Fact]
    public void PickMissingBottom_IsNotStrippable()
    {
        Strippable(1, 2).Should().BeFalse();
        Strippable(2).Should().BeFalse();
    }

    [Fact]
    public void EmptyPick_IsNotStrippable()
        => Strippable().Should().BeFalse();
}
