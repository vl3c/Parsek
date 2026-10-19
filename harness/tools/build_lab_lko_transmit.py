#!/usr/bin/env python3
"""Build the committed `lab-lko-seeded-transmit` fixture BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. LAB-10 / LAB-10b (Re-Fly and Discard Re-fly with a lab transmit in
the replaced branch) and LAB-13 (process while joined, separate the tender, transmit) need
the LABX Orbital Lab IN ORBIT with the probe tender still joined (the Re-Fly separation) and
a lab that can transmit at once: storedScience at least 1. `lab-lko-science` holds an empty
lab (nothing to send), `lab-lko-seeded` holds 499 science (a 499-Mit transmission at 2 Mits
per 0.6 s packet and 12 EC per packet would need about 3,000 EC against the 1,650 EC of
batteries, so it stalls), and `lab-pad-seeded` holds 12.5 on the launch pad, where a tender
separation cannot be flown cleanly. This variant is `lab-lko-science` with the pad variant's
12.5 science in the lab, so a transmit pays the same 12.50 LAB-4 / LAB-9b measured (12 Mits,
six packets, 72 EC, about 4 s).

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/lab-lko-science`
  (SCIENCE_SANDBOX, science 100, the LABX Orbital Lab ORBITING at about 107 x 92 km, the
  probe tender joined above the pod by its two PreAttached Clamp-O-Tron Jr ports, Valentina in
  the pod, Bob in the lab, the inert ParsekScenario node). `persistent.loadmeta` and
  `AddOns/` are copied byte for byte; identities (vessel pid 2200110055, persistentIds, part
  uids) are verbatim.

  THE LAB EDIT, in the Large.Crewed.Lab part's ModuleScienceLab only: `storedScience = 0` ->
  `storedScience = 12.5` (the pad variant's value, strictly under the part cfg's scienceCap
  500). `dataStored` stays 0 and `EXPERIMENT_DATA` stays EMPTY, so every subject the craft
  can take in this orbit is still storable (LAB-13 processes the Thermometer, lab value 5).
  The converter stays `IsActivated = False`: nothing is researched until a lane starts it.
  The field names are the ones KSP itself wrote in the base (decompiled ModuleScienceLab,
  KSP 1.12.5: `[KSPField(isPersistant = true)] float storedScience`).

  EDITED IN THE SAVE: the `Title` and `Description`.

  The helpers (`vessel_blocks`, `_block_end`) are `build_lab_seeded_variants.py`'s, imported
  rather than copied; this builder never writes that module's two fixtures.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_lab_lko_transmit.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import shutil
import sys
from typing import Dict, List

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_NAME = "lab-lko-science"
BASE_DIR = os.path.join(SAVES, BASE_NAME)
NAME = "lab-lko-seeded-transmit"

CRLF = "\r\n"
LF = "\n"
EDITED = ("persistent.sfs",)

VESSEL_NAME = "LABX Orbital Lab"
BASE_TITLE = "lab-lko-science (SCIENCE_SANDBOX)"
BASE_DESCRIPTION = "Parsek autotest fixture (synthetic, file-constructed)."
TITLE = "lab-lko-seeded-transmit (SCIENCE_SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: the LABX Orbital Lab in low Kerbin "
               "orbit with its probe tender joined, its lab holding 12.5 science and no data).")

STORED_SCIENCE = "12.5"
SCIENCE_CAP = 500.0

_PART_HEADER = "\t\t\tPART"
_MODULE_HEADER = "\t\t\t\tMODULE"


def _load_seeded_builder():
    path = os.path.join(_HERE, "build_lab_seeded_variants.py")
    spec = importlib.util.spec_from_file_location("build_lab_seeded_variants", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_SEEDED = _load_seeded_builder()
vessel_blocks = _SEEDED.vessel_blocks
_block_end = _SEEDED._block_end


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def lab_module_range(vessel: List[str]):
    """(start, end) of the Large.Crewed.Lab part's ModuleScienceLab block."""
    labs = [i for i, l in enumerate(vessel)
            if l == _PART_HEADER and vessel[i + 2] == "\t\t\t\tname = Large.Crewed.Lab"]
    assert len(labs) == 1, "lab parts %d" % len(labs)
    p_end = _block_end(vessel, labs[0])
    mods = [i for i in range(labs[0], p_end)
            if vessel[i] == _MODULE_HEADER and vessel[i + 2] == "\t\t\t\t\tname = ModuleScienceLab"]
    assert len(mods) == 1, "ModuleScienceLab modules %d" % len(mods)
    return mods[0], _block_end(vessel, mods[0]), labs[0], p_end


