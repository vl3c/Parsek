#!/usr/bin/env python3
"""Build the committed `miner-rover-depot` fixture BY CONSTRUCTION (a miner that can dock a depot).

WHY THIS FIXTURE EXISTS. The mining program's route half (lane MINE-5b,
`docs/dev/research/mining-isru-test-program-2026-10-17.md`; todo MINING-ISRU-UNTESTED-END-TO-END)
needs a mining recording that docks to a second vessel, moves Ore into it and undocks, so a
supply route can be created from it with a HARVEST origin. V's real miner carries no docking
port and no committed save holds a depot next to it. This fixture adds both by construction:
the miner drives straight forward into the depot, so no steering is needed.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/miner-rover-landed`
  (the `Mining Rover RC` at index 0, active, tanks emptied, drills retracted, every converter
  off and every clock at UT 30; the `Observer Probe` at index 1; the inert ParsekScenario
  node; `AddOns/`).

  THE MINER'S PORT. A Clamp-O-Tron Jr (`dockingPort3`) is appended as part 27, stacked on the
  free top node of the miner's `adapterSmallMiniShort` (part 26, child of the probe core, the
  front end of the miner along its driving axis +y; wheels drive toward the control point's
  up). The adapter's `attN = top, -1` becomes `attN = top, 27`; the port is `parent = 26`,
  `attN = bottom, 26`, `attN = top, -1`, `position` = the adapter's plus its top node height
  (0.1875), `rotation` identity, so its face (the port's top node, 0.1474114 above its origin,
  measured from two stacked ports in `lab-lko-sandbox`) points along +y. The PART node is the
  `lab-lko-sandbox` port's, with the identity, placement and clock keys set to the miner's
  conventions (`mid` / `launchID` / `flag` of the miner, temperatures of the adapter),
  `ModuleDockingNode state = Ready` (a free port; the donor's `PreAttached` is a port stacked
  on another node) and the mod-only `KOSNameTag` module dropped. `persistentId`, `uid` and
  `cid` are derived (sha256 of `SEED` and a tag), asserted unique in the save.

  THE DEPOT, index 2, `Ore Depot`. A copy of the ported miner with its four MiniDrills dropped
  (part indices 3, 4, 20, 21, a closed symmetry set of leaves; every part-index reference of
  the 24 surviving parts renumbered, as `build_miner_rover_variants.py` does for the RTGs), so
  it holds the same two empty RadialOreTanks (75 each), the same wheels and the same port at
  the same height above the ground. Drills are what make the miner the harvest origin; the
  depot has none. Being lighter, the depot is never the dominant vessel of the dock
  (`Vessel.GetDominantVessel`: same `vesselType` Probe, larger mass wins), so the miner stays
  the root side and the undock splits the depot off as the new vessel.
  PLACEMENT (decompiled ProtoVessel / CelestialBody, KSP 1.12.5): a vessel's `lat` / `lon` /
  `alt` place its ROOT part at `GetWorldSurfacePosition`, and its `rot` is `srfRelRotation`
  (world = bodyTransform.rotation * rot), both in the body-relative frame where the surface
  normal is (cos lat cos lon, sin lat, cos lat sin lon). The depot's `rot` is the miner's
  turned 180 degrees about the miner's local z (its ground normal): (ay, -ax, aw, -az), so
  the two ports face each other on the same slope. Its root sits `GAP_METERS` + twice the
  root-to-face distance ahead of the miner's root along the miner's forward (rot * +y): the
  faces are `GAP_METERS` apart. `DEPOT_LAT` / `DEPOT_LON` / `DEPOT_ALT` are those numbers,
  committed as literals (libm last-ulp differences must not change the bytes) and
  re-derived by the builder within 1e-9. `skipGroundPositioning = True` as the miner has it,
  so KSP places the depot exactly there.
  DERIVED, deterministically (sha256 of `SEED` and the old value): the vessel `pid`, the
  vessel `persistentId`, every part `persistentId`, `uid` and `cid`, one `mid`; `launchID` 4;
  asserted disjoint from every other id in the save. `name` = `Ore Depot`.

  EDITED IN THE SAVE: the `Title` and `Description`; `persistent.loadmeta` vesselCount 3.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_miner_rover_depot.py` runs it in-process. Run without arguments, the
builder writes the fixture and prints the ids a lane names (vessel pids and the depot's
first ore tank's part persistentId).

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import math
import os
import re
import shutil
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "miner-rover-landed")
PORT_DONOR_SFS = os.path.join(SAVES, "lab-lko-sandbox", "persistent.sfs")
OUT_DIR = os.path.join(SAVES, "miner-rover-depot")

sys.path.insert(0, _HERE)
import build_miner_rover_landed as landed  # noqa: E402
import build_miner_rover_variants as variants  # noqa: E402

LF = "\n"
EDITED = ("persistent.sfs", "persistent.loadmeta")
SEED = "miner-rover-depot/v1"
TITLE = "miner-rover-depot (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: the real mining rover with a "
               "docking port and an ore depot parked ahead of it).")

BASE_PART_COUNT = 27
ADAPTER_INDEX = 26
ADAPTER_NAME = "adapterSmallMiniShort"
ADAPTER_TOP_NODE = 0.1875
PORT_NAME = "dockingPort3"
PORT_TOP_NODE = 0.1474114
PORT_STRIPPED_MODULE = "KOSNameTag"
DRILL_NAME = "MiniDrill"
DRILL_INDICES = (3, 4, 20, 21)
ORE_TANK_NAME = "RadialOreTank"

DEPOT_NAME = "Ore Depot"
DEPOT_LAUNCH_ID = "4"
GAP_METERS = 1.5
KERBIN_RADIUS = 600000.0
DEPOT_LAT = "0.029842483513667382"
DEPOT_LON = "-74.72475809576127"
DEPOT_ALT = "65.87756411731243"
COORD_TOLERANCE = 1e-9

_PID_RE = re.compile(r"^(\t+)persistentId = (\d+)$")
_PART_KEY_RE = re.compile(r"^\t\t\t\t(uid|cid|mid|launchID) = (\d+)$")


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def _derive_guid(tag: str) -> str:
    return hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:32]


def _top(block: List[str], key: str) -> str:
    prefix = "\t\t\t" + key + " = "
    hits = [l[len(prefix):] for l in block if l.startswith(prefix)]
    assert len(hits) == 1, "vessel key %r found %d times" % (key, len(hits))
    return hits[0]


def _part_key(part: List[str], key: str) -> int:
    prefix = "\t\t\t\t" + key + " = "
    hits = [i for i, l in enumerate(part) if l.startswith(prefix)]
    assert len(hits) == 1, "part key %r found %d times" % (key, len(hits))
    return hits[0]


def _set_part(part: List[str], key: str, value: str) -> None:
    part[_part_key(part, key)] = "\t\t\t\t" + key + " = " + value


def _ids(lines: List[str], key: str) -> set:
    rx = re.compile(r"^\t+" + key + r" = (\d+)$")
    return {m.group(1) for m in (rx.match(l) for l in lines) if m}


# --- geometry -------------------------------------------------------------------------------

def _qrot(q: Tuple[float, ...], v: Tuple[float, ...]) -> Tuple[float, float, float]:
    x, y, z, w = q
    vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def _surface_normal(lat_deg: float, lon_deg: float) -> Tuple[float, float, float]:
    la, lo = math.radians(lat_deg), math.radians(lon_deg)
    return (math.cos(la) * math.cos(lo), math.sin(la), math.cos(la) * math.sin(lo))


def _parse_quat(text: str) -> Tuple[float, ...]:
    q = tuple(float(c) for c in text.split(","))
    assert len(q) == 4
    return q


def depot_rotation_text(miner_rot: str) -> str:
    """The miner's rot turned 180 degrees about its local z: (ay, -ax, aw, -az)."""
    ax, ay, az, aw = miner_rot.split(",")

    def neg(s: str) -> str:
        return s[1:] if s.startswith("-") else "-" + s
    return ",".join((ay, neg(ax), aw, neg(az)))


