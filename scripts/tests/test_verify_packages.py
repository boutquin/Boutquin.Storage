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

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "verify-packages.py"
SPEC = importlib.util.spec_from_file_location("verify_packages", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
verify_packages = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = verify_packages
SPEC.loader.exec_module(verify_packages)

VERSION = "2.0.0"
COMMIT = "0123456789abcdef0123456789abcdef01234567"
REPOSITORY_URL = "https://github.com/boutquin/Boutquin.Storage"
RELEASE_NOTES = (
    "Release notes: https://github.com/boutquin/Boutquin.Storage/blob/main/CHANGELOG.md"
)


def nuspec(
    package_id: str,
    dependencies: tuple[tuple[str, str], ...] = (),
    release_notes: str = RELEASE_NOTES,
) -> str:
    dependency_xml = "".join(
        f'<dependency id="{dependency_id}" version="{version}" />'
        for dependency_id, version in dependencies
    )
    return f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{package_id}</id>
    <version>{VERSION}</version>
    <license type="expression">Apache-2.0</license>
    <readme>README.md</readme>
    <projectUrl>{REPOSITORY_URL}</projectUrl>
    <releaseNotes>{release_notes}</releaseNotes>
    <repository type="git" url="{REPOSITORY_URL}" commit="{COMMIT}" />
    <dependencies><group targetFramework="net10.0">{dependency_xml}</group></dependencies>
  </metadata>
</package>
"""


def write_package(
    directory: Path,
    package_id: str,
    *,
    dependencies: tuple[tuple[str, str], ...] = (),
    release_notes: str = RELEASE_NOTES,
    readme: str = "# Package fixture\n",
) -> Path:
    package_path = directory / f"{package_id}.{VERSION}.nupkg"
    payloads = {
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
    with zipfile.ZipFile(package_path, "w") as archive:
        archive.writestr(
            f"{package_id}.nuspec",
            nuspec(package_id, dependencies, release_notes),
        )
        archive.writestr("README.md", readme)
        for payload in payloads[package_id]:
            archive.writestr(payload, b"fixture-dll")
    return package_path


def good_identity(path: Path) -> verify_packages.AssemblyIdentity:
    return verify_packages.AssemblyIdentity(
        path=path,
        name=path.stem,
        assembly_version="2.0.0.0",
        file_version="2.0.0.0",
        informational_version=f"{VERSION}+{COMMIT}",
    )


class VerifyPackagesTests(unittest.TestCase):
    def make_complete_feed(self, directory: Path) -> None:
        write_package(
            directory,
            "Boutquin.Storage.Domain",
            dependencies=(("Boutquin.Domain", "[1.0.0]"),),
        )
        write_package(
            directory,
            "Boutquin.Storage.Infrastructure",
            dependencies=(("Boutquin.Storage.Domain", "[2.0.0]"),),
        )
        write_package(directory, "Boutquin.Storage.SourceGenerator")

    def test_packaged_readme_requires_portable_links(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            feed = Path(temporary_directory)
            self.make_complete_feed(feed)
            write_package(
                feed,
                "Boutquin.Storage.SourceGenerator",
                readme="See [the guide](docs/guide.md).\n",
            )
            with self.assertRaisesRegex(verify_packages.VerificationError, "relative link"):
                verify_packages.verify_package_archives(
                    feed, VERSION, COMMIT, identity_inspector=good_identity
                )

    def test_complete_package_family_passes_archive_and_identity_checks(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            feed = Path(temporary_directory)
            self.make_complete_feed(feed)

            result = verify_packages.verify_package_archives(
                feed,
                VERSION,
                COMMIT,
                identity_inspector=good_identity,
            )

            self.assertEqual(
                set(result.packages),
                {
                    "Boutquin.Storage.Domain",
                    "Boutquin.Storage.Infrastructure",
                    "Boutquin.Storage.SourceGenerator",
                },
            )
            self.assertEqual(len(result.identities), 4)

    def test_prerelease_candidate_keeps_stable_managed_version_components(self) -> None:
        candidate = "2.0.0-ci.12345.2"

        self.assertEqual(
            verify_packages._expected_assembly_version(candidate),
            "2.0.0.0",
        )
        self.assertEqual(
            verify_packages._expected_file_version(candidate),
            "2.0.0.0",
        )

    def test_infrastructure_may_pin_the_exact_same_family_prerelease_candidate(self) -> None:
        candidate = "2.0.0-ci.12345.2"
        metadata = verify_packages.PackageMetadata(
            path=Path("Boutquin.Storage.Infrastructure.2.0.0-ci.12345.2.nupkg"),
            package_id="Boutquin.Storage.Infrastructure",
            version=candidate,
            repository_url=REPOSITORY_URL,
            repository_commit=COMMIT,
            dependencies=(("Boutquin.Storage.Domain", f"[{candidate}]"),),
        )

        verify_packages._validate_dependency_contract(metadata, candidate)

    def test_missing_source_generator_package_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            feed = Path(temporary_directory)
            write_package(
                feed,
                "Boutquin.Storage.Domain",
                dependencies=(("Boutquin.Domain", "[1.0.0]"),),
            )
            write_package(
                feed,
                "Boutquin.Storage.Infrastructure",
                dependencies=(("Boutquin.Storage.Domain", "[2.0.0]"),),
            )

            with self.assertRaisesRegex(
                verify_packages.VerificationError,
                "Boutquin.Storage.SourceGenerator",
            ):
                verify_packages.verify_package_archives(
                    feed,
                    VERSION,
                    COMMIT,
                    identity_inspector=good_identity,
                )

    def test_local_only_release_notes_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            feed = Path(temporary_directory)
            write_package(
                feed,
                "Boutquin.Storage.Domain",
                dependencies=(("Boutquin.Domain", "[1.0.0]"),),
                release_notes="See CHANGELOG.md for release notes.",
            )
            write_package(
                feed,
                "Boutquin.Storage.Infrastructure",
                dependencies=(("Boutquin.Storage.Domain", "[2.0.0]"),),
            )
            write_package(feed, "Boutquin.Storage.SourceGenerator")

            with self.assertRaisesRegex(
                verify_packages.VerificationError,
                "release notes must be",
            ):
                verify_packages.verify_package_archives(
                    feed,
                    VERSION,
                    COMMIT,
                    identity_inspector=good_identity,
                )

    def test_dll_identity_mismatching_nuspec_version_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            feed = Path(temporary_directory)
            self.make_complete_feed(feed)

            def stale_identity(path: Path) -> verify_packages.AssemblyIdentity:
                identity = good_identity(path)
                if path.name == "Boutquin.Storage.Infrastructure.dll":
                    return verify_packages.AssemblyIdentity(
                        path=path,
                        name=identity.name,
                        assembly_version="1.0.0.0",
                        file_version="1.9.9.0",
                        informational_version=f"1.9.9+{COMMIT}",
                    )
                return identity

            with self.assertRaisesRegex(
                verify_packages.VerificationError,
                "Boutquin.Storage.Infrastructure.dll.*assembly version",
            ):
                verify_packages.verify_package_archives(
                    feed,
                    VERSION,
                    COMMIT,
                    identity_inspector=stale_identity,
                )

    def test_storage_dependency_ranges_must_be_exact(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            feed = Path(temporary_directory)
            write_package(
                feed,
                "Boutquin.Storage.Domain",
                dependencies=(("Boutquin.Domain", "1.0.0"),),
            )
            write_package(
                feed,
                "Boutquin.Storage.Infrastructure",
                dependencies=(("Boutquin.Storage.Domain", "[2.0.0]"),),
            )
            write_package(feed, "Boutquin.Storage.SourceGenerator")

            with self.assertRaisesRegex(
                verify_packages.VerificationError,
                r"Boutquin\.Domain.*\[1\.0\.0\]",
            ):
                verify_packages.verify_package_archives(
                    feed,
                    VERSION,
                    COMMIT,
                    identity_inspector=good_identity,
                )

    def test_private_prerelease_dependency_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            feed = Path(temporary_directory)
            write_package(
                feed,
                "Boutquin.Storage.Domain",
                dependencies=(
                    ("Boutquin.Domain", "[1.0.0]"),
                    ("Boutquin.Experimental", "[3.0.0-beta.1]"),
                ),
            )
            write_package(
                feed,
                "Boutquin.Storage.Infrastructure",
                dependencies=(("Boutquin.Storage.Domain", "[2.0.0]"),),
            )
            write_package(feed, "Boutquin.Storage.SourceGenerator")

            with self.assertRaisesRegex(
                verify_packages.VerificationError,
                "private prerelease dependency Boutquin.Experimental",
            ):
                verify_packages.verify_package_archives(
                    feed,
                    VERSION,
                    COMMIT,
                    identity_inspector=good_identity,
                )


if __name__ == "__main__":
    unittest.main()
