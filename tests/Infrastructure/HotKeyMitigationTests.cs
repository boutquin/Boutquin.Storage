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

public sealed class HotKeyMitigationTests
{
    [Fact]
    public void GetReadKeys_ReturnsEveryDistinctSalt()
    {
        var strategy = new SaltedHotKeyStrategy<string>();

        var keys = strategy.GetReadKeys("celebrity", 4);

        keys.Should().HaveCount(4).And.OnlyHaveUniqueItems();
        keys.Should().OnlyContain(key => key.StartsWith("Y2VsZWJyaXR5:salt-", StringComparison.Ordinal));
    }

    [Fact]
    public void GetWriteKey_IsDeterministicAndBelongsToTheReadSet()
    {
        var strategy = new SaltedHotKeyStrategy<string>();

        var first = strategy.GetWriteKey("celebrity", 8, 42);
        var second = strategy.GetWriteKey("celebrity", 8, 42);

        second.Should().Be(first);
        strategy.GetReadKeys("celebrity", 8).Should().Contain(first);
    }

    [Fact]
    public void SaltCount_MustBePositive()
    {
        var strategy = new SaltedHotKeyStrategy<string>();

        var act = () => strategy.GetReadKeys("key", 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
