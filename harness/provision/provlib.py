"""Pure decision logic for the M-A6 automation-stack provisioner.

This module holds every side-effect-free decision the provisioner makes:
manifest drift diffing, settings-delta application, pin resolution, junction
classification, UTF-16 signature counting, disk/path guards, and lockfile
acquire/reclaim semantics. It imports nothing that touches the network, KSP, or
the filesystem so the whole module is unit-testable in isolation (see
test_provlib.py). The orchestrator (provision.py) performs the actual I/O and
calls into here for every branch it takes.

Design authority: docs/dev/design-autotest-stack-setup.md (Module M-A6).
ASCII only; stdlib only.
"""

from __future__ import annotations

import posixpath
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Sequence, Tuple

# ---------------------------------------------------------------------------
# Capability table (design GT-2 / GT-3). Data, not a unit test: the design
# dropped the "restate the table back to itself" test as vacuous; the real
# guard is the BUILD-TT reflection smoke over the built assembly. Kept here as
# the harness admission INPUT and for MANIFEST emission.
# ---------------------------------------------------------------------------

TESTINGTOOLS_CAPABILITIES: Tuple[str, ...] = (
    "LoadSave",
    "RemoveOtherVessels",
    "SetCircularOrbit",
    "SetOrbit",
    "ClearRotation",
    "ApplyRotation",
)

# Present on master ("RPC Deprecation #926"), ABSENT at the v0.5.4 release pin.
MISSING_VS_MASTER: Tuple[str, ...] = ("autoLoadFlags", "Quit", "SetLanded")

# BUILD-TT: TestingTools source at v0.5.4 is exactly five files. The shim
# compiles only two; AutoLoadGame.cs unconditionally auto-loads
# saves/default/persistent.sfs 15 frames after MAINMENU and would race the
# seam's LoadGame boot (M-A2), so it and AutoSwitchVessel.cs are DROPPED.
TESTINGTOOLS_SHIM_SOURCES: Tuple[str, ...] = ("OrbitTools.cs", "TestingTools.cs")
TESTINGTOOLS_DROPPED_SOURCES: Tuple[str, ...] = ("AutoLoadGame.cs", "AutoSwitchVessel.cs")

# Stock asset payloads junctioned (not copied) at CLONE (design CLONE step).
STOCK_JUNCTION_GAMEDATA: Tuple[str, ...] = ("Squad", "SquadExpansion")
# KSP 1.12.5 ships NO KSP_x64_Data/StreamingAssets (assets live in
# sharedassets*/level*/resources.assets directly): the StreamingAssets entry
# was a generic-Unity-layout assumption from the design and produced a
# DANGLING junction on the first live provision (verified absent 2026-07-12).
# The tuple stays as the mechanism for future genuinely-junctionable trees.
STOCK_JUNCTION_TREES: Tuple[str, ...] = ()

# Stack components installed by the script, never sourced from dev GameData.
STACK_COMPONENT_NAMES: Tuple[str, ...] = (
    "krpc",
    "testingtools",
    "mechjeb2",
    "krpc_mechjeb",
    "parsek",
)

# Real GameData subfolder each stack component lands in (N11: the dry-run plan
# labels the actual on-disk target, not the pin id). kRPC + TestingTools +
# KRPC.MechJeb share GameData/kRPC; MechJeb2 and Parsek own their own folders.
STACK_COMPONENT_INSTALL_FOLDER: Dict[str, str] = {
    "krpc": "GameData/kRPC",
    "testingtools": "GameData/kRPC",
    "krpc_mechjeb": "GameData/kRPC",
    "mechjeb2": "GameData/MechJeb2",
    "parsek": "GameData/Parsek",
}


def stack_component_install_folder(name: str) -> str:
    """The instance GameData subfolder a stack component installs into (N11)."""
    return STACK_COMPONENT_INSTALL_FOLDER.get(name, "%s/%s" % (GAMEDATA_DIR, name))

# ModuleManager cache artifacts deleted from the instance so MM regenerates
# them against the instance's actual mod set (design MM CACHE / EC-2).
MM_CACHE_FILES: Tuple[str, ...] = (
    "ModuleManager.ConfigCache",
    "ModuleManager.ConfigSHA",
    "ModuleManager.Physics",
    "ModuleManager.TechTree",
)

# Log levels mirroring ParsekLog plus Amber for the design's loud-but-not-fatal
# warnings (master-commit pin, EC-11 stage-vs-source).
LOG_LEVELS: Tuple[str, ...] = ("Info", "Verbose", "Warn", "Amber", "Error")


# ---------------------------------------------------------------------------
# Logging format (pure). Orchestrator writes these to provision-log.txt.
# ---------------------------------------------------------------------------


def format_log_line(level: str, step: str, message: str) -> str:
    """Format one provisioning-log line: ``[Provision][LEVEL][Step] message``.

    Mirrors ParsekLog's ``[Parsek][LEVEL][Subsystem]`` shape (design's
    Diagnostic Logging section). ``level`` is normalized to a known level;
    unknown levels are passed through so a caller typo is visible, not silently
    swallowed.
    """
    return "[Provision][%s][%s] %s" % (level, step, message)


# ---------------------------------------------------------------------------
# Pin resolution (design PIN / GT-1). resolve_pin compares the git-resolved
# commit against the recorded pin. Callers MUST peel annotated tags with
# ``git rev-parse <tag>^{commit}`` before calling: kRPC v0.5.4 is an ANNOTATED
# tag, so a bare ``git rev-parse v0.5.4`` yields the tag OBJECT sha
# (4e9dfbed...), not the commit (11f1f13...). Peeling is the orchestrator's job;
# this function just compares the peeled result against pins.toml.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class PinResolution:
    ok: bool
    reason: str  # "match" | "mismatch" | "missing"
    expected: str
    resolved: str


def resolve_pin(clone_ref_output: str, expected_commit: str) -> PinResolution:
    """Compare a git-resolved commit against the recorded pin commit.

    ``clone_ref_output`` is the raw stdout of ``git rev-parse <tag>^{commit}``
    (may carry trailing whitespace/newline, or be empty when the tag is
    missing). Returns ``missing`` on empty output (moved/deleted tag), ``match``
    when the resolved commit equals the recorded pin, else ``mismatch`` (a
    moved/retagged ref -- guards GT-1).
    """
    resolved = (clone_ref_output or "").strip()
    expected = (expected_commit or "").strip()
    if not resolved:
        return PinResolution(False, "missing", expected, resolved)
    if resolved.lower() == expected.lower():
        return PinResolution(True, "match", expected, resolved)
    return PinResolution(False, "mismatch", expected, resolved)


# ---------------------------------------------------------------------------
# KRPC.MechJeb pairing (design PAIR / GT-6 / EC-14). Pure decision.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class PairDecision:
    ok: bool
    reason: str  # "match" | "mismatch" | "verify-required-nonv054"
    requires_web_verify: bool


def evaluate_krpc_mechjeb_pair(krpc_tag: str, fork: str, tag: str, paired_krpc_tag: str) -> PairDecision:
    """Decide whether the pinned KRPC.MechJeb pairs with the pinned kRPC (GT-6).

    Under the v0.5.4 kRPC pin the only CHANGELOG-proven pair is genhis v0.7.1
    (``pairedKrpcTag == v0.5.4``); any deviation is EC-14 ``mismatch``. Under a
    non-v0.5.4 kRPC pin the pairing cannot be verified locally: return
    ``verify-required-nonv054`` with ``requires_web_verify=True`` so the caller
    refuses to guess and prints the web-verification procedure (EC-14 deferred
    branch)."""
    if krpc_tag != "v0.5.4":
        return PairDecision(False, "verify-required-nonv054", True)
    ok = fork == "genhis" and tag == "v0.7.1" and paired_krpc_tag == "v0.5.4"
    return PairDecision(ok, "match" if ok else "mismatch", False)


# ---------------------------------------------------------------------------
# Parsek.dll deploy-source selection (design DEPLOY). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class DeploySourceDecision:
    source: Optional[str]
    reason: str  # "override" | "worktree-build" | "missing-no-source"
    ok: bool


