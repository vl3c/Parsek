#!/usr/bin/env python3
"""Build the `career-same-name-pad` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. `KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY` stage 1 shipped
the launch-guid filter that drops same-name recordings belonging to a DIFFERENT
launch; stage 2 wants live proof that the filter fires on a real recovery. The
repro needs one save carrying, at the moment a crewed craft is recovered:

  - two or more COMMITTED recordings whose `vesselName` matches the recovered
    vessel, and
  - a live launch whose guid CONCLUSIVELY DIFFERS from theirs, so the filter has
    something to drop (`guidDropped=2`), and
  - un-banked flight science, so the recover mission can reach RECOVER at all.

`L6-career-same-name-recover` reading run 1 (`2026-09-02_1137`) measured why the
obvious shortcut cannot do it. That lane re-flew `science_bench_recover` over
`career-earned-pad` - L3's PRODUCED save, which already carries two same-name
`Jumping Flea` recordings - and the flight FLEW, landed and collected, but
TRANSMIT credited ZERO career science: L3 had already banked
`crewReport@KerbinSrfLandedLaunchPad` and `mysteryGoo@KerbinSrfLandedLaunchPad`
to their caps. The mission's structural TRANSMIT -> RECOVER gate therefore failed
BEFORE recovery, the phase the correlator fires in, and no mission param can
lower the floor (`science_bench_recover.schema.toml` -> `transmitMinScienceGain`
is `required = true, min = 0.001`, and the comment above it explains that a 0.0
floor would be satisfied by any two identical readings). THE BANKED-SCIENCE
CONFLICT IS INTRINSIC TO REUSING A PRODUCED SAVE: the same flight that leaves
same-name recordings behind is the flight that banks the biome.

THE SPLIT THIS BUILDER MAKES. Take the recordings from the produced save and the
CAREER from the pre-flight one:

  RECORDINGS_DIR  `Source/Parsek.Tests/Fixtures/C2CareerPostFix` - the one
                  committed copy of what the L3 run produced. Contributes its
                  `RECORDING_TREE` node (two chained `Jumping Flea` recordings,
                  `recordedVesselGuid = f77e4207...`, `vesselPersistentId =
                  2905720181`) and the matching `Parsek/Recordings/` sidecars.
  HOST_NAME       `career-science-pad` - the save L3 FLEW to produce them, i.e.
                  the same craft, the same crew, the same pools BEFORE the
                  flight: funds 500000, science 100, and ZERO banked `Science`
                  subject nodes. Contributes everything else.

The two halves fit exactly because they are two moments of one timeline: the
recordings' `preLaunchFunds = 500000` / `preLaunchScience = 100` are the host's
own live pools. The result is a career that has flown that craft twice before
and banked nothing - which is not a state any single produced save can be in.

WHAT IS DELIBERATELY *NOT* COPIED: the produced save's
`Parsek/GameState/ledger.pgld`. Its rows credit the very science this fixture
must leave un-banked, and the recalculation engine patches KSP state from the
ledger - splicing it back would re-create the blocker the fixture exists to
avoid. Two recordings with no ledger rows is a coherent state (a recording is
not an action); a banked pool with an un-banked ledger is not.

THE FIVE EDITS (all against the HOST save text):

  1. The `RECORDING_TREE` node is spliced into the host's `ParsekScenario`
     SCENARIO node, ahead of its closing brace. Node text is copied VERBATIM -
     no id, UT, chain or point-count is rewritten, so the sidecars the copy step
     brings along stay the authority on their own contents.
  2. The `rewindSave = parsek_rw_*` hint is STRIPPED from the spliced text.
     `Parsek/Saves/` is not copied (harvest exhaust; `CommittedFixtureRewindSave
     Tests` forbids committing it), so leaving the hint would commit a dangling
     reference.
  3. THE LOAD-BEARING EDIT: the host VESSEL's `pid` is re-stamped to
     `NEW_LAUNCH_GUID`. The host craft IS the craft those recordings recorded,
     so its committed `pid` is byte-identical to their `recordedVesselGuid`; left
     alone, the live launch and both recordings would read as the SAME launch,
     `IsConclusiveLaunchGuidMismatch` would be false for both, and the run would
     measure `guidDropped=0` - the filter never firing, on a fixture built to
     make it fire.
  4. `persistentId` is NOT re-stamped, and that asymmetry is the subject. KSP
     bakes `persistentId` into the `.craft` and reuses it on every launch, so a
     genuine relaunch of this craft DOES collide on pid while carrying a fresh
     `Vessel.id`. Keeping `2905720181` on both sides is what makes this fixture a
     repro of the trap the todo entry names ("a pid match is not a launch match")
     rather than a fixture where the guid filter has nothing to disambiguate.
  5. The career clock is moved to `TARGET_UT` (the produced save's own clock,
     409.56) and the vessel's `lct` / `lastUT` follow it, so both spliced
     recordings lie wholly in the PAST of a craft that has just rolled out. The
     host's clock is 9.06, which would leave committed recordings running from
     9.99 to 347.02 in the FUTURE at load - a state no real career reaches.

WHAT THE FIXTURE THEN PRODUCES ON A FLIGHT, and it is arithmetic, not a hope: a
recover run auto-records its own launch as a chained pair (L3's own recovery
measured `candidates=2` for exactly that reason), so at recovery the correlator
sees `nameMatches=4` - two spliced plus two live - drops the two whose guid
conclusively differs (`guidDropped=2`) and picks from the two survivors. That is
the shape `L6-career-same-name-recover` pins. It is a PREDICTION until the
reading run measures it; nothing here is armed.

USAGE
    python harness/tools/build_career_same_name_pad.py            # write
    python harness/tools/build_career_same_name_pad.py --check    # verify bytes

Stdlib only; no KSP, no network. ASCII only. CRLF out, like every committed
fixture.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_REPO_ROOT = os.path.dirname(_HARNESS_ROOT)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# One copy of the ConfigNode-text helpers, for the same reason every sibling
# builder imports them: a second implementation is a second thing to drift.
import build_career_pad_craft as base_builder  # noqa: E402

read_lines = base_builder.read_lines
write_lines = base_builder.write_lines
find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value
set_top_value = base_builder.set_top_value
contains_line = base_builder.contains_line

RECORDINGS_DIR = os.path.join(_REPO_ROOT, "Source", "Parsek.Tests", "Fixtures",
                              "C2CareerPostFix")
HOST_NAME = "career-science-pad"
TARGET_NAME = "career-same-name-pad"
CREW_NAME = base_builder.CREW_NAME

# The re-stamped LIVE launch identity. A FIXED literal, not generated: the
# fixture must be byte-reproducible so the drift cell can rebuild and compare.
# It is asserted against every `recordedVesselGuid` in the produced save, so a
# re-harvest that happened to mint this guid reds rather than shipping a fixture
# whose filter silently has nothing to drop.
NEW_LAUNCH_GUID = "9b3c71e4d0a84f52ae6d18c4f7b25a30"

# The recorded launch the spliced tree carries. Held as constants so `verify`
# STATES the relationship it guards rather than merely computing it.
RECORDED_VESSEL_GUID = "f77e42072e3d4c59b04581daba628b55"
RECORDED_VESSEL_PERSISTENT_ID = "2905720181"
RECORDED_VESSEL_NAME = "Jumping Flea"
EXPECT_RECORDING_COUNT = 2

# The produced save's own clock: the moment just after those two recordings
# existed. Used verbatim so the fixture's timeline is the real one rather than a
# round number invented here.
TARGET_UT = "409.55999999991747"

# Post-conditions on the produced (or committed) save. THE POOLS ARE THE SEED
# ONES: a fixture that came out at the earned 536558 / 111.599998 would mean the
# splice had dragged the produced career's pools along and re-created the exact
# blocker this fixture exists to remove.
EXPECT_MODE = "CAREER"
EXPECT_SITUATION = "PRELAUNCH"
EXPECT_FUNDS = "500000"
EXPECT_SCIENCE = "100"

# The Rewind-to-LAUNCH hint, matched exactly as CommittedFixtureRewindSaveTests
# matches it so the strip and the gate cannot drift apart.
_REWIND_HINT_RE = re.compile(r"^\s*rewindSave = parsek_rw_\w+\s*$")

# `Saves/` is Rewind-to-LAUNCH harvest exhaust and `GameState/` is the earned
# ledger (see the docstring): neither is copied.
PRUNED_PARSEK_SUBDIRS = ("Saves", "GameState")


# ---------------------------------------------------------------------------
# THE BUILD
# ---------------------------------------------------------------------------

def recording_tree_lines(produced_lines: List[str]) -> List[str]:
    """The produced save's `RECORDING_TREE` node text, hint-stripped.

    Returned as lines at their ORIGINAL indentation, which is already the depth
    a `ParsekScenario` child sits at in both saves (GAME > SCENARIO > node), so
    the splice needs no re-indent."""
    scenario = _scenario_node(produced_lines, "ParsekScenario")
    if scenario is None:
        raise AssertionError("the produced save has no ParsekScenario node")
    trees = child_nodes(produced_lines, scenario, "RECORDING_TREE")
    if len(trees) != 1:
        raise AssertionError("expected exactly 1 RECORDING_TREE, found %d" % len(trees))
    start, end = trees[0]
    return [line for line in produced_lines[start:end]
            if not _REWIND_HINT_RE.match(line)]


def build(host_lines: List[str], produced_lines: List[str], title: str) -> List[str]:
    """The five edits, in order. Pure: takes and returns line lists."""
    lines = list(host_lines)
    set_top_value(lines, "Title", title)

    # 1 + 2. Splice the hint-stripped RECORDING_TREE into ParsekScenario.
    scenario = _scenario_node(lines, "ParsekScenario")
    if scenario is None:
        raise AssertionError("the host save has no ParsekScenario node")
    tree = recording_tree_lines(produced_lines)
    close = scenario[1] - 1          # the SCENARIO node's own closing brace
    lines[close:close] = tree

    # 3 + 4. Re-stamp the LAUNCH guid; leave the craft-baked persistentId alone.
    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        raise AssertionError("the host save has no FLIGHTSTATE node")
    vessels = child_nodes(lines, fs, "VESSEL")
    if len(vessels) != 1:
        raise AssertionError("expected exactly 1 host VESSEL, found %d" % len(vessels))
    ship = vessels[0]
    set_value(lines, ship, "pid", NEW_LAUNCH_GUID)

    # 5. Move the career clock past the spliced recordings, and roll the craft
    #    out at that clock rather than 400 seconds before it.
    set_value(lines, fs, "UT", TARGET_UT)
    fs = find_node(lines, "FLIGHTSTATE")
    ship = child_nodes(lines, fs, "VESSEL")[0]
    for key in ("lct", "lastUT"):
        if get_value(lines, ship, key) is not None:
            set_value(lines, ship, key, TARGET_UT)
    return lines


def build_loadmeta(host_meta: List[str], lines: List[str]) -> List[str]:
    """Restamp the loadmeta's UT (the pools and vessel count are unchanged).

    The Load menu reads this file, not the save, so a stale clock here shows the
    fixture at the wrong time and - more to the point - a mismatch is exactly the
    kind of silent fixture divergence that reads as a product defect later."""
    out = list(host_meta)
    for i, line in enumerate(out):
        if line.startswith("UT = "):
            out[i] = "UT = %s" % TARGET_UT
    return out


# ---------------------------------------------------------------------------
# THE POST-CONDITIONS
# ---------------------------------------------------------------------------

def verify(lines: List[str], crew_name: str) -> List[str]:
    """Return a list of failure strings (empty = every post-condition holds)."""
    problems: List[str] = []

    mode = None
    for line in lines:
        if line.startswith("\tMode = "):
            mode = line.split("=", 1)[1].strip()
            break
    if mode != EXPECT_MODE:
        problems.append("GAME Mode is %r, expected %r" % (mode, EXPECT_MODE))

    # ---- THE SUBJECT: un-banked flight science ----------------------------
    #
    # This is the post-condition the whole fixture exists for. A `Science` node
    # under ResearchAndDevelopment is a BANKED subject; the recover mission's
    # TRANSMIT gate needs the launchpad biome to still pay, and reading run 1 of
    # L6 measured what happens when it does not.
    rnd = _scenario_node(lines, "ResearchAndDevelopment")
    if rnd is None:
        problems.append("no ResearchAndDevelopment SCENARIO node")
    else:
        banked = child_nodes(lines, rnd, "Science")
        if banked:
            ids = [get_value(lines, node, "id") for node in banked]
            problems.append(
                "the career has %d BANKED Science subject(s) %s - a second flight "
                "over this biome would transmit for a 0.0 pool rise and the recover "
                "mission would fail its TRANSMIT gate before reaching recovery"
                % (len(banked), ids))
        if _scenario_value(lines, "ResearchAndDevelopment", "sci") != EXPECT_SCIENCE:
            problems.append("science pool is %r, expected %r"
                            % (_scenario_value(lines, "ResearchAndDevelopment", "sci"),
                               EXPECT_SCIENCE))
    if _scenario_value(lines, "Funding", "funds") != EXPECT_FUNDS:
        problems.append("funds pool is %r, expected %r"
                        % (_scenario_value(lines, "Funding", "funds"), EXPECT_FUNDS))

    # ---- the live launch --------------------------------------------------
    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        problems.append("no FLIGHTSTATE node")
        return problems
    vessels = child_nodes(lines, fs, "VESSEL")
    if len(vessels) != 1:
        problems.append("expected exactly 1 VESSEL, found %d" % len(vessels))
        return problems
    ship = vessels[0]

    if get_value(lines, ship, "sit") != EXPECT_SITUATION:
        problems.append("vessel sit is %r, expected %r"
                        % (get_value(lines, ship, "sit"), EXPECT_SITUATION))
    if get_value(lines, fs, "activeVessel") != "0":
        problems.append("activeVessel is %r, expected '0'"
                        % get_value(lines, fs, "activeVessel"))
    if not contains_line(lines, ship, "crew = %s" % crew_name):
        problems.append("no `crew = %s` line inside the vessel: the recovery XP row "
                        "the lane pins needs a crewed recovery" % crew_name)

    # ---- the correlator's preconditions, stated rather than computed ------
    vessel_guid = get_value(lines, ship, "pid")
    vessel_pid = get_value(lines, ship, "persistentId")
    recorded_guids = _values_named(lines, "recordedVesselGuid")
    recorded_pids = _values_named(lines, "vesselPersistentId")
    recorded_names = _values_named(lines, "vesselName")

    if len(recorded_guids) != EXPECT_RECORDING_COUNT:
        problems.append("expected %d recordedVesselGuid values, found %d: the "
                        "spliced RECORDING_TREE did not survive"
                        % (EXPECT_RECORDING_COUNT, len(recorded_guids)))
    if set(recorded_guids) not in (set(), {RECORDED_VESSEL_GUID}):
        problems.append("the spliced recordings carry %r, expected all %r"
                        % (sorted(set(recorded_guids)), RECORDED_VESSEL_GUID))
    if vessel_guid != NEW_LAUNCH_GUID:
        problems.append("vessel pid is %r, expected the re-stamped %r"
                        % (vessel_guid, NEW_LAUNCH_GUID))
    if vessel_guid in recorded_guids:
        problems.append(
            "the live launch guid %r equals a recording's recordedVesselGuid: "
            "IsConclusiveLaunchGuidMismatch would be FALSE for it and the run would "
            "measure guidDropped=0, which is the one outcome this fixture must not "
            "produce" % vessel_guid)
    if vessel_pid not in recorded_pids:
        problems.append(
            "the live craft-baked persistentId %r matches NO recording (%r): the "
            "pid collision IS the subject - a fixture without it proves the filter "
            "on a case nothing else could confuse" % (vessel_pid, sorted(set(recorded_pids))))
    if vessel_pid != RECORDED_VESSEL_PERSISTENT_ID:
        problems.append("vessel persistentId is %r, expected the craft-baked %r"
                        % (vessel_pid, RECORDED_VESSEL_PERSISTENT_ID))
    if set(recorded_names) not in (set(), {RECORDED_VESSEL_NAME}):
        problems.append("the spliced recordings name %r, expected all %r - the "
                        "correlator matches on NAME first"
                        % (sorted(set(recorded_names)), RECORDED_VESSEL_NAME))

    # ---- the clock --------------------------------------------------------
    ut = get_value(lines, fs, "UT")
    ends = [float(v) for v in _values_named(lines, "explicitEndUT")]
    if ut is None:
        problems.append("FLIGHTSTATE has no UT")
    elif ends and float(ut) < max(ends):
        problems.append("career clock UT=%s is BEFORE the spliced recordings end "
                        "(%s): they would be committed recordings in the future"
                        % (ut, max(ends)))
    if get_value(lines, ship, "lct") != ut:
        problems.append("vessel lct is %r, expected the career clock %r"
                        % (get_value(lines, ship, "lct"), ut))

    # ---- the dangling-hint gate ------------------------------------------
    stray = [line.strip() for line in lines if _REWIND_HINT_RE.match(line)]
    if stray:
        problems.append("rewindSave hint(s) survived the strip: %s - Parsek/Saves/ "
                        "is not committed, so the reference dangles" % (stray,))
    return problems


# ---------------------------------------------------------------------------
# small readers (same shapes as the sibling builders)
# ---------------------------------------------------------------------------

def _values_named(lines: List[str], key: str) -> List[str]:
    prefix = key + " = "
    out: List[str] = []
    for line in lines:
        s = line.strip()
        if s.startswith(prefix):
            out.append(s[len(prefix):].strip())
    return out


def _scenario_node(lines: List[str], scenario_name: str) -> Optional[Tuple[int, int]]:
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == scenario_name:
            return node
        i = node[0] + 1


def _scenario_value(lines: List[str], scenario_name: str, key: str) -> Optional[str]:
    node = _scenario_node(lines, scenario_name)
    return None if node is None else get_value(lines, node, key)


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of writing it")
    parser.add_argument("--crew", default=CREW_NAME)
    parser.add_argument("--target-name", default=TARGET_NAME)
    args = parser.parse_args(argv)

    target_dir = os.path.join(_SAVES, args.target_name)
    target_sfs = os.path.join(target_dir, "persistent.sfs")

    if args.check:
        if not os.path.isfile(target_sfs):
            print("FAIL: %s does not exist" % target_sfs)
            return 1
        problems = verify(read_lines(target_sfs), args.crew)
        for p in problems:
            print("FAIL: %s" % p)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    host_dir = os.path.join(_SAVES, HOST_NAME)
    for d in (RECORDINGS_DIR, host_dir):
        if not os.path.isdir(d):
            print("FAIL: missing input fixture %s" % d)
            return 1

    host_lines = read_lines(os.path.join(host_dir, "persistent.sfs"))
    produced_lines = read_lines(os.path.join(RECORDINGS_DIR, "persistent.sfs"))
    built = build(host_lines, produced_lines, "%s (CAREER)" % args.target_name)

    problems = verify(built, args.crew)
    if problems:
        for p in problems:
            print("FAIL: %s" % p)
        return 1

    os.makedirs(target_dir, exist_ok=True)
    write_lines(target_sfs, built)
    write_lines(os.path.join(target_dir, "persistent.loadmeta"),
                build_loadmeta(read_lines(os.path.join(host_dir, "persistent.loadmeta")),
                               built))

    # The recordings the spliced tree names. Copied from the PRODUCED save,
    # which is the only place they exist; `Saves/` and `GameState/` are pruned
    # (see the docstring).
    parsek_src = os.path.join(RECORDINGS_DIR, "Parsek")
    parsek_dst = os.path.join(target_dir, "Parsek")
    if not os.path.isdir(parsek_src):
        print("FAIL: the produced save carries no Parsek/ sidecar directory")
        return 1
    shutil.rmtree(parsek_dst, ignore_errors=True)
    shutil.copytree(parsek_src, parsek_dst,
                    ignore=shutil.ignore_patterns(*PRUNED_PARSEK_SUBDIRS))

    # File-tree post-conditions, checked here rather than in `verify` (which is
    # pure over the save text): every `.prec` needs its readable `.prec.txt`
    # mirror, and no regenerable `.craft.txt` snapshot may come along.
    recordings = os.path.join(parsek_dst, "Recordings")
    names = os.listdir(recordings) if os.path.isdir(recordings) else []
    precs = sorted(n for n in names if n.endswith(".prec"))
    missing = sorted(n for n in precs if (n + ".txt") not in names)
    snapshots = sorted(n for n in names
                       if n.endswith(("_vessel.craft.txt", "_ghost.craft.txt")))
    if len(precs) != EXPECT_RECORDING_COUNT or missing or snapshots:
        if len(precs) != EXPECT_RECORDING_COUNT:
            print("FAIL: expected %d .prec sidecars, found %d: %s"
                  % (EXPECT_RECORDING_COUNT, len(precs), precs))
        if missing:
            print("FAIL: these trajectories have no .prec.txt mirror: %s" % (missing,))
        if snapshots:
            print("FAIL: forbidden snapshot mirrors came along: %s" % (snapshots,))
        return 1

    # AddOns is the harness-instance mod settings every committed harness fixture
    # carries; it comes from the HOST, which is itself a harness fixture.
    addons_src = os.path.join(host_dir, "AddOns")
    addons_dst = os.path.join(target_dir, "AddOns")
    if os.path.isdir(addons_src):
        shutil.rmtree(addons_dst, ignore_errors=True)
        shutil.copytree(addons_src, addons_dst)

    print("OK: wrote %s (host=%s recordings=C2CareerPostFix crew=%s)"
          % (target_dir, HOST_NAME, args.crew))
    return 0


if __name__ == "__main__":
    sys.exit(main())
