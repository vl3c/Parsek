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

Status (v1): this module is the dry-run PLANNER plus the pure decision library
(provlib.py). Live execution of the heavy provisioning phases (BUILD-TT, CLONE,
INSTALL -- and by extension the SETTINGS/DEPLOY/MM-CACHE/MANIFEST writes that
follow them) is NOT yet implemented: a non-dry-run invocation aborts loudly at
the first unimplemented phase rather than half-provisioning an instance or
writing a manifest that claims a completeness the run cannot back. --repair is
likewise unimplemented and aborts. Live execution lands with the coordinated
smoke-run task (design doc, "Test Plan" / "Deferred to live execution").

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
from typing import Dict, List, Optional, Sequence

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
    # Optional --krpc-src override: a dev-supplied kRPC git clone (e.g. the
    # umbrella mods/krpc) used instead of the module-owned .cache/krpc-src clone.
    krpc_src_override: Optional[str] = None
    log_lines: List[str] = field(default_factory=list)
    aborted: bool = False
    abort_reason: str = ""
    # SF2: live-run log lines are buffered in log_lines and NOT written to the
    # instance provision-log.txt until the EC-16 alias gate passes, so no file is
    # ever created at the alias target (a mis-configured instanceDir that nests
    # in / equals the read-only dev install). Flipped True + flushed in PREFLIGHT.
    log_file_enabled: bool = False
    # SF9: the prior provision-manifest.json (if any) + whether it describes a
    # COMPLETE provision (manifest present AND no .provision-incomplete marker).
    # Populated once after PREFLIGHT on a live run; the idempotent skip fast path
    # trusts on-disk state only when prior_complete is True.
    prior_manifest: Optional[Dict] = None
    prior_complete: bool = False

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
    """Emit one provisioning-log line to stdout, buffer it, and (live runs, ONLY
    after the EC-16 alias gate opened the log file) append it to
    provision-log.txt. Before the gate the line stays buffered so nothing is ever
    written at a mis-aliased instance target (SF2)."""
    line = provlib.format_log_line(level, step, message)
    ctx.log_lines.append(line)
    print(line)
    if not ctx.dry_run and ctx.log_file_enabled:
        _append_log_file(ctx, line)


def _append_log_file(ctx: ProvisionContext, line: str) -> None:
    try:
        os.makedirs(ctx.parsek_gamedata, exist_ok=True)
        with open(ctx.log_path(), "a", encoding="utf-8") as fh:
            fh.write(line + "\n")
    except OSError:
        pass  # never let logging failure mask the real work


def _enable_and_flush_log_file(ctx: ProvisionContext) -> None:
    """Open the instance provision-log.txt and flush every buffered line to it
    (SF2). Called ONLY after the EC-16 alias gate passes; a no-op under dry-run."""
    if ctx.dry_run or ctx.log_file_enabled:
        return
    ctx.log_file_enabled = True
    for line in ctx.log_lines:
        _append_log_file(ctx, line)


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
# Self-contained git-source resolution (module boundary). BUILD-TT + PIN read
# git-pinned components from a MODULE-OWNED clone under .cache/<comp>-src, never
# the umbrella mods/ clone (submodule readiness). The pure decision lives in
# provlib.resolve_git_source; the I/O (read-only probe + clone/fetch) is here.
# ---------------------------------------------------------------------------


def _git_source_cache_dir(comp: str) -> str:
    return os.path.join(CACHE_DIR, provlib.GIT_SOURCE_CACHE_DIRNAME.get(comp, "%s-src" % comp))


def _git_has_commit(clone_dir: str, commit: str) -> bool:
    """Read-only: True if ``clone_dir`` already has the pinned commit OBJECT (a
    blobless clone keeps every commit; only blobs are fetched lazily)."""
    if not commit:
        return False
    out = subprocess.run(
        ["git", "-C", clone_dir, "rev-parse", "--verify", "--quiet", "%s^{commit}" % commit],
        capture_output=True, text=True,
    )
    return out.returncode == 0


def _normalize_repo_url(url: str) -> str:
    """Case-insensitive compare form for git remote URLs: trailing slashes and a
    trailing .git are cosmetic."""
    u = (url or "").strip().rstrip("/")
    if u.lower().endswith(".git"):
        u = u[:-4]
    return u.lower()


def _git_origin_matches(clone_dir: str, repo_url: str) -> bool:
    """Read-only: True if ``clone_dir``'s origin URL matches the pinned
    sourceRepo. A mismatch means the pin's fork moved since the cache was
    cloned (e.g. KRPC.MechJeb genhis -> darchambault); fetching from the stale
    origin can never deliver the new tag/commit. An unreadable origin counts
    as a mismatch so the refetch path re-points it."""
    if not repo_url:
        return True  # no pin to compare against; EC-4 aborts later anyway
    out = subprocess.run(
        ["git", "-C", clone_dir, "config", "--get", "remote.origin.url"],
        capture_output=True, text=True,
    )
    if out.returncode != 0:
        return False
    return _normalize_repo_url(out.stdout) == _normalize_repo_url(repo_url)


def _shallow_clone_source(ctx: ProvisionContext, comp: str, repo_url: str,
                          cache_dir: str, commit: str) -> bool:
    """Blobless clone the pinned repo into ``cache_dir`` (all refs, no blobs, no
    checkout); fetch the pinned commit explicitly if the initial clone lacks it."""
    import shutil
    os.makedirs(CACHE_DIR, exist_ok=True)
    if os.path.isdir(cache_dir):
        # A leftover dir without a usable .git: remove and reclone.
        shutil.rmtree(_long(cache_dir), ignore_errors=True)
    cmd = ["git", "clone", "--filter=blob:none", "--no-checkout", repo_url, cache_dir]
    log(ctx, "Info", "Source", "%s clone %s -> %s" % (comp, repo_url, cache_dir))
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        abort(ctx, "Source", "EC-4",
              "git clone %s failed: %s" % (repo_url, (res.stderr or "").strip()[:200]))
        return False
    if not _git_has_commit(cache_dir, commit):
        return _fetch_source_commit(ctx, comp, cache_dir, commit)
    return True


def _fetch_source_commit(ctx: ProvisionContext, comp: str, cache_dir: str,
                         commit: str, repo_url: str = "") -> bool:
    """Refetch a stale cache clone (moved pin / interrupted earlier fetch / a
    fork re-pin), then assert the pinned commit is present. When ``repo_url``
    is given and the clone's origin differs (cached-wrong-origin), origin is
    re-pointed FIRST - fetching from a stale fork can never deliver the new
    pin's tag/commit."""
    if repo_url and not _git_origin_matches(cache_dir, repo_url):
        log(ctx, "Info", "Source",
            "%s origin re-pointed to %s (fork re-pin; stale cache origin)" % (comp, repo_url))
        res = subprocess.run(
            ["git", "-C", cache_dir, "remote", "set-url", "origin", repo_url],
            capture_output=True, text=True,
        )
        if res.returncode != 0:
            abort(ctx, "Source", "EC-4",
                  "git remote set-url in %s failed: %s"
                  % (cache_dir, (res.stderr or "").strip()[:200]))
            return False
    log(ctx, "Info", "Source", "%s fetch in %s (pinned commit missing)" % (comp, cache_dir))
    res = subprocess.run(
        ["git", "-C", cache_dir, "fetch", "--filter=blob:none", "--tags", "origin"],
        capture_output=True, text=True,
    )
    if res.returncode != 0:
        abort(ctx, "Source", "EC-4",
              "git fetch in %s failed: %s" % (cache_dir, (res.stderr or "").strip()[:200]))
        return False
    if not _git_has_commit(cache_dir, commit):
        abort(ctx, "Source", "EC-4",
              "%s pinned commit %s absent after fetch (%s)" % (comp, commit, cache_dir))
        return False
    return True


def _ensure_git_source(ctx: ProvisionContext, comp: str,
                       override: Optional[str] = None) -> Optional[str]:
    """Resolve (and, on a live run, materialize) the module-owned git source
    clone for a git-pinned component, replacing the umbrella mods/ read. Returns
    the source dir to peel / git-show from, or None (aborted / unmaterialized).

    Memoized on ``ctx`` so PIN and BUILD-TT share one resolution (no double
    clone). Under --dry-run nothing is cloned: an already-present cache clone is
    reused for the read-only peel, otherwise the source is unmaterialized and the
    logged plan line states the clone/fetch a live run would do.
    """
    memo = getattr(ctx, "git_source_dirs", None)
    if memo is None:
        memo = {}
        ctx.git_source_dirs = memo
    if comp in memo:
        return memo[comp]

    pin = ctx.pins.get(comp, {})
    commit = pin.get("commit", "")
    repo_url = pin.get("sourceRepo", "")
    cache_dir = _git_source_cache_dir(comp)
    override_present = bool(override) and os.path.isdir(os.path.join(override, ".git"))
    cache_has_git = os.path.isdir(os.path.join(cache_dir, ".git"))
    cache_has_commit = cache_has_git and _git_has_commit(cache_dir, commit)
    cache_origin_matches = (not cache_has_git) or _git_origin_matches(cache_dir, repo_url)
    decision = provlib.resolve_git_source(
        cache_dir, commit, override, override_present, cache_has_git,
        cache_has_commit, cache_origin_matches)
    log(ctx, "Info", "Source",
        "%s source-resolution action=%s reason=%s dir=%s (repo=%s commit=%s)"
        % (comp, decision.action, decision.reason, decision.source_dir,
           repo_url or "-", commit or "-"))

    if decision.action == "use-override":
        if not override_present:
            abort(ctx, "Source", "EC-4",
                  "--krpc-src %s is not a git clone (no .git)" % override)
            memo[comp] = None
            return None
        memo[comp] = decision.source_dir
        return decision.source_dir

    if ctx.dry_run:
        result = decision.source_dir if decision.action == "reuse-cache" else None
        memo[comp] = result
        return result

    # Live: materialize the module-owned cache clone.
    if not repo_url or provlib.is_open_pin(repo_url):
        abort(ctx, "Source", "EC-4", "%s has no sourceRepo pin to clone" % comp)
        memo[comp] = None
        return None
    if decision.action == "clone":
        ok = _shallow_clone_source(ctx, comp, repo_url, cache_dir, commit)
    else:  # refetch-cache (incl. cached-wrong-origin: fetch re-points origin first)
        ok = _fetch_source_commit(ctx, comp, cache_dir, commit, repo_url)
    memo[comp] = cache_dir if ok else None
    return cache_dir if ok else None


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

    # Dev-install aliasing guard (EC-16). The live primitives overwrite
    # settings.cfg, copy the DLL, and DELETE the MM cache; an instance dir that
    # equals, nests inside, or contains the read-only dev install (or is not
    # under automation/) would corrupt the clone source. ABORT before any write.
    inst_norm = os.path.normcase(os.path.normpath(ctx.instance_dir))
    dev_norm = os.path.normcase(os.path.normpath(ctx.dev_install))
    alias = provlib.check_instance_dir_alias(inst_norm, dev_norm, ctx.profile.get("instanceDir", ""))
    if not alias.ok:
        abort(ctx, "Preflight", "EC-16",
              "instance dir aliases the dev install (%s): instance=%s dev=%s"
              % (alias.reason, ctx.instance_dir, ctx.dev_install))
        return

    # SF2: EC-16 passed -- the instance target is safe. Open the provision-log
    # file and flush every buffered PREFLIGHT line into it now.
    _enable_and_flush_log_file(ctx)

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
        # Record ownership so _finish removes ONLY a lock this run created (never
        # a lock a concurrent live run holds, which we would have refused above).
        ctx.lock_path = lock_path  # type: ignore[attr-defined]
        ctx.lock_acquired = True  # type: ignore[attr-defined]

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
        override = ctx.krpc_src_override if comp == "krpc" else None
        clone = _ensure_git_source(ctx, comp, override)
        if ctx.aborted:
            return resolved
        tag = pin.get("tag", "")
        expected = pin.get("commit", "")
        if not clone or not os.path.isdir(os.path.join(clone, ".git")):
            # No source clone materialized (dry-run without a pre-existing cache
            # clone): report the intended assertion instead of peeling.
            log(ctx, "Info", "Pin", "%s tag=%s expected-commit=%s (source not materialized%s)"
                % (comp, tag, expected, " in dry-run" if ctx.dry_run else ""))
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
    kmj = ctx.pins.get("krpc_mechjeb", {})

    # Three pinned release artifacts. krpc_mechjeb ordered before mechjeb2 so its
    # (resolved) download succeeds; the OPEN mechjeb2 pin then aborts DOWNLOAD by
    # design (EC-13) until a durable build URL+sha256 is recorded.
    for comp, url, sha, name in (
        ("krpc", krpc.get("releaseZipUrl"), krpc.get("releaseZipSha256"), "krpc release zip"),
        ("krpc_mechjeb", kmj.get("downloadUrl"), kmj.get("releaseZipSha256"), "krpc_mechjeb release zip"),
        ("mechjeb2", mj.get("downloadUrl"), mj.get("sha256"), "mechjeb2 build"),
    ):
        if provlib.is_open_pin(sha):
            if ctx.dry_run:
                log(ctx, "Amber", "Download",
                    "%s sha256 is OPEN -- a live run would download, print the hash, and ABORT (EC-13). "
                    "url=%s" % (name, url))
                continue
            # If the URL itself is still OPEN (e.g. mechjeb2 has no download URL
            # yet), we cannot fetch anything to compute a hash. Fire EC-13
            # directly instead of calling _download("OPEN") and aborting with a
            # bogus EC-6 download-failed error.
            if provlib.is_open_pin(url):
                log(ctx, "Error", "Download",
                    "%s downloadUrl is OPEN (EC-13): record both the URL and its sha256 "
                    "in pins.toml and re-run." % name)
                abort(ctx, "Download", "EC-13", "%s url+sha256 OPEN" % comp)
                return
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
        if comp == "krpc" and not _assert_krpc_zip_layout(ctx, data):
            return  # SF4: wrong layout -> abort, never cache/install this zip
        os.makedirs(CACHE_DIR, exist_ok=True)
        with open(os.path.join(CACHE_DIR, os.path.basename(url)), "wb") as fh:
            fh.write(data)


