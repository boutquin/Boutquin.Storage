#!/usr/bin/env python3
# Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

"""Behavior tests for the public export release gate."""

from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "check-public-export.py"


class PublicExportGateTests(unittest.TestCase):
    def run_gate(self, export_dir: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--export-dir", str(export_dir)],
            check=False,
            capture_output=True,
            text=True,
        )

    @staticmethod
    def write_files(root: Path, files: dict[str, str]) -> None:
        for relative_path, content in files.items():
            path = root / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")

    def test_rejects_private_artifact_that_reaches_export(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(root, {"README.md": "# Public\n", "specs/plan.md": "secret\n"})

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("private artifact exported: specs/plan.md", result.stdout)

    def test_rejects_public_link_to_hidden_artifact(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {"README.md": "Read the [local instructions](CLAUDE.md).\n"},
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("reference to private artifact: CLAUDE.md", result.stdout)

    def test_rejects_normalized_private_targets_outside_markdown_prose(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "Read the [encoded plan](s%70ecs/plan.md).\n",
                    "docs/page.html": '<a href="../CLAUDE.md">local guidance</a>\n',
                },
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("reference to private artifact: specs/plan.md", result.stdout)
            self.assertIn("reference to private artifact: CLAUDE.md", result.stdout)

    def test_rejects_bare_internal_planning_references_in_public_prose(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "See feature-secret-index.md for the design.\n",
                    "src/Example.cs": "// Keep this on the roadmap until launch.\n",
                },
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode)
            self.assertIn(
                "reference to private planning artifact: feature-secret-index.md",
                result.stdout,
            )
            self.assertIn(
                "reference to private planning artifact: roadmap",
                result.stdout,
            )

    def test_accepts_link_to_shipped_delivery_status_document(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.write_files(
                root,
                {
                    "README.md": "See [delivery status](docs/ROADMAP.md).\n",
                    "docs/ROADMAP.md": "# Delivery status\n",
                },
            )
            result = self.run_gate(root)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_checks_absolute_links_into_the_public_repository(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.write_files(root, {
                "README.md": "[Guide](https://github.com/boutquin/Boutquin.Storage/blob/main/docs/missing.md)\n",
            })
            result = self.run_gate(root)
            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn("link target does not exist: docs/missing.md", result.stdout)

    def test_rejects_missing_relative_link(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(root, {"docs/guide.md": "See [details](missing.md).\n"})

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("link target does not exist: docs/missing.md", result.stdout)

    def test_rejects_missing_anchor(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "# Existing section\n",
                    "docs/guide.md": "See [API](../README.md#missing-section).\n",
                },
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("anchor does not exist: README.md#missing-section", result.stdout)

    def test_accepts_markdown_and_html_anchors(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "# Public API\n\n## Public API\n",
                    "docs/page.html": '<h2 id="details">Details</h2>\n',
                    "docs/guide.md": (
                        "[first](../README.md#public-api)\n"
                        "[duplicate](../README.md#public-api-1)\n"
                        '<a href="page.html#details">HTML details</a>\n'
                    ),
                },
            )

            result = self.run_gate(root)

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("public export check passed", result.stdout)

    def test_anchor_preserves_inline_code_text_in_heading(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "## The `Key` type\n",
                    "docs/guide.md": "[Key docs](../README.md#the-key-type)\n",
                },
            )

            result = self.run_gate(root)

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_checks_xml_doc_hrefs_but_ignores_generated_csharp_strings(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "# Public\n",
                    "src/Good.cs": (
                        'private const string Generated = "x\\\" href=\\\"missing.md\\\"";\n'
                    ),
                    "src/Bad.cs": (
                        '/// See <see href="../missing.md">the missing guide</see>.\n'
                    ),
                },
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("src/Bad.cs:1", result.stdout)
            self.assertIn("link target does not exist: missing.md", result.stdout)
            self.assertNotIn("src/Good.cs", result.stdout)

    def test_does_not_treat_checker_regex_strings_as_public_prose(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "# Public\n",
                    "scripts/check.py": (
                        'PRIVATE = re.compile(r"CLAUDE\\.md|specs/")\n'
                    ),
                },
            )

            result = self.run_gate(root)

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_rejects_hardcoded_private_dependency_path_in_public_docs(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "CONTRIBUTING.md": (
                        "Build `../Boutquin.Domain/src/Boutquin.Domain.csproj` first.\n"
                    )
                },
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("hardcoded private dependency path", result.stdout)

    def test_rejects_private_references_in_non_markdown_exported_text(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "# Public\n",
                    ".github/workflows/x.yml": "# derived from specs/plan.md and CLAUDE.md\n",
                    "src/A.csproj": "<Project><!-- see specs/plan.md --></Project>\n",
                    "Directory.Build.props": "<Project><!-- see AGENTS.md --></Project>\n",
                    "docs/x.json": '{"note": "see specs/plan.md"}\n',
                    "run.sh": "# uses hooks/pre-commit and .claude/settings.json\n",
                },
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            for path in (
                ".github/workflows/x.yml:1",
                "src/A.csproj:1",
                "Directory.Build.props:1",
                "docs/x.json:1",
                "run.sh:1",
            ):
                self.assertIn(path, result.stdout)

    def test_rejects_private_reference_in_trailing_csharp_comment(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "# Public\n",
                    "src/A.cs": (
                        "int x = 1;\n"
                        "int y = 2; // design: specs/plan.md and CLAUDE.md\n"
                        "int z = 3; /* see AGENTS.md */ int w = 4;\n"
                    ),
                },
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn("src/A.cs:2", result.stdout)
            self.assertIn("src/A.cs:3", result.stdout)
            self.assertNotIn("src/A.cs:1", result.stdout)

    def test_ignores_comment_lookalikes_inside_csharp_string_literals(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": "# Public\n",
                    "src/Good.cs": (
                        'var a = "see // specs/plan.md";\n'
                        'var b = @"verbatim // CLAUDE.md";\n'
                        "var c = '\"'; var d = \"AGENTS.md /* x */\";\n"
                        'var e = """\n'
                        "// specs/inside-raw-string.md\n"
                        '""";\n'
                        'var f = @"first\n'
                        "// CLAUDE.md inside a multi-line verbatim string\n"
                        '";\n'
                    ),
                },
            )

            result = self.run_gate(root)

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_rejects_internal_spec_section_citation_but_keeps_public_literature(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_files(
                root,
                {
                    "README.md": (
                        "Raft paper §5.2 and RFC 6962 §2.1 are fine.\n"
                        "See wire-format-2.0.md §6.3 for the layout.\n"
                    ),
                    "src/A.cs": (
                        "// Ongaro §5.4.2 commit rule.\n"
                        "int x = 1; // the §2.4 narrative\n"
                        "/// <remarks>pre-§2.10 behaviour</remarks>\n"
                    ),
                    "docs/guide.md": "The §2.2 single-checksum decision applies.\n",
                },
            )

            result = self.run_gate(root)

            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn("src/A.cs:2: [internal-section-citation]", result.stdout)
            self.assertIn("src/A.cs:3: [internal-section-citation]", result.stdout)
            self.assertIn("docs/guide.md:1: [internal-section-citation]", result.stdout)
            self.assertNotIn("README.md", result.stdout)
            self.assertNotIn("src/A.cs:1", result.stdout)

    def test_revision_mode_validates_the_git_archive_with_export_attributes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            self.write_files(
                repo,
                {
                    ".gitattributes": "specs/ export-ignore\n",
                    "README.md": "# Public\n",
                    "specs/private.md": "not public\n",
                },
            )
            subprocess.run(["git", "init", "-b", "main"], cwd=repo, check=True, capture_output=True)
            subprocess.run(
                ["git", "config", "user.email", "tests@example.invalid"],
                cwd=repo,
                check=True,
            )
            subprocess.run(["git", "config", "user.name", "Tests"], cwd=repo, check=True)
            subprocess.run(["git", "add", "."], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-m", "fixture"], cwd=repo, check=True, capture_output=True)

            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--revision", "HEAD"],
                cwd=repo,
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("public export check passed", result.stdout)


if __name__ == "__main__":
    unittest.main()
