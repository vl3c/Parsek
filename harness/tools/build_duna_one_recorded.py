#!/usr/bin/env python3
"""Strip the harvested `duna-one-recorded` fixture down to the Duna One mission.

WHY THIS EXISTS, AND WHAT MAKES THIS FIXTURE A NEW PROVENANCE CLASS. Every other
RECORDED-state fixture in the tree is the product of a DRIVEN harness run: one
scenario, one craft, one committed tree, harvested straight out of the produced
save. This one is the first harvested from the OPERATOR'S OWN FREE-PLAY SESSION
(`logs/2026-08-25_1537_s15-duna-one-manifest-run2`, visually validated
2026-08-25), which is what the M-A7 RC-WARP lane needs: a real multi-vessel
interplanetary mission with a loop-armed MISSION row, EVA, decoupled children and
debris, rather than a synthesised profile.

A free-play save is not a fixture, though. That one carried FOUR unrelated
RECORDING_TREEs (a Jumping Flea, two Kerbal X launches, and the Duna One mission),
47 recording sidecar families, five stand-in kerbal slots, a supersede row from a
re-fly of a tree nobody wants, 12 MB of orphan-sweep quarantine, and six analyzer
INV2 FAILs. This tool is the recipe that turns it into a subject: keep the ONE
mission, drop everything else, and repair the one defect that stands between the
committed bytes and a GREEN `analyze-recordings.ps1 -FailOnRed -FreshSaveGate`.

INPUT. The output of

    python harness/tools/harvest_bdock_station.py \\
        --save-dir <log-copy>/saves/s15 --target-name duna-one-recorded \\
        --expect-situation PRELAUNCH --keep-parsek

i.e. this tool edits `harness/fixtures/saves/duna-one-recorded` IN PLACE. The
harvest already did the generic half (title normalisation, `rewindSave` hint
strip, `Parsek/Saves`, `.craft.txt` snapshot mirrors, `_quarantine`); everything
below is Duna-One-specific and belongs nowhere near the generic harvester.

NEVER NAME THE FIXTURE `s15`. Staging rmtree's the same-named save inside the
automation instance, so a fixture called `s15` would delete the operator's
hand-played save the first time any scenario staged it.

WHAT IT DOES, in order:

  1. THE SAVE. Keeps RECORDING_TREE `1ccdb192...` ("Duna One", 13 recordings) and
     its MISSION `0aad5325...` (`loopPlayback = True`); deletes the other three
     trees, their three MISSION rows, and the single RECORDING_SUPERSEDES ENTRY
     (both of whose ids belong to the dropped Kerbal X tree `7f01f8b9...`, so the
     row would be an orphan the LoadTimeSweep warns about).
  2. THE SIDECARS. Keeps the 13 kept recordings' sidecar families; deletes the
     other 34.
  3. THE KERBALS. `KERBAL_SLOTS` / `CREW_REPLACEMENTS` are pruned to VALENTINA's
     slot and entry alone - her stand-in `Laemy Kerman` is the one that serves a
     KEPT recording (the EVA `f9caa140...`, vesselName `Valentina Kerman`). The
     other four slots reserve crew for trees that no longer exist. The stand-in
     kerbal must survive in `ROSTER`, which `verify` asserts: a CREW_REPLACEMENTS
     entry naming a kerbal the roster does not carry is a dangling reference.
     GROUP_HIERARCHY IS KEPT WHOLE, deliberately: it is name-keyed UI state, an
     entry for a group nothing references is inert, and pruning it would couple
     this recipe to the auto-group naming rules for no gain.
  4. THE TWO DIRECTORIES THE LOG COPY DID NOT CARRY. `collect-logs.py` copies the
     save's `Parsek/` to a SIBLING `parsek/` directory and leaves only
     `Parsek/Recordings` under `saves/<name>/`, so the harvest produced a fixture
     with no `Parsek/GameState/`; and the operator's dev-instance save carries no
     `AddOns/` at all. GameState (all 9 files) is restored from the log copy's
     sibling; `AddOns/DistantObject/Settings.cfg` is copied from a sibling
     COMMITTED fixture (618 bytes, byte-identical across all 30-odd fixtures that
     carry that variant - `verify` re-checks the size and the donor's bytes).
  5. THE INV2 REPAIR - see `find_redundant_sections` below for the whole story.

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture and
writes nothing. It is WIRED, not decorative: `DunaOneRecordedFixtureDriftTests`
in `harness/lib/test_build_duna_one_recorded.py` runs the same `verify_*`
functions in-process, so a hand-edit of the committed bytes reds in the harness
suite. It CANNOT re-run `build` the way `CareerEarnedPadFixtureDriftTests` does,
because this fixture's input is a collected log directory that is not committed
and never will be (24 MB of hand-played save). The byte-stability claim it makes
instead is the one that matters here: re-running the INV2 dedupe over the
COMMITTED `.prec` must drop NOTHING, which is exactly the statement "the repair
already ran and the result is stable".

Usage:
    python harness/tools/build_duna_one_recorded.py            # strip in place
    python harness/tools/build_duna_one_recorded.py --check    # verify only

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import struct
import sys
from typing import List, Optional, Sequence, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_REPO_ROOT = os.path.dirname(_HARNESS_ROOT)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text node helpers, for the same reason
# build_career_earned_pad.py imports them: a second implementation is a second
# thing to drift. The FILE I/O is deliberately NOT reused - those helpers
# normalise to CRLF on write, and `harvest_bdock_station.py` writes this save's
# `persistent.sfs` with an explicit LF-only newline. Keeping the harvest's own
# line endings means the committed bytes are still "what the tool chain wrote".
import build_career_pad_craft as base_builder  # noqa: E402

find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value

TARGET_NAME = "duna-one-recorded"

# The AddOns donor. Any of the ~30 fixtures carrying the 618-byte variant would
# do; a RECORDED sibling is named so the donor is a fixture of the same class.
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
KEEP_TREE_ID = "1ccdb19215034ac19f3a8e31697b05ed"
DROP_TREE_IDS = (
    "90a6f987fff74837a32ae5b3fe5e28da",   # Jumping Flea
    "7f01f8b9a57e4788bf9d9d009c90ed10",   # Kerbal X (the re-flown one)
    "ced7848157674b8ea19311377c0f6fbc",   # Kerbal X #2
)
KEEP_MISSION_ID = "0aad5325bcfb4ea1a147d8691ec26443"
EXPECT_MISSION_NAME = "Duna One"
EXPECT_ROOT_GROUP = "Duna One"

# The 13 recordings of the Duna One tree, in treeOrder. Spelled out rather than
# derived from the save so that a save whose tree changed reds loudly here
# instead of silently shipping a different payload.
KEEP_RECORDING_IDS = (
    "5d68d429060b429987bc8be7bb930bd2",   # 0  chain 0, the launch
    "6dae41d1b2584f0cbc49afdd587cbdfe",   # 1  debris
    "efe1ab5e2fbd48dd868c724f3ab56344",   # 2  debris
    "b06a8b5f81274995879f42b67d24eae8",   # 3  debris
    "4ed6e4f2767d455685b64b488704a023",   # 4  debris
    "70e9c28bd20947d0afeaa1deb9215e34",   # 5  debris
    "7c92064d5d0640a1a18126bc2aab2cc7",   # 6  debris
    "6561c8eb97dd48d6825e9d6c7c04d22a",   # 7  the decoupled Kerbal X Probe
    "cead1f22d40443f48d1bd955ad072257",   # 8  landed Kerbal X
    "f9caa140787248f3b67d48dfcf494c7b",   # 9  Valentina's EVA
    "61e9177193444e329247d0e8288cf91e",   # 10 chain 1, THE TRANSFER
    "0b91670cf8334780a2b78687e80d6923",   # 11 chain 2, the descent
    "7609b87bc4fa44788ecd180e177b8475",   # 12 chain 3, the landing
)

# The kerbal whose reservation survives. Her stand-in flies the kept EVA
# recording f9caa140; the other four slots reserved crew for dropped trees.
KEEP_KERBAL_OWNER = "Valentina Kerman"
KEEP_KERBAL_STANDIN = "Laemy Kerman"

# --- the INV2 repair -----------------------------------------------------
#
# THE DEFECT. The transfer recording `61e9177193444e329247d0e8288cf91e` carries
# 75 TrackSections, six of which are REDUNDANT COPIES of coverage another section
# already owns. `Inv2NoDoubleCover` reports each as a FAIL (two sections covering
# one UT = an ambiguous playback position), and six FAILs is six times more than
# the fresh-save gate tolerates.
#
# WHAT THEY ARE, and why this is a repair rather than a rewrite. Five of the six
# sit at the FOUR SOI seams plus one mid-Duna capture arc, and every one is a
# pure duplicate of a span its neighbour already covers:
#
#   idx 34  [64044032.725027621, 65004886.739419721]   Kerbin -> Sun seam
#   idx 43  [70898646.0584081,   70912683.547375381]   Sun -> Duna seam
#   idx 47  [70956143.35894987,  70956471.231831044]   Duna -> Ike seam
#   idx 51  [70958360.7066507,   70958731.38776888]    Ike -> Duna seam
#       all four are EXACT-SPAN duplicates of the following section (35/44/48/52),
#       and in each pair the dropped one is the frame-less `ref=0 src=0` shell
#       while the KEPT one is the `ref=2 src=2` OrbitalCheckpoint that carries the
#       span's ORBIT_SEGMENT. Dropping the shell therefore removes no orbit data.
#   idx 60  [70960696.459866241, 70960923.929514691]   contained in 61
#   idx 62  [70960923.929514691, 70962487.1269182]     contained in 61
#       the three-section cluster around the Duna capture: 61 spans
#       [70960696.459866241, 70962487.1269182] and 60 + 62 partition exactly that
#       span. 62's nested ORBIT_SEGMENT is element-for-element identical to 61's
#       (same sma / ecc / inc / lan / argPe / mna / EPOCH) - a re-clip of one
#       conic, not a second orbit - so again nothing unique is lost.
#
# COVERAGE IS EXACTLY PRESERVED. The predicate below only ever drops a section
# whose span is CONTAINED in a kept one, so the union over the section list is
# invariant by construction - which `repair_prec` asserts rather than assumes.
# No trajectory POINT is touched, no other recording is touched, and the top-level
# 22-entry ORBIT_SEGMENT list (the surface the loop lanes read) is untouched: the
# splice removes whole TrackSection records from the binary and decrements the
# section count, leaving every other byte, including the string table, verbatim.
INV2_DROPPED_SECTION_INDICES = (34, 43, 47, 51, 60, 62)
INV2_REPAIR_RECORDING_ID = "61e9177193444e329247d0e8288cf91e"
INV2_EXPECTED_SECTIONS_BEFORE = 75
INV2_EXPECTED_SECTIONS_AFTER = 69

# The four SOI seams the fixture's provenance comment quotes, kept here so the
# repair and the registry pin cannot drift apart.
EXPLICIT_START_UT = 52569490.911798075
SOI_SEAM_UTS = (
    ("Kerbin->Sun", 64044032.725027621),
    ("Sun->Duna", 70898646.0584081),
    ("Duna->Ike", 70956143.35894987),
    ("Ike->Duna", 70958360.7066507),
)

# Harvest exhaust and derived data no fixture may carry. Re-asserted here rather
# than left to the harness cells alone so `--check` is a complete statement about
# these bytes on its own.
FORBIDDEN_DIR_NAMES = ("_quarantine", "Saves")
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


# ---------------------------------------------------------------------------
# Step 1 + 3: the save edits.
# ---------------------------------------------------------------------------


def strip_save(lines: List[str]) -> List[str]:
    """Return the save with everything but the Duna One mission removed.

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
            "save moved and this recipe must be re-derived" % (sorted(ids), sorted(want)))
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
    # belong to the dropped Kerbal X tree, so after step 1 the row resolves
    # nothing on either side. `LoadTimeSweep` warn-logs exactly that shape.
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
        raise SystemExit("expected exactly one KERBAL_SLOTS node, got %d" % len(slots))
    owners = [get_value(out, s, "owner") for s in child_nodes(out, slots[0], "SLOT")]
    if KEEP_KERBAL_OWNER not in owners:
        raise SystemExit("KERBAL_SLOTS carries no slot for %r (owners: %r)"
                         % (KEEP_KERBAL_OWNER, owners))
    out = _drop_grandchild_nodes(
        out, "KERBAL_SLOTS", "SLOT", "owner",
        tuple(o for o in owners if o != KEEP_KERBAL_OWNER))

    scn = parsek_scenario(out)
    repl = child_nodes(out, scn, "CREW_REPLACEMENTS")
    if len(repl) != 1:
        raise SystemExit("expected exactly one CREW_REPLACEMENTS node, got %d" % len(repl))
    originals = [get_value(out, e, "original")
                 for e in child_nodes(out, repl[0], "ENTRY")]
    if KEEP_KERBAL_OWNER not in originals:
        raise SystemExit("CREW_REPLACEMENTS carries no entry for %r" % KEEP_KERBAL_OWNER)
    out = _drop_grandchild_nodes(
        out, "CREW_REPLACEMENTS", "ENTRY", "original",
        tuple(o for o in originals if o != KEEP_KERBAL_OWNER))

    return out