def port_face_offset(adapter_y: float) -> float:
    """Root-to-port-face distance along +y (the port's origin plus its top node)."""
    return adapter_y + ADAPTER_TOP_NODE + PORT_TOP_NODE


def depot_coordinates(miner: List[str], adapter_y: float) -> Tuple[float, float, float]:
    lat, lon = float(_top(miner, "lat")), float(_top(miner, "lon"))
    alt = float(_top(miner, "alt"))
    q = _parse_quat(_top(miner, "rot"))
    n = _surface_normal(lat, lon)
    up = _qrot(q, (0.0, 0.0, -1.0))
    assert sum(a * b for a, b in zip(up, n)) > 0.999, "miner rot does not stand on its normal"
    fwd = _qrot(q, (0.0, 1.0, 0.0))
    dist = 2.0 * port_face_offset(adapter_y) + GAP_METERS
    root = tuple(c * (KERBIN_RADIUS + alt) + dist * f for c, f in zip(n, fwd))
    mag = math.sqrt(sum(c * c for c in root))
    return (math.degrees(math.asin(root[1] / mag)),
            math.degrees(math.atan2(root[2], root[0])),
            mag - KERBIN_RADIUS)


# --- the miner's port -----------------------------------------------------------------------

def _port_donor_part() -> List[str]:
    lines = landed._read(PORT_DONOR_SFS).split(LF)
    for i, line in enumerate(lines):
        if line == "\t\t\tPART" and lines[i + 2] == "\t\t\t\tname = " + PORT_NAME:
            return list(lines[i:landed._block_end(lines, i)])
    raise AssertionError("no %s in the port donor" % PORT_NAME)