def select_parsek_dll_source(
    override: Optional[str],
    worktree_dll: str,
    worktree_dll_exists: bool,
) -> DeploySourceDecision:
    """Pick the Parsek.dll source for DEPLOY.

    An explicit ``--parsek-dll`` override always wins; otherwise the current
    worktree's own ``bin/Debug`` build when it exists. There is NO hardcoded
    sibling-worktree fallback (SF8): a differently-named worktree with no local
    build and no override yields ``ok=False`` (source None) so DEPLOY aborts
    demanding ``--parsek-dll`` rather than silently deploying some other
    worktree's DLL. ``worktree_dll_exists`` is injected so this stays pure over
    the filesystem."""
    if override:
        return DeploySourceDecision(override, "override", True)
    if worktree_dll_exists:
        return DeploySourceDecision(worktree_dll, "worktree-build", True)
    return DeploySourceDecision(None, "missing-no-source", False)


# ---------------------------------------------------------------------------
# Parsek DEPLOY auxiliary payload (design DEPLOY). The deployed GameData/Parsek
# is NOT just Plugins/Parsek.dll: the KSP-AVC version file and the toolbar
# textures ship in the same payload (matching the dev layout + scripts/
# release.py). Their absence floods ToolbarControl.OnGUI with a per-frame
# NullReferenceException -- ParsekFlight/ParsekKSC AddToAllToolbars read
# "Parsek/Textures/parsek_64" (large) + "Parsek/Textures/parsek_32" (small), and
# a mod that registers a toolbar button whose icon texture cannot be found NREs
# every OnGUI. This decides the aux dest->source mapping purely (existence
# probe injected), tried in priority order per dest.
# ---------------------------------------------------------------------------

# The full dev/app-launcher texture set. 32 + 64 are the two ToolbarControl
# actually reads, so they are REQUIRED; 24 + 38 round out the dev layout.
PARSEK_TEXTURE_SIZES = (24, 32, 38, 64)
PARSEK_REQUIRED_TEXTURE_SIZES = (32, 64)

# img/ basenames. scripts/release.py sources 32/64 from the repo img/ folder;
# only these two exist there. 24/38 live only in the dev install's Textures/.
_PARSEK_IMG_TEXTURE_BASENAME = {32: "parsek logo - 32.png", 64: "parsek logo - 64.png"}


@dataclass(frozen=True)
class ParsekAuxFile:
    dest_rel: str   # relative to GameData/Parsek, forward-slash form
    source: str     # resolved absolute source path
    origin: str     # "worktree-gamedata" | "worktree-img" | "dev-install"


@dataclass(frozen=True)
class ParsekAuxPayload:
    files: Tuple[ParsekAuxFile, ...]      # resolved dest->source, stable order
    missing_required: Tuple[str, ...]     # required dests with NO available source
    missing_optional: Tuple[str, ...]     # optional dests with NO available source


def resolve_parsek_aux_payload(
    worktree_gamedata_parsek: str,
    worktree_img: str,
    dev_gamedata_parsek: str,
    exists: Callable[[str], bool],
) -> ParsekAuxPayload:
    """Resolve the Parsek DEPLOY aux payload (version file + toolbar textures).

    Priority per dest: this worktree's own GameData/Parsek copy first (repo is
    the source of truth), then the repo img/ folder for the two textures it
    carries, then the read-only dev install's GameData/Parsek as the last resort
    (24/38 exist only there). ``exists`` is injected so the decision is pure over
    the filesystem. Required missing (Parsek.version, parsek_32/64) are surfaced
    separately from optional missing (parsek_24/38) so the caller can fail loud
    on the former and merely note the latter."""
    files: List[ParsekAuxFile] = []
    missing_required: List[str] = []
    missing_optional: List[str] = []

    def _first(candidates: Sequence[Tuple[str, str]]) -> Optional[Tuple[str, str]]:
        for origin, path in candidates:
            if path and exists(path):
                return origin, path
        return None

    ver_hit = _first([
        ("worktree-gamedata", posixpath.join(_slash(worktree_gamedata_parsek), "Parsek.version")),
        ("dev-install", posixpath.join(_slash(dev_gamedata_parsek), "Parsek.version")),
    ])
    if ver_hit:
        files.append(ParsekAuxFile("Parsek.version", ver_hit[1], ver_hit[0]))
    else:
        missing_required.append("Parsek.version")

    for size in PARSEK_TEXTURE_SIZES:
        dest = "Textures/parsek_%d.png" % size
        cands: List[Tuple[str, str]] = [
            ("worktree-gamedata",
             posixpath.join(_slash(worktree_gamedata_parsek), "Textures", "parsek_%d.png" % size)),
        ]
        if size in _PARSEK_IMG_TEXTURE_BASENAME:
            cands.append(("worktree-img",
                          posixpath.join(_slash(worktree_img), _PARSEK_IMG_TEXTURE_BASENAME[size])))
        cands.append(("dev-install",
                      posixpath.join(_slash(dev_gamedata_parsek), "Textures", "parsek_%d.png" % size)))
        hit = _first(cands)
        if hit:
            files.append(ParsekAuxFile(dest, hit[1], hit[0]))
        elif size in PARSEK_REQUIRED_TEXTURE_SIZES:
            missing_required.append(dest)
        else:
            missing_optional.append(dest)

    return ParsekAuxPayload(tuple(files), tuple(missing_required), tuple(missing_optional))


def _slash(path: str) -> str:
    """Normalize a possibly-backslash Windows path to forward slashes so the pure
    posixpath.join above composes candidate source paths deterministically on
    every platform. The result is only ever passed to the injected ``exists``
    predicate and (live) to the copy helper, both of which accept forward
    slashes on Windows."""
    return (path or "").replace("\\", "/")


# ---------------------------------------------------------------------------
# Settings-delta application (design SETTINGS / EC-15). Pure, line-oriented.
# ---------------------------------------------------------------------------

# A KSP settings.cfg entry: optional indent, KEY, optional space, '=', value.
# Node braces, comments (//), and blank lines are NOT entries and are preserved
# verbatim.
def settings_key_of(line: str) -> Optional[str]:
    """Return the KEY of a ``KEY = value`` settings line, else None.

    Blank lines, ``//`` comments, and ``{`` / ``}`` node braces return None so
    they are treated as opaque and preserved by apply_settings.
    """
    stripped = line.strip()
    if not stripped or stripped.startswith("//") or stripped in ("{", "}"):
        return None
    eq = line.find("=")
    if eq < 0:
        return None
    key = line[:eq].strip()
    if not key:
        return None
    # A valid KSP key is a bareword (letters/digits/underscore).
    for ch in key:
        if not (ch.isalnum() or ch == "_"):
            return None
    return key


def _rewrite_settings_value(line: str, new_value: str) -> str:
    """Replace the value of a matched settings line, preserving indent + key +
    the exact separator run up to and including ``=`` plus one space."""
    eq = line.find("=")
    prefix = line[: eq + 1]  # indent + key + spaces + '='
    return "%s %s" % (prefix.rstrip(), new_value)


def keys_present(base_lines: Sequence[str], keys: Sequence[str]) -> set:
    """Return the subset of ``keys`` that already exist as entries in base_lines
    (used to classify replaced-vs-appended for EC-15 logging)."""
    present = set()
    have = {settings_key_of(l) for l in base_lines}
    for k in keys:
        if k in have:
            present.add(k)
    return present


def apply_settings(base_lines: Sequence[str], deltas: Dict[str, str]) -> List[str]:
    """Apply profile settings deltas as pure key-replacements.

    Existing keys are rewritten in place (order, comments, and unrelated keys
    preserved); keys absent from base are APPENDED at the end (KSP tolerates
    extra keys, EC-15). Deterministic: appended keys follow the deltas' dict
    insertion order. Returns the new line list.
    """
    result: List[str] = []
    seen: set = set()
    for line in base_lines:
        key = settings_key_of(line)
        if key is not None and key in deltas:
            result.append(_rewrite_settings_value(line, str(deltas[key])))
            seen.add(key)
        else:
            result.append(line)
    for key, value in deltas.items():
        if key not in seen:
            result.append("%s = %s" % (key, str(value)))
    return result


# ---------------------------------------------------------------------------
# kRPC settings stamp (design SETTINGS / F3). Pure, ConfigNode-line-oriented.
#
# kRPC ships GameData/kRPC/PluginData/settings.cfg with autoStartServers=False /
# autoAcceptConnections=False / confirmRemoveClient=True, which force a manual
# in-game click every launch and defeat unattended operation. The provisioner
# stamps the three keys hands-free. The shipped file is a ConfigNode
# ``KRPCConfiguration { ... }`` with the three keys as FLAT children, so present
# keys are rewritten in place (reusing settings_key_of / _rewrite_settings_value)
# and any absent key is inserted just before the node's closing brace (NOT
# appended at file end, which would land it OUTSIDE the node). An entirely absent
# file synthesizes a minimal node carrying only the three keys.
# ---------------------------------------------------------------------------