def _drop_child_nodes(lines: List[str], node_name: str, key: str,
                      values: Sequence[str]) -> List[str]:
    """Delete every direct `node_name` child of ParsekScenario whose `key` is in
    ``values``. Deleted last-first so earlier spans stay valid."""
    out = list(lines)
    targets = set(values)
    while True:
        scn = parsek_scenario(out)
        spans = [n for n in child_nodes(out, scn, node_name)
                 if get_value(out, n, key) in targets]
        if not spans:
            return out
        start, end = spans[-1]
        del out[start:end]


def _drop_grandchild_nodes(lines: List[str], parent_name: str, child_name: str,
                           key: str, values: Sequence[str]) -> List[str]:
    out = list(lines)
    targets = set(values)
    if not targets:
        return out
    while True:
        scn = parsek_scenario(out)
        parents = child_nodes(out, scn, parent_name)
        if not parents:
            return out
        spans = [n for n in child_nodes(out, parents[0], child_name)
                 if get_value(out, n, key) in targets]
        if not spans:
            return out
        start, end = spans[-1]
        del out[start:end]


# ---------------------------------------------------------------------------
# Step 5: the INV2 repair. A minimal binary reader over the `.prec` format
# (`Source/Parsek/TrajectorySidecarBinary.cs`), used ONLY to locate the byte
# range of each TrackSection record; the splice then removes whole records and
# decrements the count. Nothing is re-encoded, so every surviving byte - the
# string table included, even where a dropped section was its only user - is the
# one Parsek wrote.
# ---------------------------------------------------------------------------


