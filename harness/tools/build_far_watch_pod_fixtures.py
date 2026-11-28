#!/usr/bin/env python3
"""Derive the committed far-watch pod fixtures from `kerbin-splashdown-recorded` BY CONSTRUCTION.

WHY THESE FIXTURES EXIST. Far watch step 3 (docs/dev/design-far-watch.md 17.4) owes two
lanes the corpus cannot host: FW-3 (a far watch asked for while the ship flies inside the
atmosphere is refused with stock's own reason, NOT_IN_ATMOSPHERE) and FW-4 (a far watch
on a ship whose path falls into the atmosphere ends at its return point, 300 s before the
71 km crossing, and gives the ship back alive). Both need the ship in the air or on a
falling path BESIDE a committed ghost past 300 km. The only committed host with a far
ghost is W1's `kerbin-splashdown-recorded` (the B4 Kerbal X tree looped over a capsule
landed 161 deg of arc east of KSC), and its ghosts only play once the looped mission
reaches its anchor, UT 21583.965183089826 (the recording's start plus one Kerbin day),
about 20,300 s past the save's clock: a ship in the air would not live through that jump.

THE CLOCK. Both fixtures move the save clock to SAVE_UT = 21560 (FLIGHTSTATE `UT`, the
`persistent.loadmeta` `UT` and the capsule's `lastUT`), 24 s before the anchor. The
loop anchor is "the next faithful launch at or after the UT the loop was enabled at"
(`MissionLoopUnitBuilder`, `MissionStore` stamps `LoopAnchorUT` with the enable UT), so
a `MissionConfig loop=true` sent right after the load, before 21583.965, keeps the
anchor W1 measured, and the ghosts then play without a `StartLoopPlayback` jump: the
ascent member (index 0) from UT 21583.965, the coast member (index 1) from UT 21761.6
(loopUT = UT - 21549.425, W1 section 2A). Kerbin turns by 338.85 deg between the source's
clock and SAVE_UT, so every ORBIT below is rotated or written for SAVE_UT.

THE CAPSULE. The `#autoLOC_501232` VESSEL (mk1-3pod with Jebediah Kerman and a CUT
parachuteLarge, `activeVessel = 1`) is the only vessel edited; its parts, crew, rotation
and every other vessel stay byte for byte. Common edits: `landed = False`, `hgt = -1`
(stock's value off the ground), `lastUT = SAVE_UT`, and the ORBIT's SMA / ECC / LPE /
LAN / MNA / EPH as below (INC and REF stay the source's).

  kerbin-splashdown-pod-in-air (FW-3): lifted to IN_AIR_ALTITUDE (25 km) straight above
    its landing spot, at rest relative to the air at the top of a near-vertical drop
    (build_gs1_armed_chute_drop.py's recipe): the source's landed ORBIT sits at its
    apoapsis at its own clock (MNA = pi, EPH = UT), so INC / LPE / MNA stay, LAN turns
    with Kerbin by 360 * (SAVE_UT - source UT) / P so the apoapsis stays over the same
    body-fixed spot, SMA / ECC come from vis-viva with Kerbin's co-rotation speed at the
    apoapsis, `sit = FLYING`, `alt` = 25 km, `lat` / `lon` stay. A drag-free fall from
    25 km loses about 5 km in the 33 s to the lane's probe at UT 21593 (g about 9 m/s2
    there), so the capsule is near 20 km when the lane asks for the far watch.

  kerbin-splashdown-pod-suborbital (FW-4): on an equatorial prograde arc with the
    periapsis at SUBORBITAL_PERIAPSIS_ALTITUDE (60 km) and the apoapsis at
    SUBORBITAL_APOAPSIS_ALTITUDE (106.4 km, the LKO apoapsis of graze-high-probe-stack),
    timed so it falls through h_safe (70 km + 1000 m, FarWatchShipSafety.SafeAltitude)
    at CROSSING_UT = 22490: 720 s after FW-4 asks for the far watch at UT 21770, so the
    start is admitted with a return point at 22190 (danger - 300 s) and not refused
    (600 s, RefuseDangerWithinSeconds). The periapsis points at
    PERIAPSIS_INERTIAL_LONGITUDE (46 deg, inertial, LAN + LPE), which puts the capsule
    on the far side of Kerbin from the coast member over the whole watch, about 1,380 km
    away (harness/lib/test_far_watch_pod_fixtures.py recomputes the separation from the
    recording's points). `sit = SUB_ORBITAL`, `alt` / `lat` / `lon` the capsule's place
    at SAVE_UT.

THE FRAME. KSP's inertial longitude is LAN + LPE + true anomaly (for these near-equatorial
conics) and body-fixed longitude = inertial longitude - Kerbin's rotation angle,
initialRotation + 360 UT / P. The builder derives initialRotation from the source's own
landed capsule (its ORBIT and its lat / lon at the same UT) and refuses to build unless it
reads 90 deg, stock Kerbin's.

WHAT IS COPIED. Every other file of the source byte for byte: the recording sidecars
under `Parsek/`, the ParsekScenario node (the committed Kerbal X tree), `AddOns/`.

Run `python harness/tools/build_far_watch_pod_fixtures.py` to (re)write both fixtures and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_far_watch_pod_fixtures.py` runs the check in-process and re-derives the
geometry the two lanes rely on.

Stdlib only; ASCII only; no em dashes.
"""
from __future__ import annotations