# The three hands-free kRPC settings (order-stable for the synth + insert paths).
KRPC_SETTINGS_DELTAS: Tuple[Tuple[str, str], ...] = (
    ("autoStartServers", "True"),
    ("autoAcceptConnections", "True"),
    ("confirmRemoveClient", "False"),
)


def _synth_krpc_settings_lines() -> List[str]:
    """Minimal KRPCConfiguration node carrying only the three hands-free keys
    (used when the kRPC zip shipped no PluginData/settings.cfg)."""
    lines = ["KRPCConfiguration", "{"]
    for key, value in KRPC_SETTINGS_DELTAS:
        lines.append("\t%s = %s" % (key, value))
    lines.append("}")
    return lines


def _insert_krpc_keys_before_last_brace(lines: List[str], missing: Sequence[str]) -> List[str]:
    """Insert the given missing keys just before the LAST closing-brace line (the
    KRPCConfiguration node close), so an absent key lands INSIDE the node rather
    than after it. Falls back to appending when no closing brace is found."""
    deltas = dict(KRPC_SETTINGS_DELTAS)
    inserts = ["\t%s = %s" % (k, deltas[k]) for k in missing]
    idx = None
    for i in range(len(lines) - 1, -1, -1):
        if lines[i].strip() == "}":
            idx = i
            break
    if idx is None:
        return list(lines) + inserts
    return list(lines[:idx]) + inserts + list(lines[idx:])


def stamp_krpc_settings(shipped_text: Optional[str]) -> str:
    """Return the stamped kRPC settings.cfg text with the three hands-free keys set.

    ``shipped_text`` is the kRPC-shipped PluginData/settings.cfg contents, or None
    (or blank) when the zip carried no such file. Present keys are rewritten in
    place (order / comments / unrelated keys preserved, EC-15 style); absent keys
    are inserted before the KRPCConfiguration node's closing brace; an absent file
    synthesizes a minimal node. Idempotent: re-stamping already-stamped text
    yields the same text. Only the three keys are touched -- no other key's value
    is hardcoded. The returned text is LF-terminated (the caller writes it
    newline='\\n' so the on-disk hash matches)."""
    deltas = dict(KRPC_SETTINGS_DELTAS)
    if shipped_text is None or not shipped_text.strip():
        return "\n".join(_synth_krpc_settings_lines()) + "\n"
    result: List[str] = []
    seen: set = set()
    for line in shipped_text.splitlines():
        key = settings_key_of(line)
        if key is not None and key in deltas:
            result.append(_rewrite_settings_value(line, deltas[key]))
            seen.add(key)
        else:
            result.append(line)
    missing = [k for k, _ in KRPC_SETTINGS_DELTAS if k not in seen]
    if missing:
        result = _insert_krpc_keys_before_last_brace(result, missing)
    return "\n".join(result) + "\n"


# ---------------------------------------------------------------------------
# Manifest drift diffing (design VERIFY / EC-3 / EC-5). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ManifestDiff:
    field: str
    expected: object
    actual: object
    kind: str  # "changed" | "missing" | "added"


def _flatten(obj: object, prefix: str, out: Dict[str, object]) -> None:
    if isinstance(obj, dict):
        for k in obj:
            child = "%s.%s" % (prefix, k) if prefix else str(k)
            _flatten(obj[k], child, out)
    elif isinstance(obj, (list, tuple)):
        # Lists are compared as a whole (order-sensitive) -- capability lists,
        # source-file lists. Store the tuple so a changed element is one diff.
        out[prefix] = tuple(obj)
    else:
        out[prefix] = obj


ADMISSION_KEYS: Tuple[str, ...] = (
    "profile",
    "kspVersion",
    "components",
    "settingsDeltasApplied",
    "devSourcedMods",
)


def project_admission(manifest: Dict) -> Dict[str, object]:
    """Project a manifest to the flat admission-relevant field set the harness
    compares (design: profile, kspVersion, component pins+hashes, parsek sha +
    signatureStrings, settings deltas, devSourcedMods hashes)."""
    out: Dict[str, object] = {}
    for key in ADMISSION_KEYS:
        if key in manifest:
            _flatten(manifest[key], key, out)
    return out


def compare_manifest(expected: Dict, actual: Dict) -> List[ManifestDiff]:
    """Field-level diff of two manifests' admission projections.

    ``expected`` is the pins/profile the harness expects; ``actual`` is the
    on-disk manifest. Returns every mismatch: a changed hash/tag/commit/setting
    (``changed``), a field present in expected but absent from actual
    (``missing`` -- e.g. a dropped component), and an extra field in actual
    (``added``). Empty list == admit. Guards EC-3/EC-5 (silent admission of a
    drifted instance).
    """
    exp = project_admission(expected)
    act = project_admission(actual)
    diffs: List[ManifestDiff] = []
    for k in sorted(exp):
        if k not in act:
            diffs.append(ManifestDiff(k, exp[k], None, "missing"))
        elif exp[k] != act[k]:
            diffs.append(ManifestDiff(k, exp[k], act[k], "changed"))
    for k in sorted(act):
        if k not in exp:
            diffs.append(ManifestDiff(k, None, act[k], "added"))
    return diffs


# ---------------------------------------------------------------------------
# SF10: per-component installed-file inventory diff (design SF10 / EC-3). Pure.
#
# The manifest records a DLL hash per stack component but not the SET of files
# each component installed, so a file ADDED inside a stack component's own folder
# (an extra DLL dropped into GameData/kRPC beside the hashed ones) is invisible.
# INSTALL records a per-component installed-file inventory (instance-relative
# path + sha256); VERIFY re-scans each install folder and diffs it against the
# recorded inventory. Missing / changed authored files drift; an on-disk file the
# inventory never recorded is an ADDED drift -- EXCEPT under a runtime-writable
# (PluginData) subtree, which KSP rewrites on every launch (settings, caches). A
# PluginData path is recorded for visibility but never red on add/change/remove
# (the same reason EC-3's dev-mod tree-hash prunes it, is_runtime_writable_dir):
# hashing runtime writes would re-drift the instance per game launch.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class InventoryDiff:
    rel: str
    kind: str  # "missing" | "changed" | "added"


def path_has_runtime_writable_segment(rel: str) -> bool:
    """True if any path segment of ``rel`` is a runtime-writable dir (the
    PluginData convention). Such paths are recorded in the inventory for
    visibility but are never a drift (SF10 PluginData tolerance)."""
    for seg in (rel or "").replace("\\", "/").split("/"):
        if is_runtime_writable_dir(seg):
            return True
    return False


def diff_inventory(recorded: Dict[str, str], current: Dict[str, str]) -> List[InventoryDiff]:
    """Diff a recorded installed-file inventory against the current on-disk set.

    ``recorded`` / ``current`` map an instance-relative file path to its sha256.
    Returns, in sorted order:
      - ``missing``: a recorded (authored) file absent on disk,
      - ``changed``: a recorded file whose on-disk hash differs,
      - ``added``: an on-disk file the inventory never recorded (an injected
        extra file -- the SF10 gap this closes).
    Any path under a runtime-writable (PluginData) subtree is tolerated on ALL
    three axes (never emitted): the game rewrites it per launch, so flagging it
    would re-drift the instance on every boot. Empty list == the folder matches
    its recorded inventory exactly (outside PluginData).

    Path comparison is CASE-INSENSITIVE (item 10), mirroring the PluginData
    ``name.lower()`` handling (is_runtime_writable_dir): KSP's GameData lives on a
    case-insensitive Windows filesystem, so a recorded ``GameData/kRPC/Foo.dll`` and an
    on-disk scan yielding ``GameData/krpc/Foo.dll`` are the SAME file. A case-sensitive
    compare would false-red it as one ``missing`` + one ``added``. The reported ``rel``
    keeps the RECORDED (or current) original casing for readability."""
    out: List[InventoryDiff] = []
    current_fold = {rel.lower(): rel for rel in current}
    recorded_fold = {rel.lower() for rel in recorded}
    for rel in sorted(recorded):
        if path_has_runtime_writable_segment(rel):
            continue
        cur_rel = current_fold.get(rel.lower())
        if cur_rel is None:
            out.append(InventoryDiff(rel, "missing"))
        elif current[cur_rel] != recorded[rel]:
            out.append(InventoryDiff(rel, "changed"))
    for rel in sorted(current):
        if rel.lower() in recorded_fold or path_has_runtime_writable_segment(rel):
            continue
        out.append(InventoryDiff(rel, "added"))
    return out