def phase_build_tt(ctx: ProvisionContext, resolved: Dict[str, str]) -> None:
    """Build the 2-file TestingTools shim from the pinned kRPC ref (design BUILD-TT).

    Exports OrbitTools.cs + TestingTools.cs via ``git show`` at the PEEL-VERIFIED
    kRPC commit (``resolved['krpc']``, N12 -- not the mutable tag name, so a
    retagged ref cannot silently change the shim source), NEVER a working-tree
    build (GT-1); AutoLoadGame.cs / AutoSwitchVessel.cs deliberately dropped
    (GT-4). Authors a standalone SDK-style net472 shim csproj + minimal
    AssemblyInfo (GT-9 HintPaths into the dev Managed dir + the extracted release
    kRPC binaries), ``dotnet build -c Release``, asserts the AutoLoadGame type is
    ABSENT and the six control-capability RPCs are PRESENT in the built assembly
    (S-4 / N17), then hashes + caches TestingTools.dll.
    """
    tt = ctx.pins.get("testingtools", {})
    full = list(tt.get("fullSourceSetAtTag", provlib.TESTINGTOOLS_SHIM_SOURCES
                        + provlib.TESTINGTOOLS_DROPPED_SOURCES))
    sel = provlib.select_testingtools_sources(full)
    if not sel.ok:
        abort(ctx, "Build-TT", "EC-4", "shim source selection failed: %s" % sel.reason)
        return
    # N12: build from the peeled commit, not the tag. Falls back to the tag only
    # when a dry-run without the clone left resolved['krpc'] unpopulated.
    build_ref = resolved.get("krpc") or tt.get("sourceRepoRef", "v0.5.4")
    # Module-owned source (.cache/krpc-src or --krpc-src); PIN already resolved +
    # memoized it, so this reuses that decision without a second clone/log.
    krpc_src = _ensure_git_source(ctx, "krpc", ctx.krpc_src_override)
    if ctx.aborted:
        return
    log(ctx, "Info", "Build-TT",
        "shim sources=%s dropped=%s autoloader-excluded=%s build-ref=%s src=%s"
        % (",".join(sel.included), ",".join(sel.dropped), sel.autoloader_excluded,
           build_ref, krpc_src or "(unmaterialized)"))
    if ctx.dry_run:
        log(ctx, "Info", "Build-TT",
            "would git-show %s from %s@%s, author TestingTools.shim.csproj "
            "(net472, HintPaths into devInstall Managed + release kRPC), dotnet build -c Release, "
            "hash + cache; assert AutoLoadGame type ABSENT + six capability RPCs PRESENT (S-4)"
            % (",".join(sel.included), krpc_src or _git_source_cache_dir("krpc"), build_ref))
        return
    # SF9: skip the dotnet build only when a prior COMPLETE provision built the
    # shim from an UNCHANGED set of inputs and the cached TestingTools.dll still
    # matches the recorded hash. The shim is built with HintPaths into the release
    # kRPC binaries (from the kRPC release zip) AND the dev install's Managed
    # reference DLLs (the KSP version), so the cached hash alone is NOT proof the
    # dll is still fresh (reviewer S1). Every fresh input must be re-checked:
    #   - the peeled kRPC SOURCE commit (a retagged ref changes the shim source);
    #   - the kRPC RELEASE-ZIP pin (a moved releaseZipSha256 changes the linked
    #     compile refs even with the same commit) via _install_pin_stable;
    #   - the dev KSP buildID64 (a version bump changes the Managed reference
    #     DLLs the shim links against).
    cached = os.path.join(CACHE_DIR, "TestingTools.dll")
    recorded_tt = _prior_component(ctx, "testingtools").get("dllSha256")
    prior_krpc_commit = _prior_component(ctx, "krpc").get("commit")
    current_tt = sha256_file(cached) if os.path.isfile(cached) else None
    commit_stable = bool(build_ref) and build_ref == prior_krpc_commit
    krpc_pin_stable = _install_pin_stable(ctx, "krpc")
    recorded_b64 = (ctx.prior_manifest or {}).get("buildId64Sha256")
    dev_b64 = _dev_buildid64_sha(ctx)
    buildid_stable = bool(recorded_b64) and dev_b64 is not None and dev_b64 == recorded_b64
    inputs_stable = commit_stable and krpc_pin_stable and buildid_stable
    decision = provlib.decide_idempotent_skip(
        ctx.prior_complete and inputs_stable, recorded_tt, current_tt)
    if decision.skip:
        ctx.testingtools_dll = cached  # type: ignore[attr-defined]
        ctx.testingtools_sha = current_tt  # type: ignore[attr-defined]
        log(ctx, "Info", "Build-TT",
            "SF9 skip dotnet build (hash-match; cached TestingTools.dll sha256=%s; "
            "krpc-commit+releaseZip-pin+dev-buildID64 all stable)" % current_tt)
        return
    if ctx.prior_complete and not decision.skip:
        log(ctx, "Info", "Build-TT",
            "SF9 rebuild TestingTools (no skip): commit_stable=%s krpc_pin_stable=%s "
            "buildid_stable=%s hash-reason=%s"
            % (commit_stable, krpc_pin_stable, buildid_stable, decision.reason))
    _build_testingtools(ctx, sel, tt, build_ref, krpc_src)


def phase_pair(ctx: ProvisionContext) -> None:
    """Resolve KRPC.MechJeb against the pinned kRPC (GT-6 / EC-14)."""
    krpc_tag = ctx.pins.get("krpc", {}).get("tag", "")
    kmj = ctx.pins.get("krpc_mechjeb", {})
    decision = provlib.evaluate_krpc_mechjeb_pair(
        krpc_tag, kmj.get("fork", ""), kmj.get("tag", ""), kmj.get("pairedKrpcTag", ""))
    if not decision.requires_web_verify:
        log(ctx, "Info" if decision.ok else "Error", "Pair",
            "krpc_mechjeb fork=%s tag=%s pairedKrpcTag=%s vs krpc %s %s"
            % (kmj.get("fork"), kmj.get("tag"), kmj.get("pairedKrpcTag"), krpc_tag,
               "OK" if decision.ok else "MISMATCH"))
        if not decision.ok:
            abort(ctx, "Pair", "EC-14",
                  "krpc_mechjeb (fork=%s, tag=%s) is not a web-verified pair for kRPC v0.5.4 "
                  "(see provlib.PROVEN_KRPC_MECHJEB_PAIRS_V054)" % (kmj.get("fork"), kmj.get("tag")))
    else:
        log(ctx, "Amber", "Pair",
            "non-v0.5.4 kRPC pinned: pairing UNVERIFIED locally. Web-verify at "
            "github.com/Genhis/KRPC.MechJeb/releases and "
            "github.com/darchambault/KRPC.MechJeb/releases for the fork+tag whose "
            "'Updated for kRPC <ver>' matches the pin, then re-run.")
        abort(ctx, "Pair", "EC-14", "non-v0.5.4 pairing must be web-verified, not guessed")


