#!/usr/bin/env python3
"""Derive the committed `gs1-armed-chute-drop` fixture from `gs1-two-stage-pad` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. Lane ASR-8 proves owner ruling Q10b (2026-11-21): a stage whose
parachutes are ARMED but not yet OPEN when Parsek stops watching it does not count as
chuted, so its predicted tail is the drag-free fall to impact (`Destroyed`, crew dead),
never a chute-speed descent and a landing. The lane needs a craft whose chutes can be
armed where they cannot open: stock `ModuleParachute` in the ACTIVE (armed) state opens
only once the static pressure reaches `minAirPressureToOpen` (0.04 atm on every chute of
the GS1 craft, about 16-18 km on Kerbin) and, with the persisted `automateSafeDeploy = 0`,
only while the deploy reads SAFE. No committed host carries a chuted craft that high:
the GS1 Auto-Chute Booster's own hop stages at a ~700 m apoapsis, where its six Mk2-R
canopies open within a physics step (ASR-2 / ASR-7 measured `canopy=Deployed
openChutes=6` at the commit), and the harness has no verb for an ascent to 60 km.

THE CRAFT. `gs1-two-stage-pad`'s GS1 Auto-Chute Booster (root `mk1pod.v2` crewed by
Valentina Kerman with a Mk16 `parachuteSingle`, `Decoupler.1` partPid 1500054019, then
the `probeCoreOcto2.v2` booster with an LV-909 and six Mk2-R `parachuteRadial`), so the
decouple gives a crewed parent and a probe-cored CONTROLLED child, and both carry chutes.

THE DERIVATION. Take gs1-two-stage-pad's persistent.sfs byte for byte and change only the
`GS1 Auto-Chute Booster` VESSEL's situation fields and ORBIT SMA / ECC: the craft is
lifted `LIFT_METERS` (62 km) straight above the pad, at the top of a near-vertical arc.
The pad vessel's own ORBIT already sits at its apoapsis at the save's UT (MNA = pi,
EPH = UT, the KSP-written PRELAUNCH orbit), so keeping INC / LPE / LAN / MNA / EPH / REF
and raising the apoapsis radius by the lift keeps the craft over the pad. The apoapsis
speed is Kerbin's co-rotation speed at the lifted radius, so the craft starts at rest
relative to the air (no horizontal drift, no reentry heating): vis-viva gives SMA, and
ECC = ra / SMA - 1 (about 0.993; periapsis radius about 2.3 km, deep inside Kerbin).
`sit = FLYING` (below the 70 km atmosphere top), `landed = False`, `landedAt` /
`displaylandedAt` emptied, `alt` = the lifted altitude, `hgt = -1` (stock's value for a
vessel off the ground). Nothing else changes: the crew, the parts (every chute STOWED),
`lat` / `lon` / `rot`, the asteroid, the inert ParsekScenario node.

So the ASR-8 flight is seam-only: load, StartRecording, arm all seven chutes
(`PartAction chute all=true`) while the air is far too thin to open them, decouple the
booster, and leave the flight scene. A drag-free drop from the 62 km apex needs about
100 s of game time to reach the 0.04 atm line; the seam steps take seconds.

WHAT IS NOT COPIED. `Ships/` and `AddOns/` (the seam launches nothing).
`persistent.loadmeta` is copied unchanged (same vessel count and UT).

Run `python harness/tools/build_gs1_armed_chute_drop.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_gs1_armed_chute_drop.py` runs the check in-process and re-derives the
apex altitude, the co-rotating apex speed and the chutes' opening threshold.
"""
from __future__ import annotations

import argparse
import math
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs1-two-stage-pad")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs1-armed-chute-drop")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

VESSEL_NAME = "GS1 Auto-Chute Booster"

# Stock Kerbin (KSP 1.12.5 body data).
KERBIN_GM = 3.5316e12
KERBIN_RADIUS = 600000.0
KERBIN_ROTATION_PERIOD = 21549.425

LIFT_METERS = 62000.0


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
    # 12 significant digits (build_mun_escape_probe_stack's convention), so the
    # derivation rebuilds byte-identically on every platform.
    return repr(float("%.12g" % float(value)))


def source_apoapsis_radius(vessel_text: str) -> float:
    sma = float(re.search(r"\n\t\t\t\tSMA = ([^\n]*)\n", vessel_text).group(1))
    ecc = float(re.search(r"\n\t\t\t\tECC = ([^\n]*)\n", vessel_text).group(1))
    return sma * (1.0 + ecc)


def corotation_speed(radius: float) -> float:
    return 2.0 * math.pi * radius / KERBIN_ROTATION_PERIOD


def drop_elements(apoapsis_radius: float):
    """SMA / ECC of the arc whose apoapsis is `apoapsis_radius` with the co-rotation speed there."""
    v = corotation_speed(apoapsis_radius)
    sma = 1.0 / (2.0 / apoapsis_radius - v * v / KERBIN_GM)
    ecc = apoapsis_radius / sma - 1.0
    return sma, ecc


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    text = source_bytes.decode("utf-8")
    start, end = _vessel_span(text, VESSEL_NAME)
    vessel = text[start:end]
    ra = source_apoapsis_radius(vessel) + LIFT_METERS
    sma, ecc = drop_elements(ra)

    vessel = _replace_one(vessel, r"\n\t\t\tsit = PRELAUNCH\n", "\n\t\t\tsit = FLYING\n")
    vessel = _replace_one(vessel, r"\n\t\t\tlanded = True\n", "\n\t\t\tlanded = False\n")
    vessel = _replace_one(vessel, r"\n\t\t\tlandedAt = [^\n]*\n", "\n\t\t\tlandedAt = \n")
    vessel = _replace_one(vessel, r"\n\t\t\tdisplaylandedAt = [^\n]*\n", "\n\t\t\tdisplaylandedAt = \n")
    vessel = _replace_one(vessel, r"\n\t\t\talt = [^\n]*\n",
                          "\n\t\t\talt = %s\n" % _fmt(ra - KERBIN_RADIUS))
    vessel = _replace_one(vessel, r"\n\t\t\thgt = [^\n]*\n", "\n\t\t\thgt = -1\n")
    vessel = _replace_one(vessel, r"\n\t\t\t\tSMA = [^\n]*\n", "\n\t\t\t\tSMA = %s\n" % _fmt(sma))
    vessel = _replace_one(vessel, r"\n\t\t\t\tECC = [^\n]*\n", "\n\t\t\t\tECC = %s\n" % _fmt(ecc))
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
        print("gs1-armed-chute-drop: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
