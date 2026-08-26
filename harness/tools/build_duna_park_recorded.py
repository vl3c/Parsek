#!/usr/bin/env python3
"""Strip the harvested `duna-park-recorded` fixture down to the parking mission.

WHAT THIS SUBJECT IS, AND WHY IT IS NOT `duna-one-recorded`. Both fixtures come
out of the SAME operator save (`logs/2026-08-25_1537_s15-duna-one-manifest-run2`,
save `s15`), which carries four unrelated `RECORDING_TREE`s, and both are Duna
missions - but they are two DIFFERENT WAYS OF GETTING TO DUNA, and the render
lanes need both:

  `duna-one-recorded` keeps tree `1ccdb192...`, a DIRECT transfer. It parks in
  KERBIN orbit (six consecutive Kerbin ORBIT_SEGMENTs at sma 731,229.576,
  ecc 0.00131), ejects, and its three Sun segments are ONE transfer conic split
  by warp (sma 17,604,964,389.77 throughout). The departure burn happens inside
  Kerbin's SOI; the vessel is never in a heliocentric PARKING orbit.

  THIS ONE keeps tree `ced78481...` ("Kerbal X #2"), a HELIOCENTRIC PARKING
  DEPARTURE - the operator's description was "orbits the star until alignment is
  good". Its main transfer `aa48920e...` (856 points, the largest recording in
  the save) ejects from Kerbin almost immediately and then COASTS ON A SUN ORBIT
  for 13,502,219.93 s (about 156 Kerbin days) across THREE consecutive segments
  whose elements are identical to ten significant figures:

    seg 2  Sun  sma 14,072,049,898.090191  ecc 0.0326934153364881
           [2,547,568,544.056056 -> 2,560,670,336.8959155]
    seg 3  Sun  sma 14,072,049,898.089064  ecc 0.032693415336629207
           [2,560,670,342.59591  -> 2,560,985,257.3773251]
    seg 4  Sun  sma 14,072,049,898.090006  ecc 0.03269341533672783
           [2,560,985,293.2372909 -> 2,561,070,763.99165]

  That park orbit sits 3.5% outside Kerbin's own heliocentric sma
  (13,599,840,256 m), which is what makes it a PHASING orbit rather than a
  transfer. THE DEPARTURE BURN IS THEN VISIBLE AS AN ELEMENT STEP at
  2,561,070,900.03: seg 5 jumps to sma 17,908,765,008.46 / ecc 0.19216439941
  (+27% sma, +488% ecc). Duna SOI entry follows at 2,570,454,935.62 (segs 6-10,
  hyperbolic, ecc 3.6025), and capture into a Duna ellipse at 2,570,492,255.34
  (seg 11, sma 495,883.11, ecc 0.042042). It reaches Duna and stays.

  THE THIRD CANDIDATE WAS RULED OUT MECHANICALLY, not by name: tree `7f01f8b9...`
  (also "Kerbal X") carries NO Sun ORBIT_SEGMENT at all - its two chains run
  Kerbin -> Mun -> Kerbin. It never leaves the Kerbin system.

PROVENANCE CLASS. Free-play harvest, exactly like `duna-one-recorded`: the
operator's own hand-played session rather than a driven harness run. See that
tool's header for why that class exists and what it buys.

NEVER NAME THE FIXTURE `s15`. Staging rmtree's the same-named save inside the
automation instance, so a fixture called `s15` would delete the operator's
hand-played save the first time any scenario staged it.

INPUT. The output of

    python harness/tools/harvest_bdock_station.py \\
        --save-dir <log-copy>/saves/s15 --target-name duna-park-recorded \\
        --expect-situation PRELAUNCH --keep-parsek

i.e. this tool edits `harness/fixtures/saves/duna-park-recorded` IN PLACE. The
harvest already did the generic half (title normalisation, the four `rewindSave`
hint clears, `Parsek/Saves`, `.craft.txt` snapshot mirrors, `_quarantine`);
everything below is Kerbal-X-#2-specific.

WHAT IT DOES, in order:

  1. THE SAVE. Keeps `RECORDING_TREE ced78481...` (14 recordings) and its
     `MISSION 6fa271de...`; deletes the other three trees, their three MISSION
     rows, and the single `RECORDING_SUPERSEDES` ENTRY (both of whose ids belong
     to the dropped `7f01f8b9...` tree, so the row would be an orphan the
     LoadTimeSweep warns about).
  2. THE SIDECARS. Keeps the 14 kept recordings' sidecar families; deletes the
     rest.
  3. THE KERBALS. `KERBAL_SLOTS` / `CREW_REPLACEMENTS` are pruned to MERGER's
     slot and entry alone - the kept EVA recording `2f724cbd...` carries
     `evaCrewName = Merger Kerman`, and the other four slots reserve crew for
     trees that no longer exist. The stand-in `Vergel Kerman` must survive in
     `ROSTER`, which `verify` asserts: a CREW_REPLACEMENTS entry naming a kerbal
     the roster does not carry is a dangling reference. GROUP_HIERARCHY IS KEPT
     WHOLE, deliberately, for the reason `build_duna_one_recorded.py` gives: it
     is name-keyed UI state, an entry for a group nothing references is inert,
     and pruning it would couple this recipe to the auto-group naming rules for
     no gain.
  4. THE TWO DIRECTORIES THE LOG COPY DID NOT CARRY. `collect-logs.py` copies the
     save's `Parsek/` to a SIBLING `parsek/` directory and leaves only
     `Parsek/Recordings` under `saves/<name>/`, so the harvest produced a fixture
     with no `Parsek/GameState/`; and the operator's dev-instance save carries no
     `AddOns/` at all. GameState (all 9 files) is restored from the log copy's
     sibling; `AddOns/DistantObject/Settings.cfg` is copied from a sibling
     COMMITTED fixture (618 bytes, byte-identical across all the fixtures that
     carry that variant - `verify` re-checks the size and the donor's bytes).

  5. THE INV2 REPAIR - see `INV2_REPAIR_RECORDING_ID` below for the whole story.
     It is here because the gate SAID SO, not on principle: the analyzer Forbid
     gate was run FIRST on the freshly stripped bytes and read
     `FAIL=4 WARN=16 RED=1`, four `INV2-NO-DOUBLE-COVER` FAILs on the park
     transfer. The sixteen WARNs are INV8 phantom-attribution from the restored
     `ledger.pgld`, which still records the whole free-play career; they are the
     same class `duna-one-recorded` carries fifteen of, and they do not red.

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture and
writes nothing. It is WIRED, not decorative: `DunaParkRecordedFixtureDriftTests`
in `harness/lib/test_build_duna_park_recorded.py` runs the same `verify_*`
functions in-process, so a hand-edit of the committed bytes reds in the harness
suite. Like its sibling it CANNOT re-run `build`: the input is a 24 MB collected
log directory of a hand-played save that is not committed and never will be. The
byte-stability claim it makes instead is BROADER than the sibling's, which checks
one recording: re-running the containment dedupe over EVERY kept `.prec` must
drop nothing, and every section list must be overlap-free in the exact sense
`Inv2NoDoubleCover` means.

Usage:
    python harness/tools/build_duna_park_recorded.py            # strip in place
    python harness/tools/build_duna_park_recorded.py --check    # verify only

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_REPO_ROOT = os.path.dirname(_HARNESS_ROOT)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# The node helpers, the ConfigNode-text file I/O, the generic node-drop passes
# and the `.prec` reader are all IMPORTED from the sibling recipe rather than
# copied. Everything imported here is generic over its arguments (the two
# `_drop_*` passes take the node name, key and values; the reader takes bytes);
# only the constants and the ordering below are this subject's.
import build_duna_one_recorded as sibling  # noqa: E402

read_lines = sibling.read_lines
write_lines = sibling.write_lines
parsek_scenario = sibling.parsek_scenario
find_node = sibling.find_node
child_nodes = sibling.child_nodes
get_value = sibling.get_value
_drop_child_nodes = sibling._drop_child_nodes
_drop_grandchild_nodes = sibling._drop_grandchild_nodes
read_prec_sections = sibling.read_prec_sections
overlapping_pairs = sibling.overlapping_pairs
find_redundant_sections = sibling.find_redundant_sections
text_section_spans = sibling.text_section_spans
_read_bytes = sibling._read_bytes

TARGET_NAME = "duna-park-recorded"

# The AddOns donor. Any of the fixtures carrying the 618-byte variant would do;
# a RECORDED sibling is named so the donor is a fixture of the same class.
ADDONS_DONOR_NAME = "ike-orbit-recorded"
ADDONS_REL = os.path.join("AddOns", "DistantObject", "Settings.cfg")
ADDONS_EXPECTED_BYTES = 618

# The collected log the fixture was harvested from. Only `Parsek/GameState/` is
# read from it (step 4); everything else already came through the harvest.
DEFAULT_SOURCE_LOG = os.path.join(
    os.path.dirname(_REPO_ROOT), "logs",
    "2026-08-25_1537_s15-duna-one-manifest-run2")
GAMESTATE_FILE_COUNT = 9

# --- what survives -------------------------------------------------------
KEEP_TREE_ID = "ced7848157674b8ea19311377c0f6fbc"
DROP_TREE_IDS = (
    "90a6f987fff74837a32ae5b3fe5e28da",   # Jumping Flea
    "7f01f8b9a57e4788bf9d9d009c90ed10",   # Kerbal X (Mun; the re-flown one)
    "1ccdb19215034ac19f3a8e31697b05ed",   # Duna One (the DIRECT transfer)
)
KEEP_MISSION_ID = "6fa271def0a549eb8375ddbf445b1344"
EXPECT_MISSION_NAME = "Kerbal X #2"
EXPECT_ROOT_GROUP = "Kerbal X #2"
EXPECT_LOOP_PLAYBACK = "False"

# The 14 recordings of the Kerbal X #2 tree, in treeOrder. Spelled out rather
# than derived from the save so that a save whose tree changed reds loudly here
# instead of silently shipping a different payload.
KEEP_RECORDING_IDS = (
    "8538d9e156a34af194083c6068739888",   # 0  chain 0, the launch
    "8884af4a14f84256b73704a97a2e5476",   # 1  debris
    "8eabd16b458f4317a24e2e1adc6cec9c",   # 2  debris
    "f475897c8f4a4523835b3620197b5af2",   # 3  debris
    "d154c46d7d884bb582559e18fb55e730",   # 4  debris
    "df7ebece093b4c828f9a41c25e18927c",   # 5  debris
    "5f7e2b8768a642deb919163c8d2ed8ee",   # 6  debris
    "ed76396d1c8e42a8b4e9c7a39f868063",   # 7  the decoupled Kerbal X Probe
    "9f68333fa762479e81bfe7d1827d4afd",   # 8  debris shed at Duna
    "5466febaeffa4042ba211c0e1fe88b91",   # 9  the landed Kerbal X
    "2f724cbd0030407f884e125e89f0add0",   # 10 Merger's EVA on Duna
    "aa48920e43fb4bf483940e0d8191a1ce",   # 11 chain 1, THE PARK + TRANSFER
    "acf1435a6df24b2aa2abbc6da88d6b36",   # 12 chain 2, the descent
    "c5d0148a530348c9a5effad16021b836",   # 13 chain 3, the landing
)

# The chain the mission flies, and its parking-departure member. Pinned so a
# re-harvest that swapped in a different flight with the same topology cannot
# pass every cell in the suite.
CHAIN_ID = "1d63a7a6cd86466389b748bf8f092f42"
PARK_TRANSFER_RECORDING_ID = "aa48920e43fb4bf483940e0d8191a1ce"

# --- the park signature, measured off the committed `.prec.txt` ----------
#
# THE FIXTURE'S REASON TO EXIST, made mechanical. `verify_park_signature` reads
# the transfer recording's top-level ORBIT_SEGMENT list and asserts the three
# facts the header argues: a run of consecutive SUN segments whose sma agree to
# PARK_SMA_REL_TOLERANCE, a departure step out of that run, and a Duna endpoint.
# Without this the one property that distinguishes this subject from
# `duna-one-recorded` would be unpinned anywhere.
PARK_BODY = "Sun"
PARK_SEGMENT_INDICES = (2, 3, 4)
PARK_SMA = 14072049898.090191
# The three park segments agree to ~8e-14 relative; 1e-9 is four orders of
# magnitude looser than the observed spread and still three orders TIGHTER than
# the departure step it has to separate from (0.27 relative).
PARK_SMA_REL_TOLERANCE = 1e-9
PARK_START_UT = 2547568544.056056
PARK_END_UT = 2561070763.99165
DEPARTURE_SEGMENT_INDEX = 5
DEPARTURE_SMA = 17908765008.460636
DEPARTURE_START_UT = 2561070900.0315204
DUNA_SOI_ENTRY_UT = 2570454935.6223264
EXPECT_SEGMENT_COUNT = 14
# Body roster of the 14 top-level segments, in order.
EXPECT_SEGMENT_BODIES = (
    "Kerbin", "Kerbin", "Sun", "Sun", "Sun", "Sun",
    "Duna", "Duna", "Duna", "Duna", "Duna", "Duna", "Duna", "Duna",
)

# The kerbal whose reservation survives. Her stand-in flies the kept EVA
# recording 2f724cbd (`evaCrewName = Merger Kerman`); the other four slots
# reserved crew for dropped trees.
KEEP_KERBAL_OWNER = "Merger Kerman"
KEEP_KERBAL_STANDIN = "Vergel Kerman"
EVA_RECORDING_ID = "2f724cbd0030407f884e125e89f0add0"

# Harvest exhaust and derived data no fixture may carry. Re-asserted here rather
# than left to the harness cells alone so `--check` is a complete statement about
# these bytes on its own.
FORBIDDEN_DIR_NAMES = ("_quarantine", "Saves", "RewindPoints", "Backup")
FORBIDDEN_FILE_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")
FORBIDDEN_FILE_PREFIXES = ("quicksave",)

# Chain CONTINUATIONS reuse the chain head's `_vessel.craft` rather than
# carrying one of their own, so the per-family completeness check is stated as
# a floor plus these exemptions rather than "every family has all four".
NO_VESSEL_CRAFT_RECORDING_IDS = (
    "aa48920e43fb4bf483940e0d8191a1ce",   # chainIndex 1
    "acf1435a6df24b2aa2abbc6da88d6b36",   # chainIndex 2
)
# NOTE that `c5d0148a...` (chainIndex 3) is NOT exempt: it carries its own
# `_vessel.craft` despite being a continuation, so the exemption is a MEASURED
# list rather than "every recording with a chainIndex above zero".
NO_GHOST_CRAFT_RECORDING_IDS = ()
# 14 x 4 - the two missing `_vessel.craft` above.
EXPECTED_AUTHORITATIVE_SIDECARS = 54

# --- the INV2 repair -----------------------------------------------------
#
# THE DEFECT, as the gate reported it before anything was written: four
# `INV2-NO-DOUBLE-COVER` FAILs, all on the park transfer `aa48920e...`, which
# carries 52 TrackSections. Each dropped section is a duplicate of coverage a
# neighbour already owns, and each falls into one of the two shapes
# `build_duna_one_recorded.py` documents:
#
#   idx 1   [2547277750.9906116, 2547280707.4508519]  a frame-less 65-byte shell
#           with NO ORBIT_SEGMENT, a strict prefix of idx 2's span.
#   idx 3   [2547280707.4508519, 2547281403.3664637]  a RE-CLIP of idx 2's conic:
#           same inc / ecc / sma / lan / argPe / mna / body / ofr*, and the SAME
#           `epoch = 2547277750.9906116`. Contained in 2.
#   idx 12  [2547568544.056056,  2560670336.8959155]  the KERBIN->SUN SOI SEAM:
#           a `ref=0` shell that is an EXACT-span duplicate of idx 13, the
#           `ref=2 src=2` OrbitalCheckpoint carrying the PARK segment itself.
#   idx 30  [2570454935.6223264, 2570490859.060472]   the SUN->DUNA SOI SEAM:
#           the same `ref=0` shell shape beside idx 31's checkpoint.
#
# TWO OF THE FOUR SIT EXACTLY ON SOI SEAMS, which is not a coincidence - see
# todo-and-known-bugs.md -> RECORDER-SUSPECTED-DOUBLE-EMIT-AT-SOI-SEAM. It is
# also why the repair is safe HERE specifically: the sections it KEEPS at those
# two seams (13 and 31) are the ones carrying the park segment and the Duna
# arrival hyperbola, i.e. the fixture's whole signature.
#
# THE TOP-LEVEL ORBIT_SEGMENT LIST IS NOT TOUCHED. The splice removes whole
# TrackSection records and decrements the section count; the recording's own
# 14-entry segment list - the surface `verify_park_signature` reads - is a
# separate structure earlier in the file and every byte of it survives.
INV2_REPAIR_RECORDING_ID = PARK_TRANSFER_RECORDING_ID
INV2_DROPPED_SECTION_INDICES = (1, 3, 12, 30)
INV2_EXPECTED_SECTIONS_BEFORE = 52
INV2_EXPECTED_SECTIONS_AFTER = 48


def _stem(name: str) -> str:
    """The recording id a sidecar file belongs to.

    `.txt` IS STRIPPED FIRST, unlike the sibling recipe's copy: without it
    `X_ghost.craft.txt` falls through to `name.split('.')[0]` and reads as a
    family called `X_ghost`. The harvest prunes those mirrors, so the sibling
    never sees one - but a stem function that is only correct because of what its
    caller happens to have deleted is a trap, and it has already cost one
    phantom orphan sweep in a harvest plan."""
    if name.endswith(".txt"):
        name = name[:-len(".txt")]
    for suffix in ("_vessel.craft", "_ghost.craft"):
        if name.endswith(suffix):
            return name[:-len(suffix)]
    return name.split(".")[0]


# ---------------------------------------------------------------------------
# Step 1 + 3: the save edits.
# ---------------------------------------------------------------------------


def strip_save(lines: List[str]) -> List[str]:
    """Return the save with everything but the Kerbal X #2 mission removed.

    Pure over the input line list. Every node it expects is ASSERTED before the
    cut, so a re-harvest whose shape moved reds naming what moved rather than
    quietly producing a fixture with the wrong payload."""
    out = list(lines)

    scn = parsek_scenario(out)
    if scn is None:
        raise SystemExit("harvested save has no ParsekScenario SCENARIO node")

    # --- trees -----------------------------------------------------------
    trees = child_nodes(out, scn, "RECORDING_TREE")
    ids = [get_value(out, t, "id") for t in trees]
    want = {KEEP_TREE_ID} | set(DROP_TREE_IDS)
    if set(ids) != want:
        raise SystemExit(
            "harvested save carries trees %r, expected exactly %r - the source "
            "save moved and this recipe must be re-derived"
            % (sorted(ids), sorted(want)))
    out = _drop_child_nodes(out, "RECORDING_TREE", "id", DROP_TREE_IDS)

    # --- missions --------------------------------------------------------
    scn = parsek_scenario(out)
    missions = child_nodes(out, scn, "MISSION")
    mission_trees = {get_value(out, m, "treeId"): get_value(out, m, "id")
                     for m in missions}
    if mission_trees.get(KEEP_TREE_ID) != KEEP_MISSION_ID:
        raise SystemExit(
            "the kept tree's MISSION id is %r, expected %r"
            % (mission_trees.get(KEEP_TREE_ID), KEEP_MISSION_ID))
    drop_mission_ids = tuple(mid for tid, mid in sorted(mission_trees.items())
                             if tid != KEEP_TREE_ID)
    out = _drop_child_nodes(out, "MISSION", "id", drop_mission_ids)

    # --- the orphan supersede row ---------------------------------------
    #
    # Dropped WHOLE rather than emptied: the one ENTRY names
    # oldRecordingId=edbf05b2... / newRecordingId=rec_85fda1cd..., both of which
    # belong to the dropped Mun-mission Kerbal X tree, so after the tree drop it
    # resolves nothing on either side. `LoadTimeSweep` warn-logs exactly that
    # shape.
    scn = parsek_scenario(out)
    supersedes = child_nodes(out, scn, "RECORDING_SUPERSEDES")
    if len(supersedes) > 1:
        raise SystemExit("expected at most one RECORDING_SUPERSEDES node, got %d"
                         % len(supersedes))
    if supersedes:
        entries = child_nodes(out, supersedes[0], "ENTRY")
        if len(entries) != 1:
            raise SystemExit(
                "expected exactly 1 RECORDING_SUPERSEDES ENTRY, got %d - the "
                "source save moved and the whole-node drop below is sized "
                "against a single orphan row" % len(entries))
        kept = set(KEEP_RECORDING_IDS)
        for key in ("oldRecordingId", "newRecordingId"):
            rid = get_value(out, entries[0], key)
            if rid in kept:
                raise SystemExit(
                    "the RECORDING_SUPERSEDES ENTRY's %s %r belongs to a KEPT "
                    "recording: dropping the row would destroy live history"
                    % (key, rid))
        start, end = supersedes[0]
        del out[start:end]

    # --- the crew reservations ------------------------------------------
    scn = parsek_scenario(out)
    slots = child_nodes(out, scn, "KERBAL_SLOTS")
    if len(slots) != 1:
        raise SystemExit("expected exactly one KERBAL_SLOTS node, got %d"
                         % len(slots))
    owners = [get_value(out, s, "owner")
              for s in child_nodes(out, slots[0], "SLOT")]
    if KEEP_KERBAL_OWNER not in owners:
        raise SystemExit("KERBAL_SLOTS carries no slot for %r (owners: %r)"
                         % (KEEP_KERBAL_OWNER, owners))
    out = _drop_grandchild_nodes(
        out, "KERBAL_SLOTS", "SLOT", "owner",
        tuple(o for o in owners if o != KEEP_KERBAL_OWNER))

    scn = parsek_scenario(out)
    repl = child_nodes(out, scn, "CREW_REPLACEMENTS")
    if len(repl) != 1:
        raise SystemExit("expected exactly one CREW_REPLACEMENTS node, got %d"
                         % len(repl))
    originals = [get_value(out, e, "original")
                 for e in child_nodes(out, repl[0], "ENTRY")]
    if KEEP_KERBAL_OWNER not in originals:
        raise SystemExit("CREW_REPLACEMENTS carries no entry for %r"
                         % KEEP_KERBAL_OWNER)
    out = _drop_grandchild_nodes(
        out, "CREW_REPLACEMENTS", "ENTRY", "original",
        tuple(o for o in originals if o != KEEP_KERBAL_OWNER))

    return out


# ---------------------------------------------------------------------------
# Post-conditions. Run on the freshly stripped fixture AND on --check.
# ---------------------------------------------------------------------------


def verify_save(lines: List[str]) -> List[str]:
    """Failure strings for the save half of the fixture (empty = all hold)."""
    problems: List[str] = []

    scn = parsek_scenario(lines)
    if scn is None:
        return ["no ParsekScenario SCENARIO node"]

    trees = child_nodes(lines, scn, "RECORDING_TREE")
    if len(trees) != 1:
        problems.append("expected exactly 1 RECORDING_TREE, found %d" % len(trees))
    elif get_value(lines, trees[0], "id") != KEEP_TREE_ID:
        problems.append("the surviving tree is %r, expected %r"
                        % (get_value(lines, trees[0], "id"), KEEP_TREE_ID))
    elif get_value(lines, trees[0], "autoGeneratedRootGroupName") != EXPECT_ROOT_GROUP:
        problems.append("the tree's root group is %r, expected %r"
                        % (get_value(lines, trees[0], "autoGeneratedRootGroupName"),
                           EXPECT_ROOT_GROUP))

    if trees:
        recordings = child_nodes(lines, trees[0], "RECORDING")
        got = [get_value(lines, r, "recordingId") for r in recordings]
        if got != list(KEEP_RECORDING_IDS):
            problems.append("tree recordings are %r, expected the 14 Kerbal X #2 "
                            "ids %r" % (got, list(KEEP_RECORDING_IDS)))
        else:
            # The EVA whose crew the kept reservation serves, and the chain the
            # park signature lives on: both asserted rather than assumed.
            by_id = dict(zip(got, recordings))
            eva = by_id[EVA_RECORDING_ID]
            if get_value(lines, eva, "evaCrewName") != KEEP_KERBAL_OWNER:
                problems.append(
                    "the kept EVA %s carries evaCrewName %r, expected %r - the "
                    "surviving crew reservation is chosen from this field"
                    % (EVA_RECORDING_ID, get_value(lines, eva, "evaCrewName"),
                       KEEP_KERBAL_OWNER))
            transfer = by_id[PARK_TRANSFER_RECORDING_ID]
            if get_value(lines, transfer, "chainId") != CHAIN_ID:
                problems.append("the park transfer %s is on chain %r, expected %r"
                                % (PARK_TRANSFER_RECORDING_ID,
                                   get_value(lines, transfer, "chainId"), CHAIN_ID))

    missions = child_nodes(lines, scn, "MISSION")
    if len(missions) != 1:
        problems.append("expected exactly 1 MISSION, found %d" % len(missions))
    else:
        mission = missions[0]
        for key, expected in (("id", KEEP_MISSION_ID),
                              ("treeId", KEEP_TREE_ID),
                              ("name", EXPECT_MISSION_NAME),
                              ("loopPlayback", EXPECT_LOOP_PLAYBACK)):
            got = get_value(lines, mission, key)
            if got != expected:
                problems.append("MISSION %s is %r, expected %r"
                                % (key, got, expected))

    for name in ("RECORDING_SUPERSEDES", "LEDGER_TOMBSTONES", "REWIND_POINTS",
                 "REWIND_RETIREMENTS", "ROUTES"):
        if child_nodes(lines, scn, name):
            problems.append("ParsekScenario still carries a %s node" % name)

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
    return problems


def _verify_kerbals(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    problems: List[str] = []

    slots = child_nodes(lines, scn, "KERBAL_SLOTS")
    owners: List[str] = []
    standins: List[str] = []
    if len(slots) != 1:
        problems.append("expected exactly 1 KERBAL_SLOTS node, found %d" % len(slots))
    else:
        for slot in child_nodes(lines, slots[0], "SLOT"):
            owners.append(get_value(lines, slot, "owner"))
            for entry in child_nodes(lines, slot, "CHAIN_ENTRY"):
                standins.append(get_value(lines, entry, "name"))
        if owners != [KEEP_KERBAL_OWNER]:
            problems.append("KERBAL_SLOTS owners are %r, expected [%r]"
                            % (owners, KEEP_KERBAL_OWNER))
        if standins != [KEEP_KERBAL_STANDIN]:
            problems.append("KERBAL_SLOTS stand-ins are %r, expected [%r]"
                            % (standins, KEEP_KERBAL_STANDIN))

    repl = child_nodes(lines, scn, "CREW_REPLACEMENTS")
    if len(repl) != 1:
        problems.append("expected exactly 1 CREW_REPLACEMENTS node, found %d"
                        % len(repl))
    else:
        pairs = [(get_value(lines, e, "original"), get_value(lines, e, "replacement"))
                 for e in child_nodes(lines, repl[0], "ENTRY")]
        if pairs != [(KEEP_KERBAL_OWNER, KEEP_KERBAL_STANDIN)]:
            problems.append("CREW_REPLACEMENTS is %r, expected [(%r, %r)]"
                            % (pairs, KEEP_KERBAL_OWNER, KEEP_KERBAL_STANDIN))

    # THE DANGLING-REFERENCE HALF. A replacement naming a kerbal the ROSTER does
    # not carry is exactly the shape the prune could produce by accident.
    roster = find_node(lines, "ROSTER")
    if roster is None:
        problems.append("save has no ROSTER node")
    else:
        names = {get_value(lines, k, "name")
                 for k in child_nodes(lines, roster, "KERBAL")}
        for who in (KEEP_KERBAL_OWNER, KEEP_KERBAL_STANDIN):
            if who not in names:
                problems.append("ROSTER has no %r: the kept reservation would "
                                "resolve nothing" % who)
    return problems


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
        problems.append("sidecar families on disk are %r, expected the 14 kept ids"
                        % (stems,))

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
        count = len(os.listdir(gamestate))
        if count != GAMESTATE_FILE_COUNT:
            problems.append("Parsek/GameState carries %d file(s), expected %d"
                            % (count, GAMESTATE_FILE_COUNT))

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


def read_top_level_orbit_segments(mirror_path: str) -> List[dict]:
    """The top-level ORBIT_SEGMENT records of a `.prec.txt`, in file order.

    TOP-LEVEL ONLY, matched on the UNINDENTED header exactly as
    `text_section_spans` matches TRACK_SECTION: an ORBIT_SEGMENT nested inside a
    TrackSection is indented, and counting those would fold the per-section
    checkpoints into the recording's own segment list."""
    with open(mirror_path, "r", encoding="utf-8", newline="") as fh:
        lines = fh.read().replace("\r\n", "\n").split("\n")
    out: List[dict] = []
    i = 0
    while i < len(lines):
        if lines[i] == "ORBIT_SEGMENT" and i + 1 < len(lines) and lines[i + 1] == "{":
            j = i + 2
            body = {}
            while j < len(lines) and lines[j] != "}":
                line = lines[j]
                if line.startswith("\t") and " = " in line:
                    key, value = line[1:].split(" = ", 1)
                    body[key] = value
                j += 1
            out.append(body)
            i = j + 1
        else:
            i += 1
    return out