def group_inventory_by_folder(inventories: Dict[str, List]) -> Dict[str, Tuple[Tuple[str, ...], Dict[str, str]]]:
    """Group per-component recorded inventories by their shared install folder.

    ``inventories`` maps component -> list of ``{"rel":.., "sha256":..}`` entries.
    kRPC / TestingTools / KRPC.MechJeb all install into ``GameData/kRPC``, so an
    added-file scan of that folder must diff against the UNION of their recorded
    files (a file the folder scan finds that belongs to a sibling is not
    "added"). Returns ``folder -> (owner_components, merged rel->sha map)`` where
    ``owner_components`` is the sorted tuple of components installing there (the
    first is the repair target VERIFY attributes folder drift to)."""
    folders: Dict[str, Dict[str, str]] = {}
    owners: Dict[str, set] = {}
    for comp, files in (inventories or {}).items():
        folder = STACK_COMPONENT_INSTALL_FOLDER.get(comp)
        if not folder:
            continue
        merged = folders.setdefault(folder, {})
        owners.setdefault(folder, set()).add(comp)
        for entry in files or []:
            rel = entry.get("rel")
            if rel:
                merged[rel] = entry.get("sha256")
    return {folder: (tuple(sorted(owners[folder])), merged)
            for folder, merged in folders.items()}


# ---------------------------------------------------------------------------
# Junction classification + resolution (design CLONE / EC-8). Pure.
# ---------------------------------------------------------------------------


def classify_gamedata_entry(name: str, dev_sourced_mods: Sequence[str]) -> str:
    """Classify a GameData entry into how CLONE handles it.

    - ``junction-stock``: Squad / SquadExpansion stock asset payloads
      (junctioned back to the dev install, negligible drift risk).
    - ``copy-devsourced``: a non-stock mod folder the profile content-hashes and
      COPIES (a dev-GameData change must not silently drift an instance).
    - ``stack-install``: a stack component the script installs (kRPC,
      TestingTools, MechJeb2, KRPC.MechJeb, Parsek) -- not from dev GameData.
    - ``unknown``: anything else (logged, not silently copied).
    """
    if name in STOCK_JUNCTION_GAMEDATA:
        return "junction-stock"
    if name in dev_sourced_mods:
        return "copy-devsourced"
    # Normalize the stack-component GameData folder names.
    lname = name.lower()
    if lname in ("krpc", "krpc.mechjeb", "mechjeb2", "parsek", "testingtools"):
        return "stack-install"
    return "unknown"


def verify_junctions(manifest: Dict, resolve_fn: Callable[[str], Optional[str]]) -> List[str]:
    """Return the junction LINK keys whose link no longer resolves to its
    recorded TARGET (EC-8).

    ``resolve_fn(link_key) -> realpath`` is injected (the orchestrator passes
    ``os.path.realpath`` of the link path, or None/"" when the link is absent).
    The check is on the LINK, not merely the target's existence: a directory
    junction reports ``os.path.islink() == False``, so ``realpath`` of the link
    is the correct probe. A link that is missing (dangling) OR repointed
    (realpath != recorded target) fails; verifying only that the target exists
    would pass a deleted or repointed junction. Empty list == all junctions
    resolve to their recorded targets.
    """
    dangling: List[str] = []
    targets = manifest.get("junctionTargets", {}) or {}
    for link_key, target_path in targets.items():
        actual = resolve_fn(link_key)
        if not actual or _normcase_path(actual) != _normcase_path(target_path):
            dangling.append(link_key)
    return dangling


# ---------------------------------------------------------------------------
# UTF-16 signature grep (design DEPLOY / .claude/CLAUDE.md DLL-identity recipe).
# ---------------------------------------------------------------------------


def count_utf16(dll_bytes: bytes, signature: str) -> int:
    """Count non-overlapping occurrences of ``signature`` encoded UTF-16-LE in
    ``dll_bytes`` (the DLL-identity check; guards EC-9/EC-11)."""
    if not signature:
        return 0
    needle = signature.encode("utf-16-le")
    return dll_bytes.count(needle)


def check_signatures(dll_bytes: bytes, expected: Dict[str, int]) -> Dict[str, Tuple[int, int, bool]]:
    """For each ``signature -> expected_count``, return
    ``signature -> (actual, expected, ok)``. A present signature counting 0 or an
    absent one counting != expected fails."""
    out: Dict[str, Tuple[int, int, bool]] = {}
    for sig, exp in expected.items():
        actual = count_utf16(dll_bytes, sig)
        out[sig] = (actual, exp, actual == exp)
    return out


# ---------------------------------------------------------------------------
# Disk budget + path length guards (design CLONE / EC-6 / EC-7). Pure.
# ---------------------------------------------------------------------------


def estimate_instance_bytes(
    profile: Dict,
    component_sizes: Dict[str, int],
    base_copy_bytes: int = 0,
) -> int:
    """Estimate the COPIED (non-junctioned) byte footprint of one instance.

    Junctioned stock trees (Squad / SquadExpansion / StreamingAssets) cost ~0.
    The estimate sums a base copy surface plus every dev-sourced mod and stack
    component whose size is known in ``component_sizes`` (bytes). Unknown
    components contribute 0 (the orchestrator measures real sizes at run time;
    this pure form is for the pre-CLONE budget check and its test).
    """
    total = int(base_copy_bytes)
    for name in profile.get("devSourcedMods", []) or []:
        total += int(component_sizes.get(name, 0))
    for name in profile.get("stackComponents", []) or []:
        total += int(component_sizes.get(name, 0))
    return total


def is_over_budget(estimate_bytes: int, free_bytes: int, safety_margin_bytes: int = 0) -> bool:
    """True if ``estimate_bytes`` (+ margin) would not fit in ``free_bytes``
    (EC-6: pre-check before CLONE/DOWNLOAD)."""
    return (int(estimate_bytes) + int(safety_margin_bytes)) > int(free_bytes)


EXTENDED_LENGTH_PREFIX = "\\\\?\\"


def is_path_too_long(path: str, limit: int = 260) -> bool:
    """True if ``path`` would exceed the Windows MAX_PATH limit (EC-7).

    A path already carrying the extended-length ``\\\\?\\`` prefix is exempt
    (that prefix opts out of MAX_PATH). Otherwise flags at >= limit.
    """
    if path.startswith(EXTENDED_LENGTH_PREFIX):
        return False
    return len(path) >= limit


# ---------------------------------------------------------------------------
# Dev-install aliasing guard (design EC-16). Pure string-path predicate over
# already-absolute paths. Guards the destructive live primitives (settings
# overwrite, DLL copy, MM-cache delete) from ever targeting the read-only dev
# install or a path that overlaps it.
# ---------------------------------------------------------------------------


def _normcase_path(path: str) -> str:
    """Case- and separator-normalize a path for boundary comparison.

    Windows-targeted (KSP): fold case and unify separators to forward slashes,
    strip a trailing slash. Pure string manipulation; no filesystem access.
    """
    return (path or "").replace("\\", "/").rstrip("/").lower()


def is_path_within(child: str, parent: str) -> bool:
    """True if ``child`` equals ``parent`` or is nested under it (path-boundary
    aware: ``.../automation`` is NOT within ``.../auto``)."""
    c = _normcase_path(child)
    p = _normcase_path(parent)
    if not p:
        return False
    return c == p or c.startswith(p + "/")


@dataclass(frozen=True)
class InstanceDirDecision:
    ok: bool
    reason: str  # "ok" | "equals-dev-install" | "nested-in-dev-install"
                 # | "dev-install-nested-in-instance" | "not-under-automation"


