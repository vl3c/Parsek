#!/bin/bash
# SessionStart hook: provision cloud (Claude Code on the web) sessions so agents
# can compile Parsek and run the xUnit suite on the Linux VM.
#
# Local sessions: exits immediately (everything below is cloud-only).
# Cloud sessions: installs dotnet-sdk-8.0 + mono (apt; cached in the environment
# snapshot after the first session), clones the private vl3c/ksp-refs repo with
# the KSP/Unity/mod reference DLLs when it is attached to the session, and
# exports KSPDIR for the csprojs' reference resolution.
#
# Run tests with: scripts/cloud-test.sh   (NOT `dotnet test` - Ubuntu's apt-built
# SDK lacks testhost.net472.exe, so tests run via mono + xunit console runner.)
set -uo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

KSP_REFS_DIR="${KSP_REFS_DIR:-/workspace/ksp-refs}"

# --- Toolchain (idempotent; baked into the environment cache after first run) ---
if ! command -v dotnet >/dev/null 2>&1 || ! command -v mono >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  sudo apt-get update -qq || true
  sudo apt-get install -y -qq dotnet-sdk-8.0 mono-devel || true
fi

# --- KSP reference DLLs (private repo; must be attached to the session) ---
# If the clone fails (repo not attached), don't fail the session: the agent can
# attach vl3c/ksp-refs via add_repo and clone it later. See .claude/CLAUDE.md.
if [ ! -f "$KSP_REFS_DIR/KSP_x64_Data/Managed/Assembly-CSharp.dll" ]; then
  git clone --depth 1 https://github.com/vl3c/ksp-refs "$KSP_REFS_DIR" 2>/dev/null \
    || echo "[session-start] ksp-refs not cloned (repo not attached?); agent must add_repo vl3c/ksp-refs + clone before building" >&2
fi

# --- Session environment ---
if [ -f "$KSP_REFS_DIR/KSP_x64_Data/Managed/Assembly-CSharp.dll" ]; then
  echo "export KSPDIR=\"$KSP_REFS_DIR\"" >> "$CLAUDE_ENV_FILE"
fi

# Prefetch the xunit console runner so cloud-test.sh works offline-ish and the
# package lands in the environment cache.
if [ ! -d "$HOME/.nuget/packages/xunit.runner.console/2.4.2" ]; then
  ( cd "$(mktemp -d)" \
    && dotnet new classlib -o prefetch --framework netstandard2.0 >/dev/null 2>&1 \
    && cd prefetch \
    && dotnet add package xunit.runner.console --version 2.4.2 >/dev/null 2>&1 ) || true
fi

exit 0