import argparse
import math
import os
import re
import shutil
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
SOURCE = "kerbin-splashdown-recorded"
SOURCE_DIR = os.path.join(SAVES, SOURCE)
IN_AIR = "kerbin-splashdown-pod-in-air"
SUBORBITAL = "kerbin-splashdown-pod-suborbital"
TARGETS = (IN_AIR, SUBORBITAL)

SFS = "persistent.sfs"
LOADMETA = "persistent.loadmeta"
VESSEL_NAME = "#autoLOC_501232"

# Stock Kerbin (KSP 1.12.5 body data); the period is the loop cadence W1 logged.
KERBIN_GM = 3.5316e12
KERBIN_RADIUS = 600000.0
KERBIN_ROTATION_PERIOD = 21549.425183089826
KERBIN_INITIAL_ROTATION = 90.0
# FarWatchShipSafety.SafeAltitude on Kerbin: the 70 km atmosphere plus the 1000 m margin.
KERBIN_SAFE_ALTITUDE = 71000.0

# W1 section 2A: the loop anchor (the recording's start plus one Kerbin day).
LOOP_ANCHOR_UT = 21583.965183089826
SAVE_UT = 21560.0

IN_AIR_ALTITUDE = 25000.0

SUBORBITAL_PERIAPSIS_ALTITUDE = 60000.0
SUBORBITAL_APOAPSIS_ALTITUDE = 106400.0
CROSSING_UT = 22490.0
PERIAPSIS_INERTIAL_LONGITUDE = 46.0


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _fmt(value: float) -> str:
    # 12 significant digits (build_mun_escape_probe_stack's convention), so the
    # derivation rebuilds byte-identically on every platform.
    return repr(float("%.12g" % float(value)))


def _vessel_span(sfs: str, name: str):
    m = re.search(r"\n\t\tVESSEL\n\t\t\{\n\t\t\tpid = [^\n]*\n\t\t\tpersistentId = [^\n]*\n\t\t\tname = "
                  + re.escape(name) + r"\n", sfs)
    if m is None:
        raise SystemExit("VESSEL %r not found in %s" % (name, SOURCE_DIR))
    start = m.start()
    end = sfs.index("\n\t\t}\n", start) + len("\n\t\t}\n")
    return start, end


def _replace_one(text: str, pattern: str, replacement: str) -> str:
    out, n = re.subn(pattern, lambda _m: replacement, text, count=1)
    if n != 1:
        raise SystemExit("pattern not found exactly once: %s" % pattern)
    return out


def _value(text: str, key: str, depth: int) -> str:
    m = re.search(r"\n" + "\t" * depth + re.escape(key) + r" = ([^\n]*)\n", text)
    if m is None:
        raise SystemExit("key %r not found" % key)
    return m.group(1)


def _set(text: str, key: str, value: str, depth: int) -> str:
    tabs = "\t" * depth
    return _replace_one(text, r"\n" + tabs + re.escape(key) + r" = [^\n]*\n",
                        "\n%s%s = %s\n" % (tabs, key, value))


def source_save_ut(sfs: str) -> float:
    return float(_value(sfs, "UT", 2))


def rotation_angle(ut: float) -> float:
    return KERBIN_INITIAL_ROTATION + 360.0 * ut / KERBIN_ROTATION_PERIOD


