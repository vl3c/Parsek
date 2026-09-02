#!/usr/bin/env python3
"""Finish the harvested `interbody-route-recorded` fixture: the INTER-BODY route host.

WHY THIS FIXTURE EXISTS, AND WHY IT IS A HARVEST RATHER THAN A FLIGHT. Roadmap
gap G10 (`autotest-roadmap.md`) needs a committed save carrying an Active route
that runs BETWEEN TWO BODIES, because every route mechanism that is
route-SPECIFIC in the render engages only on that shape:
`RouteTrajectoryLineRenderer.ClassifyRouteScope = InterBody` has never been read
live, and `FilterLegsToEndpointBodies` - the ratified transfer-leg DROP, the
deliberate gap in the overview line - has never dropped a leg on a driven run.
On a SameBody subject (V18T's `depot-route-recorded`) both clauses are satisfied
BY SCOPE and confirm nothing.

No forge and no seam can produce this subject. Route candidacy is seal-gated with
no seam path: `RouteCommand action=create` walks
`TestCommandRouteCommand.ClassifyCreateRefusal` and `RouteAnalysisEngine` answers
`MissingRouteProof` unless the source recording carries a
`ROUTE_CONNECTION_WINDOWS` node. Only four committed fixtures carry one at all
(`bdock-recorded`, `depot-route-recorded`, `rover-route-recorded`,
`rover-route-career`) and every member of all four starts on Kerbin; the Duna
fixtures (`duna-park-recorded`, `duna-one-recorded`) carry none. So the subject
had to come from an operator campaign, which is the `duna-one-recorded` /
`depot-route-recorded` / `rover-route-recorded` provenance class.

THE SOURCE. The operator's own hand-played SANDBOX campaign
`Kerbal Space Program/saves/orbital supply route` (persistent.sfs 1.68 MB, 306
sidecar files before the orphan prune), harvested READ-ONLY from a scratch COPY
on 2026-09-02 with

    python harness/tools/harvest_bdock_station.py \\
        --save-dir <scratch copy> --target-name interbody-route-recorded \\
        --expect-situation ORBITING --keep-parsek

The operator's save was never written to. Two sibling saves were inspected and
rejected as subjects in the same pass: `orbital supply route CLEAN` carries ZERO
`ROUTE` nodes, and `orbital supply route DELIVERY test` carries one
`Route: Kerbin -> Kerbin` at `completedCycles = 1` (a SameBody duplicate of
V18T's subject). A fourth,
`orbital supply route.backup-2026-06-19_pre-recovery`, carries no ParsekScenario
recordings at all.

NEVER NAME THE FIXTURE AFTER THE SOURCE SAVE. `run.py::stage_fixture` rmtree's
the same-named save inside the automation instance, so a fixture called
`orbital supply route` would delete the operator's campaign the first time a lane
staged it.

WHAT THE HARVEST PRODUCED, AND THE TWO HAND STEPS AFTER IT.
The harvester pruned `Parsek/Saves` (rewind-point payload), cleared four dangling
`rewindSave` hints, dropped 86 ORPHAN sidecars (recording ids named nowhere in
persistent.sfs, 306 -> 220 files), and normalized the title. Two things were then
done by hand and are re-asserted by `verify_tree` below rather than re-run:

  1. `Ships/` was DELETED. The harvester keeps it, but no recorded free-play
     fixture in the corpus commits one and no lane over this subject enters the
     editor - the recordings carry their own `_vessel.craft` / `_ghost.craft`
     sidecars. Deleting it also keeps the fixture out of
     `harness/fixtures/shared-ships.toml`, which it has no business being in.
  2. `AddOns/DistantObject/Settings.cfg` was replaced with the 618-byte variant
     every other committed fixture carries, copied from `depot-route-recorded`
     (a fixture of the same class and the same lane family, the rover builder's
     donor rule). The operator's dev instance ships a 653-byte variant; keeping
     it would make this fixture the only one in 53 that differs, for a file no
     lane reads.

WHAT MAKES IT THE G10 SUBJECT - checked against the roadmap's own 8-step
specification, read out of the harvested bytes:

  1. depot at Duna FIRST      `Depot Station Duna I`, two recordings, startBodyName = Duna
  2. transport from the pad   `Duna Supply 1`, startBodyName = Kerbin, isKscOrigin = True
  3. cargo carried            DELIVERY_MANIFEST LiquidFuel 257.83 / Oxidizer 315.13
  4. dock at the depot        transferKind = DockingPort, dockUT = 72353218.8197432
  5. undock recorded          undockUT = 72353267.2397331 in the SAME window node
  6. tree sealed              ZERO `mergeState` lines in the whole save, and the
                              codec writes that key only when the state is NOT
                              Immutable, so every recording is Immutable
  7. route Active, 0 cycles   status = Active, completedCycles = 0, skippedCycles = 0
  8. nothing deleted          45 recordings across 4 committed trees, 23 Destroyed
                              terminals (the ascent debris) still present

The route's own scope inputs are the point: ORIGIN bodyName = Kerbin, STOP
endpoint bodyName = Duna, `dispatchWindowPeriod = 0`. Before the 2026-09-02 scope
fix (todo ROUTE-INTERBODY-SCOPE-NEVER-REACHABLE) that period made it classify
`MalformedMixedBodies` and draw nothing; scope is now derived from those two body
names, so this save reads `InterBody`.

A SECOND ROUTE RIDES ALONG, DELIBERATELY KEPT. The save also carries
`Route: KSC -> Mun`, `status = Paused`, origin Kerbin / stop Mun,
`dispatchWindowPeriod = 0`. It is a SECOND inter-body route under the new rule
and a Paused one, so the fixture pins the route-count and status census at 2/1/1
rather than 1/1/0, and any lane over it must expect two committed routes. Cutting
it would have meant editing the operator's ParsekScenario by hand, which is a
worse trade than a slightly richer census.

Usage:

    python harness/tools/build_interbody_route_recorded.py            # finish (idempotent)
    python harness/tools/build_interbody_route_recorded.py --check    # verify the committed bytes

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS, "fixtures", "saves")

TARGET_NAME = "interbody-route-recorded"

# --- title / active vessel ------------------------------------------------

EXPECTED_TITLE = "\tTitle = interbody-route-recorded (SANDBOX)"
EXPECTED_MODE = "\tMode = SANDBOX"
# activeVessel indexes FLIGHTSTATE's VESSEL list; index 0 is `Depot`, ORBITING.
# A FLIGHT boot over this fixture therefore lands on a Kerbin-orbit depot, which
# is the route's ORIGIN body - the right host for a map-open route-line reading.
EXPECTED_ACTIVE_VESSEL_INDEX = 0
EXPECTED_ACTIVE_VESSEL_NAME = "Depot"
EXPECTED_ACTIVE_VESSEL_SIT = "ORBITING"
EXPECTED_FLIGHTSTATE_VESSELS = 20

# --- the ParsekScenario shape --------------------------------------------

EXPECTED_TREES = 4
EXPECTED_RECORDINGS = 45
EXPECTED_ROUTES = 2
EXPECTED_CONNECTION_WINDOWS = 2

# The route facet, pinned here AND in `test_saveparse.RECORDED_FIXTURES` so the
# two cannot drift (the `RouteFacetFixtureAgreementTests` pattern that
# `depot-route-recorded` established).
ROUTE_FACET_PINS = {
    "count": 2, "dormant": 0, "stops": 2, "sourceRefs": 8,
    "completedCycles": 0, "skippedCycles": 0,
    "codecRejects": 0, "unparsed": 0, "unknownStatuses": 0,
    "unknownConnectionKinds": 0,
    "statuses": {"Active": 1, "Paused": 1},
    "connectionKinds": {"DockingPort": 2},
    "originBodies": {"Kerbin": 2},
    "destinationBodies": {"Duna": 1, "Mun": 1},
    "holdKinds": {},
    "ids": ["8f644e71b1164df3bb735330127d2ee7",
            "71a983a16dc04d78bc2a2b90f1d184b0"],
    "destinationVesselPids": ["4277041026", "1413036399"],
    "dismissedCandidates": 2, "promptedCandidates": 0,
}

# The INTER-BODY route (the subject) and the Paused Mun route that rides along.
INTERBODY_ROUTE_ID = "71a983a16dc04d78bc2a2b90f1d184b0"
INTERBODY_ROUTE_TREE_ID = "3daf0cff159d413794c626137cd81002"
INTERBODY_ROUTE_DOCK_UT = "72353218.8197432"
INTERBODY_ROUTE_UNDOCK_UT = "72353267.2397331"
INTERBODY_ROUTE_ORIGIN_BODY = "Kerbin"
INTERBODY_ROUTE_DESTINATION_BODY = "Duna"
MUN_ROUTE_ID = "8f644e71b1164df3bb735330127d2ee7"
MUN_ROUTE_TREE_ID = "02382fcdcb1a465388529350c0879cd6"

# The four [root..undock] members of the inter-body route, in tree order. Pinned
# as the fixture's `recordingIds` subset rather than all 45: these are the ones
# the route resolves and the ones a route-line build walks, so a sidecar loss
# HERE is the loss that breaks the lane. Their bodies are the scope inputs:
# Kerbin, <transfer - no startBodyName>, Duna, Duna.
INTERBODY_ROUTE_MEMBER_IDS = (
    "d23e453bc982482b850ce717ba83bffd",   # Duna Supply 1, startBodyName = Kerbin
    "5ca48c99fa55435e8cf8547a6ef27a39",   # Duna Supply 1 Probe, the transfer leg
    "3700f40e66c84ff79ce5197b362cf937",   # Depot Station Duna I, startBodyName = Duna
    "caa6190c37f74e928bfcdc8652ef3910",   # Depot Station Duna I, the dock member
)

# --- the file tree --------------------------------------------------------

ADDONS_DONOR_NAME = "depot-route-recorded"
ADDONS_REL = os.path.join("AddOns", "DistantObject", "Settings.cfg")
ADDONS_EXPECTED_BYTES = 618

GAMESTATE_FILES = (
    "baseline_0.pgsb",
    "baseline_83044949.9671543.pgsb",
    "baseline_87620501.988759115.pgsb",
    "baseline_87622602.648320884.pgsb",
    "baseline_87622904.98825781.pgsb",
    "baseline_87622905.188257769.pgsb",
    "events.pgse",
    "ledger.pgld",
    "milestones.pgsm",
)

# `analysis` is here for the reason the rover builder gives: running
# `analyze-recordings.ps1` against the fixture WRITES an `analysis/` directory
# into it that must not be committed. `Ships` is here because this builder
# DELETES the one the harvester kept (see the module docstring).
FORBIDDEN_DIR_NAMES = ("_quarantine", "Saves", "Backup", "RewindPoints",
                       "Ships", "analysis")
FORBIDDEN_FILE_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")
FORBIDDEN_FILE_PREFIXES = ("quicksave",)

# Non-`.txt` files under Parsek/Recordings. 175 today (45 recordings: 45 `.pann`
# + 45 `.prec` + 43 `_vessel.craft` + 42 `_ghost.craft`). A FLOOR, not a pin, so
# an added sidecar kind does not red the fixture.
EXPECTED_AUTHORITATIVE_SIDECARS = 175


# ---------------------------------------------------------------------------
#  File I/O (LF-only, matching the harvest)
# ---------------------------------------------------------------------------


def read_lines(path: str) -> List[str]:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


def write_lines(path: str, lines: List[str]) -> None:
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write("\n".join(lines))


def _count_lines(lines: List[str], pattern: str) -> int:
    rx = re.compile(pattern)
    return sum(1 for ln in lines if rx.search(ln))


def _node_block(lines: List[str], start: int) -> Tuple[int, int]:
    """[start .. end) of the brace block whose opening `{` follows line `start`."""
    i = start + 1
    while i < len(lines) and lines[i].strip() != "{":
        i += 1
    depth = 0
    j = i
    while j < len(lines):
        s = lines[j].strip()
        if s == "{":
            depth += 1
        elif s == "}":
            depth -= 1
            if depth == 0:
                return start, j + 1
        j += 1
    return start, len(lines)


def _route_blocks(lines: List[str]) -> List[Tuple[int, int]]:
    out = []
    for i, ln in enumerate(lines):
        if ln.strip() == "ROUTE":
            out.append(_node_block(lines, i))
    return out


def _value(lines: List[str], span: Tuple[int, int], key: str) -> Optional[str]:
    rx = re.compile(r"^\s*" + re.escape(key) + r"\s*=\s*(.*)$")
    for ln in lines[span[0]:span[1]]:
        m = rx.match(ln)
        if m:
            return m.group(1).strip()
    return None


def _child_span(lines: List[str], span: Tuple[int, int],
                name: str) -> Optional[Tuple[int, int]]:
    for i in range(span[0], span[1]):
        if lines[i].strip() == name:
            return _node_block(lines, i)
    return None


# ---------------------------------------------------------------------------
#  Verification
# ---------------------------------------------------------------------------


def verify_save(lines: List[str]) -> List[str]:
    """Post-conditions over the fixture's persistent.sfs. Empty list = all hold."""
    problems: List[str] = []

    if EXPECTED_TITLE not in lines:
        problems.append("title line is not %r" % EXPECTED_TITLE)
    if EXPECTED_MODE not in lines:
        problems.append("mode line is not %r" % EXPECTED_MODE)

    problems += _verify_active_vessel(lines)
    problems += verify_seal_state(lines)
    problems += verify_route_windows(lines)
    problems += verify_routes(lines)

    trees = _count_lines(lines, r"^\s*RECORDING_TREE\s*$")
    if trees != EXPECTED_TREES:
        problems.append("expected %d RECORDING_TREE node(s), found %d"
                        % (EXPECTED_TREES, trees))
    recs = _count_lines(lines, r"^\s*RECORDING\s*$")
    if recs != EXPECTED_RECORDINGS:
        problems.append("expected %d RECORDING node(s), found %d"
                        % (EXPECTED_RECORDINGS, recs))

    # The harvest clears rewindSave hints because Parsek/Saves is pruned; a
    # non-empty one would point at a payload the fixture does not carry.
    for i, ln in enumerate(lines):
        m = re.match(r"^\s*rewindSave\s*=\s*(\S.*)$", ln)
        if m:
            problems.append("line %d: dangling rewindSave hint %r "
                            "(Parsek/Saves is pruned)" % (i + 1, m.group(1)))
    return problems


