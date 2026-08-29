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
      science, a crewed CAREER_LOG) with EVERY Parsek trace removed, i.e. exactly
      what a player's career looks like the moment before they install Parsek.
      Three edits and one omission:
        1. the `SCENARIO{name=ParsekScenario}` node is deleted whole. Deleted,
           not emptied: a save that never met Parsek has no such node, and KSP
           re-injects an empty one (AddToAllGames) at load. The empty-node case
           is separately unit-covered by `PreParsekBackupTests`.
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
      restamp. The two strip steps run over it as well and MUST find nothing,
      which is asserted - that is the negative control proving the strip above
      removed something real.

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
# `expects_*` flags are the negative control: the untouched fixture MUST have had
# something to strip, and the brand-new one MUST NOT, so a base swap that silently
# changed which is which reds instead of producing a fixture nobody meant.
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


def strip_parsek_scenario(lines: List[str]) -> Tuple[List[str], bool]:
    node = find_named_scenario(lines, PARSEK_SCENARIO_NAME)
    if node is None:
        return lines, False
    return lines[:node[0]] + lines[node[1]:], True


def strip_parsek_settings(lines: List[str]) -> Tuple[List[str], bool]:
    node = find_node(lines, PARSEK_SETTINGS_NODE)
    if node is None:
        return lines, False
    return lines[:node[0]] + lines[node[1]:], True


def build(base_lines: List[str], save_name: str) -> List[str]:
    """The whole derivation, pure over the base's lines."""
    lines = list(base_lines)
    lines, _ = strip_parsek_scenario(lines)
    lines, _ = strip_parsek_settings(lines)
    if not set_top_value(lines, "Title", "%s (CAREER)" % save_name):
        raise SystemExit("base save has no GAME-level Title line to restamp")
    return lines


def build_with_flags(base_lines: List[str], save_name: str) -> Tuple[List[str], bool, bool]:
    lines = list(base_lines)
    lines, had_scenario = strip_parsek_scenario(lines)
    lines, had_settings = strip_parsek_settings(lines)
    if not set_top_value(lines, "Title", "%s (CAREER)" % save_name):
        raise SystemExit("base save has no GAME-level Title line to restamp")
    return lines, had_scenario, had_settings


# ---------------------------------------------------------------------------
# Post-conditions
# ---------------------------------------------------------------------------


def verify(lines: List[str], save_name: str) -> List[str]:
    """Every way the built save fails the pre-Parsek contract. Empty == good."""
    problems: List[str] = []
    if find_named_scenario(lines, PARSEK_SCENARIO_NAME) is not None:
        problems.append("%s: a SCENARIO{name=%s} node survived the strip; "
                        "HasParsekGameplayFootprint would read this save as "
                        "already-touched and the backup would never fire"
                        % (save_name, PARSEK_SCENARIO_NAME))
    if find_node(lines, PARSEK_SETTINGS_NODE) is not None:
        problems.append("%s: a %s node survived the strip" % (save_name, PARSEK_SETTINGS_NODE))
    for i, line in enumerate(lines):
        if line.strip() == "name = %s" % PARSEK_SCENARIO_NAME:
            problems.append("%s: line %d still names %s"
                            % (save_name, i + 1, PARSEK_SCENARIO_NAME))
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
    problems += verify(built, target)

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
