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

"""Verify the three-package Boutquin.Storage release family and a cold consumer."""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.parse
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Sequence
from xml.etree import ElementTree
from xml.sax.saxutils import escape


PACKAGE_IDS = (
    "Boutquin.Storage.Domain",
    "Boutquin.Storage.Infrastructure",
    "Boutquin.Storage.SourceGenerator",
)
PUBLIC_REPOSITORY_URL = "https://github.com/boutquin/Boutquin.Storage"
PUBLIC_RELEASE_NOTES = (
    "Release notes: https://github.com/boutquin/Boutquin.Storage/blob/main/CHANGELOG.md"
)
PACKAGE_DLLS = {
    "Boutquin.Storage.Domain": (
        "lib/net10.0/Boutquin.Storage.Domain.dll",
    ),
    "Boutquin.Storage.Infrastructure": (
        "lib/net10.0/Boutquin.Storage.Infrastructure.dll",
    ),
    "Boutquin.Storage.SourceGenerator": (
        "analyzers/dotnet/cs/Boutquin.Storage.SourceGenerator.dll",
        "analyzers/dotnet/cs/Boutquin.Storage.SourceGenerator.CodeFixes.dll",
    ),
}
TEMPLATE_DIRECTORY = Path(__file__).resolve().parent / "package-consumer"
IDENTITY_MARKER = "PACKAGE_IDENTITY:"
CONSUMER_SUCCESS_MARKER = "PACKAGE_CONSUMER_OK"


class VerificationError(RuntimeError):
    """Raised when a package or consumer release invariant does not hold."""


@dataclass(frozen=True)
class PackageMetadata:
    path: Path
    package_id: str
    version: str
    repository_url: str
    repository_commit: str
    dependencies: tuple[tuple[str, str], ...]
    project_url: str = ""
    license_type: str = ""
    license_expression: str = ""
    readme: str = ""
    release_notes: str = ""


@dataclass(frozen=True)
class AssemblyIdentity:
    path: Path
    name: str
    assembly_version: str
    file_version: str
    informational_version: str


@dataclass(frozen=True)
class ArchiveVerification:
    packages: dict[str, PackageMetadata]
    identities: tuple[AssemblyIdentity, ...]


IdentityInspector = Callable[[Path], AssemblyIdentity]


def _required_text(parent: ElementTree.Element, name: str, source: Path) -> str:
    child = parent.find(f"{{*}}{name}")
    if child is None or child.text is None or not child.text.strip():
        raise VerificationError(f"{source.name}: nuspec metadata is missing {name}")
    return child.text.strip()


def _read_package_metadata(package_path: Path) -> tuple[PackageMetadata, tuple[str, ...]]:
    try:
        with zipfile.ZipFile(package_path) as archive:
            nuspec_names = tuple(
                name for name in archive.namelist() if name.lower().endswith(".nuspec")
            )
            if len(nuspec_names) != 1:
                raise VerificationError(
                    f"{package_path.name}: expected exactly one nuspec, found {len(nuspec_names)}"
                )
            try:
                root = ElementTree.fromstring(archive.read(nuspec_names[0]))
            except ElementTree.ParseError as error:
                raise VerificationError(
                    f"{package_path.name}: invalid nuspec XML: {error}"
                ) from error
            archive_names = tuple(archive.namelist())
    except (OSError, zipfile.BadZipFile) as error:
        raise VerificationError(f"{package_path.name}: unreadable nupkg: {error}") from error

    metadata = root.find("{*}metadata")
    if metadata is None:
        raise VerificationError(f"{package_path.name}: nuspec is missing metadata")

    repository = metadata.find("{*}repository")
    if repository is None:
        raise VerificationError(f"{package_path.name}: nuspec is missing repository metadata")
    repository_url = repository.get("url", "").strip()
    repository_commit = repository.get("commit", "").strip()
    repository_type = repository.get("type", "").strip()
    if repository_type != "git":
        raise VerificationError(
            f"{package_path.name}: repository type must be git, found {repository_type!r}"
        )

    dependencies = tuple(
        (dependency.get("id", "").strip(), dependency.get("version", "").strip())
        for dependency in metadata.findall(".//{*}dependency")
    )
    license_element = metadata.find("{*}license")
    if license_element is None:
        raise VerificationError(f"{package_path.name}: nuspec is missing license metadata")
    return (
        PackageMetadata(
            path=package_path,
            package_id=_required_text(metadata, "id", package_path),
            version=_required_text(metadata, "version", package_path),
            repository_url=repository_url,
            repository_commit=repository_commit,
            dependencies=dependencies,
            project_url=_required_text(metadata, "projectUrl", package_path),
            license_type=license_element.get("type", "").strip(),
            license_expression=(license_element.text or "").strip(),
            readme=_required_text(metadata, "readme", package_path),
            release_notes=_required_text(metadata, "releaseNotes", package_path),
        ),
        archive_names,
    )