def _verify_active_vessel(lines: List[str]) -> List[str]:
    problems: List[str] = []
    idx = None
    for ln in lines:
        m = re.match(r"^\s*activeVessel\s*=\s*(\d+)\s*$", ln)
        if m:
            idx = int(m.group(1))
            break
    if idx is None:
        return ["FLIGHTSTATE carries no activeVessel line"]
    if idx != EXPECTED_ACTIVE_VESSEL_INDEX:
        problems.append("activeVessel is %d, expected %d"
                        % (idx, EXPECTED_ACTIVE_VESSEL_INDEX))

    vessels = [i for i, ln in enumerate(lines) if ln.strip() == "VESSEL"]
    # FLIGHTSTATE's VESSEL nodes sit at one indent depth; craft-file VESSELs do
    # not appear in persistent.sfs, so the whole list is the flight state's.
    if len(vessels) != EXPECTED_FLIGHTSTATE_VESSELS:
        problems.append("expected %d FLIGHTSTATE VESSEL node(s), found %d"
                        % (EXPECTED_FLIGHTSTATE_VESSELS, len(vessels)))
    if idx is not None and 0 <= idx < len(vessels):
        span = _node_block(lines, vessels[idx])
        name = _value(lines, span, "name")
        sit = _value(lines, span, "sit")
        if name != EXPECTED_ACTIVE_VESSEL_NAME:
            problems.append("active vessel is %r, expected %r"
                            % (name, EXPECTED_ACTIVE_VESSEL_NAME))
        if sit != EXPECTED_ACTIVE_VESSEL_SIT:
            problems.append("active vessel situation is %r, expected %r"
                            % (sit, EXPECTED_ACTIVE_VESSEL_SIT))
    return problems


