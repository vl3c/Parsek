#!/usr/bin/env python3
"""Derive the committed `graze-high-probe-stack` / `graze-low-probe-stack` fixtures from
`gs2-orbital-stack` BY CONSTRUCTION.

WHY THESE FIXTURES EXIST. Lanes ASR-3 / ASR-4 prove how Parsek ends the recording of a
stage left in a GRAZING Kerbin orbit (periapsis inside the 70 km atmosphere, so KSP reads
the vessel SUB_ORBITAL). Owner ruling: a periapsis that stays ABOVE the line where stock
deletes an unloaded vessel (static pressure > 1 kPa, about 22-25 km on Kerbin) ends
`Orbiting`, one that dips below it ends `Destroyed` on the first pass. No committed host
carries a stage on such an orbit, and no mission can put one there without a deorbit
burn the harness has no verb for. `gs2-orbital-stack` (GS-2's / RF-21's / SOI-3's base)
carries the crewed Kerbal X stack with its probe-cored core under `Decoupler.2` partPid
2923926328 in a ~100 km LKO, so moving that stack onto a grazing orbit gives both lanes a
controlled child to release and a crewed parent, with no ascent and no burn.

THE DERIVATION. Take gs2-orbital-stack's persistent.sfs byte for byte and change only
the `Kerbal X` VESSEL's situation, altitude and ORBIT SMA / ECC / MNA: the apoapsis
radius stays the LKO orbit's own (a (1 + e) of the source elements, ~106 km), the
periapsis altitude is `PERIAPSIS_ALTITUDES[<fixture>]` (40 km above the 1 kPa line, 15 km
below it), and the stack sits AT apoapsis at the save's own UT (MNA = pi, EPH = UT), so
the flight starts at the top of the arc, ~10 minutes of game time before the descent
reaches the atmosphere. `sit = SUB_ORBITAL`, `alt` = the apoapsis altitude. INC / LPE /
LAN / EPH / REF stay the source's. Nothing else changes: the crew, the parts, the unlit
Skipper, the asteroid, the launch-pad debris, the inert ParsekScenario node.

WHAT IS NOT COPIED. `Ships/` and `AddOns/` (the seam launches nothing).
`persistent.loadmeta` is copied unchanged (same vessel count and UT).

Run `python harness/tools/build_graze_probe_stacks.py` to (re)write both fixtures and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_graze_probe_stacks.py` runs the check in-process and re-derives the
periapsis altitudes from the committed elements.
"""
from __future__ import annotations

import argparse
import math
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs2-orbital-stack")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

VESSEL_NAME = "Kerbal X"

KERBIN_RADIUS = 600000.0

# fixture leaf -> periapsis ALTITUDE (m). 40 km stays above the 1 kPa line (about
# 22-25 km on Kerbin); 15 km is below it.
PERIAPSIS_ALTITUDES = {
    "graze-high-probe-stack": 40000.0,
    "graze-low-probe-stack": 15000.0,
}


def target_dir(leaf: str) -> str:
    return os.path.join(HARNESS_ROOT, "fixtures", "saves", leaf)


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _vessel_span(sfs: str, name: str):
    m = re.search(r"\n\t\tVESSEL\n\t\t\{\n\t\t\tpid = [^\n]*\n\t\t\tpersistentId = [^\n]*\n\t\t\tname = "
                  + re.escape(name) + r"\n", sfs)
    if m is None:
        raise SystemExit("VESSEL %r not found in %s" % (name, SOURCE_DIR))
    start = m.start()
    end = sfs.index("\n\t\t}\n", start) + len("\n\t\t}\n")
    return start, end


def _replace_one(text: str, pattern: str, replacement: str) -> str:
    out, n = re.subn(pattern, replacement, text, count=1)
    if n != 1:
        raise SystemExit("pattern not found exactly once: %s" % pattern)
    return out


def _fmt(value: float) -> str:
    # 12 significant digits, the build_mun_escape_probe_stack convention, so the
    # derivation rebuilds byte-identically on every platform.
    return repr(float("%.12g" % float(value)))


def source_apoapsis_radius(vessel_text: str) -> float:
    sma = float(re.search(r"\n\t\t\t\tSMA = ([^\n]*)\n", vessel_text).group(1))
    ecc = float(re.search(r"\n\t\t\t\tECC = ([^\n]*)\n", vessel_text).group(1))
    return sma * (1.0 + ecc)


def grazing_elements(apoapsis_radius: float, periapsis_altitude: float):
    rp = KERBIN_RADIUS + periapsis_altitude
    sma = (apoapsis_radius + rp) / 2.0
    ecc = (apoapsis_radius - rp) / (apoapsis_radius + rp)
    return sma, ecc


def derive_persistent_sfs(source_bytes: bytes, periapsis_altitude: float) -> bytes:
    text = source_bytes.decode("utf-8")
    start, end = _vessel_span(text, VESSEL_NAME)
    vessel = text[start:end]
    ra = source_apoapsis_radius(vessel)
    sma, ecc = grazing_elements(ra, periapsis_altitude)

    vessel = _replace_one(vessel, r"\n\t\t\tsit = ORBITING\n", "\n\t\t\tsit = SUB_ORBITAL\n")
    vessel = _replace_one(vessel, r"\n\t\t\talt = [^\n]*\n",
                          "\n\t\t\talt = %s\n" % _fmt(ra - KERBIN_RADIUS))
    vessel = _replace_one(vessel, r"\n\t\t\t\tSMA = [^\n]*\n", "\n\t\t\t\tSMA = %s\n" % _fmt(sma))
    vessel = _replace_one(vessel, r"\n\t\t\t\tECC = [^\n]*\n", "\n\t\t\t\tECC = %s\n" % _fmt(ecc))
    vessel = _replace_one(vessel, r"\n\t\t\t\tMNA = [^\n]*\n",
                          "\n\t\t\t\tMNA = %s\n" % _fmt(math.pi))
    return (text[:start] + vessel + text[end:]).encode("utf-8")


def derive_all(leaf: str) -> dict:
    files = {}
    for name in COPIED_FILES:
        src = _read(os.path.join(SOURCE_DIR, name))
        files[name] = (derive_persistent_sfs(src, PERIAPSIS_ALTITUDES[leaf])
                       if name == "persistent.sfs" else src)
    return files


def check() -> list:
    """Differences between a fresh derivation and the committed bytes (empty = OK)."""
    problems = []
    for leaf in sorted(PERIAPSIS_ALTITUDES):
        out = target_dir(leaf)
        for name, data in derive_all(leaf).items():
            path = os.path.join(out, name)
            if not os.path.isfile(path):
                problems.append("missing %s" % path)
            elif _read(path) != data:
                problems.append("%s differs from a fresh derivation" % path)
        for name in sorted(os.listdir(out)) if os.path.isdir(out) else []:
            if name not in COPIED_FILES:
                problems.append("unexpected file in fixture %s: %s" % (leaf, name))
    return problems


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixtures; exit 1 on drift")
    args = parser.parse_args(argv)
    if args.check:
        problems = check()
        for p in problems:
            print("DRIFT: " + p)
        print("graze probe stacks: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    for leaf in sorted(PERIAPSIS_ALTITUDES):
        out = target_dir(leaf)
        os.makedirs(out, exist_ok=True)
        for name, data in derive_all(leaf).items():
            with open(os.path.join(out, name), "wb") as fh:
                fh.write(data)
            print("wrote %s (%d bytes)" % (os.path.join(out, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