class _PrecReader(object):
    def __init__(self, blob: bytes):
        self.b = blob
        self.i = 0

    def u8(self) -> int:
        v = self.b[self.i]
        self.i += 1
        return v if isinstance(v, int) else ord(v)

    def i32(self) -> int:
        v = struct.unpack_from("<i", self.b, self.i)[0]
        self.i += 4
        return v

    def f64(self) -> float:
        v = struct.unpack_from("<d", self.b, self.i)[0]
        self.i += 8
        return v

    def string(self) -> str:
        n, shift = 0, 0
        while True:
            byte = self.u8()
            n |= (byte & 0x7F) << shift
            if not byte & 0x80:
                break
            shift += 7
        v = self.b[self.i:self.i + n].decode("utf-8")
        self.i += n
        return v

    # `WritePoint` is 89 fixed bytes; the sparse layout drops whichever of
    # body/funds/science/reputation the list carries a default for.
    def skip_point_list(self) -> None:
        count = self.i32()
        if count == 0:
            return
        flags = self.u8()
        if not flags & 0x01:                      # SparsePointListFlagEnabled
            self.i += count * 89
            return
        if flags & 0x02:
            self.i += 4                           # default body index
        if flags & 0x04:
            self.i += 8                           # default funds
        if flags & 0x08:
            self.i += 4                           # default science
        if flags & 0x10:
            self.i += 4                           # default reputation
        for _ in range(count):
            self.i += 32 + 16 + 12                # ut/lat/lon/alt, rot, velocity
            point_flags = self.u8()
            self.i += _sparse_field_bytes(flags, point_flags, 0x02, 0x01, 4)
            self.i += _sparse_field_bytes(flags, point_flags, 0x04, 0x02, 8)
            self.i += _sparse_field_bytes(flags, point_flags, 0x08, 0x04, 4)
            self.i += _sparse_field_bytes(flags, point_flags, 0x10, 0x08, 4)
            self.i += 8 + 1                       # groundClearance, flags

    def skip_orbit_segment_list(self) -> None:
        # 9 doubles + body index + predicted flag + 4 rotation + 3 angular floats.
        self.skip_fixed_list(105)

    def skip_fixed_list(self, element_bytes: int) -> None:
        """Skip a `count`-prefixed list of fixed-size records.

        The count is read into a LOCAL first, deliberately: `self.i +=
        self.i32() * n` looks equivalent and is not - Python's augmented
        assignment loads `self.i` BEFORE evaluating the right-hand side, so the
        four bytes `i32()` consumed are silently un-consumed and every later
        offset is short by four."""
        count = self.i32()
        self.i += count * element_bytes


