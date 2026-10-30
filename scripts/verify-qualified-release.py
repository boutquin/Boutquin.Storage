#!/usr/bin/env python3
"""Verify the immutable Storage 2.0.1 archives before uploading them.

Copyright (c) 2026 Pierre G. Boutquin. Licensed under the Apache License, Version 2.0.
"""
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile

SOURCE = "b25d820553b9b6ba956838a691fe53b624a2bb3e"
REPOSITORY = "https://github.com/boutquin/Boutquin.Storage"
ARCHIVES = {
    "Boutquin.Storage.Domain.2.0.1.nupkg": "0cbe58efad7409a7f85b1a489c985155afd4e9bd3e76585c9b31f72a76a9fb36",
    "Boutquin.Storage.Domain.2.0.1.snupkg": "d644c5c12705e4c9b9b64565c754c3ed0c88645393b3673a913fdf9b78b62214",
    "Boutquin.Storage.Infrastructure.2.0.1.nupkg": "840cf4245f77a3be21251c71722dec3d748b37030a5eb92d70570fa8ce0e626c",
    "Boutquin.Storage.Infrastructure.2.0.1.snupkg": "5cca37207dc5e2f320b577977ab33766d0c6ee7bd66c8e6f16caccfb812c7039",
    "Boutquin.Storage.SourceGenerator.2.0.1.nupkg": "3060dce17b65b691bd027797c37ca15461f29a9230ecf2547ebd38414bb1c1c4",
}


def verify(directory: Path, commit: str) -> dict:
    if commit != SOURCE:
        raise ValueError("Release tag does not name the qualified source")
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Release directory must be a regular directory")
    if {path.name for path in directory.iterdir()} != set(ARCHIVES):
        raise ValueError("Release archive inventory differs")
    for name, expected in ARCHIVES.items():
        path = directory / name
        if path.is_symlink() or not path.is_file():
            raise ValueError("Release archive is not a regular file")
        if hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise ValueError("Qualified archive bytes changed: " + name)
        with zipfile.ZipFile(path) as archive:
            package_id = name.split(".2.0.1.")[0]
            metadata = ET.fromstring(archive.read(package_id + ".nuspec"))
            values = {node.tag.rsplit("}", 1)[-1]: node for node in metadata.iter()}
            if values["id"].text != package_id or values["version"].text != "2.0.1":
                raise ValueError("Package identity differs")
            repository = values["repository"].attrib
            if repository.get("commit") != SOURCE or repository.get("url") != REPOSITORY or repository.get("type") != "git":
                raise ValueError("Package source differs")
    return {"status": "PASS_EXACT_QUALIFIED_ARCHIVES", "source": SOURCE, "sha256": ARCHIVES}


if __name__ == "__main__":
    print(json.dumps(verify(Path(sys.argv[1]), sys.argv[2]), indent=2))
