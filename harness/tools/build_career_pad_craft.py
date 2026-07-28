#!/usr/bin/env python3
"""Build the `career-pad-craft` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. Every flyable fixture in the repo is SANDBOX, and the only two
CAREER templates (`fresh-career`, `fresh-science`) carry ZERO `VESSEL` nodes.
That is roadmap item R11 ("a CAREER fixture with a flyable craft"), and it is
what blocks any scenario whose subject is a career-ledger consequence of a
FLIGHT rather than of a KSC button. The roadmap proposed closing it with a
`FORGE-career-pad` FLIGHT. This tool closes it without one: the two inputs are
already committed, already proven, and the splice between them is mechanical.

WHAT IT SPLICES. Exactly three edits against the `fresh-career` SAVE, plus a
`persistent.loadmeta` restamp (vessel count + UT only - every other loadmeta
field is left alone because the splice moves no career pool):

  1. `fresh-career`'s empty `FLIGHTSTATE` is replaced by `b1-pad-craft`'s, with
     every non-`Ship` `VESSEL` dropped. On `b1-pad-craft` that drops one
     `type = SpaceObject` asteroid (`Ast. OTI-107`) and keeps the stock
     "Jumping Flea" (mk1pod.v2 + parachuteSingle + 2x GooExperiment +
     solidBooster.sm.v2 + fins) on the LaunchPad at `sit = PRELAUNCH`. The
     asteroid is dropped for two reasons: `fresh-career`'s own
     `DiscoverableObjects` scenario never registered it, and a second tracked
     vessel is a free variable in any consumer's `expectations.recordings.count`.
     `activeVessel` is re-indexed onto the surviving ship.
  2. The base `ROSTER`'s entry for the crew kerbal is replaced by
     `b1-pad-craft`'s entry for the same kerbal, so the roster row is the
     `state = Assigned` one KSP ITSELF wrote alongside this exact vessel rather
     than a hand-edited `Available` row.
  3. The donor's INERT `SCENARIO{name=ParsekScenario}` node is copied in.
     LOAD-BEARING - without it the seam's FLIGHT focus route never creates the
     ScenarioModule, so `OnSave` never runs and a whole flight is recorded in
     memory and thrown away. CL-1 flight 1 proved it. See the call site.

The CRAFT ITSELF IS COPIED VERBATIM - same `pid`, same `persistentId`, same
parts, same `stg = 2`, same `automateSafeDeploy = 0` on the chute. A consumer
that wants to reason from `b1-pad-craft`'s measured flight profile can, because
it is the same craft, byte for byte.

WHAT IS NOT TOUCHED. Every career surface comes from `fresh-career` unchanged:
`Mode = CAREER`, `Funding` 500000, `ResearchAndDevelopment` 100,
`Reputation` 0, all 10 facilities at level 0, the `start` tech node, the
4-kerbal roster plus the `Verhat Kerman` applicant, and the DIFFICULTY block -
including `MissingCrewsRespawn = False`, which is load-bearing for the crew-loss
lane (see the fixtures README).

The career parts requirement is satisfied without touching the tech tree: a
persisted `VESSEL` node loads regardless of unlock state, and every part on this
craft is in the stock `start` node anyway.

Usage:
    python harness/tools/build_career_pad_craft.py            # write the fixture
    python harness/tools/build_career_pad_craft.py --check    # verify only, no write

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture, so
CI or a reviewer can confirm the committed bytes are what this recipe produces
without regenerating them. It is WIRED, not decorative: `FixtureDriftTests` in
`harness/missions/lib/test_cl1_crew_loss.py` runs the same `verify` in-process and
additionally re-runs `build` over the CURRENT inputs and asserts byte-identity
with the committed save, so a change to either upstream fixture reds in the suite
rather than drifting silently into a live flight.

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
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

BASE_NAME = "fresh-career"
DONOR_NAME = "b1-pad-craft"
TARGET_NAME = "career-pad-craft"
CREW_NAME = "Jebediah Kerman"

# Post-conditions asserted on the produced (or committed) save. Each is a fact a
# consumer scenario depends on; a violated one aborts rather than shipping a
# fixture whose consumer would red ten minutes into a flight.
EXPECT_MODE = "CAREER"
EXPECT_SITUATION = "PRELAUNCH"
EXPECT_FUNDS = "500000"
EXPECT_SCIENCE = "100"
EXPECT_REPUTATION = "0"


# ---------------------------------------------------------------------------
# Minimal ConfigNode-text helpers. The .sfs is tab-indented CRLF text; every
# operation here is line-based and depth-tracked, so nothing is re-serialized
# and the copied craft stays byte-identical.
# ---------------------------------------------------------------------------


def read_lines(path: str) -> List[str]:
    """Read a CRLF .sfs into a list of lines with the newlines stripped."""
    with open(path, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    return text.replace("\r\n", "\n").split("\n")


def write_lines(path: str, lines: List[str]) -> None:
    """Write lines back as CRLF, matching every committed fixture in the repo."""
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write("\r\n".join(lines))


def find_node(lines: List[str], name: str, start: int = 0,
              stop: Optional[int] = None) -> Optional[Tuple[int, int]]:
    """(header_index, end_index_exclusive) of the first node called ``name``.

    A node is a header line whose stripped text is exactly ``name`` followed by
    an opening brace; the end is its matching close brace. Returns None when no
    such node begins at/after ``start`` (and before ``stop``, when given)."""
    limit = len(lines) if stop is None else min(stop, len(lines))
    i = start
    while i < limit:
        if lines[i].strip() == name and i + 1 < limit and lines[i + 1].strip() == "{":
            depth = 0
            j = i + 1
            while j < len(lines):
                s = lines[j].strip()
                if s == "{":
                    depth += 1
                elif s == "}":
                    depth -= 1
                    if depth == 0:
                        return (i, j + 1)
                j += 1
            return None
        i += 1
    return None


def child_nodes(lines: List[str], node: Tuple[int, int],
                name: str) -> List[Tuple[int, int]]:
    """Every DIRECT child node called ``name`` inside ``node``.

    Depth-scoped on purpose: a bare scan for `VESSEL` would also match a nested
    one, and a bare scan for `KERBAL` inside `ROSTER` must not descend into a
    kerbal's own inventory nodes."""
    start, end = node
    out: List[Tuple[int, int]] = []
    i = start + 2  # past the header + its opening brace
    depth = 0
    while i < end - 1:
        s = lines[i].strip()
        if s == "{":
            depth += 1
        elif s == "}":
            depth -= 1
        elif depth == 0 and s == name and i + 1 < end and lines[i + 1].strip() == "{":
            found = find_node(lines, name, i, end)
            if found is None:
                break
            out.append(found)
            i = found[1]
            continue
        i += 1
    return out