def verify_seal_state(lines: List[str]) -> List[str]:
    """Step 6 of the roadmap specification: every recording Immutable.

    `RecordingTreeRecordCodec.SaveRewindToStagingMergeState` writes the
    `mergeState` key ONLY when the state is not Immutable, and the read side
    defaults an absent key to Immutable. So ZERO occurrences in the whole save is
    the sealed reading, and it is what the route create gate required.
    """
    hits = [i + 1 for i, ln in enumerate(lines)
            if re.match(r"^\s*mergeState\s*=", ln)]
    if hits:
        return ["expected no mergeState lines (every recording Immutable = the "
                "tree is sealed); found %d at line(s) %s"
                % (len(hits), ", ".join(str(h) for h in hits[:8]))]
    return []


def verify_route_windows(lines: List[str]) -> List[str]:
    """Steps 4 + 5: a dock/undock PAIR with transferKind = DockingPort."""
    problems: List[str] = []
    nodes = [i for i, ln in enumerate(lines)
             if ln.strip() == "ROUTE_CONNECTION_WINDOWS"]
    if len(nodes) != EXPECTED_CONNECTION_WINDOWS:
        problems.append("expected %d ROUTE_CONNECTION_WINDOWS node(s), found %d"
                        % (EXPECTED_CONNECTION_WINDOWS, len(nodes)))
    found = {}
    for start in nodes:
        span = _node_block(lines, start)
        win = _child_span(lines, span, "WINDOW")
        if win is None:
            problems.append("ROUTE_CONNECTION_WINDOWS at line %d has no WINDOW"
                            % (start + 1))
            continue
        dock = _value(lines, win, "dockUT")
        undock = _value(lines, win, "undockUT")
        kind = _value(lines, win, "transferKind")
        if not dock or not undock:
            problems.append("WINDOW at line %d is not a complete dock/undock "
                            "pair (dockUT=%r undockUT=%r)"
                            % (win[0] + 1, dock, undock))
        if kind != "DockingPort":
            problems.append("WINDOW at line %d has transferKind=%r, expected "
                            "DockingPort (a claw would stamp Grapple, a "
                            "different RouteConnectionKind reading)"
                            % (win[0] + 1, kind))
        if dock:
            found[dock] = undock
    if INTERBODY_ROUTE_DOCK_UT not in found:
        problems.append("the inter-body dock window (dockUT=%s) is missing"
                        % INTERBODY_ROUTE_DOCK_UT)
    elif found[INTERBODY_ROUTE_DOCK_UT] != INTERBODY_ROUTE_UNDOCK_UT:
        problems.append("the inter-body window's undockUT is %r, expected %s"
                        % (found[INTERBODY_ROUTE_DOCK_UT],
                           INTERBODY_ROUTE_UNDOCK_UT))
    return problems