def _sparse_field_bytes(list_flags: int, point_flags: int,
                        list_bit: int, override_bit: int, size: int) -> int:
    """Bytes a sparse point spends on one optional field."""
    if not list_flags & list_bit:
        return size
    return size if point_flags & override_bit else 0


def read_prec_sections(blob: bytes) -> Tuple[int, List[dict]]:
    """(offset of the TrackSection count int32, one dict per section).

    Each dict carries `index`, `start`/`end` byte offsets and `startUT`/`endUT`.
    Raises if the walk does not land exactly on end-of-file, which is the whole
    safety argument for splicing bytes out of a format this does not re-encode."""
    r = _PrecReader(blob)
    if blob[:4] != b"PSK0":
        raise SystemExit("not a PSK0 trajectory sidecar")
    r.i = 4
    r.i32()                                       # formatVersion
    r.i32()                                       # recordingSchemaGeneration
    r.i32()                                       # sidecarEpoch
    r.string()                                    # recordingId
    r.u8()                                        # sectionAuthoritative flag
    for _ in range(r.i32()):                      # string table
        r.string()
    r.skip_point_list()                           # flat fallback points
    r.skip_orbit_segment_list()                   # top-level orbit segments
    r.skip_fixed_list(28)                         # part events
    r.skip_fixed_list(68)                         # flag events
    r.skip_fixed_list(16)                         # segment events

    count_offset = r.i
    count = r.i32()
    sections: List[dict] = []
    for index in range(count):
        start = r.i
        r.i32()                                   # environment
        r.i32()                                   # referenceFrame
        start_ut = r.f64()
        end_ut = r.f64()
        r.i32()                                   # anchorVesselId
        r.i32()                                   # anchorRecordingId index
        r.i += 4                                  # sampleRateHz
        r.i32()                                   # source
        r.i += 4 + 4 + 4                          # discontinuity, min/max altitude
        r.u8()                                    # isBoundarySeam
        r.skip_point_list()                       # frames
        r.skip_point_list()                       # bodyFixedFrames
        r.skip_orbit_segment_list()               # checkpoints
        sections.append({"index": index, "start": start, "end": r.i,
                         "startUT": start_ut, "endUT": end_ut})
    if r.i != len(blob):
        raise SystemExit(
            "trajectory walk ended at byte %d of %d: the .prec layout is not the "
            "one this reader models, and splicing it would corrupt the fixture"
            % (r.i, len(blob)))
    return count_offset, sections