def _validate_dependency_contract(metadata: PackageMetadata, expected_version: str) -> None:
    required_dependency: tuple[str, str] | None = None
    if metadata.package_id == "Boutquin.Storage.Domain":
        required_dependency = ("Boutquin.Domain", "[1.0.1]")
    elif metadata.package_id == "Boutquin.Storage.Infrastructure":
        required_dependency = ("Boutquin.Storage.Domain", f"[{expected_version}]")

    for dependency_id, version_range in metadata.dependencies:
        if (
            dependency_id.startswith("Boutquin.")
            and "-" in version_range
            and (dependency_id, version_range) != required_dependency
        ):
            raise VerificationError(
                f"{metadata.package_id}: private prerelease dependency "
                f"{dependency_id} {version_range} is forbidden"
            )

    if required_dependency is None:
        return
    dependency_id, required_range = required_dependency
    actual_ranges = [
        version_range
        for candidate_id, version_range in metadata.dependencies
        if candidate_id == dependency_id
    ]
    if actual_ranges != [required_range]:
        raise VerificationError(
            f"{metadata.package_id}: dependency {dependency_id} must occur once at exact "
            f"range {required_range}; found {actual_ranges or 'none'}"
        )


def _version_components(version: str) -> tuple[int, int, int]:
    match = re.fullmatch(
        r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
        r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
        r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?",
        version,
    )
    if match is None:
        raise VerificationError(f"version must be valid SemVer, found {version!r}")
    return tuple(int(part) for part in match.groups())


def _expected_assembly_version(version: str) -> str:
    major, _, _ = _version_components(version)
    return f"{major}.0.0.0"


def _expected_file_version(version: str) -> str:
    major, minor, patch = _version_components(version)
    return f"{major}.{minor}.{patch}.0"


def _validate_identity(
    identity: AssemblyIdentity,
    expected_version: str,
    expected_commit: str,
) -> None:
    expected_name = identity.path.stem
    if identity.name != expected_name:
        raise VerificationError(
            f"{identity.path.name}: assembly name must be {expected_name}, found {identity.name}"
        )

    expected_assembly_version = _expected_assembly_version(expected_version)
    if identity.assembly_version != expected_assembly_version:
        raise VerificationError(
            f"{identity.path.name}: assembly version must be {expected_assembly_version}, "
            f"found {identity.assembly_version}"
        )

    expected_file_version = _expected_file_version(expected_version)
    if identity.file_version != expected_file_version:
        raise VerificationError(
            f"{identity.path.name}: file version must be {expected_file_version}, "
            f"found {identity.file_version}"
        )

    expected_informational_version = f"{expected_version}+{expected_commit}"
    if identity.informational_version != expected_informational_version:
        raise VerificationError(
            f"{identity.path.name}: informational version must be "
            f"{expected_informational_version}, found {identity.informational_version}"
        )


def _validate_packaged_readme(package_path: Path, name: str) -> None:
    with zipfile.ZipFile(package_path) as archive:
        content = archive.read(name).decode("utf-8")
    targets = re.findall(r"\]\(\s*<?([^>\s)]+)", content)
    targets += re.findall(r"\b(?:href|src)=[\"']([^\"']+)", content)
    for target in targets:
        target = html.unescape(target)
        if not target.startswith("#") and urllib.parse.urlsplit(target).scheme not in {
            "https", "http", "mailto"
        }:
            raise VerificationError(
                f"{package_path.name}: packaged README contains a relative link: {target}"
            )


