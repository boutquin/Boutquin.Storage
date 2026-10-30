# Release validation

Use the build steps below to prepare a new, unpublished candidate from a clean checkout of the public
[`boutquin/Boutquin.Storage`](https://github.com/boutquin/Boutquin.Storage) repository after the
release candidate has been exported there. They prepare the three public packages from one commit:

- `Boutquin.Storage.Domain`
- `Boutquin.Storage.Infrastructure`
- `Boutquin.Storage.SourceGenerator`

The qualified 2.0.1 archives are already frozen. Deliver those exact bytes using the
[publication procedure](#publish-the-qualified-201-archives); do not rebuild or repack that identity.

The external `Boutquin.Domain` dependency is pinned to 1.0.1. Public release validation
requires that exact version to be available from the public feeds configured in `nuget.config`;
a private preparation feed does not qualify public package delivery. No sibling checkout is required.

## Prepare a clean release build

Use the repository SDK (10.0.403) and one version override for every build and pack operation.
Build without incremental outputs before packing so package metadata and assembly identities cannot
come from different builds.

```sh
dotnet --version  # 10.0.403
: "${PACKAGE_VERSION:?Set a new unpublished candidate version}"
RELEASE_COMMIT="$(git rev-parse HEAD)"

dotnet restore Boutquin.Storage.slnx --configfile nuget.config
dotnet build Boutquin.Storage.slnx -c Release --no-restore --no-incremental \
  -p:MinVerVersionOverride="$PACKAGE_VERSION"
dotnet test Boutquin.Storage.slnx -c Release --no-build
dotnet format Boutquin.Storage.slnx --verify-no-changes --no-restore
```

Pack all three deliverables from that build with the same override into an empty
`artifacts/packages` directory:

```sh
dotnet pack src/Domain/Boutquin.Storage.Domain.csproj -c Release --no-build --no-restore \
  -p:MinVerVersionOverride="$PACKAGE_VERSION" -o artifacts/packages
dotnet pack src/Infrastructure/Boutquin.Storage.Infrastructure.csproj -c Release --no-build --no-restore \
  -p:MinVerVersionOverride="$PACKAGE_VERSION" -o artifacts/packages
dotnet pack src/SourceGenerator/Boutquin.Storage.SourceGenerator.csproj -c Release --no-build --no-restore \
  -p:MinVerVersionOverride="$PACKAGE_VERSION" -o artifacts/packages
```

## Validate the public artifact

Run the export audit, its regression tests, and package inspection from the same commit:

```sh
python3 scripts/check-public-export.py --revision HEAD
python3 -m unittest discover -s scripts/tests -v
python3 scripts/verify-packages.py \
  --packages artifacts/packages \
  --version "$PACKAGE_VERSION" \
  --commit "$RELEASE_COMMIT"
```

The export audit scans every exported text file for private references (Markdown, workflows, project and
build files, JSON, shell scripts, and all C# comments including trailing ones) and for citations of internal
spec section numbers; citations into public literature and the shipped wire-format document are allowed.
It exits non-zero on any finding, so resolve findings before tagging.

The package verifier checks the package set, versions, repository commit metadata, dependency
ranges, assembly identities, analyzer layout, and public package contents. Treat a skipped or
missing check as a failed release gate.

## Publish the qualified 2.0.1 archives

Package validation does not publish anything. Complete the release gates and obtain the required
publication authorization before performing these steps.

1. Verify the accepted five-archive manifest. Disable the existing `Publish to NuGet` workflow
   (`publish.yml`) and confirm its disabled state before creating any tag: that workflow rebuilds
   packages and must not replace the qualified 2.0.1 bytes.
2. Make the qualified source commit `b25d820553b9b6ba956838a691fe53b624a2bb3e` publicly reachable
   through the repository's normal protected-branch/PR process. Retain that exact commit for the
   `v2.0.1` tag. The separate manual-uploader integration commit is delivery tooling, not the source
   of the package DLLs or PDBs; do not tag the tooling commit as the package source.
3. Attach the original three `.nupkg` and two `.snupkg` archives to the GitHub release. Download
   them into a new directory and verify the original hashes and peeled source tag:

   ```sh
   python3 scripts/verify-qualified-release.py release-assets \
     "$(git rev-parse 'refs/tags/v2.0.1^{commit}')"
   ```

4. Dispatch `Publish qualified Storage 2.0.1` (`publish-qualified.yml`) once from public `main`.
   It checks the fixed archive hashes and source identity, then uploads the original files without
   building or packing. Preserve the upload receipts. A partial failure requires reconciliation
   against the registry before any further upload attempt; do not retry blindly or rebuild.
5. Disable the manual uploader after completion and keep the rebuilding publisher disabled.
   Independently verify all three NuGet signatures and original payloads, all four managed DLL
   identities, both symbol-server PDBs, and public SourceLink URLs at the qualified source commit.
   Run an empty-cache NuGet-only consumer and locked replay before announcing the release.

NuGet's repository signature changes the downloaded archive bytes. Compare signed downloads with
the unsigned originals by payload entry and retain separate hashes for each form. Existing candidate
archives and the GitHub release assets must remain byte-for-byte unchanged.