def verify_routes(lines: List[str]) -> List[str]:
    """The two committed ROUTE nodes and the inter-body one's scope inputs."""
    problems: List[str] = []
    blocks = _route_blocks(lines)
    if len(blocks) != EXPECTED_ROUTES:
        problems.append("expected %d ROUTE node(s), found %d"
                        % (EXPECTED_ROUTES, len(blocks)))
    by_id = {}
    for span in blocks:
        rid = _value(lines, span, "id")
        if rid:
            by_id[rid] = span

    for rid in (INTERBODY_ROUTE_ID, MUN_ROUTE_ID):
        if rid not in by_id:
            problems.append("ROUTE id=%s is missing" % rid)
    if INTERBODY_ROUTE_ID not in by_id:
        return problems

    span = by_id[INTERBODY_ROUTE_ID]
    checks = [
        ("status", "Active"),
        ("isKscOrigin", "True"),
        ("completedCycles", "0"),
        ("skippedCycles", "0"),
        # KEPT AND DEMOTED: the scope fix left the wire alone, so this line must
        # still read 0. If it ever reads non-zero the fixture was re-harvested
        # from a save written by a build that changed the codec.
        ("dispatchWindowPeriod", "0"),
        ("backingMissionTreeId", INTERBODY_ROUTE_TREE_ID),
        ("recordedDockUT", INTERBODY_ROUTE_DOCK_UT),
        ("reaimWindowBasisEngaged", "True"),
    ]
    for key, want in checks:
        got = _value(lines, span, key)
        if got != want:
            problems.append("inter-body ROUTE %s=%r, expected %r"
                            % (key, got, want))

    origin = _child_span(lines, span, "ORIGIN")
    if origin is None:
        problems.append("inter-body ROUTE has no ORIGIN node")
    else:
        got = _value(lines, origin, "bodyName")
        if got != INTERBODY_ROUTE_ORIGIN_BODY:
            problems.append("inter-body ROUTE ORIGIN bodyName=%r, expected %r "
                            "(this is the scope rule's origin input)"
                            % (got, INTERBODY_ROUTE_ORIGIN_BODY))

    stop = _child_span(lines, span, "STOP")
    if stop is None:
        problems.append("inter-body ROUTE has no STOP node")
    else:
        endpoint = _child_span(lines, stop, "ENDPOINT")
        if endpoint is None:
            problems.append("inter-body ROUTE STOP has no ENDPOINT node")
        else:
            got = _value(lines, endpoint, "bodyName")
            if got != INTERBODY_ROUTE_DESTINATION_BODY:
                problems.append(
                    "inter-body ROUTE STOP ENDPOINT bodyName=%r, expected %r - "
                    "origin != destination is the WHOLE reason this fixture "
                    "exists (ClassifyRouteScope reads exactly this pair)"
                    % (got, INTERBODY_ROUTE_DESTINATION_BODY))
        kind = _value(lines, stop, "connectionKind")
        if kind != "DockingPort":
            problems.append("inter-body ROUTE STOP connectionKind=%r, expected "
                            "DockingPort" % (kind,))
        manifest = _child_span(lines, stop, "DELIVERY_MANIFEST")
        if manifest is None or (manifest[1] - manifest[0]) <= 3:
            problems.append("inter-body ROUTE STOP carries no DELIVERY_MANIFEST "
                            "(step 3 of the specification: cargo must have been "
                            "transferred at the dock)")

    ids = _child_span(lines, span, "RECORDING_IDS")
    if ids is None:
        problems.append("inter-body ROUTE has no RECORDING_IDS node")
    else:
        members = [ln.strip().split("=", 1)[1].strip()
                   for ln in lines[ids[0]:ids[1]] if "=" in ln]
        if tuple(members) != INTERBODY_ROUTE_MEMBER_IDS:
            problems.append("inter-body ROUTE members are %r, expected %r"
                            % (members, list(INTERBODY_ROUTE_MEMBER_IDS)))
    return problems