def _extract_payload(
    package_path: Path,
    archive_path: str,
    destination: Path,
) -> Path:
    destination_path = destination / Path(archive_path).name
    try:
        with zipfile.ZipFile(package_path) as archive:
            destination_path.write_bytes(archive.read(archive_path))
    except KeyError as error:
        raise VerificationError(
            f"{package_path.name}: required payload is missing: {archive_path}"
        ) from error
    return destination_path


def verify_package_archives(
    package_directory: Path,
    expected_version: str,
    expected_commit: str,
    *,
    dotnet: str = "dotnet",
    identity_inspector: IdentityInspector | None = None,
) -> ArchiveVerification:
    """Validate package archives and every shipped managed DLL identity."""

    package_directory = package_directory.resolve()
    if not package_directory.is_dir():
        raise VerificationError(f"package directory does not exist: {package_directory}")
    if not re.fullmatch(r"[0-9a-fA-F]{40}", expected_commit):
        raise VerificationError("commit must be a full 40-character hexadecimal SHA")
    _expected_assembly_version(expected_version)

    package_paths = tuple(
        sorted(
            path
            for path in package_directory.glob("*.nupkg")
            if not path.name.endswith(".snupkg")
        )
    )
    parsed: dict[str, tuple[PackageMetadata, tuple[str, ...]]] = {}
    for package_path in package_paths:
        metadata, archive_names = _read_package_metadata(package_path)
        if metadata.package_id in parsed:
            raise VerificationError(
                f"duplicate package id {metadata.package_id}: "
                f"{parsed[metadata.package_id][0].path.name}, {package_path.name}"
            )
        parsed[metadata.package_id] = (metadata, archive_names)

    expected_ids = set(PACKAGE_IDS)
    actual_ids = set(parsed)
    if actual_ids != expected_ids or len(package_paths) != len(PACKAGE_IDS):
        missing = sorted(expected_ids - actual_ids)
        unexpected = sorted(actual_ids - expected_ids)
        details = []
        if missing:
            details.append(f"missing: {', '.join(missing)}")
        if unexpected:
            details.append(f"unexpected: {', '.join(unexpected)}")
        if len(package_paths) != len(PACKAGE_IDS):
            details.append(f"normal nupkg count: {len(package_paths)} (expected 3)")
        raise VerificationError("package family mismatch (" + "; ".join(details) + ")")

    for package_id in PACKAGE_IDS:
        metadata, archive_names = parsed[package_id]
        if metadata.version != expected_version:
            raise VerificationError(
                f"{metadata.path.name}: nuspec version must be {expected_version}, "
                f"found {metadata.version}"
            )
        if metadata.repository_url != PUBLIC_REPOSITORY_URL:
            raise VerificationError(
                f"{metadata.path.name}: repository URL must be {PUBLIC_REPOSITORY_URL}, "
                f"found {metadata.repository_url or 'none'}"
            )
        if metadata.repository_commit.lower() != expected_commit.lower():
            raise VerificationError(
                f"{metadata.path.name}: repository commit must be {expected_commit}, "
                f"found {metadata.repository_commit or 'none'}"
            )
        if metadata.project_url != PUBLIC_REPOSITORY_URL:
            raise VerificationError(
                f"{metadata.path.name}: project URL must be {PUBLIC_REPOSITORY_URL}, "
                f"found {metadata.project_url}"
            )
        if metadata.license_type != "expression" or metadata.license_expression != "Apache-2.0":
            raise VerificationError(
                f"{metadata.path.name}: license must be expression Apache-2.0, found "
                f"{metadata.license_type} {metadata.license_expression}"
            )
        if metadata.readme != "README.md" or "README.md" not in archive_names:
            raise VerificationError(
                f"{metadata.path.name}: README.md must be declared and included"
            )
        _validate_packaged_readme(metadata.path, metadata.readme)
        if metadata.release_notes != PUBLIC_RELEASE_NOTES:
            raise VerificationError(
                f"{metadata.path.name}: release notes must be {PUBLIC_RELEASE_NOTES!r}, "
                f"found {metadata.release_notes!r}"
            )
        _validate_dependency_contract(metadata, expected_version)
        for required_payload in PACKAGE_DLLS[package_id]:
            if required_payload not in archive_names:
                raise VerificationError(
                    f"{metadata.path.name}: required payload is missing: {required_payload}"
                )

    with tempfile.TemporaryDirectory(prefix="boutquin-storage-identities-") as temporary:
        extraction_directory = Path(temporary)
        extracted = []
        for package_id in PACKAGE_IDS:
            metadata, _ = parsed[package_id]
            for archive_path in PACKAGE_DLLS[package_id]:
                extracted.append(
                    _extract_payload(metadata.path, archive_path, extraction_directory)
                )

        if identity_inspector is None:
            identities = _inspect_assembly_identities(extracted, dotnet)
        else:
            identities = tuple(identity_inspector(path) for path in extracted)
        if len(identities) != len(extracted):
            raise VerificationError(
                f"assembly inspector returned {len(identities)} identities for "
                f"{len(extracted)} DLLs"
            )
        for identity in identities:
            _validate_identity(identity, expected_version, expected_commit)

    return ArchiveVerification(
        packages={package_id: parsed[package_id][0] for package_id in PACKAGE_IDS},
        identities=tuple(identities),
    )


