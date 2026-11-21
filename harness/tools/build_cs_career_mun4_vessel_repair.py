#!/usr/bin/env python3
"""Derive the committed `cs-career-mun4-vessel-repair` fixture from `cs-career-mun4` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. Todo CONTRACT-REBUILD-SKIPS-ACCEPT-WORLD-EFFECTS item 4: after a rewind
to before the accept of a construction / repair contract, the ledger rebuild re-Activates the
contract through `Contract.Load`, which never runs `OnAccepted`, so the craft the first
timeline's accept spawned (`ContractSystem.SpawnPreBuiltShip`) is missing from the rewind world.
The fix re-runs the contract's own `OnAccepted` at the Space Center
(`KspStatePatcher.SpawnRebuiltPrebuiltShip`). No committed fixture held an Offered construction
or repair contract, so that reflection shell had no live proof. This fixture is the host of lane
`CT-3-vessel-repair-rebuilt-at-space-center`: RSQ-1's proven career (`cs-career-mun4`, the same
34 recordings and ledger) plus one Offered `VesselRepairContract`.

THE DERIVATION. `cs-career-mun4`'s `persistent.sfs` byte for byte, plus two edits:

  1. ONE CONTRACT node inserted as the LAST `CONTRACT` of ContractSystem's `CONTRACTS` block
     (right before its first `CONTRACT_FINISHED`, so a surplus withdrawal, which walks the list
     from its start, reaches every other Offered Trivial contract first). The node is a stock
     saved Offered `VesselRepairContract` copied from the dev career save `c1` (KSP 1.12.5) with
     three values changed:
       - `guid` re-keyed (REPAIR_GUID, a fixed uuid5; no other contract carries it);
       - `values[8]` (dateExpire) set to the source clock rounded plus the contract's own
         TimeExpiry (43200 s), so the offer outlives the lane;
       - `roverCraftDef` written install-relative (`GameData/...`). Stock writes the running
         install's absolute path, but `PreBuiltCraft.CraftByURL` compares only the substring from
         the LAST `GameData`, so the relative form resolves on any install and the fixture holds
         no machine path. Stock re-saves the absolute form on the first save.
     Its `seed` (296452091) is kept: `OnAccepted` regenerates the satellite's Kerbin orbit from the
     mission seed, and the stock vessel name `Damaged Kerbin AS-P Satellite NV5SQ` is the seed's
     designation. The craft is the stock `Repair Contract Sat 9` (orbit-only, carries the
     `largeSolarPanel` the contract breaks), which every stock install ships.
  2. An `ExpParts { evaRepairKit = 1 }` node appended to the ResearchAndDevelopment scenario.
     `VesselRepairContract.MeetRequirements` is `ResearchAndDevelopment.PartModelPurchased(
     evaRepairKit)`, and stock moves an Offered (or Active) contract that fails it to
     OfferExpired on its next update. The career has not researched spaceExploration (the kit's
     node); with that node absent from R&D, `partTechAvailable` and `partModelPurchased` both fall
     through to the experimental count, so one count satisfies the requirement without touching
     the tech tree. Parsek's ledger never patches experimental counts (the tech-tree and
     purchased-part patches would take back a hand-added node or part, which is why the count is
     used instead). It is the state a player has with an Active part test of the kit.

`persistent.loadmeta` and every file under `Parsek/` are copied byte for byte. `Ships/` is not
copied: the lane launches the shared `Jumping Flea` (`fixtures/shared-ships.toml`).

Run `python harness/tools/build_cs_career_mun4_vessel_repair.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_cs_career_mun4_vessel_repair.py` runs the check in-process.

Stdlib only; ASCII only; no em dashes.
"""
from __future__ import annotations

import argparse
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun4")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun4-vessel-repair")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")
COPIED_TREE = "Parsek"

REPAIR_GUID = "df927e5a-b1e6-5ae7-ad39-c1d1d7fb82fe"
REPAIR_TYPE = "VesselRepairContract"
VESSEL_NAME = "Damaged Kerbin AS-P Satellite NV5SQ"
CRAFT_URL = "GameData/Squad/Contracts/PreBuiltCraft/RepairContract/Repair Contract Sat 9.craft"
REPAIR_KIT = "evaRepairKit"
TIME_EXPIRY = 43200

CONTRACT_HEADER = "\t\t\tCONTRACT"
CONTRACT_FINISHED_HEADER = "\t\t\tCONTRACT_FINISHED"
CONTRACT_CLOSE = "\t\t\t}"
SCENARIO_CLOSE = "\t}"

