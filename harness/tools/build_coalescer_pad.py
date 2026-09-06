#!/usr/bin/env python3
"""Derive the committed `coalescer-pad` fixture from `gs1-two-stage-pad` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. The two in-game `Coalescer` cells (RuntimeTests.cs,
`ControlledChildBreakupSeed_LogsLiveResidualDecision` and
`ControlledChildBreakup_StampsParentAnchorContract`) start a recording, call
`StageManager.ActivateNextStage()` ONCE, and wait for a CONTROLLED child - a
command-bearing half coming off a decoupler. They read the controlled-child seed
decision off the log and the parent-anchor stamp off the tree. `gs1-two-stage-pad`
carries the right craft for that (mk1pod.v2 above a Decoupler.1, probeCoreOcto2.v2
plus tank plus liquidEngine2 below it - two ModuleCommand parts and one
ModuleDecouple), but its staging is the GS-1 flight's: stage 2 ignites the engine,
stage 1 fires the decoupler and the booster's chutes, stage 0 the pod chute. On the
pad the cells' one staging call therefore ignites the engine and separates nothing,
both cells `InGameAssert.Skip`, and H62's first census read
`total=2 passed=0 failed=0 skipped=2` (run 2026-09-06_2258) - a vacuous batch.

THE DERIVATION. Take gs1-two-stage-pad's persistent.sfs byte for byte and swap the
stage of exactly two parts of the `GS1 Auto-Chute Booster` VESSEL: `Decoupler.1`
moves from inverse stage 1 to 2 and `liquidEngine2` from 2 to 1, with their
stage-queue order (`sqor`) swapped the same way so the VAB staging list stays
consistent. `stg = 3` is untouched, so the first `ActivateNextStage()` on the pad
now fires stage 2 = the decoupler: the pod (root, stays the active vessel) parts
from the booster (probe core + tank + engine + chutes + fins), which is exactly
the controlled child the cells want, and the engine stays unlit. Nothing else in
the save changes - same asteroid, same kerbal, same scenario nodes - so a census
delta between H62 and any other gs1-hosted lane is attributable to the staging
alone.

WHAT IS NOT COPIED. `Ships/VAB/` and `AddOns/` are left out: this fixture hosts a
seam-driven isolated batch that launches nothing through kRPC, and copying crafts
in would only widen the anti-duplication sweep's surface. `persistent.loadmeta` is
copied because KSP's load menu reads it.

Run `python harness/tools/build_coalescer_pad.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_coalescer_pad.py` runs the check in-process so a hand edit to
either fixture reds in the unit suite rather than in a flight.
"""
from __future__ import annotations

import argparse
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs1-two-stage-pad")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "coalescer-pad")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

VESSEL_NAME = "GS1 Auto-Chute Booster"
# part name -> (istg before, istg after). sqor is swapped identically.
STAGE_SWAP = {
    "Decoupler.1": (1, 2),
    "liquidEngine2": (2, 1),
}


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _vessel_span(sfs: str, name: str):
    """(start, end) of the VESSEL node whose `name = <name>`; the node is closed by
    the first `\n\t\t}\n` after its header (VESSEL nodes are at depth 2)."""
    m = re.search(r"\n\t\tVESSEL\n\t\t\{\n\t\t\tpid = [^\n]*\n\t\t\tpersistentId = [^\n]*\n\t\t\tname = "
                  + re.escape(name) + r"\n", sfs)
    if m is None:
        raise SystemExit("VESSEL %r not found in %s" % (name, SOURCE_DIR))
    start = m.start()
    end = sfs.index("\n\t\t}\n", start) + len("\n\t\t}\n")
    return start, end


def _swap_part_stages(vessel: str) -> str:
    out = vessel
    for part, (before, after) in STAGE_SWAP.items():
        pat = re.compile(
            r"(\n\t\t\tPART\n\t\t\t\{\n\t\t\t\tname = " + re.escape(part)
            + r"\n(?:\t\t\t\t[^\n]*\n)*?\t\t\t\tistg = )" + str(before)
            + r"(\n(?:\t\t\t\t[^\n]*\n)*?\t\t\t\tsqor = )" + str(before) + r"\n")
        matches = list(pat.finditer(out))
        if len(matches) != 1:
            raise SystemExit("expected exactly one %s PART with istg=%d sqor=%d, found %d"
                             % (part, before, before, len(matches)))
        m = matches[0]
        out = out[:m.start()] + m.group(1) + str(after) + m.group(2) + str(after) + "\n" + out[m.end():]
    return out


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    text = source_bytes.decode("utf-8")
    start, end = _vessel_span(text, VESSEL_NAME)
    vessel = _swap_part_stages(text[start:end])
    return (text[:start] + vessel + text[end:]).encode("utf-8")


def derive_all() -> dict:
    files = {}
    for name in COPIED_FILES:
        src = _read(os.path.join(SOURCE_DIR, name))
        files[name] = derive_persistent_sfs(src) if name == "persistent.sfs" else src
    return files


def check() -> list:
    """Differences between a fresh derivation and the committed bytes (empty = OK)."""
    problems = []
    for name, data in derive_all().items():
        path = os.path.join(TARGET_DIR, name)
        if not os.path.isfile(path):
            problems.append("missing %s" % path)
        elif _read(path) != data:
            problems.append("%s differs from a fresh derivation" % path)
    for name in sorted(os.listdir(TARGET_DIR)) if os.path.isdir(TARGET_DIR) else []:
        if name not in COPIED_FILES:
            problems.append("unexpected file in fixture: %s" % name)
    return problems


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixture; exit 1 on drift")
    args = parser.parse_args(argv)
    if args.check:
        problems = check()
        for p in problems:
            print("DRIFT: " + p)
        print("coalescer-pad: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