def _strip_module(part: List[str], name: str) -> List[str]:
    out = []
    i = 0
    dropped = 0
    while i < len(part):
        if part[i] == "\t\t\t\tMODULE" and part[i + 2] == "\t\t\t\t\tname = " + name:
            i = landed._block_end(part, i)
            dropped += 1
            continue
        out.append(part[i])
        i += 1
    assert dropped == 1, "%s module found %d times" % (name, dropped)
    return out


def build_port(adapter: List[str], used_pids: set, used_uids: set) -> List[str]:
    part = _port_donor_part()
    part = _strip_module(part, PORT_STRIPPED_MODULE)
    pid = _derive_uint("miner-port|persistentId")
    uid = _derive_uint("miner-port|uid")
    assert str(pid) not in used_pids and str(uid) not in used_uids, "port id collision"
    adapter_pos = adapter[_part_key(adapter, "position")].split(" = ", 1)[1].split(",")
    adapter_y = float(adapter_pos[1])
    port_y = repr(adapter_y + ADAPTER_TOP_NODE)
    for key, value in (
            ("cid", str(_derive_uint("miner-port|cid"))),
            ("uid", str(uid)),
            ("mid", adapter[_part_key(adapter, "mid")].split(" = ", 1)[1]),
            ("persistentId", str(pid)),
            ("launchID", adapter[_part_key(adapter, "launchID")].split(" = ", 1)[1]),
            ("parent", str(ADAPTER_INDEX)),
            ("position", ",".join((adapter_pos[0], port_y, adapter_pos[2]))),
            ("rotation", "0,0,0,1"),
            ("sepI", "-1"),
            ("srfN", ", -1"),
            ("temp", adapter[_part_key(adapter, "temp")].split(" = ", 1)[1]),
            ("tempExt", adapter[_part_key(adapter, "tempExt")].split(" = ", 1)[1]),
            ("tempExtUnexp", adapter[_part_key(adapter, "tempExtUnexp")].split(" = ", 1)[1]),
            ("staticPressureAtm",
             adapter[_part_key(adapter, "staticPressureAtm")].split(" = ", 1)[1]),
            ("flag", adapter[_part_key(adapter, "flag")].split(" = ", 1)[1])):
        _set_part(part, key, value)
    attn = [i for i, l in enumerate(part) if l.startswith("\t\t\t\tattN = ")]
    assert [part[i] for i in attn] == ["\t\t\t\tattN = top, 5", "\t\t\t\tattN = bottom, 0"]
    part[attn[0]] = "\t\t\t\tattN = top, -1"
    part[attn[1]] = "\t\t\t\tattN = bottom, %d" % ADAPTER_INDEX
    state = [i for i, l in enumerate(part) if l == "\t\t\t\t\tstate = PreAttached"]
    assert len(state) == 1, "port module state found %d times" % len(state)
    part[state[0]] = "\t\t\t\t\tstate = Ready"
    assert "\t\t\t\t\tdockUId = 0" in part
    return part


def add_port(miner: List[str], used_pids: set, used_uids: set) -> List[str]:
    blocks = variants.part_blocks(miner)
    assert len(blocks) == BASE_PART_COUNT, "base miner has %d parts" % len(blocks)
    a_start, a_end = blocks[ADAPTER_INDEX]
    adapter = miner[a_start:a_end]
    assert adapter[2] == "\t\t\t\tname = " + ADAPTER_NAME, adapter[2]
    top = [i for i, l in enumerate(adapter) if l == "\t\t\t\tattN = top, -1"]
    assert len(top) == 1, "the adapter's top node must be free"
    port = build_port(adapter, used_pids, used_uids)
    out = list(miner)
    out[a_start + top[0]] = "\t\t\t\tattN = top, %d" % BASE_PART_COUNT
    end = blocks[-1][1]
    out[end:end] = port
    assert len(variants.part_blocks(out)) == BASE_PART_COUNT + 1
    return out


