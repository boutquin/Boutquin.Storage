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
/// Phase-12 batch D3 — diagnostic-completeness audit results.
/// </summary>
/// <remarks>
/// <para>
/// The audit grepped every BSSG identifier defined in <c>RuleIdentifiers.cs</c> against
/// the test corpus. Two IDs surfaced as uncovered; both are intentionally so:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>BSSG004 (DuplicateAssemblyDefaults)</b> — declared on
/// <c>StorageDefaultsAttribute</c> with <c>AttributeUsage(AllowMultiple = false)</c>.
/// The C# compiler rejects a second <c>[assembly: StorageDefaults]</c> with CS0579 at
/// bind time, so the generator's <c>ForAttributeWithMetadataName</c> pipeline only
/// receives one bound occurrence. The <c>locations.Length &gt; 1</c> guard at
/// <c>StorageSourceGenerator.cs</c> is therefore unreachable from any user source under
/// modern Roslyn. Treated as accepted/unreachable per the accepted/unreachable escape hatch.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>BSSG009</b> — intentionally <i>reserved unshipped</i> per the comment in
/// <c>RuleIdentifiers.cs</c>: "Phase 2 — tag-validation diagnostics.
/// BSSG009 is reserved unshipped." The slot is held so the contiguous tag-validation
/// block (BSSG010..BSSG013) can grow without renumbering. No diagnostic descriptor
/// exists; no test is required.
/// </description>
/// </item>
/// </list>
/// <para>
/// Every other BSSG ID (001, 002, 003, 005, 006, 007, 008, 010..013, 014, 015, 016,
/// 017, 018, 019, 020, 021, 022) has at least one test asserting <c>d.Id == "BSSGNNN"</c>
/// in the test corpus.
/// </para>
/// </remarks>
public sealed class Phase12DiagnosticAuditTests
{
    // Audit completed; no fault-injection tests required. The audit results are pinned
    // in this file's XML doc comment so a future grep for "BSSG004" or "BSSG009" lands
    // on the documentation explaining their intentional non-coverage.
}
