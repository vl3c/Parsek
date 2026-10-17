#!/usr/bin/env python3
"""Build the committed `miner-rover-full` and `miner-rover-starved` fixtures BY CONSTRUCTION.

WHY THESE FIXTURES EXIST. The mining program's lane MINE-11
(`docs/dev/research/mining-isru-test-program-2026-10-17.md`) needs V's real miner in two
edge states that `miner-rover-landed` (tanks empty, four RTGs, 1800 EC) cannot reach in a
short flight: tanks nearly full, so the drills reach capacity and stop producing while
still switched on, and no power at all, so every converter is switched on and makes
nothing. Each is a one-vessel edit of the base; the `Observer Probe` (index 1) and
everything else stay as the base has them.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed
  `fixtures/saves/miner-rover-landed` (the `Mining Rover RC` at index 0, active, radiators
  on, drills retracted, every converter off and every clock at UT 30; the `Observer Probe`
  at index 1; the inert ParsekScenario node). `persistent.loadmeta` and `AddOns/` are
  copied byte for byte (vesselCount 2, UT 30). Identities (vessel pid, persistentIds, part
  uids) are verbatim in both variants.

  `miner-rover-full` (MINE-11a). In the miner only, RESOURCE `amount`s:
    Ore = `FULL_ORE_PER_TANK` (74.9) in each of the two RadialOreTanks (149.8 of 150);
    LiquidFuel, Oxidizer and MonoPropellant = their own `maxAmount` (90, 110, 20 + 20).
  ElectricCharge, the RTGs and every module are the base's. With the ISRU's Lf+Ox outputs
  full, the drills' ore has nowhere to go but the ore tanks, which they top up and stop.
  Stock writes `amount` as a plain double, and a RESOURCE is matched by name on load, so
  only the values change.

  `miner-rover-starved` (MINE-11b). In the miner only:
    the four `rtg` PART blocks (part indices 12..15, leaves surface-attached to the
    FL-T200 at index 7, in one symmetry set; nothing attaches to them) are DROPPED, and
    every part-index reference of the 23 surviving parts is renumbered (`parent`, `sym`,
    `srfN`, `attN`; an index above 15 moves down by 4). The builder asserts no surviving
    part names a dropped part by index, uid or persistentId. KSP writes exactly this
    shape itself when parts leave a vessel (indices are positions in the PART list), so
    stock loads it as a 23-part vessel;
    `amount` = 0 on every ElectricCharge RESOURCE (the probe core and the two batteries;
    `maxAmount` kept).
  WHY DROP THE PARTS rather than switch the generators off (decompiled ModuleGenerator /
  PartModule / BaseFieldList, KSP 1.12.5): the RTG's `isAlwaysActive` is a cfg KSPField,
  and `OnStart` sets `generatorIsActive = moduleIsEnabled` when it is set, so a saved
  `generatorIsActive = False` is overwritten on every load. `BaseFieldList.Load` does read
  a non-persistent field out of a save node, so writing `isAlwaysActive = False` (or
  `moduleIsEnabled = False`) would hold for the first load, but `BaseFieldList.Save` writes
  only `isPersistant` fields: the first save after it (the rewind point, Parsek's end
  snapshot, any quicksave) drops the override and the RTGs come back on, so the spawned
  rover would have power. `PartModule.isEnabled = False` gates only `OnUpdate` /
  `OnFixedUpdate`, while ModuleGenerator produces from Unity's `FixedUpdate`. Removing the
  module alone does not work either: ProtoPartSnapshot matches modules by name, and a part
  missing a saved module keeps the prefab's. Dropping the parts is the one edit that
  survives every save and load.
  Starved converters stay switched on: no `IsActivated = false` path in BaseConverter or
  ModuleResourceHarvester depends on ElectricCharge (only overheat, not landed, no ground
  contact, no abundance, too fast).

  EDITED IN EACH SAVE: the `Title` and `Description`.

`--check` re-derives and compares every file of both fixtures against the committed bytes;
`harness/lib/test_miner_rover_variants.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
from typing import Dict, List

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "miner-rover-landed")

sys.path.insert(0, _HERE)
import build_miner_rover_landed as landed  # noqa: E402

LF = "\n"
EDITED = ("persistent.sfs",)
FULL = "miner-rover-full"
STARVED = "miner-rover-starved"
VARIANTS = (FULL, STARVED)

FULL_ORE_PER_TANK = "74.9"
ORE_TANKS = 2
ORE_TANK_MAX = 75.0
FILLED_TO_MAX = {"LiquidFuel": 1, "Oxidizer": 1, "MonoPropellant": 2}

RTG_PART = "rtg"
RTG_INDICES = (12, 13, 14, 15)
BASE_PART_COUNT = 27
EC_RESOURCES = 3

TITLES = {
    FULL: "miner-rover-full (SANDBOX)",
    STARVED: "miner-rover-starved (SANDBOX)",
}
DESCRIPTIONS = {
    FULL: ("Parsek autotest fixture (file-constructed: the real mining rover with its "
           "ore tanks nearly full and its fuel tanks full)."),
    STARVED: ("Parsek autotest fixture (file-constructed: the real mining rover with its "
              "RTGs removed and no ElectricCharge)."),
}

_PART_HEADER = "\t\t\tPART"
_RESOURCE_HEADER = "\t\t\t\tRESOURCE"
_INDEX_REFS = (
    re.compile(r"^(\t\t\t\t(?:parent|sym) = )(-?\d+)$"),
    re.compile(r"^(\t\t\t\t(?:srfN|attN) = [^,]*, )(-?\d+)(.*)$"),
)


def out_dir(variant: str) -> str:
    return os.path.join(SAVES, variant)


def part_blocks(vessel: List[str]) -> List[List[int]]:
    """[start, end_exclusive] of every PART block in a VESSEL block, in part order."""
    out = []
    for i, line in enumerate(vessel):
        if line == _PART_HEADER:
            out.append([i, landed._block_end(vessel, i)])
    return out


def _part_name(vessel: List[str], start: int) -> str:
    line = vessel[start + 2]
    assert line.startswith("\t\t\t\tname = "), "PART without a leading name"
    return line[len("\t\t\t\tname = "):]


def _resource_amounts(vessel: List[str]):
    """Yields (name, amount_index, maxAmount_value) for every RESOURCE node."""
    for i, line in enumerate(vessel):
        if line != _RESOURCE_HEADER:
            continue
        end = landed._block_end(vessel, i)
        assert vessel[i + 2].startswith("\t\t\t\t\tname = "), "RESOURCE without a leading name"
        name = vessel[i + 2][len("\t\t\t\t\tname = "):]
        amounts = [k for k in range(i, end) if vessel[k].startswith("\t\t\t\t\tamount = ")]
        maxes = [k for k in range(i, end) if vessel[k].startswith("\t\t\t\t\tmaxAmount = ")]
        assert len(amounts) == 1 and len(maxes) == 1, "RESOURCE %s amount keys" % name
        yield name, amounts[0], vessel[maxes[0]].split(" = ", 1)[1]


def fill_miner(miner: List[str]) -> List[str]:
    out = list(miner)
    ore = 0
    filled: Dict[str, int] = {}
    for name, k, max_text in _resource_amounts(out):
        if name == "Ore":
            assert out[k] == "\t\t\t\t\tamount = 0", "the base's ore tanks must be empty"
            assert float(max_text) == ORE_TANK_MAX, "ore tank capacity %s" % max_text
            out[k] = "\t\t\t\t\tamount = " + FULL_ORE_PER_TANK
            ore += 1
        elif name in FILLED_TO_MAX:
            assert out[k] == "\t\t\t\t\tamount = 0", "the base's %s must be empty" % name
            assert float(max_text) > 0
            out[k] = "\t\t\t\t\tamount = " + max_text
            filled[name] = filled.get(name, 0) + 1
    assert ore == ORE_TANKS, "ore tanks found %d" % ore
    assert filled == FILLED_TO_MAX, filled
    return out


def starve_miner(miner: List[str]) -> List[str]:
    blocks = part_blocks(miner)
    assert len(blocks) == BASE_PART_COUNT, "base miner has %d parts" % len(blocks)
    rtgs = [i for i, (s, _e) in enumerate(blocks) if _part_name(miner, s) == RTG_PART]
    assert tuple(rtgs) == RTG_INDICES, "rtg part indices %r" % rtgs
    dropped = set(RTG_INDICES)
    remap = {}
    new_index = 0
    for old in range(len(blocks)):
        if old not in dropped:
            remap[old] = new_index
            new_index += 1

    dropped_ids = set()
    for idx in RTG_INDICES:
        s, e = blocks[idx]
        for line in miner[s:e]:
            key = line.strip().split(" = ", 1)
            if line.startswith("\t\t\t\t") and not line.startswith("\t\t\t\t\t") \
                    and key[0] in ("uid", "persistentId"):
                dropped_ids.add(key[1])

    head = miner[:blocks[0][0]]
    tail = miner[blocks[-1][1]:]
    body: List[str] = []
    for old, (s, e) in enumerate(blocks):
        if old in dropped:
            continue
        for line in miner[s:e]:
            for rx in _INDEX_REFS:
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
    assert len(part_blocks(out)) == BASE_PART_COUNT - len(RTG_INDICES)
    assert not any(l == "\t\t\t\tname = " + RTG_PART for l in out), "an rtg survived"

    ec = 0
    for name, k, _max in _resource_amounts(out):
        if name == "ElectricCharge":
            out[k] = "\t\t\t\t\tamount = 0"
            ec += 1
    assert ec == EC_RESOURCES, "ElectricCharge resources found %d" % ec
    assert not any(l.strip() == "name = ModuleGenerator" for l in out), "a generator survived"
    return out


def build_sfs(variant: str) -> str:
    base = landed._read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "miner-rover-landed is committed LF"
    lines = base.split(LF)
    blocks = landed.vessel_blocks(lines)
    assert [b[2] for b in blocks] == [landed.ROVER_NAME, landed.PROBE_NAME], \
        [b[2] for b in blocks]
    m_start, m_end, _ = blocks[0]
    miner = lines[m_start:m_end]
    edited = fill_miner(miner) if variant == FULL else starve_miner(miner)
    lines[m_start:m_end] = edited
    assert "\t\tactiveVessel = 0" in lines, "the miner stays the active vessel"
    title = lines.index("\tTitle = " + landed.TITLE)
    lines[title] = "\tTitle = " + TITLES[variant]
    desc = lines.index("\tDescription = " + landed.DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTIONS[variant]
    return LF.join(lines)


def expected_files(variant: str) -> Dict[str, bytes]:
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = landed._read_bytes(path)
    files["persistent.sfs"] = build_sfs(variant).encode("utf-8")
    return files


def committed_problems(variant: str) -> List[str]:
    want = expected_files(variant)
    have = {}
    target = out_dir(variant)
    if os.path.isdir(target):
        for root, _dirs, names in os.walk(target):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, target).replace(os.sep, "/")] = \
                    landed._read_bytes(path)
    problems = []
    for rel in sorted(set(want) | set(have)):
        if rel not in have:
            problems.append("%s: missing %s" % (variant, rel))
        elif rel not in want:
            problems.append("%s: unexpected %s" % (variant, rel))
        elif have[rel] != want[rel]:
            problems.append("%s: drift in %s" % (variant, rel))
    return problems


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true",
                    help="re-derive and compare against the committed fixtures")
    args = ap.parse_args(argv)
    if args.check:
        problems = [p for v in VARIANTS for p in committed_problems(v)]
        for p in problems:
            print("DRIFT: " + p)
        print("OK" if not problems else "FAIL")
        return 0 if not problems else 1
    for variant in VARIANTS:
        target = out_dir(variant)
        if os.path.isdir(target):
            shutil.rmtree(target)
        for rel, data in expected_files(variant).items():
            path = os.path.join(target, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as fh:
                fh.write(data)
        print("wrote " + target)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