def verify_park_signature(fixture_dir: str) -> List[str]:
    """THE SUBJECT PIN: the transfer really is a heliocentric PARKING departure.

    This is the cell that separates this fixture from `duna-one-recorded`, whose
    Sun run is a single transfer conic rather than a park. It asserts, off the
    committed bytes: the segment count and body roster; that segments 2-4 are
    consecutive SUN segments agreeing on sma to `PARK_SMA_REL_TOLERANCE`; that
    they are CONTIGUOUS enough to be one coast (each starts at or after the
    previous end, and the run spans PARK_START_UT..PARK_END_UT); that segment 5
    is a DEPARTURE STEP out of that sma by orders more than the tolerance; and
    that the run ends at Duna."""
    problems: List[str] = []
    mirror = os.path.join(fixture_dir, "Parsek", "Recordings",
                          PARK_TRANSFER_RECORDING_ID + ".prec.txt")
    if not os.path.isfile(mirror):
        return ["the park transfer's mirror %s.prec.txt is missing"
                % PARK_TRANSFER_RECORDING_ID]

    segments = read_top_level_orbit_segments(mirror)
    if len(segments) != EXPECT_SEGMENT_COUNT:
        problems.append("%s carries %d top-level ORBIT_SEGMENTs, expected %d"
                        % (PARK_TRANSFER_RECORDING_ID, len(segments),
                           EXPECT_SEGMENT_COUNT))
        return problems

    bodies = tuple(s.get("body") for s in segments)
    if bodies != EXPECT_SEGMENT_BODIES:
        problems.append("the segment body roster is %r, expected %r"
                        % (bodies, EXPECT_SEGMENT_BODIES))

    park = [segments[i] for i in PARK_SEGMENT_INDICES]
    for index, segment in zip(PARK_SEGMENT_INDICES, park):
        if segment.get("body") != PARK_BODY:
            problems.append("park segment %d is around %r, expected %r"
                            % (index, segment.get("body"), PARK_BODY))
        sma = float(segment["sma"])
        if abs(sma - PARK_SMA) / PARK_SMA > PARK_SMA_REL_TOLERANCE:
            problems.append(
                "park segment %d has sma %r, which differs from the pinned "
                "park sma %r by more than %g relative - this is no longer one "
                "parking orbit" % (index, sma, PARK_SMA, PARK_SMA_REL_TOLERANCE))

    # One coast, not three unrelated arcs: each park segment must begin at or
    # after its predecessor's end, and the run must span the pinned window.
    for previous, current, index in zip(park, park[1:], PARK_SEGMENT_INDICES[1:]):
        if float(current["startUT"]) < float(previous["endUT"]):
            problems.append("park segment %d starts before its predecessor ends"
                            % index)
    if float(park[0]["startUT"]) != PARK_START_UT:
        problems.append("the park run starts at %r, expected %r"
                        % (float(park[0]["startUT"]), PARK_START_UT))
    if float(park[-1]["endUT"]) != PARK_END_UT:
        problems.append("the park run ends at %r, expected %r"
                        % (float(park[-1]["endUT"]), PARK_END_UT))

    departure = segments[DEPARTURE_SEGMENT_INDEX]
    if departure.get("body") != PARK_BODY:
        problems.append("the departure segment is around %r, expected %r"
                        % (departure.get("body"), PARK_BODY))
    departure_sma = float(departure["sma"])
    if departure_sma != DEPARTURE_SMA:
        problems.append("the departure segment sma is %r, expected %r"
                        % (departure_sma, DEPARTURE_SMA))
    if abs(departure_sma - PARK_SMA) / PARK_SMA <= PARK_SMA_REL_TOLERANCE:
        problems.append("the departure segment sma %r is indistinguishable from "
                        "the park sma %r: there is no departure burn here"
                        % (departure_sma, PARK_SMA))
    if float(departure["startUT"]) != DEPARTURE_START_UT:
        problems.append("the departure starts at %r, expected %r"
                        % (float(departure["startUT"]), DEPARTURE_START_UT))

    # ... and it actually gets to Duna.
    arrivals = [s for s in segments if s.get("body") == "Duna"]
    if not arrivals:
        problems.append("no Duna segment: this subject must REACH Duna")
    elif float(arrivals[0]["startUT"]) != DUNA_SOI_ENTRY_UT:
        problems.append("Duna SOI entry is at %r, expected %r"
                        % (float(arrivals[0]["startUT"]), DUNA_SOI_ENTRY_UT))
    return problems