def check_instance_dir_alias(instance_dir: str, dev_install: str, instance_rel: str) -> InstanceDirDecision:
    """Reject an instance dir that aliases or overlaps the dev install.

    ``instance_dir`` / ``dev_install`` are absolute paths; ``instance_rel`` is
    the profile's raw ``instanceDir`` value (which MUST live under
    ``automation/`` so an instance can never be mistaken for the dev tree). The
    live primitives overwrite settings.cfg, copy the DLL, and DELETE the MM
    cache -- pointed at the dev install (or a parent/child of it) they would
    corrupt the read-only clone source. Returns the first failing condition:
    equal, instance nested in dev install, dev install nested in instance, or
    the relative dir not under ``automation/``."""
    if is_path_within(instance_dir, dev_install) and is_path_within(dev_install, instance_dir):
        return InstanceDirDecision(False, "equals-dev-install")
    if is_path_within(instance_dir, dev_install):
        return InstanceDirDecision(False, "nested-in-dev-install")
    if is_path_within(dev_install, instance_dir):
        return InstanceDirDecision(False, "dev-install-nested-in-instance")
    rel = (instance_rel or "").replace("\\", "/")
    if not rel.startswith("automation/"):
        return InstanceDirDecision(False, "not-under-automation")
    return InstanceDirDecision(True, "ok")


# ---------------------------------------------------------------------------
# Lockfile acquire / reclaim (design EC-10). Pure over injected clock + pid +
# liveness probe.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class LockDecision:
    acquired: bool
    reason: str  # "acquired-free" | "reclaimed-stale" | "refused-live"
    holder_pid: Optional[int]


def acquire_lock(
    existing_lock: Optional[Dict],
    pid: int,
    now: float,
    is_alive_fn: Callable[[int], bool],
) -> LockDecision:
    """Decide whether ``pid`` may acquire the instance lock.

    ``existing_lock`` is None (no lock) or ``{"pid": int, "timestamp": float}``.
    - No lock -> acquire (``acquired-free``).
    - Lock held by this same pid -> acquire (re-entrant, ``acquired-free``).
    - Lock held by a LIVE other pid -> refuse (``refused-live``, EC-10 second
      concurrent run).
    - Lock held by a DEAD pid -> reclaim (``reclaimed-stale``).
    ``is_alive_fn(pid) -> bool`` and ``now`` are injected for purity.
    """
    if not existing_lock:
        return LockDecision(True, "acquired-free", pid)
    holder = existing_lock.get("pid")
    if holder == pid:
        return LockDecision(True, "acquired-free", pid)
    # A malformed holder pid (missing / None / non-integer -- a truncated or
    # hand-corrupted lockfile) is treated as STALE and reclaimed, never a crash:
    # a lockfile we cannot parse must not wedge every future run.
    try:
        holder_pid = int(holder)
    except (TypeError, ValueError):
        return LockDecision(True, "reclaimed-stale", None)
    if is_alive_fn(holder_pid):
        return LockDecision(False, "refused-live", holder_pid)
    return LockDecision(True, "reclaimed-stale", holder_pid)


# ---------------------------------------------------------------------------
# BUILD-TT source selection (design BUILD-TT / GT-2 / S-4). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class TestingToolsSourceSelection:
    included: List[str]
    dropped: List[str]
    autoloader_excluded: bool
    ok: bool
    reason: str


def select_testingtools_sources(available_files: Sequence[str]) -> TestingToolsSourceSelection:
    """Select the TWO shim source files from the TestingTools source set.

    Keeps OrbitTools.cs + TestingTools.cs; DROPS AutoLoadGame.cs and
    AutoSwitchVessel.cs so the seam's LoadGame boot owns save selection without
    a racing auto-loader (GT-2/GT-4). ``autoloader_excluded`` asserts
    AutoLoadGame.cs is NOT in the included set (S-4 build-time guard). ``ok`` is
    False if a required shim source is missing from ``available_files``.
    """
    avail = set(available_files)
    included = [f for f in TESTINGTOOLS_SHIM_SOURCES if f in avail]
    dropped = [f for f in TESTINGTOOLS_DROPPED_SOURCES if f in avail]
    autoloader_excluded = "AutoLoadGame.cs" not in included
    missing = [f for f in TESTINGTOOLS_SHIM_SOURCES if f not in avail]
    if missing:
        return TestingToolsSourceSelection(
            included, dropped, autoloader_excluded, False,
            "missing-shim-source:%s" % ",".join(missing),
        )
    if not autoloader_excluded:
        return TestingToolsSourceSelection(
            included, dropped, autoloader_excluded, False, "autoloader-not-excluded",
        )
    return TestingToolsSourceSelection(included, dropped, autoloader_excluded, True, "ok")


# ---------------------------------------------------------------------------
# Self-contained git-source resolution (module boundary / submodule readiness).
# BUILD-TT (git-show the shim sources) and PIN (peel-verify the pinned commit)
# read the git-pinned components from a MODULE-OWNED clone under
# harness/provision/.cache/<comp>-src, NOT the umbrella mods/ clone, so nothing
# under harness/ depends on a sibling checkout that a submodule split would leave
# behind. This pure decision picks the source; the shell probes the three
# booleans read-only and performs the clone / fetch. Pure.
# ---------------------------------------------------------------------------

# Cache subdir name (under .cache/) for each git-pinned component's source clone.
GIT_SOURCE_CACHE_DIRNAME: Dict[str, str] = {
    "krpc": "krpc-src",
    "krpc_mechjeb": "krpc_mechjeb-src",
}


@dataclass(frozen=True)
class GitSourceDecision:
    action: str        # use-override | reuse-cache | refetch-cache | clone
    source_dir: str    # the git dir the shell peels / git-shows from
    fetch: bool        # True when the shell must hit the network (clone/refetch)
    reason: str        # override-present | override-missing | cached-and-has-commit
                       #   | cached-stale | absent


def resolve_git_source(
    cache_dir: str,
    commit: str,
    override_path: Optional[str] = None,
    override_present: bool = False,
    cache_has_git: bool = False,
    cache_has_commit: bool = False,
) -> GitSourceDecision:
    """Decide where a git-pinned component's source is read from.

    Priority order (idempotent):
      - ``--krpc-src`` OVERRIDE wins when given (a dev pointing at an existing
        clone, e.g. the umbrella ``mods/krpc``); ``override_present`` reports
        whether it is actually a git clone so the shell can abort cleanly.
      - REUSE the module-owned ``cache_dir`` clone when it already contains the
        pinned ``commit`` (``cached-and-has-commit``) -- no network.
      - REFETCH when the cache clone exists but lacks the pinned commit
        (``cached-stale``: a moved pin or an interrupted earlier fetch).
      - CLONE when no cache clone exists yet (``absent``).

    Pure: the shell computes ``override_present`` / ``cache_has_git`` /
    ``cache_has_commit`` with read-only probes and executes the clone/fetch.
    """
    if override_path:
        return GitSourceDecision(
            "use-override", override_path, False,
            "override-present" if override_present else "override-missing")
    if cache_has_git and cache_has_commit:
        return GitSourceDecision("reuse-cache", cache_dir, False, "cached-and-has-commit")
    if cache_has_git:
        return GitSourceDecision("refetch-cache", cache_dir, True, "cached-stale")
    return GitSourceDecision("clone", cache_dir, True, "absent")


# ---------------------------------------------------------------------------
# OPEN-pin guard (design DOWNLOAD / EC-13). Pure.
# ---------------------------------------------------------------------------

OPEN_SENTINELS: Tuple[str, ...] = ("OPEN", "OPEN-fill-at-first-download")


def is_open_pin(value: Optional[str]) -> bool:
    """True if a pin field is an unresolved OPEN placeholder (EC-13: DOWNLOAD
    must compute+print the hash then ABORT, never install unverified bytes)."""
    if value is None:
        return True
    v = value.strip()
    return v == "" or v in OPEN_SENTINELS or v.startswith("OPEN")


# ---------------------------------------------------------------------------
# Live CLONE planning (design CLONE / EC-6 / EC-7). Pure over path strings and
# directory-entry name lists; the orchestrator does the actual copy / junction.
# ---------------------------------------------------------------------------

GAMEDATA_DIR = "GameData"
STREAMINGASSETS = "StreamingAssets"

# The marker written at the START of CLONE -- before the mutable surface, the
# junctions, and the stack payloads are written (the PREFLIGHT lockfile +
# provision-log are the only instance-local writes that precede it) -- and
# cleared LAST on VERIFY success (design EC-6): its presence means the instance
# is a half-provision the harness must refuse to admit (run.py reads it as
# `.provision-incomplete`).
PROVISION_INCOMPLETE_MARKER = ".provision-incomplete"