def verify_tree(fixture_dir: str) -> List[str]:
    """Post-conditions over the committed file tree."""
    problems: List[str] = []

    loadmeta = os.path.join(fixture_dir, "persistent.loadmeta")
    if not os.path.isfile(loadmeta):
        problems.append("fixture carries no persistent.loadmeta")

    addons = os.path.join(fixture_dir, ADDONS_REL)
    if not os.path.isfile(addons):
        problems.append("fixture carries no %s" % ADDONS_REL.replace("\\", "/"))
    else:
        size = os.path.getsize(addons)
        if size != ADDONS_EXPECTED_BYTES:
            problems.append("%s is %d bytes, expected %d (the variant every "
                            "other committed fixture carries)"
                            % (ADDONS_REL.replace("\\", "/"), size,
                               ADDONS_EXPECTED_BYTES))
        donor = os.path.join(_SAVES, ADDONS_DONOR_NAME, ADDONS_REL)
        if os.path.isfile(donor):
            with open(donor, "rb") as fh:
                want = fh.read()
            with open(addons, "rb") as fh:
                got = fh.read()
            if got != want:
                problems.append("%s does not match the %s donor byte for byte"
                                % (ADDONS_REL.replace("\\", "/"),
                                   ADDONS_DONOR_NAME))

    gamestate = os.path.join(fixture_dir, "Parsek", "GameState")
    if not os.path.isdir(gamestate):
        problems.append("fixture carries no Parsek/GameState")
    else:
        got = sorted(os.listdir(gamestate))
        if got != sorted(GAMESTATE_FILES):
            problems.append("Parsek/GameState holds %r, expected %r"
                            % (got, sorted(GAMESTATE_FILES)))

    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")
    if not os.path.isdir(recordings):
        problems.append("fixture carries no Parsek/Recordings")
        return problems

    names = os.listdir(recordings)
    authoritative = [f for f in names if not f.endswith(".txt")]
    if len(authoritative) < EXPECTED_AUTHORITATIVE_SIDECARS:
        problems.append("Parsek/Recordings holds %d authoritative sidecar(s), "
                        "expected at least %d"
                        % (len(authoritative), EXPECTED_AUTHORITATIVE_SIDECARS))

    for rid in INTERBODY_ROUTE_MEMBER_IDS:
        prec = os.path.join(recordings, rid + ".prec")
        if not os.path.isfile(prec):
            problems.append("route member %s.prec is missing" % rid)
        elif os.path.getsize(prec) == 0:
            problems.append("route member %s.prec is empty" % rid)

    # ORPHAN SWEEP, the harvester's own rule re-asserted against the committed
    # bytes: a sidecar whose recording id is named NOWHERE in persistent.sfs is
    # payload nothing can reach. The membership test is the widest one on
    # purpose (substring over the whole save text), so it can only ever name a
    # file that is genuinely unreferenced.
    sfs = os.path.join(fixture_dir, "persistent.sfs")
    if os.path.isfile(sfs):
        with open(sfs, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
        orphans = []
        for name in sorted(names):
            rec_id = re.split(r"[._]", name, maxsplit=1)[0]
            if rec_id and rec_id not in text:
                orphans.append(name)
        if orphans:
            problems.append("orphan sidecar(s) named nowhere in persistent.sfs: "
                            "%s" % ", ".join(orphans[:8]))

    for root, dirs, files in os.walk(fixture_dir):
        for d in list(dirs):
            if d in FORBIDDEN_DIR_NAMES:
                problems.append("forbidden directory committed: %s"
                                % os.path.relpath(os.path.join(root, d),
                                                  fixture_dir).replace("\\", "/"))
        for f in files:
            rel = os.path.relpath(os.path.join(root, f),
                                  fixture_dir).replace("\\", "/")
            if f.endswith(FORBIDDEN_FILE_SUFFIXES):
                problems.append("forbidden mirror committed: %s" % rel)
            if f.lower().startswith(FORBIDDEN_FILE_PREFIXES):
                problems.append("forbidden quicksave committed: %s" % rel)
    return problems


# ---------------------------------------------------------------------------
#  Entry point
# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of finishing it")
    parser.add_argument("--target-name", default=TARGET_NAME)
    args = parser.parse_args(argv)

    fixture_dir = os.path.join(_SAVES, args.target_name)
    sfs = os.path.join(fixture_dir, "persistent.sfs")
    if not os.path.isfile(sfs):
        print("FAIL: %s does not exist (run harvest_bdock_station.py first, "
              "see this module's docstring for the exact invocation)" % sfs)
        return 1

    if args.check:
        problems = verify_save(read_lines(sfs))
        problems += verify_tree(fixture_dir)
        for p in problems:
            print("FAIL: %s" % p)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    # --- 1: drop the Ships/ directory the harvester keeps ----------------
    ships = os.path.join(fixture_dir, "Ships")
    if os.path.isdir(ships):
        shutil.rmtree(ships)
        print("removed Ships/ (no recorded fixture commits one)")
    else:
        print("Ships/ already absent")

    # --- 2: AddOns donor restore -----------------------------------------
    donor = os.path.join(_SAVES, ADDONS_DONOR_NAME, ADDONS_REL)
    if not os.path.isfile(donor):
        print("FAIL: AddOns donor %s is missing" % donor)
        return 1
    addons_dst = os.path.join(fixture_dir, ADDONS_REL)
    os.makedirs(os.path.dirname(addons_dst), exist_ok=True)
    shutil.copy2(donor, addons_dst)
    print("restored %s from %s (%d bytes)"
          % (ADDONS_REL.replace("\\", "/"), ADDONS_DONOR_NAME,
             os.path.getsize(addons_dst)))

    problems = verify_save(read_lines(sfs))
    problems += verify_tree(fixture_dir)
    for p in problems:
        print("FAIL: %s" % p)
    if problems:
        return 1
    print("OK: wrote %s" % fixture_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
