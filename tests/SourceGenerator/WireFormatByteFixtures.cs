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
using System.Reflection;
using Xunit.Abstractions;

namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// Phase-12 batch D1 — byte-level snapshot fixtures pinning the TaggedWireEmitter output for
/// every shape MarketData persists. A drift in either the emitter or these expected hex
/// strings fails the test loudly; reviewers must update the doc and the fixture in the same
/// commit.
/// </summary>
public sealed class WireFormatByteFixtures(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // ── 1: DateOnly = 2026-04-25 ────────────────────────────────────────────

    [Fact]
    public void DateOnly_2026_04_25_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_DateOnly
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly D);
                public static class F { public static R Make() => new R(new System.DateOnly(2026, 4, 25)); }
            }
            """;
        AssertEncodesTo(source, "D1_DateOnly.R", "D1_DateOnly.F", "06 00 01 08 92 93 2D");
    }

    // ── 2: DateTimeOffset UTC ───────────────────────────────────────────────

    [Fact]
    public void DateTimeOffset_Utc_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_DtoUtc
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateTimeOffset At);
                public static class F
                {
                    public static R Make() => new R(new System.DateTimeOffset(2026, 4, 25, 9, 30, 0, System.TimeSpan.Zero));
                }
            }
            """;
        AssertEncodesTo(source, "D1_DtoUtc.R", "D1_DtoUtc.F",
            "0E 00 01 0C 0A 80 B8 E5 C6 D3 D5 A8 EF 08 00");
    }

    // ── 3: DateTimeOffset non-UTC ──────────────────────────────────────────

    [Fact]
    public void DateTimeOffset_NonUtcOffset_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_DtoNonUtc
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateTimeOffset At);
                public static class F
                {
                    public static R Make() => new R(new System.DateTimeOffset(2026, 4, 25, 16, 0, 0, System.TimeSpan.FromHours(-5)));
                }
            }
            """;
        AssertEncodesTo(source, "D1_DtoNonUtc.R", "D1_DtoNonUtc.F",
            "0F 00 01 0C 0B 80 80 D8 A2 BB DC A8 EF 08 D7 04");
    }

    // ── 4, 5, 6: Decimal at scale 4 — positive, negative, zero ──────────────

    [Fact]
    public void Decimal_Scale4_PositiveValue_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_DecPos
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(4)] decimal V);
                public static class F { public static R Make() => new R(123.4567m); }
            }
            """;
        AssertEncodesTo(source, "D1_DecPos.R", "D1_DecPos.F",
            "13 00 01 0D 87 D6 12 00 00 00 00 00 00 00 00 00 00 00 04 00");
    }

    [Fact]
    public void Decimal_Scale4_NegativeValue_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_DecNeg
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(4)] decimal V);
                public static class F { public static R Make() => new R(-123.4567m); }
            }
            """;
        AssertEncodesTo(source, "D1_DecNeg.R", "D1_DecNeg.F",
            "13 00 01 0D 87 D6 12 00 00 00 00 00 00 00 00 00 00 00 04 80");
    }

    [Fact]
    public void Decimal_Scale4_Zero_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_DecZero
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(4)] decimal V);
                public static class F { public static R Make() => new R(0m); }
            }
            """;
        AssertEncodesTo(source, "D1_DecZero.R", "D1_DecZero.F",
            "13 00 01 0D 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00");
    }

    // ── 7: Guid known value ─────────────────────────────────────────────────

    [Fact]
    public void Guid_KnownValue_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_Guid
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.Guid G);
                public static class F
                {
                    public static R Make() => new R(new System.Guid("12345678-1234-5678-1234-567812345678"));
                }
            }
            """;
        AssertEncodesTo(source, "D1_Guid.R", "D1_Guid.F",
            "13 00 01 0D 78 56 34 12 34 12 78 56 12 34 56 78 12 34 56 78");
    }

    // ── 8 & 9: long varint compaction (small, max) ─────────────────────────

    [Fact]
    public void Long_SmallVolume_VarintBytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_LongSmall
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] long Volume);
                public static class F { public static R Make() => new R(100_000L); }
            }
            """;
        AssertEncodesTo(source, "D1_LongSmall.R", "D1_LongSmall.F",
            "06 00 01 08 C0 9A 0C");
    }

    [Fact]
    public void Long_MaxValue_VarintBytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_LongMax
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] long Volume);
                public static class F { public static R Make() => new R(long.MaxValue); }
            }
            """;
        AssertEncodesTo(source, "D1_LongMax.R", "D1_LongMax.F",
            "0D 00 01 08 FE FF FF FF FF FF FF FF FF 01");
    }

    // ── 10 & 11: string ASCII, UTF-8 multibyte ──────────────────────────────

    [Fact]
    public void String_AsciiBytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_StrAscii
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] string S);
                public static class F { public static R Make() => new R("AAPL"); }
            }
            """;
        AssertEncodesTo(source, "D1_StrAscii.R", "D1_StrAscii.F",
            "08 00 01 0C 04 41 41 50 4C");
    }

    [Fact]
    public void String_Utf8Multibyte_Bytes()
    {
        // 'ñ' encodes to 0xC3 0xB1 in UTF-8 — two bytes — so the length-prefix counts BYTES,
        // not chars. The fixture pins this contract: "señor" is 5 chars but 6 UTF-8 bytes.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_StrUtf8
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] string S);
                public static class F { public static R Make() => new R("señor"); }
            }
            """;
        AssertEncodesTo(source, "D1_StrUtf8.R", "D1_StrUtf8.F",
            "0A 00 01 0C 06 73 65 C3 B1 6F 72");
    }

    // ── 12 & 13: Nullable<DateOnly> — null and present ─────────────────────

    [Fact]
    public void Nullable_DateOnly_Null_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_NullDateNull
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly? D);
                public static class F { public static R Make() => new R((System.DateOnly?)null); }
            }
            """;
        AssertEncodesTo(source, "D1_NullDateNull.R", "D1_NullDateNull.F", "02 00 01");
    }

    [Fact]
    public void Nullable_DateOnly_Present_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_NullDatePres
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly? D);
                public static class F { public static R Make() => new R(new System.DateOnly(2026, 4, 25)); }
            }
            """;
        AssertEncodesTo(source, "D1_NullDatePres.R", "D1_NullDatePres.F",
            "06 00 01 08 92 93 2D");
    }

    // ── 14: full MarketData Bar shape ──────────────────────────────────────

    [Fact]
    public void MarketDataBar_FullShape_Bytes()
    {
        // DateOnly + 4 decimals (with explicit scale) + long + Guid — every wire-type arm in
        // a single record. The hex pin guards against silent drift in any one field's encoding.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_Bar
            {
                [StorageSerializable]
                public partial record struct Bar(
                    [property: Tag(1)] System.DateOnly Date,
                    [property: Tag(2)][property: DecimalScale(4)] decimal Open,
                    [property: Tag(3)][property: DecimalScale(4)] decimal High,
                    [property: Tag(4)][property: DecimalScale(4)] decimal Low,
                    [property: Tag(5)][property: DecimalScale(4)] decimal Close,
                    [property: Tag(6)] long Volume,
                    [property: Tag(7)] System.Guid Id);

                public static class F
                {
                    public static Bar Make() => new Bar(
                        new System.DateOnly(2026, 4, 25),
                        100m, 101m, 99m, 100.5m,
                        1_000_000L,
                        new System.Guid("12345678-1234-5678-1234-567812345678"));
                }
            }
            """;
        AssertEncodesTo(source, "D1_Bar.Bar", "D1_Bar.F",
            "5F 00 01 08 92 93 2D " +
            "15 64 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "1D 65 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "25 63 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "2D ED 03 00 00 00 00 00 00 00 00 00 00 00 00 01 00 " +
            "30 80 89 7A " +
            "3D 78 56 34 12 34 12 78 56 12 34 56 78 12 34 56 78");
    }

    // ── 15: empty record — tag-0 + version-1 + length-prefix only ──────────

    [Fact]
    public void EmptyRecord_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D1_Empty
            {
                [StorageSerializable]
                public partial record struct R();
                public static class F { public static R Make() => new R(); }
            }
            """;
        AssertEncodesTo(source, "D1_Empty.R", "D1_Empty.F", "02 00 01");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════════════════

    private void AssertEncodesTo(string source, string recordFqn, string factoryFqn, string expectedHex)
    {
        var asm = CompileAndLoad(source);
        var factory = asm.GetType(factoryFqn)!;
        var original = factory.GetMethod("Make")!.Invoke(null, null)!;

        var actualBytes = Serialize(original);
        var actualHex = FormatHex(actualBytes);

        // CAPTURE PASS: when expectedHex is "??", surface the actual hex via the test output
        // and fail loudly so the reviewer can paste it back into the test.
        if (expectedHex.Trim() == "??")
        {
            _output.WriteLine($"CAPTURE {recordFqn}: \"{actualHex}\"");
            throw new Xunit.Sdk.XunitException(
                $"Capture-mode placeholder '??' for {recordFqn}. Actual bytes: \"{actualHex}\". " +
                "Replace the '??' in the source with this hex string to lock the encoding.");
        }

        var expectedBytes = ParseHex(expectedHex);
        actualBytes.Should().Equal(expectedBytes,
            $"encoding of {recordFqn} drifted. Got \"{actualHex}\", expected \"{expectedHex}\". " +
            "If the change is intentional, update the locked hex in the same commit.");

        // Round-trip closure — re-serialising the deserialised value must reproduce the bytes.
        var roundtrip = Deserialize(asm, recordFqn, actualBytes);
        Serialize(roundtrip).Should().Equal(actualBytes,
            $"round-trip of {recordFqn} must reproduce the original bytes byte-for-byte");
    }

    private static byte[] ParseHex(string hex)
    {
        var tokens = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            bytes[i] = Convert.ToByte(tokens[i], 16);
        }
        return bytes;
    }

    private static string FormatHex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "TestAsm_" + Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);
        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty(because: "generated code must compile; got:\n" +
            string.Join("\n", errors.Select(d => d.ToString())));

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        result.Success.Should().BeTrue();
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static byte[] Serialize(object record)
    {
        var method = record.GetType().GetMethod("Serialize", [typeof(Stream)])!;
        using var ms = new MemoryStream();
        method.Invoke(record, [ms]);
        return ms.ToArray();
    }

    private static object Deserialize(Assembly asm, string typeFqn, byte[] bytes)
    {
        var t = asm.GetType(typeFqn)!;
        var method = t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!;
        using var ms = new MemoryStream(bytes, writable: false);
        return method.Invoke(null, [ms])!;
    }
}
