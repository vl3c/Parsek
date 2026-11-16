#!/usr/bin/env python3
"""Harvest a FORGE-produced save into a committed fixture.

A FIXTURE-FORGE run leaves a KSP save holding the state some scenario needs as
its START state, persisted as `persistent.sfs`. This tool copies that produced
save, PRUNES Parsek state (so the fixture carries zero prior recordings /
backups), NORMALIZES the title, and writes it under
`harness/fixtures/saves/<target-name>/`.

It is the headless replacement for the operator fixture flight (2026-07-22
operator-principle override): the automation forges its own state.

GENERIC over the three forges shipped so far -- the produced save's SITUATION is
not assumed anywhere in this tool:

  FORGE-bdock-station (forge_station)  --target-name bdock-station-pad
      the docking Kerbal X pre-placed on the LaunchPad (PRELAUNCH).
  FORGE-eva3-pad      (forge_station)  --target-name eva3-pad-3crew
      the same pad state with three named crew aboard.
  FORGE-eva2-lko      (forge_lko)      --target-name eva2-lko-crewed
      a CREWED orbital stage parked in a ~100 km circular Kerbin orbit
      (ORBITING, not PRELAUNCH).
  CS-0-new-science-game (NewGame)    --target-name cs-science-new
      --allow-no-vessel --expect-mode SCIENCE_SANDBOX: the scripted campaign's
      genuine stock New Game, a Space Center start with no vessel at all (a
      later campaign stage adds --keep-parsek to carry its recordings on).
  FORGE-gs1-two-stage (forge_station)  --target-name gs1-two-stage-pad
      the GS-1 two-stage auto-chute booster on the LaunchPad (PRELAUNCH), one
      named crew. NO NEW TOOL was written for it: `--target-name` and
      `--expect-situation` were already the only two things that differed, which
      is what the generic-over-the-forges claim above is for. A FIFTH forge should
      reuse this the same way rather than fork it.

The only pad-shaped thing left is the OPTIONAL sanity gate: pass
`--expect-situation ORBITING` (or any comma-separated set) to require that the
save's ACTIVE vessel is in one of those situations before the fixture is written.
Omitted, the gate is off and the behaviour is exactly what the two pad forges
have always had (activeVessel present + at least one VESSEL node). The active
vessel's name + situation are ALWAYS logged, so an operator sees what was stamped
even without the gate.

`--keep-parsek` inverts the Parsek prune for RECORDED-STATE fixtures: a lane
whose committed fixture IS a produced save carrying committed recordings (the
B17 duna-direct recording the re-aim option-3 validation consumes) needs the
save's `Parsek/` directory -- the recording sidecars (`Recordings/*.prec` /
`*_vessel.craft` / `*_ghost.craft` / `*.pcrf`) live there, and pruning them
would strip the fixture down to metadata pointing at nothing. The flag keeps
`Parsek/` (and copies it as a kept directory) while STILL pruning the
`.parsek-backup*` staging dirs, which are safety copies no fixture should
carry. Default OFF: every pad/orbital START-state fixture stays Parsek-clean
exactly as before.

Usage:
    # PREFERRED: harvest from the run's OWN snapshot, which run.py copies out of
    # the instance before releasing the machine lock. It cannot be overwritten by
    # a sibling run, unlike the instance save (see the produced-save clobber race
    # in the todo doc). The path is results/<runId>_save/, and the runId is the
    # one in results/<runId>.json (its "snapshot" block names the dir).
    python harness/tools/harvest_bdock_station.py \
        --save-dir harness/results/<runId>_save
    # The live instance save still works, and is what to use when the snapshot
    # was skipped (the run's snapshot.reason says why). It is a RACE: a sibling
    # run staging the same saveTemplate leaf deletes it.
    #   <ksp-instance>/saves/bdock-forge-base/
    python harness/tools/harvest_bdock_station.py --save-dir <path-to-produced-save>
    # or point at the instance root + the run-save name:
    python harness/tools/harvest_bdock_station.py --instance <ksp-instance> \
        --run-save bdock-forge-base
    # the orbital forge, with the situation gate armed:
    python harness/tools/harvest_bdock_station.py --instance <ksp-instance> \
        --run-save bdock-forge-base --target-name eva2-lko-crewed \
        --expect-situation ORBITING
    # a RACE lane (multiplayer T-59): the save AND the race shared folder the run
    # snapshotted, the folder into harness/fixtures/race/<race-name>/:
    python harness/tools/harvest_bdock_station.py \
        --save-dir harness/results/<runId>_save --target-name <save-fixture> \
        --keep-parsek --race-folder harness/results/<runId>_race --race-name <n>

THE RACE HARVEST (`--race-folder` + `--race-name`, multiplayer plan T-59). A race
lane leaves two things: the produced save and the race shared folder run.py copied
to `results/<runId>_race/` (with its listing `results/<runId>_race.listing.txt`).
The flag copies that folder to `harness/fixtures/race/<race-name>/`, the folder a
later lane stages with `[fixture.race] sharedFolder = "fixtures/race/<race-name>"`;
a flown player folder is design 15.10's recorded opponent. Before anything is
written it refuses a folder that is not a `*_race` folder inside `harness/results/`
(symlinks resolved, `..` refused, an absolute path elsewhere refused, no link
inside the folder) and a name `[fixture.race]` could not stage, and it checks the
folder against its listing and against the shared-folder grammar `raceparse`
reads (`race_folder_problems`; the chain facets wait for T-21, see
`race_chain_facets`). The save half is the ordinary harvest above, with one prune
added for every race save: the rebuildable rival cache `Parsek/Race/rivals/`
(design 4.4: a cache, never part of the save) is never copied, so no rival
`.prec.txt` lands where `OptimizerTransferCohesionTests` globs `fixtures/saves/`.
The rest of `Parsek/Race/` (`local.cfg`, `race.cfg`, `own/`) is kept.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
from typing import Dict, List, NamedTuple, Optional, Sequence, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)                       # harness/
_HARNESS_LIB = os.path.join(_HARNESS_ROOT, "lib")
if _HARNESS_LIB not in sys.path:
    sys.path.insert(0, _HARNESS_LIB)

import hlib  # noqa: E402
import raceparse  # noqa: E402

_FIXTURES_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")
# The race harvest: where a run's race folder snapshot may come from, and where the
# committed race shared-folder fixtures live (`run.RACE_FIXTURES_DIR`).
_RESULTS_DIR = os.path.join(_HARNESS_ROOT, "results")
_FIXTURES_RACE = os.path.join(_HARNESS_ROOT, "fixtures", "race")
# The rebuildable rival cache inside a race save, `<save>/Parsek/Race/rivals/`
# (design 4.1 / 4.4). Matched at exactly this place under the save root, so a
# directory of the same name anywhere else in the save is kept.
_RIVAL_CACHE_PARTS = ("Parsek", "Race", "rivals")
# The top level of a race shared folder (design 4.1): the race file and players/.
_RACE_FOLDER_RACE_FILE = "race.cfg"
_RACE_FOLDER_PLAYER_FILE = "player.cfg"
_RACE_FOLDER_STANDINGS_DIR = "standings"
# The shared craft library. A produced save's Ships/VAB carries whatever craft the
# forge launched, which is usually a craft the library ALREADY holds -- harvesting
# it verbatim is how the fixture tree accumulated 27 duplicate copies in the first
# place. Step 3b below drops such a copy and tells the operator to declare the
# fixture a consumer in shared-ships.toml instead.
_FIXTURES_SHIPS = os.path.join(_HARNESS_ROOT, "fixtures", "ships")

# Files copied verbatim from the produced save (the bootable pad state + the craft
# the Interceptor re-launches). Everything else (quicksaves, Parsek state,
# pre-Parsek backups) is deliberately excluded.
_KEEP_FILES = ("persistent.sfs", "persistent.loadmeta")
# Subdirectories copied verbatim (Ships/VAB carries the Kerbal X.craft; AddOns is
# the stock scaffolding the sibling fixtures also commit).
_KEEP_DIRS = ("Ships", "AddOns")
# Directories that must NEVER end up in the fixture (Parsek recordings / rewind
# points / journals, and pre-Parsek safety backups the mod drops).
_PRUNE_DIR_NAMES = ("Parsek",)
_PRUNE_DIR_PREFIXES = (".parsek-backup", ".parsek-backup-staging")
# Readable SNAPSHOT mirrors are never committed to a fixture (see
# _ignore_pruned_keep_parsek for why, and CommittedFixtureMirrorTests for the
# gate). `.prec.txt` is deliberately NOT in this tuple - the trajectory mirror IS
# committed, because scenario headers cite values out of it.
_PRUNE_MIRROR_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")
# Directories under Parsek/ that a fixture must never carry. `Saves` is where
# Parsek writes the legacy Rewind-to-LAUNCH quicksaves (`parsek_rw_*.sfs`) AND the
# career-start snapshot (`parsek_career_start.sfs`, via the same
# `RecordingPaths.BuildRewindSaveRelativePath` root). --keep-parsek used to copy
# the directory verbatim: 137,355 lines over 7 files that no spec and no seam verb
# reaches, and that the one analyzer rule which looks (`Inv9RewindPoint`) only
# existence-checks. No committed fixture carries a career-start snapshot today, so
# pruning the directory wholesale is safe; a fixture that ever needs one must
# narrow this to the `parsek_rw_` prefix rather than widen the keep set. NOT to be
# confused with `RewindPoints`, which IS payload -
# `RewindInvoker.PartLoaderPrecondition.Check` deep-parses those.
_PRUNE_PARSEK_SUBDIRS = ("Saves",)
# Directories under Parsek/Recordings/ that a fixture must never carry.
# `_quarantine` is `RecordingFileJanitor.OrphanQuarantineDirName`: where
# `CleanOrphanFiles` PARKS orphaned sidecars instead of deleting them, so a
# long-lived hand-played save accumulates every sidecar whose recording the
# store could no longer resolve. It is recovery exhaust by construction - the
# store's own top-level-only scans never descend into it (see the doc comment on
# `OrphanQuarantineDirName`), so nothing a fixture consumer reads can reach it.
# Found by `duna-one-recorded` (the first harvest of an operator's free-play
# save, 2026-08-25), which carried 12 MB over 375 files of it - larger than the
# whole recorded payload the fixture exists for. Mirrors the `Parsek/Saves`
# clause below and is gated the same way, by
# `CommittedFixtureQuarantineTests` in harness/lib/test_hlib.py.
# `_versions` is `ParsekFileCollector`'s retained-version store
# (`_versions/<id>.<hash><suffix>`): earlier sidecar versions kept only while a
# referrer save (`*.sfs`, `Backup/*.sfs`) names their (id, hash). A harvest copies
# persistent.sfs alone, whose RECORDING rows name the HEAD hashes, so every retained
# version in a fixture is unnamed and the collector would delete it on the first
# load. Found by `cs-science-flea` (CS-1's produced save, 2026-10-26): three
# versions of its one recording, each a .prec with no readable mirror.
# EXCEPT a version the harvested persistent.sfs DOES name: a save that holds a
# committed tree AND its resumed in-flight copy (`isActive = True`, written after
# the committed-spawned restore resumed the tree on the live vessel) names the
# HEAD hash on the in-flight copy's rows but an OLDER hash on the committed
# copy's, and that older body lives only in `_versions`. Pruning it makes the
# load reject the committed tree (`stale-sidecar-epoch`) and a seam DiscardTree
# then reaps the whole tree. Found by `duna-ike-recorded` (B36, 2026-11-07).
# `_restore_named_versions` copies exactly those files back after the copy.
_PRUNE_RECORDINGS_SUBDIRS = ("_quarantine", "_versions")
_VERSION_FILE_RE = re.compile(r"^([0-9a-f]{32})\.([0-9a-f]{16})(?:\.prec|_vessel\.craft|_ghost\.craft)$")
_SFS_SIDECAR_HASH_RE = re.compile(r"^\s*(?:prec|vessel|ghost)Hash\s*=\s*([0-9a-f]{16})\s*$", re.M)
_SFS_RECORDING_ID_RE = re.compile(r"^\s*recordingId\s*=\s*([0-9a-f]{32})\s*$", re.M)


def log(msg: str) -> None:
    sys.stdout.write("[Harvest] %s\n" % msg)


def resolve_save_dir(args) -> str:
    if args.save_dir:
        return os.path.abspath(args.save_dir)
    if args.instance:
        return os.path.abspath(os.path.join(args.instance, "saves", args.run_save))
    raise SystemExit("harvest: pass --save-dir <path> OR --instance <dir> [--run-save NAME]")


# A `<key> = parsek_rw_<id>` line: any key, judged by the VALUE shape. The product
# writes two such keys - `rewindSave` on a RECORDING node
# (`RecordingTreeRecordCodec`) and `resumeRewindSave` on the PARSEK_ACTIVE_TREE
# resume node (`ParsekScenario`) - and a re-fly restores the second one, so a rule
# keyed on one spelling misses the reference that actually bites. Matching the
# value is the rule `CommittedFixtureRewindSaveTests` applies to the corpus.
_REWIND_SAVE_VALUE_RE = re.compile(r"^(\s*)(\w+)\s*=\s*parsek_rw_\w+\s*$")
_REWIND_SAVE_TOKEN = "parsek_rw_"


def strip_rewind_save_hints(text: str):
    """Clear every `<key> = parsek_rw_*` value; returns (text, cleared, leftovers).

    WHY THIS EXISTS. `_PRUNE_PARSEK_SUBDIRS` drops `Parsek/Saves` wholesale, so
    the Rewind-to-LAUNCH quicksaves never reach a fixture -- but anything that
    named one kept pointing at the pruned file. That is a DANGLING REFERENCE, and
    the committed contract forbids both halves of it:
    `CommittedFixtureRewindSaveTests` forbids the payload, the pointer in
    persistent.sfs, and ANY `parsek_rw_*` reference inside a
    `Parsek/RewindPoints/*.sfs` quicksave, so a fixture must carry NEITHER.

    The RewindPoint quicksaves are in scope because each embeds its own copy of
    the ParsekScenario: a re-fly restores `resumeRewindSave` out of it, copies it
    onto the tree root, and Inv9RewindPoint reports the dangling name
    (`missing-rewind-save-provisional` on a CommittedProvisional recording). The
    product never leaves that state - its own save still HAS the payload - it is
    the prune that makes the name dangle, so the prune clears it.

    Only the VALUE is cleared, not the key: the line is part of the serialized
    shape (RewindPoint quicksaves are deep-parsed payload with pinned line
    counts), and Parsek reads an empty value exactly like an absent one - it
    writes neither key when the value is empty.

    `leftovers` lists every line that still names `parsek_rw_` after the clear
    (a reference in some other shape); the caller refuses to commit those rather
    than guess at an edit. Line endings are preserved: a CR before the LF stays."""
    out, cleared, leftovers = [], 0, []
    for lineno, line in enumerate(text.split("\n"), 1):
        body, cr = (line[:-1], "\r") if line.endswith("\r") else (line, "")
        m = _REWIND_SAVE_VALUE_RE.match(body)
        if m:
            out.append(m.group(1) + m.group(2) + " = " + cr)
            cleared += 1
            continue
        if _REWIND_SAVE_TOKEN in body:
            leftovers.append("%d: %s" % (lineno, body.strip()))
        out.append(line)
    return "\n".join(out), cleared, leftovers


# KSP writes the launch command line into every save's `envInfo` line. Since the
# harness launches with `-logFile <instance>/Player.log` (parallel install slots),
# that argument carries the operator's absolute install path - their home folder
# on Windows - into every produced save, and this repository is public. KSP
# rewrites `envInfo` on every save and nothing reads it, so the harvest resets the
# Args to the no-argument form the older fixtures carry.
_ENV_INFO_ARGS_RE = re.compile(r"^(\s*envInfo = .*?Args: )(.*)$")
ENV_INFO_BARE_ARGS = "KSP_x64.exe  - "


def scrub_env_launch_args(text: str):
    """Reset the Args of every `envInfo` line to the bare form; returns
    (text, scrubbed). Line endings are preserved: a CR before the LF stays."""
    out, scrubbed = [], 0
    for line in text.split("\n"):
        body, cr = (line[:-1], "\r") if line.endswith("\r") else (line, "")
        m = _ENV_INFO_ARGS_RE.match(body)
        if m and m.group(2) != ENV_INFO_BARE_ARGS:
            out.append(m.group(1) + ENV_INFO_BARE_ARGS + cr)
            scrubbed += 1
            continue
        out.append(line)
    return "\n".join(out), scrubbed


def rewind_point_quicksaves(save_dir: str):
    """Every file directly under `<save_dir>/Parsek/RewindPoints`, sorted; []
    when absent. Every file, not just `*.sfs`: the corpus cell scans the whole
    directory, so a stray file the harvest skipped would red it after commit."""
    rp_dir = os.path.join(save_dir, "Parsek", "RewindPoints")
    if not os.path.isdir(rp_dir):
        return []
    return sorted(os.path.join(rp_dir, n) for n in os.listdir(rp_dir)
                  if os.path.isfile(os.path.join(rp_dir, n)))


def strip_rewind_point_hints(save_dir: str, write: bool):
    """Apply `strip_rewind_save_hints` and `scrub_env_launch_args` to every RewindPoint quicksave under
    `save_dir`. Returns (cleared, leftovers) summed over the files, each leftover
    prefixed with its file name. `write=False` is the dry run the pre-write
    refusal gate uses; `write=True` rewrites only the files that changed.

    The bytes go through latin-1, which round-trips every byte, so a rewritten
    quicksave is byte-identical to its source everywhere but the cleared values."""
    total, leftovers = 0, []
    for path in rewind_point_quicksaves(save_dir):
        with open(path, "rb") as fh:
            text = fh.read().decode("latin-1")
        new_text, cleared, left = strip_rewind_save_hints(text)
        new_text, scrubbed = scrub_env_launch_args(new_text)
        total += cleared
        leftovers.extend("%s:%s" % (os.path.basename(path), l) for l in left)
        if write and (cleared or scrubbed):
            with open(path, "wb") as fh:
                fh.write(new_text.encode("latin-1"))
    return total, leftovers


def read_game_mode(sfs_text: str) -> str:
    """The GAME node's `Mode = ...` value, upper-cased, or "" when unreadable.

    Pure text read of the FIRST TOP-LEVEL `Mode` line, which is where KSP writes
    `SANDBOX` / `SCIENCE_SANDBOX` / `CAREER`. Returns "" rather than guessing on a
    save with no Mode line, so the caller can fall back loudly.

    The indent is bounded to ONE tab/space on purpose rather than matched with
    `\\s*`: `Mode` is a common value name inside nested nodes (part modules and
    several mods write one), and a greedy-indent match would read whichever came
    first in the file and could silently stamp a fixture from a part's setting.
    KSP writes the GAME node's own values at exactly one tab."""
    m = re.search(r"^[ \t]?Mode\s*=\s*(\S+)\s*$", sfs_text, re.MULTILINE)
    return m.group(1).strip().upper() if m else ""