# Top-level dev-install entries never copied verbatim into a fresh instance:
#   GameData      -- built selectively (junction stock, copy dev-sourced, install
#                    stack, delete MM cache); handled by the GameData builder.
#   settings.cfg  -- authored by the SETTINGS phase from the profile deltas.
#   saves / Logs / Screenshots / temp -- mutable, harness- or run-owned; carrying
#                    dev state into an automation instance would poison a run.
#   KSP.log       -- the dev run's log; a fresh instance regenerates its own.
#   Player.log    -- the Unity player log (same rationale).
# Crash dumps (N20) are timestamped, so they are matched by prefix below.
CLONE_SKIP_TOPLEVEL: Tuple[str, ...] = (
    GAMEDATA_DIR, "settings.cfg", "saves", "Logs", "Screenshots", "temp",
    "KSP.log", "Player.log",
)

# Timestamped crash-dump artifacts KSP / Unity leave at the game root
# (``crash_2024-01-01_120000/`` folders, ``error.log`` companions). Matched by
# case-insensitive prefix since the exact name varies per crash (N20).
CLONE_SKIP_TOPLEVEL_PREFIXES: Tuple[str, ...] = ("crash_", "crash-", "crash ")


def clone_toplevel_disposition(name: str, ksp_data_dir_name: str = "KSP_x64_Data") -> str:
    """Classify a top-level dev-install entry for CLONE.

    Returns one of:
      - ``build-gamedata``: the GameData dir (junction stock + copy dev-sourced;
        the generic tree copy MUST skip it).
      - ``copy-tree-except-junction``: the KSP data dir (copied, but its
        ``StreamingAssets`` subtree is junctioned, not copied -- ``EC-6`` bulk).
      - ``skip``: settings.cfg / saves / Logs / Screenshots / temp / KSP.log /
        Player.log / any ``crash_*`` dump (N20).
      - ``copy``: everything else (exe, buildID64.txt, Internals, PDLauncher,
        top-level files -- the small mutable surface).
    """
    if name == GAMEDATA_DIR:
        return "build-gamedata"
    if name == ksp_data_dir_name:
        return "copy-tree-except-junction"
    if name in CLONE_SKIP_TOPLEVEL:
        return "skip"
    lname = name.lower()
    if any(lname.startswith(p) for p in CLONE_SKIP_TOPLEVEL_PREFIXES):
        return "skip"
    return "copy"


def ksp_data_entry_is_junction(name: str) -> bool:
    """True if an entry directly under the KSP data dir is junctioned (the bulk
    read-only ``StreamingAssets`` asset tree) rather than copied (design CLONE)."""
    return name == STREAMINGASSETS


def to_extended_length_path(path: str) -> str:
    """Return ``path`` with the Windows extended-length ``\\\\?\\`` prefix (EC-7 / R13).

    Deep KSP asset trees under a long umbrella root can exceed MAX_PATH (260); the
    ``\\\\?\\`` prefix opts the copy/junction primitives out of that limit. A UNC
    path (``\\\\server\\share``) becomes ``\\\\?\\UNC\\server\\share``. An already
    prefixed path is returned unchanged. Pure string manipulation -- the caller
    only applies it on Windows where a real path would overflow. The prefix
    requires backslash separators, so forward slashes are normalized."""
    if not path or path.startswith(EXTENDED_LENGTH_PREFIX):
        return path
    p = path.replace("/", "\\")
    if p.startswith("\\\\"):
        return "\\\\?\\UNC\\" + p[2:]
    return EXTENDED_LENGTH_PREFIX + p


def is_runtime_writable_dir(name: str) -> bool:
    """True for directory names KSP mods use as their RUNTIME-WRITABLE area
    (the ``PluginData`` convention: ModuleManager excludes it from GameDatabase,
    and mods write caches/settings there at play time -- e.g.
    KSPCommunityFixes/PluginData/TextureCache). Such dirs are pruned from the
    dev-sourced-mod content tree-hash (EC-3): the drift contract covers the
    AUTHORED payload, and hashing runtime writes would re-drift the instance on
    every game launch."""
    return name.lower() == "plugindata"


def canonical_tree_digest_input(entries: Sequence[Tuple[str, str]]) -> str:
    """Build the canonical string a dev-sourced mod's content tree-hash is taken
    over (design EC-3 drift check). ``entries`` is ``(relpath, filehash)`` pairs
    (the orchestrator per-file-hashes; this stays pure). Deterministic: relpaths
    are separator-normalized to ``/`` and sorted, so the same tree always yields
    the same digest input regardless of walk order or OS separator. The caller
    sha256s the returned string's UTF-8 bytes."""
    norm = sorted((rel.replace("\\", "/"), h) for rel, h in entries)
    return "\n".join("%s\0%s" % (rel, h) for rel, h in norm)


# ---------------------------------------------------------------------------
# BUILD-TT shim project generation (design BUILD-TT / GT-4 / GT-9 / S-4). Pure
# string builders; the orchestrator writes them to a temp dir and runs dotnet.
# ---------------------------------------------------------------------------

# KSP-shipped managed DLLs the TestingTools shim references (GT-4 usings:
# Assembly-CSharp for HighLogic/Vessel/FlightGlobals, UnityEngine[.CoreModule]
# for Vector3/GameObject, PhysicsModule for rigidbody access). HintPathed into
# the dev install's Managed dir (GT-9 pattern).
TESTINGTOOLS_KSP_MANAGED_REFS: Tuple[str, ...] = (
    "Assembly-CSharp",
    "Assembly-CSharp-firstpass",
    "UnityEngine",
    "UnityEngine.CoreModule",
    "UnityEngine.PhysicsModule",
)

# kRPC compile references (KRPC.Service attributes live in KRPC.Core;
# Services.Vessel in KRPC.SpaceCenter; Google.Protobuf transitively). HintPathed
# into the extracted release zip's GameData/kRPC/ (GT-4).
TESTINGTOOLS_KRPC_REFS: Tuple[str, ...] = (
    "KRPC.Core",
    "KRPC.SpaceCenter",
    "Google.Protobuf",
)


def render_testingtools_assemblyinfo(assembly_name: str = "TestingTools") -> str:
    """Author the minimal AssemblyInfo.cs that replaces the bazel-generated one
    (GT-4). Only the assembly title/product; kRPC service discovery keys off the
    ``[KRPCService]`` attributes in the compiled types, not assembly metadata."""
    return (
        "using System.Reflection;\n"
        '[assembly: AssemblyTitle("%s")]\n'
        '[assembly: AssemblyProduct("%s")]\n'
        '[assembly: AssemblyVersion("0.5.4.0")]\n'
    ) % (assembly_name, assembly_name)


def _csproj_reference(name: str, hint_path: str) -> str:
    return (
        '    <Reference Include="%s">\n'
        "      <HintPath>%s</HintPath>\n"
        "      <Private>false</Private>\n"
        "    </Reference>\n"
    ) % (name, hint_path)


def render_testingtools_shim_csproj(
    managed_dir: str,
    krpc_gamedata_dir: str,
    source_files: Sequence[str],
    target_framework: str = "net472",
    ksp_managed_refs: Sequence[str] = TESTINGTOOLS_KSP_MANAGED_REFS,
    krpc_refs: Sequence[str] = TESTINGTOOLS_KRPC_REFS,
    assemblyinfo_file: str = "AssemblyInfo.cs",
) -> str:
    """Author the standalone SDK-style TestingTools shim csproj (GT-4 / GT-9).

    Compiles ONLY the explicitly listed ``source_files`` (the 2-file shim +
    AssemblyInfo) by turning off default compile globbing, so an AutoLoadGame.cs
    that happens to sit in the build dir can never be swept in (S-4 defense in
    depth alongside the reflection assertion). References the KSP managed DLLs
    from ``managed_dir`` and the kRPC compile DLLs from ``krpc_gamedata_dir``
    via HintPath with ``<Private>false</Private>`` (output is TestingTools.dll
    alone, no copied dependencies). ``TargetFramework`` is net472 (KSP's runtime).
    """
    refs = "".join(
        _csproj_reference(r, "%s\\%s.dll" % (managed_dir, r)) for r in ksp_managed_refs
    ) + "".join(
        _csproj_reference(r, "%s\\%s.dll" % (krpc_gamedata_dir, r)) for r in krpc_refs
    )
    compiles = "".join('    <Compile Include="%s" />\n' % f for f in source_files)
    compiles += '    <Compile Include="%s" />\n' % assemblyinfo_file
    return (
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        "  <PropertyGroup>\n"
        "    <TargetFramework>%s</TargetFramework>\n"
        "    <AssemblyName>TestingTools</AssemblyName>\n"
        "    <RootNamespace>TestingTools</RootNamespace>\n"
        "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n"
        "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n"
        "    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>\n"
        "    <Deterministic>true</Deterministic>\n"
        "    <NoWarn>0618</NoWarn>\n"
        "  </PropertyGroup>\n"
        "  <ItemGroup>\n"
        "%s"
        "  </ItemGroup>\n"
        "  <ItemGroup>\n"
        "%s"
        "  </ItemGroup>\n"
        "</Project>\n"
    ) % (target_framework, refs, compiles)


