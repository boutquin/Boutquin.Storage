# Boutquin.Storage 2.0.1

Storage engines, serialization and data processing components for .NET 10, inspired by
*Designing Data-Intensive Applications*.

| Package | Purpose |
| --- | --- |
| `Boutquin.Storage.Domain` | Storage interfaces, value types and serialization helpers |
| `Boutquin.Storage.Infrastructure` | LSM engines, WAL recovery, SSTables, columnar time-series storage, indexes and processing components |
| `Boutquin.Storage.SourceGenerator` | Generated tagged serialization, ordered keys, columnar access and compiler diagnostics |

## Install

Install matching versions for an application that uses generated serializers:

```sh
dotnet add package Boutquin.Storage.Domain --version 2.0.1
dotnet add package Boutquin.Storage.Infrastructure --version 2.0.1
dotnet add package Boutquin.Storage.SourceGenerator --version 2.0.1
```

The runtime targets .NET 10. The generator needs a Roslyn 5.9 or newer compiler host;
.NET SDK 10.0.403 satisfies this requirement. The external `Boutquin.Domain` dependency
is pinned to 1.0.1.

## Documentation

- [Quick start and examples](https://github.com/boutquin/Boutquin.Storage/blob/main/README.md)
- [Source generator types and diagnostics](https://github.com/boutquin/Boutquin.Storage/blob/main/docs/source-generator-type-reference.md)
- [Migration from 1.x](https://github.com/boutquin/Boutquin.Storage/blob/main/docs/migration-1x-to-2x.md)
- [Release notes](https://github.com/boutquin/Boutquin.Storage/blob/main/CHANGELOG.md)

Migration requires exporting and re-ingesting existing 1.x data; 2.0.0 does not read the old
persisted formats and its detection of them is best-effort, so mark 1.x data directories with an
out-of-band marker (see the migration guide). The `Boutquin.Storage.Distributed` reference assembly
is available in the source repository and is not a NuGet package.

Licensed under [Apache 2.0](https://github.com/boutquin/Boutquin.Storage/blob/main/LICENSE.txt).