def seed_lab(vessel: List[str]) -> List[str]:
    out = list(vessel)
    m_start, m_end, p_start, p_end = lab_module_range(out)
    hits = [i for i in range(m_start, m_end) if out[i] == "\t\t\t\t\tstoredScience = 0"]
    assert len(hits) == 1, "the base lab's storedScience must be 0"
    out[hits[0]] = "\t\t\t\t\tstoredScience = " + STORED_SCIENCE
    assert float(STORED_SCIENCE) < SCIENCE_CAP
    assert "\t\t\t\t\tdataStored = 0" in out[m_start:m_end], "the base lab's dataStored must be 0"
    exp = [i for i in range(m_start, m_end) if out[i] == "\t\t\t\t\tEXPERIMENT_DATA"]
    assert len(exp) == 1 and out[exp[0] + 1] == "\t\t\t\t\t{" \
        and out[exp[0] + 2] == "\t\t\t\t\t}", "the base lab's EXPERIMENT_DATA must be empty"
    conv = [i for i in range(p_start, p_end)
            if out[i] == _MODULE_HEADER and out[i + 2] == "\t\t\t\t\tname = ModuleScienceConverter"]
    assert len(conv) == 1
    assert "\t\t\t\t\tIsActivated = False" in out[conv[0]:_block_end(out, conv[0])], \
        "research must be off"
    return out


def build_sfs() -> str:
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    lines = base.replace(CRLF, LF).split(LF)
    blocks = vessel_blocks(lines)
    assert [b[2] for b in blocks] == [VESSEL_NAME], [b[2] for b in blocks]
    v_start, v_end, _ = blocks[0]
    vessel = lines[v_start:v_end]
    assert "\t\t\tsit = ORBITING" in vessel, "the base vessel is ORBITING"
    ports = [i for i, l in enumerate(vessel) if l == "\t\t\t\tname = dockingPort3"]
    assert len(ports) == 2, "the tender's two Jr ports must be aboard"
    lines[v_start:v_end] = seed_lab(vessel)
    assert "\t\tactiveVessel = 0" in lines, "the lab stays the active vessel"
    lines[lines.index("\tTitle = " + BASE_TITLE)] = "\tTitle = " + TITLE
    lines[lines.index("\tDescription = " + BASE_DESCRIPTION)] = "\tDescription = " + DESCRIPTION
    return CRLF.join(lines)


def expected_files() -> Dict[str, bytes]:
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = _read_bytes(path)
    files["persistent.sfs"] = build_sfs().encode("utf-8")
    return files


def out_dir() -> str:
    return os.path.join(SAVES, NAME)


def committed_problems() -> List[str]:
    want = expected_files()
    have = {}
    target = out_dir()
    if os.path.isdir(target):
        for root, _dirs, names in os.walk(target):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, target).replace(os.sep, "/")] = _read_bytes(path)
    problems = []
    for rel in sorted(set(want) | set(have)):
        if rel not in have:
            problems.append("%s: missing %s" % (NAME, rel))
        elif rel not in want:
            problems.append("%s: unexpected %s" % (NAME, rel))
        elif have[rel] != want[rel]:
            problems.append("%s: drift in %s" % (NAME, rel))
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
    target = out_dir()
    if os.path.isdir(target):
        shutil.rmtree(target)
    for rel, data in expected_files().items():
        path = os.path.join(target, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote " + target)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