def phase_clone(ctx: ProvisionContext):
    """Copy the mutable surface and junction the stock asset trees (design CLONE).

    Returns ``(junctionTargets, devSourcedMods_status)`` for the manifest. In a
    live run this writes the ``.provision-incomplete`` marker FIRST, pre-checks
    free space (EC-6), copies the mutable surface (exe, KSP data dir minus the
    junctioned ``StreamingAssets``, Internals, top-level files -- extended-length
    paths for deep trees, EC-7), junctions the stock asset payloads
    (``StreamingAssets`` + ``Squad`` + ``SquadExpansion``, mklink /J), and copies
    each dev-sourced mod folder recording its content tree-hash (EC-3). The marker
    is cleared LAST, only on VERIFY success. The dry-run plan records
    ``planned-copy`` for present mods and ``absent-source`` for an absent optional
    mod (EC-12).
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

    # EC-12: optional mods (e.g. PersistentRotation) may be absent from the dev
    # GameData. required=false -> record absent-source + WARN; required=true and
    # absent -> ABORT. Present ones join the copy set.
    copy_mods: List[str] = list(dev)
    for opt in ctx.profile.get("optionalMods", []) or []:
        oname = opt.get("name", "")
        required = bool(opt.get("required", False))
        src = os.path.join(ctx.dev_install, "GameData", oname)
        present = os.path.isdir(src)
        if present:
            log(ctx, "Info", "Clone", "optional mod %s present -> copy" % oname)
            copy_mods.append(oname)
        elif required:
            abort(ctx, "Clone", "EC-12", "required mod %s absent from dev GameData" % oname)
            return junctions, dev_status
        else:
            log(ctx, "Warn", "Clone",
                "EC-12 optional mod %s absent-source (required=false): %s"
                % (oname, opt.get("reason", "")))
            dev_status[oname] = "absent-source"

    if ctx.dry_run:
        for name in copy_mods:
            log(ctx, "Info", "Clone", "copy GameData/%s (%s)"
                % (name, provlib.classify_gamedata_entry(name, dev)))
            # "planned-copy": a dry-run plan preview. The real content tree-hash is
            # computed only by the live CLONE; do not record a forward-looking
            # "pending-hash" the run cannot fill.
            dev_status[name] = "planned-copy"
        log(ctx, "Info", "Clone",
            "would copy mutable surface (exe, KSP data dir, Internals, top-level) "
            "and create %d junctions via mklink /J" % len(junctions))
        return junctions, dev_status

    # LIVE. SF6(a): re-run the EC-16 alias guard on the REALPATH-resolved instance
    # paths before the first instance write -- a junction/symlink could resolve a
    # string-clean path INTO the read-only dev install.
    if not _recheck_alias_resolved(ctx):
        return junctions, dev_status
    # Marker first (EC-6): any abort past this point leaves a half-provision the
    # harness refuses to admit; VERIFY clears it only on success.
    _write_incomplete_marker(ctx)
    # SF9: skip the bulk mutable-surface copy + junction (re)creation when a prior
    # COMPLETE provision left an instance whose buildID64 already matches AND whose
    # junctions still resolve (VERIFY re-checks buildID/settings/DLLs/junctions in
    # full afterward, so nothing this elides goes unverified).
    if _clone_surface_skips(ctx, junctions):
        log(ctx, "Info", "Clone",
            "SF9 skip mutable-surface copy + junctions (hash-match; prior complete instance)")
    else:
        if not _precheck_free_space(ctx):
            return junctions, dev_status
        _clone_mutable_surface(ctx)
        if ctx.aborted:
            return junctions, dev_status
        _create_junctions(ctx, junctions)
        if ctx.aborted:
            return junctions, dev_status
    gd_instance = os.path.join(ctx.instance_dir, "GameData")
    os.makedirs(_long(gd_instance), exist_ok=True)
    prior_dev = (ctx.prior_manifest or {}).get("devSourcedMods", {}) or {}
    skipped_mods = 0
    for name in copy_mods:
        src = os.path.join(ctx.dev_install, "GameData", name)
        dst = os.path.join(gd_instance, name)
        if not os.path.exists(src):
            abort(ctx, "Clone", "EC-3", "dev-sourced mod %s vanished mid-run: %s" % (name, src))
            return junctions, dev_status
        # SF9/S3: skip re-copying a dev-sourced mod ONLY when the fresh DEV-SOURCE
        # tree-hash equals BOTH the recorded manifest hash AND the instance
        # tree-hash (dev-source == recorded == instance). Comparing instance vs
        # manifest alone is a proxy that never notices a dev-side mod UPDATE: the
        # instance still matches the old recorded hash so the skip fires and the
        # update never propagates (pre-SF9 re-copied every run). Gating on the
        # fresh source (mirror of the DEPLOY Parsek.dll pattern) re-copies the
        # instant the dev source changes.
        recorded = prior_dev.get(name)
        recorded_hash = recorded if recorded not in ("absent-source", "planned-copy") else None
        src_hash = _content_tree_hash(src, ctx)
        current = _content_tree_hash(dst, ctx) if os.path.isdir(dst) or os.path.isfile(dst) else None
        source_matches_recorded = recorded_hash is not None and recorded_hash == src_hash
        decision = provlib.decide_idempotent_skip(
            ctx.prior_complete and source_matches_recorded, src_hash, current)
        if decision.skip:
            dev_status[name] = current
            skipped_mods += 1
            continue
        dst_hash = _copy_and_verify_dev_mod(ctx, name, src, dst)
        if ctx.aborted:
            return junctions, dev_status
        dev_status[name] = dst_hash
        log(ctx, "Info", "Clone",
            "copied GameData/%s tree-hash=%s (instance-verified reason=%s)"
            % (name, dst_hash, decision.reason))
    if skipped_mods:
        log(ctx, "Info", "Clone",
            "SF9 skipped %d/%d dev-sourced mod copies (hash-match)" % (skipped_mods, len(copy_mods)))
    return junctions, dev_status


def _clone_surface_skips(ctx: ProvisionContext, junctions: Dict[str, str]) -> bool:
    """SF9 gate for the bulk mutable-surface copy + junction creation. True only
    when a prior COMPLETE provision left this instance's buildID64.txt matching
    the recorded manifest hash, every recorded junction still resolves to its
    target, AND the instance's KSP_x64_Data/Managed stat (fileCount + bytes) still
    matches the recorded stat. buildID64 is the version fingerprint of the copied
    KSP install, but it is a SINGLE-FILE proxy: it cannot detect a
    partially-deleted instance or a swapped stock Managed DLL (reviewer S2), and
    VERIFY never re-hashes the stock Managed tree, so a false skip would go
    uncaught. The Managed-stat re-scan is the cheap fresh integrity check that
    closes that gap.

    FRESH-SOURCE rule (item 10, mirror of the BUILD-TT gate + the dev-sourced-mod copy):
    the mutable surface is COPIED FROM the dev install, so the skip must also gate on the
    DEV install's CURRENT buildID64 -- not just instance-vs-recorded. A dev-side KSP
    version bump changes the source buildID64 while the instance still matches the OLD
    recorded hash, so an instance-vs-recorded-only gate would SKIP and the new KSP version
    would never re-propagate into the instance (the exact dev-UPDATE blind spot the
    dev-sourced-mod copy calls out). Require dev-source == recorded == instance. RESIDUAL
    GAP (noted honestly in design-autotest-stack-setup.md): buildID64 fingerprints the
    version, not the copied exe / top-level files; a same-version dev change to those is
    still not caught here (VERIFY does not re-hash them either)."""
    recorded_b64 = (ctx.prior_manifest or {}).get("buildId64Sha256")
    b64_path = os.path.join(ctx.instance_dir, "buildID64.txt")
    current_b64 = sha256_file(b64_path) if os.path.isfile(b64_path) else None
    dev_b64 = _dev_buildid64_sha(ctx)
    # Gate on the FRESH source: skip only when the dev install's current buildID64 still
    # equals the recorded hash (else a KSP version bump must re-copy). decide_idempotent_skip
    # then confirms the instance matches that same fresh id.
    source_matches_recorded = recorded_b64 is not None and dev_b64 == recorded_b64
    decision = provlib.decide_idempotent_skip(
        ctx.prior_complete and source_matches_recorded, dev_b64, current_b64)
    if not decision.skip:
        if ctx.prior_complete and recorded_b64 is not None and not source_matches_recorded:
            log(ctx, "Info", "Clone",
                "SF9 no mutable-surface skip: dev buildID64 changed (fresh-source rule; a KSP version bump must re-copy)")
        return False

    def _resolve_link(link_key: str) -> Optional[str]:
        link_abs = os.path.join(ctx.instance_dir, link_key)
        try:
            os.lstat(link_abs)
        except OSError:
            return None
        if not os.path.exists(link_abs):
            return None
        return os.path.realpath(link_abs)
    dangling = provlib.verify_junctions({"junctionTargets": junctions}, _resolve_link)
    if dangling:
        log(ctx, "Info", "Clone",
            "SF9 no mutable-surface skip: %d junction(s) need (re)creation" % len(dangling))
        return False

    # S2 fresh integrity check: re-stat the copied Managed surface and refuse to
    # skip if it drifted from the recorded stat (a deleted / resized stock DLL).
    recorded_stat = (ctx.prior_manifest or {}).get("mutableSurfaceManagedStat")
    cur_count, cur_bytes = _managed_dir_stat(ctx)
    if not provlib.mutable_surface_stat_matches(recorded_stat, cur_count, cur_bytes):
        rec = recorded_stat or {}
        log(ctx, "Info", "Clone",
            "SF9 no mutable-surface skip: Managed stat drift recorded=%s/%s current=%d/%d"
            % (rec.get("fileCount"), rec.get("bytes"), cur_count, cur_bytes))
        return False
    return True


def _copy_and_verify_dev_mod(ctx: ProvisionContext, name: str, src: str, dst: str) -> Optional[str]:
    """Copy one dev-sourced mod into the instance and return its INSTANCE
    content-tree hash (BLOCKER 1).

    Hashes the SOURCE (canonical expectation) AND the just-written instance copy;
    a mismatch means a partial / failed copy, so it ABORTS (EC-3) and returns None
    rather than record a hash the instance does not actually carry. The recorded
    hash is the instance's (now == source's), so VERIFY re-hashes the same bytes
    it will read back.

    A pre-existing instance copy is scoped-deleted first (same rationale as
    _repair_dev_mods: a merge-copy cannot remove a file injected into the
    instance copy -- e.g. runtime caches from a prior game launch -- so the
    post-copy hash check would abort forever)."""
    # Only a DIRECTORY needs the pre-clear: a single-file mod is exactly
    # replaced by the copy (and rmtree cannot delete a file anyway).
    if os.path.isdir(dst) and not _scoped_delete_instance_subtree(ctx, dst):
        abort(ctx, "Clone", "EC-3",
              "dev-sourced mod %s: stale instance copy could not be cleared: %s" % (name, dst))
        return None
    if os.path.isdir(src):
        _copy_dir(ctx, src, dst)
    else:
        _copy_one(src, dst)
    src_hash = _content_tree_hash(src, ctx)
    dst_hash = _content_tree_hash(dst, ctx)
    if src_hash != dst_hash:
        abort(ctx, "Clone", "EC-3",
              "dev-sourced mod %s partial copy: source-hash=%s instance-hash=%s"
              % (name, src_hash, dst_hash))
        return None
    return dst_hash


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

    # settingsBaseSha256 is over the RAW dev-file bytes exactly as read (line
    # endings included), NOT a "\n"-rejoin of the split lines: it records what
    # the dev settings.cfg actually was on disk, so a re-hash by any tool
    # matches. splitlines() is only used to drive the line-oriented delta apply.
    with open(dev_settings, "rb") as fh:
        base_bytes = fh.read()
    base_sha = sha256_bytes(base_bytes)
    base_lines = base_bytes.decode("utf-8").splitlines()
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
    # Dry-run preview hash over the text we WOULD write.
    final_sha = sha256_bytes(final_text.encode("utf-8"))

    if not ctx.dry_run:
        os.makedirs(ctx.instance_dir, exist_ok=True)
        out_path = os.path.join(ctx.instance_dir, "settings.cfg")
        # newline="\n": suppress the platform CRLF translation Python's text mode
        # applies by default on Windows, so the on-disk bytes match final_text.
        with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(final_text)
        # Record settingsFinalSha256 over the bytes ACTUALLY on disk, not the
        # in-memory text: VERIFY (and the harness) re-hash the raw file, so the
        # recorded value must be that same raw-byte hash or every live run would
        # exit 3 DRIFT on a platform that rewrote the line endings.
        final_sha = sha256_file(out_path)
    log(ctx, "Info", "Settings", "settingsFinalSha256=%s" % final_sha)
    ctx.settings_base_sha = base_sha  # type: ignore[attr-defined]
    ctx.settings_final_sha = final_sha  # type: ignore[attr-defined]
    return deltas


def _resolve_aux_payload(ctx: ProvisionContext) -> "provlib.ParsekAuxPayload":
    """Resolve the Parsek DEPLOY aux payload (version + toolbar textures) for
    this instance: worktree GameData/Parsek first, repo img/ next, dev install
    GameData/Parsek last."""
    return provlib.resolve_parsek_aux_payload(
        os.path.join(WORKTREE_ROOT, "GameData", "Parsek"),
        os.path.join(WORKTREE_ROOT, "img"),
        os.path.join(ctx.dev_install, "GameData", "Parsek"),
        os.path.isfile)


def phase_deploy(ctx: ProvisionContext) -> Dict[str, object]:
    """Stage-then-install Parsek.dll with hash + UTF-16 grep (design DEPLOY)."""
    # SF8: no hardcoded sibling-worktree default. The override wins, else this
    # worktree's own bin/Debug build; absent both, DEPLOY aborts demanding
    # --parsek-dll rather than deploying an unrelated worktree's DLL.
    worktree_dll = os.path.join(WORKTREE_ROOT, "Source", "Parsek", "bin", "Debug", "Parsek.dll")
    sel = provlib.select_parsek_dll_source(
        ctx.parsek_dll_override, worktree_dll, os.path.isfile(worktree_dll))
    source = sel.source
    log(ctx, "Info", "Deploy", "parsek dll source=%s (%s)" % (source, sel.reason))

    info: Dict[str, object] = {"kind": "staged-build", "stagedFrom": source}
    if ctx.dry_run:
        if not sel.ok:
            log(ctx, "Amber", "Deploy",
                "no worktree bin/Debug Parsek.dll at %s and no --parsek-dll: a live run would ABORT "
                "(EC-9) demanding --parsek-dll (build this worktree: cd Source/Parsek && dotnet build)"
                % worktree_dll)
            return info
        log(ctx, "Info", "Deploy",
            "would copy %s -> .stage (hash) -> %s/GameData/Parsek/Plugins/Parsek.dll, "
            "re-hash + assert equal, UTF-16 grep %s"
            % (source, ctx.instance_dir, "/".join(PARSEK_SIGNATURE_STRINGS)))
        payload = _resolve_aux_payload(ctx)
        for f in payload.files:
            log(ctx, "Info", "Deploy", "would install aux %s <- %s (%s), hash into manifest"
                % (f.dest_rel, f.source, f.origin))
        for dest in payload.missing_required:
            log(ctx, "Amber", "Deploy", "required aux payload %s: NO source (a live run would ABORT EC-9)" % dest)
        for dest in payload.missing_optional:
            log(ctx, "Info", "Deploy", "optional aux payload %s: no source, skipped" % dest)
        return info

    if not sel.ok:
        abort(ctx, "Deploy", "EC-9",
              "no Parsek.dll to deploy: build this worktree (cd Source/Parsek && dotnet build) "
              "or pass --parsek-dll")
        return info
    if not os.path.isfile(source):
        abort(ctx, "Deploy", "EC-9", "Parsek.dll source not found: %s (use --parsek-dll)" % source)
        return info
    plugins = os.path.join(ctx.parsek_gamedata, "Plugins")
    installed = os.path.join(plugins, "Parsek.dll")
    # SF9: skip staging + install-copy when a prior COMPLETE provision left an
    # instance DLL whose hash already equals the SOURCE we would deploy (the
    # worktree build has not changed). Comparing SOURCE vs installed -- not the
    # manifest -- means a rebuilt Parsek.dll still redeploys.
    src_sha = sha256_file(source)
    current_installed = sha256_file(installed) if os.path.isfile(installed) else None
    dll_skip = provlib.decide_idempotent_skip(ctx.prior_complete, src_sha, current_installed)
    if dll_skip.skip:
        installed_sha = current_installed
        log(ctx, "Info", "Deploy",
            "SF9 skip stage+install-copy (hash-match; installed-sha256=%s == source)" % installed_sha)
    else:
        os.makedirs(STAGE_DIR, exist_ok=True)
        stage = os.path.join(STAGE_DIR, "Parsek.dll")
        _copy_file(source, stage)
        stage_sha = sha256_file(stage)
        log(ctx, "Info", "Deploy", "stage-sha256=%s (skip-reason=%s)" % (stage_sha, dll_skip.reason))
        if src_sha != stage_sha:
            log(ctx, "Amber", "Deploy", "EC-11 source changed after stage copy (informational): src=%s stage=%s"
                % (src_sha, stage_sha))
        os.makedirs(plugins, exist_ok=True)
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

    # DEPLOY is not just the DLL: install the version file + toolbar textures in
    # the same GameData/Parsek payload (their absence floods ToolbarControl.OnGUI
    # with per-frame NREs). Hash each into the manifest so VERIFY re-hashes and
    # --repair reconverges (field components.parsek.auxFiles.<dest>).
    payload = _resolve_aux_payload(ctx)
    for dest in payload.missing_required:
        abort(ctx, "Deploy", "EC-9",
              "required Parsek aux payload %s has no source (worktree GameData/Parsek, "
              "repo img/, or dev install GameData/Parsek)" % dest)
        return info
    aux_hashes: Dict[str, str] = {}
    aux_skipped = 0
    for f in payload.files:
        dst = os.path.join(ctx.parsek_gamedata, *f.dest_rel.split("/"))
        # SF9: skip the aux copy when the installed file already equals the source.
        src_aux_sha = sha256_file(f.source) if os.path.isfile(f.source) else None
        cur_aux_sha = sha256_file(dst) if os.path.isfile(dst) else None
        aux_skip = provlib.decide_idempotent_skip(ctx.prior_complete, src_aux_sha, cur_aux_sha)
        if aux_skip.skip:
            aux_hashes[f.dest_rel] = cur_aux_sha
            aux_skipped += 1
            continue
        # B2: _copy_one is long-path-safe (extended-length prefix on both src+dst
        # and the makedirs), matching the DLL / mutable-surface copies; a bare
        # shutil.copyfile overflows MAX_PATH for a deep aux path under a long
        # umbrella root.
        _copy_one(f.source, dst)
        sha = sha256_file(dst)
        aux_hashes[f.dest_rel] = sha
        log(ctx, "Info", "Deploy", "aux %s <- %s (%s) sha256=%s" % (f.dest_rel, f.source, f.origin, sha))
    if aux_skipped:
        log(ctx, "Info", "Deploy", "SF9 skipped %d aux copies (hash-match)" % aux_skipped)
    for dest in payload.missing_optional:
        log(ctx, "Warn", "Deploy", "optional Parsek aux payload %s absent from all sources; skipped" % dest)
    info["auxFiles"] = aux_hashes
    return info


def phase_install(ctx: ProvisionContext) -> None:
    """Extract the stack payloads into the instance GameData and hash every
    installed DLL into the manifest (design INSTALL)."""
    stack = ctx.profile.get("stackComponents", []) or []
    if not stack:
        # An empty stack list is almost always a profile-TOML bug (a
        # stackComponents key misplaced under a [[table]] header binds to that
        # table and leaves the top-level list empty -- BLOCKER 1). Warn loudly
        # rather than silently installing nothing.
        log(ctx, "Warn", "Install",
            "profile '%s' has NO stackComponents -- no stack payloads would be installed. "
            "Check the profile TOML: a bare key placed after a [[table]] header silently "
            "binds to that table and empties the top-level list." % ctx.profile_name)
        return
    for name in stack:
        if name == "parsek":
            continue
        log(ctx, "Info", "Install", "stack component %s -> %s (hash into manifest)"
            % (name, provlib.stack_component_install_folder(name)))
    if ctx.dry_run:
        log(ctx, "Info", "Install",
            "would extract kRPC into GameData/kRPC, drop TestingTools.dll + KRPC.MechJeb.dll "
            "alongside, extract MechJeb2 into GameData/MechJeb2, hash every DLL, "
            "record a per-component installed-file inventory (SF10), and on a re-run "
            "hash-short-circuit any component already installed identically (SF9)")
        if "krpc" in stack:
            log(ctx, "Info", "Install",
                "would stamp GameData/kRPC/PluginData/settings.cfg "
                "(autoStartServers=True/autoAcceptConnections=True/confirmRemoveClient=False) "
                "and record krpcSettingsSha256")
        return
    _install_stack(ctx, stack)


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
            # NOTE: autoLoaderAbsent is deliberately NOT recorded here. It is a
            # claim only the BUILD-TT reflection smoke over the ACTUAL built
            # assembly can back (S-4 -- AutoLoadGame absent + the six capability
            # RPCs present); a dry-run planner has no built bytes to assert it.
            "testingtools": {"kind": "built-shim", "krpcRef": tt.get("sourceRepoRef"),
                             "sourceFiles": list(tt.get("sourceFiles", [])),
                             "capabilities": list(tt.get("capabilities", [])),
                             "bootChannel": tt.get("bootChannel"),
                             "missing": list(tt.get("missingVsMaster", []))},
            "mechjeb2": {"kind": "release", "buildNumber": mj.get("buildNumber"),
                         "sha256": mj.get("sha256")},
            "krpc_mechjeb": {"kind": "git", "fork": kmj.get("fork"), "tag": kmj.get("tag"),
                             "commit": resolved.get("krpc_mechjeb", kmj.get("commit")),
                             "pairedKrpc": kmj.get("pairedKrpcTag")},
            "parsek": parsek_info,
        },
        "devSourcedMods": dev_status,
        # SF10: per-stack-component installed-file inventory (relpath + sha256).
        # Top-level (NOT under "components", an admission key) so the harness admit
        # path is unaffected; VERIFY does the inventory-vs-disk added/missing diff.
        "componentInventories": getattr(ctx, "component_inventories", {}),
        "junctionTargets": junctions,
        "settingsDeltasApplied": deltas,
        "settingsBaseSha256": getattr(ctx, "settings_base_sha", None),
        "settingsFinalSha256": getattr(ctx, "settings_final_sha", None),
        # F3: hash of the stamped kRPC PluginData/settings.cfg, re-hashed in VERIFY
        # (mirrors settingsFinalSha256) so a later manual kRPC settings edit drifts.
        "krpcSettingsSha256": getattr(ctx, "krpc_settings_sha", None),
        "buildId64Sha256": _buildid64_sha(ctx),
        # SF9/S2: instance KSP_x64_Data/Managed (fileCount, bytes). The CLONE
        # mutable-surface skip re-stats this and refuses to skip on a drift that
        # buildID64 alone would miss (a deleted / resized stock Managed DLL).
        "mutableSurfaceManagedStat": _managed_stat_dict(ctx),
    }
    # Merge the live per-component installed-DLL hashes INSTALL/BUILD-TT computed
    # (installedDlls per kRPC/MechJeb2, dllSha256 per TestingTools/KRPC.MechJeb).
    # These are the "real hashes replace the planned-copy markers" (design
    # MANIFEST); the field names (dllSha256, installedDlls) match what
    # hlib.admit_instance / build_expected_admission diff.
    for comp, fields in getattr(ctx, "component_extra", {}).items():
        if comp in manifest["components"]:
            manifest["components"][comp].update({k: v for k, v in fields.items() if v is not None})
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
    """Re-read the instance from scratch and cross-check the just-written manifest.

    Checks (design VERIFY): junction realpath resolution (EC-8), every recorded
    component DLL hash vs on-disk (EC-9/EC-10 mid-run clobber), the Parsek UTF-16
    signature re-grep, settingsFinalSha256 (N-4), and buildId64Sha256 (N-5). Each
    mismatch is recorded as a structured drift field on ``ctx.verify_drift`` so
    --repair can converge only the drifted component. Returns True == no drift;
    on success the .provision-incomplete marker is cleared LAST.
    """
    if ctx.dry_run:
        log(ctx, "Info", "Verify",
            "would re-read instance: per-component DLL hashes vs manifest, Parsek "
            "UTF-16 grep, junction resolution, settingsFinalSha256 re-hash, "
            "krpcSettingsSha256 re-hash, buildId64Sha256 re-hash, per-component "
            "installed-file inventory diff (added-file detection, PluginData-tolerant, SF10)")
        return True

    drift: List[provlib.ManifestDiff] = []

    def _drift(field: str, expected, actual) -> None:
        drift.append(provlib.ManifestDiff(field, expected, actual, "changed"))

    # Junction resolution (EC-8). Probe the LINK (realpath), not just the
    # target's existence: a junction reports islink()==False, and a deleted or
    # repointed junction must fail even when the old target still exists.
    def _resolve_link(link_key: str) -> Optional[str]:
        link_abs = os.path.join(ctx.instance_dir, link_key)
        # CORRECTED (second live smoke): the earlier "exists() lies about
        # junctions" reading was wrong - exists() was CORRECTLY reporting a
        # junction whose target does not exist (the StreamingAssets entry
        # pointed at a dir KSP 1.12.5 does not ship). Proper probe: lstat for
        # LINK presence (deleted link -> None), then exists() for TARGET
        # reachability (dangling junction -> None), then realpath for the
        # repointed-target compare.
        try:
            os.lstat(link_abs)
        except OSError:
            return None
        if not os.path.exists(link_abs):
            return None
        return os.path.realpath(link_abs)
    dangling = provlib.verify_junctions(manifest, _resolve_link)
    if dangling:
        for link in dangling:
            log(ctx, "Error", "Verify", "junction DANGLING %s (EC-8)" % link)
            _drift("junctionTargets.%s" % link, manifest.get("junctionTargets", {}).get(link), None)
    else:
        log(ctx, "Info", "Verify", "junctions resolve OK (%d)" % len(manifest.get("junctionTargets", {})))

    # Parsek DLL hash + UTF-16 grep (EC-9/EC-10 mid-run clobber).
    parsek = manifest["components"].get("parsek", {})
    installed = os.path.join(ctx.parsek_gamedata, "Plugins", "Parsek.dll")
    if parsek.get("dllSha256") and os.path.isfile(installed):
        with open(installed, "rb") as fh:
            dll_bytes = fh.read()
        cur = sha256_bytes(dll_bytes)
        match = cur == parsek["dllSha256"]
        log(ctx, "Info" if match else "Error", "Verify",
            "parsek dll on-disk sha256 %s manifest" % ("==" if match else "!="))
        if not match:
            _drift("components.parsek.dllSha256", parsek["dllSha256"], cur)
        for s, exp in (parsek.get("signatureStrings", {}) or {}).items():
            n = provlib.count_utf16(dll_bytes, s)
            ok_sig = n == exp
            log(ctx, "Info" if ok_sig else "Error", "Verify",
                "parsek UTF-16 grep string=%s count=%d expected=%d %s"
                % (s, n, exp, "OK" if ok_sig else "DRIFT"))
            if not ok_sig:
                _drift("components.parsek.signatureStrings.%s" % s, exp, n)

    # Parsek aux payload (version + toolbar textures) re-hash. These live in the
    # GameData/Parsek payload independent of the DLL; a missing texture is what
    # floods ToolbarControl.OnGUI, so a dropped/edited aux file must drift.
    for dest_rel, exp in (parsek.get("auxFiles", {}) or {}).items():
        aux_path = os.path.join(ctx.parsek_gamedata, *dest_rel.split("/"))
        if not os.path.isfile(aux_path):
            log(ctx, "Error", "Verify", "parsek aux %s MISSING" % dest_rel)
            _drift("components.parsek.auxFiles.%s" % dest_rel, exp, None)
            continue
        cur = sha256_file(aux_path)
        ok_aux = cur == exp
        log(ctx, "Info" if ok_aux else "Error", "Verify",
            "parsek aux %s on-disk sha256 %s manifest" % (dest_rel, "==" if ok_aux else "!="))
        if not ok_aux:
            _drift("components.parsek.auxFiles.%s" % dest_rel, exp, cur)

    # Per-component installed DLL hashes (INSTALL / BUILD-TT).
    for comp in ("krpc", "testingtools", "krpc_mechjeb", "mechjeb2"):
        cdata = manifest["components"].get(comp, {})
        for dll_name, exp_sha in (cdata.get("installedDlls", {}) or {}).items():
            _verify_component_dll(ctx, comp, dll_name, exp_sha, _drift)
        if cdata.get("dllSha256"):
            _verify_component_named_dll(ctx, comp, cdata["dllSha256"], _drift)

    # settingsFinalSha256 re-hash (N-4).
    final = manifest.get("settingsFinalSha256")
    inst_settings = os.path.join(ctx.instance_dir, "settings.cfg")
    if final and os.path.isfile(inst_settings):
        cur = sha256_file(inst_settings)
        match = cur == final
        log(ctx, "Info" if match else "Error", "Verify",
            "settingsFinalSha256 re-hash %s" % ("OK" if match else "DRIFT"))
        if not match:
            _drift("settingsDeltasApplied", final, cur)

    # krpcSettingsSha256 re-hash (F3): the stamped kRPC PluginData/settings.cfg
    # must still carry the hands-free keys; a manual kRPC settings change drifts.
    krpc_settings = manifest.get("krpcSettingsSha256")
    krpc_settings_path = _krpc_settings_path(ctx)
    if krpc_settings and os.path.isfile(krpc_settings_path):
        cur = sha256_file(krpc_settings_path)
        match = cur == krpc_settings
        log(ctx, "Info" if match else "Error", "Verify",
            "krpcSettingsSha256 re-hash %s" % ("OK" if match else "DRIFT"))
        if not match:
            _drift("krpcSettingsSha256", krpc_settings, cur)

    # buildId64Sha256 re-hash (N-5): pins the ACTUAL instance KSP version.
    b64 = manifest.get("buildId64Sha256")
    b64_path = os.path.join(ctx.instance_dir, "buildID64.txt")
    if b64 and os.path.isfile(b64_path):
        cur = sha256_file(b64_path)
        match = cur == b64
        log(ctx, "Info" if match else "Error", "Verify",
            "buildId64Sha256 re-hash %s" % ("OK" if match else "DRIFT"))
        if not match:
            _drift("buildId64Sha256", b64, cur)

    # Dev-sourced mod content-tree hashes (BLOCKER 1). Re-hash each recorded
    # instance GameData/<mod> and compare to the manifest: this is the ONLY
    # instance-side check on a dev-sourced copy, catching a swapped DLL or an
    # injected extra file that a plain re-copy would miss. Non-hash statuses
    # ("absent-source", the dry-run "planned-copy") are skipped.
    for name, recorded in (manifest.get("devSourcedMods", {}) or {}).items():
        if not recorded or recorded in ("absent-source", "planned-copy"):
            continue
        modpath = os.path.join(ctx.instance_dir, "GameData", name)
        if not os.path.exists(modpath):
            log(ctx, "Error", "Verify", "dev-sourced mod %s MISSING from instance GameData" % name)
            _drift("devSourcedMods.%s" % name, recorded, None)
            continue
        cur = _content_tree_hash(modpath, ctx)
        match = cur == recorded
        log(ctx, "Info" if match else "Error", "Verify",
            "dev-sourced mod %s content-hash %s manifest" % (name, "==" if match else "!="))
        if not match:
            _drift("devSourcedMods.%s" % name, recorded, cur)

    # SF10: per-stack-component installed-file inventory diff. Re-scan each install
    # folder and diff it against the recorded inventory: a recorded file missing or
    # changed drifts, and a file the inventory never recorded is an ADDED drift
    # (the gap this closes -- a DLL dropped into GameData/kRPC beside the hashed
    # ones). PluginData paths are tolerated (runtime-writable). An OLD manifest
    # with no inventories is amber-tolerated (verifies as before -- re-provision to
    # arm the added-file check).
    _verify_inventories(ctx, manifest, drift)

    ctx.verify_drift = drift  # type: ignore[attr-defined]
    ok = not drift
    if ok:
        # Marker cleared LAST: a present manifest + no marker == a completed,
        # verified provision the harness may admit (design EC-6).
        _clear_incomplete_marker(ctx)
    log(ctx, "Info" if ok else "Error", "Verify",
        "instance=%s result=%s drift=%d" % (ctx.profile_name, "OK" if ok else "DRIFT", len(drift)))
    return ok


def _dll_install_path(ctx: ProvisionContext, comp: str, dll_name: str) -> str:
    """Resolve where a stack component's DLL lives in the instance GameData.
    kRPC / TestingTools / KRPC.MechJeb share GameData/kRPC; MechJeb2 its own.
    Layouts differ per component (kRPC ships DLLs flat; MechJeb2 under
    Plugins/ - the first live smoke false-drifted all four MechJeb2 DLLs
    when this resolver assumed flat), so probe flat, then Plugins/, then a
    bounded recursive search of the component subtree."""
    sub = "MechJeb2" if comp == "mechjeb2" else "kRPC"
    root = os.path.join(ctx.instance_dir, "GameData", sub)
    flat = os.path.join(root, dll_name)
    if os.path.isfile(flat):
        return flat
    plugins = os.path.join(root, "Plugins", dll_name)
    if os.path.isfile(plugins):
        return plugins
    for walk_root, _dirs, files in os.walk(root):
        if dll_name in files:
            return os.path.join(walk_root, dll_name)
    return flat  # missing: caller reports None-hash drift against this path


def _verify_component_dll(ctx, comp, dll_name, exp_sha, drift_fn) -> None:
    path = _dll_install_path(ctx, comp, dll_name)
    cur = sha256_file(path) if os.path.isfile(path) else None
    match = cur == exp_sha
    log(ctx, "Info" if match else "Error", "Verify",
        "%s %s on-disk sha256 %s manifest" % (comp, dll_name, "==" if match else "!="))
    if not match:
        drift_fn("components.%s.installedDlls.%s" % (comp, dll_name), exp_sha, cur)


def _scan_folder_hashes(ctx: ProvisionContext, folder_rel: str, folder_abs: str) -> Dict[str, str]:
    """SF10: instance-relative path -> sha256 for every file under an install
    folder, skipping reparse-point (junction) subdirs. Keys match the recorded
    inventory's instance-relative form (``GameData/kRPC/...``)."""
    out: Dict[str, str] = {}
    if not os.path.isdir(folder_abs):
        return out
    for r, dirs, names in os.walk(folder_abs):
        _prune_reparse_dirs(ctx, r, dirs)
        for n in names:
            p = os.path.join(r, n)
            rel_in = os.path.relpath(p, folder_abs).replace("\\", "/")
            out["%s/%s" % (folder_rel, rel_in)] = sha256_file(p)
    return out


