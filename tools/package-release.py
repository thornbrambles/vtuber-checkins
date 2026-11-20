#!/usr/bin/env python3
"""Build versioned install ZIP from clean repository contents only.

Usage: python tools/package-release.py v5.0.0
"""
import pathlib
import re
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
ALLOWED = ["src/actions", "overlay", "docs", "config", "tools"]
EXTRA = ["README.md", "CONTRIBUTING.md"]


def build(version):
    if not re.fullmatch(r"v\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?", version):
        raise ValueError("Version must look like v5.0.0")
    output = ROOT / "dist" / f"vtuber-checkins-{version}.zip"
    output.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for directory in ALLOWED:
            for path in sorted((ROOT / directory).rglob("*")):
                if path.is_file() and path.suffix not in {".pyc", ".pyo"} and "__pycache__" not in path.parts:
                    archive.write(path, path.relative_to(ROOT).as_posix())
        for item in EXTRA:
            archive.write(ROOT / item, item)
    with zipfile.ZipFile(output) as archive:
        broken = archive.testzip()
        if broken:
            raise IOError("Invalid release archive member: " + broken)
    print(output)
    return output


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("Usage: python tools/package-release.py vX.Y.Z")
    try:
        build(sys.argv[1])
    except (ValueError, IOError) as exc:
        sys.exit(str(exc))
