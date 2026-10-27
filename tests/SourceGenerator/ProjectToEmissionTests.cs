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
using Boutquin.Storage.Domain.Exceptions;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.SourceGenerator.Tests;

// ProjectTo emission tests on [StorageSerializable] / [ColumnarStorable] records.
// The generator must emit an IProjectable<TSelf> base-list entry and a ProjectTo method so that
// memtable-resident records expose the same projection shape as records read back from a columnar
// segment (DDIA Ch. 4 p. 141 uniformity).
//
// These tests compile a generator-produced record into a live assembly and exercise ProjectTo via
// reflection — same pattern as TaggedWireTests. The ProjectTo emission is the only way callers get
// the memtable-vs-segment uniformity guarantee without hand-implementing IProjectable<T> on every
// annotated record.
public sealed class ProjectToEmissionTests
{
    // The generated source must declare IProjectable<TSelf> in the base list. Asserted via
    // text search on the generator output rather than runtime type inspection, because the base
    // list is what C# parses; a missing interface-in-base would not compile even if the method
    // body was emitted.
    [Fact]
    public void StorageSerializable_Record_EmitsIProjectableBaseListEntry()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar(
                [property: Tag(1)] long Volume,
                [property: Tag(2)] double Close);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        var generated = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString())
            .Aggregate(string.Empty, (acc, s) => acc + "\n" + s);

        generated.Should().Contain("global::Boutquin.Storage.Domain.Interfaces.IProjectable<Bar>",
            "[StorageSerializable] records must declare IProjectable<TSelf> so the decorator can "
            + "cast them without a dynamic type check");
    }

    // The emitted ProjectTo signature must match the IProjectable<TSelf> contract. A drift
    // here would compile (the method implements the interface) but callers binding against
    // IProjectable<TSelf> would hit a resolution error.
    [Fact]
    public void StorageSerializable_Record_EmitsProjectToSignature()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar([property: Tag(1)] long Volume);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        var generated = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString())
            .Aggregate(string.Empty, (acc, s) => acc + "\n" + s);

        generated.Should().Contain(
            "public Bar ProjectTo(global::Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection projection)",
            "the emitter must produce the exact IProjectable<TSelf>.ProjectTo signature, with "
            + "a fully-qualified ColumnProjection parameter so ambiguity with user-space types "
            + "cannot break the generated code");
    }

    // ColumnProjection.All returns the record unchanged. Early-out branch avoids the
    // per-field switch when the caller wants every column.
    [Fact]
    public void StorageSerializable_Record_ProjectTo_All_ReturnsUnchanged()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar(
                [property: Tag(1)] long Volume,
                [property: Tag(2)] double Close);
            """;

        var asm = CompileAndLoad(source);
        var record = InstantiateRecord(asm, "TestNamespace.Bar", 1_000_000L, 42.5);

        var projected = InvokeProjectTo(record, ColumnProjection.All);

        projected.Should().Be(record,
            "ColumnProjection.All is the uniformity-preserving identity — no clipping");
    }

    // A non-All projection clips every unprojected field to default(TField). The record's
    // unprojected fields stay at `default` and the projected field matches the original.
    [Fact]
    public void StorageSerializable_Record_ProjectTo_Subset_ClipsFields()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar(
                [property: Tag(1)] long Volume,
                [property: Tag(2)] double Close);
            """;

        var asm = CompileAndLoad(source);
        var record = InstantiateRecord(asm, "TestNamespace.Bar", 1_000_000L, 42.5);

        var projected = InvokeProjectTo(record, new ColumnProjection(["Volume"]));

        var t = projected.GetType();
        t.GetProperty("Volume")!.GetValue(projected).Should().Be(1_000_000L);
        t.GetProperty("Close")!.GetValue(projected).Should().Be(0.0,
            "unprojected double should be default(double) — the whole read-amplification argument "
            + "is that unprojected fields are not read; in-memory ProjectTo preserves that shape");
    }

    // A [StorageSerializable] record CLASS (a supported shape — BSSG003 is Warning-only) with a
    // non-empty subset projection must return a populated instance, not NRE. The emitter used to
    // initialise __result with default(TSelf), which is null for a record class, so the first
    // `__result with { ... }` dereferenced null and threw NullReferenceException at runtime. The
    // fix branches on IsRecordStruct and emits `new T()` for the record-class case (parity with
    // ColumnarCodeGenerator.AppendProjectTo).
    [Fact]
    public void StorageSerializable_RecordClass_ProjectTo_Subset_ReturnsPopulatedInstance()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record class Quote(
                [property: Tag(1)] long Volume,
                [property: Tag(2)] double Close);
            """;

        var asm = CompileAndLoad(source);
        var record = InstantiateRecord(asm, "TestNamespace.Quote", 1_000_000L, 42.5);

        var projected = InvokeProjectTo(record, new ColumnProjection(["Volume"]));

        var t = projected.GetType();
        t.GetProperty("Volume")!.GetValue(projected).Should().Be(1_000_000L,
            "the projected field must round-trip on a record class, not NRE on a null __result");
        t.GetProperty("Close")!.GetValue(projected).Should().Be(0.0,
            "unprojected fields stay at default(TField) on the freshly constructed instance");
    }

    // Strict mode (AllowMissing = false) raises ColumnNotFoundException on an unknown
    // projected name. Mirrors IColumnarSegmentFormat.ReadAsync's missing-column behaviour so a
    // caller sees the same exception regardless of where the record came from.
    [Fact]
    public void StorageSerializable_Record_ProjectTo_MissingFieldName_Strict_Throws()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar([property: Tag(1)] long Volume);
            """;

        var asm = CompileAndLoad(source);
        var record = InstantiateRecord(asm, "TestNamespace.Bar", 99L);

        var projection = new ColumnProjection(["Bogus"]);

        Action act = () => InvokeProjectTo(record, projection);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<ColumnNotFoundException>();
    }

    // Lenient mode silently drops unknown projected names; no ColumnNotFoundException is
    // raised. The returned record has every declared field at default (none of the requested
    // names match).
    [Fact]
    public void StorageSerializable_Record_ProjectTo_MissingFieldName_Lenient_Succeeds()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar([property: Tag(1)] long Volume);
            """;

        var asm = CompileAndLoad(source);
        var record = InstantiateRecord(asm, "TestNamespace.Bar", 99L);

        var projection = new ColumnProjection(["Bogus"], AllowMissing: true);

        var projected = InvokeProjectTo(record, projection);

        projected.GetType().GetProperty("Volume")!.GetValue(projected).Should().Be(0L,
            "no field matched — every declared field is default; AllowMissing swallows the "
            + "unknown name rather than throwing");
    }

    // BSSG006 pattern. A record that already lists IProjectable<TSelf> in its base list
    // must not receive a second generator-emitted implementation; the generator skips the entire
    // IProjectable emission (base list + method) for that record. Matches the existing skip
    // logic for ISerializable<T>.
    [Fact]
    public void ExplicitlyImplementsIProjectable_SkipsGeneratorEmission()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Interfaces;
            using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar([property: Tag(1)] long Volume)
                : IProjectable<Bar>
            {
                public Bar ProjectTo(ColumnProjection projection) => this;
            }
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        var generated = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString())
            .Aggregate(string.Empty, (acc, s) => acc + "\n" + s);

        generated.Should().NotContain(
            "public Bar ProjectTo(global::Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection projection)",
            "when the user hand-implements IProjectable<TSelf>, the generator must skip its own "
            + "ProjectTo emission — otherwise both implementations would collide on the partial");
    }

    // Phase 1 fixture ProjectableTestBar has been operating on a hand-rolled ProjectTo for months.
    // This test is a sanity guard that a consumer who both hand-implements IProjectable AND
    // annotates with [StorageSerializable] does not end up with two ProjectTo methods on the same
    // partial type — which would be a CS0111 error and fail the build silently for every
    // consumer who migrated before the generator landed.
    [Fact]
    public void ExplicitlyImplementsIProjectable_Still_Compiles()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Interfaces;
            using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar([property: Tag(1)] long Volume)
                : IProjectable<Bar>
            {
                public Bar ProjectTo(ColumnProjection projection) => this;
            }
            """;

        var asm = CompileAndLoad(source);
        var record = InstantiateRecord(asm, "TestNamespace.Bar", 42L);

        // Invoke via the interface method; the user-supplied implementation returns `this` so
        // every field round-trips regardless of projection.
        var projected = InvokeProjectTo(record, new ColumnProjection(["Volume"]));

        projected.GetType().GetProperty("Volume")!.GetValue(projected).Should().Be(42L,
            "user-owned IProjectable<T> implementation must be the one invoked — the generator "
            + "must not emit a colliding second method");
    }

    // ColumnarStorable records run through a separate generator pipeline. They must pick
    // up the same IProjectable<TSelf> base list + ProjectTo body so the engine's runtime cast
    // resolves identically regardless of which annotation the record carries (DDIA p. 141
    // uniformity is independent of the wire-format choice).
    [Fact]
    public void ColumnarStorable_Record_EmitsIProjectableBaseListEntry()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [ColumnarStorable]
            public partial record Bar
            {
                public System.DateOnly Date { get; init; }
                public long Volume { get; init; }
                public Bar() { }
            }
            """;

        var compilation = TestHelpers.CreateCompilation(source);
        var generator = new ColumnarSourceGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var generated = driver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString())
            .Aggregate(string.Empty, (acc, s) => acc + "\n" + s);

        generated.Should().Contain("IProjectable<Bar>",
            "[ColumnarStorable] records must also gain IProjectable<TSelf> so the k-way merge "
            + "can cast memtable records without dispatching on the annotation kind");
        generated.Should().Contain("ProjectTo(global::Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection projection)",
            "the ProjectTo signature must match the IProjectable<TSelf> contract across both "
            + "generator pipelines so the single runtime cast in the decorator works for either");
    }

    // Zero-allocation target for record-struct ProjectTo. Call ProjectTo 100k times on a
    // generator-emitted record struct; the per-call payload must stay on the stack. The test
    // tolerates a small fixed allocation for the argument array/list of FieldNames but rejects
    // per-call heap growth (which would be the tell-tale of a reference-type capture).
    [Fact]
    public void ProjectTo_ZeroAllocation_RecordStruct()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar(
                [property: Tag(1)] long Volume,
                [property: Tag(2)] double Close);

            public static class BarFactory
            {
                public static Bar Make() => new Bar(1_000_000L, 42.5);
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.BarFactory")!;
        var record = factory.GetMethod("Make")!.Invoke(null, null)!;
        var method = record.GetType().GetMethod("ProjectTo", [typeof(ColumnProjection)])!;
        var projection = new ColumnProjection(["Volume"]);
        var args = new object[] { projection };

        // Warmup runs — JIT + any caching — before the measurement window.
        for (var i = 0; i < 1000; i++)
        {
            _ = method.Invoke(record, args);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            _ = method.Invoke(record, args);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        var allocatedPerCall = (after - before) / 100_000.0;

        // Reflection Invoke's `object[]` boxing dominates this number, not the method body. A
        // pass-through zero-alloc target allows the boxed-result object from reflection but
        // forbids per-call LOH growth. An order-of-magnitude guard — the record-struct ProjectTo
        // itself is definitionally stack-only; any heap growth past ~200 B/call would indicate
        // an emitter regression (foreach on a non-enumerator, unintended closure, etc.).
        allocatedPerCall.Should().BeLessThan(200,
            "record-struct ProjectTo must not allocate per call beyond reflection's object-boxing "
            + "baseline; emission drift (e.g., LINQ in the body, closure capture) would fail this");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers — compile generator output into a live assembly and invoke ProjectTo via
    // reflection. Mirrors the TaggedWireTests pattern so generator drift is a build-level
    // failure rather than a silent text mismatch.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "TestAsm_" + System.Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        _ = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty(
            because: "generated code must compile before we can exercise ProjectTo; got:\n" +
                     string.Join("\n", errors.Select(d => d.ToString())));

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        result.Success.Should().BeTrue(because: "generated code must emit for runtime tests");

        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static object InstantiateRecord(Assembly asm, string typeFqn, params object[] ctorArgs)
    {
        var t = asm.GetType(typeFqn) ?? throw new InvalidOperationException($"Type {typeFqn} not found.");
        return Activator.CreateInstance(t, ctorArgs)!;
    }

    private static object InvokeProjectTo(object record, ColumnProjection projection)
    {
        var method = record.GetType().GetMethod("ProjectTo", [typeof(ColumnProjection)])
            ?? throw new InvalidOperationException("Record lacks ProjectTo(ColumnProjection).");
        return method.Invoke(record, [projection])!;
    }
}