def _verify_inventories(ctx: ProvisionContext, manifest: Dict,
                        drift: List["provlib.ManifestDiff"]) -> None:
    """SF10 VERIFY step: diff each install folder's on-disk file set against the
    recorded per-component inventory (grouped by shared folder). Missing/changed
    authored files and added non-PluginData files drift, attributed to the
    folder's primary component so ``--repair`` re-installs it, each carrying its
    real diff kind (missing / changed / added). An old manifest without
    inventories is amber-tolerated (batch-summary logging)."""
    inventories = manifest.get("componentInventories")
    if inventories is None:
        log(ctx, "Amber", "Verify",
            "component inventory absent from manifest - re-provision to arm the added-file check (SF10)")
        return
    folders = provlib.group_inventory_by_folder(inventories)
    for folder_rel, (owners, recorded_map) in folders.items():
        folder_abs = os.path.join(ctx.instance_dir, *folder_rel.split("/"))
        current_map = _scan_folder_hashes(ctx, folder_rel, folder_abs)
        diffs = provlib.diff_inventory(recorded_map, current_map)
        owner = owners[0] if owners else "unknown"
        if not diffs:
            log(ctx, "Info", "Verify",
                "inventory %s OK (%d recorded files, owners=%s)"
                % (folder_rel, len(recorded_map), ",".join(owners) or "-"))
            continue
        n_missing = sum(1 for d in diffs if d.kind == "missing")
        n_changed = sum(1 for d in diffs if d.kind == "changed")
        n_added = sum(1 for d in diffs if d.kind == "added")
        log(ctx, "Error", "Verify",
            "inventory %s DRIFT missing=%d changed=%d added=%d (owner=%s)"
            % (folder_rel, n_missing, n_changed, n_added, owner))
        # B3: cap the per-file Error lines so a wholesale folder drift (every file
        # missing / changed) cannot flood the log; the summary line above already
        # carries the totals, and every diff still lands in the drift list below so
        # --repair convergence is unaffected.
        drift_log_cap = 10
        for i, d in enumerate(diffs):
            recorded_hash = recorded_map.get(d.rel)
            actual_hash = current_map.get(d.rel)
            if i < drift_log_cap:
                log(ctx, "Error", "Verify", "inventory %s %s %s" % (folder_rel, d.kind, d.rel))
            elif i == drift_log_cap:
                log(ctx, "Error", "Verify",
                    "inventory %s ... and %d more drift line(s) suppressed (see summary above)"
                    % (folder_rel, len(diffs) - drift_log_cap))
            drift.append(provlib.ManifestDiff(
                "components.%s.inventory.%s" % (owner, d.rel),
                recorded_hash if d.kind != "added" else None,
                actual_hash if d.kind != "missing" else None,
                d.kind))


