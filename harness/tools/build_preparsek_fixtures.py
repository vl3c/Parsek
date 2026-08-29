#!/usr/bin/env python3
"""Build the two `preparsek-*` save fixtures BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. The pre-Parsek safety backup fires on the first cold
`ParsekScenario.OnLoad` of a save that has NO Parsek footprint, and skips a
brand-new empty career. Automating that (docs/dev/todo-and-known-bugs.md,
"Pre-Parsek save safety backup") needs one fixture on each side of that gate, and
`fixtures/saves/` had NEITHER:

  - Every committed career with real progress (`career-earned-pad`,
    `career-pad-craft`, ...) was HARVESTED from a Parsek run, so it carries a
    populated `SCENARIO{name=ParsekScenario}` node - `HasParsekGameplayFootprint`
    reads that as "already touched" and the gate skips
    `reason=already-parsek-footprint`.
  - Every footprint-FREE career (`fresh-career`, `fresh-sandbox`, `fresh-science`,
    `strategy-career`) is brand-new empty by construction - zero VESSEL nodes,
    an empty `ProgressTracking > Progress`, no CONTRACT and no per-subject
    Science - so `IsBrandNewEmptySave` is true and the gate skips
    `reason=brand-new-empty`.

So no committed fixture could ever reach `reason=eligible`, and the backup path
had never run under the harness at all. (The prior-backup reap in
`run.py::stage_fixture` was written for the `fresh-*` saves on the assumption they
DO trigger it; it is harmless, and it is what keeps this lane's own backups from
accumulating across runs.)

WHAT IT BUILDS.

  `preparsek-untouched-career`  <- `career-earned-pad`
      A career with real progress (a PRELAUNCH pad craft, contracts, per-subject
      science, a crewed CAREER_LOG) carrying no Parsek GAMEPLAY state, i.e. what a
      player's career looks like the moment Parsek first opens it. Three edits and
      one omission:
        1. the `SCENARIO{name=ParsekScenario}` node is REDUCED TO ITS INERT FORM -
           `name` and `scene` only, the two values a freshly created
           `ProtoScenarioModule` carries - rather than deleted.

           BOTH HALVES OF THAT ARE LOAD-BEARING, AND THE FIRST ONE ALREADY COST A
           FLIGHT ONCE (CL-1 flight 1, quoted at length in
           `build_career_pad_craft.py`). KEPT, because this fixture is FOCUSABLE:
           the seam's FLIGHT route (`LoadGameImpl`'s focusable branch) goes
           straight to `FlightDriver.StartAndFocusVessel` with NO
           `UpdateScenarioModules` and no `SaveGame`, so a save with no
           ParsekScenario NODE boots with no ParsekScenario MODULE - `OnLoad` never
           runs and `MaybeBackupOnFirstColdContact` can never fire. That is why
           every flyable fixture in the corpus carries the node and only the
           vessel-less `fresh-*` KSC templates do not (`test_saveparse.py`'s
           EXPECTED_SCENARIO_PRESENCE, and the cell that now GATES that rule).
           REDUCED, because the donor's node is POPULATED (4 values + a
           RECORDING_TREE child) and `HasParsekGameplayFootprint` reads that as
           "already touched" -> `reason=already-parsek-footprint`, which is exactly
           what must not happen here. The inert form sits under that predicate's
           `nodes.Count == 0 && values.Count <= 2` floor.

           IT IS ALSO THE MORE FAITHFUL SHAPE, not a compromise. A real player's
           pre-Parsek career acquires precisely this node the moment Parsek is
           installed and the save is opened, because KSP creates it via
           AddToAllGames - and `PreParsekBackup.cs`'s own class comment says so:
           the captured file is gameplay-pristine but NOT byte-identical, because
           that empty node is there. The fixture reproduces the state the code was
           written for rather than a state no player ever has.
        2. the `PARAMETERS > ParsekSettings` node is deleted. It is NOT read by
           `HasParsekGameplayFootprint`, so leaving it would not change the gate -
           it is deleted because a pre-Parsek save does not have one (no `fresh-*`
           fixture does), and because the harvested copy still carries three keys
           the 2026-08-27 settings simplification deleted.
        3. `Title` is restamped to the new folder name, as every other builder does.
        4. the `Parsek/` sidecar tree is NOT copied. That directory IS a footprint
           on its own (`parsekSubdirExists`), so copying it would defeat the point.

  `preparsek-brandnew-career`   <- `fresh-career`
      The brand-new-empty control, needed as its OWN leaf rather than reusing
      `fresh-career`: two specs that share a `saveTemplate` leaf share one staged
      save directory in the instance, and a sibling run can overwrite a finished
      run's produced save (the produced-save clobber race). Only edit: the `Title`
      restamp. The two Parsek-node steps run over it as well and MUST find nothing,
      which is asserted - the negative control proving the reduction above acted on
      something real. It keeps NO ParsekScenario node, and that is correct rather
      than an oversight: it is VESSEL-LESS, so `DecideLoadRoute` takes the
      NoVesselSpaceCenter branch, where `LoadGameImpl` DOES call
      `UpdateScenarioModules` + `SaveGame` before `game.Start()` and KSP writes the
      inert node to disk itself. That is the shape B10 / R7c / L1 / M2 have all
      flown green from `fresh-career`.

VERIFYING, and the drift guard. `--check` re-runs the build over the committed
bases and diffs; `lib/test_preparsek_fixtures.py` wires that into the suite, so a
re-harvest of `career-earned-pad` (itself a derived fixture) reds locally instead
of leaving these two silently stale.

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
SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text helpers (the same import every sibling builder
# does): a second implementation is a second thing to drift.
import build_career_pad_craft as base_builder  # noqa: E402

read_lines = base_builder.read_lines
write_lines = base_builder.write_lines
find_node = base_builder.find_node
get_value = base_builder.get_value
set_top_value = base_builder.set_top_value

PARSEK_SCENARIO_NAME = "ParsekScenario"
PARSEK_SETTINGS_NODE = "ParsekSettings"

# (target, base, expects_parsek_scenario, expects_parsek_settings). The two
# `expects_*` flags are the negative control: the untouched fixture MUST have had a
# node to reduce and settings to delete, and the brand-new one MUST have neither, so
# a base swap that silently changed which is which reds instead of producing a
# fixture nobody meant. `expects_parsek_scenario` doubles as "this target KEEPS an
# inert node": the reduction only runs where a node exists, and a node is only needed
# where the fixture is focusable.
TARGETS: Tuple[Tuple[str, str, bool, bool], ...] = (
    ("preparsek-untouched-career", "career-earned-pad", True, True),
    ("preparsek-brandnew-career", "fresh-career", False, False),
)

# Files/dirs copied verbatim beside the rewritten persistent.sfs. `Parsek/` is
# deliberately absent (see the module docstring).
VERBATIM_FILES: Tuple[str, ...] = ("persistent.loadmeta",)
VERBATIM_DIRS: Tuple[str, ...] = ("AddOns",)


# ---------------------------------------------------------------------------
# Edits
# ---------------------------------------------------------------------------


def find_named_scenario(lines: List[str], name: str) -> Optional[Tuple[int, int]]:
    """(start, end) of the `SCENARIO` node whose direct `name = <name>` matches."""
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == name:
            return node
        i = node[1]


def scenario_value_lines(lines: List[str], node: Tuple[int, int],
                         key: str) -> List[str]:
    """Every DIRECT `key = ...` line inside ``node``, verbatim (indent included)."""
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
            out.append(lines[i])
    return out


def make_parsek_scenario_inert(lines: List[str]) -> Tuple[List[str], bool]:
    """Reduce `SCENARIO{name=ParsekScenario}` to its `name` + `scene` values.

    The node is KEPT (a focusable fixture whose save has no node boots with no
    module - see the module docstring) and EMPTIED (a populated node is a Parsek
    footprint, which is the thing under test). Every surviving line is copied
    VERBATIM from the donor, indentation included, so the result is whatever the
    base's own formatting is rather than this file's guess at it.
    """
    node = find_named_scenario(lines, PARSEK_SCENARIO_NAME)
    if node is None:
        return lines, False
    start, end = node
    header, brace, closer = lines[start], lines[start + 1], lines[end - 1]
    kept = scenario_value_lines(lines, node, "name") \
        + scenario_value_lines(lines, node, "scene")
    if len(kept) != 2:
        raise SystemExit(
            "the base's ParsekScenario node has %d name/scene value line(s), not 2; "
            "the inert form KSP writes is exactly those two, so this is a base whose "
            "shape changed and the derivation must be re-read, not patched" % len(kept))
    return lines[:start] + [header, brace] + kept + [closer] + lines[end:], True


def strip_parsek_settings(lines: List[str]) -> Tuple[List[str], bool]:
    node = find_node(lines, PARSEK_SETTINGS_NODE)
    if node is None:
        return lines, False
    return lines[:node[0]] + lines[node[1]:], True


def build_with_flags(base_lines: List[str], save_name: str) -> Tuple[List[str], bool, bool]:
    """The whole derivation, pure over the base's lines.

    Returns (lines, had_scenario_node, had_settings_node) so the caller can assert
    the base was the shape it was supposed to be. There is deliberately no second,
    flag-less `build()` wrapper: this file's own header warns that a second
    implementation is a second thing to drift, and one existed here briefly.
    """
    lines = list(base_lines)
    lines, had_scenario = make_parsek_scenario_inert(lines)
    lines, had_settings = strip_parsek_settings(lines)
    if not set_top_value(lines, "Title", "%s (CAREER)" % save_name):
        raise SystemExit("base save has no GAME-level Title line to restamp")
    return lines, had_scenario, had_settings


# ---------------------------------------------------------------------------
# Post-conditions
# ---------------------------------------------------------------------------


def verify(lines: List[str], save_name: str, expect_inert_node: bool) -> List[str]:
    """Every way the built save fails the pre-Parsek contract. Empty == good.

    The ParsekScenario expectation is TWO-SIDED and both sides are failures the
    lane has a name for: a node that is absent where one is wanted means the
    FLIGHT route never instantiates the module (CL-1 flight 1's zero-recording
    run), and a node that is present but POPULATED means
    `HasParsekGameplayFootprint` reads the save as already-touched and the backup
    is skipped. Only the inert middle satisfies both.
    """
    problems: List[str] = []
    node = find_named_scenario(lines, PARSEK_SCENARIO_NAME)
    if expect_inert_node:
        if node is None:
            problems.append(
                "%s: no SCENARIO{name=%s} node. This fixture is FOCUSABLE, and the "
                "seam's FLIGHT route calls StartAndFocusVessel with no "
                "UpdateScenarioModules, so with no node the ParsekScenario module is "
                "never instantiated, OnLoad never runs, and the backup can never fire"
                % (save_name, PARSEK_SCENARIO_NAME))
        else:
            start, end = node
            body = [lines[i].strip() for i in range(start + 2, end - 1)]
            extra = [b for b in body
                     if b and not b.startswith("name =") and not b.startswith("scene =")]
            if extra:
                problems.append(
                    "%s: the %s node is not INERT - %d line(s) beyond name/scene (%s). "
                    "HasParsekGameplayFootprint reads any child node, or any value past "
                    "name+scene, as a Parsek footprint and the backup would be skipped "
                    "with reason=already-parsek-footprint"
                    % (save_name, PARSEK_SCENARIO_NAME, len(extra), ", ".join(extra[:4])))
    elif node is not None:
        problems.append("%s: carries a SCENARIO{name=%s} node it was not meant to"
                        % (save_name, PARSEK_SCENARIO_NAME))

    if find_node(lines, PARSEK_SETTINGS_NODE) is not None:
        problems.append("%s: a %s node survived the strip" % (save_name, PARSEK_SETTINGS_NODE))
    expected_title = "\tTitle = %s (CAREER)" % save_name
    if expected_title not in lines:
        problems.append("%s: expected %r among the GAME-level lines" % (save_name, expected_title))
    return problems


def verify_tree(target_dir: str, save_name: str) -> List[str]:
    """Post-conditions on the built DIRECTORY (as opposed to the save text)."""
    problems: List[str] = []
    if os.path.isdir(os.path.join(target_dir, "Parsek")):
        problems.append("%s: a Parsek/ subdir exists; that alone is a footprint "
                        "(parsekSubdirExists) and suppresses the backup" % save_name)
    for leaf in VERBATIM_FILES:
        if not os.path.isfile(os.path.join(target_dir, leaf)):
            problems.append("%s: missing %s" % (save_name, leaf))
    if not os.path.isfile(os.path.join(target_dir, "persistent.sfs")):
        problems.append("%s: missing persistent.sfs" % save_name)
    return problems


# ---------------------------------------------------------------------------
# Shell
# ---------------------------------------------------------------------------


def build_one(target: str, base: str, expect_scenario: bool, expect_settings: bool,
              check_only: bool) -> List[str]:
    base_dir = os.path.join(SAVES, base)
    target_dir = os.path.join(SAVES, target)
    base_sfs = os.path.join(base_dir, "persistent.sfs")
    if not os.path.isfile(base_sfs):
        return ["%s: base save missing at %s" % (target, base_sfs)]

    built, had_scenario, had_settings = build_with_flags(read_lines(base_sfs), target)
    problems: List[str] = []
    if had_scenario != expect_scenario:
        problems.append("%s: base %s %s a ParsekScenario node (expected %s) - the base "
                        "changed shape, so this fixture is no longer the subject it was "
                        "committed as" % (target, base,
                                          "HAS" if had_scenario else "has NO",
                                          "one" if expect_scenario else "none"))
    if had_settings != expect_settings:
        problems.append("%s: base %s %s a ParsekSettings node (expected %s)"
                        % (target, base, "HAS" if had_settings else "has NO",
                           "one" if expect_settings else "none"))
    problems += verify(built, target, expect_inert_node=expect_scenario)

    target_sfs = os.path.join(target_dir, "persistent.sfs")
    if check_only:
        if not os.path.isfile(target_sfs):
            problems.append("%s: fixture is not committed at %s" % (target, target_sfs))
            return problems
        if read_lines(target_sfs) != built:
            problems.append("%s: the committed persistent.sfs is NOT byte-identical to a "
                            "fresh rebuild from %s - re-run "
                            "`python harness/tools/build_preparsek_fixtures.py`"
                            % (target, base))
        problems += verify_tree(target_dir, target)
        for leaf in VERBATIM_FILES:
            src, dst = os.path.join(base_dir, leaf), os.path.join(target_dir, leaf)
            if os.path.isfile(src) and (not os.path.isfile(dst)
                                        or open(src, "rb").read() != open(dst, "rb").read()):
                problems.append("%s: %s differs from the base's copy" % (target, leaf))
        return problems

    if problems:
        return problems
    if os.path.isdir(target_dir):
        shutil.rmtree(target_dir)
    os.makedirs(target_dir)
    write_lines(target_sfs, built)
    for leaf in VERBATIM_FILES:
        src = os.path.join(base_dir, leaf)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(target_dir, leaf))
    for leaf in VERBATIM_DIRS:
        src = os.path.join(base_dir, leaf)
        if os.path.isdir(src):
            shutil.copytree(src, os.path.join(target_dir, leaf))
    return verify_tree(target_dir, target)


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true",
                    help="verify the committed fixtures against a fresh rebuild; write nothing")
    args = ap.parse_args(argv)

    all_problems: List[str] = []
    for target, base, expect_scenario, expect_settings in TARGETS:
        problems = build_one(target, base, expect_scenario, expect_settings, args.check)
        all_problems += problems
        print("%-28s <- %-20s %s" % (target, base, "PROBLEMS" if problems else "OK"))
    for p in all_problems:
        print("  - %s" % p)
    return 1 if all_problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
