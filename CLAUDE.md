# Boutquin.Storage

## Project Overview

C# .NET 10 library implementing storage algorithms from Kleppmann's *Designing Data-Intensive Applications*. Clean architecture with Domain (interfaces) and Infrastructure (implementations) layers.

## Build & Test

```bash
dotnet build                    # Build all projects
dotnet test                     # Run all 702 tests (660 infrastructure + 42 generator)
dotnet test --verbosity normal  # Verbose test output
dotnet format --verify-no-changes  # Verify code formatting
```

## Key Conventions

- **TreatWarningsAsErrors** enabled — zero warnings allowed
- **ConfigureAwait(false)** on all awaits in library code (src/), **ConfigureAwait(true)** in tests
- **Nullable reference types** enabled with strict warnings
- **"Why" comments** — explain constraints and design decisions, not what the code does
- **MinVer** for versioning — versions come from git tags (`v0.x.y`), not .csproj
- **Apache 2.0** license with copyright headers on all source files
- **TDD workflow** — write failing test first, then implement fix, then verify green
- **Sorted input validation** — SSTable rejects unsorted input with `ArgumentException` (fail-fast, not silent sort)
- **fsync for durability** — WriteAheadLog uses `Flush(flushToDisk: true)`, not `Flush()`
- **Explicit endianness** — on-disk formats use `BinaryPrimitives` with little-endian, not `BinaryWriter`

## Source Generator

- `src/SourceGenerator/` — Roslyn `IIncrementalGenerator` (netstandard2.0) emitting serialization, comparison, and equality for `[Key]` and `[StorageSerializable]` record structs
- Generator discovers attributes by FQN string (`ForAttributeWithMetadataName`) — no project reference to Domain (avoids TFM mismatch)
- Pipeline types (`TypeToGenerate`, `PropertyInfo`, etc.) are plain value types — no Roslyn types allowed past the transform step (required for incremental caching)
- `TreatWarningsAsErrors=false` in generator .csproj — netstandard2.0 Polyfill types generate CS1591 warnings we can't control
- Generator is referenced as analyzer: `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`
- `[Conditional("BOUTQUIN_STORAGE_GENERATOR")]` on attributes — they vanish from compiled output
- Record structs auto-synthesize `Equals`/`==`/`!=`/`IEquatable<T>` — generator detects `IsRecordStruct` and skips these
- Diagnostic rules BSSG001–BSSG006 in `DiagnosticDescriptors.cs`; `StorageDiagnosticSuppressor` suppresses CA1036/S1210

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md) for interface hierarchy, LSM engine composition, data flow diagrams, and component navigation.

Key AI-relevant notes:
- Interface hierarchy splits on serialization (IComparable vs ISerializable) and bulk operations axes
- `ILsmStorageEngine` extends `IBulkStorageEngine` (not `IBulkKeyValueStore`) — usable anywhere `IStorageEngine` is expected
- `IBulkKeyValueStore` uses loose `IComparable` constraints for in-memory data structures; file-backed engines add `ISerializable` via `IBulkStorageEngine`

## Type Patterns

- `SerializableWrapper<T>` — Generic wrapper implementing `ISerializable<T>` and `IComparable`, used as TKey/TValue in tests
- Record structs for value objects (`FileLocation`, `SsTableMetadata`)
- `SemaphoreSlim` for async concurrency in `LsmStorageEngine` (non-reentrant — extract internal methods to avoid deadlock)
- `ObjectDisposedException.ThrowIf(_disposed, this)` on all public methods of disposable types
- `Interlocked.Exchange` for thread-safe dispose detection in `WriteAheadLog`
- `Guard.AgainstNullOrDefault` for null key/value validation at API boundaries
- `NotSupportedException` for `RemoveAsync` in RedBlackTree (append-only semantics — deletes are tombstones)

## CI/CD

- `pr-verify.yml` — Build, test, coverage, format check on PRs to main
- `publish.yml` — NuGet publish on `v*` tag push, with MinVer tag verification
- NuGet API key stored as GitHub secret `NUGET_API_KEY`
- Pre-commit hook enforces `dotnet format --verify-no-changes`
