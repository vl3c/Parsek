#!/usr/bin/env python3
"""Build the committed `miner-rover-landed` fixture BY CONSTRUCTION (the mining / ISRU host).

WHY THIS FIXTURE EXISTS. MINING-ISRU-UNTESTED-END-TO-END: no committed fixture carried a
drill, an ISRU, an ore tank or Ore, so no lane could mine ore or run a converter loop on a
real vessel. The roadmap's MINE-1 forge (`minmus-miner-landed`) needs part definitions
harvested from a live VAB session; this fixture side-steps that with a REAL vessel V built
and flew in his dev install (`Mining Rover RC`, sandbox save `isru`): it mined ore and ran
the ISRU there, so its part and module state is what stock KSP itself wrote.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: `fixtures/saves/fresh-sandbox` (SANDBOX, no
  vessels, `activeVessel = -1`, FLIGHTSTATE `UT = 0`, no ParsekScenario node).
  DONOR: `fixtures/donors/isru-mining-rover/mining-rover-rc.vessel`, the depth-2 VESSEL
  block extracted verbatim from V's collected persistent.sfs (see that directory's
  README). 27 parts: probeStackSmall, advSasModule, MiniISRU, MiniDrill x4,
  RadialOreTank x2, fuelTankSmall, radialRCSTank x2, roverWheel1 x4, rtg x4,
  radPanelSm x4, ksp.r.largeBatteryPack x2, adapterSmallMiniShort; crewless; LANDED
  just west of the KSC (lat 0.0296, lon -74.7247).
  INSERTED as vessel index 0 (the active vessel) at the end of FLIGHTSTATE, where KSP
  writes its VESSEL nodes (after `commNetUIModeFlight`, before the closing brace).
  IDENTITIES verbatim: the vessel `pid`, the vessel `persistentId` (110406740), every
  part `persistentId` and `uid`. The donor is in no other fixture, so nothing collides.
  EDITED IN THE CLONE, so the lane starts from a quiet, empty rover:
    `lct` / `lastUT` / ORBIT `EPH` = 30 and `met` = 0 (re-stamped to the fixture UT);
    every `lastUpdateTime` (the drills' harvesters and the ISRU's converters) = 30, so
    no converter catches up across the donor's 3626 s on load;
    `amount` = 0 on every Ore, LiquidFuel, Oxidizer and MonoPropellant RESOURCE
    (`maxAmount` kept; ElectricCharge verbatim), so the drills and the ISRU have room
    to fill;
    `IsCooling` = True on the four radPanelSm ModuleActiveRadiator modules (V's save had
    them False). LOAD-BEARING: stock ModuleCoreHeat.MoveCoreEnergyToRadiators counts
    only radiators whose IsCooling is set, so with them off nothing drains the drills'
    and the ISRU's core heat. MINE-2's first flight (2026-10-17) warped the rover on
    rails with them off, and all ten 1200 K parts (four RTGs, four wheels, two
    batteries) exploded about 28 minutes in while it was packed (inferred overheat:
    a packed part has no collision path). A mining rig runs with its radiators on;
    switching them on is what a player does before a long warp;
    every `MODULE` whose `name` starts with `ModuleRestock` STRIPPED (10 blocks: two on
    the MiniISRU, one on each roverWheel1, one on each radPanelSm). The harness's
    stock-minimal instance has no ReStock; KSP's ProtoPartSnapshot matches modules by
    name, so the removal leaves every stock module's state where it was.
  ASSERTED UNCHANGED: crewless, LANDED, all four ModuleAnimationGroup `isDeployed =
  False`, every `IsActivated = False` (converters and harvesters off).
  (The radiators are the one switch turned ON; see above.)
  SPLICED: the INERT `SCENARIO{name=ParsekScenario}` node (name, scene,
  missionHideArchived, gameStateEventCount), copied verbatim from
  `fixtures/saves/b1-pad-craft` and placed after the last stock SCENARIO, as every
  flown pad fixture carries it. LOAD-BEARING: a save with a focusable vessel boots
  through the seam's FLIGHT route, which never calls UpdateScenarioModules, so a
  node-less save boots with no ParsekScenario MODULE and Parsek is inert for the whole
  flight (CL-1 flight 1; `test_every_node_less_fixture_is_vessel_less`). Three values
  is also a footprint by `PreParsekBackup.HasParsekGameplayFootprint`, so no pre-Parsek
  backup fires on the first load.
  SECOND VESSEL, index 1: a FLIGHT host for after Rewind-to-Launch strips the rover.
  TimeJump and Real Spawn Control need FLIGHT, and a save whose only vessel was the
  stripped rover reloads at the Space Center. CLONED from the crewless 13-part `Kerbal X
  Probe` (type Probe, ORBITING Kerbin, PE ~93 km) in `fixtures/saves/eva2-lko-crewed`;
  RENAMED `Observer Probe` (vessel `name` only) so logs never confuse it with a Kerbal
  X. DERIVED, deterministically (sha256 of `PROBE_SEED` and the old value), so the clone
  shares no identity with its donor: the vessel `pid` (launch Guid), the vessel
  `persistentId` and EVERY part `persistentId`; the builder asserts none collides with
  the rover's. Part `uid`s stay verbatim; the builder asserts they do not collide with
  the rover's either. `lct` / `lastUT` / ORBIT `EPH` = 30 and `met` = 0 (the other
  orbital elements verbatim, so the probe sits at a different point of the same orbit);
  every `lastUpdateTime` would be 30, and the donor has none. Its MechJeb module data
  (inert on a stock install) stays verbatim.
  EDITED IN THE SAVE: FLIGHTSTATE `UT = 30`, `activeVessel = 0` (the rover); the
  `Title` (so the Load menu names the fixture) and `Description`;
  `persistent.loadmeta`'s `vesselCount` 0 -> 2 and `UT` 0 -> 30.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_miner_rover_landed.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
FIXTURES = os.path.join(HARNESS, "fixtures")
SAVES = os.path.join(FIXTURES, "saves")
BASE_DIR = os.path.join(SAVES, "fresh-sandbox")
DONOR_VESSEL = os.path.join(FIXTURES, "donors", "isru-mining-rover", "mining-rover-rc.vessel")
SCENARIO_DONOR_SFS = os.path.join(SAVES, "b1-pad-craft", "persistent.sfs")
PROBE_DONOR_SFS = os.path.join(SAVES, "eva2-lko-crewed", "persistent.sfs")
OUT_DIR = os.path.join(SAVES, "miner-rover-landed")

BASE_TITLE = "fresh-sandbox (SANDBOX)"
BASE_DESCRIPTION = "Parsek autotest fixture (synthetic, file-constructed)."
TITLE = "miner-rover-landed (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: fresh-sandbox plus a real "
               "mining rover).")
LF = "\n"
EDITED = ("persistent.sfs", "persistent.loadmeta")

FIXTURE_UT = "30"
ROVER_NAME = "Mining Rover RC"
ROVER_PERSISTENT_ID = 110406740
ROVER_PID = "e585079827a441ee8026bf1afad27961"
EMPTIED_RESOURCES = {"Ore": 2, "LiquidFuel": 1, "Oxidizer": 1, "MonoPropellant": 2}
RESTOCK_PREFIX = "ModuleRestock"
RESTOCK_STRIPPED_BY_PART = {"MiniISRU": 2, "roverWheel1": 4, "radPanelSm": 4}
LAST_UPDATE_TIME_COUNT = 25
IS_DEPLOYED_COUNT = 4
IS_ACTIVATED_COUNT = 16
PART_COUNT = 27
RADIATOR_COUNT = 4

PROBE_DONOR_NAME = "Kerbal X Probe"
PROBE_NAME = "Observer Probe"
PROBE_PART_COUNT = 13
PROBE_SEED = "miner-rover-landed/observer-probe/v1"


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def vessel_blocks(lines: List[str]) -> List[Tuple[int, int, str]]:
    """(start, end_exclusive, name) of every depth-2 VESSEL block under FLIGHTSTATE."""
    out = []
    in_fs = False
    i = 0
    while i < len(lines):
        line = lines[i]
        if line == "\tFLIGHTSTATE":
            in_fs = True
        elif in_fs and line == "\t}":
            break
        elif in_fs and line == "\t\tVESSEL":
            assert lines[i + 1] == "\t\t{", "VESSEL not followed by an open brace"
            j = i + 2
            while lines[j] != "\t\t}":
                j += 1
            name = ""
            for k in range(i + 2, j):
                if lines[k].startswith("\t\t\tname = "):
                    name = lines[k][len("\t\t\tname = "):]
                    break
            out.append((i, j + 1, name))
            i = j
        i += 1
    return out


def rover_persistent_id() -> int:
    """The rover's vessel persistentId, read from the donor (kept verbatim)."""
    for line in _read(DONOR_VESSEL).split(LF):
        if line.startswith("\t\t\tpersistentId = "):
            return int(line[len("\t\t\tpersistentId = "):])
    raise AssertionError("donor vessel has no persistentId")