def find_redundant_sections(sections: Sequence[dict]) -> List[int]:
    """Indices of sections whose span is fully covered by a section that is KEPT.

    THE PREDICATE IS CONTAINMENT, NOT OVERLAP, and that is the whole reason
    coverage survives: a section is dropped only when some other section's
    [start,end] contains its own, so the union over the list cannot change.
    A partial overlap - two sections that cross without either containing the
    other - is NOT repairable this way and is deliberately left alone; the caller
    asserts the result is overlap-free, so such a case reds instead of silently
    shipping.

    ORDERING makes the choice deterministic and picks the right survivor of an
    exact-span pair: widest-first (`start` ascending, `end` descending), then
    LARGEST PAYLOAD first, then original index. On this recording that keeps the
    `ref=2 src=2` OrbitalCheckpoint (170 bytes, carrying the span's
    ORBIT_SEGMENT) over the frame-less `ref=0 src=0` shell (65 bytes) at each of
    the four SOI seams."""
    order = sorted(sections,
                   key=lambda s: (s["startUT"], -s["endUT"],
                                  -(s["end"] - s["start"]), s["index"]))
    kept: List[dict] = []
    dropped: List[int] = []
    for section in order:
        covered = any(k["startUT"] <= section["startUT"]
                      and k["endUT"] >= section["endUT"] for k in kept)
        if covered:
            dropped.append(section["index"])
        else:
            kept.append(section)
    return sorted(dropped)


