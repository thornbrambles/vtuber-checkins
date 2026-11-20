#!/usr/bin/env python3
"""Compile each standalone CPHInline C# action independently against minimal test stubs.

Requires dotnet SDK 8.0 and NuGet network access for Newtonsoft.Json.
This catches C# syntax/type errors but does NOT simulate Streamer.bot runtime.
"""
import pathlib
import shutil
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "actions"
PROJECT = ROOT / "tests" / "csharp" / "action-compile.csproj"
STUBS = ROOT / "tests" / "csharp" / "Stubs.cs"


def main():
    if not shutil.which("dotnet"):
        print("dotnet SDK not installed; cannot run C# compile smoke test.", file=sys.stderr)
        return 2
    errors = []
    with tempfile.TemporaryDirectory(prefix="checkin-csharp-") as folder:
        base = pathlib.Path(folder)
        (base / "action-compile.csproj").write_bytes(PROJECT.read_bytes())
        (base / "Stubs.cs").write_bytes(STUBS.read_bytes())
        for action in sorted(SRC.glob("*.cs")):
            (base / "Action.cs").write_bytes(action.read_bytes())
            proc = subprocess.run(
                ["dotnet", "build", "action-compile.csproj", "--nologo", "-v:q", "-p:RestoreIgnoreFailedSources=false"],
                cwd=base, capture_output=True, text=True
            )
            if proc.returncode:
                print(f"FAIL: {action.name}\n{proc.stdout}\n{proc.stderr}", file=sys.stderr)
                errors.append(action.name)
            else:
                print("PASS:", action.name)
    if errors:
        print("C# compile failures:", ", ".join(errors), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
