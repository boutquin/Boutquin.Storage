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

"""Validate the exact file set intended for the public repository.

The default release use is ``--revision HEAD``. That mode asks ``git archive``
to apply the repository's export attributes, then validates the resulting tree.
``--export-dir`` validates an already-exported tree and keeps the checker usable
after the public repository has been created.
"""

from __future__ import annotations

import argparse
import html
import posixpath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unicodedata
import urllib.parse
from collections import Counter
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Iterable, Iterator


PRIVATE_ROOT_FILES = {
    "AGENTS.md",
    "CLAUDE.local.md",
    "CLAUDE.md",
    "review-accepted.yaml",
}
PRIVATE_ROOT_DIRECTORIES = {
    ".agents",
    ".claude",
    ".code-arena",
    ".codex",
    ".git",
    "_local",
    "hooks",
    "specs",
}
MARKDOWN_SUFFIXES = {".md", ".markdown"}
HTML_SUFFIXES = {".html", ".htm", ".xml"}
# Exported text whose whole content is prose-like for the private-reference scan: workflows,
# project and build files, JSON, and shell scripts ship to the public repository exactly as
# Markdown does. Python files are deliberately absent: the checker's own scripts carry the
# private-name patterns as regular-expression literals.
SCANNED_TEXT_SUFFIXES = {
    ".rst",
    ".txt",
    ".yml",
    ".yaml",
    ".csproj",
    ".props",
    ".targets",
    ".json",
    ".sh",
    ".slnx",
    ".config",
}

MARKDOWN_LINK_RE = re.compile(
    r"!?\[[^\]\n]*\]\(\s*(?:<(?P<angle>[^>\n]+)>|(?P<plain>[^\s)\n]+))"
)
MARKDOWN_REFERENCE_RE = re.compile(
    r"^\s*\[[^\]\n]+\]:\s*(?:<(?P<angle>[^>\n]+)>|(?P<plain>\S+))",
    re.MULTILINE,
)
HTML_LINK_RE = re.compile(
    r"\b(?:href|src)\s*=\s*[\"'](?P<target>[^\"']+)[\"']",
    re.IGNORECASE,
)
EXPLICIT_ANCHOR_RE = re.compile(
    r"\b(?:id|name)\s*=\s*[\"'](?P<anchor>[^\"']+)[\"']",
    re.IGNORECASE,
)
MARKDOWN_HEADING_RE = re.compile(r"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$")
FENCE_RE = re.compile(r"^\s{0,3}(`{3,}|~{3,})")
INLINE_CODE_RE = re.compile(r"(`+)(.*?)(\1)")

PRIVATE_REFERENCE_RE = re.compile(
    r"(?<![A-Za-z0-9_.-])(?:"
    r"AGENTS\.md|CLAUDE(?:\.local)?\.md|review-accepted\.yaml|"
    r"(?:\.agents|\.claude|\.code-arena|\.codex|specs|_local|hooks)/"
    r"[A-Za-z0-9_./-]*"
    r")"
)
# An internal spec section number ("§2.4", "pre-§2.10") is a citation into the private specs/ tree.
# Citations into public literature are the allowed exception. The allow-list is applied to the
# matched line only, so a public citation cannot excuse an internal one on the next line.
INTERNAL_SECTION_RE = re.compile(r"§\s*[0-9A-Z]")
PUBLIC_SECTION_CITATION_RE = re.compile(
    r"Raft|Ongaro|Shapiro|Pelkonen|Abadi|Pillai|Hellerstein|Demers|Karger|Knuth|"
    r"et al\.|VLDB|Architecture of a Database System|RFC\s*[0-9]|DDIA|Figure\s*[0-9]|"
    r"Leader Completeness|Log Matching|State Machine Safety|current-term guard|"
    r"wire-format-2\.0\.md"
)
# The shipped wire-format document numbers its own sections, and its fixture tests cite those
# numbers; both are public, so their section references are not citations into specs/.
PUBLIC_SECTION_SELF_REFERENCE_PATHS = {
    "docs/wire-format-2.0.md",
    "tests/SourceGenerator/WireFormatDocFixtureTests.cs",
}
# A concrete public ROADMAP.md link is checked against the export below; only
# an unqualified planning reference lacks a public artifact to follow.
PRIVATE_PLANNING_REFERENCE_RE = re.compile(
    r"\b(?:feature-[a-z0-9-]+\.md|roadmap(?!\.md\b))\b",
    re.IGNORECASE,
)
PRIVATE_DEPENDENCY_PATH_RES = (
    re.compile(r"(?:^|[\s`'(\[])(?:/Users/[^/\s`)'\]]+/|~/Developer/|~/(?:\.claude|\.codex|\.pierre)/)"),
    re.compile(r"(?:\.\./)+Boutquin\.[A-Za-z0-9_.-]+/"),
    re.compile(r"[A-Za-z]:\\Users\\[^\\\s]+\\"),
)