def overlapping_pairs(sections: Sequence[dict]) -> List[Tuple[int, int]]:
    """Every INTERIOR overlap, in the shape `Inv2NoDoubleCover` reports.

    Mirrors that rule's running-max sweep exactly (sort by start then end; a
    section whose start is strictly BEFORE the running covered end is an
    overlap), so a green reading here means a green reading there."""
    order = sorted(sections, key=lambda s: (s["startUT"], s["endUT"]))
    if not order:
        return []
    cover_index = order[0]["index"]
    cover_end = order[0]["endUT"]
    out: List[Tuple[int, int]] = []
    for section in order[1:]:
        if section["startUT"] < cover_end:
            out.append((cover_index, section["index"]))
        if section["endUT"] > cover_end:
            cover_end = section["endUT"]
            cover_index = section["index"]
    return out


def splice_out_sections(blob: bytes, count_offset: int, sections: Sequence[dict],
                        drop: Sequence[int]) -> bytes:
    """Remove whole TrackSection records and decrement the count int32."""
    by_index = {s["index"]: s for s in sections}
    out = bytearray(blob)
    for index in sorted(drop, reverse=True):
        section = by_index[index]
        del out[section["start"]:section["end"]]
    new_count = len(sections) - len(drop)
    struct.pack_into("<i", out, count_offset, new_count)
    return bytes(out)


# --- the readable mirror ---------------------------------------------------


def text_section_spans(text: str) -> List[Tuple[int, int, float, float]]:
    """(first_line, last_line_exclusive, startUT, endUT) per top-level
    TRACK_SECTION block of a `.prec.txt`, in file order.

    Top-level only: an `ORBIT_SEGMENT` nested inside a section is indented, and a
    depth-blind scan would mistake a nested node's braces for the section's."""
    lines = text.split("\n")
    spans: List[Tuple[int, int, float, float]] = []
    i = 0
    while i < len(lines):
        if lines[i] == "TRACK_SECTION" and i + 1 < len(lines) and lines[i + 1] == "{":
            depth = 0
            j = i + 1
            while j < len(lines):
                stripped = lines[j].strip()
                if stripped == "{":
                    depth += 1
                elif stripped == "}":
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            body = lines[i:j + 1]
            start_ut = _first_top_value(body, "startUT")
            end_ut = _first_top_value(body, "endUT")
            spans.append((i, j + 1, start_ut, end_ut))
            i = j + 1
        else:
            i += 1
    return spans


