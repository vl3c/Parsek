#!/usr/bin/env python3
"""Build the `career-earned-pad` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. The career-ledger lane's B.4 strict-per-identity gate needs a
subject with POPULATED per-identity facets, and it needs to run the in-game
`LedgerGroundTruth` category, which is `Scene = GameScenes.FLIGHT`. Exactly one
committed career carries those facets - the save harness run
`2026-08-19_2130_L3-career-science-recover` produced, committed as
`Source/Parsek.Tests/Fixtures/C2CareerPostFix/` - and it carries ZERO `VESSEL`
nodes, because the flight that earned it RECOVERED the craft. A save with no
vessel routes `LoadGame` to the NoVesselSpaceCenter path, so a FLIGHT-scene
batch scene-skips its only declaration: the exact vacuity defect B10 and
L1-passive-sandbox were re-flown to fix.

This tool closes that gap the same way `build_career_pad_craft.py` closed R11:
splice a focusable PRELAUNCH craft into a career that otherwise has none. The
difference is which half is precious. There the career was empty
(`fresh-career`) and the craft was the payload; HERE THE CAREER IS THE PAYLOAD -
14 ledger actions, three `ScienceEarning` rows, a vessel-recovery `FundsEarning`
credit, five milestones, a recorded crewed recovery and Jebediah's career log -
and the craft is only the key that opens the FLIGHT scene.

WHAT IT SPLICES. Four edits against the harvested career SAVE, plus a
`persistent.loadmeta` restamp (vessel count only - see `build_loadmeta`):

  1. `career-science-pad`'s single `type = Ship` `VESSEL` node is inserted into
     the base's own (vessel-less) `FLIGHTSTATE`. THE BASE'S `FLIGHTSTATE` IS
     KEPT, not replaced - which is the one place this builder deliberately
     departs from `build_career_pad_craft.py`'s whole-node swap. That save's
     `FLIGHTSTATE` was `fresh-career`'s and carried nothing; this one carries
     `UT = 408.72`, the clock the whole 14-action ledger was written against.
     Replacing it would rewind the career's clock to the donor's `UT = 9.06`,
     i.e. BEFORE every action in the ledger it is being spliced into.
  2. The spliced vessel's `pid` and `persistentId` are RE-STAMPED, and this is
     the load-bearing edit rather than hygiene. The donor craft IS the craft
     this career flew and recovered: its `pid` is
     `f77e42072e3d4c59b04581daba628b55` and its `persistentId` is `2905720181`,
     byte-identical to both recordings' `recordedVesselGuid` /
     `vesselPersistentId`. `LedgerGroundTruthDiff.CompareRecovery` treats a
     recovery credit whose vessel is STILL PRESENT in the save as a divergence:
     guid-corroborated it is ALWAYS HARD, pid-only it is report-only - which
     strict promotes to hard anyway. A verbatim splice would therefore red the
     armed run on a pure fixture artifact that looks exactly like a product
     defect. Both stamps are asserted non-colliding in `verify`.
  3. The vessel's `lct` / `lastUT` are moved to the base's `UT`, so the craft
     rolled out onto the pad at the career's current time rather than 400
     seconds before it.
  4. The `rewindSave = parsek_rw_*` hint is STRIPPED from every RECORDING node.
     `CommittedFixtureRewindSaveTests` pins both halves of one rule: no fixture
     may commit a `Parsek/Saves/parsek_rw_*.sfs` (harvest exhaust nothing
     automated reads), and none may carry a hint pointing at one that is not
     committed (a dangling reference `Inv9RewindPoint` WARNs on, and FAILs on
     when the owning recording is `CommittedProvisional` - which would turn the
     analyzer RED under the harness's Forbid fresh-save gate). The copy step
     below drops `Parsek/Saves/` for the first half; this edit is the second.

  5. Jebediah's ROSTER row has `state` flipped `Available` -> `Assigned`,
     because the spliced craft carries him. HIS ROW IS OTHERWISE UNTOUCHED, and
     that is deliberate: `build_career_pad_craft.py` swaps the whole row for the
     donor's, which HERE would delete his `CAREER_LOG` (`flight = 1`,
     `Land,Kerbin`, `Flight,Kerbin`, `Recover`) - the SAVE SIDE of the diff's
     `KerbalXp` facet. The reconstruction credits those entries off the ledger,
     so deleting them would manufacture a `PhantomInRecon` divergence: again
     report-only by default, again promoted by strict, again a fixture artifact
     wearing a defect's clothes.

WHAT IS CARRIED VERBATIM. Everything that makes the career a subject: the
`Parsek/` sidecar tree (both recordings, `ledger.pgld`, the GameState
baselines), the POPULATED `SCENARIO{name=ParsekScenario}` node with its single
committed `RECORDING_TREE`, all three earned pools (funds 536558, science
111.599998, reputation 1.99999881), the roster's six kerbals and the full
facility / tech / contract state. The donor contributes ONE `VESSEL` node and
its `AddOns/` directory, and nothing else.

WHY THE POPULATED ParsekScenario NODE IS NOT REPLACED by the donor's inert one:
it IS the payload. `build_career_pad_craft.py` copies the donor's because
`fresh-career` has none; here the base's node carries the committed tree the
recovery credit resolves through, and the tree is NOT active (`isActive` is
absent), so the in-game cell's "no pending / no active uncommitted tree" guard
still passes.

Usage:
    python harness/tools/build_career_earned_pad.py            # write the fixture
    python harness/tools/build_career_earned_pad.py --check    # verify only, no write

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture.
It is WIRED, not decorative: `CareerEarnedPadFixtureDriftTests` runs the same
`verify` in-process AND re-runs `build` over the CURRENT inputs asserting
byte-identity with the committed save, so a re-harvest of `C2CareerPostFix` or a
change to `career-science-pad` reds in the harness suite rather than drifting
silently into a live flight.

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
_HARNESS_ROOT = os.path.dirname(_HERE)
_REPO_ROOT = os.path.dirname(_HARNESS_ROOT)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text helpers, for the same reason
# build_career_science_pad.py imports them: a second implementation is a second
# thing to drift.
import build_career_pad_craft as base_builder  # noqa: E402

read_lines = base_builder.read_lines
write_lines = base_builder.write_lines
find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value
set_top_value = base_builder.set_top_value
contains_line = base_builder.contains_line

# The BASE is the xUnit fixture rather than a harness one, and that is
# deliberate: it is the ONE committed copy of the save run 2026-08-19_2130
# produced, and `C2CareerPostFixReplayTests` already makes the closes-to-zero
# claim about those exact bytes. Deriving from it keeps a single source of truth;
# the drift test is what makes the derivation enforced rather than assumed.
BASE_DIR = os.path.join(_REPO_ROOT, "Source", "Parsek.Tests", "Fixtures", "C2CareerPostFix")
DONOR_NAME = "career-science-pad"
TARGET_NAME = "career-earned-pad"
CREW_NAME = base_builder.CREW_NAME

# The re-stamped vessel identity. FIXED LITERALS, not generated: the fixture must
# be byte-reproducible so the drift test can compare against the committed bytes.
#
# Neither value is derived from the donor's - that is the whole point. `verify`
# asserts both against every `recordedVesselGuid` / `vesselPersistentId` the
# save's recordings carry, so a future re-harvest that happened to reuse one of
# these reds rather than shipping a colliding fixture.
NEW_VESSEL_GUID = "3d90c5b1a47e4f28b6c1e0d752843af6"
NEW_VESSEL_PERSISTENT_ID = "1846203977"

# Post-conditions on the produced (or committed) save. The three pools are the
# EARNED ones, not the seed - a fixture that came out at 500000 / 100 / 0 would
# mean the splice had dropped the career payload it exists to carry.
EXPECT_MODE = "CAREER"
EXPECT_SITUATION = "PRELAUNCH"
EXPECT_FUNDS = "536558"
EXPECT_SCIENCE = "111.599998"
EXPECT_REPUTATION = "1.99999881"

# The recorded launch this career flew and recovered. Held as constants so
# `verify` states the collision it is guarding against rather than merely
# computing it, and so a re-harvest that changed them reds loudly.
RECORDED_VESSEL_GUID = "f77e42072e3d4c59b04581daba628b55"
RECORDED_VESSEL_PERSISTENT_ID = "2905720181"

# Jebediah's save-side career log, the KerbalXp facet's ground truth. Asserted
# present because the roster edit below is one `set_value` away from the
# whole-row swap that would delete it.
EXPECT_CAREER_LOG_ENTRY = "0 = Recover"

# The Rewind-to-LAUNCH hint, matched exactly as CommittedFixtureRewindSaveTests
# matches it so the strip and the gate cannot drift apart.
_REWIND_HINT_RE = re.compile(r"^\s*rewindSave = parsek_rw_\w+\s*$")

# Harvest exhaust the fixture must not carry (same cell, first half).
PRUNED_PARSEK_SUBDIRS = ("Saves",)


# ---------------------------------------------------------------------------
# The splice.
# ---------------------------------------------------------------------------


def build(base_lines: List[str], donor_lines: List[str],
          crew_name: str, title: str) -> List[str]:
    """Return the spliced save. Pure over the two input line lists."""
    base = list(base_lines)

    # ---- the donor's single Ship vessel, extracted and re-stamped ----
    donor_fs = find_node(donor_lines, "FLIGHTSTATE")
    if donor_fs is None:
        raise SystemExit("donor has no FLIGHTSTATE node")
    donor_vessels = child_nodes(donor_lines, donor_fs, "VESSEL")
    ships = [v for v in donor_vessels
             if get_value(donor_lines, v, "type") == "Ship"]
    if len(ships) != 1:
        raise SystemExit("expected exactly one type=Ship VESSEL in the donor, got %d"
                         % len(ships))
    vessel = list(donor_lines[ships[0][0]:ships[0][1]])
    vessel_node = (0, len(vessel))

    if get_value(vessel, vessel_node, "pid") != RECORDED_VESSEL_GUID:
        raise SystemExit(
            "donor vessel pid is %r, expected the recorded launch's %r - the "
            "re-stamp below is sized against that identity, so an unexpected "
            "one means the donor moved and this recipe must be re-derived"
            % (get_value(vessel, vessel_node, "pid"), RECORDED_VESSEL_GUID))
    if get_value(vessel, vessel_node, "persistentId") != RECORDED_VESSEL_PERSISTENT_ID:
        raise SystemExit(
            "donor vessel persistentId is %r, expected %r (see above)"
            % (get_value(vessel, vessel_node, "persistentId"),
               RECORDED_VESSEL_PERSISTENT_ID))

    if not set_value(vessel, vessel_node, "pid", NEW_VESSEL_GUID):
        raise SystemExit("could not re-stamp the vessel pid")
    if not set_value(vessel, vessel_node, "persistentId", NEW_VESSEL_PERSISTENT_ID):
        raise SystemExit("could not re-stamp the vessel persistentId")

    # ---- insert into the BASE's own FLIGHTSTATE, keeping its clock ----
    base_fs = find_node(base, "FLIGHTSTATE")
    if base_fs is None:
        raise SystemExit("base has no FLIGHTSTATE node")
    if child_nodes(base, base_fs, "VESSEL"):
        raise SystemExit("base FLIGHTSTATE already carries a VESSEL - this recipe "
                         "assumes the recovered-craft shape and would double it")
    base_ut = get_value(base, base_fs, "UT")
    if not base_ut:
        raise SystemExit("base FLIGHTSTATE has no UT")

    # The craft rolls out NOW, not at the donor's launch time.
    set_value(vessel, vessel_node, "lct", base_ut)
    set_value(vessel, vessel_node, "lastUT", base_ut)

    # Indent the donor's lines are already at (FLIGHTSTATE child depth) matches
    # the base's, both being GAME > FLIGHTSTATE > VESSEL, so the block is
    # inserted verbatim before the FLIGHTSTATE close brace.
    insert_at = base_fs[1] - 1
    base[insert_at:insert_at] = vessel

    base_fs = find_node(base, "FLIGHTSTATE")
    if len(child_nodes(base, base_fs, "VESSEL")) != 1:
        raise SystemExit("post-splice vessel count is not 1")
    if not set_value(base, base_fs, "activeVessel", "0"):
        raise SystemExit("could not set activeVessel")

    # ---- strip the rewind-to-LAUNCH hints (the payload is not committed) ----
    base = [line for line in base
            if not _REWIND_HINT_RE.match(line)]

    # ---- roster: flip the crew kerbal Available -> Assigned, nothing else ----
    roster = find_node(base, "ROSTER")
    if roster is None:
        raise SystemExit("base has no ROSTER node")
    kerbal = _kerbal_named(base, roster, crew_name)
    if kerbal is None:
        raise SystemExit("base roster has no %s" % crew_name)
    if not set_value(base, kerbal, "state", "Assigned"):
        raise SystemExit("could not set %s state" % crew_name)

    set_top_value(base, "Title", title)
    return base


def _kerbal_named(lines: List[str], roster: Tuple[int, int],
                  name: str) -> Optional[Tuple[int, int]]:
    for kerbal in child_nodes(lines, roster, "KERBAL"):
        if get_value(lines, kerbal, "name") == name:
            return kerbal
    return None


def build_loadmeta(base_meta: List[str], lines: List[str]) -> List[str]:
    """Restamp the base loadmeta's vessel count from the spliced save.

    The UT is NOT restamped, unlike `build_career_pad_craft.build_loadmeta`:
    there the spliced FLIGHTSTATE brought a new clock with it, here the base's
    own clock is kept, so the base loadmeta's UT is already correct. Every other
    field (gameMode, the pool readouts, the timestamp) stays as the harvest
    wrote it: the splice moves no pool."""
    out = list(base_meta)
    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        raise SystemExit("spliced save has no FLIGHTSTATE")
    count = len(child_nodes(lines, fs, "VESSEL"))
    for i, line in enumerate(out):
        if line.startswith("vesselCount = "):
            out[i] = "vesselCount = %d" % count
    return out


# ---------------------------------------------------------------------------
# Post-conditions. Run on both the freshly built save and on --check.
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
        problems.append("no `crew = %s` line inside the vessel" % crew_name)

    # ---- THE COLLISION GUARDS, and they are the reason this builder exists ----
    #
    # `LedgerGroundTruthDiff.CompareRecovery` correlates every recovery credit
    # against the save's live vessels: a guid match is an ALWAYS-HARD consistency
    # divergence, a pid-only match is report-only and therefore promoted by the
    # strict mode this fixture is built to arm. The donor craft IS the recovered
    # one, so both stamps must differ from every recorded identity in the save.
    vessel_guid = get_value(lines, ship, "pid")
    vessel_pid = get_value(lines, ship, "persistentId")
    recorded_guids = _values_named(lines, "recordedVesselGuid")
    recorded_pids = _values_named(lines, "vesselPersistentId")
    if not recorded_guids:
        problems.append("no recordedVesselGuid in the save: the career payload "
                        "(the committed RECORDING_TREE) did not survive the splice")
    if vessel_guid in recorded_guids:
        problems.append(
            "spliced vessel pid %r collides with a recording's recordedVesselGuid: "
            "CompareRecovery would raise a guid-corroborated ALWAYS-HARD "
            "consistency divergence" % vessel_guid)
    if vessel_pid in recorded_pids:
        problems.append(
            "spliced vessel persistentId %r collides with a recording's "
            "vesselPersistentId: CompareRecovery would raise a pid-only "
            "consistency divergence, which strict promotes to hard" % vessel_pid)

    dangling = [i for i, line in enumerate(lines, 1) if _REWIND_HINT_RE.match(line)]
    if dangling:
        problems.append(
            "a rewindSave = parsek_rw_* hint survived at line(s) %s: the payload is "
            "not committed, so Inv9RewindPoint would WARN on a dangling reference "
            "(and FAIL on a CommittedProvisional owner, reddening the analyzer under "
            "the harness Forbid gate)" % (dangling,))

    # The clock must still be the career's own, not the donor's.
    ut = get_value(lines, fs, "UT")
    end_uts = [float(v) for v in _values_named(lines, "explicitEndUT")]
    if not end_uts:
        problems.append("no explicitEndUT in the save: no recording survived")
    elif ut is None or float(ut) < max(end_uts):
        problems.append("FLIGHTSTATE UT %r is before the last recording's "
                        "explicitEndUT %r: the donor's clock replaced the career's"
                        % (ut, max(end_uts)))

    # ---- the career payload ----
    scn = _scenario_node(lines, "ParsekScenario")
    if scn is None:
        problems.append("no ParsekScenario SCENARIO node: the career payload is gone")
    elif not contains_line(lines, scn, "RECORDING_TREE"):
        problems.append("ParsekScenario carries no RECORDING_TREE: the recovery "
                        "credit would resolve no recording")
    elif contains_line(lines, scn, "isActive = True"):
        problems.append("the committed tree is marked isActive: the in-game cell's "
                        "no-active-uncommitted-tree guard would Skip the batch")

    roster = find_node(lines, "ROSTER")
    if roster is None:
        problems.append("no ROSTER node")
    else:
        kerbal = _kerbal_named(lines, roster, crew_name)
        if kerbal is None:
            problems.append("roster has no %s" % crew_name)
        else:
            state = get_value(lines, kerbal, "state")
            if state != "Assigned":
                problems.append("roster %s state is %r, expected 'Assigned'"
                                % (crew_name, state))
            if get_value(lines, kerbal, "type") != "Crew":
                problems.append("roster %s is not type=Crew" % crew_name)
            # The KerbalXp facet's SAVE side. Its loss is what the roster edit
            # was deliberately narrowed to avoid.
            if not contains_line(lines, kerbal, EXPECT_CAREER_LOG_ENTRY):
                problems.append(
                    "roster %s lost its CAREER_LOG %r: the KerbalXp facet would "
                    "read PhantomInRecon, a fixture artifact strict promotes to hard"
                    % (crew_name, EXPECT_CAREER_LOG_ENTRY))

    for node_name, key, expected in (("Funding", "funds", EXPECT_FUNDS),
                                     ("ResearchAndDevelopment", "sci", EXPECT_SCIENCE),
                                     ("Reputation", "rep", EXPECT_REPUTATION)):
        got = _scenario_value(lines, node_name, key)
        if got != expected:
            problems.append("%s.%s is %r, expected the EARNED %r"
                            % (node_name, key, got, expected))
    return problems


def _values_named(lines: List[str], key: str) -> List[str]:
    """Every `key = value` in the whole save, at any depth."""
    prefix = key + " ="
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
        i = node[1]


def _scenario_value(lines: List[str], scenario_name: str, key: str) -> Optional[str]:
    node = _scenario_node(lines, scenario_name)
    return get_value(lines, node, key) if node is not None else None


# ---------------------------------------------------------------------------


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

    donor_dir = os.path.join(_SAVES, DONOR_NAME)
    for d in (BASE_DIR, donor_dir):
        if not os.path.isdir(d):
            print("FAIL: missing input fixture %s" % d)
            return 1

    base_lines = read_lines(os.path.join(BASE_DIR, "persistent.sfs"))
    donor_lines = read_lines(os.path.join(donor_dir, "persistent.sfs"))
    title = "%s (CAREER)" % args.target_name
    built = build(base_lines, donor_lines, args.crew, title)

    problems = verify(built, args.crew)
    if problems:
        for p in problems:
            print("FAIL: %s" % p)
        return 1

    os.makedirs(target_dir, exist_ok=True)
    write_lines(target_sfs, built)
    write_lines(os.path.join(target_dir, "persistent.loadmeta"),
                build_loadmeta(read_lines(os.path.join(BASE_DIR, "persistent.loadmeta")),
                               built))

    # The career payload: recordings, ledger, GameState baselines. Copied from
    # the BASE, because that is the half this fixture exists to carry.
    parsek_src = os.path.join(BASE_DIR, "Parsek")
    parsek_dst = os.path.join(target_dir, "Parsek")
    if not os.path.isdir(parsek_src):
        print("FAIL: base carries no Parsek/ sidecar directory")
        return 1
    shutil.rmtree(parsek_dst, ignore_errors=True)
    # `Saves/` is Rewind-to-LAUNCH harvest exhaust: nothing automated reads it and
    # CommittedFixtureRewindSaveTests forbids committing it. Not copying is the
    # whole of the prune - no fixture file is ever removed.
    shutil.copytree(parsek_src, parsek_dst,
                    ignore=shutil.ignore_patterns(*PRUNED_PARSEK_SUBDIRS))

    # POST-CONDITION ON THE PAYLOAD, checked here rather than in `verify` because
    # `verify` is pure over the save TEXT and this is a file-tree fact. Every
    # `.prec` needs its readable `.prec.txt` mirror (a live test input
    # OptimizerTransferCohesionTests globs), and no `.craft.txt` snapshot mirror
    # may come along (regenerable, and forbidden by the same test class).
    recordings = os.path.join(parsek_dst, "Recordings")
    names = os.listdir(recordings) if os.path.isdir(recordings) else []
    missing = sorted(n for n in names
                     if n.endswith(".prec") and (n + ".txt") not in names)
    snapshots = sorted(n for n in names
                       if n.endswith(("_vessel.craft.txt", "_ghost.craft.txt")))
    if missing or snapshots:
        if missing:
            print("FAIL: these trajectories have no .prec.txt mirror (add them to "
                  "the BASE fixture, which is where the derivation reads them): %s"
                  % (missing,))
        if snapshots:
            print("FAIL: the base carries forbidden snapshot mirrors: %s" % (snapshots,))
        return 1

    # AddOns comes from the DONOR: it is the harness-instance mod settings every
    # committed harness fixture carries, and the xUnit base has none.
    addons_src = os.path.join(donor_dir, "AddOns")
    addons_dst = os.path.join(target_dir, "AddOns")
    if os.path.isdir(addons_src):
        shutil.rmtree(addons_dst, ignore_errors=True)
        shutil.copytree(addons_src, addons_dst)

    print("OK: wrote %s (base=C2CareerPostFix donor=%s crew=%s)"
          % (target_dir, DONOR_NAME, args.crew))
    return 0


if __name__ == "__main__":
    sys.exit(main())