def _verify_component_named_dll(ctx, comp, exp_sha, drift_fn) -> None:
    dll_name = {"testingtools": "TestingTools.dll", "krpc_mechjeb": "KRPC.MechJeb.dll"}.get(comp)
    if not dll_name:
        return
    path = _dll_install_path(ctx, comp, dll_name)
    cur = sha256_file(path) if os.path.isfile(path) else None
    match = cur == exp_sha
    log(ctx, "Info" if match else "Error", "Verify",
        "%s %s on-disk sha256 %s manifest" % (comp, dll_name, "==" if match else "!="))
    if not match:
        drift_fn("components.%s.dllSha256" % comp, exp_sha, cur)


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


def _assert_krpc_zip_layout(ctx: ProvisionContext, data: bytes) -> bool:
    """GT-5: assert the kRPC release zip carries the compile DLLs and does NOT
    ship TestingTools.dll. SF4: a failure ABORTS (EC-3) so DOWNLOAD never
    proceeds to cache + install a zip with the wrong layout. Returns True on a
    clean layout, False after aborting."""
    import io
    import zipfile
    krpc = ctx.pins.get("krpc", {})
    with zipfile.ZipFile(io.BytesIO(data)) as zf:
        names = [n.lower() for n in zf.namelist()]
    missing: List[str] = []
    for must in krpc.get("releaseCompileDlls", []):
        present = any(must.lower() in n for n in names)
        log(ctx, "Info" if present else "Error", "Download",
            "GT-5 zip contains %s %s" % (must, "OK" if present else "MISSING"))
        if not present:
            missing.append(must)
    forbidden_present: List[str] = []
    for forbidden in krpc.get("mustNotContain", []):
        present = any(forbidden.lower() in n for n in names)
        log(ctx, "Error" if present else "Info", "Download",
            "GT-5 zip must-not-contain %s %s" % (forbidden, "FAIL" if present else "OK"))
        if present:
            forbidden_present.append(forbidden)
    if missing or forbidden_present:
        abort(ctx, "Download", "EC-3",
              "kRPC zip layout wrong (GT-5): missing=%s forbidden-present=%s"
              % (",".join(missing) or "-", ",".join(forbidden_present) or "-"))
        return False
    return True


def _copy_file(src: str, dst: str) -> None:
    import shutil
    shutil.copyfile(src, dst)


# --- CLONE live helpers (design CLONE / EC-6 / EC-7). Never run under dry-run. ---


def _long(path: str) -> str:
    """Windows extended-length form of an absolute path (EC-7); a no-op elsewhere
    or when the path is already prefixed. Deep KSP asset trees under a long
    umbrella root overflow MAX_PATH without it."""
    if os.name == "nt":
        return provlib.to_extended_length_path(os.path.abspath(path))
    return path


def _find_ksp_data_dir(dev_install: str) -> str:
    for cand in ("KSP_x64_Data", "KSP_Data"):
        if os.path.isdir(os.path.join(dev_install, cand)):
            return cand
    return "KSP_x64_Data"


def _copy_one(src: str, dst: str) -> None:
    import shutil
    os.makedirs(_long(os.path.dirname(dst)), exist_ok=True)
    shutil.copyfile(_long(src), _long(dst))


def _is_reparse_point(path: str) -> bool:
    """SF6(b): True if ``path`` is a reparse point -- a junction or symlink dir
    that a copy / size / hash walk must NOT descend into (it points at a stock
    tree junctioned separately, or could form a loop). Uses the Windows
    ``st_reparse_tag`` when available, else a symlink / realpath-differs
    fallback."""
    import stat as _stat
    try:
        st = os.lstat(path)
    except OSError:
        return False
    if getattr(st, "st_reparse_tag", 0):
        return True
    if _stat.S_ISLNK(st.st_mode):
        return True
    try:
        return os.path.isdir(path) and os.path.realpath(path) != os.path.abspath(path)
    except OSError:
        return False


def _prune_reparse_dirs(ctx: Optional[ProvisionContext], root: str, dirs: List[str]) -> None:
    """Remove reparse-point (junction / symlink) subdirs from an os.walk ``dirs``
    list IN PLACE (SF6b), logging each skip when a ctx is available."""
    keep: List[str] = []
    for d in dirs:
        full = os.path.join(root, d)
        if _is_reparse_point(full):
            if ctx is not None:
                log(ctx, "Verbose", "Clone", "skip reparse-point dir %s" % full)
        else:
            keep.append(d)
    dirs[:] = keep


def _copy_dir(ctx: ProvisionContext, src: str, dst: str, skip_top=None):
    """Recursive tree copy honoring extended-length paths, optionally skipping
    top-level child names (used to junction StreamingAssets instead of copying
    it) and always skipping reparse-point subdirs (SF6b). Returns (files, bytes)."""
    files = 0
    total = 0
    for root, dirs, names in os.walk(src):
        rel = os.path.relpath(root, src)
        if rel == "." and skip_top is not None:
            dirs[:] = [d for d in dirs if not skip_top(d)]
        _prune_reparse_dirs(ctx, root, dirs)
        target_root = dst if rel == "." else os.path.join(dst, rel)
        os.makedirs(_long(target_root), exist_ok=True)
        for n in names:
            _copy_one(os.path.join(root, n), os.path.join(target_root, n))
            files += 1
            try:
                total += os.path.getsize(os.path.join(root, n))
            except OSError:
                pass
    return files, total


def _dir_bytes(root: str, skip_top=None, ctx: Optional[ProvisionContext] = None) -> int:
    total = 0
    for r, dirs, names in os.walk(root):
        if skip_top is not None and os.path.relpath(r, root) == ".":
            dirs[:] = [d for d in dirs if not skip_top(d)]
        _prune_reparse_dirs(ctx, r, dirs)
        for n in names:
            try:
                total += os.path.getsize(os.path.join(r, n))
            except OSError:
                pass
    return total


def _measure_copy_bytes(ctx: ProvisionContext, copy_mods: List[str]) -> int:
    """Sum the bytes CLONE will actually copy (mirrors the copy plan): the
    top-level copy set minus the junctioned StreamingAssets, plus each
    dev-sourced mod folder. Junctioned stock trees cost ~0."""
    dev = ctx.dev_install
    ksp_data = _find_ksp_data_dir(dev)
    total = 0
    for name in os.listdir(dev):
        disp = provlib.clone_toplevel_disposition(name, ksp_data)
        src = os.path.join(dev, name)
        if disp == "copy-tree-except-junction":
            total += _dir_bytes(src, skip_top=provlib.ksp_data_entry_is_junction, ctx=ctx)
        elif disp == "copy":
            total += _dir_bytes(src, ctx=ctx) if os.path.isdir(src) else _safe_size(src)
    for name in copy_mods:
        src = os.path.join(dev, "GameData", name)
        if os.path.isdir(src):
            total += _dir_bytes(src, ctx=ctx)
        elif os.path.isfile(src):
            total += _safe_size(src)
    return total


def _safe_size(path: str) -> int:
    try:
        return os.path.getsize(path)
    except OSError:
        return 0


def _precheck_free_space(ctx: ProvisionContext) -> bool:
    """EC-6: refuse before CLONE if the intended copy would not fit."""
    import shutil
    copy_mods = list(ctx.profile.get("devSourcedMods", []) or [])
    for opt in ctx.profile.get("optionalMods", []) or []:
        if os.path.isdir(os.path.join(ctx.dev_install, "GameData", opt.get("name", ""))):
            copy_mods.append(opt.get("name", ""))
    est = _measure_copy_bytes(ctx, copy_mods)
    parent = os.path.dirname(os.path.abspath(ctx.instance_dir)) or ctx.instance_dir
    os.makedirs(parent, exist_ok=True)
    free = shutil.disk_usage(parent).free
    margin = 512 * 1024 * 1024
    if provlib.is_over_budget(est, free, margin):
        abort(ctx, "Clone", "EC-6",
              "insufficient free space: estimate=%d margin=%d free=%d" % (est, margin, free))
        return False
    log(ctx, "Info", "Clone", "free-space pre-check estimate=%d free=%d OK" % (est, free))
    return True


