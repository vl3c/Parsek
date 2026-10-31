#!/usr/bin/env python3
"""Build the committed `CS Engine Test.craft` BY CONSTRUCTION, as a DERIVATIVE.

RW-17's craft: the shared Jumping Flea with one LV-909 Terrier stacked under its booster, the
part a `cs-career-mun1` part-test contract puts under test on the pad. The Terrier is never lit:
the contract's "Run Test" button is the whole use of it. Its block is lifted from the campaign's
`CS Mun Lander.craft`, renamed and re-attached; `attN` is the only link KSP needs, and the
booster gains the matching `link` and bottom node.

Usage:
    python harness/tools/build_cs_engine_test_craft.py --check
    python harness/tools/build_cs_engine_test_craft.py --write
"""

from __future__ import annotations

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir))
BASE = os.path.join(ROOT, "harness", "fixtures", "ships", "Jumping Flea.craft")
DONOR = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-career-mun4",
                     "Ships", "VAB", "CS Mun Lander.craft")
OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-career-mun1",
                   "Ships", "VAB", "CS Engine Test.craft")

SHIP_NAME = "CS Engine Test"
SHIP_PID = "2905720377"
ENGINE = "liquidEngine3.v2"
ENGINE_UID = 4200700011
ENGINE_PID = "2264732011"
HOST_RE = re.compile(r"(?m)^\tpart = (solidBooster\.sm\.v2_\d+)$")
# The booster's own top node sits 0.7575 above its centre; its bottom node mirrors it.
NODE_OFFSET = 0.7575


def _read(path):
    # Both inputs are KSP-written CRLF.
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _block(text, token):
    """The whole `PART { ... }` block holding `part = <token>`."""
    i = text.index("\tpart = %s\n" % token)
    start = text.rindex("PART\n{\n", 0, i)
    end = text.index("\n}\n", i) + 3
    return start, end


def build():
    base = _read(BASE)
    donor = _read(DONOR)
    host = HOST_RE.search(base).group(1)

    donor_token = re.search(r"(?m)^\tpart = (liquidEngine3\.v2_\d+)$", donor).group(1)
    start, end = _block(donor, donor_token)
    block = donor[start:end]
    engine_token = "%s_%d" % (ENGINE, ENGINE_UID)

    host_start, host_end = _block(base, host)
    host_block = base[host_start:host_end]
    host_y = float(re.search(r"(?m)^\tpos = \S+?,(\S+?),", host_block).group(1))
    host_stage = re.search(r"(?m)^\tistg = (\S+)$", host_block).group(1)

    block = block.replace(donor_token, engine_token)
    block = re.sub(r"(?m)^\tpersistentId = \d+$", "\tpersistentId = %s" % ENGINE_PID, block, count=1)
    block = re.sub(r"(?m)^\tpos = \S+$", "\tpos = 0,%s,0" % repr(round(host_y - NODE_OFFSET, 7)), block, count=1)
    block = re.sub(r"(?m)^\tattPos0 = \S+$", "\tattPos0 = 0,%s,0" % repr(-NODE_OFFSET), block, count=1)
    block = re.sub(r"(?m)^\tistg = \S+$", "\tistg = %s" % host_stage, block, count=1)
    block = re.sub(r"(?m)^\tdstg = \S+$", "\tdstg = 0", block, count=1)
    block = re.sub(r"(?m)^\tsqor = \S+$", "\tsqor = 1", block, count=1)
    block = re.sub(r"(?m)^\tsidx = \S+$", "\tsidx = 1", block, count=1)
    block = re.sub(r"(?m)^\tlink = .*\n", "", block)
    block = re.sub(r"(?m)^\tattN = .*\n", "", block)
    block = block.replace("\tmodSize = 0,0,0\n",
                          "\tmodSize = 0,0,0\n\tattN = top,%s_0|%s|0\n" % (host, repr(NODE_OFFSET)), 1)

    new_host = host_block.replace(
        "\tattN = top,", "\tlink = %s\n\tattN = bottom,%s_0|%s|0\n\tattN = top," % (
            engine_token, engine_token, repr(-NODE_OFFSET)), 1)
    out = base[:host_start] + new_host + base[host_end:]
    out = out.replace("ship = Jumping Flea\n", "ship = %s\n" % SHIP_NAME, 1)
    out = re.sub(r"(?m)^description = .*$", "description = RW-17's part-test craft", out, count=1)
    out = re.sub(r"(?m)^persistentId = \d+$", "persistentId = %s" % SHIP_PID, out, count=1)
    if not out.endswith("\n"):
        out += "\n"
    return out + block


def check():
    if not os.path.isfile(OUT):
        return ["missing " + OUT]
    with open(OUT, encoding="utf-8", newline="") as fh:
        on_disk = fh.read()
    return [] if on_disk == build() else ["%s differs from a fresh derivation" % OUT]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()
    if args.write:
        with open(OUT, "w", encoding="utf-8", newline="") as fh:
            fh.write(build())
    problems = check()
    for p in problems:
        print(p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
