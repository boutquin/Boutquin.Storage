#!/usr/bin/env zsh
# Run all Boutquin.Storage benchmarks in Release mode.
# BenchmarkDotNet requires Release builds for valid results.
#
# Usage: ./run-benchmarks.sh
# Results: benchmarks/StorageEngine/BenchmarkDotNet.Artifacts/results/
#          benchmarks/Hashing/BenchmarkDotNet.Artifacts/results/

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"

echo "=== Building solution in Release mode ==="
dotnet build "$REPO_ROOT" -c Release --verbosity quiet

echo ""
echo "=== Running StorageEngine benchmarks (12 classes) ==="
echo "This will take a while — BenchmarkDotNet runs multiple iterations per benchmark."
echo ""
dotnet run --project "$REPO_ROOT/benchmarks/StorageEngine" -c Release --no-build

echo ""
echo "=== Running Hashing benchmarks ==="
echo ""
dotnet run --project "$REPO_ROOT/benchmarks/Hashing" -c Release --no-build

echo ""
echo "=== Done ==="
echo "StorageEngine results: benchmarks/StorageEngine/BenchmarkDotNet.Artifacts/results/"
echo "Hashing results:       benchmarks/Hashing/BenchmarkDotNet.Artifacts/results/"
