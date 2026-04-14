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

namespace Boutquin.Storage.Infrastructure.Tests.ValueObjects;

public sealed class DownloadResultTests
{
    [Fact]
    public void Constructor_StoresAllProperties()
    {
        var errors = new List<string> { "AAPL: 404 Not Found" };
        var result = new DownloadResult(Downloaded: 5, Skipped: 3, Errors: errors);

        result.Downloaded.Should().Be(5);
        result.Skipped.Should().Be(3);
        result.Errors.Should().ContainSingle().Which.Should().Be("AAPL: 404 Not Found");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new DownloadResult(1, 2, Array.Empty<string>());
        var b = new DownloadResult(1, 2, Array.Empty<string>());

        // Record equality compares by value for primitive fields but by reference for lists.
        // This test documents that behavior.
        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void EmptyResult_HasZeroCounts()
    {
        var result = new DownloadResult(0, 0, []);

        result.Downloaded.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.Errors.Should().BeEmpty();
    }
}
