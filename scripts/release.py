"""Parsek release packaging script.

Builds Release, runs tests, and packages the mod zip for distribution.

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


def read_version():
    version_file = ROOT / "GameData" / "Parsek" / "Parsek.version"
    if not version_file.exists():
        sys.exit(f"ERROR: {version_file} not found")
    data = json.loads(version_file.read_text())
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
        "GameData/Parsek/Textures/parsek_24.png": ROOT / "img" / "parsek logo - 24.png",
        "GameData/Parsek/Textures/parsek_38.png": ROOT / "img" / "parsek logo - 38.png",
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
    run(["dotnet", "test", "Source/Parsek.Tests/Parsek.Tests.csproj"], "Running Tests")
    package(version)

    print(f"\n=== Done ===")


if __name__ == "__main__":
    main()