def _clone_mutable_surface(ctx: ProvisionContext) -> None:
    dev = ctx.dev_install
    inst = ctx.instance_dir
    ksp_data = _find_ksp_data_dir(dev)
    os.makedirs(_long(inst), exist_ok=True)
    files = 0
    total = 0
    for name in sorted(os.listdir(dev)):
        disp = provlib.clone_toplevel_disposition(name, ksp_data)
        if disp in ("skip", "build-gamedata"):
            continue
        src = os.path.join(dev, name)
        dst = os.path.join(inst, name)
        if disp == "copy-tree-except-junction":
            f, b = _copy_dir(ctx, src, dst, skip_top=provlib.ksp_data_entry_is_junction)
        elif os.path.isdir(src):
            f, b = _copy_dir(ctx, src, dst)
        else:
            _copy_one(src, dst)
            f, b = 1, _safe_size(src)
        files += f
        total += b
    log(ctx, "Info", "Clone", "mutable surface copied files=%d bytes=%d" % (files, total))


def _make_junction(link_abs: str, target_abs: str) -> subprocess.CompletedProcess:
    # Directory junctions need no admin. Remove any pre-existing link first for
    # idempotency (rmdir on a junction removes the link, never the target).
    # NOTE: os.path.exists/isdir/islink can ALL report False for an existing
    # junction (observed on the second live smoke: lstat showed reparse tag
    # 0xA0000003 while exists() was False, so the conditional pre-clear
    # skipped and mklink failed with "already exists"). rmdir unconditionally
    # and ignore absence.
    try:
        os.rmdir(link_abs)
    except OSError:
        pass
    # cmd parses forward slashes in the link/target as switches ("Invalid
    # switch" seen on the first live smoke when mixed separators reached
    # mklink), so normalize both to backslashes before invoking it.
    return subprocess.run(
        ["cmd", "/c", "mklink", "/J",
         os.path.normpath(link_abs), os.path.normpath(target_abs)],
        capture_output=True, text=True,
    )


def _create_junctions(ctx: ProvisionContext, junctions: Dict[str, str]) -> None:
    for link_rel, target in junctions.items():
        link_abs = os.path.join(ctx.instance_dir, link_rel)
        os.makedirs(_long(os.path.dirname(link_abs)), exist_ok=True)
        res = _make_junction(link_abs, target)
        if res.returncode != 0:
            abort(ctx, "Clone", "EC-8",
                  "junction failed %s -> %s: %s" % (link_rel, target, (res.stderr or res.stdout).strip()))
            return
        log(ctx, "Info", "Clone", "junctioned %s -> %s" % (link_rel, target))


def _content_tree_hash(root: str, ctx: Optional[ProvisionContext] = None) -> str:
    """Deterministic content hash of a dev-sourced mod (folder or single file),
    via the pure canonical digest input (EC-3). Reparse-point subdirs are pruned
    (SF6b) so a junction inside a dev-sourced mod cannot pull a stock tree into
    the hash. Runtime-writable dirs (the PluginData convention) are pruned too:
    the drift contract covers the authored payload, and the game writes caches
    there on every launch (provlib.is_runtime_writable_dir)."""
    entries: List[tuple] = []
    if os.path.isfile(root):
        entries.append((os.path.basename(root), sha256_file(root)))
    else:
        for r, dirs, names in os.walk(root):
            _prune_reparse_dirs(ctx, r, dirs)
            dirs[:] = [d for d in dirs if not provlib.is_runtime_writable_dir(d)]
            for n in names:
                p = os.path.join(r, n)
                entries.append((os.path.relpath(p, root), sha256_file(p)))
    return sha256_bytes(provlib.canonical_tree_digest_input(entries).encode("utf-8"))


def _recheck_alias_resolved(ctx: ProvisionContext) -> bool:
    """SF6(a): re-run the EC-16 alias guard on the REALPATH-resolved instance dir
    (and its GameData child, when present) versus the resolved dev install. The
    PREFLIGHT check is string-level; a junction/symlink could make a clean-looking
    instanceDir actually resolve INTO the read-only dev install. Aborts EC-16 and
    returns False on any equality/nesting relationship, else True."""
    dev_real = os.path.normcase(os.path.normpath(os.path.realpath(ctx.dev_install)))
    targets = [ctx.instance_dir]
    gd = os.path.join(ctx.instance_dir, "GameData")
    if os.path.exists(gd):
        targets.append(gd)
    for t in targets:
        real = os.path.normcase(os.path.normpath(os.path.realpath(t)))
        alias = provlib.check_instance_dir_alias(real, dev_real, ctx.profile.get("instanceDir", ""))
        if alias.reason in ("equals-dev-install", "nested-in-dev-install",
                            "dev-install-nested-in-instance"):
            abort(ctx, "Clone", "EC-16",
                  "resolved instance path aliases dev install (%s): resolved=%s dev=%s"
                  % (alias.reason, real, dev_real))
            return False
    log(ctx, "Info", "Clone", "EC-16 resolved-path alias re-check OK (dev=%s)" % dev_real)
    return True


def _incomplete_marker_path(ctx: ProvisionContext) -> str:
    return os.path.join(ctx.parsek_gamedata, provlib.PROVISION_INCOMPLETE_MARKER)


def _write_incomplete_marker(ctx: ProvisionContext) -> None:
    os.makedirs(ctx.parsek_gamedata, exist_ok=True)
    with open(_incomplete_marker_path(ctx), "w", encoding="utf-8") as fh:
        fh.write("provisioning in progress pid=%d %s\n" % (os.getpid(), _utcnow_iso()))
    log(ctx, "Info", "Clone", "wrote %s (cleared on VERIFY success)" % provlib.PROVISION_INCOMPLETE_MARKER)


def _clear_incomplete_marker(ctx: ProvisionContext) -> None:
    path = _incomplete_marker_path(ctx)
    if os.path.isfile(path):
        try:
            os.remove(path)
            log(ctx, "Info", "Verify", "cleared %s" % provlib.PROVISION_INCOMPLETE_MARKER)
        except OSError as exc:
            log(ctx, "Warn", "Verify", "could not clear incomplete marker: %s" % exc)


# --- BUILD-TT live helpers (design BUILD-TT). Never run under dry-run. ---


def _cached_zip_path(ctx: ProvisionContext, comp: str, url_key: str) -> Optional[str]:
    url = ctx.pins.get(comp, {}).get(url_key)
    if not url or provlib.is_open_pin(url):
        return None
    return os.path.join(CACHE_DIR, os.path.basename(url))


def _git_show_file(clone_dir: str, ref: str, repo_path: str) -> Optional[bytes]:
    """Read one file's bytes at a git ref, read-only (never checks the clone
    out). ``git show <ref>:<path>``; returns None on failure."""
    out = subprocess.run(
        ["git", "-C", clone_dir, "show", "%s:%s" % (ref, repo_path)],
        capture_output=True,
    )
    if out.returncode != 0:
        return None
    return out.stdout


def _extract_krpc_refs(ctx: ProvisionContext, dest: str) -> bool:
    """Extract the kRPC compile-reference DLLs from the cached release zip into
    ``dest`` so BUILD-TT can HintPath them. Returns False (and aborts) if the
    cached zip is missing (DOWNLOAD must run first)."""
    import zipfile
    zip_path = _cached_zip_path(ctx, "krpc", "releaseZipUrl")
    if not zip_path or not os.path.isfile(zip_path):
        abort(ctx, "Build-TT", "EC-4",
              "kRPC release zip not cached at %s (DOWNLOAD must run first)" % zip_path)
        return False
    os.makedirs(dest, exist_ok=True)
    refs = provlib.TESTINGTOOLS_KRPC_REFS
    with zipfile.ZipFile(zip_path) as zf:
        for name in zf.namelist():
            base = name.rsplit("/", 1)[-1]
            if base.endswith(".dll") and any(base == "%s.dll" % r for r in refs):
                with zf.open(name) as src, open(os.path.join(dest, base), "wb") as dst:
                    dst.write(src.read())
    missing = [r for r in refs if not os.path.isfile(os.path.join(dest, "%s.dll" % r))]
    if missing:
        abort(ctx, "Build-TT", "EC-4",
              "kRPC zip missing compile refs: %s" % ", ".join(missing))
        return False
    return True


def _build_testingtools(ctx: ProvisionContext, sel, tt: Dict, ref: str,
                        krpc_clone: Optional[str] = None) -> None:
    # Module-owned kRPC source clone (.cache/krpc-src or --krpc-src), resolved by
    # phase_build_tt via _ensure_git_source. Never the umbrella mods/ clone.
    if krpc_clone is None:
        krpc_clone = _ensure_git_source(ctx, "krpc", ctx.krpc_src_override)
    if not krpc_clone:
        abort(ctx, "Build-TT", "EC-4", "kRPC source clone not resolved (see Source phase)")
        return
    subdir = tt.get("sourceSubdir", "tools/TestingTools/src")
    build_dir = os.path.join(CACHE_DIR, "testingtools-build")
    src_dir = os.path.join(build_dir, "src")
    refs_dir = os.path.join(CACHE_DIR, "krpc-refs", "GameData", "kRPC")
    os.makedirs(src_dir, exist_ok=True)

    # Export the 2 shim sources at the pin (git show, never a working-tree read).
    for fname in sel.included:
        data = _git_show_file(krpc_clone, ref, "%s/%s" % (subdir, fname))
        if data is None:
            abort(ctx, "Build-TT", "EC-4", "git show %s:%s/%s failed" % (ref, subdir, fname))
            return
        with open(os.path.join(src_dir, fname), "wb") as fh:
            fh.write(data)
    log(ctx, "Info", "Build-TT", "exported %s from %s@%s" % (",".join(sel.included), krpc_clone, ref))

    if not _extract_krpc_refs(ctx, refs_dir):
        return

    managed = os.path.join(ctx.dev_install, _find_ksp_data_dir(ctx.dev_install), "Managed")
    csproj = provlib.render_testingtools_shim_csproj(
        managed, refs_dir, list(sel.included),
        target_framework=tt.get("targetFramework", "net472"))
    with open(os.path.join(src_dir, "TestingTools.shim.csproj"), "w", encoding="utf-8") as fh:
        fh.write(csproj)
    with open(os.path.join(src_dir, "AssemblyInfo.cs"), "w", encoding="utf-8") as fh:
        fh.write(provlib.render_testingtools_assemblyinfo())

    out_dir = os.path.join(build_dir, "out")
    cmd = ["dotnet", "build", os.path.join(src_dir, "TestingTools.shim.csproj"),
           "-c", "Release", "-o", out_dir, "--nologo", "-v", "minimal"]
    log(ctx, "Info", "Build-TT", "build: %s" % " ".join(cmd))
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        tail = "\n".join((res.stdout or "").splitlines()[-25:])
        abort(ctx, "Build-TT", "EC-4", "dotnet build failed:\n%s" % tail)
        return

    built = os.path.join(out_dir, "TestingTools.dll")
    if not os.path.isfile(built):
        abort(ctx, "Build-TT", "EC-4", "build reported success but TestingTools.dll absent")
        return
    with open(built, "rb") as fh:
        dll_bytes = fh.read()
    assertion = provlib.evaluate_build_tt_assembly(dll_bytes)
    log(ctx, "Info" if assertion.ok else "Error", "Build-TT",
        "AutoLoadGame-absent %s (autoloadGame=%d testingToolsType=%s)"
        % ("OK" if assertion.ok else "FAIL", assertion.autoloadgame_count, assertion.has_testingtools_type))
    if not assertion.ok:
        abort(ctx, "Build-TT", "EC-4", "S-4 assertion failed: %s" % assertion.reason)
        return

    # N17: assert the six control-capability RPC method names are PRESENT in the
    # built assembly (UTF-8 metadata grep, same dependency-free reflection proxy).
    # A shim that compiled but exposes none of the capabilities the harness boots
    # against is as useless as one carrying AutoLoadGame.
    missing_caps = [c for c in provlib.TESTINGTOOLS_CAPABILITIES
                    if provlib.count_utf8(dll_bytes, c) == 0]
    log(ctx, "Info" if not missing_caps else "Error", "Build-TT",
        "capability RPCs present=%d/%d%s"
        % (len(provlib.TESTINGTOOLS_CAPABILITIES) - len(missing_caps),
           len(provlib.TESTINGTOOLS_CAPABILITIES),
           "" if not missing_caps else " MISSING=%s" % ",".join(missing_caps)))
    if missing_caps:
        abort(ctx, "Build-TT", "EC-4",
              "S-4 capability RPCs absent from built assembly: %s" % ",".join(missing_caps))
        return

    cached = os.path.join(CACHE_DIR, "TestingTools.dll")
    _copy_file(built, cached)
    tt_sha = sha256_bytes(dll_bytes)
    ctx.testingtools_dll = cached  # type: ignore[attr-defined]
    ctx.testingtools_sha = tt_sha  # type: ignore[attr-defined]
    log(ctx, "Info", "Build-TT", "build OK dll-sha256=%s cached=%s" % (tt_sha, cached))


# --- INSTALL live helpers (design INSTALL). Never run under dry-run. ---


def _extra(ctx: ProvisionContext) -> Dict[str, Dict]:
    """Per-component manifest fields (installed DLL hashes) populated by INSTALL
    and merged into the manifest by phase_manifest."""
    if not hasattr(ctx, "component_extra"):
        ctx.component_extra = {}  # type: ignore[attr-defined]
    return ctx.component_extra  # type: ignore[attr-defined]