# --- the depot ------------------------------------------------------------------------------

def _drop_drills(vessel: List[str]) -> List[str]:
    blocks = variants.part_blocks(vessel)
    drills = [i for i, (s, _e) in enumerate(blocks)
              if vessel[s + 2] == "\t\t\t\tname = " + DRILL_NAME]
    assert tuple(drills) == DRILL_INDICES, "drill part indices %r" % drills
    dropped = set(DRILL_INDICES)
    remap = {}
    for old in range(len(blocks)):
        if old not in dropped:
            remap[old] = len(remap)
    dropped_ids = set()
    for idx in DRILL_INDICES:
        s, e = blocks[idx]
        for line in vessel[s:e]:
            key = line.strip().split(" = ", 1)
            if line.startswith("\t\t\t\t") and not line.startswith("\t\t\t\t\t") \
                    and key[0] in ("uid", "persistentId"):
                dropped_ids.add(key[1])
    head = vessel[:blocks[0][0]]
    tail = vessel[blocks[-1][1]:]
    body: List[str] = []
    for old, (s, e) in enumerate(blocks):
        if old in dropped:
            continue
        for line in vessel[s:e]:
            for rx in variants._INDEX_REFS:
                m = rx.match(line)
                if not m:
                    continue
                ref = int(m.group(2))
                if ref >= 0:
                    assert ref not in dropped, \
                        "part %d references dropped part %d: %r" % (old, ref, line)
                    line = m.group(1) + str(remap[ref]) + (m.group(3) if m.lastindex >= 3 else "")
                break
            for ident in dropped_ids:
                assert not line.endswith(" " + ident), \
                    "surviving part %d names a dropped id: %r" % (old, line)
            body.append(line)
    out = head + body + tail
    assert len(variants.part_blocks(out)) == len(blocks) - len(DRILL_INDICES)
    assert not any(l == "\t\t\t\tname = " + DRILL_NAME for l in out), "a drill survived"
    assert not any(l.strip() == "name = ModuleResourceHarvester" for l in out)
    return out


def build_depot(ported_miner: List[str], used: Dict[str, set]) -> List[str]:
    depot = _drop_drills(ported_miner)
    mid = str(_derive_uint("depot|mid"))
    assert mid not in used["mid"]
    out = []
    seen_pids = set()
    seen_uids = set()
    for line in depot:
        m = _PID_RE.match(line)
        k = _PART_KEY_RE.match(line)
        if line.startswith("\t\t\tpid = "):
            line = "\t\t\tpid = " + _derive_guid("depot|vessel-pid")
        elif m:
            new_pid = _derive_uint("depot|persistentId|" + m.group(2))
            assert new_pid not in seen_pids, "derived persistentId collision"
            assert str(new_pid) not in used["persistentId"], "persistentId collides in the save"
            seen_pids.add(new_pid)
            line = "%spersistentId = %d" % (m.group(1), new_pid)
        elif k and k.group(1) == "uid":
            new_uid = _derive_uint("depot|uid|" + k.group(2))
            assert new_uid not in seen_uids, "derived uid collision"
            assert str(new_uid) not in used["uid"], "uid collides in the save"
            seen_uids.add(new_uid)
            line = "\t\t\t\tuid = %d" % new_uid
        elif k and k.group(1) == "cid":
            line = "\t\t\t\tcid = %d" % _derive_uint("depot|cid|" + k.group(2))
        elif k and k.group(1) == "mid":
            line = "\t\t\t\tmid = " + mid
        elif k and k.group(1) == "launchID":
            line = "\t\t\t\tlaunchID = " + DEPOT_LAUNCH_ID
        out.append(line)
    parts = len(variants.part_blocks(out))
    assert len(seen_pids) == parts + 1, "vessel + every part persistentId re-derived"
    assert len(seen_uids) == parts, "every part uid re-derived"

    blocks = variants.part_blocks(ported_miner)
    adapter = ported_miner[blocks[ADAPTER_INDEX][0]:blocks[ADAPTER_INDEX][1]]
    adapter_y = float(adapter[_part_key(adapter, "position")].split(" = ", 1)[1].split(",")[1])
    lat, lon, alt = depot_coordinates(ported_miner, adapter_y)
    for name, got, want in (("lat", lat, DEPOT_LAT), ("lon", lon, DEPOT_LON),
                            ("alt", alt, DEPOT_ALT)):
        assert abs(got - float(want)) < COORD_TOLERANCE, \
            "depot %s re-derived as %r, committed %s" % (name, got, want)
    landed._set_top(out, "name", DEPOT_NAME)
    landed._set_top(out, "lat", DEPOT_LAT)
    landed._set_top(out, "lon", DEPOT_LON)
    landed._set_top(out, "alt", DEPOT_ALT)
    landed._set_top(out, "rot", depot_rotation_text(_top(ported_miner, "rot")))
    assert _top(out, "skipGroundPositioning") == "True"
    assert _top(out, "type") == "Probe"
    return out


