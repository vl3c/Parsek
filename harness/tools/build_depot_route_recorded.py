#!/usr/bin/env python3
"""Finish the harvested `depot-route-recorded` fixture: the suite's FIRST ROUTE.

WHY THIS FIXTURE EXISTS, AND WHY IT IS A HARVEST RATHER THAN A FLIGHT. B27 in
`docs/dev/autotest-roadmap.md` was registered as a route subject forged "over the
BDOCK station fixture", and that route is closed: route candidacy is gated on
`IsTreeFullySealed`, and `SealSlot` / `RouteCommand` are RESERVED command-seam
verbs (H35 ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH), so no driven run can
currently create a ROUTE at all. The only verb-free path to a real one is to
harvest a save an operator already flew - the `duna-one-recorded` provenance
class, ratified for this fixture on 2026-08-26. B27 therefore ships as a
FORGE-CLASS STAMP (this tool plus its drift test); the flight variant stays
deferred behind those two verbs.

THE SOURCE. `Kerbal Space Program/saves/orbital supply route DELIVERY test`, the
operator's own free-play sandbox save, 340,420 B, READ-ONLY (harvest from a copy,
never point a tool at the live save). It carries one ROUTE in `GhostDriving`
status with `completedCycles = 1`, which is the state the M-A7 route-render lanes
need to read: a route that has actually run a delivery cycle and is still active.

NEVER NAME THE FIXTURE AFTER THE SOURCE SAVE. `run.py::stage_fixture` rmtree's
the same-named save inside the automation instance, so a fixture called
`orbital supply route DELIVERY test` would delete the operator's hand-played save
the first time any scenario staged it. Hence `depot-route-recorded`.

INPUT. The output of

    python harness/tools/harvest_bdock_station.py \\
        --save-dir <scratch copy of the source save> \\
        --target-name depot-route-recorded \\
        --expect-situation ORBITING --keep-parsek

i.e. this tool edits `harness/fixtures/saves/depot-route-recorded` IN PLACE. The
harvest did the generic half (title normalisation, the two `rewindSave` hint
clears, `Parsek/Saves`, the `.craft.txt` snapshot mirrors); everything below is
depot-route-specific.

`--expect-situation ORBITING` IS THE RIGHT GATE AND IT IS NOT THE OBVIOUS ONE.
The source save's `activeVessel = 0` is an ASTEROID (`Ast. YRJ-552`), which the
harvest's focusability check happily accepts - so the gate is armed against the
situation the fixture's active vessel has AFTER step 1 below re-points it to
`Depot`, and ORBITING is true of both, which is why the harvest passes and the
verify clause below is what actually proves the re-point happened.

WHAT IT DOES, in order:

  1. THE ACTIVE VESSEL. Re-points `FLIGHTSTATE/activeVessel` from the asteroid at
     index 0 to `Depot` at index 9 - the ROUTE's STOP endpoint vessel
     (`vesselPersistentId = 3620499050`). A fixture that boots focused on a
     random asteroid is a fixture whose consumer's first frame is somewhere the
     lane did not ask for; the verify clause re-resolves the index by NAME and
     PID rather than trusting the number.
  2. THE EMPTY REWIND-POINT DIRECTORY. `Parsek/RewindPoints/` exists in the
     source and is EMPTY, and `--keep-parsek` copies it verbatim. An empty
     directory is exhaust, and the save carries no `REWIND_POINTS` node for it to
     back, so it is removed and then forbidden by name.
  3. THE HARVESTED CRAFT. `Ships/` is dropped whole. The source carries
     `Kerbal X.craft` (an operator edit, so the harvest's content-addressed
     shared-library drop did not recognise it) plus KSP's `Auto-Saved Ship.craft`
     VAB autosave. This fixture is a RECORDED render subject that never launches
     anything - exactly like `duna-one-recorded`, the other free-play harvest,
     which carries no `Ships/` either - so committing two craft files here would
     be pure payload with no consumer.
  4. THE ADDONS SCAFFOLDING. The source save has no `AddOns/` at all; the
     618-byte `DistantObject/Settings.cfg` every sibling fixture carries is
     copied from a RECORDED sibling, and `verify` re-checks its size AND the
     donor's bytes.
  5. THE INV2 REPAIR - see `INV2_REPAIR_RECORDING_ID` below for the whole story.

WHAT IT DELIBERATELY DOES NOT DO, which is most of what its `duna-one-recorded`
template does, and each omission is load-bearing:

  * NO TREE IS DROPPED. Both `RECORDING_TREE`s survive whole. `c9ef80ee...` is
    the ROUTE's `backingMissionTreeId`, and `RouteStore.RevalidateSources`
    compares NINE `SOURCE` fields (treeId / treeOrder / startUT / endUT /
    sidecarEpoch / format / generation / routeProofHash / recordingId) against a
    live rebuild - any drift flips the route to `SourceChanged`, which never
    auto-recovers and kills the `GhostDriving` state this fixture exists for.
    `af5628b4...` was checked for independence and is NOT independent: its chain
    recordings `56298d83` and `ed43b6fb` carry `vesselPersistentId 3620499050`
    and `recordedVesselGuid 05d3ea0f...`, the SAME LAUNCH as the `Depot` the
    ROUTE's STOP endpoint names. It is the Depot's own launch lineage.
  * NO SIDECAR IS SWEPT. Every one of the 22 recordings in the save has its
    family on disk and vice versa - there are no orphans. (The 64-stem reading
    that suggested ~42 orphans was an artefact of reading `_stem` over the
    `_vessel.craft.txt` / `_ghost.craft.txt` MIRRORS, which the harvest prunes;
    `_stem` below strips `.txt` first so it cannot recur.)
  * NO KERBAL SLOT IS PRUNED. All four `KERBAL_SLOTS` / `CREW_REPLACEMENTS`
    entries serve kept recordings, and all eight kerbals are in the `ROSTER`.
  * ONE `.prec` IS REPAIRED, and only because the gate said so. The analyzer
    Forbid gate was run FIRST, before any repair was written, and read
    `FAIL=2 WARN=0 RED=1`: two `INV2-NO-DOUBLE-COVER` FAILs on the Transporter's
    chain segment `a85a7ae0...`. Step 5 below is the same CONTAINMENT dedupe
    `build_duna_one_recorded.py` uses, imported rather than copied, and it drops
    exactly two sections. Every other `.prec` in the fixture is byte-for-byte
    what Parsek wrote.

THE ROUTE SHAPE PINS MOVED TO THE SHARED FACET 2026-09-02. They were
builder-side only because `harness/lib/saveparse.py` had no `routes` facet; it
now parses the ROUTES node, so `verify_route` asserts
`saveparse.observed_routes_facets` against `ROUTE_FACET_PINS` - the same
vocabulary a scenario declares through `[expectations.routes]` - plus the parsed
`RouteRow`'s identity fields (id, name, status, backing tree, dock member,
`RECORDING_IDS`, `EXCLUDED_INTERVALS`). What stayed here is what a facet should
NOT model: this fixture's float clocks and cursor indices
(`ROUTE_SCALAR_PINS`), the ORIGIN's three coordinate zeros, the nine SOURCE
fields `RevalidateSources` compares, and the STOP endpoint's resolution to a
live `Depot` VESSEL node (a FLIGHTSTATE fact, not a route fact).

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture and
writes nothing. It is WIRED, not decorative: `DepotRouteRecordedFixtureDriftTests`
in `harness/lib/test_build_depot_route_recorded.py` runs the same `verify_*`
functions in-process, so a hand-edit of the committed bytes reds in the harness
suite. Like its template it CANNOT re-run `build`: the input is an operator save
outside the repo that will never be committed.

Usage:
    python harness/tools/build_depot_route_recorded.py            # finish in place
    python harness/tools/build_depot_route_recorded.py --check    # verify only

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import sys
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")
_LIB = os.path.join(_HARNESS_ROOT, "lib")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
if _LIB not in sys.path:
    sys.path.insert(0, _LIB)

# THE SHARED SAVE PARSER. The route SHAPE pins below read through
# `saveparse.observed_routes_facets` rather than this file's own line scanner,
# so the fixture check and a scenario's `[expectations.routes]` window are two
# readings of ONE parser. Same rationale as the two imports below: a second
# implementation is a second thing to drift.
import saveparse  # noqa: E402

# ONE copy of the ConfigNode-text node helpers, for the same reason
# build_duna_one_recorded.py imports them: a second implementation is a second
# thing to drift. The FILE I/O is deliberately NOT reused - those helpers
# normalise to CRLF on write, and `harvest_bdock_station.py` writes this save's
# `persistent.sfs` with an explicit LF-only newline. Keeping the harvest's own
# line endings means the committed bytes are still "what the tool chain wrote".
import build_career_pad_craft as base_builder  # noqa: E402

find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value

# The INV2 machinery is IMPORTED, not copied. `build_duna_one_recorded.py` owns
# the `.prec` reader, the containment predicate, the `Inv2NoDoubleCover`-shaped
# overlap sweep and the byte splice, all of them pure and all of them already
# unit-tested on synthetic shapes in `test_build_duna_one_recorded.py`. A second
# copy would be a second thing to drift, and the predicate is the one piece of
# judgement in either repair.
import build_duna_one_recorded as inv2  # noqa: E402

read_prec_sections = inv2.read_prec_sections
find_redundant_sections = inv2.find_redundant_sections
overlapping_pairs = inv2.overlapping_pairs
text_section_spans = inv2.text_section_spans

TARGET_NAME = "depot-route-recorded"

# The AddOns donor. Any of the ~30 fixtures carrying the 618-byte variant would
# do; a RECORDED sibling is named so the donor is a fixture of the same class.
ADDONS_DONOR_NAME = "ike-orbit-recorded"
ADDONS_REL = os.path.join("AddOns", "DistantObject", "Settings.cfg")
ADDONS_EXPECTED_BYTES = 618

# `Parsek/GameState` comes through the harvest here, unlike `duna-one-recorded`
# (whose source was a COLLECTED LOG, where collect-logs.py moves the save's
# `Parsek/` to a sibling `parsek/` and leaves only `Parsek/Recordings` behind).
# This is a direct save-directory harvest, so nothing needs restoring - only
# asserting. SIX files, not duna-one's nine.
GAMESTATE_FILES = (
    "baseline_0.pgsb",
    "baseline_1361.8067384522021.pgsb",
    "baseline_17623.328634215453.pgsb",
    "events.pgse",
    "ledger.pgld",
    "milestones.pgsm",
)

# --- the two trees, kept WHOLE ------------------------------------------
#
# Spelled out rather than derived from the save so that a re-harvest whose shape
# moved reds loudly here instead of silently shipping a different payload.
DEPOT_LINEAGE_TREE_ID = "af5628b43854443d861b312d77d4629b"      # "Kerbal X"
BACKING_TREE_ID = "c9ef80ee91b34de2b3717a4fb8bd1226"            # "Kerbal X #2"

DEPOT_LINEAGE_RECORDING_IDS = (
    "56298d8360d14db68d488f6d2aee7f72",   # 0  chain 0, the Depot's launch
    "8b76aa505ea1490282a77127eca60e42",   # 1  debris
    "271fcf13cf324aab99dedf022a0b8701",   # 2  debris
    "d44fdc4cc1284b839c0cbd52718727d9",   # 3  debris
    "0254e32d38344f81a55aa548e46fc3f8",   # 4  debris
    "6996cdece5f946558130f27e9fd94f9a",   # 5  debris
    "bc856135add54204b51fe6479fcc3947",   # 6  debris
    "3a881d7e090d420bb83d86324a1e358d",   # 7  Depot Probe
    "ed43b6fbd97b438895d9cfbaf3bf1c9b",   # 8  chain 1, the Depot in orbit
)
BACKING_TREE_RECORDING_IDS = (
    "44129e52aec64f08b25cdd3ca22ea34d",   # 0  chain 0, the launch     [ROUTE src]
    "ee42e0e523a84732a5a96b25ccedb58d",   # 1  debris
    "9290180cd1034abcb977e12c5e16ec4b",   # 2  debris
    "0d81230e06274d40a051ad865fea45f7",   # 3  debris
    "e269b79b5540422898bec716523b16e0",   # 4  debris
    "1e447d2a207c4a6faca53929c799b112",   # 5  debris
    "997efb42ed894fbd8e7f92ed628d69f4",   # 6  debris
    "8b036c83624b44e6b531f03990d31b5e",   # 7  Kerbal X Probe          [ROUTE src]
    "0c8ec58d618246e38eafedc116a262c8",   # 8  the Depot leg           [ROUTE src]
    "70667ab4a1d34ef0bc05ce9911bfcd30",   # 9  THE DOCK MEMBER         [ROUTE src]
    "ef23bb0df71645f4833607864ea2c627",   # 10 the Depot after the dock
    "efb9be7191284013983a9f3662604bc4",   # 11 Transporter
    "a85a7ae00da043c28e13fe221630ce85",   # 12 chain 1, the Transporter's launch
)
KEEP_RECORDING_IDS = DEPOT_LINEAGE_RECORDING_IDS + BACKING_TREE_RECORDING_IDS

DEPOT_LINEAGE_MISSION_ID = "ab583a948310406c91b7c7116c7799cd"
BACKING_MISSION_ID = "a414bc8c24214defb1c12adbeaed0e19"

# --- the ROUTE (decision 6) ---------------------------------------------
#
# THE SHAPE PINS MOVED TO THE SHARED FACET 2026-09-02. `saveparse.py` now parses
# the ROUTES node (`observed_routes_facets`), so the route's SHAPE and IDENTITY
# are pinned once, in the vocabulary every scenario spec can also express through
# `[expectations.routes]`, and `verify_route` reads through that parser instead
# of a private line scanner. What stayed builder-side is what a facet should NOT
# carry: this fixture's float CLOCKS and cursor indices, and the nine SOURCE
# fields `RevalidateSources` compares. Those are FIXTURE IDENTITY (a re-harvest
# must red), not a window a lane would ever declare.
ROUTE_ID = "5420f805fcbb453b8d5928b71393f14b"
# The name carries U+2192 RIGHTWARDS ARROW, which is what Parsek's route-naming
# code writes. Spelled as an escape rather than pasted so this file stays ASCII.
ROUTE_NAME = "Route: Kerbin \u2192 Kerbin"
# Asserted against the parsed `saveparse.RouteRow` (attribute -> value), so the
# node-reading half is the shared parser's, not this file's.
ROUTE_ROW_PINS = {
    "route_id": ROUTE_ID,
    "name": ROUTE_NAME,
    "status": "Active",
    "is_ksc_origin": True,
    "completed_cycles": 1,
    "skipped_cycles": 0,
    "backing_mission_tree_id": BACKING_TREE_ID,
    "dock_member_recording_id": "70667ab4a1d34ef0bc05ce9911bfcd30",
    "codec_reject": "",
    "dormant": False,
}
# The raw `key = value` remainder: every scalar the facet deliberately does not
# model. Read with `get_value` because they are TEXT pins - a float that
# round-trips through Python would compare equal after a writer change that
# altered the printed form, and the printed form is what the game reads back.
ROUTE_SCALAR_PINS = {
    "pauseAfterCurrentCycle": "True",
    "recordedDockUT": "17478.248634212287",
    "nextDispatchUT": "17478.248634212287",
    "dispatchWindowEpochUT": "1420.246738452149",
    # SameBody. The route's two ends are both Kerbin, so there is no synodic
    # window to wait on and the dispatch cadence is the transit duration alone.
    "dispatchWindowPeriod": "0",
    "dispatchInterval": "16058.001895760137",
    "transitDuration": "16058.001895760137",
    "loopAnchorUT": "-1",
    "lastObservedLoopCycleIndex": "0",
    "currentSegmentIndex": "-1",
    "pendingStopIndex": "-1",
    "kscDispatchFundsCost": "0",
}
ROUTE_RECORDING_IDS = (
    "44129e52aec64f08b25cdd3ca22ea34d",
    "8b036c83624b44e6b531f03990d31b5e",
    "0c8ec58d618246e38eafedc116a262c8",
    "70667ab4a1d34ef0bc05ce9911bfcd30",
)
# (recordingId, treeOrder, sidecarEpoch, startUT, endUT, routeProofHash) for each
# SOURCE row, in file order. These are the nine fields `RevalidateSources`
# compares (treeId / format / generation are pinned once below, being constant
# across the four rows), so this tuple IS the "do not touch the backing tree"
# assertion made mechanical.
ROUTE_SOURCE_ROWS = (
    ("44129e52aec64f08b25cdd3ca22ea34d", "0", "10",
     "1420.246738452149", "1734.5198414792137", "no-route-proof"),
    ("8b036c83624b44e6b531f03990d31b5e", "7", "3",
     "2554.2070300253936", "2889.8156530721844", "no-route-proof"),
    ("0c8ec58d618246e38eafedc116a262c8", "8", "4",
     "17265.722696710152", "17478.268634212287", "no-route-proof"),
    ("70667ab4a1d34ef0bc05ce9911bfcd30", "9", "3",
     "17478.248634212287", "17594.508634214824", "5432980487a27600"),
)
ROUTE_SOURCE_TREE_ID = BACKING_TREE_ID
ROUTE_SOURCE_FORMAT_VERSION = "1"
ROUTE_SOURCE_SCHEMA_GENERATION = "4"
ROUTE_EXCLUDED_INTERVALS = (
    "44129e52aec64f08b25cdd3ca22ea34d/seg3",
    "efb9be7191284013983a9f3662604bc4",
)
# The ORIGIN's COORDINATES only. Its body and its `isSurface` flag are facet /
# row assertions now; these three zeros are a KSC-origin detail no scenario
# window would ever carry, so they stay a raw text pin here.
ROUTE_ORIGIN = {"latitude": "0", "longitude": "0", "altitude": "0"}
ROUTE_ORIGIN_BODY = "Kerbin"
ROUTE_STOP_ENDPOINT_PID = "3620499050"
ROUTE_STOP_ENDPOINT_BODY = "Kerbin"
ROUTE_STOP_CONNECTION_KIND = "DockingPort"

# THE FACET, verbatim: `saveparse.observed_routes_facets` over this fixture's
# committed bytes. The SAME dict is pinned in `test_saveparse.py`'s
# `RECORDED_FIXTURES["depot-route-recorded"]["routes"]`, so the builder's check
# and the fixture sweep read one measurement rather than two hand-kept copies -
# and every value is spelled through the constant it belongs to, so a re-harvest
# that moved one identity cannot leave the facet copy agreeing with the old one.
ROUTE_FACET_PINS = {
    "count": 1,
    "dormant": 0,
    "stops": 1,
    "sourceRefs": len(ROUTE_SOURCE_ROWS),
    "completedCycles": 1,
    "skippedCycles": 0,
    "codecRejects": 0,
    "unparsed": 0,
    "unknownStatuses": 0,
    "statuses": {"Active": 1},
    "connectionKinds": {ROUTE_STOP_CONNECTION_KIND: 1},
    "originBodies": {ROUTE_ORIGIN_BODY: 1},
    "destinationBodies": {ROUTE_STOP_ENDPOINT_BODY: 1},
    "holdKinds": {},
    "ids": [ROUTE_ID],
    "destinationVesselPids": [ROUTE_STOP_ENDPOINT_PID],
    "dismissedCandidates": 0,
    "promptedCandidates": 0,
}

# D4: the dock member's RECORDING node must stay BYTE-EXACT. `routeProofHash`
# 5432980487a27600 is computed over that recording's ROUTE_CONNECTION_WINDOWS,
# and a mismatch is exactly the `SourceChanged` flip described above. The `.prec`
# binary is NOT part of that hash, which is why this is a node-level pin rather
# than a file-level one: it is the surface the hash actually covers.
DOCK_MEMBER_RECORDING_ID = "70667ab4a1d34ef0bc05ce9911bfcd30"
DOCK_MEMBER_NODE_SHA256 = (
    "cac2898ceb32a46e6068ffa3366bdaddc6f056cd4a0c46981dbb47262852b3bf")

# --- the active vessel (decision 5) -------------------------------------
ACTIVE_VESSEL_INDEX = 9
ACTIVE_VESSEL_NAME = "Depot"
ACTIVE_VESSEL_PID = ROUTE_STOP_ENDPOINT_PID
ACTIVE_VESSEL_SITUATION = "ORBITING"
# The asteroid the source save left focused; asserted as GONE from index 9 so a
# re-harvest that reordered FLIGHTSTATE cannot silently re-point at it.
SOURCE_ACTIVE_VESSEL_NAME = "Ast. YRJ-552"
# `Transporter` must survive too (D3): it is the vessel the route's delivery leg
# flew, and recording efb9be71 is its trajectory.
REQUIRED_VESSELS = (
    (ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID, "Ship", "ORBITING"),
    ("Transporter", "788309716", "Ship", "ORBITING"),
)
EXPECTED_VESSEL_COUNT = 19

EXPECT_KERBAL_PAIRS = (
    ("Jebediah Kerman", "Barton Kerman"),
    ("Bill Kerman", "Lagerpont Kerman"),
    ("Bob Kerman", "Adlo Kerman"),
    ("Valentina Kerman", "Ludgee Kerman"),
)

# Harvest exhaust and derived data no fixture may carry. `Backup` and
# `RewindPoints` are the two this source added to the inherited list (D7): the
# save root carries KSP's own rolling `Backup/` (four `persistent (...).sfs`
# copies, ~1.1 MB) and `Parsek/RewindPoints/` exists but is EMPTY.
FORBIDDEN_DIR_NAMES = ("_quarantine", "Saves", "Backup", "RewindPoints", "Ships")
FORBIDDEN_FILE_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")
FORBIDDEN_FILE_PREFIXES = ("quicksave",)

# Two recordings legitimately carry no ghost/vessel snapshot of their own, so the
# per-family completeness check below is stated as a floor plus these exemptions
# rather than as "every family has all four".
NO_GHOST_CRAFT_RECORDING_IDS = ("70667ab4a1d34ef0bc05ce9911bfcd30",)
NO_VESSEL_CRAFT_RECORDING_IDS = ("0c8ec58d618246e38eafedc116a262c8",)
# 22 x .prec + 22 x .pann + 21 x _vessel.craft + 21 x _ghost.craft.
EXPECTED_AUTHORITATIVE_SIDECARS = 86

# --- the INV2 repair -----------------------------------------------------
#
# THE DEFECT, as the gate reported it before anything was written:
#
#   FAIL INV2-NO-DOUBLE-COVER target=a85a7ae0...#27
#     a=[6163.7967133907259,6194.1851923946306] b=[6163.7967133907259,6590.41224317588]
#   FAIL INV2-NO-DOUBLE-COVER target=a85a7ae0...#28
#     a=[6163.7967133907259,6590.41224317588]   b=[6194.1851923946306,6590.41224317588]
#
# `a85a7ae00da043c28e13fe221630ce85` is the TRANSPORTER's launch chain segment
# (treeOrder 12, chainIndex 1, 638 points), and it carries 50 TrackSections of
# which three describe one span three ways:
#
#   idx 26  [6163.7967133907259, 6194.1851923946306]  65 bytes, NO ORBIT_SEGMENT
#   idx 27  [6163.7967133907259, 6590.41224317588]    the OrbitalCheckpoint
#   idx 28  [6194.1851923946306, 6590.41224317588]    a RE-CLIP of 27's conic
#
# 26 and 28 partition 27's span EXACTLY, so both are contained and the covered
# union cannot move. Nothing unique is lost either: 26 is a frame-less `ref=2
# src=2` shell with no ORBIT_SEGMENT at all, and 28's nested segment is
# element-for-element identical to 27's - same inc / ecc / sma / lan / argPe /
# mna / body / ofr*, and the same `epoch = 6163.7967133907259`, i.e. it is
# literally 27's orbit re-clipped to a later startUT.
#
# THE REPAIRED RECORDING IS NOT A ROUTE SOURCE, which is the property that makes
# this safe to do at all in a route fixture: `a85a7ae0` is not one of the four
# `ROUTE_RECORDING_IDS`, so no `routeProofHash` and no `SOURCE` row's
# `sidecarEpoch` / `startUT` / `endUT` covers it. `verify_prec` asserts that
# rather than leaving it to this comment.
INV2_REPAIR_RECORDING_ID = "a85a7ae00da043c28e13fe221630ce85"
INV2_DROPPED_SECTION_INDICES = (26, 28)
INV2_EXPECTED_SECTIONS_BEFORE = 50
INV2_EXPECTED_SECTIONS_AFTER = 48
# The one span the three sections argued over; asserted still present exactly
# ONCE after the repair.
INV2_KEPT_SPAN = (6163.7967133907259, 6590.41224317588)


# ---------------------------------------------------------------------------
# File I/O that preserves the harvest's LF line endings.
# ---------------------------------------------------------------------------


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def read_lines(path: str) -> List[str]:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


def write_lines(path: str, lines: List[str]) -> None:
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write("\n".join(lines))


def parsek_scenario(lines: List[str]) -> Optional[Tuple[int, int]]:
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == "ParsekScenario":
            return node
        i = node[1]


def route_node(lines: List[str]) -> Optional[Tuple[int, int]]:
    scn = parsek_scenario(lines)
    if scn is None:
        return None
    routes = child_nodes(lines, scn, "ROUTES")
    if len(routes) != 1:
        return None
    entries = child_nodes(lines, routes[0], "ROUTE")
    return entries[0] if len(entries) == 1 else None


def flightstate_node(lines: List[str]) -> Optional[Tuple[int, int]]:
    return find_node(lines, "FLIGHTSTATE")


def vessel_records(lines: List[str]) -> List[dict]:
    """(name, pid, type, sit) per FLIGHTSTATE VESSEL, in `activeVessel` order.

    Scoped to FLIGHTSTATE's DIRECT children, for the reason
    `harvest_bdock_station.flightstate_span` spells out: `activeVessel` is an
    index into exactly those nodes, and a VESSEL node living anywhere else in the
    save would shift every index by one."""
    fs = flightstate_node(lines)
    if fs is None:
        return []
    out = []
    for node in child_nodes(lines, fs, "VESSEL"):
        out.append({"name": get_value(lines, node, "name"),
                    "pid": get_value(lines, node, "persistentId"),
                    "type": get_value(lines, node, "type"),
                    "sit": get_value(lines, node, "sit")})
    return out


def _stem(name: str) -> str:
    """The recording id a sidecar file belongs to.

    `.txt` IS STRIPPED FIRST, unlike the `duna-one-recorded` template's copy, and
    that is not cosmetic: without it `X_ghost.craft.txt` falls through to
    `name.split('.')[0]` and reads as a family called `X_ghost`. That is exactly
    how this save's 22 recordings once read as 64 families and produced a phantom
    orphan sweep in the harvest plan."""
    if name.endswith(".txt"):
        name = name[:-len(".txt")]
    for suffix in ("_vessel.craft", "_ghost.craft"):
        if name.endswith(suffix):
            return name[:-len(suffix)]
    return name.split(".")[0]


def dock_member_node_digest(lines: List[str]) -> Optional[str]:
    """sha256 over the dock member's RECORDING node lines, verbatim.

    THE HASH IS OVER THE NODE, NOT THE FILE, because that is the surface
    `routeProofHash` covers: the recording's `ROUTE_CONNECTION_WINDOWS` live in
    the save, and the `.prec` binary is not hashed at all."""
    scn = parsek_scenario(lines)
    if scn is None:
        return None
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        for rec in child_nodes(lines, tree, "RECORDING"):
            if get_value(lines, rec, "recordingId") == DOCK_MEMBER_RECORDING_ID:
                blob = "\n".join(lines[rec[0]:rec[1]]).encode("utf-8")
                return hashlib.sha256(blob).hexdigest()
    return None


# ---------------------------------------------------------------------------
# Step 1: the active-vessel re-point.
# ---------------------------------------------------------------------------


def repoint_active_vessel(lines: List[str]) -> Tuple[List[str], str]:
    """Point `FLIGHTSTATE/activeVessel` at the `Depot`. Returns (lines, note).

    The index is RE-RESOLVED from the VESSEL list by name + persistentId rather
    than taken from the constant, and the constant is then asserted against it -
    so a re-harvest that reordered FLIGHTSTATE reds naming the new index instead
    of silently focusing whatever now sits at 9."""
    out = list(lines)
    fs = flightstate_node(out)
    if fs is None:
        raise SystemExit("harvested save has no FLIGHTSTATE node")

    records = vessel_records(out)
    if len(records) != EXPECTED_VESSEL_COUNT:
        raise SystemExit(
            "FLIGHTSTATE carries %d VESSEL nodes, expected %d - the source save "
            "moved and every index in this recipe must be re-derived"
            % (len(records), EXPECTED_VESSEL_COUNT))

    matches = [i for i, r in enumerate(records)
               if r["name"] == ACTIVE_VESSEL_NAME and r["pid"] == ACTIVE_VESSEL_PID]
    if len(matches) != 1:
        raise SystemExit(
            "expected exactly one VESSEL named %r with persistentId %s, found %d"
            % (ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID, len(matches)))
    index = matches[0]
    if index != ACTIVE_VESSEL_INDEX:
        raise SystemExit(
            "%r resolves to FLIGHTSTATE index %d, not the documented %d - "
            "re-derive ACTIVE_VESSEL_INDEX before writing"
            % (ACTIVE_VESSEL_NAME, index, ACTIVE_VESSEL_INDEX))
    if records[index]["sit"] != ACTIVE_VESSEL_SITUATION:
        raise SystemExit(
            "%r is %r, expected %r - the harvest's situation gate was armed "
            "against this" % (ACTIVE_VESSEL_NAME, records[index]["sit"],
                              ACTIVE_VESSEL_SITUATION))

    before = get_value(out, fs, "activeVessel")
    if not set_value(out, fs, "activeVessel", str(index)):
        raise SystemExit("FLIGHTSTATE has no activeVessel value to rewrite")
    return out, "activeVessel %s -> %d (%r, pid %s, %s)" % (
        before, index, ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID,
        ACTIVE_VESSEL_SITUATION)


# ---------------------------------------------------------------------------
# Step 5: the INV2 repair. Thin wrapper over the imported machinery; the reader,
# predicate and splice all live in build_duna_one_recorded.py.
# ---------------------------------------------------------------------------


def repair_prec(recordings_dir: str) -> List[dict]:
    """Run the INV2 containment dedupe over the Transporter segment's sidecar.

    Delegates to `inv2.repair_prec` by pointing its module constant at THIS
    fixture's recording for the call. Rebinding rather than re-implementing keeps
    one copy of the coverage-invariance assertion, the partial-overlap refusal
    and the mirror-agreement check; the try/finally restores the constant so a
    process that imported both builders cannot leave the other one aimed here."""
    saved = inv2.INV2_REPAIR_RECORDING_ID
    inv2.INV2_REPAIR_RECORDING_ID = INV2_REPAIR_RECORDING_ID
    try:
        return inv2.repair_prec(recordings_dir)
    finally:
        inv2.INV2_REPAIR_RECORDING_ID = saved


# ---------------------------------------------------------------------------
# Post-conditions. Run on the freshly finished fixture AND on --check.
# ---------------------------------------------------------------------------


def verify_save(lines: List[str]) -> List[str]:
    """Failure strings for the save half of the fixture (empty = all hold)."""
    problems: List[str] = []

    scn = parsek_scenario(lines)
    if scn is None:
        return ["no ParsekScenario SCENARIO node"]

    trees = child_nodes(lines, scn, "RECORDING_TREE")
    ids = [get_value(lines, t, "id") for t in trees]
    if ids != [DEPOT_LINEAGE_TREE_ID, BACKING_TREE_ID]:
        problems.append("RECORDING_TREE ids are %r, expected the two kept trees "
                        "%r" % (ids, [DEPOT_LINEAGE_TREE_ID, BACKING_TREE_ID]))
    else:
        for tree, want in ((trees[0], DEPOT_LINEAGE_RECORDING_IDS),
                           (trees[1], BACKING_TREE_RECORDING_IDS)):
            got = [get_value(lines, r, "recordingId")
                   for r in child_nodes(lines, tree, "RECORDING")]
            if got != list(want):
                problems.append(
                    "tree %s carries recordings %r, expected %r - the backing "
                    "tree must stay WHOLE or RouteStore.RevalidateSources flips "
                    "the route to SourceChanged"
                    % (get_value(lines, tree, "id"), got, list(want)))
            # treeOrder is persisted verbatim and is one of the nine fields
            # RevalidateSources compares, so a renumber is a route killer.
            orders = [get_value(lines, r, "treeOrder")
                      for r in child_nodes(lines, tree, "RECORDING")]
            if orders != [str(i) for i in range(len(orders))]:
                problems.append("tree %s treeOrders are %r, expected 0..%d"
                                % (get_value(lines, tree, "id"), orders,
                                   len(orders) - 1))

    missions = child_nodes(lines, scn, "MISSION")
    mission_map = {get_value(lines, m, "treeId"): get_value(lines, m, "id")
                   for m in missions}
    if mission_map != {DEPOT_LINEAGE_TREE_ID: DEPOT_LINEAGE_MISSION_ID,
                       BACKING_TREE_ID: BACKING_MISSION_ID}:
        problems.append("MISSION rows are %r, expected one per kept tree"
                        % (mission_map,))

    for name in ("RECORDING_SUPERSEDES", "LEDGER_TOMBSTONES", "REWIND_POINTS",
                 "REWIND_RETIREMENTS"):
        if child_nodes(lines, scn, name):
            problems.append("ParsekScenario carries a %s node" % name)

    # Every schema generation in the save must be the current one; a stray older
    # value would be a recording RecordingStore rejects at load.
    gens = {line.strip().split("=", 1)[1].strip() for line in lines
            if line.strip().startswith("recordingSchemaGeneration = ")}
    if gens != {"4"}:
        problems.append("recordingSchemaGeneration values are %r, expected {'4'}"
                        % (sorted(gens),))

    # The harvester clears these; INV9's dangling-hint WARN depends on it.
    dangling = [i for i, line in enumerate(lines, 1)
                if line.strip().startswith("rewindSave = parsek_rw_")]
    if dangling:
        problems.append("a rewindSave = parsek_rw_* hint survived at line(s) %s"
                        % (dangling,))

    problems += _verify_kerbals(lines, scn)
    problems += _verify_active_vessel(lines)
    problems += verify_route(lines)
    return problems


def _verify_kerbals(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    problems: List[str] = []

    slots = child_nodes(lines, scn, "KERBAL_SLOTS")
    if len(slots) != 1:
        problems.append("expected exactly 1 KERBAL_SLOTS node, found %d" % len(slots))
    else:
        pairs = []
        for slot in child_nodes(lines, slots[0], "SLOT"):
            entries = child_nodes(lines, slot, "CHAIN_ENTRY")
            standin = get_value(lines, entries[0], "name") if entries else None
            pairs.append((get_value(lines, slot, "owner"), standin))
        if pairs != list(EXPECT_KERBAL_PAIRS):
            problems.append("KERBAL_SLOTS owner/stand-in pairs are %r, expected %r"
                            % (pairs, list(EXPECT_KERBAL_PAIRS)))

    repl = child_nodes(lines, scn, "CREW_REPLACEMENTS")
    if len(repl) != 1:
        problems.append("expected exactly 1 CREW_REPLACEMENTS node, found %d"
                        % len(repl))
    else:
        pairs = [(get_value(lines, e, "original"), get_value(lines, e, "replacement"))
                 for e in child_nodes(lines, repl[0], "ENTRY")]
        if pairs != list(EXPECT_KERBAL_PAIRS):
            problems.append("CREW_REPLACEMENTS is %r, expected %r"
                            % (pairs, list(EXPECT_KERBAL_PAIRS)))

    # THE DANGLING-REFERENCE HALF. A replacement naming a kerbal the ROSTER does
    # not carry is exactly the shape a prune could produce by accident - and this
    # recipe prunes no kerbals, so this cell states that as a fact rather than
    # trusting it.
    roster = find_node(lines, "ROSTER")
    if roster is None:
        problems.append("save has no ROSTER node")
    else:
        names = {get_value(lines, k, "name")
                 for k in child_nodes(lines, roster, "KERBAL")}
        for owner, standin in EXPECT_KERBAL_PAIRS:
            for who in (owner, standin):
                if who not in names:
                    problems.append("ROSTER has no %r: the reservation would "
                                    "resolve nothing" % who)
    return problems


def _verify_active_vessel(lines: List[str]) -> List[str]:
    problems: List[str] = []
    fs = flightstate_node(lines)
    if fs is None:
        return ["save has no FLIGHTSTATE node"]

    records = vessel_records(lines)
    if len(records) != EXPECTED_VESSEL_COUNT:
        problems.append("FLIGHTSTATE carries %d VESSEL nodes, expected %d"
                        % (len(records), EXPECTED_VESSEL_COUNT))

    raw = get_value(lines, fs, "activeVessel")
    try:
        index = int(raw)
    except (TypeError, ValueError):
        return problems + ["activeVessel is %r, not an index" % raw]

    if not 0 <= index < len(records):
        problems.append("activeVessel %d does not resolve to a VESSEL node" % index)
        return problems
    active = records[index]
    if (active["name"], active["pid"]) != (ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID):
        problems.append(
            "activeVessel %d is %r (pid %s), expected %r (pid %s) - the "
            "re-point did not happen, or FLIGHTSTATE was reordered"
            % (index, active["name"], active["pid"], ACTIVE_VESSEL_NAME,
               ACTIVE_VESSEL_PID))
    if active["name"] == SOURCE_ACTIVE_VESSEL_NAME:
        problems.append("activeVessel is still the source save's asteroid %r"
                        % SOURCE_ACTIVE_VESSEL_NAME)
    if active["sit"] != ACTIVE_VESSEL_SITUATION:
        problems.append("the active vessel is %r, expected %r"
                        % (active["sit"], ACTIVE_VESSEL_SITUATION))

    # D3: the ROUTE's STOP endpoint vessel and the delivery vessel must both be
    # alive in FLIGHTSTATE with the right type and situation.
    by_pid = {r["pid"]: r for r in records}
    for name, pid, vtype, sit in REQUIRED_VESSELS:
        record = by_pid.get(pid)
        if record is None:
            problems.append("FLIGHTSTATE carries no vessel with persistentId %s "
                            "(%s)" % (pid, name))
            continue
        if (record["name"], record["type"], record["sit"]) != (name, vtype, sit):
            problems.append(
                "vessel %s is %r/%s/%s, expected %r/%s/%s"
                % (pid, record["name"], record["type"], record["sit"],
                   name, vtype, sit))
    return problems


def verify_route(lines: List[str]) -> List[str]:
    """THE ROUTE PIN (decision 6).

    THE SHAPE HALF READS THROUGH THE SHARED FACET. `saveparse.py` parses the
    ROUTES node as of 2026-09-02, so route count / status / stops / source-row
    count / endpoint bodies / route id / endpoint pid are asserted as the SAME
    facet dict a scenario spec declares through `[expectations.routes]` - one
    parser, one vocabulary, and a shape drift now reds identically here and on a
    lane. What is still read raw below is what no facet models: this fixture's
    float clocks, its cursor indices, and the nine SOURCE fields
    `RouteStore.RevalidateSources` compares against a live rebuild.
    """
    problems: List[str] = []

    scn = parsek_scenario(lines)
    if scn is None:
        return ["no ParsekScenario SCENARIO node"]
    routes = child_nodes(lines, scn, "ROUTES")
    if len(routes) != 1:
        return ["expected exactly 1 ROUTES node, found %d" % len(routes)]
    entries = child_nodes(lines, routes[0], "ROUTE")
    if len(entries) != 1:
        return ["expected exactly 1 ROUTE, found %d" % len(entries)]
    route = entries[0]

    # --- the SHARED FACET ------------------------------------------------
    snap = saveparse.parse_parsek_scenario("\n".join(lines))
    if not snap.parsed:
        return ["saveparse could not read the save: %s" % snap.error]
    facet = saveparse.observed_routes_facets(snap)
    for key in sorted(ROUTE_FACET_PINS):
        want = ROUTE_FACET_PINS[key]
        got = facet.get(key)
        if got != want:
            problems.append("ROUTE facet %s is %r, expected %r" % (key, got, want))
    if len(snap.routes) != 1:
        # Guarded above through the line scanner too; keep the parser's own
        # count as the thing every later read below indexes into.
        problems.append("saveparse read %d committed route(s), expected 1"
                        % len(snap.routes))
    else:
        row = snap.routes[0]
        for attr in sorted(ROUTE_ROW_PINS):
            want = ROUTE_ROW_PINS[attr]
            got = getattr(row, attr)
            if got != want:
                problems.append("ROUTE %s is %r, expected %r" % (attr, got, want))
        if list(row.recording_ids) != list(ROUTE_RECORDING_IDS):
            problems.append("ROUTE RECORDING_IDS are %r, expected %r"
                            % (list(row.recording_ids), list(ROUTE_RECORDING_IDS)))
        missing = [r for r in row.recording_ids if r not in KEEP_RECORDING_IDS]
        if missing:
            problems.append("ROUTE names recording(s) no kept tree carries: %r"
                            % (missing,))
        if list(row.excluded_intervals) != list(ROUTE_EXCLUDED_INTERVALS):
            problems.append("ROUTE EXCLUDED_INTERVALS are %r, expected %r"
                            % (list(row.excluded_intervals),
                               list(ROUTE_EXCLUDED_INTERVALS)))
        if row.origin is None or not row.origin.is_surface:
            problems.append("ROUTE ORIGIN is %r, expected a SURFACE endpoint "
                            "(the KSC origin)" % (row.origin,))

    # --- the raw scalar remainder ---------------------------------------
    for key, want in sorted(ROUTE_SCALAR_PINS.items()):
        got = get_value(lines, route, key)
        if got != want:
            problems.append("ROUTE %s is %r, expected %r" % (key, got, want))

    refs = child_nodes(lines, route, "SOURCE_REFS")
    if len(refs) != 1:
        problems.append("ROUTE has %d SOURCE_REFS nodes, expected 1" % len(refs))
    else:
        rows = []
        for src in child_nodes(lines, refs[0], "SOURCE"):
            rows.append(tuple(get_value(lines, src, k) for k in
                              ("recordingId", "treeOrder", "sidecarEpoch",
                               "startUT", "endUT", "routeProofHash")))
            for key, want in (("treeId", ROUTE_SOURCE_TREE_ID),
                              ("recordingFormatVersion",
                               ROUTE_SOURCE_FORMAT_VERSION),
                              ("recordingSchemaGeneration",
                               ROUTE_SOURCE_SCHEMA_GENERATION)):
                got = get_value(lines, src, key)
                if got != want:
                    problems.append("ROUTE SOURCE %s %s is %r, expected %r"
                                    % (rows[-1][0], key, got, want))
        if rows != list(ROUTE_SOURCE_ROWS):
            problems.append(
                "ROUTE SOURCE rows are %r, expected %r - these are the fields "
                "RouteStore.RevalidateSources compares against a live rebuild"
                % (rows, list(ROUTE_SOURCE_ROWS)))

    # The ORIGIN's own COORDINATES. The facet pins the origin's BODY and the
    # row check its `isSurface`; the three zeros are a KSC-origin detail no
    # window would carry, so they stay raw.
    origin = child_nodes(lines, route, "ORIGIN")
    if len(origin) != 1:
        problems.append("ROUTE has %d ORIGIN nodes, expected 1" % len(origin))
    else:
        for key, want in sorted(ROUTE_ORIGIN.items()):
            got = get_value(lines, origin[0], key)
            if got != want:
                problems.append("ROUTE ORIGIN %s is %r, expected %r"
                                % (key, got, want))

    # THE ENDPOINT MUST RESOLVE. The facet asserts the STOP endpoint's pid and
    # body; what it cannot see - and what the D3 keep-list exists to protect -
    # is whether that pid still names a live FLIGHTSTATE vessel. A route whose
    # STOP points at a deleted vessel is invisible in the ROUTE node alone.
    live = [r for r in vessel_records(lines) if r["pid"] == ROUTE_STOP_ENDPOINT_PID]
    if len(live) != 1:
        problems.append(
            "ROUTE STOP endpoint pid %s resolves to %d FLIGHTSTATE "
            "vessel(s), expected exactly 1" % (ROUTE_STOP_ENDPOINT_PID, len(live)))
    elif live[0]["name"] != ACTIVE_VESSEL_NAME:
        problems.append("ROUTE STOP endpoint pid %s resolves to %r, expected %r"
                        % (ROUTE_STOP_ENDPOINT_PID, live[0]["name"],
                           ACTIVE_VESSEL_NAME))

    # D4: the dock member's RECORDING node, byte-exact.
    digest = dock_member_node_digest(lines)
    if digest is None:
        problems.append("no RECORDING node for the dock member %s"
                        % DOCK_MEMBER_RECORDING_ID)
    elif DOCK_MEMBER_NODE_SHA256 != "PLACEHOLDER" and digest != DOCK_MEMBER_NODE_SHA256:
        problems.append(
            "the dock member %s's RECORDING node hashes %s, expected %s - its "
            "ROUTE_CONNECTION_WINDOWS back routeProofHash %s and any edit flips "
            "the route to SourceChanged"
            % (DOCK_MEMBER_RECORDING_ID, digest, DOCK_MEMBER_NODE_SHA256,
               ROUTE_SOURCE_ROWS[3][5]))
    return problems


def _repeated_values(lines: List[str], node: Tuple[int, int], key: str) -> List[str]:
    """Every DIRECT `key = value` inside ``node``, in file order.

    `get_value` returns only the FIRST, and `RECORDING_IDS` / `EXCLUDED_INTERVALS`
    are repeated-key nodes, so reading them with `get_value` would silently pin
    one entry out of four."""
    start, end = node
    prefix = key + " ="
    out: List[str] = []
    depth = 0
    for i in range(start + 2, end - 1):
        s = lines[i].strip()
        if s == "{":
            depth += 1
        elif s == "}":
            depth -= 1
        elif depth == 0 and s.startswith(prefix):
            out.append(s[len(prefix):].strip())
    return out


def verify_tree(fixture_dir: str) -> List[str]:
    """Failure strings for the FILE-TREE half of the fixture."""
    problems: List[str] = []

    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")
    if not os.path.isdir(recordings):
        return ["fixture carries no Parsek/Recordings directory"]

    names = sorted(n for n in os.listdir(recordings)
                   if os.path.isfile(os.path.join(recordings, n)))
    stems = sorted({_stem(n) for n in names})
    if stems != sorted(KEEP_RECORDING_IDS):
        problems.append("sidecar families on disk are %r, expected the %d kept "
                        "ids" % (stems, len(KEEP_RECORDING_IDS)))

    for rid in KEEP_RECORDING_IDS:
        wanted = [".prec", ".prec.txt", ".pann"]
        if rid not in NO_VESSEL_CRAFT_RECORDING_IDS:
            wanted.append("_vessel.craft")
        if rid not in NO_GHOST_CRAFT_RECORDING_IDS:
            wanted.append("_ghost.craft")
        for suffix in wanted:
            path = os.path.join(recordings, rid + suffix)
            if not os.path.isfile(path) or os.path.getsize(path) == 0:
                problems.append("%s%s missing or empty" % (rid, suffix))

    authoritative = [n for n in names if not n.endswith(".txt")]
    if len(authoritative) != EXPECTED_AUTHORITATIVE_SIDECARS:
        problems.append("Parsek/Recordings carries %d authoritative sidecar(s), "
                        "expected %d" % (len(authoritative),
                                         EXPECTED_AUTHORITATIVE_SIDECARS))

    for dirpath, dirnames, filenames in os.walk(fixture_dir):
        for d in dirnames:
            if d in FORBIDDEN_DIR_NAMES:
                problems.append("fixture carries a forbidden directory %s"
                                % os.path.relpath(os.path.join(dirpath, d),
                                                  fixture_dir))
        for f in filenames:
            rel = os.path.relpath(os.path.join(dirpath, f), fixture_dir)
            if f.endswith(FORBIDDEN_FILE_SUFFIXES):
                problems.append("fixture carries a snapshot mirror %s" % rel)
            if f.startswith(FORBIDDEN_FILE_PREFIXES):
                problems.append("fixture carries a quicksave %s" % rel)

    gamestate = os.path.join(fixture_dir, "Parsek", "GameState")
    if not os.path.isdir(gamestate):
        problems.append("fixture carries no Parsek/GameState directory")
    else:
        got = sorted(os.listdir(gamestate))
        if got != sorted(GAMESTATE_FILES):
            problems.append("Parsek/GameState carries %r, expected %r"
                            % (got, sorted(GAMESTATE_FILES)))

    addons = os.path.join(fixture_dir, ADDONS_REL)
    if not os.path.isfile(addons):
        problems.append("fixture carries no %s" % ADDONS_REL.replace("\\", "/"))
    else:
        size = os.path.getsize(addons)
        if size != ADDONS_EXPECTED_BYTES:
            problems.append("%s is %d bytes, expected %d"
                            % (ADDONS_REL.replace("\\", "/"), size,
                               ADDONS_EXPECTED_BYTES))
        donor = os.path.join(_SAVES, ADDONS_DONOR_NAME, ADDONS_REL)
        if os.path.isfile(donor) and _read_bytes(donor) != _read_bytes(addons):
            problems.append("%s differs from the %s donor's copy"
                            % (ADDONS_REL.replace("\\", "/"), ADDONS_DONOR_NAME))
    return problems


def verify_prec(fixture_dir: str) -> List[str]:
    """Failure strings for the INV2 repair: the result must be STABLE.

    Re-running the dedupe over the committed `.prec` must drop nothing (the
    repair already ran), the section list must be overlap-free in the exact sense
    `Inv2NoDoubleCover` means, and the mirror must still describe the same list.
    Those three together are the byte-stability claim, made against the surface
    that matters rather than against a hash."""
    problems: List[str] = []
    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")
    prec = os.path.join(recordings, INV2_REPAIR_RECORDING_ID + ".prec")
    if not os.path.isfile(prec):
        return ["the repaired recording %s.prec is missing"
                % INV2_REPAIR_RECORDING_ID]

    # THE SAFETY PRECONDITION, asserted rather than commented: the repair must
    # never touch a recording the ROUTE's SOURCE_REFS cover, because those rows
    # are what RouteStore.RevalidateSources compares against a live rebuild.
    if INV2_REPAIR_RECORDING_ID in ROUTE_RECORDING_IDS:
        problems.append("the INV2 repair targets %s, which IS a ROUTE source - "
                        "editing its sidecar risks a SourceChanged flip"
                        % INV2_REPAIR_RECORDING_ID)

    blob = _read_bytes(prec)
    _count_offset, sections = read_prec_sections(blob)
    if len(sections) != INV2_EXPECTED_SECTIONS_AFTER:
        problems.append("%s carries %d TrackSections, expected %d"
                        % (INV2_REPAIR_RECORDING_ID, len(sections),
                           INV2_EXPECTED_SECTIONS_AFTER))

    still_redundant = find_redundant_sections(sections)
    if still_redundant:
        problems.append("the dedupe is not stable: re-running it would drop %r"
                        % (still_redundant,))

    overlaps = overlapping_pairs(sections)
    if overlaps:
        problems.append("sections still overlap (Inv2NoDoubleCover would FAIL): %r"
                        % (overlaps,))

    spans = [(s["startUT"], s["endUT"]) for s in sections]
    duplicated = sorted(set(s for s in spans if spans.count(s) > 1))
    if duplicated:
        problems.append("these spans are still carried by two sections: %r"
                        % (duplicated,))
    # The span the three sections argued over must survive exactly once: the
    # repair removed the redundant COPIES, not the coverage.
    if spans.count(INV2_KEPT_SPAN) != 1:
        problems.append("the kept span %r appears %d time(s), expected exactly 1"
                        % (INV2_KEPT_SPAN, spans.count(INV2_KEPT_SPAN)))

    with open(prec + ".txt", "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    mirror = text_section_spans(text.replace("\r\n", "\n"))
    if len(mirror) != len(sections):
        problems.append("the .prec.txt mirror carries %d TrackSections against "
                        "the binary's %d" % (len(mirror), len(sections)))
    else:
        for section, span in zip(sections, mirror):
            if section["startUT"] != span[2] or section["endUT"] != span[3]:
                problems.append("section %d differs between the binary and its "
                                "mirror" % section["index"])
                break
    return problems


# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of finishing it")
    parser.add_argument("--print-pins", action="store_true",
                        help="print the measured pins (the dock member's node "
                             "digest) and exit; used once to author the constant")
    parser.add_argument("--target-name", default=TARGET_NAME)
    args = parser.parse_args(argv)

    fixture_dir = os.path.join(_SAVES, args.target_name)
    sfs = os.path.join(fixture_dir, "persistent.sfs")
    if not os.path.isfile(sfs):
        print("FAIL: %s does not exist (run harvest_bdock_station.py first)" % sfs)
        return 1

    if args.print_pins:
        print("DOCK_MEMBER_NODE_SHA256 = %s"
              % dock_member_node_digest(read_lines(sfs)))
        return 0

    if args.check:
        problems = verify_save(read_lines(sfs))
        problems += verify_tree(fixture_dir)
        problems += verify_prec(fixture_dir)
        for p in problems:
            print("FAIL: %s" % p)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    # --- 1: the active-vessel re-point ----------------------------------
    lines, note = repoint_active_vessel(read_lines(sfs))
    write_lines(sfs, lines)
    print("re-pointed %s" % note)

    # --- 2 + 3: the two pruned directories ------------------------------
    for rel in (os.path.join("Parsek", "RewindPoints"), "Ships"):
        path = os.path.join(fixture_dir, rel)
        if os.path.isdir(path):
            shutil.rmtree(path)
            print("pruned %s/" % rel.replace("\\", "/"))

    # --- 4: AddOns ------------------------------------------------------
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

    # --- 5: the INV2 repair ---------------------------------------------
    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")
    dropped = repair_prec(recordings)
    if dropped:
        print("INV2 repair on %s: dropped %d redundant TrackSection(s)"
              % (INV2_REPAIR_RECORDING_ID, len(dropped)))
        for section in dropped:
            print("  idx %-3d [%r, %r]"
                  % (section["index"], section["startUT"], section["endUT"]))
        got = tuple(s["index"] for s in dropped)
        if got != INV2_DROPPED_SECTION_INDICES:
            print("FAIL: the dedupe dropped %r, expected the documented %r"
                  % (got, INV2_DROPPED_SECTION_INDICES))
            return 1
    else:
        print("INV2 repair on %s: nothing to drop (already repaired)"
              % INV2_REPAIR_RECORDING_ID)

    problems = verify_save(read_lines(sfs))
    problems += verify_tree(fixture_dir)
    problems += verify_prec(fixture_dir)
    for p in problems:
        print("FAIL: %s" % p)
    if problems:
        return 1
    print("OK: wrote %s" % fixture_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