def _write_template(
    template_name: str,
    destination: Path,
    replacements: dict[str, str] | None = None,
) -> None:
    template_path = TEMPLATE_DIRECTORY / template_name
    if not template_path.is_file():
        raise VerificationError(f"package verifier template is missing: {template_path}")
    content = template_path.read_text(encoding="utf-8")
    for marker, value in (replacements or {}).items():
        content = content.replace(marker, value)
    destination.write_text(content, encoding="utf-8")


def _dotnet_environment(root: Path) -> dict[str, str]:
    environment = os.environ.copy()
    environment.update(
        {
            "DOTNET_CLI_HOME": str(root / "dotnet-home"),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
            "NUGET_PACKAGES": str(root / "packages-cache"),
        }
    )
    return environment


def _run(
    command: Sequence[str],
    *,
    cwd: Path,
    environment: dict[str, str],
    purpose: str,
) -> subprocess.CompletedProcess[str]:
    try:
        completed = subprocess.run(
            command,
            cwd=cwd,
            env=environment,
            check=False,
            capture_output=True,
            text=True,
        )
    except OSError as error:
        raise VerificationError(f"could not run {purpose}: {error}") from error
    if completed.returncode != 0:
        output = "\n".join(
            part.strip() for part in (completed.stdout, completed.stderr) if part.strip()
        )
        raise VerificationError(
            f"{purpose} failed with exit code {completed.returncode}\n{output}"
        )
    return completed


def _create_build_isolation_files(root: Path) -> None:
    for filename in (
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
    ):
        (root / filename).write_text("<Project />\n", encoding="utf-8")


def _inspect_assembly_identities(
    assembly_paths: Sequence[Path],
    dotnet: str,
) -> tuple[AssemblyIdentity, ...]:
    assembly_paths = tuple(path.resolve() for path in assembly_paths)
    temp_parent = "/private/tmp" if Path("/private/tmp").is_dir() else None
    with tempfile.TemporaryDirectory(
        prefix="boutquin-storage-inspector-", dir=temp_parent
    ) as temporary:
        root = Path(temporary)
        _create_build_isolation_files(root)
        helper = root / "inspector"
        helper.mkdir()
        _write_template("AssemblyInspector.csproj.template", helper / "AssemblyInspector.csproj")
        _write_template("AssemblyInspector.cs.template", helper / "Program.cs")
        environment = _dotnet_environment(root)
        command = [
            dotnet,
            "run",
            "--project",
            str(helper / "AssemblyInspector.csproj"),
            "--configuration",
            "Release",
            "--verbosity",
            "quiet",
            "--",
            *(str(path) for path in assembly_paths),
        ]
        completed = _run(
            command,
            cwd=helper,
            environment=environment,
            purpose="managed assembly identity inspection",
        )

    identities = []
    for line in completed.stdout.splitlines():
        if not line.startswith(IDENTITY_MARKER):
            continue
        try:
            payload = json.loads(line[len(IDENTITY_MARKER) :])
            identities.append(
                AssemblyIdentity(
                    path=Path(payload["path"]),
                    name=payload["name"],
                    assembly_version=payload["assemblyVersion"],
                    file_version=payload["fileVersion"],
                    informational_version=payload["informationalVersion"],
                )
            )
        except (KeyError, TypeError, json.JSONDecodeError) as error:
            raise VerificationError(f"invalid assembly inspector output: {line}") from error
    if len(identities) != len(assembly_paths):
        raise VerificationError(
            f"assembly inspector emitted {len(identities)} identities for "
            f"{len(assembly_paths)} DLLs"
        )
    return tuple(identities)