def title_suffix_for_mode(mode: str) -> str:
    """The committed-fixture title suffix for a GAME `Mode` value.

    The convention is the mode's own name in parentheses - `(SANDBOX)`,
    `(CAREER)`, `(SCIENCE_SANDBOX)` - which is what `build_career_pad_craft.py`
    already writes by hand for the fixture it builds by construction. An
    unreadable mode falls back to `(SANDBOX)`, the pre-2026-08-19 hardcoded value,
    so a save this cannot read behaves exactly as it did before."""
    return "(%s)" % (mode if mode else "SANDBOX")


def normalize_title(sfs_text: str, title: str) -> str:
    """Rewrite the GAME node's `Title = ...` line to `<title> (<MODE>)` (the
    committed-fixture title convention, mirroring b2-lko-craft / bdock-station-craft
    for SANDBOX and career-pad-craft for CAREER). Pure text substitution on the
    first Title line; leaves the file otherwise byte-identical. Returns the new text.

    THE SUFFIX IS DERIVED FROM THE SAVE, NOT HARDCODED (career-ledger task C.3).
    It read `(SANDBOX)` unconditionally until 2026-08-19, which was correct only
    because every forge until then produced a sandbox save; the first CAREER
    harvest would have stamped a fixture whose title contradicted its own
    `Mode = CAREER` line, and a fixture that lies about its own mode is the kind
    of thing a later reader trusts."""
    want = "%s %s" % (title, title_suffix_for_mode(read_game_mode(sfs_text)))
    # Match a leading-whitespace `Title = <anything>` line (the GAME node's title).
    pattern = re.compile(r"^(\s*Title\s*=\s*).*$", re.MULTILINE)
    if pattern.search(sfs_text):
        return pattern.sub(lambda m: m.group(1) + want, sfs_text, count=1)
    log("warning: no Title line found in persistent.sfs; leaving title unchanged")
    return sfs_text


