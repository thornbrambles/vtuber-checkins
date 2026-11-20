#!/usr/bin/env python3
"""Execute actual C# action bodies against fault-injecting test doubles on Windows.

Requires a .NET SDK and .NET Framework 4.8.1. Does not replace live API testing.
"""
import os
import pathlib
import shutil
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def main():
    if os.name != "nt":
        raise SystemExit("Run the Framework runtime regression suite on Windows.")
    with tempfile.TemporaryDirectory(prefix="checkin-runtime-") as folder:
        base = pathlib.Path(folder)
        project = (ROOT / "tests/csharp/action-compile.csproj").read_text(encoding="utf-8")
        project = project.replace("<OutputType>Library</OutputType>", "<OutputType>Exe</OutputType>")
        project = project.replace('<Compile Include="Action.cs" />', '<Compile Include="Actions.cs" />\n    <Compile Include="RuntimeTests.cs" />')
        (base / "runtime.csproj").write_text(project, encoding="utf-8")
        for name in ("Stubs.cs", "RuntimeTests.cs"):
            shutil.copyfile(ROOT / "tests/csharp" / name, base / name)
        actions = []
        for path in sorted((ROOT / "src/actions").glob("*.cs")):
            source = path.read_text(encoding="utf-8").replace("public class CPHInline", "public class Action" + path.name[:2])
            actions.append("namespace RuntimeActions {\n" + source + "\n}")
        (base / "Actions.cs").write_text("\n".join(actions), encoding="utf-8")
        runner = base / "RuntimeTests.cs"
        runner.write_text("using RuntimeActions;\n" + runner.read_text(encoding="utf-8"), encoding="utf-8")
        subprocess.run(["dotnet", "build", "runtime.csproj", "--nologo", "-v:q"], cwd=base, check=True)
        subprocess.run([str(base / "bin/Debug/net481/runtime.exe")], cwd=base, check=True)


if __name__ == "__main__":
    main()