def _block_end(lines: List[str], start: int) -> int:
    """Exclusive end of the brace block whose header line is lines[start]."""
    indent = lines[start][:len(lines[start]) - len(lines[start].lstrip("\t"))]
    assert lines[start + 1] == indent + "{", "block header not followed by an open brace"
    j = start + 2
    while lines[j] != indent + "}":
        j += 1
    return j + 1


def _set_top(clone: List[str], key: str, value: str) -> None:
    prefix = "\t\t\t" + key + " = "
    hits = [i for i, l in enumerate(clone) if l.startswith(prefix)]
    assert len(hits) == 1, "donor vessel key %r found %d times" % (key, len(hits))
    clone[hits[0]] = prefix + value


def _strip_restock(clone: List[str]) -> Tuple[List[str], Dict[str, int]]:
    out = []
    by_part: Dict[str, int] = {}
    part = None
    i = 0
    while i < len(clone):
        line = clone[i]
        if line == "\t\t\tPART":
            part = clone[i + 2][len("\t\t\t\tname = "):]
        if (line == "\t\t\t\tMODULE"
                and clone[i + 2].startswith("\t\t\t\t\tname = " + RESTOCK_PREFIX)):
            by_part[part] = by_part.get(part, 0) + 1
            i = _block_end(clone, i)
            continue
        out.append(line)
        i += 1
    return out, by_part