# The stock node (dev save c1, Offered, KSP 1.12.5) with the three derived values as holes.
_P4 = "\t\t\t\t"
_P5 = "\t\t\t\t\t"
_CONTRACT_TEMPLATE = (
    CONTRACT_HEADER,
    "\t\t\t{",
    _P4 + "guid = {guid}",
    _P4 + "type = " + REPAIR_TYPE,
    _P4 + "prestige = 0",
    _P4 + "seed = 296452091",
    _P4 + "state = Offered",
    _P4 + "viewed = Seen",
    _P4 + "agent = O.M.B. Demolition Enterprises",
    _P4 + "agentName = O.M.B. Demolition Enterprises",
    _P4 + "deadlineType = Floating",
    _P4 + "expiryType = Floating",
    _P4 + "values = 43200,18403200,22000,41999.9980926514,31499.9985694885,0,1.904762,1,{date_expire},0,0,0",
    _P4 + "bodyName = Kerbin",
    _P4 + "roverCraftDef = {craft}",
    _P4 + "vesselName = " + VESSEL_NAME,
    _P4 + "craftStartLat = 0",
    _P4 + "craftStartLon = 0",
    _P4 + "orbitEccentricity = 0.05000000074505806",
    _P4 + "orbitAltitudeFactor = 0.33000001311302185",
    _P4 + "orbitInclinationFactor = 0.25",
    _P4 + "repairPartName = largeSolarPanel",
    _P4 + "PARAM",
    _P4 + "{",
    _P5 + "name = CrewTraitParameter",
    _P5 + "state = Incomplete",
    _P5 + "values = 0,0,0,0,0",
    _P5 + "targetTrait = Engineer",
    _P5 + "targetCount = 1",
    _P5 + "vesselDescription = Active Vessel",
    _P4 + "}",
    _P4 + "PARAM",
    _P4 + "{",
    _P5 + "name = RepairPartParameter",
    _P5 + "state = Incomplete",
    _P5 + "values = 0,0,0,0,0",
    _P5 + "bodyName = Kerbin",
    _P5 + "partName = largeSolarPanel",
    _P5 + "partPersistentId = 0",
    _P5 + "vesselPersistentId = 0",
    _P5 + "vesselName = " + VESSEL_NAME,
    _P4 + "}",
    CONTRACT_CLOSE,
)

_EXP_PARTS = ("\t\tExpParts", "\t\t{", "\t\t\t" + REPAIR_KIT + " = 1", "\t\t}")

_UT_RE = re.compile(r"^\t\tUT = ([0-9.]+)$", re.M)


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def source_ut(sfs_text: str) -> float:
    """The source save's FLIGHTSTATE clock (`\\t\\tUT = ...`)."""
    m = _UT_RE.search(sfs_text)
    if m is None:
        raise SystemExit("no FLIGHTSTATE UT in %s" % SOURCE_DIR)
    return float(m.group(1))


def date_expire(sfs_text: str) -> int:
    """The offer's expiry: the source clock rounded to the second plus TimeExpiry. An integer,
    so the derived bytes do not depend on float formatting."""
    return int(round(source_ut(sfs_text))) + TIME_EXPIRY


def contract_lines(expire: int) -> list:
    out = []
    for line in _CONTRACT_TEMPLATE:
        out.append(line.replace("{guid}", REPAIR_GUID)
                       .replace("{date_expire}", str(expire))
                       .replace("{craft}", CRAFT_URL))
    return out


def scenario_bounds(lines: list, name: str) -> tuple:
    """(name line index, closing `\\t}` index) of the SCENARIO named `name`."""
    name_line = "\t\tname = " + name
    for i, line in enumerate(lines):
        if line == name_line:
            end = i
            while end < len(lines) and lines[end] != SCENARIO_CLOSE:
                end += 1
            if end >= len(lines):
                break
            return i, end
    raise SystemExit("SCENARIO %s not found" % name)


def contract_insert_index(lines: list) -> int:
    """Where the repair node goes: the first CONTRACT_FINISHED inside ContractSystem."""
    start, end = scenario_bounds(lines, "ContractSystem")
    for i in range(start, end):
        if lines[i] == CONTRACT_FINISHED_HEADER:
            if lines[i - 1] != CONTRACT_CLOSE:
                raise SystemExit("line before the first CONTRACT_FINISHED is not a CONTRACT close")
            return i
    raise SystemExit("ContractSystem holds no CONTRACT_FINISHED node")


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    if b"\r\n" in source_bytes:
        raise SystemExit("%s/persistent.sfs is expected to use LF line ends" % SOURCE_DIR)
    text = source_bytes.decode("utf-8")
    if REPAIR_GUID in text or VESSEL_NAME in text or "ExpParts" in text:
        raise SystemExit("the source already carries the repair guid, its vessel or an ExpParts node")
    lines = text.split("\n")
    expire = date_expire(text)
    at = contract_insert_index(lines)
    lines = lines[:at] + contract_lines(expire) + lines[at:]
    _, rnd_end = scenario_bounds(lines, "ResearchAndDevelopment")
    lines = lines[:rnd_end] + list(_EXP_PARTS) + lines[rnd_end:]
    return "\n".join(lines).encode("utf-8")


