#!/usr/bin/env python3
"""Build the committed `miner-rover-relay` fixture BY CONSTRUCTION (an Ore shuttle between two depots).

WHY THIS FIXTURE EXISTS. Lane MINE-4 (`docs/dev/research/mining-isru-test-program-2026-10-17.md`,
todo MINING-ISRU-UNTESTED-END-TO-END) needs a supply route whose ORIGIN is a landed Ore depot
the transport docks to and loads from (the mid-tree docked origin, logistics design 19.2.5 /
M-MIS-5 P2b), delivering to a second landed vessel. The same host serves IR-9 (a visitor docked
to the destination while a delivery is written; todo INTEGRATION-COVERAGE-LOGISTICS-REWIND-LEDGER)
and IR-7 (a Rewind-to-Launch of the route's own backing tree). No seam verb steers, so the three
vessels stand in one line: the miner docks the depot ahead of it driving forward, and the silo
behind it driving backwards, with a second port on its rear.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/miner-rover-depot`
  (index 0 the ported `Mining Rover RC`, active; index 1 the `Observer Probe`; index 2 the
  `Ore Depot` 1.5 m ahead, ports facing).

  THE MINER'S REAR PORT. A second Clamp-O-Tron Jr (`dockingPort3`) is appended as part 28,
  stacked on the free bottom node of the miner's `fuelTankSmall` (part 7, the rear end of the
  miner along -y; FL-T200 `node_stack_bottom` at -0.55525, stock cfg). The tank's
  `attN = bottom, -1` becomes `attN = bottom, 28`; the port is `parent = 7`, `attN = bottom, 7`,
  `attN = top, -1`, `position` = the tank's minus that node height, `rotation` = 180 degrees
  about z (0,0,1,0), so its face (its top node, `PORT_TOP_NODE` above its origin) points along
  -y. The PART node is built exactly as the front port's (`build_miner_rover_depot.build_port`
  conventions); its ids are derived from `SEED`.

  BALLAST AND CARGO. The miner's `fuelTankSmall` is filled (LiquidFuel 90, Oxidizer 110: about
  1.0 t) and the depot's two RadialOreTanks are filled (Ore 75 each: 1.5 t). The miner (dry
  3.625 t plus a port) outweighs the full depot (dry 2.625 t plus 1.5 t of Ore), so it stays
  the dominant vessel of both docks (`Vessel.GetDominantVessel`: same `vesselType` Probe,
  larger mass wins) and every undock splits the other half off as the new vessel, the miner
  staying active. No engine aboard burns the fuel; the MiniISRU stays off.

  THE SILO, index 3, `Ore Silo`. A copy of the depot as `miner-rover-depot` commits it (no
  drills, empty Ore tanks, one front port), with the MINER's `rot` (same heading), placed
  behind the miner so its front port faces the miner's rear port across `GAP_METERS`: its
  root sits (rear face distance + `GAP_METERS` + front face distance) behind the miner's root
  along the miner's forward (rot * +y), computed as the depot builder computes the depot.
  `SILO_LAT` / `SILO_LON` / `SILO_ALT` are those numbers, committed as literals and re-derived
  within 1e-9. DERIVED, deterministically (sha256 of `SEED` and the old value): the vessel
  `pid`, the vessel `persistentId`, every part `persistentId`, `uid` and `cid`, one `mid`;
  `launchID` 5; asserted disjoint from every other id in the save.

  EDITED IN THE SAVE: the `Title` and `Description`; `persistent.loadmeta` vesselCount 4.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_miner_rover_relay.py` runs it in-process. Run without arguments, the
builder writes the fixture and prints the ids a lane names.

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
BASE_DIR = os.path.join(SAVES, "miner-rover-depot")
OUT_DIR = os.path.join(SAVES, "miner-rover-relay")

sys.path.insert(0, _HERE)
import build_miner_rover_depot as depot  # noqa: E402

landed = depot.landed
variants = depot.variants

LF = "\n"
EDITED = ("persistent.sfs", "persistent.loadmeta")
SEED = "miner-rover-relay/v1"
TITLE = "miner-rover-relay (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: the real mining rover with docking "
               "ports front and rear, a full ore depot ahead of it and an empty ore silo "
               "behind it).")

BASE_TITLE = depot.TITLE
BASE_DESCRIPTION = depot.DESCRIPTION
BASE_PART_COUNT = depot.BASE_PART_COUNT + 1  # the depot builder's front port
TANK_INDEX = 7
TANK_NAME = "fuelTankSmall"
TANK_BOTTOM_NODE = 0.55525
REAR_PORT_ROTATION = "0,0,1,0"
BALLAST = (("LiquidFuel", "90"), ("Oxidizer", "110"))
DEPOT_ORE = "75"

SILO_NAME = "Ore Silo"
SILO_LAUNCH_ID = "5"
SILO_LAT = "0.02909251319095639"
SILO_LON = "-74.72473027503361"
SILO_ALT = "65.7673434818862"
COORD_TOLERANCE = 1e-9


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def _derive_guid(tag: str) -> str:
    return hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:32]


_top = depot._top
_part_key = depot._part_key
_set_part = depot._set_part
_ids = depot._ids


def _part_value(part: List[str], key: str) -> str:
    return part[_part_key(part, key)].split(" = ", 1)[1]


def _set_resource(part: List[str], resource: str, amount: str) -> None:
    hits = [i for i, l in enumerate(part) if l == "\t\t\t\t\tname = " + resource]
    assert len(hits) == 1, "resource %r found %d times" % (resource, len(hits))
    i = hits[0] + 1
    assert part[i].startswith("\t\t\t\t\tamount = "), part[i]
    assert part[i + 1] == "\t\t\t\t\tmaxAmount = " + amount, \
        "%s maxAmount is %r, filling to %s" % (resource, part[i + 1], amount)
    part[i] = "\t\t\t\t\tamount = " + amount


# --- the miner's rear port ------------------------------------------------------------------

def build_rear_port(tank: List[str], used_pids: set, used_uids: set) -> List[str]:
    part = depot._port_donor_part()
    part = depot._strip_module(part, depot.PORT_STRIPPED_MODULE)
    pid = _derive_uint("rear-port|persistentId")
    uid = _derive_uint("rear-port|uid")
    assert str(pid) not in used_pids and str(uid) not in used_uids, "rear port id collision"
    tank_pos = _part_value(tank, "position").split(",")
    port_y = repr(float(tank_pos[1]) - TANK_BOTTOM_NODE)
    for key, value in (
            ("cid", str(_derive_uint("rear-port|cid"))),
            ("uid", str(uid)),
            ("mid", _part_value(tank, "mid")),
            ("persistentId", str(pid)),
            ("launchID", _part_value(tank, "launchID")),
            ("parent", str(TANK_INDEX)),
            ("position", ",".join((tank_pos[0], port_y, tank_pos[2]))),
            ("rotation", REAR_PORT_ROTATION),
            ("sepI", "-1"),
            ("srfN", ", -1"),
            ("temp", _part_value(tank, "temp")),
            ("tempExt", _part_value(tank, "tempExt")),
            ("tempExtUnexp", _part_value(tank, "tempExtUnexp")),
            ("staticPressureAtm", _part_value(tank, "staticPressureAtm")),
            ("flag", _part_value(tank, "flag"))):
        _set_part(part, key, value)
    attn = [i for i, l in enumerate(part) if l.startswith("\t\t\t\tattN = ")]
    assert [part[i] for i in attn] == ["\t\t\t\tattN = top, 5", "\t\t\t\tattN = bottom, 0"]
    part[attn[0]] = "\t\t\t\tattN = top, -1"
    part[attn[1]] = "\t\t\t\tattN = bottom, %d" % TANK_INDEX
    state = [i for i, l in enumerate(part) if l == "\t\t\t\t\tstate = PreAttached"]
    assert len(state) == 1, "port module state found %d times" % len(state)
    part[state[0]] = "\t\t\t\t\tstate = Ready"
    assert "\t\t\t\t\tdockUId = 0" in part
    return part


def rear_face_offset(tank_y: float) -> float:
    """Root-to-rear-port-face distance along -y (the port's origin plus its top node)."""
    return -(tank_y - TANK_BOTTOM_NODE) + depot.PORT_TOP_NODE


def edit_miner(miner: List[str], used_pids: set, used_uids: set) -> List[str]:
    blocks = variants.part_blocks(miner)
    assert len(blocks) == BASE_PART_COUNT, "base miner has %d parts" % len(blocks)
    t_start, t_end = blocks[TANK_INDEX]
    tank = miner[t_start:t_end]
    assert tank[2] == "\t\t\t\tname = " + TANK_NAME, tank[2]
    bottom = [i for i, l in enumerate(tank) if l == "\t\t\t\tattN = bottom, -1"]
    assert len(bottom) == 1, "the tank's bottom node must be free"
    port = build_rear_port(tank, used_pids, used_uids)
    out = list(miner)
    out[t_start + bottom[0]] = "\t\t\t\tattN = bottom, %d" % BASE_PART_COUNT
    filled = out[t_start:t_end]
    for resource, amount in BALLAST:
        _set_resource(filled, resource, amount)
    out[t_start:t_end] = filled
    end = variants.part_blocks(out)[-1][1]
    out[end:end] = port
    assert len(variants.part_blocks(out)) == BASE_PART_COUNT + 1
    return out


def fill_depot(block: List[str]) -> List[str]:
    out = list(block)
    tanks = 0
    for s, e in variants.part_blocks(out):
        if out[s + 2] == "\t\t\t\tname = " + depot.ORE_TANK_NAME:
            part = out[s:e]
            _set_resource(part, "Ore", DEPOT_ORE)
            out[s:e] = part
            tanks += 1
    assert tanks == 2, "depot ore tanks %d" % tanks
    return out


# --- the silo -------------------------------------------------------------------------------

def silo_coordinates(miner: List[str]) -> Tuple[float, float, float]:
    lat, lon = float(_top(miner, "lat")), float(_top(miner, "lon"))
    alt = float(_top(miner, "alt"))
    q = depot._parse_quat(_top(miner, "rot"))
    n = depot._surface_normal(lat, lon)
    up = depot._qrot(q, (0.0, 0.0, -1.0))
    assert sum(a * b for a, b in zip(up, n)) > 0.999, "miner rot does not stand on its normal"
    fwd = depot._qrot(q, (0.0, 1.0, 0.0))
    blocks = variants.part_blocks(miner)
    tank = miner[blocks[TANK_INDEX][0]:blocks[TANK_INDEX][1]]
    adapter = miner[blocks[depot.ADAPTER_INDEX][0]:blocks[depot.ADAPTER_INDEX][1]]
    tank_y = float(_part_value(tank, "position").split(",")[1])
    adapter_y = float(_part_value(adapter, "position").split(",")[1])
    dist = rear_face_offset(tank_y) + depot.GAP_METERS + depot.port_face_offset(adapter_y)
    root = tuple(c * (depot.KERBIN_RADIUS + alt) - dist * f for c, f in zip(n, fwd))
    mag = math.sqrt(sum(c * c for c in root))
    return (math.degrees(math.asin(root[1] / mag)),
            math.degrees(math.atan2(root[2], root[0])),
            mag - depot.KERBIN_RADIUS)


def build_silo(base_depot: List[str], miner: List[str], used: Dict[str, set]) -> List[str]:
    mid = str(_derive_uint("silo|mid"))
    assert mid not in used["mid"]
    out = []
    seen_pids = set()
    seen_uids = set()
    for line in base_depot:
        m = depot._PID_RE.match(line)
        k = depot._PART_KEY_RE.match(line)
        if line.startswith("\t\t\tpid = "):
            line = "\t\t\tpid = " + _derive_guid("silo|vessel-pid")
        elif m:
            new_pid = _derive_uint("silo|persistentId|" + m.group(2))
            assert new_pid not in seen_pids, "derived persistentId collision"
            assert str(new_pid) not in used["persistentId"], "persistentId collides in the save"
            seen_pids.add(new_pid)
            line = "%spersistentId = %d" % (m.group(1), new_pid)
        elif k and k.group(1) == "uid":
            new_uid = _derive_uint("silo|uid|" + k.group(2))
            assert new_uid not in seen_uids, "derived uid collision"
            assert str(new_uid) not in used["uid"], "uid collides in the save"
            seen_uids.add(new_uid)
            line = "\t\t\t\tuid = %d" % new_uid
        elif k and k.group(1) == "cid":
            line = "\t\t\t\tcid = %d" % _derive_uint("silo|cid|" + k.group(2))
        elif k and k.group(1) == "mid":
            line = "\t\t\t\tmid = " + mid
        elif k and k.group(1) == "launchID":
            line = "\t\t\t\tlaunchID = " + SILO_LAUNCH_ID
        out.append(line)
    parts = len(variants.part_blocks(out))
    assert len(seen_pids) == parts + 1, "vessel + every part persistentId re-derived"
    assert len(seen_uids) == parts, "every part uid re-derived"

    lat, lon, alt = silo_coordinates(miner)
    for name, got, want in (("lat", lat, SILO_LAT), ("lon", lon, SILO_LON),
                            ("alt", alt, SILO_ALT)):
        assert abs(got - float(want)) < COORD_TOLERANCE, \
            "silo %s re-derived as %r, committed %s" % (name, got, want)
    landed._set_top(out, "name", SILO_NAME)
    landed._set_top(out, "lat", SILO_LAT)
    landed._set_top(out, "lon", SILO_LON)
    landed._set_top(out, "alt", SILO_ALT)
    landed._set_top(out, "rot", _top(miner, "rot"))
    assert _top(out, "skipGroundPositioning") == "True"
    assert _top(out, "type") == "Probe"
    return out


# --- the save -------------------------------------------------------------------------------

def build_sfs() -> str:
    base = landed._read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "miner-rover-depot is committed LF"
    lines = base.split(LF)
    blocks = landed.vessel_blocks(lines)
    assert [b[2] for b in blocks] == [landed.ROVER_NAME, landed.PROBE_NAME, depot.DEPOT_NAME], \
        [b[2] for b in blocks]
    (m_start, m_end, _), _probe, (d_start, d_end, _) = blocks
    used = {key: _ids(lines, key) for key in ("persistentId", "uid", "mid")}
    base_depot = lines[d_start:d_end]
    miner = edit_miner(lines[m_start:m_end], used["persistentId"], used["uid"])
    used["persistentId"] |= _ids(miner, "persistentId")
    used["uid"] |= _ids(miner, "uid")
    silo = build_silo(base_depot, miner, used)
    assert _top(silo, "pid") not in {_top(lines[s:e], "pid") for s, e, _ in blocks}
    lines[d_start:d_end] = fill_depot(base_depot) + silo
    lines[m_start:m_end] = miner
    assert "\t\tactiveVessel = 0" in lines, "the miner stays the active vessel"
    title = lines.index("\tTitle = " + BASE_TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + BASE_DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTION
    return LF.join(lines)


def build_loadmeta() -> str:
    meta = landed._read(os.path.join(BASE_DIR, "persistent.loadmeta"))
    meta, n = re.subn(r"^vesselCount = 3(\r?\n)", r"vesselCount = 4\1", meta, count=1, flags=re.M)
    assert n == 1, "loadmeta vesselCount = 3 not found"
    return meta


def _part_pids(block: List[str], name: str) -> List[int]:
    out = []
    for s, e in variants.part_blocks(block):
        if block[s + 2] == "\t\t\t\tname = " + name:
            out.append(int(_part_value(block[s:e], "persistentId")))
    return out


def relay_ids() -> Dict[str, int]:
    """The ids a lane names: vessel persistentIds, the ports and the first ore tanks."""
    lines = build_sfs().split(LF)
    blocks = landed.vessel_blocks(lines)
    miner, _probe, dep, silo = (lines[s:e] for s, e, _ in blocks)
    ports = _part_pids(miner, depot.PORT_NAME)
    return {"minerPersistentId": int(_top(miner, "persistentId")),
            "depotPersistentId": int(_top(dep, "persistentId")),
            "siloPersistentId": int(_top(silo, "persistentId")),
            "minerFrontPortPersistentId": ports[0],
            "minerRearPortPersistentId": ports[1],
            "minerOreTankPersistentId": _part_pids(miner, depot.ORE_TANK_NAME)[0],
            "siloOreTankPersistentId": _part_pids(silo, depot.ORE_TANK_NAME)[0]}


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
    print("wrote " + OUT_DIR + " " + " ".join("%s=%d" % kv for kv in sorted(relay_ids().items())))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