def _inventories(ctx: ProvisionContext) -> Dict[str, List[Dict[str, str]]]:
    """SF10: the per-stack-component installed-file inventory populated by INSTALL
    and written to the manifest's top-level ``componentInventories`` by
    phase_manifest (deliberately OUTSIDE the admission projection, so the harness
    admit path is unchanged; VERIFY does the inventory-vs-disk diff)."""
    if not hasattr(ctx, "component_inventories"):
        ctx.component_inventories = {}  # type: ignore[attr-defined]
    return ctx.component_inventories  # type: ignore[attr-defined]


def _inventory_of(ctx: ProvisionContext, rel_paths: Sequence[str]) -> List[Dict[str, str]]:
    """SF10: build an installed-file inventory (instance-relative path + sha256)
    for the given written paths, sorted + deduped. A path whose file is absent is
    skipped (it will surface as a VERIFY 'missing' against the recorded set)."""
    seen: Dict[str, str] = {}
    for rel in rel_paths:
        norm = rel.replace("\\", "/")
        p = os.path.join(ctx.instance_dir, *norm.split("/"))
        if os.path.isfile(p):
            seen[norm] = sha256_file(p)
    return [{"rel": rel, "sha256": seen[rel]} for rel in sorted(seen)]


def _current_component_dll_map(ctx: ProvisionContext, comp: str,
                               names: Sequence[str]) -> Dict[str, Optional[str]]:
    """The current on-disk sha256 of each named installed DLL for a component
    (None when the DLL is absent), resolved through the layout-aware probe."""
    out: Dict[str, Optional[str]] = {}
    for n in names:
        p = _dll_install_path(ctx, comp, n)
        out[n] = sha256_file(p) if os.path.isfile(p) else None
    return out


def _install_pin_stable(ctx: ProvisionContext, comp: str) -> bool:
    """SF9 footgun gate: only skip a stack component's extraction when the prior
    manifest's pin identity for that component still equals the current pins.toml
    value -- a moved release pin (new sha256 / tag) must re-extract, never skip on
    a stale on-disk-equals-old-manifest match. TestingTools is gated by its
    source hash (the built shim), not a release pin, so it returns True here."""
    prior = _prior_component(ctx, comp)
    pin = ctx.pins.get(comp, {}) or {}
    if comp == "krpc":
        return bool(prior.get("sha256")) and prior.get("sha256") == pin.get("releaseZipSha256")
    if comp == "mechjeb2":
        return bool(prior.get("sha256")) and prior.get("sha256") == pin.get("sha256")
    if comp == "krpc_mechjeb":
        # N1: gate on BOTH the tag and the pinned commit. The tag is mutable (it
        # can be re-pointed to a new commit); requiring the peeled commit too
        # means a moved KRPC.MechJeb pin re-extracts, never skips on a stale tag.
        return (bool(prior.get("tag")) and prior.get("tag") == pin.get("tag")
                and bool(prior.get("commit")) and prior.get("commit") == pin.get("commit"))
    return True


def _extract_zip_plan(ctx: ProvisionContext, comp: str, zip_path: str) -> List[str]:
    """Extract a component's release zip into the instance per plan_zip_install;
    returns the list of instance-relative destinations written."""
    import zipfile
    written: List[str] = []
    with zipfile.ZipFile(zip_path) as zf:
        plan = provlib.plan_zip_install(comp, zf.namelist())
        for entry, dest_rel in plan:
            # SF5 zip-slip guard: a destination that escapes the instance
            # GameData/ (a ``../`` entry) ABORTS before any write.
            if provlib.gamedata_dest_escapes(dest_rel):
                abort(ctx, "Install", "EC-3",
                      "zip-slip: %s entry %s -> dest %s escapes instance GameData"
                      % (comp, entry, dest_rel))
                return written
            dest_abs = os.path.join(ctx.instance_dir, dest_rel.replace("/", os.sep))
            os.makedirs(_long(os.path.dirname(dest_abs)), exist_ok=True)
            with zf.open(entry) as src, open(_long(dest_abs), "wb") as dst:
                dst.write(src.read())
            written.append(dest_rel)
    return written


def _hash_installed_dlls(ctx: ProvisionContext, rel_paths: Sequence[str]) -> Dict[str, str]:
    out: Dict[str, str] = {}
    for rel in rel_paths:
        if not rel.lower().endswith(".dll"):
            continue
        p = os.path.join(ctx.instance_dir, rel.replace("/", os.sep))
        if os.path.isfile(p):
            out[os.path.basename(rel)] = sha256_file(p)
    return out


def _prior_inventory(ctx: ProvisionContext, comp: str) -> List[Dict[str, str]]:
    """The prior manifest's recorded installed-file inventory for a component
    (SF10), reused verbatim when SF9 skips that component's re-extraction."""
    return list(((ctx.prior_manifest or {}).get("componentInventories", {}) or {}).get(comp, []) or [])


def _inventory_of_folder(ctx: ProvisionContext, comp: str) -> List[Dict[str, str]]:
    """SF10 FALLBACK (item 2): build a component's installed-file inventory by SCANNING
    its on-disk install folder, sorted ``[{"rel","sha256"}]``. Used only when the prior
    manifest predates componentInventories (``_prior_inventory`` returns []): a hash-match
    SKIP that carried the empty prior forward would stamp the inventory present-but-EMPTY,
    the amber path (keyed on the whole map being None) would never fire, and the next real
    VERIFY over the live instance would red EVERY on-disk file as an ADDED-file drift storm.
    Scanning the folder ARMS a real inventory instead. For a component sharing a folder
    (kRPC), this records the folder SUPERSET; the VERIFY diff is folder-UNION so a superset
    causes no missing/added drift (the union already covers every on-disk file)."""
    folder_rel = provlib.stack_component_install_folder(comp)
    folder_abs = os.path.join(ctx.instance_dir, *folder_rel.split("/"))
    hashes = _scan_folder_hashes(ctx, folder_rel, folder_abs)
    return [{"rel": rel, "sha256": hashes[rel]} for rel in sorted(hashes)]


def _install_stack(ctx: ProvisionContext, stack: Sequence[str]) -> None:
    extra = _extra(ctx)
    inv = _inventories(ctx)

    # kRPC release zip -> GameData/kRPC/ (subtree as-is, GT-5).
    if "krpc" in stack:
        recorded_map = _prior_component(ctx, "krpc").get("installedDlls", {}) or {}
        expected = provlib.installed_map_digest(recorded_map) if recorded_map else None
        current = provlib.installed_map_digest(
            {k: v for k, v in _current_component_dll_map(ctx, "krpc", list(recorded_map)).items() if v})
        skip = provlib.decide_idempotent_skip(
            ctx.prior_complete and _install_pin_stable(ctx, "krpc"), expected, current)
        if skip.skip:
            extra["krpc"] = {"installedDlls": recorded_map}
            # item 2: arm the inventory from disk when the prior manifest predates it,
            # so the first post-M-A6.2 skip does not stamp an empty inventory (drift storm).
            inv["krpc"] = _prior_inventory(ctx, "krpc") or _inventory_of_folder(ctx, "krpc")
            # kRPC settings.cfg (PluginData): RE-STAMP rather than re-hash. The
            # stamp is idempotent, so a healthy file is byte-identical; a stale
            # or hand-edited file CONVERGES back to the provisioner contract
            # (hands-free keys + the default server node) instead of being
            # silently absorbed into the manifest. Absorbing the on-disk hash
            # here previously made the settings surface unrepairable through
            # the SF9 skip (first live B1 run, 2026-07-19: a pre-server-node
            # config could never be repaired without forcing a full re-extract).
            _stamp_krpc_settings(ctx)
            log(ctx, "Info", "Install", "SF9 skip kRPC extraction (hash-match, pin stable)")
        else:
            zip_path = _cached_zip_path(ctx, "krpc", "releaseZipUrl")
            if not zip_path or not os.path.isfile(zip_path):
                abort(ctx, "Install", "EC-4", "kRPC zip not cached: %s" % zip_path)
                return
            written = _extract_zip_plan(ctx, "krpc", zip_path)
            want = provlib.krpc_installed_dll_names(ctx.pins.get("krpc", {}))
            hashes = _hash_installed_dlls(ctx, [w for w in written if os.path.basename(w) in want])
            extra["krpc"] = {"installedDlls": hashes}
            log(ctx, "Info", "Install", "kRPC extracted files=%d hashed-dlls=%d" % (len(written), len(hashes)))
            # F3: stamp GameData/kRPC/PluginData/settings.cfg for unattended operation
            # (autoStartServers/autoAcceptConnections/confirmRemoveClient) so every
            # provisioned instance is hands-free. Recorded as krpcSettingsSha256 +
            # re-hashed in VERIFY so a later manual kRPC settings change is caught as drift.
            _stamp_krpc_settings(ctx)
            # SF10: record the full extracted file set (incl. the stamped
            # PluginData/settings.cfg) so VERIFY catches an ADDED file in GameData/kRPC.
            inv["krpc"] = _inventory_of(ctx, list(written) + ["GameData/kRPC/PluginData/settings.cfg"])

    # Built TestingTools.dll dropped alongside kRPC.
    if "testingtools" in stack:
        cached = getattr(ctx, "testingtools_dll", None)
        if not cached or not os.path.isfile(cached):
            abort(ctx, "Install", "EC-4", "TestingTools.dll not built/cached (BUILD-TT must run first)")
            return
        dest_rel = "GameData/kRPC/TestingTools.dll"
        dest = os.path.join(ctx.instance_dir, *dest_rel.split("/"))
        expected = getattr(ctx, "testingtools_sha", None)  # source (built shim) hash
        current = sha256_file(dest) if os.path.isfile(dest) else None
        skip = provlib.decide_idempotent_skip(ctx.prior_complete, expected, current)
        if skip.skip:
            extra["testingtools"] = {"dllSha256": current}
            inv["testingtools"] = _prior_inventory(ctx, "testingtools") or _inventory_of(ctx, [dest_rel])
            log(ctx, "Info", "Install", "SF9 skip TestingTools.dll install (hash-match source)")
        else:
            os.makedirs(_long(os.path.dirname(dest)), exist_ok=True)
            _copy_file(cached, _long(dest))
            extra["testingtools"] = {"dllSha256": sha256_file(dest)}
            inv["testingtools"] = _inventory_of(ctx, [dest_rel])
            log(ctx, "Info", "Install", "TestingTools.dll installed sha256=%s" % extra["testingtools"]["dllSha256"])

    # KRPC.MechJeb prebuilt DLL (+ json) from its release zip -> GameData/kRPC/.
    if "krpc_mechjeb" in stack:
        recorded = _prior_component(ctx, "krpc_mechjeb").get("dllSha256")
        dest = _dll_install_path(ctx, "krpc_mechjeb", "KRPC.MechJeb.dll")
        current = sha256_file(dest) if os.path.isfile(dest) else None
        skip = provlib.decide_idempotent_skip(
            ctx.prior_complete and _install_pin_stable(ctx, "krpc_mechjeb"), recorded, current)
        if skip.skip:
            extra["krpc_mechjeb"] = {"dllSha256": recorded}
            # item 2: arm the inventory from disk when the prior manifest predates it.
            inv["krpc_mechjeb"] = _prior_inventory(ctx, "krpc_mechjeb") or _inventory_of_folder(ctx, "krpc_mechjeb")
            log(ctx, "Info", "Install", "SF9 skip KRPC.MechJeb extraction (hash-match, pin stable)")
        else:
            zip_path = _cached_zip_path(ctx, "krpc_mechjeb", "downloadUrl")
            if not zip_path or not os.path.isfile(zip_path):
                abort(ctx, "Install", "EC-4", "KRPC.MechJeb zip not cached: %s" % zip_path)
                return
            written = _extract_zip_plan(ctx, "krpc_mechjeb", zip_path)
            hashes = _hash_installed_dlls(ctx, written)
            extra["krpc_mechjeb"] = {"dllSha256": hashes.get("KRPC.MechJeb.dll")}
            inv["krpc_mechjeb"] = _inventory_of(ctx, written)
            log(ctx, "Info", "Install", "KRPC.MechJeb installed dll-sha256=%s" % extra["krpc_mechjeb"]["dllSha256"])

    # MechJeb2 release zip -> GameData/MechJeb2/ (pin OPEN today; guarded above by
    # DOWNLOAD's EC-13, so this only runs once a durable build is pinned).
    if "mechjeb2" in stack:
        recorded_map = _prior_component(ctx, "mechjeb2").get("installedDlls", {}) or {}
        expected = provlib.installed_map_digest(recorded_map) if recorded_map else None
        current = provlib.installed_map_digest(
            {k: v for k, v in _current_component_dll_map(ctx, "mechjeb2", list(recorded_map)).items() if v})
        skip = provlib.decide_idempotent_skip(
            ctx.prior_complete and _install_pin_stable(ctx, "mechjeb2"), expected, current)
        if skip.skip:
            extra["mechjeb2"] = {"installedDlls": recorded_map}
            # item 2: arm the inventory from disk when the prior manifest predates it.
            inv["mechjeb2"] = _prior_inventory(ctx, "mechjeb2") or _inventory_of_folder(ctx, "mechjeb2")
            log(ctx, "Info", "Install", "SF9 skip MechJeb2 extraction (hash-match, pin stable)")
            return
        zip_path = _cached_zip_path(ctx, "mechjeb2", "downloadUrl")
        if zip_path and os.path.isfile(zip_path):
            written = _extract_zip_plan(ctx, "mechjeb2", zip_path)
            extra["mechjeb2"] = {"installedDlls": _hash_installed_dlls(ctx, written)}
            inv["mechjeb2"] = _inventory_of(ctx, written)
            log(ctx, "Info", "Install", "MechJeb2 extracted files=%d" % len(written))
        else:
            log(ctx, "Warn", "Install", "MechJeb2 zip not cached (pin OPEN); skipped")