@dataclass(frozen=True, order=True)
class Finding:
    path: str
    line: int
    kind: str
    message: str

    def render(self) -> str:
        location = self.path if self.line == 0 else f"{self.path}:{self.line}"
        return f"{location}: [{self.kind}] {self.message}"


@dataclass(frozen=True)
class Link:
    source: str
    line: int
    target: str


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate a Boutquin.Storage public export without network access."
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument(
        "--revision",
        metavar="REV",
        help="git revision to archive and validate (release use: HEAD)",
    )
    source.add_argument(
        "--export-dir",
        type=Path,
        metavar="PATH",
        help="already-exported directory to validate",
    )
    return parser.parse_args(argv)


def archive_revision(revision: str, destination: Path) -> None:
    """Materialize a git revision using its committed export attributes."""
    archive_path = destination.parent / "public-export.tar"
    try:
        with archive_path.open("wb") as archive_file:
            result = subprocess.run(
                ["git", "archive", "--format=tar", revision],
                check=False,
                stdout=archive_file,
                stderr=subprocess.PIPE,
                text=False,
            )
    except OSError as error:
        raise RuntimeError(f"could not run git archive: {error}") from error

    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"git archive failed for {revision!r}: {detail}")

    with tarfile.open(archive_path, mode="r:") as archive:
        members = archive.getmembers()
        for member in members:
            member_path = PurePosixPath(member.name)
            if member_path.is_absolute() or ".." in member_path.parts:
                raise RuntimeError(f"git archive produced an unsafe path: {member.name}")
            if member.issym():
                resolved_link = posixpath.normpath(
                    posixpath.join(posixpath.dirname(member.name), member.linkname)
                )
                if (
                    PurePosixPath(member.linkname).is_absolute()
                    or resolved_link == ".."
                    or resolved_link.startswith("../")
                ):
                    raise RuntimeError(
                        f"git archive produced an unsafe symbolic link: {member.name}"
                    )

        # Extract explicitly instead of relying on tarfile's version-specific
        # extraction filters. The archive comes from git, and this keeps the gate
        # compatible with the Python versions available on supported CI runners.
        for member in members:
            target = destination.joinpath(*PurePosixPath(member.name).parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            elif member.isfile():
                target.parent.mkdir(parents=True, exist_ok=True)
                source = archive.extractfile(member)
                if source is None:
                    raise RuntimeError(f"could not read archived file: {member.name}")
                with source, target.open("wb") as output:
                    shutil.copyfileobj(source, output)
                target.chmod(member.mode & 0o777)
            elif member.issym():
                target.parent.mkdir(parents=True, exist_ok=True)
                target.symlink_to(member.linkname)
            else:
                raise RuntimeError(f"unsupported entry in git archive: {member.name}")


def relative_files(root: Path) -> list[str]:
    return sorted(
        path.relative_to(root).as_posix()
        for path in root.rglob("*")
        if path.is_file() or path.is_symlink()
    )


def relative_directories(root: Path) -> set[str]:
    return {
        path.relative_to(root).as_posix()
        for path in root.rglob("*")
        if path.is_dir()
    }


def is_private_path(relative_path: str) -> bool:
    parts = PurePosixPath(relative_path).parts
    if not parts:
        return False
    return parts[0] in PRIVATE_ROOT_DIRECTORIES or (
        len(parts) == 1 and parts[0] in PRIVATE_ROOT_FILES
    )


def read_text(path: Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None


def line_number(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def markdown_without_fenced_code(text: str) -> str:
    """Mask fenced Markdown code while preserving line numbers."""
    output: list[str] = []
    fence_character = ""
    fence_length = 0
    for line in text.splitlines(keepends=True):
        fence = FENCE_RE.match(line)
        if fence:
            marker = fence.group(1)
            if not fence_character:
                fence_character = marker[0]
                fence_length = len(marker)
            elif marker[0] == fence_character and len(marker) >= fence_length:
                fence_character = ""
                fence_length = 0
            output.append("\n" if line.endswith("\n") else "")
            continue
        if fence_character:
            output.append("\n" if line.endswith("\n") else "")
            continue
        output.append(line)
    return "".join(output)


def markdown_without_code_blocks(text: str) -> str:
    """Mask fenced and inline Markdown code while preserving line numbers."""
    return INLINE_CODE_RE.sub(
        lambda match: " " * len(match.group(0)),
        markdown_without_fenced_code(text),
    )


def csharp_doc_comments(text: str) -> str:
    """Return XML documentation comments with original line positions preserved."""
    lines: list[str] = []
    in_doc_block = False
    for line in text.splitlines(keepends=True):
        stripped = line.lstrip()
        if stripped.startswith("///"):
            prefix_length = len(line) - len(stripped) + 3
            lines.append(" " * prefix_length + stripped[3:])
            continue
        if stripped.startswith("/**"):
            in_doc_block = True
        if in_doc_block:
            lines.append(line)
            if "*/" in line:
                in_doc_block = False
        else:
            lines.append("\n" if line.endswith("\n") else "")
    return "".join(lines)


def csharp_comment_prose(text: str) -> str:
    """Return the comment text of a C# file, masking code and string-literal contents.

    Every character that is not comment text becomes a space (newlines are kept), so line
    numbers and columns survive. Comments are recognised wherever they start, including after
    code on the same line, but a ``//`` or ``/*`` inside a regular, verbatim, interpolated or
    raw string literal, or inside a character literal, is string content and is not a comment.
    """
    output = [" " if character != "\n" else "\n" for character in text]
    length = len(text)
    index = 0

    def reveal(start: int, end: int) -> None:
        for position in range(start, end):
            if text[position] != "\n":
                output[position] = text[position]

    while index < length:
        character = text[index]
        pair = text[index:index + 2]
        if pair == "//":
            end = text.find("\n", index)
            end = length if end < 0 else end
            reveal(index + 2, end)
            index = end
            continue
        if pair == "/*":
            end = text.find("*/", index + 2)
            end = length if end < 0 else end
            reveal(index + 2, end)
            index = min(length, end + 2)
            continue
        if character == "'":
            # Character literal: '"' and '\'' must not open or close a string.
            close = index + 1
            if close < length and text[close] == "\\":
                close += 2
            else:
                close += 1
            if close < length and text[close] == "'":
                index = close + 1
                continue
            index += 1
            continue
        if character == '"':
            quote_run = 1
            while index + quote_run < length and text[index + quote_run] == '"':
                quote_run += 1
            if quote_run >= 3:
                # Raw string literal: ends at a run of at least as many quotes.
                closing = '"' * quote_run
                end = text.find(closing, index + quote_run)
                index = length if end < 0 else end + quote_run
                continue
            verbatim = False
            back = index - 1
            while back >= 0 and text[back] in "@$":
                verbatim = verbatim or text[back] == "@"
                back -= 1
            index += 1
            while index < length:
                current = text[index]
                if verbatim:
                    if current == '"':
                        if text[index + 1:index + 2] == '"':
                            index += 2
                            continue
                        break
                else:
                    if current == "\\":
                        index += 2
                        continue
                    if current == '"' or current == "\n":
                        break
                index += 1
            index += 1
            continue
        index += 1
    return "".join(output)


def links_in_text(relative_path: str, text: str) -> Iterator[Link]:
    suffix = PurePosixPath(relative_path).suffix.lower()
    if suffix in MARKDOWN_SUFFIXES:
        searchable = markdown_without_code_blocks(text)
        patterns: Iterable[re.Pattern[str]] = (
            MARKDOWN_LINK_RE,
            MARKDOWN_REFERENCE_RE,
            HTML_LINK_RE,
        )
    elif suffix in HTML_SUFFIXES:
        searchable = text
        patterns = (HTML_LINK_RE,)
    elif suffix == ".cs":
        searchable = csharp_doc_comments(text)
        patterns = (HTML_LINK_RE,)
    else:
        return

    seen: set[tuple[int, str]] = set()
    for pattern in patterns:
        for match in pattern.finditer(searchable):
            groups = match.groupdict()
            target = groups.get("angle") or groups.get("plain") or groups.get("target")
            if target is None:
                continue
            target = html.unescape(target.strip())
            identity = (match.start(), target)
            if identity in seen:
                continue
            seen.add(identity)
            yield Link(relative_path, line_number(searchable, match.start()), target)


def github_slug(heading: str) -> str:
    heading = html.unescape(re.sub(r"<[^>]+>", "", heading)).strip().lower()
    heading = re.sub(r"[`*_~]", "", heading)
    heading = "".join(
        character
        for character in heading
        if not unicodedata.category(character).startswith("P") or character in "-_"
    )
    return re.sub(r"\s+", "-", heading).strip("-")


def anchors_for(relative_path: str, text: str) -> set[str]:
    anchors = {
        html.unescape(match.group("anchor"))
        for match in EXPLICIT_ANCHOR_RE.finditer(text)
    }
    if PurePosixPath(relative_path).suffix.lower() not in MARKDOWN_SUFFIXES:
        return anchors

    counts: Counter[str] = Counter()
    searchable = markdown_without_fenced_code(text)
    for line in searchable.splitlines():
        match = MARKDOWN_HEADING_RE.match(line)
        if match is None:
            continue
        base = github_slug(match.group(1))
        if not base:
            continue
        duplicate_number = counts[base]
        counts[base] += 1
        anchors.add(base if duplicate_number == 0 else f"{base}-{duplicate_number}")
    return anchors


def prose_for_scan(relative_path: str, text: str) -> str | None:
    suffix = PurePosixPath(relative_path).suffix.lower()
    if suffix in MARKDOWN_SUFFIXES or suffix in SCANNED_TEXT_SUFFIXES:
        return text
    if suffix == ".cs":
        return csharp_comment_prose(text)
    return None


def private_reference_findings(relative_path: str, text: str) -> Iterator[Finding]:
    prose = prose_for_scan(relative_path, text)
    if prose is None:
        return
    private_matches = list(PRIVATE_REFERENCE_RE.finditer(prose))
    for match in private_matches:
        yield Finding(
            relative_path,
            line_number(prose, match.start()),
            "private-reference",
            f"reference to private artifact: {match.group(0).rstrip('/')}",
        )
    for match in PRIVATE_PLANNING_REFERENCE_RE.finditer(prose):
        if any(
            match.start() < private_match.end()
            and private_match.start() < match.end()
            for private_match in private_matches
        ):
            continue
        yield Finding(
            relative_path,
            line_number(prose, match.start()),
            "private-reference",
            f"reference to private planning artifact: {match.group(0)}",
        )
    prose_lines = prose.split("\n")
    for match in INTERNAL_SECTION_RE.finditer(prose):
        if relative_path in PUBLIC_SECTION_SELF_REFERENCE_PATHS:
            break
        line = line_number(prose, match.start())
        if PUBLIC_SECTION_CITATION_RE.search(prose_lines[line - 1]):
            continue
        yield Finding(
            relative_path,
            line,
            "internal-section-citation",
            "citation of an internal spec section number; state the rationale in prose instead",
        )
    for pattern in PRIVATE_DEPENDENCY_PATH_RES:
        for match in pattern.finditer(prose):
            if any(
                match.start() < private_match.end()
                and private_match.start() < match.end()
                for private_match in private_matches
            ):
                continue
            yield Finding(
                relative_path,
                line_number(prose, match.start()),
                "private-dependency",
                f"hardcoded private dependency path: {match.group(0).strip()}",
            )


def normalized_link_target(source: str, target: str) -> tuple[str, str] | None:
    if not target or target.startswith("//"):
        return None
    parsed = urllib.parse.urlsplit(target)
    if parsed.scheme or parsed.netloc:
        # Package readmes use absolute links because they also render outside GitHub.
        # Resolve this repository's public-main links against the same export so an
        # absolute URL cannot conceal a missing or export-ignored document.
        parts = urllib.parse.unquote(parsed.path).strip("/").split("/")
        if (
            parsed.hostname == "github.com"
            and len(parts) >= 5
            and [part.lower() for part in parts[:2]] == ["boutquin", "boutquin.storage"]
            and parts[2] in {"blob", "tree"}
            and parts[3] == "main"
        ):
            return posixpath.normpath("/".join(parts[4:])), urllib.parse.unquote(parsed.fragment)
        return None

    decoded_path = urllib.parse.unquote(parsed.path)
    fragment = urllib.parse.unquote(parsed.fragment)
    if decoded_path.startswith("/"):
        candidate = decoded_path.lstrip("/")
    elif decoded_path:
        candidate = posixpath.join(posixpath.dirname(source), decoded_path)
    else:
        candidate = source
    return posixpath.normpath(candidate), fragment


def validate_links(
    root: Path,
    files: list[str],
    directories: set[str],
    texts: dict[str, str],
) -> Iterator[Finding]:
    file_set = set(files)
    anchor_cache: dict[str, set[str]] = {}
    for relative_path, text in texts.items():
        for link in links_in_text(relative_path, text):
            normalized = normalized_link_target(link.source, link.target)
            if normalized is None:
                continue
            target_path, fragment = normalized
            if target_path == ".." or target_path.startswith("../"):
                yield Finding(
                    link.source,
                    link.line,
                    "broken-link",
                    f"link escapes export root: {link.target}",
                )
                continue
            if is_private_path(target_path):
                yield Finding(
                    link.source,
                    link.line,
                    "private-reference",
                    f"reference to private artifact: {target_path}",
                )
                continue
            if target_path not in file_set and target_path not in directories:
                yield Finding(
                    link.source,
                    link.line,
                    "broken-link",
                    f"link target does not exist: {target_path}",
                )
                continue
            if not fragment:
                continue

            anchor_target = target_path
            if target_path in directories:
                readme = posixpath.join(target_path, "README.md")
                if readme not in file_set:
                    yield Finding(
                        link.source,
                        link.line,
                        "broken-anchor",
                        f"cannot resolve anchor on directory: {target_path}#{fragment}",
                    )
                    continue
                anchor_target = readme
            target_text = texts.get(anchor_target)
            if target_text is None:
                target_text = read_text(root / anchor_target)
                if target_text is None:
                    yield Finding(
                        link.source,
                        link.line,
                        "broken-anchor",
                        f"cannot read anchor target: {anchor_target}#{fragment}",
                    )
                    continue
            if anchor_target not in anchor_cache:
                anchor_cache[anchor_target] = anchors_for(anchor_target, target_text)
            if fragment not in anchor_cache[anchor_target]:
                yield Finding(
                    link.source,
                    link.line,
                    "broken-anchor",
                    f"anchor does not exist: {anchor_target}#{fragment}",
                )


def validate_export(root: Path) -> list[Finding]:
    files = relative_files(root)
    directories = relative_directories(root)
    findings: list[Finding] = []
    private_files = {path for path in files if is_private_path(path)}
    findings.extend(
        Finding(path, 0, "private-artifact", f"private artifact exported: {path}")
        for path in sorted(private_files)
    )

    texts: dict[str, str] = {}
    for relative_path in files:
        if relative_path in private_files:
            continue
        text = read_text(root / relative_path)
        if text is None:
            continue
        texts[relative_path] = text
        findings.extend(private_reference_findings(relative_path, text))

    findings.extend(validate_links(root, files, directories, texts))
    return sorted(set(findings))


def run(argv: list[str]) -> int:
    args = parse_args(argv)
    try:
        if args.export_dir is not None:
            root = args.export_dir.resolve()
            if not root.is_dir():
                raise RuntimeError(f"export directory does not exist: {root}")
            findings = validate_export(root)
            source_description = str(root)
        else:
            with tempfile.TemporaryDirectory(prefix="boutquin-storage-public-export-") as directory:
                root = Path(directory) / "export"
                root.mkdir()
                archive_revision(args.revision, root)
                findings = validate_export(root)
            source_description = f"git archive {args.revision}"
    except RuntimeError as error:
        print(f"public export check could not run: {error}", file=sys.stderr)
        return 2

    if findings:
        print(f"public export check failed for {source_description}: {len(findings)} finding(s)")
        for finding in findings:
            print(finding.render())
        return 1

    print(f"public export check passed for {source_description}")
    return 0


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
