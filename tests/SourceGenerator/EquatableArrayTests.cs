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
namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// Unit tests for <see cref="EquatableArray{T}"/> — edge cases around the default (null-backed)
/// instance that the normal pipeline construction path never produces but that struct semantics
/// allow.
/// </summary>
public sealed class EquatableArrayTests
{
    [Fact]
    public void Indexer_DefaultInstance_ThrowsIndexOutOfRangeException()
    {
        // default(EquatableArray<int>) has _array == null. The null-forgiving operator `!`
        // in `_array![index]` converts the NullReferenceException to a confusing crash.
        // The fix uses a null-check that throws IndexOutOfRangeException — the same exception
        // a regular array throws on an out-of-bounds access.
        Assert.Throws<IndexOutOfRangeException>(() =>
        {
            var arr = default(EquatableArray<int>);
            _ = arr[0];
        });
    }
}