def _krpc_settings_path(ctx: ProvisionContext) -> str:
    return os.path.join(ctx.instance_dir, "GameData", "kRPC", "PluginData", "settings.cfg")


def _stamp_krpc_settings(ctx: ProvisionContext) -> None:
    """F3: rewrite the kRPC PluginData/settings.cfg with the COMPLETE golden
    template (provlib.stamp_krpc_settings ignores the prior contents by design:
    a partial file zero-defaults every omitted key and maxTimePerUpdate=0 kills
    all RPC execution). Records ctx.krpc_settings_sha over the ACTUAL on-disk
    bytes (LF-written) so VERIFY re-hashes the same bytes."""
    path = _krpc_settings_path(ctx)
    shipped = None
    if os.path.isfile(path):
        with open(path, "r", encoding="utf-8") as fh:
            shipped = fh.read()
    stamped = provlib.stamp_krpc_settings(shipped)
    os.makedirs(_long(os.path.dirname(path)), exist_ok=True)
    # newline="\n": no CRLF translation, so the on-disk bytes match the hash.
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(stamped)
    sha = sha256_file(path)
    ctx.krpc_settings_sha = sha  # type: ignore[attr-defined]
    log(ctx, "Info", "Install",
        "kRPC settings stamped golden-template (full key set + Item-wrapped default server; prior file %s) sha256=%s"
        % ("replaced" if shipped is not None else "absent", sha))


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


def _dev_buildid64_sha(ctx: ProvisionContext) -> Optional[str]:
    """Hash the DEV install's buildID64.txt -- the KSP version whose Managed
    reference DLLs the BUILD-TT shim HintPaths against. Compared to the recorded
    manifest buildId64Sha256 to detect a KSP version bump that would leave the
    cached TestingTools.dll linked against stale reference DLLs (reviewer S1)."""
    dev = os.path.join(ctx.dev_install, "buildID64.txt")
    return sha256_file(dev) if os.path.isfile(dev) else None


def _managed_dir_stat(ctx: ProvisionContext) -> tuple:
    """SF9/S2: (file_count, total_bytes) of the instance's KSP_x64_Data/Managed
    dir -- the stock / reference DLL surface. A partially-deleted instance (a
    stock Managed DLL removed) or a swapped DLL of a different size drops the
    count / size, so the CLONE mutable-surface skip gate re-stats and refuses to
    skip when it differs from the recorded stat (which buildID64 alone cannot
    catch). Reparse-point subdirs are pruned so no junctioned tree is pulled in.
    Returns (0, 0) when the dir is absent (e.g. a fixture install without it)."""
    data = _find_ksp_data_dir(ctx.dev_install)
    managed = os.path.join(ctx.instance_dir, data, "Managed")
    count = 0
    total = 0
    if not os.path.isdir(managed):
        return (0, 0)
    for r, dirs, names in os.walk(managed):
        _prune_reparse_dirs(ctx, r, dirs)
        for n in names:
            count += 1
            total += _safe_size(os.path.join(r, n))
    return (count, total)


def _managed_stat_dict(ctx: ProvisionContext) -> Dict[str, int]:
    """Manifest-shaped form of _managed_dir_stat (SF9/S2)."""
    count, total = _managed_dir_stat(ctx)
    return {"fileCount": count, "bytes": total}


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
    # SF3: any OSError / subprocess failure / missing tool (dotnet, git) raised by
    # a live phase is caught here, converted to a clean EC abort + exit 2 with the
    # lock released via _finish. The .provision-incomplete marker is deliberately
    # NOT cleared (only VERIFY success clears it), so a crashed live run stays
    # un-admittable. FileNotFoundError (a missing dotnet/git) is an OSError
    # subclass, so it is caught first for a toolchain-specific EC.
    try:
        return _run_phases(ctx)
    except FileNotFoundError as exc:
        abort(ctx, "Live", "EC-4", "required tool not found (dotnet / git?): %s" % exc)
        return _finish(ctx, 2)
    except (OSError, subprocess.SubprocessError) as exc:
        abort(ctx, "Live", "EC-6", "live provisioning I/O / subprocess failure: %s" % exc)
        return _finish(ctx, 2)


def _load_prior_provision_state(ctx: ProvisionContext) -> None:
    """SF9: read the prior provision-manifest.json (if any) and decide whether it
    describes a COMPLETE provision (manifest present AND no .provision-incomplete
    marker). Only then may the heavy phases hash-short-circuit. Live runs only:
    under dry-run nothing is skipped (the phases already collapse to plan lines).
    A parse failure or a present incomplete-marker leaves prior_complete False."""
    if ctx.dry_run:
        return
    manifest_path = os.path.join(ctx.parsek_gamedata, "provision-manifest.json")
    marker_present = os.path.isfile(_incomplete_marker_path(ctx))
    prior = None
    if os.path.isfile(manifest_path):
        try:
            with open(manifest_path, "r", encoding="utf-8") as fh:
                prior = json.load(fh)
        except (OSError, ValueError) as exc:
            log(ctx, "Warn", "Preflight", "prior manifest unreadable (%s); full re-provision" % exc)
            prior = None
    ctx.prior_manifest = prior
    ctx.prior_complete = bool(prior) and not marker_present
    log(ctx, "Info", "Preflight",
        "SF9 idempotency prior-manifest=%s incomplete-marker=%s prior-complete=%s"
        % ("present" if prior else "absent", marker_present, ctx.prior_complete))


def _prior_component(ctx: ProvisionContext, comp: str) -> Dict:
    return ((ctx.prior_manifest or {}).get("components", {}) or {}).get(comp, {}) or {}


def _run_phases(ctx: ProvisionContext) -> int:
    phase_preflight(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)
    _load_prior_provision_state(ctx)

    resolved = phase_pin(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    phase_download(ctx)
    if ctx.aborted:
        return _finish(ctx, 2)

    phase_build_tt(ctx, resolved)
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
    if ctx.aborted:
        return _finish(ctx, 2)
    phase_mm_cache(ctx)
    manifest = phase_manifest(ctx, resolved, junctions, deltas, parsek_info, dev_status)
    verified = phase_verify(ctx, manifest)
    if not verified and ctx.repair and not ctx.dry_run:
        verified = _repair_and_reverify(ctx, manifest, resolved, deltas, parsek_info)
    return _finish(ctx, 0 if verified else 3)


def _repair_and_reverify(ctx: ProvisionContext, manifest: Dict, resolved: Dict,
                         deltas: Dict, parsek_info: Dict) -> bool:
    """--repair: converge ONLY the drifted components (design VERIFY --repair),
    then re-VERIFY once. The drift set comes from the pure plan_repair over the
    VERIFY diff; an unrepairable field is logged (a targeted re-install cannot fix
    it) and the run stays non-zero."""
    drift = getattr(ctx, "verify_drift", [])
    plan = provlib.plan_repair(drift)
    log(ctx, "Info", "Repair",
        "drift=%d -> components=%s devMods=%s settings=%s unrepairable=%s"
        % (len(drift), ",".join(plan.components) or "-", ",".join(plan.dev_mods) or "-",
           plan.settings, ",".join(plan.unrepairable) or "-"))
    for f in plan.unrepairable:
        log(ctx, "Warn", "Repair", "field %s has no targeted repair (needs a full re-provision)" % f)

    stack_repair = [c for c in plan.components if c in provlib.STACK_COMPONENT_NAMES and c != "parsek"]
    if stack_repair:
        _repair_stack_folders(ctx, stack_repair)
    if "parsek" in plan.components:
        log(ctx, "Info", "Repair", "re-deploying Parsek.dll")
        parsek_info = phase_deploy(ctx)
    if plan.dev_mods:
        _repair_dev_mods(ctx, plan.dev_mods)
    if plan.settings:
        log(ctx, "Info", "Repair", "re-applying settings deltas")
        phase_settings(ctx)
    if ctx.aborted:
        return False
    # Re-stamp the manifest with any refreshed component hashes, then re-VERIFY.
    manifest = phase_manifest(ctx, resolved, manifest.get("junctionTargets", {}),
                              deltas, parsek_info, manifest.get("devSourcedMods", {}))
    return phase_verify(ctx, manifest)


def _repair_stack_folders(ctx: ProvisionContext, stack_repair: Sequence[str]) -> None:
    """--repair convergence for stack components, including SF10 inventory
    added-file drift. Expands the drifted components to every sibling sharing an
    install folder, scoped-deletes each affected folder behind the EC-16 fence
    (so an INJECTED extra file is REMOVED, not merely overwritten by a re-extract),
    then re-installs the full sibling set. The scoped delete forces the SF9 skip
    off (the DLLs are absent), so every sibling genuinely re-extracts."""
    profile_stack = set(ctx.profile.get("stackComponents", []) or [])
    folders = {provlib.stack_component_install_folder(c) for c in stack_repair}
    siblings = [c for c in provlib.STACK_COMPONENT_NAMES
                if c != "parsek"
                and provlib.stack_component_install_folder(c) in folders
                and c in profile_stack]
    # B1: mark the instance un-admittable BEFORE the scoped delete (defense in
    # depth). A crash between the delete and the re-install would otherwise leave a
    # component folder half-emptied yet still admissible; the post-repair VERIFY
    # clears the marker on success, exactly as the CLONE-time marker does.
    _write_incomplete_marker(ctx)
    for folder_rel in sorted(folders):
        folder_abs = os.path.join(ctx.instance_dir, *folder_rel.split("/"))
        if os.path.isdir(folder_abs):
            _scoped_delete_instance_subtree(ctx, folder_abs)
    log(ctx, "Info", "Repair",
        "re-installing stack folders=%s components=%s" % (",".join(sorted(folders)), ",".join(siblings)))
    _install_stack(ctx, siblings)


def _repair_dev_mods(ctx: ProvisionContext, names: Sequence[str]) -> None:
    for name in names:
        src = os.path.join(ctx.dev_install, "GameData", name)
        dst = os.path.join(ctx.instance_dir, "GameData", name)
        if not os.path.exists(src):
            log(ctx, "Warn", "Repair", "dev-sourced mod %s absent at source; skip" % name)
            continue
        # A plain re-copy overwrites same-named files but CANNOT remove a file
        # injected into the instance copy, so drift from an injected extra file
        # would never converge. Scoped-delete the instance mod dir first (fenced
        # behind the EC-16 alias guard + strict instance-GameData containment),
        # then re-copy from the dev source.
        if not _scoped_delete_instance_subtree(ctx, dst):
            continue
        if os.path.isdir(src):
            _copy_dir(ctx, src, dst)
        else:
            _copy_one(src, dst)
        log(ctx, "Info", "Repair", "re-copied GameData/%s (scoped-delete + copy)" % name)


def _scoped_delete_instance_subtree(ctx: ProvisionContext, target: str) -> bool:
    """Delete a subtree that MUST live strictly inside the instance GameData,
    behind the EC-16 dev-install alias fence. Returns False (and logs) without
    deleting if either guard trips, so a misconfigured instanceDir can never
    rmtree into the read-only dev install."""
    import shutil
    gd_instance = os.path.join(ctx.instance_dir, "GameData")
    inst_norm = os.path.normcase(os.path.normpath(ctx.instance_dir))
    dev_norm = os.path.normcase(os.path.normpath(ctx.dev_install))
    alias = provlib.check_instance_dir_alias(inst_norm, dev_norm, ctx.profile.get("instanceDir", ""))
    if not alias.ok:
        log(ctx, "Error", "Repair",
            "EC-16 refuse scoped delete: instance aliases dev install (%s)" % alias.reason)
        return False
    # Strict containment: target must be nested UNDER instance GameData, never
    # equal to it and never outside it.
    strictly_inside = (provlib.is_path_within(target, gd_instance)
                       and not provlib.is_path_within(gd_instance, target))
    if not strictly_inside:
        log(ctx, "Error", "Repair",
            "refuse scoped delete of %s: not strictly inside instance GameData %s"
            % (target, gd_instance))
        return False
    if os.path.exists(target):
        try:
            shutil.rmtree(_long(target))
        except OSError as exc:
            log(ctx, "Warn", "Repair", "scoped delete failed %s: %s" % (target, exc))
            return False
    return True


def _finish(ctx: ProvisionContext, code: int) -> int:
    # Release the instance lock this run acquired (live only). Only remove a lock
    # we own; a lock held by another live run was refused, not written, here.
    if getattr(ctx, "lock_acquired", False) and not ctx.dry_run:
        lock_path = getattr(ctx, "lock_path", None)
        if lock_path and os.path.isfile(lock_path):
            try:
                os.remove(lock_path)
                log(ctx, "Info", "Summary", "released provision lock %s" % lock_path)
            except OSError as exc:
                log(ctx, "Warn", "Summary", "could not remove lock %s: %s" % (lock_path, exc))
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
    p.add_argument("--krpc-src", help="explicit path to a kRPC git clone for BUILD-TT/PIN "
                   "(else a module-owned shallow clone under harness/provision/.cache/krpc-src)")
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
        krpc_src_override=args.krpc_src,
    )
    return run(ctx)


if __name__ == "__main__":
    sys.exit(main())
