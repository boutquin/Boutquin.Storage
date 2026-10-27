#!/usr/bin/env python3
"""Verify acknowledged I/O histories after killing an undisposed production host.

Copyright (c) 2026 Pierre G. Boutquin. Licensed under the Apache License, Version 2.0.
The evidence concerns process death, not controller power loss or physical disk failure.
"""
import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tests/IoCrashProbe/Boutquin.Storage.IoCrashProbe.csproj"
DLL = PROJECT.parent / "bin/Debug/net10.0/Boutquin.Storage.IoCrashProbe.dll"
DOTNET_ENV = dict(os.environ, DOTNET_PROCESSOR_COUNT="2")


def run_case(family, boundary, output):
    with tempfile.TemporaryDirectory(prefix="storage-crash-") as folder:
        process = subprocess.Popen(["dotnet", str(DLL), "write", family, folder, boundary],
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=DOTNET_ENV)
        lines = queue.Queue()
        captured = []

        def consume():
            for line in process.stdout:
                lines.put(line.rstrip())
            lines.put(None)

        reader = threading.Thread(target=consume, daemon=True)
        reader.start()
        try:
            while True:
                line = lines.get(timeout=90)
                if line is None:
                    raise RuntimeError("Writer exited before ACK: " + "\n".join(captured))
                captured.append(line)
                if line == "ACK":
                    break
            process.kill()
            process.wait(timeout=15)
            if process.returncode == 0:
                raise RuntimeError("Writer exited normally instead of being killed")
            pointers = Path(folder) / "pointers.json"
            if family == "value-log":
                pointers.write_text(next(line for line in captured if line.startswith("[")))
            result = subprocess.run(["dotnet", str(DLL), "read", family, folder, str(pointers)],
                                    text=True, capture_output=True, timeout=90, env=DOTNET_ENV)
            text = "\n".join(captured) + f"\nwriter killed exit={process.returncode}\n" + result.stdout + result.stderr
            (output / f"{family}-{boundary}.log").write_text(text)
            if result.returncode:
                raise RuntimeError(text)
            return {"family": family, "boundary": boundary, "killed_exit": process.returncode,
                    "recovery_exit": result.returncode, "result": "intentional_difference" if family == "segmented" else "pass"}
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=15)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    if not args.no_build:
        subprocess.run(["dotnet", "build", str(PROJECT), "-m:1", "-p:GeneratePackageOnBuild=false"],
                       cwd=ROOT, env=DOTNET_ENV, check=True)
    cases = [(f"lsm-{strategy}-{values}", boundary)
             for strategy in range(3) for values in ("inline", "separated")
             for boundary in ("wal", "manifest")]
    cases += [(family, "ack") for family in ("wal", "value-log", "append", "indexed", "segmented")]
    results = []
    for family, boundary in cases:
        result = run_case(family, boundary, args.output)
        results.append(result)
        print(json.dumps(result), flush=True)
    (args.output / "results.json").write_text(json.dumps(results, indent=2) + "\n")
    print(f"Completed {len(results)} killed-process histories")


if __name__ == "__main__":
    main()