def repair_prec(recordings_dir: str) -> List[dict]:
    """Run the INV2 containment dedupe over the park transfer's sidecar.

    Delegates to `sibling.repair_prec` by pointing its module constant at THIS
    fixture's recording for the call. Rebinding rather than re-implementing keeps
    one copy of the coverage-invariance assertion, the partial-overlap refusal
    and the mirror-agreement check; the try/finally restores the constant so a
    process that imported both builders cannot leave the other one aimed here."""
    saved = sibling.INV2_REPAIR_RECORDING_ID
    sibling.INV2_REPAIR_RECORDING_ID = INV2_REPAIR_RECORDING_ID
    try:
        return sibling.repair_prec(recordings_dir)
    finally:
        sibling.INV2_REPAIR_RECORDING_ID = saved


def verify_prec(fixture_dir: str) -> List[str]:
    """Failure strings for the trajectory sidecars: the repair is a FIXED POINT.

    The claim is BROADER than `duna-one-recorded`'s, which checks the one
    recording it repaired: here EVERY kept recording is walked. Each `.prec`
    parses cleanly under the reader, its section list is overlap-free in the
    exact sense `Inv2NoDoubleCover` means, re-running the containment dedupe
    would drop nothing (the repair already ran, and the other thirteen never
    needed one), and the `.prec.txt` mirror still describes the same list per
    index."""
    problems: List[str] = []
    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")

    prec = os.path.join(recordings, INV2_REPAIR_RECORDING_ID + ".prec")
    if os.path.isfile(prec):
        _count_offset, sections = read_prec_sections(_read_bytes(prec))
        if len(sections) != INV2_EXPECTED_SECTIONS_AFTER:
            problems.append("%s carries %d TrackSections, expected %d"
                            % (INV2_REPAIR_RECORDING_ID, len(sections),
                               INV2_EXPECTED_SECTIONS_AFTER))
        spans = [(s["startUT"], s["endUT"]) for s in sections]
        duplicated = sorted(set(s for s in spans if spans.count(s) > 1))
        if duplicated:
            problems.append("these spans are still carried by two sections: %r"
                            % (duplicated,))
        # The two SOI-seam spans the repair de-duplicated must SURVIVE, exactly
        # once each: those checkpoints carry the park segment and the Duna
        # arrival hyperbola, which are the fixture's whole reason to exist.
        for label, span in (("Kerbin->Sun", (PARK_START_UT, 2560670336.8959155)),
                            ("Sun->Duna", (DUNA_SOI_ENTRY_UT, 2570490859.060472))):
            if spans.count(span) != 1:
                problems.append("the %s seam span %r appears %d time(s), "
                                "expected exactly 1"
                                % (label, span, spans.count(span)))

    for rid in KEEP_RECORDING_IDS:
        prec = os.path.join(recordings, rid + ".prec")
        if not os.path.isfile(prec):
            problems.append("%s.prec is missing" % rid)
            continue
        _count_offset, sections = read_prec_sections(_read_bytes(prec))

        overlaps = overlapping_pairs(sections)
        if overlaps:
            problems.append("%s sections overlap (Inv2NoDoubleCover would FAIL): "
                            "%r" % (rid, overlaps))
        redundant = find_redundant_sections(sections)
        if redundant:
            problems.append("%s: the dedupe is not stable - re-running it would "
                            "drop %r" % (rid, redundant))

        with open(prec + ".txt", "r", encoding="utf-8", newline="") as fh:
            text = fh.read()
        spans = text_section_spans(text.replace("\r\n", "\n"))
        if len(spans) != len(sections):
            problems.append("%s: the .prec.txt mirror carries %d TrackSections "
                            "against the binary's %d"
                            % (rid, len(spans), len(sections)))
            continue
        for section, span in zip(sections, spans):
            if section["startUT"] != span[2] or section["endUT"] != span[3]:
                problems.append("%s section %d differs between the binary and "
                                "its mirror" % (rid, section["index"]))
                break
    return problems


# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of stripping it")
    parser.add_argument("--target-name", default=TARGET_NAME)
    parser.add_argument("--source-log", default=DEFAULT_SOURCE_LOG,
                        help="the collected log the fixture was harvested from; "
                             "only its sibling parsek/GameState/ is read")
    args = parser.parse_args(argv)

    fixture_dir = os.path.join(_SAVES, args.target_name)
    sfs = os.path.join(fixture_dir, "persistent.sfs")
    if not os.path.isfile(sfs):
        print("FAIL: %s does not exist (run harvest_bdock_station.py first)" % sfs)
        return 1

    if args.check:
        problems = verify_save(read_lines(sfs))
        problems += verify_tree(fixture_dir)
        problems += verify_park_signature(fixture_dir)
        problems += verify_prec(fixture_dir)
        for p in problems:
            print("FAIL: %s" % p)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    # --- 1 + 3: the save ------------------------------------------------
    write_lines(sfs, strip_save(read_lines(sfs)))
    print("stripped persistent.sfs to 1 tree / 1 mission / %d recordings"
          % len(KEEP_RECORDING_IDS))

    # --- 2: the sidecars ------------------------------------------------
    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")
    keep = set(KEEP_RECORDING_IDS)
    removed = 0
    for name in sorted(os.listdir(recordings)):
        path = os.path.join(recordings, name)
        if os.path.isfile(path) and _stem(name) not in keep:
            os.remove(path)
            removed += 1
    print("removed %d sidecar file(s) belonging to dropped recordings" % removed)

    # --- 4: GameState + AddOns ------------------------------------------
    gamestate_src = os.path.join(args.source_log, "parsek", "GameState")
    if not os.path.isdir(gamestate_src):
        print("FAIL: %s does not exist - pass --source-log <collected log dir>"
              % gamestate_src)
        return 1
    gamestate_dst = os.path.join(fixture_dir, "Parsek", "GameState")
    shutil.rmtree(gamestate_dst, ignore_errors=True)
    shutil.copytree(gamestate_src, gamestate_dst)
    print("restored Parsek/GameState (%d file(s)) from the log copy"
          % len(os.listdir(gamestate_dst)))

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
    problems += verify_park_signature(fixture_dir)
    problems += verify_prec(fixture_dir)
    for p in problems:
        print("FAIL: %s" % p)
    if problems:
        return 1
    print("OK: wrote %s" % fixture_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