def get_value(lines: List[str], node: Tuple[int, int], key: str) -> Optional[str]:
    """The first DIRECT `key = value` inside ``node`` (depth 0), or None."""
    start, end = node
    prefix = key + " ="
    i = start + 2
    depth = 0
    while i < end - 1:
        s = lines[i].strip()
        if s == "{":
            depth += 1
        elif s == "}":
            depth -= 1
        elif depth == 0 and s.startswith(prefix):
            return s[len(prefix):].strip()
        i += 1
    return None


def set_value(lines: List[str], node: Tuple[int, int], key: str, value: str) -> bool:
    """Rewrite the first DIRECT `key = ...` inside ``node``, preserving indent."""
    start, end = node
    prefix = key + " ="
    i = start + 2
    depth = 0
    while i < end - 1:
        s = lines[i].strip()
        if s == "{":
            depth += 1
        elif s == "}":
            depth -= 1
        elif depth == 0 and s.startswith(prefix):
            indent = lines[i][:len(lines[i]) - len(lines[i].lstrip("\t"))]
            lines[i] = "%s%s = %s" % (indent, key, value)
            return True
        i += 1
    return False


def set_top_value(lines: List[str], key: str, value: str) -> bool:
    """Rewrite a GAME-level `key = ...` (single-tab indent) line."""
    prefix = "\t%s = " % key
    for i, line in enumerate(lines):
        if line.startswith(prefix):
            lines[i] = "%s%s" % (prefix, value)
            return True
    return False


def contains_line(lines: List[str], node: Tuple[int, int], text: str) -> bool:
    """True iff any line inside ``node`` strips to exactly ``text``."""
    return any(lines[i].strip() == text for i in range(node[0], node[1]))


# ---------------------------------------------------------------------------
# The splice.
# ---------------------------------------------------------------------------