def _empty_resources(clone: List[str]) -> Dict[str, int]:
    emptied: Dict[str, int] = {}
    for i, line in enumerate(clone):
        if line != "\t\t\t\tRESOURCE":
            continue
        end = _block_end(clone, i)
        name = clone[i + 2][len("\t\t\t\t\tname = "):]
        assert clone[i + 2].startswith("\t\t\t\t\tname = "), "RESOURCE without a leading name"
        if name not in EMPTIED_RESOURCES:
            continue
        amounts = [k for k in range(i, end) if clone[k].startswith("\t\t\t\t\tamount = ")]
        maxes = [k for k in range(i, end) if clone[k].startswith("\t\t\t\t\tmaxAmount = ")]
        assert len(amounts) == 1 and len(maxes) == 1, "RESOURCE %s amount keys" % name
        assert float(clone[maxes[0]].split(" = ", 1)[1]) > 0, "RESOURCE %s has no room" % name
        clone[amounts[0]] = "\t\t\t\t\tamount = 0"
        emptied[name] = emptied.get(name, 0) + 1
    return emptied


def build_clone() -> List[str]:
    """The donor VESSEL block, edited as the module docstring says."""
    donor = _read(DONOR_VESSEL)
    assert "\r" not in donor, "the donor vessel is committed LF"
    assert donor.endswith("\t\t}" + LF), "the donor must end with its closing brace"
    clone = donor[:-1].split(LF)
    assert clone[0] == "\t\tVESSEL" and clone[1] == "\t\t{" and clone[-1] == "\t\t}"
    assert _block_end(clone, 0) == len(clone), "the donor holds exactly one VESSEL block"
    assert "\t\t\tname = " + ROVER_NAME in clone
    assert "\t\t\tpersistentId = %d" % ROVER_PERSISTENT_ID in clone
    assert "\t\t\tpid = " + ROVER_PID in clone
    assert "\t\t\tsit = LANDED" in clone, "the donor must be LANDED"
    assert not any(l.strip().startswith("crew = ") for l in clone), "the donor must be crewless"
    assert sum(1 for l in clone if l == "\t\t\tPART") == PART_COUNT

    deployed = [l.strip() for l in clone if l.strip().startswith("isDeployed = ")]
    assert deployed == ["isDeployed = False"] * IS_DEPLOYED_COUNT, deployed
    activated = [l.strip() for l in clone if l.strip().startswith("IsActivated = ")]
    assert activated == ["IsActivated = False"] * IS_ACTIVATED_COUNT, activated

    clone, stripped = _strip_restock(clone)
    assert stripped == RESTOCK_STRIPPED_BY_PART, stripped
    assert not any("restock" in l.lower() for l in clone), "ReStock text survived the strip"

    _set_top(clone, "lct", FIXTURE_UT)
    _set_top(clone, "lastUT", FIXTURE_UT)
    _set_top(clone, "met", "0")
    eph = [i for i, l in enumerate(clone) if l.startswith("\t\t\t\tEPH = ")]
    assert len(eph) == 1, "ORBIT EPH found %d times" % len(eph)
    clone[eph[0]] = "\t\t\t\tEPH = " + FIXTURE_UT

    lut = re.compile(r"^(\t+)lastUpdateTime = .*$")
    hits = 0
    for i, line in enumerate(clone):
        m = lut.match(line)
        if m:
            clone[i] = m.group(1) + "lastUpdateTime = " + FIXTURE_UT
            hits += 1
    assert hits == LAST_UPDATE_TIME_COUNT, "lastUpdateTime found %d times" % hits

    emptied = _empty_resources(clone)
    assert emptied == EMPTIED_RESOURCES, emptied

    cooling = [i for i, l in enumerate(clone) if l.strip().startswith("IsCooling = ")]
    assert [clone[i].strip() for i in cooling] == ["IsCooling = False"] * RADIATOR_COUNT, \
        "the donor's radiators must all be off"
    for i in cooling:
        clone[i] = clone[i].replace("IsCooling = False", "IsCooling = True")
    return clone


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((PROBE_SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def _derive_guid(tag: str) -> str:
    return hashlib.sha256((PROBE_SEED + "|" + tag).encode("ascii")).hexdigest()[:32]


def _probe_donor_block() -> List[str]:
    donor = _read(PROBE_DONOR_SFS)
    assert "\r" not in donor, "eva2-lko-crewed is committed LF"
    lines = donor.split(LF)
    hits = []
    for start, end, name in vessel_blocks(lines):
        block = lines[start:end]
        if name == PROBE_DONOR_NAME and not any(l.strip().startswith("crew = ") for l in block):
            hits.append(block)
    assert len(hits) == 1, "crewless %r found %d times" % (PROBE_DONOR_NAME, len(hits))
    return list(hits[0])


def probe_persistent_id() -> int:
    """The Observer Probe's derived vessel persistentId."""
    for line in _probe_donor_block():
        if line.startswith("\t\t\tpersistentId = "):
            return _derive_uint("persistentId|" + line[len("\t\t\tpersistentId = "):])
    raise AssertionError("probe donor has no persistentId")


def build_probe(rover: List[str]) -> List[str]:
    """The `Kerbal X Probe` clone, renamed and re-identified as the docstring says."""
    clone = _probe_donor_block()
    assert "\t\t\ttype = Probe" in clone and "\t\t\tsit = ORBITING" in clone
    assert sum(1 for l in clone if l == "\t\t\tPART") == PROBE_PART_COUNT
    assert "\t\t\t\tREF = 1" in clone, "the probe must orbit Kerbin"

    pid_re = re.compile(r"^(\t+)persistentId = (\d+)$")
    rover_pids = {int(m.group(2)) for m in (pid_re.match(l) for l in rover) if m}
    rover_guids = {l.strip() for l in rover if l.startswith("\t\t\tpid = ")}
    rover_uids = {l.strip() for l in rover if l.strip().startswith("uid = ")}
    probe_uids = {l.strip() for l in clone if l.strip().startswith("uid = ")}
    assert probe_uids and not (rover_uids & probe_uids), "part uid collision with the rover"

    out = []
    seen = set()
    lut = re.compile(r"^(\t+)lastUpdateTime = .*$")
    for line in clone:
        m = pid_re.match(line)
        u = lut.match(line)
        if line.startswith("\t\t\tpid = "):
            line = "\t\t\tpid = " + _derive_guid("vessel-pid")
            assert line.strip() not in rover_guids
        elif m:
            new_pid = _derive_uint("persistentId|" + m.group(2))
            assert new_pid not in seen, "derived persistentId collision"
            assert new_pid not in rover_pids, "derived persistentId collides with the rover"
            seen.add(new_pid)
            line = "%spersistentId = %d" % (m.group(1), new_pid)
        elif u:
            line = u.group(1) + "lastUpdateTime = " + FIXTURE_UT
        out.append(line)
    assert len(seen) == PROBE_PART_COUNT + 1, "vessel + every part persistentId re-derived"
    _set_top(out, "name", PROBE_NAME)
    _set_top(out, "lct", FIXTURE_UT)
    _set_top(out, "lastUT", FIXTURE_UT)
    _set_top(out, "met", "0")
    eph = [i for i, l in enumerate(out) if l.startswith("\t\t\t\tEPH = ")]
    assert len(eph) == 1, "probe ORBIT EPH found %d times" % len(eph)
    out[eph[0]] = "\t\t\t\tEPH = " + FIXTURE_UT
    return out


def restock_blocks_stripped() -> int:
    return sum(RESTOCK_STRIPPED_BY_PART.values())


def _scenario_node(lines: List[str], name: str) -> Tuple[int, int]:
    for i, line in enumerate(lines):
        if line == "\tSCENARIO" and lines[i + 2] == "\t\tname = " + name:
            return i, _block_end(lines, i)
    return -1, -1


def build_texts() -> Tuple[str, str]:
    """Returns (persistent.sfs, persistent.loadmeta) for the fixture."""
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    scn_donor = _read(SCENARIO_DONOR_SFS)
    assert "\r" not in base, "fresh-sandbox is committed LF"
    assert "\r" not in scn_donor, "b1-pad-craft is committed LF"
    lines = base.split(LF)
    scn_lines = scn_donor.split(LF)

    assert not vessel_blocks(lines), "fresh-sandbox must be vessel-less"
    fs = lines.index("\tFLIGHTSTATE")
    fs_end = _block_end(lines, fs)
    for key, old, new in (("UT", "0", FIXTURE_UT), ("activeVessel", "-1", "0")):
        hits = [i for i in range(fs, fs_end) if lines[i].startswith("\t\t" + key + " = ")]
        assert len(hits) == 1 and lines[hits[0]] == "\t\t%s = %s" % (key, old), (key, hits)
        lines[hits[0]] = "\t\t%s = %s" % (key, new)
    assert lines[fs_end - 2] == "\t\tcommNetUIModeFlight = Path", lines[fs_end - 2]
    rover = build_clone()
    lines[fs_end - 1:fs_end - 1] = rover + build_probe(rover)

    assert _scenario_node(lines, "ParsekScenario")[0] < 0, "fresh-sandbox has no Parsek node"
    s_start, s_end = _scenario_node(scn_lines, "ParsekScenario")
    assert s_start >= 0, "b1-pad-craft has no ParsekScenario node"
    node = scn_lines[s_start:s_end]
    assert len(node) == 7 and not any(l.startswith("\t\t\t") for l in node), \
        "the copied ParsekScenario node must be the inert three-value form"
    anchor_start, anchor_end = _scenario_node(lines, "DiscoverableObjects")
    assert anchor_start >= 0 and lines[anchor_end] == "\tFLIGHTSTATE", \
        "DiscoverableObjects must be the last SCENARIO before FLIGHTSTATE"
    lines[anchor_end:anchor_end] = node

    title = lines.index("\tTitle = " + BASE_TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + BASE_DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTION
    sfs = LF.join(lines)

    meta = _read(os.path.join(BASE_DIR, "persistent.loadmeta"))
    meta, n = re.subn(r"^vesselCount = 0(\r?\n)", r"vesselCount = 2\1", meta, count=1, flags=re.M)
    assert n == 1, "loadmeta vesselCount = 0 not found"
    meta, n = re.subn(r"^UT = 0(\r?\n)", r"UT = %s\1" % FIXTURE_UT, meta, count=1, flags=re.M)
    assert n == 1, "loadmeta UT = 0 not found"
    return sfs, meta


def expected_files() -> Dict[str, bytes]:
    """{'/'-separated relative path: bytes} of every file the fixture holds."""
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = _read_bytes(path)
    sfs, meta = build_texts()
    files["persistent.sfs"] = sfs.encode("utf-8")
    files["persistent.loadmeta"] = meta.encode("utf-8")
    return files


def committed_problems() -> List[str]:
    want = expected_files()
    have = {}
    if os.path.isdir(OUT_DIR):
        for root, _dirs, names in os.walk(OUT_DIR):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, OUT_DIR).replace(os.sep, "/")] = _read_bytes(path)
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
    print("wrote " + OUT_DIR + " roverPersistentId=%d probePersistentId=%d restockStripped=%d"
          % (rover_persistent_id(), probe_persistent_id(), restock_blocks_stripped()))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
