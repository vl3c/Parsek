#!/usr/bin/env python3
"""Finish and gate `refly-a-recorded`, the RE-FLY CONTINUATION subject.

WHAT THIS SAVE IS. The operator's manual Re-Fly session of 2026-09-08 on the DEV
instance, collected to
`logs/2026-09-08_2317_refly-a-manual/` against main `2effc9d49`. A crewed
"Kerbal X With Probe" launched, the probe decoupled at UT 189.32 into a
TWO-SLOT RewindPoint (slot 0 = the pod, slot 1 = the probe), and the pod was
still ALIVE and SUB-ORBITAL when the scene exited at UT 1078.5. The scene-exit
finalizer took the stock patched-conic tail, clipped it at atmosphere entry and
handed the descent to the ballistic extrapolator, which appended TWO
`isPredicted` ORBIT_SEGMENTs running 1078.05 -> 2186.58 -> 2348.55 and stamped
`terminal = Destroyed` at the predicted impact. The commit promoted the pod's
slot to `CommittedProvisional` (`IsUnfinishedFlight=true ... reason=crashed`),
and then, 230 ms later inside the SAME commit, the optimizer's phase-change
split cut the recording at UT 191.04 into a HEAD and a brand-new chain TIP that
carries the terminal but NOT the `MergeState`. Open/closed is read from the TIP,
so the slot silently closed (`reason=sealedTipClosed`), the pod's Unfinished
Flights row never drew, and after the probe's re-fly merged
`ReapOrphanedRPs: reaped=1` deleted the RewindPoint quicksave permanently.

WHY IT IS WORTH COMMITTING. Both defects the RF program was authored against are
FROZEN IN THESE BYTES, which is why this save is harvested rather than
synthesised. No generator can produce either shape today: `ScenarioWriter` can
stamp a `mergeState` and a terminal but cannot author a chain HEAD/TIP pair, and
no generator or fixture has ever passed `isPredicted: true` to
`RecordingBuilder.AddOrbitSegment` - the only `isPredicted` data in the tree is
built inline by three in-game cells. The two shapes are:

  (1) THE CONTINUATION DEFECT, readable with no flight at all. The HEAD
      `32ca5546...` carries `chainIndex = 0`, `terminalState = 3` (SubOrbital),
      `mergeState = CommittedProvisional` and ZERO orbit segments; the TIP
      `8da7c2c2...` carries `chainIndex = 1`, `terminalState = 4` (Destroyed),
      all three orbit segments - and NO `mergeState` key at all, which
      `RecordingTreeRecordCodec` reads back as `Immutable`. That pair IS the
      defect: the terminal moved to the tip and the open bit did not.
  (2) THE RENDER DEFECT. The TIP's two `isPredicted` segments both have a
      SUBSURFACE periapsis (sma 865777.71214532177, ecc 0.31755526014312058 ->
      periapsis radius 590,845.45 m against Kerbin's 600,000 m), which is what
      `GhostTrajectoryPolylineRenderer.IsOrbitSegmentBelowSurface` drops from the
      forward-arc pass; and the chain HEAD, which map-presence source resolution
      reads, has no segments for the proto orbit line to seed from
      (`hasOrbitSegments=False` -> `orbitSource=state-vector-fallback`).

THIS FIXTURE CANNOT RE-FLY, AND THAT IS THE POINT OF SAYING SO. The RewindPoint
was reaped by the defect before the save was collected: `REWIND_POINTS` is
absent and `Parsek/RewindPoints/` never existed in the snapshot. So the fixture
serves the RENDER lanes (RF-7M / RF-7T) and any load-time / classification lane;
a lane that needs a live RewindPoint must FLY one (the RF-1 mission variant) or
inject one of the three xUnit rewind presets.

INPUT, and what the generic harvester already did:

    python harness/tools/harvest_bdock_station.py \\
        --save-dir <log-copy>/saves/re-fly-a --target-name refly-a-recorded \\
        --expect-situation PRELAUNCH --keep-parsek

(the gate passed on '#autoLOC_501224' PRELAUNCH, vessels=7). That copied
`persistent.sfs` + `persistent.loadmeta` + the whole `Parsek/` payload, rewrote
the title to `refly-a-recorded (SANDBOX)`, cleared the one dangling
`rewindSave = parsek_rw_*` hint, and pruned `Parsek/Saves/` (the legacy
rewind-to-LAUNCH quicksave plus a career-start snapshot) and the two snapshot
`.craft.txt` mirror families. `quicksave.sfs` / `quicksave.loadmeta` are simply
not in the harvester's keep set, so no lane can accidentally depend on them.

WHAT THIS TOOL ADDS, step 1: `AddOns/DistantObject/Settings.cfg`, restored from a
committed sibling because the operator's dev-instance save carries no `AddOns/`
at all - exactly the step `build_duna_one_recorded.py` takes for the same reason.

STEP 2 IS THE ONE EDIT TO THE SAVE BODY, AND IT IS WHAT MAKES THE RENDER LANES
POSSIBLE AT ALL. As harvested, `FLIGHTSTATE UT` is 11,336.0878 - the clock at the
very end of the operator's session, more than 9,000 s PAST the predicted impact
at 2,348.55. Every render surface this fixture exists to test is a FORWARD one:
the polyline's forward-arc pass, the seam bridge, the ghost's predicted-tail
playback and the proto orbit line all draw what is AHEAD of the current UT, and
`TimeJump` is FORWARD-ONLY, so no lane could ever reach the tail from there. A
render lane over the harvested clock would assert `runArcs` over an empty forward
window and read green while measuring nothing - the exact vacuity the V18T lesson
is about.

So the builder re-points the clock to `FLIGHTSTATE_UT = 1100.0`, which is chosen
rather than round: it is 21.55 s past the pod chain TIP's last RECORDED sample
(1078.4528) and 1,086.58 s short of the predicted coast's end (2,186.5752), i.e.
INSIDE the first predicted segment. From there the recorded span is behind, the
rest of the coast and the whole ballistic descent to impact are ahead, and the
ghost must be positioned from a predicted conic rather than from a point - which
is the strongest single position for reading all four surfaces at once. It also
reproduces the geometry of the seed's own two render scenes, both of which ran
with the tail in the future (23:12:56 at UT ~190-500 and 23:15:40 at UT ~32).

The pad craft's `lct` / `lastUT` are re-pointed to the same value, so no vessel
claims a launch time in the future; the five asteroids carry `lastUT = -1` and
`lct` values under 323, and the re-flown probe `lct = 188.82`, so all seven are
consistent with the new clock. NOTHING ELSE in FLIGHTSTATE is touched - no orbit,
no situation, no part. `RECORDED_FIXTURES` pins the new clock, and `verify_save`
re-derives the segment relationship rather than just comparing the number, so a
re-harvest whose tail moved cannot leave the clock pointing outside it.

NOTHING IS STRIPPED:
unlike the two free-play Duna subjects this save already holds exactly one
RECORDING_TREE and one MISSION, every one of its ten recordings belongs to that
tree, and its single `RECORDING_SUPERSEDES` ENTRY is LIVE on both sides
(`d096297d` -> `rec_d4b696...`, the probe's own re-fly), so dropping it would
destroy the only supersede topology in the committed corpus.

Everything else here is POST-CONDITIONS, and they carry the weight: `--check`
re-runs every one against the committed bytes and is wired into the suite by
`ReflyARecordedFixtureDriftTests` in `harness/lib/test_build_refly_a_recorded.py`,
so a hand-edit reds locally instead of on a lane's next flight. Like
`build_duna_one_recorded.py` it CANNOT re-run `build` from the source, whose
input is a 12 MB collected log directory that is not committed; the claim it
makes instead is that the two defect shapes above are still exactly what the
bytes say, because a re-harvest against a FIXED DLL would change both and the RF
lanes' subject would silently vanish.

Usage:
    python harness/tools/build_refly_a_recorded.py            # finish in place
    python harness/tools/build_refly_a_recorded.py --check    # verify only

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import Dict, List, Optional, Sequence, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_REPO_ROOT = os.path.dirname(_HARNESS_ROOT)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import build_career_pad_craft as base_builder  # noqa: E402

find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value

TARGET_NAME = "refly-a-recorded"

# The collected session these bytes came from, and the DLL that wrote them.
SOURCE_LOG_NAME = "2026-09-08_2317_refly-a-manual"
SOURCE_SAVE_NAME = "re-fly-a"
SOURCE_DLL_COMMIT = "2effc9d49"

ADDONS_DONOR_NAME = "duna-one-recorded"
ADDONS_REL = os.path.join("AddOns", "DistantObject", "Settings.cfg")
ADDONS_EXPECTED_BYTES = 618

EXPECT_TITLE = "refly-a-recorded (SANDBOX)"
KEEP_TREE_ID = "47cea2fe7d9e4e9eb6a729a7e280cc41"
KEEP_MISSION_ID = "a6862f3f642f4f219f6edca087eca81b"
EXPECT_MISSION_NAME = "Kerbal X With Probe"
EXPECT_ROOT_GROUP = "Kerbal X With Probe"

# The ten recordings, in treeOrder. Spelled out rather than derived so a
# re-harvest whose tree moved reds here instead of shipping a different payload.
KEEP_RECORDING_IDS = (
    "32ca55469ac1400da66fcebb3c791d65",   # 0  THE POD, chain HEAD (the subject)
    "11ac7fa82e4a4d6e9f4b9217fe40af8f",   # 1  ascent debris
    "1e89a803a31b48c5a4521533837db819",   # 2  ascent debris
    "fce975a9bb2a492eb013db97f3fec11e",   # 3  ascent debris
    "213962a5d9c04a47be88d2c0421231ff",   # 4  ascent debris
    "06fc4d2c917549379d8a04f4125dae63",   # 5  ascent debris
    "0fc1a8340b7849b7956b82e2541dfb07",   # 6  ascent debris
    "d096297d815647de988c380fb6931d0a",   # 7  the decoupled probe (SUPERSEDED)
    "8da7c2c2a6d84505b4bc121fdb9f528f",   # 8  THE POD, chain TIP (the subject)
    "rec_d4b696b61b544c1e8013073c611c8477",  # 9 the probe's RE-FLY fork
)

# --- defect (1): the split that did not carry the open bit ----------------
POD_HEAD_ID = "32ca55469ac1400da66fcebb3c791d65"
POD_TIP_ID = "8da7c2c2a6d84505b4bc121fdb9f528f"
POD_CHAIN_ID = "1848a7369eb04ed68a22dd45d4cb11e8"
SPLIT_UT = 191.04000000002392
# HEAD: pre-split remainder. `terminalState = 3` is SubOrbital, re-derived at the
# SECOND scene exit after the split nulled its terminal; `mergeState` is the
# promotion the commit made BEFORE the split ran.
POD_HEAD_FACTS = {
    "chainId": POD_CHAIN_ID, "chainIndex": "0", "pointCount": "272",
    "terminalState": "3", "mergeState": "CommittedProvisional",
    "explicitStartUT": "32.93999999999955",
    "explicitEndUT": "191.04000000002392",
}
# TIP: carries the terminal AND the whole predicted tail, and NO `mergeState`
# key. `RecordingTreeRecordCodec` omits the key exactly when the value is
# `Immutable` and reads a missing key back as `Immutable`, so the ABSENCE below
# is the defect, asserted as an absence on purpose.
POD_TIP_FACTS = {
    "chainId": POD_CHAIN_ID, "chainIndex": "1", "pointCount": "129",
    "terminalState": "4", "mergeState": None,
    "explicitStartUT": "191.04000000002392",
    "explicitEndUT": "2348.5488254909719",
}

# --- defect (2): the predicted tail nothing drew --------------------------
KERBIN_RADIUS_M = 600000.0
# The three top-level ORBIT_SEGMENTs of the TIP, verbatim off the committed
# `.prec.txt`. Segment 0 is the RECORDED checkpoint conic; 1 and 2 are the
# finalizer's predicted coast and its ballistic descent to impact. All three have
# a subsurface periapsis, which is what the polyline's forward-arc pass drops.
POD_TIP_SEGMENTS = (
    {"startUT": 216.70000000003705, "endUT": 1041.7928338768686,
     "sma": 791370.05283977953, "ecc": 0.44124279173795367,
     "body": "Kerbin", "isPredicted": False},
    {"startUT": 1078.0528338768356, "endUT": 2186.5751775686945,
     "sma": 865777.71214532177, "ecc": 0.31755526014312058,
     "body": "Kerbin", "isPredicted": True},
    {"startUT": 2186.5751775686945, "endUT": 2348.5488254909719,
     "sma": 865777.71214532247, "ecc": 0.31755526014312097,
     "body": "Kerbin", "isPredicted": True},
)
# The last RECORDED sample, and the reseeded anchor 0.4 s before it. The tail
# therefore extends the recording by 1270.10 s past the last point with ZERO
# points in that span - the hole the player saw.
POD_TIP_LAST_RECORDED_UT = 1078.4528338768353
PREDICTED_TAIL_START_UT = 1078.0528338768356
PREDICTED_IMPACT_UT = 2348.5488254909719

# --- the clock the render lanes read from (build step 2) -------------------
FLIGHTSTATE_UT = 1100.0
HARVESTED_FLIGHTSTATE_UT = 11336.087830810762
# The vessel whose launch time moves with the clock. It is the save's ACTIVE
# vessel (index 1, PRELAUNCH on the pad), and as harvested its `lct` / `lastUT`
# were the harvested clock exactly, so leaving them would put a launch in the
# future. Matched by NAME rather than by index: an index is a position in a list
# a re-harvest can reorder, and this edit must never land on an asteroid.
CLOCK_VESSEL_NAME = "#autoLOC_501224"

# The probe's re-fly, the corpus's ONLY committed supersede topology.
SUPERSEDE_RELATION_ID = "rsr_06737bc531be4bdc8683d2cc30c886f3"
SUPERSEDE_OLD_ID = "d096297d815647de988c380fb6931d0a"
SUPERSEDE_NEW_ID = "rec_d4b696b61b544c1e8013073c611c8477"

# The three crew are `permanentlyGone` because the pod's PREDICTED impact killed
# them. Pinned because it is a downstream consequence of the extrapolated tail,
# not incidental save state: a re-harvest against a build that stopped stamping
# the predicted Destroyed would have three live kerbals here instead.
EXPECT_KERBAL_SLOT_OWNERS = ("Jebediah Kerman", "Bill Kerman", "Bob Kerman")

# The chain TIP reuses the chain head's `_vessel.craft`, so it carries none of
# its own: 10 x (.prec + .pann + _ghost.craft) + 9 x _vessel.craft = 39.
EXPECT_AUTHORITATIVE_SIDECARS = 39
GAMESTATE_FILE_COUNT = 4

FORBIDDEN_DIR_NAMES = ("_quarantine", "Saves", "RewindPoints", "Backup")
FORBIDDEN_FILE_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")
FORBIDDEN_FILE_PREFIXES = ("quicksave",)


# ---------------------------------------------------------------------------
# File I/O that preserves the harvest's LF line endings.
# ---------------------------------------------------------------------------


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def read_lines(path: str) -> List[str]:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


def parsek_scenario(lines: List[str]) -> Optional[Tuple[int, int]]:
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == "ParsekScenario":
            return node
        i = node[1]


def read_top_level_orbit_segments(text: str) -> List[Dict[str, str]]:
    """Every UNINDENTED `ORBIT_SEGMENT` block of a `.prec.txt`, in file order.

    Top-level only, and by exact column rather than by `strip()`: a segment
    nested inside an OrbitalCheckpoint TrackSection is indented, is a re-clip of
    the same conic, and is NOT what any render surface selects from the
    recording's own `OrbitSegments` list. A depth-blind scan would report four
    here where the render path sees three."""
    lines = text.replace("\r\n", "\n").split("\n")
    out: List[Dict[str, str]] = []
    for i, line in enumerate(lines):
        if line != "ORBIT_SEGMENT" or i + 1 >= len(lines) or lines[i + 1] != "{":
            continue
        block: Dict[str, str] = {}
        j = i + 2
        while j < len(lines) and lines[j] != "}":
            key, _sep, value = lines[j].strip().partition(" = ")
            block[key] = value
            j += 1
        out.append(block)
    return out


def periapsis_radius(sma: float, ecc: float) -> float:
    """The predicate `IsOrbitSegmentBelowSurface` computes, in one place.

    `Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs:3269-3277`:
    `sma * (1 - ecc) < bodyRadius` drops the segment from the forward-arc pass,
    from `ComputeOrbitalCoverIntervals` and from `HasForwardArcCandidate`."""
    return sma * (1.0 - ecc)


# ---------------------------------------------------------------------------
# Post-conditions. Run after the build AND on --check.
# ---------------------------------------------------------------------------


def verify_save(lines: List[str]) -> List[str]:
    """Failure strings for the save half (empty = every post-condition holds)."""
    problems: List[str] = []

    titles = [l.strip() for l in lines if l.strip().startswith("Title = ")]
    if titles[:1] != ["Title = " + EXPECT_TITLE]:
        problems.append("the save Title is %r, expected %r"
                        % (titles[:1], "Title = " + EXPECT_TITLE))

    scn = parsek_scenario(lines)
    if scn is None:
        return problems + ["no ParsekScenario SCENARIO node"]

    trees = child_nodes(lines, scn, "RECORDING_TREE")
    if len(trees) != 1:
        problems.append("expected exactly 1 RECORDING_TREE, found %d" % len(trees))
        return problems
    tree = trees[0]
    if get_value(lines, tree, "id") != KEEP_TREE_ID:
        problems.append("the tree is %r, expected %r"
                        % (get_value(lines, tree, "id"), KEEP_TREE_ID))
    if get_value(lines, tree, "autoGeneratedRootGroupName") != EXPECT_ROOT_GROUP:
        problems.append("the tree's root group is %r, expected %r"
                        % (get_value(lines, tree, "autoGeneratedRootGroupName"),
                           EXPECT_ROOT_GROUP))

    recordings = child_nodes(lines, tree, "RECORDING")
    got = [get_value(lines, r, "recordingId") for r in recordings]
    if got != list(KEEP_RECORDING_IDS):
        problems.append("tree recordings are %r, expected the 10 seed ids %r"
                        % (got, list(KEEP_RECORDING_IDS)))

    by_id = {get_value(lines, r, "recordingId"): r for r in recordings}
    problems += _verify_split_pair(lines, by_id)

    missions = child_nodes(lines, scn, "MISSION")
    if len(missions) != 1:
        problems.append("expected exactly 1 MISSION, found %d" % len(missions))
    else:
        for key, expected in (("id", KEEP_MISSION_ID),
                              ("treeId", KEEP_TREE_ID),
                              ("name", EXPECT_MISSION_NAME)):
            value = get_value(lines, missions[0], key)
            if value != expected:
                problems.append("MISSION %s is %r, expected %r"
                                % (key, value, expected))

    problems += _verify_supersede(lines, scn)

    # THE REWIND POINT IS GONE, AND THE FIXTURE MUST SAY SO. The reaper deleted
    # it before the session was collected, so a lane that needs one cannot use
    # these bytes; an unexplained REWIND_POINTS node appearing here would mean
    # the save is no longer the defect's own product.
    for name in ("REWIND_POINTS", "REWIND_RETIREMENTS", "LEDGER_TOMBSTONES"):
        if child_nodes(lines, scn, name):
            problems.append(
                "ParsekScenario carries a %s node: this fixture is the "
                "REAPED-RP subject and cannot grow one without a re-harvest"
                % name)

    slots = child_nodes(lines, scn, "KERBAL_SLOTS")
    if len(slots) != 1:
        problems.append("expected exactly 1 KERBAL_SLOTS node, found %d" % len(slots))
    else:
        rows = child_nodes(lines, slots[0], "SLOT")
        owners = tuple(get_value(lines, s, "owner") for s in rows)
        if owners != EXPECT_KERBAL_SLOT_OWNERS:
            problems.append("KERBAL_SLOTS owners are %r, expected %r"
                            % (owners, EXPECT_KERBAL_SLOT_OWNERS))
        gone = [get_value(lines, s, "permanentlyGone") for s in rows]
        if set(gone) != {"True"}:
            problems.append(
                "KERBAL_SLOTS permanentlyGone flags are %r, expected all True - "
                "the crew died in the pod's PREDICTED impact, which is a "
                "consequence of the extrapolated tail this fixture exists for"
                % (gone,))

    gens = set()
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("recordingSchemaGeneration = "):
            gens.add(stripped.split("=", 1)[1].strip())
    if gens != {"4"}:
        problems.append("recordingSchemaGeneration values are %r, expected {'4'}"
                        % (sorted(gens),))

    dangling = [i for i, line in enumerate(lines, 1)
                if line.strip().startswith("rewindSave = parsek_rw_")]
    if dangling:
        problems.append("a rewindSave = parsek_rw_* hint survived at line(s) %s"
                        % (dangling,))

    problems += _verify_clock(lines)
    return problems


def _verify_clock(lines: List[str]) -> List[str]:
    """BUILD STEP 2, re-derived rather than compared.

    A cell that only checked `UT == 1100.0` would still pass on a re-harvest whose
    predicted tail had moved elsewhere, leaving the clock pointing at nothing and
    every render lane vacuous. So the assertion is the RELATIONSHIP: the clock must
    sit strictly inside the first predicted segment - past the last recorded sample,
    short of that segment's end - which is the property the lanes actually need.
    """
    problems: List[str] = []
    _lines, edits = repoint_clock(lines)
    if edits:
        problems.append(
            "the committed save's clock is not the re-pointed one (%d line(s) "
            "would change): run build_refly_a_recorded.py" % edits)
    if not (POD_TIP_LAST_RECORDED_UT < FLIGHTSTATE_UT
            < POD_TIP_SEGMENTS[1]["endUT"]):
        problems.append(
            "the clock %r does not sit inside the first predicted segment "
            "(%r, %r]: every forward render surface would have an empty window "
            "and the render lanes would assert nothing"
            % (FLIGHTSTATE_UT, POD_TIP_LAST_RECORDED_UT,
               POD_TIP_SEGMENTS[1]["endUT"]))
    if FLIGHTSTATE_UT >= PREDICTED_IMPACT_UT:
        problems.append("the clock %r is at or past the predicted impact %r"
                        % (FLIGHTSTATE_UT, PREDICTED_IMPACT_UT))
    return problems


def _verify_split_pair(lines: List[str], by_id) -> List[str]:
    """DEFECT (1), asserted rather than described.

    The HEAD keeps the promotion and the TIP keeps the terminal, and the TIP
    carries no `mergeState` key at all. Asserting the ABSENCE is the whole point:
    the codec omits the key exactly when the value is `Immutable`, so a key
    appearing here means the bytes came from a build that carries the open bit
    across a split - at which moment RF-1's forbid clause has no subject and the
    lane must be re-flown, not re-pinned."""
    problems: List[str] = []
    for rid, facts in ((POD_HEAD_ID, POD_HEAD_FACTS), (POD_TIP_ID, POD_TIP_FACTS)):
        node = by_id.get(rid)
        if node is None:
            problems.append("the split pair member %s is missing" % rid)
            continue
        for key, expected in sorted(facts.items()):
            value = get_value(lines, node, key)
            if value != expected:
                problems.append(
                    "%s %s is %r, expected %r%s"
                    % (rid, key, value, expected,
                       " (a mergeState key on the TIP means the split now "
                       "carries the open bit and this fixture is no longer the "
                       "continuation defect's subject)"
                       if key == "mergeState" and expected is None else ""))
    return problems


def _verify_supersede(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """The one supersede row, LIVE on both sides.

    It is the only supersede topology in the committed corpus, and both ids name
    recordings this fixture keeps - so unlike the orphan row the Duna strip
    dropped, this one must survive."""
    problems: List[str] = []
    nodes = child_nodes(lines, scn, "RECORDING_SUPERSEDES")
    if len(nodes) != 1:
        return ["expected exactly 1 RECORDING_SUPERSEDES node, found %d" % len(nodes)]
    entries = child_nodes(lines, nodes[0], "ENTRY")
    if len(entries) != 1:
        return ["expected exactly 1 RECORDING_SUPERSEDES ENTRY, found %d"
                % len(entries)]
    for key, expected in (("relationId", SUPERSEDE_RELATION_ID),
                          ("oldRecordingId", SUPERSEDE_OLD_ID),
                          ("newRecordingId", SUPERSEDE_NEW_ID)):
        value = get_value(lines, entries[0], key)
        if value != expected:
            problems.append("the supersede ENTRY's %s is %r, expected %r"
                            % (key, value, expected))
    kept = set(KEEP_RECORDING_IDS)
    for rid in (SUPERSEDE_OLD_ID, SUPERSEDE_NEW_ID):
        if rid not in kept:
            problems.append("the supersede row names %s, which the fixture does "
                            "not carry: a dangling relation" % rid)
    return problems


def verify_tree(fixture_dir: str) -> List[str]:
    """Failure strings for the FILE-TREE half."""
    problems: List[str] = []

    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")
    if not os.path.isdir(recordings):
        return ["fixture carries no Parsek/Recordings directory"]

    files = [n for n in sorted(os.listdir(recordings))
             if os.path.isfile(os.path.join(recordings, n))]
    stems = sorted({_stem(n) for n in files})
    if stems != sorted(KEEP_RECORDING_IDS):
        problems.append("sidecar families on disk are %r, expected the 10 kept ids"
                        % (stems,))

    for rid in KEEP_RECORDING_IDS:
        for suffix in (".prec", ".prec.txt", ".pann"):
            path = os.path.join(recordings, rid + suffix)
            if not os.path.isfile(path) or os.path.getsize(path) == 0:
                problems.append("%s%s missing or empty" % (rid, suffix))

    authoritative = [n for n in files if not n.endswith(".txt")]
    if len(authoritative) != EXPECT_AUTHORITATIVE_SIDECARS:
        problems.append("Parsek/Recordings carries %d authoritative sidecar(s), "
                        "expected %d" % (len(authoritative),
                                         EXPECT_AUTHORITATIVE_SIDECARS))
    # The chain TIP reuses the chain head's craft; every OTHER recording carries
    # its own. Asserted as a pair so a missing craft elsewhere cannot hide behind
    # the exemption.
    tip_craft = os.path.join(recordings, POD_TIP_ID + "_vessel.craft")
    if os.path.isfile(tip_craft):
        problems.append("%s carries a _vessel.craft: it is chainIndex 1 and must "
                        "reuse the chain head's" % POD_TIP_ID)
    for rid in KEEP_RECORDING_IDS:
        if rid == POD_TIP_ID:
            continue
        if not os.path.isfile(os.path.join(recordings, rid + "_vessel.craft")):
            problems.append("%s_vessel.craft missing" % rid)

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
    elif len(os.listdir(gamestate)) != GAMESTATE_FILE_COUNT:
        problems.append("Parsek/GameState carries %d file(s), expected %d"
                        % (len(os.listdir(gamestate)), GAMESTATE_FILE_COUNT))

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


def _stem(name: str) -> str:
    for suffix in ("_vessel.craft", "_ghost.craft"):
        if name.endswith(suffix):
            return name[:-len(suffix)]
    if name.endswith(".prec.txt"):
        return name[:-len(".prec.txt")]
    return name.rsplit(".", 1)[0]


def verify_prec(fixture_dir: str) -> List[str]:
    """DEFECT (2), asserted off the committed sidecars.

    Three claims, each of which a re-harvest against a fixed DLL would break, so
    the RF-7 lanes lose their subject LOUDLY rather than reading green over a
    fixture that no longer carries the hole:

      * the TIP carries exactly the three top-level ORBIT_SEGMENTs quoted above,
        two of them `isPredicted`;
      * EVERY one of the three has a subsurface periapsis, which is the geometric
        trigger `IsOrbitSegmentBelowSurface` fires on;
      * the HEAD - the recording map-presence source resolution reads - carries
        ZERO segments, which is why `hasOrbitSegments=False`.
    """
    problems: List[str] = []
    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")

    head_txt = os.path.join(recordings, POD_HEAD_ID + ".prec.txt")
    tip_txt = os.path.join(recordings, POD_TIP_ID + ".prec.txt")
    for path in (head_txt, tip_txt):
        if not os.path.isfile(path):
            return ["%s is missing" % os.path.basename(path)]

    with open(head_txt, "r", encoding="utf-8", errors="replace", newline="") as fh:
        head_segments = read_top_level_orbit_segments(fh.read())
    if head_segments:
        problems.append(
            "the chain HEAD %s carries %d top-level ORBIT_SEGMENT(s), expected 0 "
            "- its emptiness is what makes map-presence read hasOrbitSegments="
            "False" % (POD_HEAD_ID, len(head_segments)))

    with open(tip_txt, "r", encoding="utf-8", errors="replace", newline="") as fh:
        segments = read_top_level_orbit_segments(fh.read())
    if len(segments) != len(POD_TIP_SEGMENTS):
        return problems + [
            "the chain TIP %s carries %d top-level ORBIT_SEGMENT(s), expected %d"
            % (POD_TIP_ID, len(segments), len(POD_TIP_SEGMENTS))]

    for index, (got, want) in enumerate(zip(segments, POD_TIP_SEGMENTS)):
        for key in ("startUT", "endUT", "sma", "ecc"):
            if float(got.get(key, "nan")) != want[key]:
                problems.append("TIP segment %d %s is %r, expected %r"
                                % (index, key, got.get(key), want[key]))
        if got.get("body") != want["body"]:
            problems.append("TIP segment %d body is %r, expected %r"
                            % (index, got.get("body"), want["body"]))
        if (got.get("isPredicted") == "True") != want["isPredicted"]:
            problems.append("TIP segment %d isPredicted is %r, expected %r"
                            % (index, got.get("isPredicted"), want["isPredicted"]))
        rp = periapsis_radius(float(got.get("sma", "nan")),
                              float(got.get("ecc", "nan")))
        if not rp < KERBIN_RADIUS_M:
            problems.append(
                "TIP segment %d has periapsis radius %.3f m, which CLEARS "
                "Kerbin's %.0f m: it would be drawn as a forward arc and the "
                "render defect this fixture carries would be gone"
                % (index, rp, KERBIN_RADIUS_M))

    predicted = [s for s in segments if s.get("isPredicted") == "True"]
    if len(predicted) != 2:
        problems.append("the TIP carries %d predicted segment(s), expected 2"
                        % len(predicted))
    else:
        if float(predicted[0]["startUT"]) != PREDICTED_TAIL_START_UT:
            problems.append("the predicted tail starts at %r, expected %r"
                            % (predicted[0]["startUT"], PREDICTED_TAIL_START_UT))
        if float(predicted[-1]["endUT"]) != PREDICTED_IMPACT_UT:
            problems.append("the predicted tail ends at %r, expected the impact "
                            "UT %r" % (predicted[-1]["endUT"], PREDICTED_IMPACT_UT))
    return problems


# ---------------------------------------------------------------------------


def repoint_clock(lines: List[str]) -> Tuple[List[str], int]:
    """Set FLIGHTSTATE UT to `FLIGHTSTATE_UT` and move the pad craft's launch time
    with it. Pure over the line list; returns (lines, edits).

    IDEMPOTENT: re-running over an already-repointed save rewrites the same values
    and reports zero edits, so `build` can be run twice without drifting.

    THE SCAN IS SCOPED TO FLIGHTSTATE, and by brace depth rather than by regex over
    the whole file: `UT = ` is a common value name (several SCENARIO nodes carry
    one) and a file-wide first-match replace would silently stamp the wrong node.
    """
    out = list(lines)
    edits = 0
    start = next((i for i, l in enumerate(out) if l.strip() == "FLIGHTSTATE"), None)
    if start is None:
        raise SystemExit("the save has no FLIGHTSTATE node")
    depth, end = 0, len(out)
    for i in range(start + 1, len(out)):
        stripped = out[i].strip()
        if stripped == "{":
            depth += 1
        elif stripped == "}":
            depth -= 1
            if depth == 0:
                end = i
                break

    want_ut = repr(FLIGHTSTATE_UT)
    for i in range(start, end):
        stripped = out[i].strip()
        if stripped.startswith("UT = ") and out[i].count("\t") <= 3:
            if stripped != "UT = " + want_ut:
                out[i] = out[i][:out[i].index("UT = ")] + "UT = " + want_ut
                edits += 1
            break
    else:
        raise SystemExit("FLIGHTSTATE carries no UT line")

    # The pad craft's launch time, matched by NAME inside FLIGHTSTATE.
    vessel_starts = [i for i in range(start, end) if out[i].strip() == "VESSEL"]
    for index, vstart in enumerate(vessel_starts):
        vend = vessel_starts[index + 1] if index + 1 < len(vessel_starts) else end
        names = [out[i].strip() for i in range(vstart, vend)
                 if out[i].strip().startswith("name = ")]
        if not names or names[0] != "name = " + CLOCK_VESSEL_NAME:
            continue
        for i in range(vstart, vend):
            stripped = out[i].strip()
            for key in ("lct = ", "lastUT = "):
                if stripped.startswith(key) and stripped != key + want_ut:
                    out[i] = out[i][:out[i].index(key)] + key + want_ut
                    edits += 1
        return out, edits
    raise SystemExit("FLIGHTSTATE carries no VESSEL named %r" % CLOCK_VESSEL_NAME)


def build(fixture_dir: str) -> List[str]:
    """Two build steps: the AddOns restore, and the clock re-point."""
    donor = os.path.join(_SAVES, ADDONS_DONOR_NAME, ADDONS_REL)
    if not os.path.isfile(donor):
        return ["AddOns donor %s is missing" % donor]
    destination = os.path.join(fixture_dir, ADDONS_REL)
    os.makedirs(os.path.dirname(destination), exist_ok=True)
    shutil.copy2(donor, destination)
    print("restored %s from %s (%d bytes)"
          % (ADDONS_REL.replace("\\", "/"), ADDONS_DONOR_NAME,
             os.path.getsize(destination)))

    sfs = os.path.join(fixture_dir, "persistent.sfs")
    lines, edits = repoint_clock(read_lines(sfs))
    if edits:
        with open(sfs, "w", encoding="utf-8", newline="") as fh:
            fh.write("\n".join(lines))
    print("clock re-pointed to UT %r (%d line(s) edited; harvested %r)"
          % (FLIGHTSTATE_UT, edits, HARVESTED_FLIGHTSTATE_UT))
    return []


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of building it")
    parser.add_argument("--target-name", default=TARGET_NAME)
    args = parser.parse_args(argv)

    fixture_dir = os.path.join(_SAVES, args.target_name)
    sfs = os.path.join(fixture_dir, "persistent.sfs")
    if not os.path.isfile(sfs):
        print("FAIL: %s does not exist (run harvest_bdock_station.py first)" % sfs)
        return 1

    problems: List[str] = []
    if not args.check:
        problems += build(fixture_dir)

    problems += verify_save(read_lines(sfs))
    problems += verify_tree(fixture_dir)
    problems += verify_prec(fixture_dir)
    for p in problems:
        print("FAIL: %s" % p)
    if problems:
        return 1
    print("OK: %s satisfies every post-condition" % args.target_name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