def flightstate_span(sfs_text: str) -> str:
    """The FLIGHTSTATE node's text, or the whole input when there is no FLIGHTSTATE.

    `activeVessel` is an INDEX into the VESSEL nodes of FLIGHTSTATE, so every scan
    that index resolves against must see exactly those nodes and in that order. A
    VESSEL node living anywhere else in the save (a SCENARIO node that embeds one -
    stock DiscoverableObjects and several mods do this, and Parsek's own scenario
    node sits immediately BEFORE FLIGHTSTATE) would otherwise be counted and would
    SHIFT every index by one, so `records[active]` would name the wrong vessel and
    the situation gate would pass or fail on the wrong craft.

    Anchoring is start + brace-walk: from the FLIGHTSTATE header to its matching
    close brace, falling back to end-of-text if the braces do not balance (a
    truncated save) and to the whole input if there is no FLIGHTSTATE header at all
    (so a bare VESSEL fragment still parses, which the unit tests rely on). Pure
    text: no ConfigNode parser, no KSP."""
    m = re.search(r"(?m)^\s*FLIGHTSTATE\s*$", sfs_text)
    if m is None:
        return sfs_text
    open_idx = sfs_text.find("{", m.end())
    if open_idx < 0:
        return sfs_text[m.end():]
    depth = 0
    for i in range(open_idx, len(sfs_text)):
        ch = sfs_text[i]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return sfs_text[open_idx:i + 1]
    return sfs_text[open_idx:]