def run_cold_consumer(package_directory: Path, version: str, dotnet: str) -> str:
    """Restore, build, and execute the package consumer in a fresh external directory."""

    temp_parent = "/private/tmp" if Path("/private/tmp").is_dir() else None
    with tempfile.TemporaryDirectory(
        prefix="boutquin-storage-consumer-", dir=temp_parent
    ) as temporary:
        root = Path(temporary)
        _create_build_isolation_files(root)
        consumer = root / "consumer"
        consumer.mkdir()
        replacements = {"@@VERSION@@": version}
        _write_template(
            "PackageConsumer.csproj.template",
            consumer / "PackageConsumer.csproj",
            replacements,
        )
        _write_template("Program.cs.template", consumer / "Program.cs")
        _write_template(
            "NuGet.Config.template",
            consumer / "NuGet.Config",
            {"@@LOCAL_FEED@@": escape(str(package_directory.resolve()))},
        )
        environment = _dotnet_environment(root)
        restore = [
            dotnet,
            "restore",
            str(consumer / "PackageConsumer.csproj"),
            "--configfile",
            str(consumer / "NuGet.Config"),
            "--packages",
            str(root / "packages-cache"),
            "--no-cache",
            "--force-evaluate",
            "--verbosity",
            "minimal",
        ]
        _run(
            restore,
            cwd=consumer,
            environment=environment,
            purpose="fresh-cache package consumer restore",
        )
        completed = _run(
            [
                dotnet,
                "run",
                "--project",
                str(consumer / "PackageConsumer.csproj"),
                "--configuration",
                "Release",
                "--no-restore",
                "--verbosity",
                "minimal",
            ],
            cwd=consumer,
            environment=environment,
            purpose="package consumer build and execution",
        )
        if CONSUMER_SUCCESS_MARKER not in completed.stdout:
            raise VerificationError(
                "package consumer exited successfully without its success marker"
            )
        return completed.stdout.strip()


def _resolve_dotnet(dotnet: str) -> str:
    if os.sep in dotnet:
        candidate = Path(dotnet).expanduser().resolve()
        if not candidate.is_file():
            raise VerificationError(f"dotnet executable does not exist: {candidate}")
        return str(candidate)
    resolved = shutil.which(dotnet)
    if resolved is None:
        raise VerificationError(f"dotnet executable was not found on PATH: {dotnet}")
    return resolved


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Validate the Boutquin.Storage Domain, Infrastructure, and SourceGenerator "
            "release packages, then run a cold external consumer."
        )
    )
    parser.add_argument("--packages", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    arguments = _parser().parse_args(argv)
    try:
        dotnet = _resolve_dotnet(arguments.dotnet)
        archives = verify_package_archives(
            arguments.packages,
            arguments.version,
            arguments.commit,
            dotnet=dotnet,
        )
        print("Package metadata and payloads verified:")
        for package_id in PACKAGE_IDS:
            metadata = archives.packages[package_id]
            print(
                f"  {metadata.package_id} {metadata.version} "
                f"repository={metadata.repository_url}@{metadata.repository_commit}"
            )
        print("Packaged managed assembly identities:")
        for identity in archives.identities:
            print(
                f"  {identity.name}: assembly={identity.assembly_version} "
                f"file={identity.file_version} informational={identity.informational_version}"
            )
        consumer_output = run_cold_consumer(
            arguments.packages,
            arguments.version,
            dotnet,
        )
        print(consumer_output)
        print("PACKAGE_VERIFICATION_OK")
        return 0
    except VerificationError as error:
        print(f"PACKAGE_VERIFICATION_FAILED: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