def build(base_lines: List[str], donor_lines: List[str],
          crew_name: str, title: str) -> List[str]:
    """Return the spliced save. Pure over the two input line lists."""
    base = list(base_lines)

    donor_fs = find_node(donor_lines, "FLIGHTSTATE")
    if donor_fs is None:
        raise SystemExit("donor has no FLIGHTSTATE node")
    fs = donor_lines[donor_fs[0]:donor_fs[1]]

    # Drop every non-Ship vessel, then re-index activeVessel onto the survivor.
    fs_node = (0, len(fs))
    vessels = child_nodes(fs, fs_node, "VESSEL")
    keep = [v for v in vessels if get_value(fs, v, "type") == "Ship"]
    if len(keep) != 1:
        raise SystemExit("expected exactly one type=Ship VESSEL in the donor, got %d"
                         % len(keep))
    drop = [v for v in vessels if v not in keep]
    for start, end in sorted(drop, reverse=True):
        del fs[start:end]
    fs_node = (0, len(fs))
    vessels = child_nodes(fs, fs_node, "VESSEL")
    if len(vessels) != 1:
        raise SystemExit("post-prune vessel count is %d, expected 1" % len(vessels))
    if not set_value(fs, fs_node, "activeVessel", "0"):
        raise SystemExit("could not re-index activeVessel")

    base_fs = find_node(base, "FLIGHTSTATE")
    if base_fs is None:
        raise SystemExit("base has no FLIGHTSTATE node")
    base[base_fs[0]:base_fs[1]] = fs

    # Roster: swap the crew kerbal's row for the donor's Assigned one.
    base_roster = find_node(base, "ROSTER")
    donor_roster = find_node(donor_lines, "ROSTER")
    if base_roster is None or donor_roster is None:
        raise SystemExit("a ROSTER node is missing")
    donor_kerbal = _kerbal_named(donor_lines, donor_roster, crew_name)
    base_kerbal = _kerbal_named(base, base_roster, crew_name)
    if donor_kerbal is None:
        raise SystemExit("donor roster has no %s" % crew_name)
    if base_kerbal is None:
        raise SystemExit("base roster has no %s" % crew_name)
    base[base_kerbal[0]:base_kerbal[1]] = donor_lines[donor_kerbal[0]:donor_kerbal[1]]

    # 3. The INERT ParsekScenario SCENARIO node, copied verbatim from the donor.
    #
    # LOAD-BEARING, and it cost a flight to learn. `fresh-career` deliberately
    # carries NO Parsek footprint, which is right for a KSC-only ledger fixture:
    # the four L1 career scripts enter through the seam's SPACECENTER route, and
    # `LoadGameImpl` writes persistent.sfs after `UpdateScenarioModules` there
    # (autotest-status known-gate 6), so the ScenarioModule gets created for them.
    # The FLIGHT focus route does not do that. CL-1 flight 1 (2026-07-28) flew the
    # whole profile correctly - 262 points, the destruction path, the kerbal dead on
    # disk - and produced ZERO recordings, because `ParsekScenario` was never added
    # to the loaded game: the collected KSP.log carries not one `[Scenario]` line
    # and the produced save carries no `ParsekScenario` node, so `OnSave` never ran
    # and nothing was ever written.
    #
    # EVERY fixture that has ever flown carries this node; the only ones without it
    # are the three never-flown `fresh-*` KSC templates. Copying the donor's is the
    # same discipline as copying its vessel: 7 inert lines (`scene`,
    # `missionHideArchived`, `gameStateEventCount`), no recordings, no trees, no
    # ledger state. `eva2-lko-crewed` keeps the equivalent node for the same reason,
    # and a POPULATED node is also what suppresses `PreParsekBackup` at load, so an
    # emptied one would be a different and untested shape.
    donor_scn = _scenario_node(donor_lines, "ParsekScenario")
    if donor_scn is None:
        raise SystemExit("donor has no ParsekScenario SCENARIO node to copy")
    if _scenario_node(base, "ParsekScenario") is None:
        anchor = _scenario_node(base, "ProgressTracking") or _scenario_node(base, "Funding")
        if anchor is None:
            raise SystemExit("base has no SCENARIO node to anchor the insert against")
        base[anchor[1]:anchor[1]] = donor_lines[donor_scn[0]:donor_scn[1]]

    set_top_value(base, "Title", title)
    return base