def _first_top_value(body: List[str], key: str) -> float:
    prefix = "\t%s = " % key
    for line in body:
        if line.startswith(prefix):
            return float(line[len(prefix):])
    raise SystemExit("TRACK_SECTION block has no %s" % key)


def repair_prec(recordings_dir: str) -> List[dict]:
    """Run the INV2 dedupe over the transfer recording's `.prec` + `.prec.txt`.

    Returns the dropped sections (index, startUT, endUT) for the caller to
    report. Writes nothing when there is nothing to drop, so the tool is
    idempotent and `--check` can call the same reader."""
    prec = os.path.join(recordings_dir, INV2_REPAIR_RECORDING_ID + ".prec")
    mirror = prec + ".txt"
    blob = _read_bytes(prec)
    count_offset, sections = read_prec_sections(blob)

    with open(mirror, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    newline = "\r\n" if "\r\n" in text else "\n"
    spans = text_section_spans(text.replace("\r\n", "\n"))
    _assert_mirror_agrees(sections, spans)

    drop = find_redundant_sections(sections)
    if not drop:
        return []

    dropped = [dict(s) for s in sections if s["index"] in set(drop)]
    before = _covered_union(sections)
    after = _covered_union([s for s in sections if s["index"] not in set(drop)])
    if before != after:
        raise SystemExit(
            "the dedupe would change coverage (%r -> %r): refusing to write"
            % (before, after))

    remaining = [s for s in sections if s["index"] not in set(drop)]
    still = overlapping_pairs(remaining)
    if still:
        raise SystemExit(
            "sections still overlap after the containment dedupe: %r. Those are "
            "PARTIAL overlaps, which this repair deliberately does not touch"
            % (still,))

    with open(prec, "wb") as fh:
        fh.write(splice_out_sections(blob, count_offset, sections, drop))

    lines = text.replace("\r\n", "\n").split("\n")
    for first, last, _s, _e in sorted(
            (spans[i] for i in drop), key=lambda t: t[0], reverse=True):
        del lines[first:last]
    with open(mirror, "w", encoding="utf-8", newline="") as fh:
        fh.write(newline.join(lines))
    return dropped


def _assert_mirror_agrees(sections: Sequence[dict],
                          spans: Sequence[Tuple[int, int, float, float]]) -> None:
    """The binary and the readable mirror must describe the SAME section list.

    Both are written from `rec.TrackSections` in order, so index i must carry the
    same span in both. Checked before either is edited: the mirror edit is
    index-driven, and an index that means two different things in the two files
    would silently delete the wrong block from one of them."""
    if len(sections) != len(spans):
        raise SystemExit(
            "the .prec carries %d TrackSections and its .prec.txt mirror %d - "
            "they are not the same list" % (len(sections), len(spans)))
    for section, span in zip(sections, spans):
        if (section["startUT"] != span[2]) or (section["endUT"] != span[3]):
            raise SystemExit(
                "section %d differs between the .prec ([%r,%r]) and its mirror "
                "([%r,%r])" % (section["index"], section["startUT"],
                               section["endUT"], span[2], span[3]))


def _covered_union(sections: Sequence[dict]) -> List[Tuple[float, float]]:
    spans = sorted((s["startUT"], s["endUT"]) for s in sections)
    merged: List[Tuple[float, float]] = []
    for start, end in spans:
        if merged and start <= merged[-1][1]:
            if end > merged[-1][1]:
                merged[-1] = (merged[-1][0], end)
        else:
            merged.append((start, end))
    return merged


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
            problems.append("tree recordings are %r, expected the 13 Duna One ids %r"
                            % (got, list(KEEP_RECORDING_IDS)))

    missions = child_nodes(lines, scn, "MISSION")
    if len(missions) != 1:
        problems.append("expected exactly 1 MISSION, found %d" % len(missions))
    else:
        mission = missions[0]
        for key, expected in (("id", KEEP_MISSION_ID),
                              ("treeId", KEEP_TREE_ID),
                              ("name", EXPECT_MISSION_NAME),
                              ("loopPlayback", "True")):
            got = get_value(lines, mission, key)
            if got != expected:
                problems.append("MISSION %s is %r, expected %r" % (key, got, expected))

    for name in ("RECORDING_SUPERSEDES", "LEDGER_TOMBSTONES", "REWIND_POINTS",
                 "REWIND_RETIREMENTS"):
        found = child_nodes(lines, scn, name)
        if found:
            problems.append("ParsekScenario still carries a %s node" % name)

    # Every schema generation in the save must be the current one; a stray older
    # value would be a recording RecordingStore rejects at load.
    gens = set()
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("recordingSchemaGeneration = "):
            gens.add(stripped.split("=", 1)[1].strip())
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
        problems.append("expected exactly 1 CREW_REPLACEMENTS node, found %d" % len(repl))
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

    names = sorted(os.listdir(recordings))
    files = [n for n in names if os.path.isfile(os.path.join(recordings, n))]
    stems = sorted({_stem(n) for n in files})
    if stems != sorted(KEEP_RECORDING_IDS):
        problems.append("sidecar families on disk are %r, expected the 13 kept ids"
                        % (stems,))

    for rid in KEEP_RECORDING_IDS:
        for suffix in (".prec", ".prec.txt", ".pann"):
            path = os.path.join(recordings, rid + suffix)
            if not os.path.isfile(path) or os.path.getsize(path) == 0:
                problems.append("%s%s missing or empty" % (rid, suffix))

    for dirpath, dirnames, filenames in os.walk(fixture_dir):
        for d in dirnames:
            if d in FORBIDDEN_DIR_NAMES:
                problems.append("fixture carries a forbidden directory %s"
                                % os.path.relpath(os.path.join(dirpath, d), fixture_dir))
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
                            % (ADDONS_REL.replace("\\", "/"), size, ADDONS_EXPECTED_BYTES))
        donor = os.path.join(_SAVES, ADDONS_DONOR_NAME, ADDONS_REL)
        if os.path.isfile(donor) and _read_bytes(donor) != _read_bytes(addons):
            problems.append("%s differs from the %s donor's copy"
                            % (ADDONS_REL.replace("\\", "/"), ADDONS_DONOR_NAME))
    return problems