def count_vessels(sfs_text: str) -> int:
    # VESSEL nodes are tab/space-indented inside FLIGHTSTATE -- and ONLY those count
    # (see flightstate_span: a VESSEL node elsewhere would shift the activeVessel index).
    return len(re.findall(r"(?m)^\s*VESSEL\s*$", flightstate_span(sfs_text)))


def read_active_vessel(sfs_text: str):
    m = re.search(r"(?m)^\s*activeVessel\s*=\s*(-?\d+)\s*$", sfs_text)
    return int(m.group(1)) if m else None


def read_vessel_records(sfs_text: str):
    """(name, situation) for every VESSEL node, in FLIGHTSTATE order (the order
    `activeVessel` indexes into).

    Scans the FLIGHTSTATE span only (`flightstate_span`), so a VESSEL node embedded
    in some other node cannot shift the index. Each VESSEL node opens with its own
    `name = ` / `sit = ` lines BEFORE any child PART nodes, so the FIRST match of
    each inside the node's span is the vessel's own. Missing keys read "" (never
    guessed). Pure text parsing: no ConfigNode parser, no KSP."""
    sfs_text = flightstate_span(sfs_text)
    starts = [m.start() for m in re.finditer(r"(?m)^\s*VESSEL\s*$", sfs_text)]
    records = []
    for i, start in enumerate(starts):
        end = starts[i + 1] if i + 1 < len(starts) else len(sfs_text)
        span = sfs_text[start:end]
        name = re.search(r"(?m)^\s*name\s*=\s*(.*)$", span)
        sit = re.search(r"(?m)^\s*sit\s*=\s*(.*)$", span)
        records.append((name.group(1).strip() if name else "",
                        sit.group(1).strip() if sit else ""))
    return records


def parse_expected_situations(value):
    """Normalize a `--expect-situation` argument (comma-separated, any case) to a
    tuple of UPPER tokens. None / "" -> () = the gate is OFF."""
    if not value:
        return ()
    return tuple(tok.strip().upper() for tok in str(value).split(",") if tok.strip())


def check_active_situation(records, active_index, expected):
    """The OPTIONAL situation gate. Returns (ok, message).

    ok is True when the gate is off (`expected` empty). Otherwise the active
    index must resolve to a VESSEL record whose situation is in `expected`. Fails
    CLOSED: an out-of-range index or an unreadable situation is NOT a pass, so an
    orbital forge that lost its stage can never stamp a fixture silently."""
    if not expected:
        return True, "situation gate off"
    if active_index is None or active_index < 0 or active_index >= len(records):
        return False, ("activeVessel index %s does not resolve to one of the %d "
                       "VESSEL nodes" % (active_index, len(records)))
    name, sit = records[active_index]
    if sit.upper() not in expected:
        return False, ("active vessel %r is %s, expected one of %s"
                       % (name, sit or "<unreadable>", ",".join(expected)))
    return True, "active vessel %r is %s" % (name, sit)


def orphan_sidecars(sidecar_names, sfs_text):
    """Sidecar files whose recording id the produced persistent.sfs never
    mentions, anywhere. The 2026-08-24 G3a harvest carried two uncommitted
    single-POINT .prec stubs named nowhere in the save; a committed fixture
    cannot keep them (CommittedFixtureMirrorTests requires every committed
    .prec to keep its _vessel.craft, which an orphan stub has none of). The
    membership test is deliberately the WIDEST one - the id occurring
    ANYWHERE in the sfs text keeps the file - so this can only ever prune
    sidecars that nothing references, never one a novel node type points at.
    Pure: takes names + text, returns the orphans, touches no filesystem."""
    orphans = []
    for name in sidecar_names:
        rec_id = re.split(r"[._]", name, maxsplit=1)[0]
        if rec_id and rec_id not in sfs_text:
            orphans.append(name)
    return orphans


def check_game_mode(sfs_text: str, expected_mode: str):
    """(ok, detail) for the OPTIONAL `--expect-mode` gate. Off (ok) when no mode is
    expected; otherwise the GAME node's Mode must equal it exactly, so a campaign
    stage promoted from the wrong lane's save cannot stamp a Science ladder with a
    Career save."""
    if not expected_mode:
        return True, ""
    actual = read_game_mode(sfs_text)
    want = expected_mode.strip().upper()
    if actual != want:
        return False, "GAME Mode is %r, expected %r" % (actual or "<unreadable>", want)
    return True, "GAME Mode %s" % actual