def derived_initial_rotation(sfs: str) -> float:
    """Kerbin's initialRotation read off the landed capsule: its ORBIT sits at apoapsis
    (true anomaly 180) at the save's UT, right above its own lat / lon."""
    start, end = _vessel_span(sfs, VESSEL_NAME)
    v = sfs[start:end]
    inertial = float(_value(v, "LAN", 4)) + float(_value(v, "LPE", 4)) + 180.0
    lon = float(_value(v, "lon", 3))
    return (inertial - lon - 360.0 * source_save_ut(sfs) / KERBIN_ROTATION_PERIOD) % 360.0


def corotation_speed(radius: float) -> float:
    return 2.0 * math.pi * radius / KERBIN_ROTATION_PERIOD


def drop_elements(apoapsis_radius: float):
    """SMA / ECC of the arc whose apoapsis is `apoapsis_radius` with the co-rotation speed there."""
    v = corotation_speed(apoapsis_radius)
    sma = 1.0 / (2.0 / apoapsis_radius - v * v / KERBIN_GM)
    ecc = apoapsis_radius / sma - 1.0
    return sma, ecc


def suborbital_shape():
    rp = KERBIN_RADIUS + SUBORBITAL_PERIAPSIS_ALTITUDE
    ra = KERBIN_RADIUS + SUBORBITAL_APOAPSIS_ALTITUDE
    return (ra + rp) / 2.0, (ra - rp) / (ra + rp)


def mean_motion(sma: float) -> float:
    return math.sqrt(KERBIN_GM / sma ** 3)


def seconds_from_apoapsis_to_descent(sma: float, ecc: float, altitude: float) -> float:
    """Time from apoapsis to the descending crossing of `altitude` (radius below apoapsis)."""
    r = KERBIN_RADIUS + altitude
    e_anom = 2.0 * math.pi - math.acos((1.0 - r / sma) / ecc)
    m_anom = e_anom - ecc * math.sin(e_anom)
    return (m_anom - math.pi) / mean_motion(sma)


def solve_kepler(m_anom: float, ecc: float) -> float:
    e_anom = m_anom
    for _ in range(64):
        e_anom -= (e_anom - ecc * math.sin(e_anom) - m_anom) / (1.0 - ecc * math.cos(e_anom))
    return e_anom


def true_anomaly(m_anom: float, ecc: float) -> float:
    e_anom = solve_kepler(math.fmod(m_anom, 2.0 * math.pi), ecc)
    return 2.0 * math.atan2(math.sqrt(1.0 + ecc) * math.sin(e_anom / 2.0),
                            math.sqrt(1.0 - ecc) * math.cos(e_anom / 2.0))


def suborbital_mean_anomaly_at_save() -> float:
    sma, ecc = suborbital_shape()
    apoapsis_ut = CROSSING_UT - seconds_from_apoapsis_to_descent(sma, ecc, KERBIN_SAFE_ALTITUDE)
    return (math.pi + mean_motion(sma) * (SAVE_UT - apoapsis_ut)) % (2.0 * math.pi)


def _common(vessel: str) -> str:
    vessel = _set(vessel, "landed", "False", 3)
    vessel = _set(vessel, "hgt", "-1", 3)
    vessel = _set(vessel, "lastUT", _fmt(SAVE_UT), 3)
    return _set(vessel, "EPH", _fmt(SAVE_UT), 4)


def derive_in_air_vessel(vessel: str, source_ut: float) -> str:
    sma, ecc = drop_elements(KERBIN_RADIUS + IN_AIR_ALTITUDE)
    lan = (float(_value(vessel, "LAN", 4))
           + 360.0 * (SAVE_UT - source_ut) / KERBIN_ROTATION_PERIOD) % 360.0
    vessel = _set(vessel, "sit", "FLYING", 3)
    vessel = _common(vessel)
    vessel = _set(vessel, "alt", _fmt(IN_AIR_ALTITUDE), 3)
    vessel = _set(vessel, "SMA", _fmt(sma), 4)
    vessel = _set(vessel, "ECC", _fmt(ecc), 4)
    return _set(vessel, "LAN", _fmt(lan), 4)


