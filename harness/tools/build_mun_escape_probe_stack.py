#!/usr/bin/env python3
"""Derive the committed `mun-escape-probe-stack` fixture from `gs2-orbital-stack` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. Lane SOI-3 needs a BACKGROUND vessel that crosses an SOI
boundary while packed / on rails, recorded by Parsek's background recorder (SOI-2
covers only the loaded case). That needs two things no committed fixture has together:

  * a CONTROLLED child to release. A decoupled stack with no command part is debris,
    and a debris recording cannot cross on rails: `BackgroundRecorder.CheckDebrisTTL`
    ends it after 60 s, as soon as it unloads, or as soon as its PARENT packs (the
    `ParentOnRailsOrDestroyed` reason), which any rails warp does. SOI-1 / SOI-2's
    host `mun-park-kerbalx` has a single command part (the Mk1-3 pod), so it can only
    make debris. `gs2-orbital-stack` (GS-2's host, RF-21's host) carries the probe-cored
    core under a decoupler: `PartAction decouple partPid=2923926328` there splits off
    "Kerbal X Probe", a controlled child recorded indefinitely (RF-21 measured
    `Controllable split children: [a,b]` on it).
  * a trajectory that crosses an SOI boundary soon, with no burn. `gs2-orbital-stack`
    parks in LKO, so the probe would never leave Kerbin.

THE DERIVATION. Take gs2-orbital-stack's persistent.sfs byte for byte and change only
the `Kerbal X` VESSEL's situation, altitude and ORBIT: it is placed on a Mun ESCAPE
hyperbola (REF = 2, eccentricity 1.2, periapsis radius 500 km, so semi-major axis
-2,500,000 m and v_inf 161.4 m/s), outbound, with the Mun SOI exit (radius 2,429,559.1
m) `EXIT_LEAD_SECONDS` = 900 game s after the save's own UT (416.91). The orbit plane
and argument of periapsis are the LKO orbit's own values. v_inf plus the Mun's 543 m/s
stays under Kerbin's 767 m/s escape speed at the Mun's distance, so the Kerbin orbit
after the exit is bound whatever the asymptote direction. Nothing else changes: the
crew, the parts, the unlit Skipper, the asteroid, the launch-pad debris, the inert
ParsekScenario node.

So the SOI-3 flight is seam-only: load, StartRecording, decouple the probe core, then
`WarpToUT` to an explicit UT past the exit (the exit UT is fixed by these bytes, not by
a burn), commit, and read the probe's recording.

WHAT IS NOT COPIED. `Ships/` and `AddOns/` (the seam launches nothing).
`persistent.loadmeta` is copied unchanged (same vessel count and UT).

Run `python harness/tools/build_mun_escape_probe_stack.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_mun_escape_probe_stack.py` runs the check in-process and re-derives
the exit time from the committed elements.
"""
from __future__ import annotations

import argparse
import math
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs2-orbital-stack")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "mun-escape-probe-stack")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

VESSEL_NAME = "Kerbal X"

# Stock Mun (KSP 1.12.5 body data).
MUN_GM = 6.5138398e10
MUN_RADIUS = 200000.0
MUN_SOI = 2429559.1
MUN_REF_INDEX = 2

ECCENTRICITY = 1.2
PERIAPSIS_RADIUS = 500000.0
EXIT_LEAD_SECONDS = 900.0


def semi_major_axis() -> float:
    return PERIAPSIS_RADIUS / (1.0 - ECCENTRICITY)


def mean_motion(sma: float) -> float:
    return math.sqrt(MUN_GM / abs(sma) ** 3)


def hyperbolic_mean_anomaly_at_radius(sma: float, ecc: float, radius: float) -> float:
    """M = e sinh H - H at the outbound point where r = a (1 - e cosh H)."""
    cosh_h = (1.0 - radius / sma) / ecc
    h = math.acosh(cosh_h)
    return ecc * math.sinh(h) - h


def radius_at_mean_anomaly(sma: float, ecc: float, mean_anomaly: float) -> float:
    h = math.asinh(mean_anomaly / ecc)
    for _ in range(100):
        h -= (ecc * math.sinh(h) - h - mean_anomaly) / (ecc * math.cosh(h) - 1.0)
    return sma * (1.0 - ecc * math.cosh(h))


def seconds_to_soi_exit(sma: float, ecc: float, mean_anomaly_at_epoch: float) -> float:
    exit_m = hyperbolic_mean_anomaly_at_radius(sma, ecc, MUN_SOI)
    return (exit_m - mean_anomaly_at_epoch) / mean_motion(sma)


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
    return repr(float(value))


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    text = source_bytes.decode("utf-8")
    ut = float(re.search(r"\n\t\tUT = ([^\n]+)\n", text).group(1))
    start, end = _vessel_span(text, VESSEL_NAME)
    vessel = text[start:end]

    sma = semi_major_axis()
    exit_m = hyperbolic_mean_anomaly_at_radius(sma, ECCENTRICITY, MUN_SOI)
    mna = exit_m - EXIT_LEAD_SECONDS * mean_motion(sma)
    radius = radius_at_mean_anomaly(sma, ECCENTRICITY, mna)

    vessel = _replace_one(vessel, r"\n\t\t\tsit = ORBITING\n", "\n\t\t\tsit = ESCAPING\n")
    vessel = _replace_one(vessel, r"\n\t\t\talt = [^\n]*\n",
                          "\n\t\t\talt = %s\n" % _fmt(radius - MUN_RADIUS))
    vessel = _replace_one(vessel, r"\n\t\t\thgt = [^\n]*\n", "\n\t\t\thgt = -1\n")
    orbit = ("\n\t\t\tORBIT\n\t\t\t{\n"
             "\t\t\t\tSMA = %s\n\t\t\t\tECC = %s\n\t\t\t\tINC = \\1\n\t\t\t\tLPE = \\2\n"
             "\t\t\t\tLAN = \\3\n\t\t\t\tMNA = %s\n\t\t\t\tEPH = %s\n\t\t\t\tREF = %d\n\t\t\t}\n"
             % (_fmt(sma), _fmt(ECCENTRICITY), _fmt(mna), repr(ut), MUN_REF_INDEX))
    vessel = _replace_one(
        vessel,
        r"\n\t\t\tORBIT\n\t\t\t\{\n\t\t\t\tSMA = [^\n]*\n\t\t\t\tECC = [^\n]*\n"
        r"\t\t\t\tINC = ([^\n]*)\n\t\t\t\tLPE = ([^\n]*)\n\t\t\t\tLAN = ([^\n]*)\n"
        r"\t\t\t\tMNA = [^\n]*\n\t\t\t\tEPH = [^\n]*\n\t\t\t\tREF = 1\n\t\t\t\}\n",
        orbit)
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
        print("mun-escape-probe-stack: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