def harvest(save_dir: str, target_name: str, title: str, force: bool,
            expected_situations=(), keep_parsek: bool = False,
            allow_no_vessel: bool = False, expected_mode: str = "",
            target_root: str = "", race_folder: str = "", race_name: str = "") -> int:
    """``target_root`` (default ``harness/fixtures/saves``) is where the
    ``<target_name>`` directory is written; ``tools/campaign_runner.py`` passes
    a stage folder under ``results/campaign/`` so a chained stage stages exactly
    what a promoted fixture would hold. ``race_folder`` + ``race_name`` (both or
    neither) also harvest a run's race shared-folder snapshot into
    ``harness/fixtures/race/<race_name>/`` (see the module docstring)."""
    if bool(race_folder) != bool(race_name):
        raise SystemExit("harvest: --race-folder and --race-name go together")
    if not os.path.isdir(save_dir):
        raise SystemExit("harvest: produced save dir not found: %s" % save_dir)
    src_sfs = os.path.join(save_dir, "persistent.sfs")
    if not os.path.isfile(src_sfs):
        raise SystemExit("harvest: %s has no persistent.sfs (did the forge run + SaveGame?)"
                         % save_dir)

    with open(src_sfs, "r", encoding="utf-8", errors="replace") as fh:
        sfs_text = fh.read()

    # Sanity 1 (all forges): the produced save must boot into flight on SOME
    # vessel -- LoadGame's IsLoadedGameFocusable gate rejects a save with no
    # active vessel. No situation is assumed here (pad AND orbital forges pass).
    active = read_active_vessel(sfs_text)
    vessels = count_vessels(sfs_text)
    records = read_vessel_records(sfs_text)
    active_name, active_sit = ("", "")
    if active is not None and 0 <= active < len(records):
        active_name, active_sit = records[active]
    log("produced save: activeVessel=%s vessels=%d active=%r situation=%s"
        % (active, vessels, active_name, active_sit or "<unreadable>"))
    if allow_no_vessel:
        # A Space Center start (the scripted campaign's new game, or a stage that
        # recovered everything it flew): LoadGame routes a save with no VESSEL
        # node to SPACECENTER, so no focusable vessel is required. A vessel that
        # IS present is still fine and still logged above.
        log("--allow-no-vessel: a Space Center start fixture (vessels=%d)" % vessels)
    elif active is None or active < 0 or vessels < 1:
        msg = ("produced save is not focusable (activeVessel=%s vessels=%d): the "
               "forge run did not leave a usable vessel" % (active, vessels))
        if not force:
            raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
        log("warning: " + msg + " (writing anyway, --force)")

    mode_ok, mode_detail = check_game_mode(sfs_text, expected_mode)
    if not mode_ok:
        msg = "mode gate failed: %s" % mode_detail
        if not force:
            raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
        log("warning: " + msg + " (writing anyway, --force)")
    elif expected_mode:
        log("mode gate passed: %s" % mode_detail)

    # Sanity 2 (OPTIONAL, off unless --expect-situation is passed): the active
    # vessel must be in one of the expected situations. This is what makes an
    # ORBITAL harvest honest -- a forge that flaked mid-ascent, or a save whose
    # focus landed on the spent core, would otherwise stamp a broken fixture.
    ok, detail = check_active_situation(records, active, expected_situations)
    if not ok:
        msg = "situation gate failed: %s" % detail
        if not force:
            raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
        log("warning: " + msg + " (writing anyway, --force)")
    elif expected_situations:
        log("situation gate passed: %s" % detail)

    # Sanity 3 (--keep-parsek only), BEFORE the destructive write like every
    # other gate: a recorded-state harvest of a save with no recording
    # sidecars must refuse while the existing committed fixture is still
    # intact, not after deleting it.
    if keep_parsek:
        src_recordings = os.path.join(save_dir, "Parsek", "Recordings")
        n_sidecars = (len(os.listdir(src_recordings))
                      if os.path.isdir(src_recordings) else 0)
        if n_sidecars == 0:
            msg = ("--keep-parsek was passed but the produced save carries no "
                   "Parsek/Recordings sidecars: a recorded-state fixture whose "
                   "metadata points at nothing")
            if not force:
                raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
            log("warning: " + msg + " (writing anyway, --force)")

    # Sanity 4 (all modes), BEFORE the destructive write: every `parsek_rw_*`
    # reference the fixture would carry must be one `strip_rewind_save_hints`
    # can clear. One in any other shape would commit a dangling pointer that
    # `CommittedFixtureRewindSaveTests` reds on.
    leftovers = ["persistent.sfs:%s" % l
                 for l in strip_rewind_save_hints(sfs_text)[2]]
    if keep_parsek:
        leftovers.extend(strip_rewind_point_hints(save_dir, write=False)[1])
    if leftovers:
        msg = ("a parsek_rw_* reference the harvest cannot clear (not a "
               "`<key> = parsek_rw_<id>` line): %s" % "; ".join(leftovers))
        if not force:
            raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
        log("warning: " + msg + " (writing anyway, --force)")

    # Sanity 5 (--race-folder only), BEFORE the destructive write: the race
    # folder must be a run's snapshot, read whole, and match its listing.
    race_check = None
    if race_folder:
        race_check = check_race_folder(race_folder, race_name, force)

    target = os.path.join(target_root or _FIXTURES_SAVES, target_name)
    if os.path.isdir(target):
        # The refusal belongs AT the destructive site: a default-mode
        # (Parsek-clean) harvest aimed at an existing RECORDED-state fixture
        # would rmtree the recorded payload and rewrite it clean, exiting 0.
        existing_recordings = os.path.join(target, "Parsek", "Recordings")
        if ((not keep_parsek) and os.path.isdir(existing_recordings)
                and os.listdir(existing_recordings)):
            msg = ("target fixture %s is a RECORDED-state fixture "
                   "(Parsek/Recordings is non-empty) but --keep-parsek was "
                   "not passed; a clean harvest would silently destroy the "
                   "recorded payload" % target_name)
            if not force:
                raise SystemExit("harvest: " + msg
                                 + " (pass --force to overwrite anyway)")
            log("warning: " + msg + " (overwriting anyway, --force)")
        log("removing existing fixture %s" % target)
        shutil.rmtree(target)
    os.makedirs(target)

    # 1) persistent.sfs with the normalized title, and with any rewind-save
    #    pointer cleared (its payload is pruned with Parsek/Saves, so leaving
    #    the pointer would commit a dangling reference).
    normalized = normalize_title(sfs_text, title)
    normalized, cleared_hints, _ = strip_rewind_save_hints(normalized)
    normalized, scrubbed_env = scrub_env_launch_args(normalized)
    if scrubbed_env:
        log("reset the launch arguments in %d envInfo line(s) in persistent.sfs "
            "(they carry the operator's install path)" % scrubbed_env)
    if cleared_hints:
        log("cleared %d dangling rewind-save reference(s) in persistent.sfs "
            "(payload lives in the pruned Parsek/Saves)" % cleared_hints)
    with open(os.path.join(target, "persistent.sfs"), "w", encoding="utf-8",
              newline="\n") as fh:
        fh.write(normalized)
    log("wrote persistent.sfs (title -> %s %s)"
        % (title, title_suffix_for_mode(read_game_mode(sfs_text))))

    # 2) other kept files (loadmeta) verbatim.
    for name in _KEEP_FILES:
        if name == "persistent.sfs":
            continue
        src = os.path.join(save_dir, name)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(target, name))
            log("copied %s" % name)

    # 3) kept directories (Ships/VAB craft + AddOns; plus the save's Parsek/
    # recording sidecars in --keep-parsek mode), pruning per the mode.
    keep_dirs = _KEEP_DIRS + (("Parsek",) if keep_parsek else ())
    ignore = _keep_parsek_ignore(save_dir) if keep_parsek else _ignore_pruned
    if keep_parsek:
        rival_files = count_rival_cache_files(save_dir)
        if rival_files is not None:
            log("pruned the rebuildable rival cache %s (%d file(s); design 4.4)"
                % ("/".join(_RIVAL_CACHE_PARTS), rival_files))
    for dname in keep_dirs:
        src = os.path.join(save_dir, dname)
        if os.path.isdir(src):
            shutil.copytree(src, os.path.join(target, dname), ignore=ignore)
            log("copied %s/" % dname)

    # 3b) --keep-parsek: bring back the retained sidecar versions the harvested
    # save still names (see _PRUNE_RECORDINGS_SUBDIRS).
    if keep_parsek:
        _restore_named_versions(save_dir, target, normalized)

    # 4) belt-and-braces prune of any Parsek/backup dir that slipped in (e.g. at
    # the save root, not under a kept dir). In --keep-parsek mode only the
    # backup-staging dirs are swept; the Parsek dir IS the payload.
    pruned = _prune_state(target, keep_parsek=keep_parsek)
    if pruned:
        log("pruned %s dirs: %s"
            % ("backup" if keep_parsek else "Parsek/backup", ", ".join(pruned)))
    if keep_parsek:
        recordings = os.path.join(target, "Parsek", "Recordings")
        sidecars = sorted(os.listdir(recordings)) if os.path.isdir(recordings) \
            else []
        # Prune sidecars the save never mentions (harvest exhaust: e.g.
        # uncommitted single-POINT stubs) BEFORE the tally, so the fixture
        # the tally describes is the fixture that gets committed. See
        # orphan_sidecars for the widest-membership rationale.
        for name in orphan_sidecars(sidecars, normalized):
            os.remove(os.path.join(recordings, name))
            log("pruned orphan sidecar %s (id named nowhere in persistent.sfs)" % name)
            sidecars.remove(name)
        # The RewindPoint quicksaves embed their own ParsekScenario copy, so
        # they carry the same pointers into the pruned Parsek/Saves.
        rp_cleared, _ = strip_rewind_point_hints(target, write=True)
        if rp_cleared:
            log("cleared %d dangling rewind-save reference(s) inside "
                "Parsek/RewindPoints quicksaves" % rp_cleared)
        # The refusal gate ran BEFORE the write (Sanity 3); this is the tally.
        log("fixture Parsek/Recordings: %d file(s)" % len(sidecars))

    # 3b) drop any harvested craft the shared library already holds. The produced
    # save carries the craft the forge launched, and that is normally a library
    # craft; committing it again is the duplication the library exists to remove,
    # and SharedShipsManifestTests would red on it. Byte-identity is the test, so a
    # genuinely MODIFIED craft of the same name is kept and reported instead.
    shared = _drop_shared_library_craft(target)

    craft = os.path.join(target, "Ships", "VAB")
    craft_files = sorted(os.listdir(craft)) if os.path.isdir(craft) else []
    log("fixture Ships/VAB: %s" % (craft_files or "<none>"))
    if shared:
        log("dropped %d craft already in the shared library: %s"
            % (len(shared), ", ".join(shared)))
        log("ACTION REQUIRED: add this row to harness/fixtures/shared-ships.toml so "
            "the craft is overlaid at stage time --")
        log('  "%s" = [%s]'
            % (os.path.basename(target), ", ".join('"%s"' % s for s in shared)))
    if not craft_files and not shared:
        log("warning: the fixture has no Ships/VAB craft; any consumer that calls "
            "launch_vessel on it will fail to resolve <save>/Ships/VAB/<name>.craft "
            "(harmless for a fixture whose scenario never launches anything)")

    log("harvested -> %s" % target)
    if race_check is not None:
        write_race_folder(race_check)
    return 0