def count_utf8(data: bytes, s: str) -> int:
    """Count UTF-8 occurrences of ``s`` in ``data``. ECMA-335 stores metadata
    type/member names as UTF-8 in the ``#Strings`` heap, so a UTF-8 grep of the
    built assembly's bytes is a dependency-free reflection proxy (S-4)."""
    if not s:
        return 0
    return data.count(s.encode("utf-8"))


@dataclass(frozen=True)
class BuildTtAssertion:
    ok: bool
    autoloadgame_count: int
    has_testingtools_type: bool
    reason: str


def evaluate_build_tt_assembly(dll_bytes: bytes) -> BuildTtAssertion:
    """Assert the built TestingTools.dll is the 2-file shim (S-4 / GT-4).

    Fails if the ``AutoLoadGame`` type name is present in the assembly metadata
    (the dropped auto-loader that would race the seam's LoadGame boot) OR the
    ``TestingTools`` type name is absent (a broken/empty build). Uses a UTF-8
    metadata grep as a dependency-free reflection proxy over the ACTUAL built
    bytes -- the design's authoritative capability guard, done without a .NET
    runtime in Python."""
    auto = count_utf8(dll_bytes, "AutoLoadGame")
    has_tt = count_utf8(dll_bytes, "TestingTools") > 0
    if auto > 0:
        return BuildTtAssertion(False, auto, has_tt, "autoloadgame-present")
    if not has_tt:
        return BuildTtAssertion(False, auto, has_tt, "testingtools-type-absent")
    return BuildTtAssertion(True, auto, has_tt, "ok")


# ---------------------------------------------------------------------------
# INSTALL zip-layout mapping (design INSTALL / GT-5). Pure over a zip namelist.
# ---------------------------------------------------------------------------


def _zip_dest(component: str, entry: str) -> Optional[str]:
    """Map one zip entry to its instance-relative destination, or None to skip.

    - kRPC: the whole ``GameData/kRPC/`` subtree lands as-is (the zip already
      carries the GameData prefix, GT-5).
    - KRPC.MechJeb: only the prebuilt ``KRPC.MechJeb.dll`` (+ its ``.json``
      service definition) at the zip root, placed into ``GameData/kRPC/`` per its
      README; the language-binding sources / README / LICENSE are dropped.
    - MechJeb2: entries already under ``GameData/`` land as-is; a bare-rooted
      zip is wrapped under ``GameData/`` (defensive -- MechJeb2's layout is
      confirmed at first download, the pin is OPEN today).
    """
    e = entry.replace("\\", "/")
    low = e.lower()
    if component == "krpc":
        return e if low.startswith("gamedata/krpc/") else None
    if component == "krpc_mechjeb":
        base = e.rsplit("/", 1)[-1]
        if base in ("KRPC.MechJeb.dll", "KRPC.MechJeb.json"):
            return "GameData/kRPC/" + base
        return None
    if component == "mechjeb2":
        return e if low.startswith("gamedata/") else "GameData/" + e
    return None


def gamedata_dest_escapes(dest_rel: str) -> bool:
    """True if a planned extraction destination, once normalized, escapes the
    instance ``GameData/`` root -- a zip-slip ``../`` entry (SF5).

    Computed with ``posixpath.normpath`` on the entry BEFORE the orchestrator
    joins it to the instance dir, so ``GameData/kRPC/../../evil`` (collapses to
    ``evil``) and a MechJeb2 ``../..`` entry (``GameData/../../evil`` ->
    ``../evil``) are both rejected. An absolute path or one that walks above
    GameData/ escapes; anything that stays under ``GameData/`` is safe."""
    rel = (dest_rel or "").replace("\\", "/")
    norm = posixpath.normpath(rel)
    if norm.startswith("/") or norm == ".." or norm.startswith("../"):
        return True
    return not (norm == GAMEDATA_DIR or norm.startswith(GAMEDATA_DIR + "/"))


def plan_zip_install(component: str, names: Sequence[str]) -> List[Tuple[str, str]]:
    """Return the ordered ``(zip_entry, dest_relpath)`` extraction plan for a
    stack component's release zip, skipping directory entries and anything
    outside the component's GameData footprint (design INSTALL)."""
    out: List[Tuple[str, str]] = []
    for n in names:
        if n.endswith("/"):
            continue
        dest = _zip_dest(component, n)
        if dest:
            out.append((n, dest))
    return out


def krpc_installed_dll_names(pin: Dict) -> List[str]:
    """The kRPC runtime DLLs whose per-file hashes go into the manifest's
    ``installedDlls`` (design manifest). Falls back to the compile-ref subset."""
    return list(pin.get("releaseRuntimeDlls") or pin.get("releaseCompileDlls") or [])


# ---------------------------------------------------------------------------
# --repair convergence (design VERIFY / EC-3). Pure diff -> targeted work set.
# ---------------------------------------------------------------------------


SETTINGS_REPAIR_TOKEN = "__settings__"


def component_of_diff_field(field: str) -> Optional[str]:
    """Map a manifest-diff field path to the work item that repairs it.

    ``components.<name>....`` -> the component name; ``settingsDeltasApplied....``
    -> the settings sentinel; ``devSourcedMods.<name>`` -> ``devmod:<name>``.
    Anything else -> None (not repairable by a targeted re-install; a full
    re-provision is the fallback the caller logs)."""
    parts = (field or "").split(".")
    if len(parts) >= 2 and parts[0] == "components":
        return parts[1]
    if parts and parts[0] == "settingsDeltasApplied":
        return SETTINGS_REPAIR_TOKEN
    if len(parts) >= 2 and parts[0] == "devSourcedMods":
        return "devmod:" + parts[1]
    return None


@dataclass(frozen=True)
class RepairPlan:
    components: Tuple[str, ...]      # stack/parsek components to re-install
    dev_mods: Tuple[str, ...]       # dev-sourced mod folders to re-copy
    settings: bool                  # re-apply settings deltas
    unrepairable: Tuple[str, ...]   # diff fields with no targeted repair


def plan_repair(diffs: Sequence["ManifestDiff"]) -> RepairPlan:
    """Turn a VERIFY drift diff into the minimal targeted work set (design
    --repair: re-install ONLY the drifted component, then re-VERIFY). A drift
    field that maps to no work item is surfaced in ``unrepairable`` so the caller
    can fall back to a full re-provision rather than silently converging nothing."""
    components: set = set()
    dev_mods: set = set()
    settings = False
    unrepairable: set = set()
    for d in diffs:
        item = component_of_diff_field(d.field)
        if item is None:
            unrepairable.add(d.field)
        elif item == SETTINGS_REPAIR_TOKEN:
            settings = True
        elif item.startswith("devmod:"):
            dev_mods.add(item[len("devmod:"):])
        else:
            components.add(item)
    return RepairPlan(
        components=tuple(sorted(components)),
        dev_mods=tuple(sorted(dev_mods)),
        settings=settings,
        unrepairable=tuple(sorted(unrepairable)),
    )


# ---------------------------------------------------------------------------
# SF9: idempotent re-run hash short-circuit (design SF9). Pure per-component
# skip decision.
#
# Re-running provision.py against an already-provisioned, non-drifted instance
# used to redo every copy / build / extract. SF9 lets each heavy phase
# hash-short-circuit: skip the work when a prior COMPLETE provision exists and
# the thing that WOULD be installed already equals what is on disk. The pure
# decision compares two hashes; the caller picks their meaning per phase --
# manifest-recorded hash for the pinned components (kRPC / MechJeb2 zips, whose
# bytes are fixed by the pin) and the fresh SOURCE hash for the components whose
# input changes between runs (the rebuilt Parsek.dll, the freshly built
# TestingTools.dll) -- so a changed source still re-installs. VERIFY always runs
# fully (it is the proof); SF9 only elides redundant writes, never the check.
# ---------------------------------------------------------------------------

