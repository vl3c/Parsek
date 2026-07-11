#!/usr/bin/env python3
"""M-A6 automation-stack provisioner.

Provisions a byte-for-byte reproducible KSP automation instance for the mission
harness: a pinned kRPC + built TestingTools shim + MechJeb2 + KRPC.MechJeb
stack, a cloned KSP instance, the Parsek DLL under test deployed via a hashed
staging copy, and a manifest the harness reads to REFUSE running against a
drifted instance.

Entry point:
    python harness/provision/provision.py --profile stock-minimal [--repair] [--dry-run]

Behavior phases (design docs/dev/design-autotest-stack-setup.md), each a
function, each idempotent and logged:
    PREFLIGHT -> PIN -> DOWNLOAD -> BUILD-TT -> PAIR -> CLONE -> SETTINGS
      -> DEPLOY -> INSTALL -> MM-CACHE -> MANIFEST -> VERIFY

--dry-run prints the full action plan and computes drift where the instance
already exists, but performs NO network access, downloads, builds, clones,
junction creation, or writes outside harness/provision/. All pure decisions
live in provlib.py so they are unit-tested without side effects.

stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys
import tomllib
from dataclasses import dataclass, field
from typing import Dict, List, Optional

import provlib

# ---------------------------------------------------------------------------
# Paths + context
# ---------------------------------------------------------------------------

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))          # harness/provision
WORKTREE_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
PINS_PATH = os.path.join(SCRIPT_DIR, "pins.toml")
PROFILES_DIR = os.path.join(SCRIPT_DIR, "profiles")
CACHE_DIR = os.path.join(SCRIPT_DIR, ".cache")
STAGE_DIR = os.path.join(SCRIPT_DIR, ".stage")

# Distinctive Parsek UTF-16 signature strings for the DLL-identity grep
# (.claude/CLAUDE.md recipe). Pinned so the check survives ordinary builds; the
# counts are recorded (not asserted equal to a fixed number) so a rename shows
# as drift rather than a spurious abort. A missing signature (count 0) fails.
PARSEK_SIGNATURE_STRINGS = ("ParsekFlight", "GhostPlaybackEngine")


@dataclass
class ProvisionContext:
    profile_name: str
    pins: Dict
    profile: Dict
    umbrella_root: str
    dry_run: bool
    repair: bool
    parsek_dll_override: Optional[str]
    log_lines: List[str] = field(default_factory=list)
    aborted: bool = False
    abort_reason: str = ""

    @property
    def dev_install(self) -> str:
        return os.path.join(self.umbrella_root, self.profile.get("baseInstall", "Kerbal Space Program"))

    @property
    def instance_dir(self) -> str:
        return os.path.join(self.umbrella_root, self.profile.get("instanceDir", "automation/instance"))

    @property
    def parsek_gamedata(self) -> str:
        return os.path.join(self.instance_dir, "GameData", "Parsek")

    def log_path(self) -> str:
        return os.path.join(self.parsek_gamedata, "provision-log.txt")


# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------


def log(ctx: ProvisionContext, level: str, step: str, message: str) -> None:
    """Emit one provisioning-log line to stdout and (live runs, once the
    instance GameData/Parsek dir exists) append it to provision-log.txt."""
    line = provlib.format_log_line(level, step, message)
    ctx.log_lines.append(line)
    print(line)
    if not ctx.dry_run:
        try:
            os.makedirs(ctx.parsek_gamedata, exist_ok=True)
            with open(ctx.log_path(), "a", encoding="utf-8") as fh:
                fh.write(line + "\n")
        except OSError:
            pass  # never let logging failure mask the real work


def abort(ctx: ProvisionContext, step: str, ec: str, detail: str) -> None:
    ctx.aborted = True
    ctx.abort_reason = "%s %s" % (ec, detail)
    log(ctx, "Error", step, "%s %s" % (ec, detail))


# ---------------------------------------------------------------------------
# Small I/O helpers (skipped entirely under --dry-run by their callers)
# ---------------------------------------------------------------------------


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def git_peel_commit(clone_dir: str, tag: str) -> str:
    """Return the commit sha an (possibly annotated) tag peels to, read-only.

    Uses ``git rev-parse <tag>^{commit}`` so an ANNOTATED tag (kRPC v0.5.4)
    resolves to the underlying commit, not the tag object (GT-1). Never checks
    the clone out to a different ref.
    """
    out = subprocess.run(
        ["git", "-C", clone_dir, "rev-parse", "%s^{commit}" % tag],
        capture_output=True, text=True,
    )
    return (out.stdout or "").strip()


# ---------------------------------------------------------------------------
# Phase functions
# ---------------------------------------------------------------------------


def phase_preflight(ctx: ProvisionContext) -> None:
    log(ctx, "Info", "Preflight", "umbrella-root=%s" % ctx.umbrella_root)
    log(ctx, "Info", "Preflight", "dev-install=%s" % ctx.dev_install)
    log(ctx, "Info", "Preflight", "instance-dir=%s" % ctx.instance_dir)
    log(ctx, "Info", "Preflight", "mode=%s repair=%s" % ("dry-run" if ctx.dry_run else "live", ctx.repair))

    if not os.path.isdir(ctx.dev_install):
        abort(ctx, "Preflight", "EC-PRE", "dev install missing at %s" % ctx.dev_install)
        return
    if not os.path.isfile(PINS_PATH):
        abort(ctx, "Preflight", "EC-PRE", "pins.toml missing at %s" % PINS_PATH)
        return

    # Path-length guard (EC-7).
    if provlib.is_path_too_long(ctx.instance_dir):
        abort(ctx, "Preflight", "EC-7", "instance dir path too long: %s" % ctx.instance_dir)
        return

    # Lock decision (EC-10) -- pure logic; live acquisition writes the lockfile.
    lock_path = os.path.join(ctx.parsek_gamedata, ".provision.lock")
    existing = None
    if os.path.isfile(lock_path):
        try:
            with open(lock_path, "r", encoding="utf-8") as fh:
                existing = json.load(fh)
        except (OSError, ValueError):
            existing = None
    decision = provlib.acquire_lock(existing, os.getpid(), _now(), _pid_alive)
    log(ctx, "Info", "Preflight", "lock decision=%s holder=%s" % (decision.reason, decision.holder_pid))
    if not decision.acquired:
        abort(ctx, "Preflight", "EC-10", "instance locked by live pid %s" % decision.holder_pid)
        return
    if not ctx.dry_run:
        os.makedirs(ctx.parsek_gamedata, exist_ok=True)
        with open(lock_path, "w", encoding="utf-8") as fh:
            json.dump({"pid": os.getpid(), "timestamp": _now()}, fh)

    # KSP-running check (EC-1). Best-effort: on a live run, refuse if the
    # instance exe is held open. Under dry-run we only log the intent.
    if not ctx.dry_run and _ksp_running_against(ctx.instance_dir):
        abort(ctx, "Preflight", "EC-1", "close KSP for instance %s" % ctx.profile_name)
        return
    log(ctx, "Info", "Preflight", "profile loaded name=%s devSourcedMods=%d"
        % (ctx.profile_name, len(ctx.profile.get("devSourcedMods", []) or [])))


def phase_pin(ctx: ProvisionContext) -> Dict[str, str]:
    """Resolve git-pinned components read-only and assert against pins.toml."""
    resolved: Dict[str, str] = {}
    for comp in ("krpc", "krpc_mechjeb"):
        pin = ctx.pins.get(comp, {})
        clone = os.path.join(ctx.umbrella_root, pin.get("localClone", ""))
        tag = pin.get("tag", "")
        expected = pin.get("commit", "")
        if ctx.dry_run and not os.path.isdir(os.path.join(clone, ".git")):
            # Dry-run without the clone present: report the intended assertion.
            log(ctx, "Info", "Pin", "%s tag=%s expected-commit=%s (clone not read in dry-run)"
                % (comp, tag, expected))
            resolved[comp] = expected
            continue
        peeled = git_peel_commit(clone, tag)
        res = provlib.resolve_pin(peeled, expected)
        resolved[comp] = res.resolved or expected
        level = "Info" if res.ok else "Error"
        log(ctx, level, "Pin", "%s tag=%s -> %s %s"
            % (comp, tag, res.resolved or "(missing)", res.reason.upper()))
        if not res.ok:
            abort(ctx, "Pin", "EC-PIN", "%s pin %s does not match recorded commit %s"
                  % (comp, res.reason, expected))
            return resolved

    # GT-2 capability warning: v0.5.4 TestingTools has no auto-load / Quit /
    # SetLanded. Boot goes through the M-A2 LoadGame verb, quit via FlushAndQuit,
    # landed fixtures via SetOrbit.
    krpc_tag = ctx.pins.get("krpc", {}).get("tag", "")
    if krpc_tag == "v0.5.4":
        log(ctx, "Info", "Pin",
            "GT-2: kRPC v0.5.4 TestingTools lacks %s; boot via M-A2 LoadGame, "
            "quit via FlushAndQuit, land via SetOrbit (bootChannel=%s)"
            % (", ".join(provlib.MISSING_VS_MASTER),
               ctx.pins.get("testingtools", {}).get("bootChannel", "parsek-seam-LoadGame")))
    else:
        log(ctx, "Amber", "Pin",
            "kRPC pinned to non-v0.5.4 ref %s: auto-load flags come with an "
            "unreleased ABI and force the PAIR re-decision (darchambault 0.8.1)."
            % krpc_tag)
    return resolved


def phase_download(ctx: ProvisionContext) -> None:
    """Fetch + verify the kRPC release zip and the MechJeb2 build (live only)."""
    krpc = ctx.pins.get("krpc", {})
    mj = ctx.pins.get("mechjeb2", {})

    for comp, url, sha, name in (
        ("krpc", krpc.get("releaseZipUrl"), krpc.get("releaseZipSha256"), "krpc release zip"),
        ("mechjeb2", mj.get("downloadUrl"), mj.get("sha256"), "mechjeb2 build"),
    ):
        if provlib.is_open_pin(sha):
            if ctx.dry_run:
                log(ctx, "Amber", "Download",
                    "%s sha256 is OPEN -- a live run would download, print the hash, and ABORT (EC-13). "
                    "url=%s" % (name, url))
                continue
            # Live: download, compute, print, ABORT (never trust unverified bytes).
            data = _download(ctx, url)
            if data is None:
                return
            actual = sha256_bytes(data)
            log(ctx, "Error", "Download",
                "%s sha256 unrecorded (EC-13). computed=%s -- record it in pins.toml and re-run."
                % (name, actual))
            abort(ctx, "Download", "EC-13", "%s sha256 OPEN" % comp)
            return
        if ctx.dry_run:
            log(ctx, "Info", "Download", "%s url=%s expected-sha256=%s (no fetch in dry-run)"
                % (name, url, sha))
            continue
        # Live path.
        data = _download(ctx, url)
        if data is None:
            return
        actual = sha256_bytes(data)
        ok = actual.lower() == str(sha).lower()
        log(ctx, "Info" if ok else "Error", "Download",
            "%s bytes=%d sha256 expected=%s actual=%s %s"
            % (name, len(data), sha, actual, "OK" if ok else "FAIL"))
        if not ok:
            abort(ctx, "Download", "EC-3", "%s sha256 mismatch" % comp)
            return
        os.makedirs(CACHE_DIR, exist_ok=True)
        with open(os.path.join(CACHE_DIR, os.path.basename(url)), "wb") as fh:
            fh.write(data)
        if comp == "krpc":
            _assert_krpc_zip_layout(ctx, data)


def phase_build_tt(ctx: ProvisionContext) -> None:
    """Build the 2-file TestingTools shim from the pinned kRPC ref (live only)."""
    tt = ctx.pins.get("testingtools", {})
    full = list(tt.get("fullSourceSetAtTag", provlib.TESTINGTOOLS_SHIM_SOURCES
                        + provlib.TESTINGTOOLS_DROPPED_SOURCES))
    sel = provlib.select_testingtools_sources(full)
    if not sel.ok:
        abort(ctx, "Build-TT", "EC-4", "shim source selection failed: %s" % sel.reason)
        return
    log(ctx, "Info", "Build-TT",
        "shim sources=%s dropped=%s autoloader-excluded=%s"
        % (",".join(sel.included), ",".join(sel.dropped), sel.autoloader_excluded))
    if ctx.dry_run:
        log(ctx, "Info", "Build-TT",
            "would git-archive %s from %s@%s, author TestingTools.shim.csproj "
            "(net472, HintPaths into devInstall Managed + release kRPC), dotnet build -c Release, "
            "hash + cache; assert AutoLoadGame type ABSENT (S-4)"
            % (",".join(sel.included), tt.get("localClone", "mods/krpc"), tt.get("sourceRepoRef", "v0.5.4")))
        return
    # Live build is a real dotnet invocation; kept out of the smoke path.
    log(ctx, "Info", "Build-TT", "(live build would run here; see design BUILD-TT)")


def phase_pair(ctx: ProvisionContext) -> None:
    """Resolve KRPC.MechJeb against the pinned kRPC (GT-6 / EC-14)."""
    krpc_tag = ctx.pins.get("krpc", {}).get("tag", "")
    kmj = ctx.pins.get("krpc_mechjeb", {})
    if krpc_tag == "v0.5.4":
        ok = kmj.get("fork") == "genhis" and kmj.get("tag") == "v0.7.1" \
            and kmj.get("pairedKrpcTag") == "v0.5.4"
        log(ctx, "Info" if ok else "Error", "Pair",
            "krpc_mechjeb fork=%s tag=%s pairedKrpcTag=%s vs krpc %s %s"
            % (kmj.get("fork"), kmj.get("tag"), kmj.get("pairedKrpcTag"), krpc_tag,
               "OK" if ok else "MISMATCH"))
        if not ok:
            abort(ctx, "Pair", "EC-14", "genhis 0.7.1 pairing assertion failed for v0.5.4")
    else:
        log(ctx, "Amber", "Pair",
            "non-v0.5.4 kRPC pinned: pairing UNVERIFIED locally. Web-verify at "
            "github.com/Genhis/KRPC.MechJeb/releases and "
            "github.com/darchambault/KRPC.MechJeb/releases for the fork+tag whose "
            "'Updated for kRPC <ver>' matches the pin, then re-run.")
        abort(ctx, "Pair", "EC-14", "non-v0.5.4 pairing must be web-verified, not guessed")


def phase_clone(ctx: ProvisionContext):
    """Copy the mutable surface and junction the stock asset trees (live only).

    Returns ``(junctionTargets, devSourcedMods_status)`` for the manifest. In a
    live run each dev-sourced mod's status is its content tree-hash; here (and in
    dry-run) it is ``pending-hash`` for present mods and ``absent-source`` for an
    absent optional mod (EC-12).
    """
    junctions: Dict[str, str] = {}
    dev_status: Dict[str, str] = {}
    for tree in provlib.STOCK_JUNCTION_TREES:
        junctions[tree] = os.path.join(ctx.dev_install, tree)
    for name in provlib.STOCK_JUNCTION_GAMEDATA:
        junctions["GameData/%s" % name] = os.path.join(ctx.dev_install, "GameData", name)

    dev = ctx.profile.get("devSourcedMods", []) or []
    log(ctx, "Info", "Clone",
        "junction-targets=%d dev-sourced-copies=%d" % (len(junctions), len(dev)))
    for link, target in junctions.items():
        # Only GameData entries carry a copy-vs-junction classification; the bulk
        # asset trees (StreamingAssets) are always junctioned stock payloads.
        kind = (provlib.classify_gamedata_entry(os.path.basename(link), dev)
                if link.startswith("GameData/") else "junction-stock-tree")
        log(ctx, "Info", "Clone", "junction %s -> %s (%s)" % (link, target, kind))
    for name in dev:
        log(ctx, "Info", "Clone", "copy GameData/%s (%s)"
            % (name, provlib.classify_gamedata_entry(name, dev)))
        dev_status[name] = "pending-hash"

    # EC-12: optional mods (e.g. PersistentRotation) may be absent from the dev
    # GameData. required=false -> record absent-source + WARN; required=true and
    # absent -> ABORT.
    for opt in ctx.profile.get("optionalMods", []) or []:
        oname = opt.get("name", "")
        required = bool(opt.get("required", False))
        src = os.path.join(ctx.dev_install, "GameData", oname)
        present = os.path.isdir(src)
        if present:
            log(ctx, "Info", "Clone", "optional mod %s present -> copy" % oname)
            dev_status[oname] = "pending-hash"
        elif required:
            abort(ctx, "Clone", "EC-12", "required mod %s absent from dev GameData" % oname)
            return junctions, dev_status
        else:
            log(ctx, "Warn", "Clone",
                "EC-12 optional mod %s absent-source (required=false): %s"
                % (oname, opt.get("reason", "")))
            dev_status[oname] = "absent-source"

    if ctx.dry_run:
        log(ctx, "Info", "Clone",
            "would copy mutable surface (exe, Managed, settings.cfg, Internals, "
            "top-level) and create %d junctions via mklink /J" % len(junctions))
        return junctions, dev_status
    # Live copy + junction creation is kept out of the smoke path (design CLONE).
    log(ctx, "Info", "Clone", "(live copy + junctions would run here)")
    return junctions, dev_status


def phase_settings(ctx: ProvisionContext) -> Dict[str, str]:
    """Apply the profile settings deltas over the dev settings.cfg."""
    deltas = {k: str(v) for k, v in (ctx.profile.get("settings", {}) or {}).items()}
    dev_settings = os.path.join(ctx.dev_install, "settings.cfg")

    if not os.path.isfile(dev_settings):
        if ctx.dry_run:
            log(ctx, "Amber", "Settings",
                "dev settings.cfg not found at %s (dry-run: cannot preview delta application)"
                % dev_settings)
            log(ctx, "Info", "Settings", "would apply %d delta(s): %s"
                % (len(deltas), ", ".join(sorted(deltas))))
            return deltas
        abort(ctx, "Settings", "EC-SET", "dev settings.cfg missing at %s" % dev_settings)
        return deltas

    with open(dev_settings, "r", encoding="utf-8") as fh:
        base_lines = fh.read().splitlines()
    base_sha = sha256_bytes(("\n".join(base_lines)).encode("utf-8"))
    present = provlib.keys_present(base_lines, list(deltas))
    appended = [k for k in deltas if k not in present]
    log(ctx, "Info", "Settings", "settingsBaseSha256=%s" % base_sha)
    for k in deltas:
        kind = "replace" if k in present else "append"
        log(ctx, "Info" if k in present else "Warn", "Settings",
            "delta %s=%s (%s)" % (k, deltas[k], kind))
    if appended:
        log(ctx, "Warn", "Settings", "EC-15 appended keys absent from dev cfg: %s"
            % ", ".join(appended))

    new_lines = provlib.apply_settings(base_lines, deltas)
    final_text = "\n".join(new_lines) + "\n"
    final_sha = sha256_bytes(final_text.encode("utf-8"))
    log(ctx, "Info", "Settings", "settingsFinalSha256=%s" % final_sha)

    if not ctx.dry_run:
        os.makedirs(ctx.instance_dir, exist_ok=True)
        with open(os.path.join(ctx.instance_dir, "settings.cfg"), "w", encoding="utf-8") as fh:
            fh.write(final_text)
    ctx.settings_base_sha = base_sha  # type: ignore[attr-defined]
    ctx.settings_final_sha = final_sha  # type: ignore[attr-defined]
    return deltas


def phase_deploy(ctx: ProvisionContext) -> Dict[str, object]:
    """Stage-then-install Parsek.dll with hash + UTF-16 grep (design DEPLOY)."""
    source = ctx.parsek_dll_override or os.path.join(
        ctx.umbrella_root, "Parsek-autotest-provision", "Source", "Parsek", "bin", "Debug", "Parsek.dll")
    # Prefer the current worktree's own build if present.
    worktree_dll = os.path.join(WORKTREE_ROOT, "Source", "Parsek", "bin", "Debug", "Parsek.dll")
    if not ctx.parsek_dll_override and os.path.isfile(worktree_dll):
        source = worktree_dll

    info: Dict[str, object] = {"kind": "staged-build", "stagedFrom": source}
    if ctx.dry_run:
        log(ctx, "Info", "Deploy",
            "would copy %s -> .stage (hash) -> %s/GameData/Parsek/Plugins/Parsek.dll, "
            "re-hash + assert equal, UTF-16 grep %s"
            % (source, ctx.instance_dir, "/".join(PARSEK_SIGNATURE_STRINGS)))
        return info

    if not os.path.isfile(source):
        abort(ctx, "Deploy", "EC-9", "Parsek.dll source not found: %s (use --parsek-dll)" % source)
        return info
    os.makedirs(STAGE_DIR, exist_ok=True)
    stage = os.path.join(STAGE_DIR, "Parsek.dll")
    _copy_file(source, stage)
    stage_sha = sha256_file(stage)
    log(ctx, "Info", "Deploy", "stage-sha256=%s" % stage_sha)

    src_sha = sha256_file(source)
    if src_sha != stage_sha:
        log(ctx, "Amber", "Deploy", "EC-11 source changed after stage copy (informational): src=%s stage=%s"
            % (src_sha, stage_sha))

    plugins = os.path.join(ctx.parsek_gamedata, "Plugins")
    os.makedirs(plugins, exist_ok=True)
    installed = os.path.join(plugins, "Parsek.dll")
    _copy_file(stage, installed)
    installed_sha = sha256_file(installed)
    match = installed_sha == stage_sha
    log(ctx, "Info" if match else "Error", "Deploy",
        "installed-sha256=%s hashMatch %s" % (installed_sha, "OK" if match else "FAIL"))
    if not match:
        abort(ctx, "Deploy", "EC-9", "installed Parsek.dll hash != stage")
        return info

    with open(installed, "rb") as fh:
        dll_bytes = fh.read()
    sigs: Dict[str, int] = {}
    for s in PARSEK_SIGNATURE_STRINGS:
        n = provlib.count_utf16(dll_bytes, s)
        sigs[s] = n
        log(ctx, "Info" if n > 0 else "Error", "Deploy",
            "UTF-16 grep string=%s count=%d %s" % (s, n, "OK" if n > 0 else "FAIL(absent)"))
        if n == 0:
            abort(ctx, "Deploy", "EC-9", "Parsek signature %s absent from installed DLL" % s)
            return info
    info["dllSha256"] = installed_sha
    info["signatureStrings"] = sigs
    return info


def phase_install(ctx: ProvisionContext) -> None:
    for name in ctx.profile.get("stackComponents", []) or []:
        if name == "parsek":
            continue
        log(ctx, "Info", "Install", "stack component %s -> GameData (hash into manifest)" % name)
    if ctx.dry_run:
        log(ctx, "Info", "Install",
            "would extract kRPC into GameData/kRPC, drop TestingTools.dll + KRPC.MechJeb.dll "
            "alongside, extract MechJeb2 into GameData/MechJeb2, hash every DLL")


def phase_mm_cache(ctx: ProvisionContext) -> None:
    for cache in provlib.MM_CACHE_FILES:
        target = os.path.join(ctx.instance_dir, "GameData", cache)
        log(ctx, "Info", "MM-Cache", "delete %s (regenerate on first boot, EC-2)" % cache)
        if not ctx.dry_run and os.path.exists(target):
            try:
                os.remove(target)
            except OSError as exc:
                log(ctx, "Warn", "MM-Cache", "could not delete %s: %s" % (target, exc))


def phase_manifest(ctx: ProvisionContext, resolved: Dict[str, str],
                   junctions: Dict[str, str], deltas: Dict[str, str],
                   parsek_info: Dict[str, object], dev_status: Dict[str, str]) -> Dict:
    krpc = ctx.pins.get("krpc", {})
    tt = ctx.pins.get("testingtools", {})
    mj = ctx.pins.get("mechjeb2", {})
    kmj = ctx.pins.get("krpc_mechjeb", {})
    manifest = {
        "schema": 1,
        "profile": ctx.profile_name,
        "generatedUtc": _utcnow_iso(),
        "provisionScriptCommit": _git_head(WORKTREE_ROOT),
        "kspVersion": ctx.pins.get("kspVersion", "1.12.5"),
        "instanceDir": ctx.profile.get("instanceDir"),
        "components": {
            "krpc": {"kind": "release-zip", "tag": krpc.get("tag"),
                     "commit": resolved.get("krpc", krpc.get("commit")),
                     "sha256": krpc.get("releaseZipSha256")},
            "testingtools": {"kind": "built-shim", "krpcRef": tt.get("sourceRepoRef"),
                             "sourceFiles": list(tt.get("sourceFiles", [])),
                             "capabilities": list(tt.get("capabilities", [])),
                             "bootChannel": tt.get("bootChannel"),
                             "autoLoaderAbsent": True,
                             "missing": list(tt.get("missingVsMaster", []))},
            "mechjeb2": {"kind": "release", "buildNumber": mj.get("buildNumber"),
                         "sha256": mj.get("sha256")},
            "krpc_mechjeb": {"kind": "git", "fork": kmj.get("fork"), "tag": kmj.get("tag"),
                             "commit": resolved.get("krpc_mechjeb", kmj.get("commit")),
                             "pairedKrpc": kmj.get("pairedKrpcTag")},
            "parsek": parsek_info,
        },
        "devSourcedMods": dev_status,
        "junctionTargets": junctions,
        "settingsDeltasApplied": deltas,
        "settingsBaseSha256": getattr(ctx, "settings_base_sha", None),
        "settingsFinalSha256": getattr(ctx, "settings_final_sha", None),
        "buildId64Sha256": _buildid64_sha(ctx),
    }
    if ctx.dry_run:
        log(ctx, "Info", "Manifest",
            "would atomically write provision-manifest.json (components=%d, junctions=%d, deltas=%d)"
            % (len(manifest["components"]), len(junctions), len(deltas)))
        return manifest
    os.makedirs(ctx.parsek_gamedata, exist_ok=True)
    path = os.path.join(ctx.parsek_gamedata, "provision-manifest.json")
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2, sort_keys=True)
    os.replace(tmp, path)
    log(ctx, "Info", "Manifest", "wrote %s" % path)
    return manifest


def phase_verify(ctx: ProvisionContext, manifest: Dict) -> bool:
    """Re-read the instance and cross-check the just-written manifest."""
    if ctx.dry_run:
        log(ctx, "Info", "Verify",
            "would re-read instance: DLL hashes vs manifest, UTF-16 grep, junction "
            "resolution, settingsFinalSha256 re-hash, buildId64Sha256 re-hash")
        return True

    ok = True
    # Junction resolution (EC-8).
    dangling = provlib.verify_junctions(manifest, os.path.exists)
    if dangling:
        ok = False
        for link in dangling:
            log(ctx, "Error", "Verify", "junction DANGLING %s (EC-8)" % link)
    else:
        log(ctx, "Info", "Verify", "junctions resolve OK (%d)" % len(manifest.get("junctionTargets", {})))

    # Parsek DLL hash + UTF-16 grep (EC-9/EC-10 mid-run clobber).
    parsek = manifest["components"].get("parsek", {})
    installed = os.path.join(ctx.parsek_gamedata, "Plugins", "Parsek.dll")
    if parsek.get("dllSha256") and os.path.isfile(installed):
        cur = sha256_file(installed)
        match = cur == parsek["dllSha256"]
        log(ctx, "Info" if match else "Error", "Verify",
            "parsek dll on-disk sha256 %s manifest" % ("==" if match else "!="))
        ok = ok and match

    # settingsFinalSha256 re-hash (N-4).
    final = manifest.get("settingsFinalSha256")
    inst_settings = os.path.join(ctx.instance_dir, "settings.cfg")
    if final and os.path.isfile(inst_settings):
        cur = sha256_file(inst_settings)
        match = cur == final
        log(ctx, "Info" if match else "Error", "Verify",
            "settingsFinalSha256 re-hash %s" % ("OK" if match else "DRIFT"))
        ok = ok and match

    log(ctx, "Info" if ok else "Error", "Verify",
        "instance=%s result=%s" % (ctx.profile_name, "OK" if ok else "DRIFT"))
    return ok


# ---------------------------------------------------------------------------
# Live-only OS helpers (never exercised under --dry-run)
# ---------------------------------------------------------------------------


def _now() -> float:
    import time
    return time.time()


def _utcnow_iso() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _pid_alive(pid: int) -> bool:
    try:
        out = subprocess.run(["tasklist", "/FI", "PID eq %d" % pid],
                             capture_output=True, text=True)
        return str(pid) in (out.stdout or "")
    except OSError:
        return False


def _ksp_running_against(instance_dir: str) -> bool:
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe"],
                             capture_output=True, text=True)
        return "KSP_x64.exe" in (out.stdout or "")
    except OSError:
        return False


def _download(ctx: ProvisionContext, url: str) -> Optional[bytes]:
    import urllib.request
    try:
        with urllib.request.urlopen(url) as resp:  # noqa: S310 (pinned URLs)
            return resp.read()
    except Exception as exc:  # noqa: BLE001
        abort(ctx, "Download", "EC-6", "download failed %s: %s" % (url, exc))
        return None


def _assert_krpc_zip_layout(ctx: ProvisionContext, data: bytes) -> None:
    import io
    import zipfile
    krpc = ctx.pins.get("krpc", {})
    with zipfile.ZipFile(io.BytesIO(data)) as zf:
        names = [n.lower() for n in zf.namelist()]
    for must in krpc.get("releaseCompileDlls", []):
        present = any(must.lower() in n for n in names)
        log(ctx, "Info" if present else "Error", "Download",
            "GT-5 zip contains %s %s" % (must, "OK" if present else "MISSING"))
    for forbidden in krpc.get("mustNotContain", []):
        present = any(forbidden.lower() in n for n in names)
        log(ctx, "Error" if present else "Info", "Download",
            "GT-5 zip must-not-contain %s %s" % (forbidden, "FAIL" if present else "OK"))


def _copy_file(src: str, dst: str) -> None:
    import shutil
    shutil.copyfile(src, dst)


def _git_head(repo: str) -> str:
    out = subprocess.run(["git", "-C", repo, "rev-parse", "HEAD"],
                         capture_output=True, text=True)
    return (out.stdout or "").strip()


def _buildid64_sha(ctx: ProvisionContext) -> Optional[str]:
    path = os.path.join(ctx.instance_dir, "buildID64.txt")
    if os.path.isfile(path):
        return sha256_file(path)
    dev = os.path.join(ctx.dev_install, "buildID64.txt")
    if ctx.dry_run and os.path.isfile(dev):
        return sha256_file(dev)  # dry-run preview against the dev install's id
    return None


# ---------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------


def load_toml(path: str) -> Dict:
    with open(path, "rb") as fh:
        return tomllib.load(fh)


def print_action_plan(ctx: ProvisionContext) -> None:
    plan = provlib.build_action_plan(ctx.pins, ctx.profile)
    print("")
    print("=== DRY-RUN ACTION PLAN: profile=%s ===" % ctx.profile_name)
    for a in plan:
        print("  [%-9s] %-8s %s" % (a.step, a.verb, a.detail))
    print("=== %d planned actions ===" % len(plan))
    print("")


def run(ctx: ProvisionContext) -> int:
    if ctx.dry_run:
        print_action_plan(ctx)

    phase_preflight(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    resolved = phase_pin(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    phase_download(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    phase_build_tt(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    phase_pair(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    junctions, dev_status = phase_clone(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)
    deltas = phase_settings(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    parsek_info = phase_deploy(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    phase_install(ctx)
    phase_mm_cache(ctx)
    manifest = phase_manifest(ctx, resolved, junctions, deltas, parsek_info, dev_status)
    verified = phase_verify(ctx, manifest)
    return _finish(ctx, 0 if verified else 3)


def _finish(ctx: ProvisionContext, code: int) -> int:
    if ctx.aborted:
        log(ctx, "Error", "Summary", "ABORT: %s (exit=2)" % ctx.abort_reason)
        return 2
    log(ctx, "Info", "Summary",
        "instance=%s %s exit=%d" % (ctx.profile_name,
                                    "dry-run plan complete" if ctx.dry_run else "provisioned",
                                    code))
    return code


def main(argv: Optional[List[str]] = None) -> int:
    p = argparse.ArgumentParser(description="M-A6 automation-stack provisioner")
    p.add_argument("--profile", required=True, help="profile name under profiles/ (e.g. stock-minimal)")
    p.add_argument("--repair", action="store_true", help="re-install any drifted component then re-verify")
    p.add_argument("--dry-run", action="store_true",
                   help="print the action plan + drift; no network, downloads, builds, or writes outside harness/")
    p.add_argument("--parsek-dll", help="explicit path to the Parsek.dll to deploy (else the current worktree bin/Debug)")
    p.add_argument("--umbrella-root", help="override the umbrella root (default: parent of this worktree)")
    args = p.parse_args(argv)

    profile_path = os.path.join(PROFILES_DIR, "%s.toml" % args.profile)
    if not os.path.isfile(profile_path):
        print("[Provision][Error][Preflight] profile not found: %s" % profile_path)
        return 2
    pins = load_toml(PINS_PATH)
    profile = load_toml(profile_path)
    umbrella = args.umbrella_root or os.path.abspath(os.path.join(WORKTREE_ROOT, ".."))

    ctx = ProvisionContext(
        profile_name=args.profile,
        pins=pins,
        profile=profile,
        umbrella_root=umbrella,
        dry_run=args.dry_run,
        repair=args.repair,
        parsek_dll_override=args.parsek_dll,
    )
    return run(ctx)


if __name__ == "__main__":
    sys.exit(main())