def derive_suborbital_vessel(vessel: str) -> str:
    sma, ecc = suborbital_shape()
    inc = float(_value(vessel, "INC", 4))
    lan = float(_value(vessel, "LAN", 4))
    lpe = (PERIAPSIS_INERTIAL_LONGITUDE - lan) % 360.0
    mna = suborbital_mean_anomaly_at_save()
    nu = true_anomaly(mna, ecc)
    radius = sma * (1.0 - ecc * ecc) / (1.0 + ecc * math.cos(nu))
    u = math.radians(lpe) + nu
    lat = math.degrees(math.asin(math.sin(math.radians(inc)) * math.sin(u)))
    lon = (PERIAPSIS_INERTIAL_LONGITUDE + math.degrees(nu) - rotation_angle(SAVE_UT)) % 360.0
    if lon > 180.0:
        lon -= 360.0
    vessel = _set(vessel, "sit", "SUB_ORBITAL", 3)
    vessel = _common(vessel)
    vessel = _set(vessel, "lat", _fmt(lat), 3)
    vessel = _set(vessel, "lon", _fmt(lon), 3)
    vessel = _set(vessel, "alt", _fmt(radius - KERBIN_RADIUS), 3)
    vessel = _set(vessel, "SMA", _fmt(sma), 4)
    vessel = _set(vessel, "ECC", _fmt(ecc), 4)
    vessel = _set(vessel, "LPE", _fmt(lpe), 4)
    return _set(vessel, "MNA", _fmt(mna), 4)


def derive_sfs(source_bytes: bytes, leaf: str) -> bytes:
    text = source_bytes.decode("utf-8")
    rot = derived_initial_rotation(text)
    if abs(rot - KERBIN_INITIAL_ROTATION) > 1e-4:
        raise SystemExit("the source capsule reads Kerbin's initialRotation as %r, not 90" % rot)
    source_ut = source_save_ut(text)
    if not source_ut < SAVE_UT < LOOP_ANCHOR_UT:
        raise SystemExit("SAVE_UT must sit between the source clock and the loop anchor")
    start, end = _vessel_span(text, VESSEL_NAME)
    vessel = text[start:end]
    vessel = (derive_in_air_vessel(vessel, source_ut) if leaf == IN_AIR
              else derive_suborbital_vessel(vessel))
    text = text[:start] + vessel + text[end:]
    text = _set(text, "UT", _fmt(SAVE_UT), 2)
    return text.encode("utf-8")


def derive_loadmeta(source_bytes: bytes) -> bytes:
    return _set(source_bytes.decode("utf-8"), "UT", _fmt(SAVE_UT), 0).encode("utf-8")


def build_files(leaf: str) -> dict:
    files = {}
    for dirpath, _dirs, names in os.walk(SOURCE_DIR):
        for name in names:
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, SOURCE_DIR).replace(os.sep, "/")
            data = _read(path)
            if rel == SFS:
                data = derive_sfs(data, leaf)
            elif rel == LOADMETA:
                data = derive_loadmeta(data)
            files[rel] = data
    if SFS not in files or LOADMETA not in files:
        raise SystemExit("%s lacks persistent.sfs or persistent.loadmeta" % SOURCE)
    return dict(sorted(files.items()))


def check() -> list:
    """Differences between a fresh derivation and the committed bytes (empty = OK)."""
    problems = []
    for leaf in TARGETS:
        out_dir = os.path.join(SAVES, leaf)
        files = build_files(leaf)
        for rel, data in files.items():
            path = os.path.join(out_dir, rel)
            if not os.path.isfile(path):
                problems.append("missing %s/%s" % (leaf, rel))
            elif _read(path) != data:
                problems.append("%s/%s differs from a fresh derivation" % (leaf, rel))
        committed = set()
        for root, _dirs, names in os.walk(out_dir):
            for n in names:
                committed.add(os.path.relpath(os.path.join(root, n), out_dir).replace(os.sep, "/"))
        for extra in sorted(committed - set(files)):
            problems.append("unexpected file in %s: %s" % (leaf, extra))
    return problems


def _write(leaf: str, files: dict) -> None:
    out_dir = os.path.join(SAVES, leaf)
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    for rel, data in files.items():
        path = os.path.join(out_dir, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote %d file(s) under %s" % (len(files), out_dir))


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixtures; exit 1 on drift")
    args = parser.parse_args(argv)
    if args.check:
        problems = check()
        for p in problems:
            print("DRIFT: " + p)
        print("far-watch pod fixtures: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    for leaf in TARGETS:
        _write(leaf, build_files(leaf))
    return 0


if __name__ == "__main__":
    sys.exit(main())
