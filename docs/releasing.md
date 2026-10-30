# Release validation

Run this checklist from a clean checkout of the public
[`boutquin/Boutquin.Storage`](https://github.com/boutquin/Boutquin.Storage) repository after the
release candidate has been exported there. It prepares the three public packages from one commit:

- `Boutquin.Storage.Domain`
- `Boutquin.Storage.Infrastructure`
- `Boutquin.Storage.SourceGenerator`

The external `Boutquin.Domain` dependency is pinned to 1.0.1. Public release validation
requires that exact version to be available from the public feeds configured in `nuget.config`;
a private preparation feed does not qualify public package delivery. No sibling checkout is required.

## Prepare a clean release build

Use the repository SDK (10.0.403) and one version override for every build and pack operation.
Build without incremental outputs before packing so package metadata and assembly identities cannot
come from different builds.

```sh
dotnet --version  # 10.0.403
PACKAGE_VERSION=2.0.1
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

## Publish

Package validation does not publish anything. After the verified commit is on the public
`boutquin/Boutquin.Storage` `main` branch, create the `v2.0.1` tag from that exact public commit and
push the tag through the normal maintainer release process. Package SourceLink and repository
commit URLs must resolve to this public commit. The tag-triggered publish workflow is the
publication boundary; confirm all three packages on NuGet before announcing the release.