def _digest(path: str) -> str:
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def _drop_shared_library_craft(target: str) -> list:
    """Remove craft under ``<target>/Ships/VAB`` that the shared library already
    holds byte-for-byte; return their ship names (sorted).

    Content-addressed rather than name-matched on purpose: a harvest that renamed
    the ship still produced a duplicate, and a craft that merely SHARES a name with
    a library entry but differs in content is a real divergence the operator needs
    to see, not something to silently delete."""
    vab = os.path.join(target, "Ships", "VAB")
    if not os.path.isdir(vab) or not os.path.isdir(_FIXTURES_SHIPS):
        return []
    library = {}
    for name in os.listdir(_FIXTURES_SHIPS):
        if name.endswith(".craft"):
            library[_digest(os.path.join(_FIXTURES_SHIPS, name))] = name[:-len(".craft")]
    dropped = []
    for name in sorted(os.listdir(vab)):
        path = os.path.join(vab, name)
        if not os.path.isfile(path):
            continue
        ship = library.get(_digest(path))
        if ship is not None:
            os.remove(path)
            dropped.append(ship)
    return sorted(dropped)


def named_version_files(version_names, sfs_text):
    """Pure: the `_versions` file names whose (recording id, hash) the save names.
    A file is kept only when its id is a `recordingId` row AND its hash is one of
    the save's `precHash` / `vesselHash` / `ghostHash` values; everything else is
    collector exhaust and stays pruned."""
    ids = set(_SFS_RECORDING_ID_RE.findall(sfs_text or ""))
    hashes = set(_SFS_SIDECAR_HASH_RE.findall(sfs_text or ""))
    kept = []
    for name in version_names:
        m = _VERSION_FILE_RE.match(name)
        if m and m.group(1) in ids and m.group(2) in hashes:
            kept.append(name)
    return sorted(kept)


def _restore_named_versions(save_dir, target, sfs_text):
    src = os.path.join(save_dir, "Parsek", "Recordings", "_versions")
    if not os.path.isdir(src):
        return
    names = [n for n in os.listdir(src) if os.path.isfile(os.path.join(src, n))]
    kept = named_version_files(names, sfs_text)
    if kept:
        dst = os.path.join(target, "Parsek", "Recordings", "_versions")
        os.makedirs(dst, exist_ok=True)
        for n in kept:
            shutil.copy2(os.path.join(src, n), os.path.join(dst, n))
    log("retained sidecar versions: kept %d named by persistent.sfs, pruned %d unnamed%s"
        % (len(kept), len(names) - len(kept),
           (" (" + ", ".join(kept) + ")") if kept else ""))


def _ignore_pruned(directory, names):
    ignored = []
    for n in names:
        if n in _PRUNE_DIR_NAMES or any(n.startswith(p) for p in _PRUNE_DIR_PREFIXES):
            ignored.append(n)
    return set(ignored)


def _ignore_pruned_keep_parsek(directory, names):
    """--keep-parsek copytree filter: the backup-staging dirs are pruned, and so
    are the readable snapshot mirrors, `Parsek/Saves` and
    `Parsek/Recordings/_quarantine`; the Parsek dir (the recording sidecars) is
    otherwise the fixture's payload.

    WHY THE MIRRORS ARE PRUNED. Parsek writes a readable text mirror beside each
    authoritative sidecar (default-on diagnostics). For the trajectory that mirror
    is worth committing - four scenario headers cite values read straight out of a
    `.prec.txt`. For the two SNAPSHOT mirrors it is not: nothing cites one, and
    they cost 334,023 lines across 99 files, 1.85x the craft duplication the shared
    ship library removed (both mirror kinds together would be 3.04x it). They are also strictly derived from the
    authoritative `_vessel.craft` / `_ghost.craft` binaries, which ARE committed:
    an offline decode reconstructs all 99 byte-for-byte. Load the fixture in KSP
    to get them back (the vessel mirror through
    `ReconcileReadableSidecarMirrors`'s `AuthoritativeSidecar` fallback, the ghost
    mirror through load-time snapshot hydration - see harness/README.md)."""
    pruned = set(n for n in names
                 if any(n.startswith(p) for p in _PRUNE_DIR_PREFIXES)
                 or n.endswith(_PRUNE_MIRROR_SUFFIXES))
    # Drop Parsek/Saves wholesale (see _PRUNE_PARSEK_SUBDIRS). Matched on the
    # PARENT being the Parsek dir so a same-named directory elsewhere in the tree
    # is untouched.
    if os.path.basename(directory) == "Parsek":
        pruned |= set(n for n in names if n in _PRUNE_PARSEK_SUBDIRS)
    # Drop Parsek/Recordings/_quarantine and _versions wholesale (see
    # _PRUNE_RECORDINGS_SUBDIRS). Matched on BOTH path components rather than on
    # the leaf name alone, exactly as the Parsek/Saves clause above is: a
    # same-named directory elsewhere in the tree is untouched.
    parent = os.path.dirname(directory)
    if (os.path.basename(directory) == "Recordings"
            and os.path.basename(parent) == "Parsek"):
        pruned |= set(n for n in names if n in _PRUNE_RECORDINGS_SUBDIRS)
    return pruned


def _keep_parsek_ignore(save_dir: str):
    """The --keep-parsek copytree filter for one save: `_ignore_pruned_keep_parsek`
    plus the rival cache `<save_dir>/Parsek/Race/rivals` (design 4.4: rebuilt at the
    next sync, never part of the save). Matched on the full path under the save
    root, so only that one directory goes: `Parsek/Race/local.cfg`, `race.cfg`,
    `own/` and a `rivals` directory anywhere else are copied."""
    race_dir = os.path.normcase(os.path.abspath(
        os.path.join(save_dir, *_RIVAL_CACHE_PARTS[:-1])))
    cache = _RIVAL_CACHE_PARTS[-1]

    def ignore(directory, names):
        pruned = set(_ignore_pruned_keep_parsek(directory, names))
        if cache in names and os.path.normcase(os.path.abspath(directory)) == race_dir:
            pruned.add(cache)
        return pruned
    return ignore


def count_rival_cache_files(save_dir: str) -> Optional[int]:
    """Files under ``<save_dir>/Parsek/Race/rivals``, or None when there is no
    rival cache (every single-player save)."""
    root = os.path.join(save_dir, *_RIVAL_CACHE_PARTS)
    if not os.path.isdir(root):
        return None
    return sum(len(files) for _d, _s, files in os.walk(root))


# ---------------------------------------------------------------------------
# The race harvest (--race-folder / --race-name, multiplayer T-59).
# ---------------------------------------------------------------------------


class RaceFolderCheck(NamedTuple):
    """What `check_race_folder` decided: the copy `write_race_folder` makes."""

    source: str                          # real path of the run's race snapshot
    name: str                            # the fixture folder name
    rows: Dict[str, Tuple[int, str]]     # copied files: path -> (bytes, sha256)
    skipped: Tuple[str, ...]             # safe-write leftovers, not copied
    summary: str


