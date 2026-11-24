#!/usr/bin/env python3
"""Build `CS Flea Twin Probe.craft`, RFN-1 / RFN-2's craft: the CS Flea Probe with a SECOND
probe stand, BY CONSTRUCTION from `build_cs_flea_probe_craft.py`'s own derivation.

WHY THIS CRAFT EXISTS. A separation INSIDE a Re-Fly needs a vessel that can split twice:
once in the recorded flight (the rewind point the Re-Fly starts from) and once more in the
attempt (a nested rewind point, authored session-provisional under the live Re-Fly marker).
The CS Flea Probe has one stand, so after its pad decouple the Flea slot carries one command
part and cannot split again into two controllable vessels. This craft carries two stands on
the booster, each a TT-38K holding an FL-T100 with a Stayputnik on top:

    stand A  at azimuth 270 (the CS Flea Probe's own stand, same geometry), stage 2
    stand B  at azimuth 150 (60 degrees from the fins at 90 and 210), stage 3

The lane decouples stand A in the recorded flight (`PartAction decouple partPid=` stand A's
decoupler), re-flies the Flea slot (the Flea plus stand B) and decouples stand B inside the
attempt. Both stands are the probe craft's layout() with its own azimuth, uids and stage, so
every geometry and tail argument in that builder's docstring holds here unchanged.

FRESH persistentIds (the craft-baked persistentId trap, .claude/CLAUDE.md): header
3615180000, parts 3615180001 on in file order: the Flea's 8 parts, stand A (decoupler
3615180009, tank, probe), stand B (decoupler 3615180012, tank, probe). KSP keeps a baked part
pid on launch unless it collides with a live vessel's, and the lanes launch it on an empty
pad, so `STAND_A_DECOUPLER_PID` / `STAND_B_DECOUPLER_PID` are the pids the lanes name. A
collision would refuse the decouple `partaction-part-not-found`, not act on the wrong part.

THE NUMBERS: 14 parts (the level-0 VAB allows 30); cost 5032 = the CS Flea Probe's 3982 plus
a second stand's 1050. Not flown.

One consumer (`cs-career-mun4`), so per `fixtures/shared-ships.toml` it lives physically in
that fixture's `Ships/VAB/`, not in the shared library.

Usage:
    python harness/tools/build_cs_flea_twin_probe_craft.py --check
    python harness/tools/build_cs_flea_twin_probe_craft.py --write

Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import importlib.util
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir))
OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-career-mun4", "Ships", "VAB",
                   "CS Flea Twin Probe.craft")

SHIP_NAME = "CS Flea Twin Probe"
HEADER_PERSISTENT_ID = 3615180000
PART_PERSISTENT_ID_BASE = 3615180001

# (azimuth, (decoupler, tank, probe) uids, stage) per stand, in file order.
STANDS = (
    (270.0, (4261180001, 4261180002, 4261180003), 2),
    (150.0, (4261180004, 4261180005, 4261180006), 3),
)
BASE_PART_COUNT = 8
STAND_A_DECOUPLER_PID = PART_PERSISTENT_ID_BASE + BASE_PART_COUNT
STAND_B_DECOUPLER_PID = PART_PERSISTENT_ID_BASE + BASE_PART_COUNT + 3


def _probe_builder():
    path = os.path.join(HERE, "build_cs_flea_probe_craft.py")
    spec = importlib.util.spec_from_file_location("build_cs_flea_probe_craft", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def build():
    b = _probe_builder()
    with open(b.BASE, encoding="utf-8", newline="") as fh:
        base = fh.read().replace("\r\n", "\n")
    header, blocks = b._split_parts(base)
    if len(blocks) != BASE_PART_COUNT:
        raise ValueError("base craft has %d parts, expected %d" % (len(blocks), BASE_PART_COUNT))
    header = re.sub(r"^ship = .*$", "ship = %s" % SHIP_NAME, header, count=1, flags=re.M)
    header = re.sub(
        r"^description = .*$",
        "description = RFN-1 / RFN-2 craft (a separation inside a Re-Fly): the stock Jumping "
        "Flea plus two probe stands on the booster's sides, each a TT-38K radial decoupler "
        "holding an FL-T100 with a Stayputnik on top. Built by construction by "
        "harness/tools/build_cs_flea_twin_probe_craft.py.",
        header, count=1, flags=re.M)
    header = re.sub(r"^persistentId = .*$", "persistentId = %d" % HEADER_PERSISTENT_ID,
                    header, count=1, flags=re.M)
    host_index = None
    for i, blk in enumerate(blocks):
        if b._scalar(blk, "part") == b.HOST:
            host_index = i
    if host_index is None:
        raise ValueError("base craft lacks %s" % b.HOST)
    host_pos = tuple(float(c) for c in b._scalar(blocks[host_index], "pos").split(","))
    fin_band = {b._scalar(blk, "attPos0").split(",")[1] for blk in blocks
                if b._scalar(blk, "part").startswith(b.FIN_PREFIX)}
    if len(fin_band) != 1:
        raise ValueError("base fins are not on one band: %r" % sorted(fin_band))
    fin_dy = float(fin_band.pop())
    added = []
    for azimuth, uids, stage in STANDS:
        stand = b.layout(host_pos, fin_dy, azimuth_deg=azimuth, uids=uids, stage=stage)
        blocks[host_index] = b._with_link(blocks[host_index], "%s_%d" % (stand[0]["name"],
                                                                         stand[0]["uid"]))
        added += stand
    # Extents grow by both stands: each reaches decoupler radius + child offset + the tank's
    # radius from the booster's axis along its azimuth.
    m = re.search(r"^size = ([^,\n]+),([^,\n]+),([^,\n]+)$", header, flags=re.M)
    reach = b.DECOUPLER_RADIUS + b.CHILD_OFFSET + 0.625
    xs = [math.cos(math.radians(a)) * reach for a, _, _ in STANDS]
    zs = [math.sin(math.radians(a)) * reach for a, _, _ in STANDS]
    half_x, half_z = float(m.group(1)) / 2.0, float(m.group(3)) / 2.0
    size_x = max(half_x, *xs) - min(-half_x, *xs)
    size_z = max(half_z, *zs) - min(-half_z, *zs)
    header = header.replace(m.group(0), "size = %s,%s,%s" % (b._fmt(size_x), m.group(2),
                                                             b._fmt(size_z)))
    pid = PART_PERSISTENT_ID_BASE
    out_blocks = []
    for blk in blocks:
        out_blocks.append(re.sub(r"^(\tpersistentId = ).*$",
                                 lambda mm: mm.group(1) + str(pid), blk, count=1, flags=re.M))
        pid += 1
    for p in added:
        out_blocks.append(b.render(p, pid))
        pid += 1
    return header + "".join(out_blocks)


def check():
    """[] when the committed craft is byte-identical to a fresh derivation."""
    if not os.path.exists(OUT):
        return ["MISSING %s - run --write" % OUT]
    with open(OUT, encoding="utf-8", newline="") as fh:
        have = fh.read()
    return [] if have == build() else ["DRIFT: %s differs from the derivation" % OUT]


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args(argv)
    if a.write:
        text = build()
        with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote %s (%d lines)" % (OUT, text.count("\n")))
        return 0
    problems = check()
    print("\n".join(problems) or "OK committed craft matches the derivation")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