# Skip-decision reason codes (batch-summary logging + tests).
SKIP_NO_PRIOR = "no-prior-complete-manifest"     # first provision / half-provision -> do
SKIP_NOT_RECORDED = "hash-not-recorded"          # nothing to compare against -> do
SKIP_ABSENT_ON_DISK = "absent-on-disk"           # target file gone -> do (re-install)
SKIP_HASH_DRIFT = "hash-drift"                    # on-disk differs from expected -> do
SKIP_HASH_MATCH = "hash-match"                    # already installed identically -> SKIP


@dataclass(frozen=True)
class SkipDecision:
    skip: bool
    reason: str  # one of the SKIP_* codes above


def decide_idempotent_skip(prior_complete: bool, expected_hash: Optional[str],
                           current_hash: Optional[str]) -> SkipDecision:
    """Decide whether a heavy phase may skip its copy / build / extract (SF9).

    ``prior_complete`` gates the whole fast path: it must be True (a prior
    provision wrote a manifest AND there is no ``.provision-incomplete`` marker)
    or the phase always does its work -- a first run or a half-provision never
    trusts on-disk state. ``expected_hash`` is what a fresh provision WOULD put
    on disk (a manifest-recorded pinned hash, or a fresh source hash); a falsy
    value means there is nothing to compare, so do the work. ``current_hash`` is
    the on-disk hash (None when the target is absent). Skip ONLY when a prior
    complete provision exists and the on-disk hash already equals the expected
    hash; every other case re-does the work so a drifted or absent component is
    always re-provisioned exactly as today."""
    if not prior_complete:
        return SkipDecision(False, SKIP_NO_PRIOR)
    if not expected_hash:
        return SkipDecision(False, SKIP_NOT_RECORDED)
    if current_hash is None:
        return SkipDecision(False, SKIP_ABSENT_ON_DISK)
    if current_hash == expected_hash:
        return SkipDecision(True, SKIP_HASH_MATCH)
    return SkipDecision(False, SKIP_HASH_DRIFT)


def installed_map_digest(name_to_hash: Dict[str, str]) -> str:
    """Canonical digest over a component's installed-DLL name->sha256 map, so a
    multi-file component (kRPC's several DLLs) compares as one hash in the SF9
    skip decision. Reuses the order/separator-independent tree-digest form; an
    empty map yields the digest of the empty input."""
    return canonical_tree_digest_input(
        [(name, h or "") for name, h in (name_to_hash or {}).items()])


def mutable_surface_stat_matches(recorded_stat: Optional[Dict],
                                 current_count: int, current_bytes: int) -> bool:
    """SF9/S2: True iff the recorded instance ``KSP_x64_Data/Managed`` stat
    (fileCount + total bytes) equals the freshly re-scanned current stat.

    The CLONE mutable-surface skip cannot trust ``buildID64.txt`` alone as a proxy
    for the whole copied surface: a partially-deleted instance (a stock Managed
    DLL removed) or a swapped-in DLL of a different size leaves buildID64 intact
    yet the surface corrupt, and VERIFY never re-hashes the stock Managed tree, so
    a false skip would go uncaught (reviewer S2). This is the cheap fresh
    instance-integrity check the skip gate re-runs each time.

    Returns False when there is no recorded stat (an old / pre-S2 manifest) so the
    skip re-copies once to arm the check. Returns False on any file-count or
    total-size change (a deleted / added / resized Managed file). RESIDUAL RISK
    (documented, design SF9): an in-place edit of a stock Managed DLL that
    preserves BOTH the file count and the exact byte size is not caught by this
    count+size stat; catching that would need a per-file content or size+mtime
    digest (the sounder-but-costlier option the design records as deferred)."""
    if not recorded_stat:
        return False
    return (recorded_stat.get("fileCount") == current_count
            and recorded_stat.get("bytes") == current_bytes)


# ---------------------------------------------------------------------------
# Dry-run action plan model (design --dry-run). Pure builder so the plan is
# testable and identical whether printed or (in a live run) executed.
# ---------------------------------------------------------------------------


@dataclass
class PlannedAction:
    step: str
    verb: str  # FETCH | BUILD | CLONE | JUNCTION | COPY | WRITE | DELETE | VERIFY
    detail: str


def build_action_plan(pins: Dict, profile: Dict) -> List[PlannedAction]:
    """Build the ordered action plan for a profile without any I/O.

    Mirrors the Behavior phases (PIN/FETCH/BUILD-TT/CLONE/SETTINGS/DEPLOY/
    VERIFY/MANIFEST). Used by --dry-run to print exactly what a live run would
    do, and by tests to assert plan coherence.
    """
    plan: List[PlannedAction] = []
    krpc = pins.get("krpc", {})
    mj = pins.get("mechjeb2", {})
    kmj = pins.get("krpc_mechjeb", {})

    plan.append(PlannedAction("PIN", "VERIFY",
        "krpc %s -> %s (peel annotated tag ^{commit})" % (krpc.get("tag"), krpc.get("commit"))))
    plan.append(PlannedAction("PIN", "VERIFY",
        "krpc_mechjeb %s %s -> %s" % (kmj.get("fork"), kmj.get("tag"), kmj.get("commit"))))

    plan.append(PlannedAction("FETCH", "FETCH",
        "krpc release zip %s (sha256 %s)" % (krpc.get("releaseZipUrl"), krpc.get("releaseZipSha256"))))
    plan.append(PlannedAction("FETCH", "FETCH",
        "mechjeb2 build %s (%s, sha256 %s)" % (mj.get("buildNumber"), mj.get("downloadUrl"), mj.get("sha256"))))

    sel = select_testingtools_sources(
        list(TESTINGTOOLS_SHIM_SOURCES) + list(TESTINGTOOLS_DROPPED_SOURCES))
    plan.append(PlannedAction("BUILD-TT", "BUILD",
        "TestingTools.dll from %s (drop %s); assert AutoLoadGame absent"
        % (",".join(sel.included), ",".join(sel.dropped))))

    plan.append(PlannedAction("PAIR", "VERIFY",
        "krpc_mechjeb fork=%s tag=%s paired with krpc %s"
        % (kmj.get("fork"), kmj.get("tag"), kmj.get("pairedKrpcTag"))))

    instance_dir = profile.get("instanceDir", "?")
    plan.append(PlannedAction("CLONE", "CLONE",
        "copy dev install -> %s (mutable surface)" % instance_dir))
    for tree in STOCK_JUNCTION_TREES:
        plan.append(PlannedAction("CLONE", "JUNCTION",
            "%s/%s -> devInstall" % (instance_dir, tree)))
    for name in STOCK_JUNCTION_GAMEDATA:
        plan.append(PlannedAction("CLONE", "JUNCTION",
            "%s/GameData/%s -> devInstall (stock payload)" % (instance_dir, name)))
    for name in profile.get("devSourcedMods", []) or []:
        plan.append(PlannedAction("CLONE", "COPY",
            "%s/GameData/%s (dev-sourced, content-hashed)" % (instance_dir, name)))

    deltas = profile.get("settings", {}) or {}
    plan.append(PlannedAction("SETTINGS", "WRITE",
        "settings.cfg with %d delta(s): %s" % (len(deltas), ", ".join(sorted(deltas)))))

    plan.append(PlannedAction("DEPLOY", "COPY",
        "Parsek.dll: source -> .stage (hash) -> %s/GameData/Parsek/Plugins (verify hash + UTF-16 grep)"
        % instance_dir))

    for name in profile.get("stackComponents", []) or []:
        if name == "parsek":
            continue
        plan.append(PlannedAction("INSTALL", "COPY",
            "%s -> %s/%s (stack component)"
            % (name, instance_dir, stack_component_install_folder(name))))

    if "krpc" in (profile.get("stackComponents", []) or []):
        plan.append(PlannedAction("INSTALL", "WRITE",
            "%s/GameData/kRPC/PluginData/settings.cfg hands-free stamp "
            "(autoStartServers=True, autoAcceptConnections=True, confirmRemoveClient=False)"
            % instance_dir))

    for cache in MM_CACHE_FILES:
        plan.append(PlannedAction("MM-CACHE", "DELETE",
            "%s/GameData/%s (regenerate on first boot)" % (instance_dir, cache)))

    plan.append(PlannedAction("MANIFEST", "WRITE",
        "%s/GameData/Parsek/provision-manifest.json (atomic tmp+rename)" % instance_dir))
    plan.append(PlannedAction("VERIFY", "VERIFY",
        "re-read instance; cross-check DLL hashes, UTF-16 grep, junctions, settingsFinalSha256, "
        "krpcSettingsSha256, buildId64Sha256"))
    return plan
