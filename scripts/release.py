"""Parsek release packaging script.

Builds Release, runs the tests in Debug and Release, and packages the mod zip for distribution.

Usage: python scripts/release.py
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent


# Version checkers (KSP-AVC, CKAN's remote-avc) read the URL field. It names the .version file
# uploaded to the LATEST GitHub release, not the one on main: main is bumped before a release
# exists, and a checker reading main would announce a version nobody can download yet.
EXPECTED_VERSION_URL = "https://github.com/vl3c/Parsek/releases/latest/download/Parsek.version"
VERSION_FILE = ROOT / "GameData" / "Parsek" / "Parsek.version"


def read_version():
    version_file = VERSION_FILE
    if not version_file.exists():
        sys.exit(f"ERROR: {version_file} not found")
    data = json.loads(version_file.read_text())
    if data.get("URL") != EXPECTED_VERSION_URL:
        sys.exit(f"ERROR: {version_file} URL is {data.get('URL')!r}, expected {EXPECTED_VERSION_URL!r}")
    v = data["VERSION"]
    return f"{v['MAJOR']}.{v['MINOR']}.{v['PATCH']}"


def check_assembly_version(version):
    asm_info = ROOT / "Source" / "Parsek" / "Properties" / "AssemblyInfo.cs"
    text = asm_info.read_text()
    match = re.search(r'AssemblyVersion\("([^"]+)"\)', text)
    if not match:
        sys.exit("ERROR: AssemblyVersion not found in AssemblyInfo.cs")
    asm_version = match.group(1)
    expected = f"{version}.0"
    if asm_version != expected:
        sys.exit(
            f"ERROR: Version mismatch\n"
            f"  Parsek.version:  {version}\n"
            f"  AssemblyInfo:    {asm_version} (expected {expected})\n"
            f"\nUpdate AssemblyInfo.cs first."
        )


def run(args, label):
    print(f"--- {label} ---")
    result = subprocess.run(args, cwd=ROOT)
    if result.returncode != 0:
        sys.exit(f"ERROR: {label} failed (exit code {result.returncode})")
    print()


def find_dll():
    candidates = [
        ROOT / "Source" / "Parsek" / "bin" / "Release" / "net472" / "Parsek.dll",
        ROOT / "Source" / "Parsek" / "bin" / "Release" / "Parsek.dll",
    ]
    for p in candidates:
        if p.exists():
            return p
    sys.exit("ERROR: Parsek.dll not found in Release output")


def package(version):
    dll = find_dll()
    zip_name = f"Parsek-v{version}.zip"
    out = ROOT / zip_name

    print(f"--- Packaging {zip_name} ---")

    files = {
        "GameData/Parsek/Plugins/Parsek.dll": dll,
        "GameData/Parsek/Parsek.version": ROOT / "GameData" / "Parsek" / "Parsek.version",
        "GameData/Parsek/LICENSE.txt": ROOT / "LICENSE",
        "GameData/Parsek/Textures/parsek_32.png": ROOT / "img" / "parsek logo - 32.png",
        "GameData/Parsek/Textures/parsek_64.png": ROOT / "img" / "parsek logo - 64.png",
    }

    for label, src in files.items():
        if not src.exists():
            sys.exit(f"ERROR: Missing {src}")

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        for arc_name, src in files.items():
            zf.write(src, arc_name)

    print(f"\nOutput: {out}")
    print(f"\nContents:")
    with zipfile.ZipFile(out, "r") as zf:
        for info in zf.infolist():
            print(f"  {info.file_size:>8}  {info.filename}")

    print(f"\nSize: {out.stat().st_size:,} bytes")


def main():
    version = read_version()
    print(f"=== Parsek Release v{version} ===\n")

    check_assembly_version(version)
    run(["dotnet", "build", "Source/Parsek/Parsek.csproj", "-c", "Release"], "Building Release")
    run(["dotnet", "test", "Source/Parsek.Tests/Parsek.Tests.csproj"], "Running Tests (Debug)")
    # Release too: it is the configuration the zip ships, and its JIT inlining is what pulls a
    # Unity ECall back into a tested method when an isolating helper lacks NoInlining.
    run(["dotnet", "test", "Source/Parsek.Tests/Parsek.Tests.csproj", "-c", "Release"],
        "Running Tests (Release)")
    package(version)

    # The release carries TWO assets: the zip, and the version file on its own, which the URL
    # field above resolves to. Upload both, e.g.:
    #   gh release create v<version> Parsek-v<version>.zip GameData/Parsek/Parsek.version ...
    print(f"\nRelease assets to upload (both):")
    print(f"  Parsek-v{version}.zip")
    print(f"  {VERSION_FILE.relative_to(ROOT).as_posix()}")
    print(f"\n=== Done ===")


if __name__ == "__main__":
    main()