def depot_ids() -> Dict[str, int]:
    """The ids a lane names: vessel persistentIds and the depot's first ore tank."""
    sfs = build_sfs()
    lines = sfs.split(LF)
    blocks = landed.vessel_blocks(lines)
    depot = lines[blocks[2][0]:blocks[2][1]]
    tanks = []
    for s, e in variants.part_blocks(depot):
        if depot[s + 2] == "\t\t\t\tname = " + ORE_TANK_NAME:
            tanks.append(int(depot[s:e][_part_key(depot[s:e], "persistentId")].split(" = ")[1]))
    return {"minerPersistentId": int(_top(lines[blocks[0][0]:blocks[0][1]], "persistentId")),
            "depotPersistentId": int(_top(depot, "persistentId")),
            "depotOreTankPersistentId": tanks[0],
            "depotOreTanks": len(tanks)}


# --- the save -------------------------------------------------------------------------------

def build_sfs() -> str:
    base = landed._read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "miner-rover-landed is committed LF"
    lines = base.split(LF)
    blocks = landed.vessel_blocks(lines)
    assert [b[2] for b in blocks] == [landed.ROVER_NAME, landed.PROBE_NAME], \
        [b[2] for b in blocks]
    (m_start, m_end, _), (_p_start, p_end, _) = blocks
    used = {key: _ids(lines, key) for key in ("persistentId", "uid", "mid")}
    miner = add_port(lines[m_start:m_end], used["persistentId"], used["uid"])
    used["persistentId"] |= _ids(miner, "persistentId")
    used["uid"] |= _ids(miner, "uid")
    depot = build_depot(miner, used)
    assert _top(depot, "pid") not in {_top(lines[s:e], "pid") for s, e, _ in blocks}
    lines[p_end:p_end] = depot
    lines[m_start:m_end] = miner
    assert "\t\tactiveVessel = 0" in lines, "the miner stays the active vessel"
    title = lines.index("\tTitle = " + landed.TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + landed.DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTION
    return LF.join(lines)


def build_loadmeta() -> str:
    meta = landed._read(os.path.join(BASE_DIR, "persistent.loadmeta"))
    meta, n = re.subn(r"^vesselCount = 2(\r?\n)", r"vesselCount = 3\1", meta, count=1, flags=re.M)
    assert n == 1, "loadmeta vesselCount = 2 not found"
    return meta


def expected_files() -> Dict[str, bytes]:
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = landed._read_bytes(path)
    files["persistent.sfs"] = build_sfs().encode("utf-8")
    files["persistent.loadmeta"] = build_loadmeta().encode("utf-8")
    return files


def committed_problems() -> List[str]:
    want = expected_files()
    have = {}
    if os.path.isdir(OUT_DIR):
        for root, _dirs, names in os.walk(OUT_DIR):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, OUT_DIR).replace(os.sep, "/")] = \
                    landed._read_bytes(path)
    problems = []
    for rel in sorted(set(want) | set(have)):
        if rel not in have:
            problems.append("missing " + rel)
        elif rel not in want:
            problems.append("unexpected " + rel)
        elif have[rel] != want[rel]:
            problems.append("drift in " + rel)
    return problems


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true",
                    help="re-derive and compare against the committed fixture")
    args = ap.parse_args(argv)
    if args.check:
        problems = committed_problems()
        for p in problems:
            print("DRIFT: " + p)
        print("OK" if not problems else "FAIL")
        return 0 if not problems else 1
    if os.path.isdir(OUT_DIR):
        shutil.rmtree(OUT_DIR)
    for rel, data in expected_files().items():
        path = os.path.join(OUT_DIR, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote " + OUT_DIR + " " + " ".join("%s=%d" % kv for kv in sorted(depot_ids().items())))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