def _stem(name: str) -> str:
    for suffix in ("_vessel.craft", "_ghost.craft"):
        if name.endswith(suffix):
            return name[:-len(suffix)]
    return name.split(".")[0]


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
        return ["the repaired recording %s.prec is missing" % INV2_REPAIR_RECORDING_ID]

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

    with open(prec + ".txt", "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    spans = text_section_spans(text.replace("\r\n", "\n"))
    if len(spans) != len(sections):
        problems.append("the .prec.txt mirror carries %d TrackSections against the "
                        "binary's %d" % (len(spans), len(sections)))
    else:
        for section, span in zip(sections, spans):
            if section["startUT"] != span[2] or section["endUT"] != span[3]:
                problems.append("section %d differs between the binary and its mirror"
                                % section["index"])
                break

    # The four SOI seams the registry pin quotes must still be section boundaries;
    # a repair that ate one would move every number in that provenance comment.
    boundaries = {s["startUT"] for s in sections} | {s["endUT"] for s in sections}
    for label, ut in SOI_SEAM_UTS:
        if ut not in boundaries:
            problems.append("the %s seam at %r is no longer a section boundary"
                            % (label, ut))
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
        problems += verify_prec(fixture_dir)
        for p in problems:
            print("FAIL: %s" % p)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    # --- 1 + 3: the save ------------------------------------------------
    stripped = strip_save(read_lines(sfs))
    write_lines(sfs, stripped)
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
    problems += verify_prec(fixture_dir)
    for p in problems:
        print("FAIL: %s" % p)
    if problems:
        return 1
    print("OK: wrote %s" % fixture_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