def _scenario_node(lines: List[str], scenario_name: str) -> Optional[Tuple[int, int]]:
    """The SCENARIO node whose `name = <scenario_name>`, or None."""
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == scenario_name:
            return node
        i = node[1]


def _kerbal_named(lines: List[str], roster: Tuple[int, int],
                  name: str) -> Optional[Tuple[int, int]]:
    for kerbal in child_nodes(lines, roster, "KERBAL"):
        if get_value(lines, kerbal, "name") == name:
            return kerbal
    return None


def build_loadmeta(base_meta: List[str], lines: List[str]) -> List[str]:
    """Restamp the base loadmeta's vessel count + UT from the spliced save.

    Every other field (gameMode, the pool readouts, the timestamp) stays as the
    base wrote it: the splice moves no pool, and the loadmeta is only the Load
    menu's preview."""
    out = list(base_meta)
    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        raise SystemExit("spliced save has no FLIGHTSTATE")
    count = len(child_nodes(lines, fs, "VESSEL"))
    ut = get_value(lines, fs, "UT") or "0"
    for i, line in enumerate(out):
        if line.startswith("vesselCount = "):
            out[i] = "vesselCount = %d" % count
        elif line.startswith("UT = "):
            out[i] = "UT = %s" % ut
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
    # The crew must actually be ABOARD: a `crew = <name>` line inside the vessel.
    if not contains_line(lines, ship, "crew = %s" % crew_name):
        problems.append("no `crew = %s` line inside the vessel" % crew_name)

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
                problems.append("roster %s is not type=Crew (kRPC GetKerbal scans "
                                "CrewRoster.Crew and would not find it)" % crew_name)

    # MissingCrewsRespawn=False is what makes the crew transition a ONE-step
    # Assigned -> Dead. A consumer pinning that log token would silently get the
    # two-step form if this ever flipped.
    if not any(line.strip() == "MissingCrewsRespawn = False" for line in lines):
        problems.append("MissingCrewsRespawn is not False")

    # Without the ScenarioModule node the FLIGHT focus route never creates
    # ParsekScenario, so OnSave never runs and the whole flight is recorded in
    # memory and thrown away. CL-1 flight 1 proved it: zero recordings.
    if _scenario_node(lines, "ParsekScenario") is None:
        problems.append("no ParsekScenario SCENARIO node: a FLIGHT-route run "
                        "would persist nothing (CL-1 flight 1, 2026-07-28)")

    for node_name, key, expected in (("Funding", "funds", EXPECT_FUNDS),
                                     ("ResearchAndDevelopment", "sci", EXPECT_SCIENCE),
                                     ("Reputation", "rep", EXPECT_REPUTATION)):
        got = _scenario_value(lines, node_name, key)
        if got != expected:
            problems.append("%s.%s is %r, expected %r" % (node_name, key, got, expected))
    return problems


def _scenario_value(lines: List[str], scenario_name: str, key: str) -> Optional[str]:
    """Read `key` out of the SCENARIO node whose `name = <scenario_name>`."""
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == scenario_name:
            return get_value(lines, node, key)
        i = node[1]


# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of writing it")
    parser.add_argument("--crew", default=CREW_NAME,
                        help="roster kerbal to place aboard (default: %s)" % CREW_NAME)
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

    base_dir = os.path.join(_SAVES, BASE_NAME)
    donor_dir = os.path.join(_SAVES, DONOR_NAME)
    for d in (base_dir, donor_dir):
        if not os.path.isdir(d):
            print("FAIL: missing input fixture %s" % d)
            return 1

    base_lines = read_lines(os.path.join(base_dir, "persistent.sfs"))
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
                build_loadmeta(read_lines(os.path.join(base_dir, "persistent.loadmeta")),
                               built))
    addons_src = os.path.join(base_dir, "AddOns")
    addons_dst = os.path.join(target_dir, "AddOns")
    if os.path.isdir(addons_src):
        shutil.rmtree(addons_dst, ignore_errors=True)
        shutil.copytree(addons_src, addons_dst)
    print("OK: wrote %s (base=%s donor=%s crew=%s)"
          % (target_dir, BASE_NAME, DONOR_NAME, args.crew))
    return 0


if __name__ == "__main__":
    sys.exit(main())