def _strictly_inside(path: str, parent: str) -> bool:
    """True when the (already resolved) ``path`` is below ``parent`` and not it."""
    a = os.path.normcase(os.path.abspath(path))
    b = os.path.normcase(os.path.abspath(parent))
    try:
        return a != b and os.path.commonpath([a, b]) == b
    except ValueError:  # different drives on Windows
        return False


def race_name_problem(name: str) -> str:
    """Empty when ``name`` is a folder `[fixture.race] sharedFolder =
    "fixtures/race/<name>"` can stage (`hlib.is_race_fixture_path`), else why not."""
    if hlib.is_race_fixture_path(hlib.RACE_FIXTURE_PREFIX + (name or "")):
        return ""
    return ("--race-name %r is not a plain folder name (letters, digits, '-', '_'; "
            "no dots or separators), so [fixture.race] could not stage it" % name)


def race_folder_path_problem(raw: str, results_dir: str) -> str:
    """Empty when ``raw`` names a run's race snapshot inside ``results_dir``, else why
    not. A ``..`` component is refused however it resolves; the path (absolute,
    or relative to the working directory) is resolved through every symlink and
    must land strictly inside the resolved ``results_dir`` on a ``*_race`` folder."""
    if not raw:
        return "no race folder given"
    if ".." in re.split(r"[\\/]+", raw):
        return "%r contains a '..' component" % raw
    real = os.path.realpath(os.path.abspath(raw))
    root = os.path.realpath(results_dir)
    if not _strictly_inside(real, root):
        return "%r resolves to %s, which is not inside %s" % (raw, real, root)
    leaf = os.path.basename(real)
    suffix = hlib.RACE_SNAPSHOT_DIR_SUFFIX
    if not leaf.endswith(suffix) or leaf == suffix:
        return "%s is not a run's race snapshot (results/<runId>%s)" % (real, suffix)
    if not os.path.isdir(real):
        return "%s is not a directory" % real
    return ""


def race_folder_links(root: str) -> List[str]:
    """Every symlink (file or directory) under ``root``, relative. A run's snapshot
    holds none (run.py copies the files themselves), and a copy through one could
    carry content from outside results/ into the fixture."""
    links = []
    for dirpath, dirnames, filenames in os.walk(root):
        for name in sorted(dirnames + filenames):
            full = os.path.join(dirpath, name)
            if os.path.islink(full):
                links.append(os.path.relpath(full, root).replace(os.sep, "/"))
    return sorted(links)


def parse_race_listing(text: str) -> Tuple[Dict[str, Tuple[int, str]], List[str]]:
    """``(rows, problems)`` of a ``results/<runId>_race.listing.txt``
    (`hlib.format_race_listing`): ``path -> (bytes, sha256)``. The first line must
    be `hlib.RACE_LISTING_HEADER`; other ``#`` lines are comments."""
    rows: Dict[str, Tuple[int, str]] = {}
    problems: List[str] = []
    lines = (text or "").splitlines()
    if not lines or lines[0].strip() != hlib.RACE_LISTING_HEADER:
        problems.append("listing does not start with %r" % hlib.RACE_LISTING_HEADER)
    for n, line in enumerate(lines[1:], 2):
        if not line.strip() or line.startswith("#"):
            continue
        parts = line.split(" ", 2)
        if len(parts) != 3 or not parts[1].isdigit() or not parts[2]:
            problems.append("listing line %d is not '<sha256> <bytes> <path>': %r" % (n, line))
            continue
        rows[parts[2]] = (int(parts[1]), parts[0])
    return rows, problems


def race_listing_problems(listed: Dict[str, Tuple[int, str]],
                          actual: Dict[str, Tuple[int, str]]) -> List[str]:
    """Every difference between the run's listing and the folder on disk: a file
    the listing names that is gone, one it does not name, one whose size or
    sha256 changed since the run copied it."""
    problems = []
    for path in sorted(set(listed) | set(actual)):
        if path not in actual:
            problems.append("%s: in the listing, not in the folder" % path)
        elif path not in listed:
            problems.append("%s: in the folder, not in the listing" % path)
        elif listed[path] != actual[path]:
            problems.append("%s: changed since the run (listing %d bytes %s, folder %d "
                            "bytes %s)" % (path, listed[path][0], listed[path][1][:12],
                                           actual[path][0], actual[path][1][:12]))
    return problems


def race_chain_facets(listing: Sequence[str]) -> Tuple[str, str]:
    """``(status, detail)`` of the per-player standing chain of a race folder.

    The plug point for T-21's structure facets (chain intact, latest complete seq,
    seq gaps, payload completeness): `raceparse` reads the folder grammar today but
    no standing, so this answers "unchecked" and the harvest logs it. When T-21
    lands, this calls its facets over the folder and a broken chain becomes a
    problem `check_race_folder` refuses."""
    return ("unchecked", "raceparse has no chain facets yet (multiplayer T-21); "
                         "%d file(s) not chain-checked" % len(listing))


def race_folder_problems(listing: Sequence[str]) -> Tuple[List[str], Tuple[str, ...], str]:
    """``(problems, skipped, summary)`` of a race folder's file listing against the
    shared-folder grammar of design 4.1, as `raceparse.collect_rival_ids` reads it
    (every player counted, none is "own" in a harvest). A problem: a file that is
    neither ``race.cfg`` nor under ``players/<id>/``, a path under a player that
    fits no part of the grammar, a player with no ``player.cfg``, no player at
    all. ``skipped`` are the safe-write leftovers the grammar ignores (not copied).
    Standing counts and ``race.cfg`` presence are reported, not required: a lane
    may harvest a folder before any export."""
    ids = raceparse.collect_rival_ids(listing, None)
    problems: List[str] = []
    has_race_file = False
    standings: Dict[str, int] = {}
    players_dir = raceparse.PLAYERS_DIR
    for path in listing:
        parts = path.split("/")
        if parts == [_RACE_FOLDER_RACE_FILE]:
            has_race_file = True
        elif len(parts) < 3 or parts[0] != players_dir:
            problems.append("%s: not part of the shared-folder layout (%s, %s/<id>/...)"
                            % (path, _RACE_FOLDER_RACE_FILE, players_dir))
        elif len(parts) == 4 and parts[2] == _RACE_FOLDER_STANDINGS_DIR \
                and path not in ids.ignored:
            standings[parts[1]] = standings.get(parts[1], 0) + 1
    problems.extend("%s: fits no part of the player folder grammar" % p
                    for p in ids.unrecognised)
    if not ids.player_ids:
        problems.append("no player folder (%s/<id>/) in the race folder" % players_dir)
    listed = set(listing)
    for player in ids.player_ids:
        cfg = "%s/%s/%s" % (players_dir, player, _RACE_FOLDER_PLAYER_FILE)
        if cfg not in listed:
            problems.append("%s: missing (a player folder without its identity file)" % cfg)
    summary = ("players=%d recordings=%d trees=%d standings=%s %s=%s skipped=%d"
               % (len(ids.player_ids), len(ids.recording_ids), len(ids.tree_ids),
                  ",".join("%s:%d" % (p, standings.get(p, 0)) for p in ids.player_ids)
                  or "-", _RACE_FOLDER_RACE_FILE, "yes" if has_race_file else "no",
                  len(ids.ignored)))
    return problems, tuple(ids.ignored), summary


def _race_rows(root: str, rels: Sequence[str]) -> Dict[str, Tuple[int, str]]:
    rows = {}
    for rel in rels:
        full = os.path.join(root, *rel.split("/"))
        rows[rel] = (os.path.getsize(full), _digest(full))
    return rows


