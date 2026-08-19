#!/bin/bash
# Build Parsek + Parsek.Tests and run the full xUnit suite on Linux (cloud
# agent VMs, or any Linux box with dotnet-sdk-8.0 + mono + the ksp-refs clone).
#
# Why not `dotnet test`: Ubuntu's apt-built .NET SDK omits testhost.net472.exe,
# so net472 test execution goes through mono + the xunit console runner instead.
# Test filtering uses xunit console syntax, e.g.:
#   scripts/cloud-test.sh -class Parsek.Tests.RewindLoggingTests
#   scripts/cloud-test.sh -method "*.InjectAllRecordings"
# Extra args are passed straight to xunit.console.exe.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
KSPDIR="${KSPDIR:-/workspace/ksp-refs}"
export KSPDIR

if [ ! -f "$KSPDIR/KSP_x64_Data/Managed/Assembly-CSharp.dll" ]; then
  echo "ERROR: KSP reference DLLs not found at KSPDIR=$KSPDIR" >&2
  echo "Clone the private repo first (attach vl3c/ksp-refs to the session):" >&2
  echo "  git clone --depth 1 https://github.com/vl3c/ksp-refs $KSPDIR" >&2
  exit 2
fi

echo "== Restoring + building (KSPDIR=$KSPDIR)"
dotnet build "$REPO_ROOT/Source/Parsek.Tests/Parsek.Tests.csproj" -p:SkipKspDeploy=true -v:q -nologo

OUT="$REPO_ROOT/Source/Parsek.Tests/bin/Debug/net472"

# The Tests csproj copies only 4 referenced DLLs (CoreModule, IMGUIModule,
# Assembly-CSharp, 0Harmony). Mono resolves field types eagerly where .NET
# Framework is lazy, so types like ParsekFlight need the remaining KSP-install
# DLLs (ToolbarControl, ClickThroughBlocker, other UnityEngine modules) present
# at runtime too. Copy the full reference set into the test output.
cp -n "$KSPDIR/KSP_x64_Data/Managed/"*.dll "$OUT/" 2>/dev/null || true
find "$KSPDIR/GameData" -name "*.dll" -exec cp -n {} "$OUT/" \; 2>/dev/null || true

# `|| true`: when the package dir doesn't exist yet, find exits non-zero and
# set -e -o pipefail would abort here, skipping the fetch fallback below.
RUNNER="$(find "$HOME/.nuget/packages/xunit.runner.console" -name xunit.console.exe -path "*net472*" 2>/dev/null | head -1 || true)"
if [ -z "$RUNNER" ]; then
  echo "xunit console runner not cached; fetching..."
  TMP="$(mktemp -d)"
  ( cd "$TMP" && dotnet new classlib -o prefetch --framework netstandard2.0 >/dev/null \
    && cd prefetch && dotnet add package xunit.runner.console --version 2.4.2 >/dev/null )
  RUNNER="$(find "$HOME/.nuget/packages/xunit.runner.console" -name xunit.console.exe -path "*net472*" | head -1 || true)"
fi
if [ -z "$RUNNER" ]; then
  echo "ERROR: xunit.runner.console 2.4.2 (net472) could not be located or fetched" >&2
  exit 2
fi

echo "== Running tests under mono ($RUNNER)"
# -parallel none: the suite has [Collection("Sequential")] classes sharing
# static state; serial execution keeps mono runs deterministic (~30s total).
cd "$OUT"
exec mono "$RUNNER" Parsek.Tests.dll -parallel none "$@"