def derive_all() -> dict:
    """{relative path: bytes} of every file the fixture must contain."""
    files = {}
    for name in COPIED_FILES:
        src = _read(os.path.join(SOURCE_DIR, name))
        files[name] = derive_persistent_sfs(src) if name == "persistent.sfs" else src
    tree = os.path.join(SOURCE_DIR, COPIED_TREE)
    for dirpath, dirnames, filenames in os.walk(tree):
        dirnames.sort()
        for fn in sorted(filenames):
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, SOURCE_DIR).replace(os.sep, "/")
            files[rel] = _read(full)
    return files


def verify(sfs_text: str) -> list:
    """Post-conditions the lane depends on, stated on the derived text."""
    problems = []
    lines = sfs_text.split("\n")
    if "\tTitle = cs-career-mun4 (CAREER)" not in lines:
        problems.append("not the cs-career-mun4 career")
    if sfs_text.count("guid = " + REPAIR_GUID) != 1:
        problems.append("the repair guid is not carried exactly once")
    try:
        start = lines.index(_P4 + "guid = " + REPAIR_GUID) - 2
    except ValueError:
        return problems + ["no repair contract node"]
    end = start
    while end < len(lines) and lines[end] != CONTRACT_CLOSE:
        end += 1
    block = [l.strip() for l in lines[start:end + 1]]
    for want in ("type = " + REPAIR_TYPE, "state = Offered", "bodyName = Kerbin",
                 "roverCraftDef = " + CRAFT_URL, "vesselName = " + VESSEL_NAME):
        if want not in block:
            problems.append("repair contract lacks '%s'" % want)
    if any(l.startswith("repairVslId") for l in block):
        problems.append("an Offered repair contract must name no vessel id")
    values = next((l for l in block if l.startswith("values = ")), "")
    fields = values[len("values = "):].split(",")
    if len(fields) != 12:
        problems.append("values does not hold 12 fields")
    else:
        ut = source_ut(sfs_text)
        if float(fields[8]) < ut + 3600.0:
            problems.append("the offer expires within an hour of the save clock")
    c_start, c_end = scenario_bounds(lines, "ContractSystem")
    headers = [i for i in range(c_start, c_end) if lines[i] == CONTRACT_HEADER]
    if not headers or headers[-1] != start:
        problems.append("the repair contract is not the last CONTRACT of ContractSystem")
    if sfs_text.count(VESSEL_NAME) != 2:
        problems.append("the satellite name must appear only in the contract and its parameter")
    r_start, r_end = scenario_bounds(lines, "ResearchAndDevelopment")
    rnd = lines[r_start:r_end]
    if "\t\t\t" + REPAIR_KIT + " = 1" not in rnd or rnd.count("\t\tExpParts") != 1:
        problems.append("R&D does not hold one ExpParts count of %s" % REPAIR_KIT)
    if any(l == "\t\t\tid = spaceExploration" for l in rnd):
        problems.append("spaceExploration is researched; the experimental count would not be read")
    return problems


def check() -> list:
    """Differences between a fresh derivation and the committed bytes (empty = OK)."""
    problems = []
    expected = derive_all()
    for rel, data in expected.items():
        path = os.path.join(TARGET_DIR, rel)
        if not os.path.isfile(path):
            problems.append("missing %s" % rel)
        elif _read(path) != data:
            problems.append("%s differs from a fresh derivation" % rel)
    if os.path.isdir(TARGET_DIR):
        for dirpath, _, filenames in os.walk(TARGET_DIR):
            for fn in filenames:
                rel = os.path.relpath(os.path.join(dirpath, fn), TARGET_DIR).replace(os.sep, "/")
                if rel not in expected:
                    problems.append("unexpected file in fixture: %s" % rel)
    return problems


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixture; exit 1 on drift")
    args = parser.parse_args(argv)
    files = derive_all()
    shape = verify(files["persistent.sfs"].decode("utf-8"))
    for p in shape:
        print("FAIL: " + p)
    if shape:
        return 1
    if args.check:
        problems = check()
        for p in problems:
            print("DRIFT: " + p)
        print("cs-career-mun4-vessel-repair: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    for rel, data in files.items():
        path = os.path.join(TARGET_DIR, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote %s (%d files)" % (TARGET_DIR, len(files)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