def check_race_folder(folder: str, name: str, force: bool) -> RaceFolderCheck:
    """The --race-folder gate, run BEFORE anything is written. The name, the
    path containment, a link inside the folder and a folder that cannot be listed
    whole are refused even with --force; a listing mismatch or a grammar problem
    is refused unless --force (diagnostics only, like every gate of this tool)."""
    problem = race_name_problem(name) or race_folder_path_problem(folder, _RESULTS_DIR)
    if problem:
        raise SystemExit("harvest: race folder refused: " + problem)
    real = os.path.realpath(os.path.abspath(folder))
    links = race_folder_links(real)
    if links:
        raise SystemExit("harvest: race folder refused: %s holds link(s): %s"
                         % (real, ", ".join(links)))
    listing, walk_problems = raceparse.walk_folder(real)
    if walk_problems:
        raise SystemExit("harvest: race folder refused: cannot be read whole: %s"
                         % "; ".join(walk_problems))
    actual = _race_rows(real, listing)
    problems: List[str] = []
    suffix = hlib.RACE_SNAPSHOT_DIR_SUFFIX
    listing_path = real[:-len(suffix)] + hlib.RACE_LISTING_SUFFIX
    if os.path.isfile(listing_path):
        with open(listing_path, "r", encoding="utf-8", errors="replace") as fh:
            listed, parse_problems = parse_race_listing(fh.read())
        problems.extend(parse_problems)
        problems.extend(race_listing_problems(listed, actual))
    else:
        problems.append("no listing %s beside the folder (run.py writes one with every "
                        "race snapshot)" % listing_path)
    grammar, skipped, summary = race_folder_problems(listing)
    problems.extend(grammar)
    chain_status, chain_detail = race_chain_facets(listing)
    log("race folder %s: files=%d %s" % (real, len(actual), summary))
    log("race folder chain: %s (%s)" % (chain_status, chain_detail))
    for rel in skipped:
        log("race folder: skipping safe-write leftover %s" % rel)
    if problems:
        msg = "race folder check failed: %s" % "; ".join(problems)
        if not force:
            raise SystemExit("harvest: " + msg + " (pass --force to write anyway)")
        log("warning: " + msg + " (writing anyway, --force)")
    skip = set(skipped)
    rows = {rel: actual[rel] for rel in actual if rel not in skip}
    return RaceFolderCheck(source=real, name=name, rows=rows, skipped=skipped,
                           summary=summary)


def write_race_folder(check: RaceFolderCheck) -> str:
    """Write ``harness/fixtures/race/<name>/``: exactly the checked files, byte for
    byte, replacing an earlier harvest of the same name. Returns the target."""
    root = _FIXTURES_RACE
    target = os.path.join(root, check.name)
    if os.path.lexists(target):
        if os.path.islink(target) or not _strictly_inside(os.path.realpath(target),
                                                          os.path.realpath(root)):
            raise SystemExit("harvest: race fixture %s is a link or resolves outside %s; "
                             "not replacing it" % (target, root))
        log("removing existing race fixture %s" % target)
        shutil.rmtree(target)
    os.makedirs(target)
    for rel in sorted(check.rows):
        dst = os.path.join(target, *rel.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(os.path.join(check.source, *rel.split("/")), dst)
    written, _problems = raceparse.walk_folder(target)
    if _race_rows(target, written) != check.rows:
        raise SystemExit("harvest: the race fixture %s differs from the checked folder "
                         "after the copy" % target)
    log("race folder harvested -> %s (%d file(s), %s)"
        % (target, len(check.rows), check.summary))
    return target


def _prune_state(root: str, keep_parsek: bool = False):
    pruned = []
    for dirpath, dirnames, _files in os.walk(root):
        for d in list(dirnames):
            is_backup = any(d.startswith(p) for p in _PRUNE_DIR_PREFIXES)
            is_parsek = d in _PRUNE_DIR_NAMES
            if is_backup or (is_parsek and not keep_parsek):
                full = os.path.join(dirpath, d)
                shutil.rmtree(full, ignore_errors=True)
                dirnames.remove(d)
                pruned.append(os.path.relpath(full, root))
    return pruned


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="harvest_bdock_station",
        description="Harvest a FORGE-produced save (pad OR orbital) into a "
                    "committed fixture: prune Parsek state, normalize the title, "
                    "write to harness/fixtures/saves/<target-name>.")
    p.add_argument("--save-dir", default=None,
                   help="path to the produced save directory. Prefer the run's "
                        "own snapshot, harness/results/<runId>_save (clobber-proof); "
                        "the live <ksp-instance>/saves/bdock-forge-base also works "
                        "but a sibling run can delete it")
    p.add_argument("--instance", default=None,
                   help="KSP instance root (alternative to --save-dir; joined with "
                        "saves/<run-save>)")
    p.add_argument("--run-save", default="bdock-forge-base",
                   help="the forge run-save name under <instance>/saves (default: "
                        "bdock-forge-base)")
    p.add_argument("--target-name", default="bdock-station-pad",
                   help="the committed fixture directory name under "
                        "harness/fixtures/saves (default: bdock-station-pad; use "
                        "eva3-pad-3crew for the EVA-3 3-crew pad fixture, "
                        "eva2-lko-crewed for the EVA-2 crewed-LKO fixture)")
    p.add_argument("--expect-situation", default=None,
                   help="comma-separated situations the produced save's ACTIVE "
                        "vessel must be in (e.g. ORBITING for an orbital forge, "
                        "PRELAUNCH for a pad forge). Omitted = gate off")
    p.add_argument("--title", default=None,
                   help="the fixture title (default: the target-name)")
    p.add_argument("--keep-parsek", action="store_true",
                   help="RECORDED-STATE fixture mode: keep the save's Parsek/ "
                        "directory (recording sidecars) instead of pruning it; "
                        ".parsek-backup* staging dirs are still pruned. Fails "
                        "unless Parsek/Recordings carries at least one sidecar "
                        "(--force overrides)")
    p.add_argument("--allow-no-vessel", action="store_true",
                   help="a Space Center start fixture (a new game, or a campaign "
                        "stage that recovered everything): no focusable vessel is "
                        "required. Composes with --keep-parsek")
    p.add_argument("--expect-mode", default=None,
                   help="the GAME Mode the produced save must carry "
                        "(SANDBOX / SCIENCE_SANDBOX / CAREER). Omitted = gate off")
    p.add_argument("--race-folder", default=None,
                   help="a race lane's shared-folder snapshot, "
                        "harness/results/<runId>_race (refused anywhere else); "
                        "copied to harness/fixtures/race/<race-name>. Needs "
                        "--race-name")
    p.add_argument("--race-name", default=None,
                   help="the race fixture folder name under harness/fixtures/race "
                        "(letters, digits, '-', '_'), the name a spec stages with "
                        "[fixture.race] sharedFolder = \"fixtures/race/<name>\"")
    p.add_argument("--force", action="store_true",
                   help="write the fixture even if the produced save is not "
                        "focusable or fails the situation gate (diagnostics only)")
    return p


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    save_dir = resolve_save_dir(args)
    title = args.title or args.target_name
    return harvest(save_dir, args.target_name, title, args.force,
                   parse_expected_situations(args.expect_situation),
                   keep_parsek=args.keep_parsek,
                   allow_no_vessel=args.allow_no_vessel,
                   expected_mode=args.expect_mode or "",
                   race_folder=args.race_folder or "",
                   race_name=args.race_name or "")


if __name__ == "__main__":
    sys.exit(main())
