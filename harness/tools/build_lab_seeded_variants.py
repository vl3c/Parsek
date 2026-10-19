#!/usr/bin/env python3
"""Build the committed `lab-pad-seeded`, `lab-lko-seeded` and `lab-lko-relay` fixtures BY CONSTRUCTION.

WHY THESE FIXTURES EXIST. The science-lab lanes need the LABX Orbital Lab's Mobile
Processing Lab already holding data and science, in two places a short flight cannot
reach: on the launch pad with a modest load (LAB-9b / LAB-14 revert and recover the
vessel and read what happens to the lab's contents), and in low Kerbin orbit with both
the data store and the science store just under their caps (the full-lab refusals and
the research cap). `lab-lko-science` holds the same vessel with an EMPTY lab. Each
variant is a one-vessel edit of it; the owner approved seeding about 200 data (Q2).

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/lab-lko-science`
  (SCIENCE_SANDBOX, the craft's 29 tech nodes, science 100, the LABX Orbital Lab ORBITING
  as LABX-1 run `2026-10-17_1957` left it: 26 parts, Valentina in the pod, Bob in the lab,
  the inert ParsekScenario node). `persistent.loadmeta` and `AddOns/` are copied byte for
  byte; identities (vessel pid, persistentIds, part uids) are verbatim in both variants.
  Rebuild `lab-lko-science` first when the base changes, then re-run this.

  THE LAB EDIT (both variants), in the Large.Crewed.Lab part's ModuleScienceLab only.
  Field names are the ones KSP itself wrote in the base (`dataStored`, `storedScience`,
  `EXPERIMENT_DATA`), which decompiled ModuleScienceLab (KSP 1.12.5) declares as
  `[KSPField(isPersistant = true)] float dataStored`, `float storedScience`, and saves
  `ExperimentData` as `EXPERIMENT_DATA { experiment = <subjectID> ... }` (OnSave
  AddValue("experiment", id); OnLoad reads every value of the node in order). The caps
  live in the part cfg, not in the save (ConfigCache Large_Crewed_Lab:
  ModuleScienceLab dataStorage = 750, ModuleScienceConverter scienceCap = 500), and
  ModuleScienceConverter clamps `dataStored > dataStorage` to dataStorage and
  `storedScience > scienceCap` to scienceCap when it processes, so every seeded value is
  STRICTLY below its cap. The converter stays `IsActivated = False` (the base's), so a
  load does not process anything until a lane starts research.

  WHY THE ENTRY LIST AND dataStored ARE NOT ONE NUMBER. The lab stores only subject ids,
  never a per-entry value: dataStored is the running sum of each item's labValue at the
  moment it was stored, and nothing in stock recomputes it from the list. The list
  matters for one rule only: ModuleScienceLab.IsStorable refuses a subject already in
  ExperimentData. The lists below use the four experiments the craft carries whose
  biomeMask leaves the situation biome-free (ConfigCache EXPERIMENT_DEFINITION:
  temperatureScan 7, barometerScan 3, mysteryGoo 3, mobileMaterialsLab 3; data 4, 6, 12,
  18). Their stock labValue off the surface is round(data * situationValue * 1.25)
  (ExperimentResultDialogPage.UpdatePageLabValue: ContextBonus 0.25 for a Kerbin subject,
  no homeworld or surface factor when not landed; ResearchAndDevelopment
  GetReferenceDataValue = data / dataScale * subjectValue; Kerbin situation values
  FlyingLow 0.7, FlyingHigh 0.9, InSpaceLow 1.0, InSpaceHigh 1.5; ScienceGainMultiplier 1
  in the base), every step in single precision as stock computes it (the FlyingHigh
  Goo's 13.4999990 rounds to 13, not 14). Per situation that is 35 / 44 / 50 / 75.

  `lab-pad-seeded` (LAB-9b, LAB-14). The lab: all SIXTEEN subjects (the four experiments
  in all four situations), dataStored = 204, their exact stock sum, and storedScience =
  12.5. THE VESSEL MOVES TO THE PAD, as stock writes a vessel there: every pad-dependent
  VESSEL key is taken from `gs1-two-stage-pad`'s KSP-written GS1 Auto-Chute Booster
  (sit = PRELAUNCH, landed = True, landedAt = LaunchPad, displaylandedAt, lat, lon, nrm,
  rot, cPch, cHdg) and its ORBIT block; met = 0, distanceTraveled = 0, lct = UT - 0.02 and
  lastUT = UT as KSP wrote them there. The ORBIT block is that template's with LAN advanced
  by Kerbin's rotation between its EPH and this save's UT and EPH set to UT; a landed
  vessel's orbit is cosmetic anyway (ProtoVessel.Load gives a LandedOrSplashed vessel
  OrbitDriver update mode UPDATE, re-derived from its position, and places it at
  GetWorldSurfacePosition(lat, lon, alt)). alt / hgt: the pad's surface height from the
  template (alt - hgt = 72.4236 m) plus this vessel's root-to-lowest-stack-node distance
  (the Poodle's bottom node: 7.526 + 1.5 = 9.026 m). That is approximate by construction,
  and stock corrects it: with skipGroundPositioning = False and vesselSpawning = False,
  Vessel.GoOffRails runs CheckGroundCollision, which raycasts the ground under the vessel
  and moves it so its mesh-lowest point (getLowestPoint) rests on the hit.
  WHY PRELAUNCH AND NOT LANDED. Revert to Launch is offered only when the focused vessel
  loaded PRELAUNCH: FlightDriver (decompiled) sets
  CanRevertToPostInit = vessel.situation == PRELAUNCH after a load and then captures
  PostInitState from the loaded game, which is the state a revert reloads. A sit = LANDED
  pad vessel loads with CanRevertToPostInit false, and the M-A2 `RevertToLaunch` verb
  refuses `reverttolaunch-unavailable`. VN-5 live-proved this route on gs1-two-stage-pad
  (`2026-10-12_1420`). PRELAUNCH is a landed state (landed = True), so Recover is offered
  as for any vessel on the pad. What a fixture CANNOT carry: Revert to VAB
  (CanRevertToPrelaunch needs ShipConstruction.ShipConfig and a PreLaunchState, which only
  a launch from the editor in the same session creates), and a revert target other than
  the loaded state itself.
  The vessel is the upper stage AS FLOWN: the booster gone, the Poodle staged and
  ignited at zero throttle (mainThrottle 0), the X200-16 part-used. Raising the throttle
  on the pad would fire it.

  `lab-lko-seeded` (the cap lanes). The vessel stays ORBITING. The lab: dataStored = 740
  (of 750) and storedScience = 499 (of 500), and the TWELVE FlyingLow / FlyingHigh /
  InSpaceHigh subjects (154 of the 740 by stock arithmetic; the rest is the seed). The
  four InSpaceLow subjects are left OUT so a lane in this orbit can still offer fresh
  data: temperatureScan (labValue 5) and barometerScan (8) fit under the 10 left, the Goo
  (15) and the Materials Bay (22) are refused for capacity. Starting research hits the
  500 cap almost at once.

  `lab-lko-relay` (LAB-11, a transmit whose only link home is a ghost relay). The vessel
  stays ORBITING, the lab carries `lab-pad-seeded`'s seed (all sixteen subjects,
  dataStored = 204, storedScience = 12.5, research off), and the save gets one CommNet
  parameter edit: `CommNetParams.enableGroundStations = False`. Decompiled
  CommNetHome.CreateNode (KSP 1.12.5): with that parameter off, every home that is not
  `isKSC` is removed (a non-permanent ground station always, a permanent launch-site home
  whenever `AllowOtherLaunchSites` is False, which the base's DIFFICULTY block says), so
  the KSC is the only home. In this ~100 km orbit the KSC sees the lab only within about 31
  degrees of longitude (occlusionMultiplierAtm = 1 in the base: the occluder is Kerbin's
  full radius), so for most of each orbit the lab has no path home at all, and the LAB-11
  injection preset (`ghost-commnet-lab`) adds the one ghost relay that bridges it
  (SyntheticRecordingTests.GhostCommNetLabPreset, pinned by
  GhostCommNetLabLaneGeometryTests).

  EDITED IN EACH SAVE: the `Title` and `Description`.

`--check` re-derives and compares every file of both fixtures against the committed bytes;
`harness/lib/test_lab_seeded_variants.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import struct
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_NAME = "lab-lko-science"
TEMPLATE_NAME = "gs1-two-stage-pad"
BASE_DIR = os.path.join(SAVES, BASE_NAME)

CRLF = "\r\n"
LF = "\n"
EDITED = ("persistent.sfs",)
PAD = "lab-pad-seeded"
LKO = "lab-lko-seeded"
RELAY = "lab-lko-relay"
VARIANTS = (PAD, LKO, RELAY)

VESSEL_NAME = "LABX Orbital Lab"
BASE_TITLE = "lab-lko-science (SCIENCE_SANDBOX)"
BASE_DESCRIPTION = "Parsek autotest fixture (synthetic, file-constructed)."
TITLES = {PAD: "lab-pad-seeded (SCIENCE_SANDBOX)", LKO: "lab-lko-seeded (SCIENCE_SANDBOX)",
          RELAY: "lab-lko-relay (SCIENCE_SANDBOX)"}
DESCRIPTIONS = {
    PAD: ("Parsek autotest fixture (file-constructed: the LABX Orbital Lab upper stage "
          "PRELAUNCH on the launch pad, its lab seeded with data and science)."),
    LKO: ("Parsek autotest fixture (file-constructed: the LABX Orbital Lab in low Kerbin "
          "orbit, its lab's data and science just under their caps)."),
    RELAY: ("Parsek autotest fixture (file-constructed: the LABX Orbital Lab in low Kerbin "
            "orbit, its lab holding 12.5 science, CommNet ground stations off so the KSC "
            "is the only home)."),
}

# Stock caps (ConfigCache Large_Crewed_Lab) every seeded value stays strictly below.
DATA_STORAGE = 750.0
SCIENCE_CAP = 500.0

# The four biome-free experiments aboard: (experiment id, data amount).
EXPERIMENTS = (("temperatureScan", 4.0), ("barometerScan", 6.0),
               ("mysteryGoo", 12.0), ("mobileMaterialsLab", 18.0))
# Kerbin situation values, in situation order.
SITUATIONS = (("FlyingLow", 0.7), ("FlyingHigh", 0.9), ("InSpaceLow", 1.0),
              ("InSpaceHigh", 1.5))
CONTEXT_BONUS = 0.25


def _f32(x: float) -> float:
    """Round to IEEE single precision: stock does this arithmetic in float."""
    return struct.unpack("f", struct.pack("f", x))[0]


def lab_value(data: float, situation_value: float) -> int:
    """UpdatePageLabValue off the surface: refValue = data / dataScale * subjectValue,
    times (1 + ContextBonus), each step a float, then Math.Round (banker's, like
    Python's round)."""
    ref = _f32(_f32(data) * _f32(situation_value))
    return int(round(_f32(ref * _f32(1.0 + CONTEXT_BONUS))))


def subjects(situations) -> List[Tuple[str, int]]:
    out = []
    for sit, value in SITUATIONS:
        if sit not in situations:
            continue
        for exp, data in EXPERIMENTS:
            out.append(("%s@Kerbin%s" % (exp, sit), lab_value(data, value)))
    return out


PAD_SUBJECTS = subjects(("FlyingLow", "FlyingHigh", "InSpaceLow", "InSpaceHigh"))
LKO_SUBJECTS = subjects(("FlyingLow", "FlyingHigh", "InSpaceHigh"))
PAD_STOCK_SUM = 204    # 35 + 44 + 50 + 75, asserted against lab_value below
LKO_STOCK_SUM = 154    # 35 + 44 + 75
assert sum(v for _s, v in PAD_SUBJECTS) == PAD_STOCK_SUM
assert sum(v for _s, v in LKO_SUBJECTS) == LKO_STOCK_SUM
SEED = {
    PAD: {"dataStored": "204", "storedScience": "12.5", "subjects": PAD_SUBJECTS},
    LKO: {"dataStored": "740", "storedScience": "499", "subjects": LKO_SUBJECTS},
    RELAY: {"dataStored": "204", "storedScience": "12.5", "subjects": PAD_SUBJECTS},
}

# The one save-level CommNet edit of `lab-lko-relay` (inside CommNetParams).
GROUND_STATIONS_ON = "\t\t\tenableGroundStations = True"
GROUND_STATIONS_OFF = "\t\t\tenableGroundStations = False"

# Pad placement: VESSEL keys copied verbatim from the template's PRELAUNCH vessel.
PAD_COPIED_KEYS = ("sit", "landed", "landedAt", "displaylandedAt", "lat", "lon", "nrm",
                   "rot", "cPch", "cHdg")
TEMPLATE_VESSEL = "GS1 Auto-Chute Booster"
KERBIN_ROTATION_PERIOD = 21549.425
ROOT_TO_LOWEST_NODE = 7.5260372161865234 + 1.5   # Poodle position.y + bottom node
PRELAUNCH_LCT_LEAD = 0.02

_V = "\t\t\t"          # VESSEL key indent
_PART_HEADER = "\t\t\tPART"
_MODULE_HEADER = "\t\t\t\tMODULE"


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _block_end(lines: List[str], header: int) -> int:
    """Index one past the close brace of the node whose header is at ``header``."""
    assert lines[header + 1].strip() == "{", "no brace after %r" % lines[header]
    depth = 0
    for i in range(header + 1, len(lines)):
        s = lines[i].strip()
        if s == "{":
            depth += 1
        elif s == "}":
            depth -= 1
            if depth == 0:
                return i + 1
    raise AssertionError("unclosed node at %d" % header)


def vessel_blocks(lines: List[str]) -> List[Tuple[int, int, str]]:
    out = []
    for i, line in enumerate(lines):
        if line == "\t\tVESSEL":
            end = _block_end(lines, i)
            name = next(l for l in lines[i:end] if l.startswith(_V + "name = "))
            out.append((i, end, name[len(_V + "name = "):]))
    return out


def _key_index(block: List[str], key: str) -> int:
    hits = [i for i, l in enumerate(block) if l.startswith(_V + key + " = ")]
    assert len(hits) == 1, "VESSEL key %s found %d times" % (key, len(hits))
    return hits[0]


def _value(block: List[str], key: str) -> str:
    return block[_key_index(block, key)].split(" = ", 1)[1]


def _set(block: List[str], key: str, value: str) -> None:
    block[_key_index(block, key)] = _V + key + " = " + value


def _orbit(block: List[str]) -> Tuple[int, int]:
    hits = [i for i, l in enumerate(block) if l == _V + "ORBIT"]
    assert len(hits) == 1, "ORBIT blocks %d" % len(hits)
    return hits[0], _block_end(block, hits[0])


def _fmt(x: float) -> str:
    return repr(float(x))


def seed_lab(vessel: List[str], variant: str) -> List[str]:
    out = list(vessel)
    labs = [i for i, l in enumerate(out)
            if l == _PART_HEADER and out[i + 2] == "\t\t\t\tname = Large.Crewed.Lab"]
    assert len(labs) == 1, "lab parts %d" % len(labs)
    p_start = labs[0]
    p_end = _block_end(out, p_start)
    mods = [i for i in range(p_start, p_end)
            if out[i] == _MODULE_HEADER and out[i + 2] == "\t\t\t\t\tname = ModuleScienceLab"]
    assert len(mods) == 1, "ModuleScienceLab modules %d" % len(mods)
    m_start = mods[0]
    m_end = _block_end(out, m_start)
    seed = SEED[variant]
    for key in ("dataStored", "storedScience"):
        hits = [i for i in range(m_start, m_end) if out[i] == "\t\t\t\t\t%s = 0" % key]
        assert len(hits) == 1, "the base lab's %s must be 0" % key
        out[hits[0]] = "\t\t\t\t\t%s = %s" % (key, seed[key])
    exp = [i for i in range(m_start, m_end) if out[i] == "\t\t\t\t\tEXPERIMENT_DATA"]
    assert len(exp) == 1 and out[exp[0] + 1] == "\t\t\t\t\t{" \
        and out[exp[0] + 2] == "\t\t\t\t\t}", "the base lab's EXPERIMENT_DATA must be empty"
    entries = ["\t\t\t\t\t\texperiment = %s" % sid for sid, _v in seed["subjects"]]
    out[exp[0] + 2:exp[0] + 2] = entries
    conv = [i for i in range(p_start, p_end + len(entries))
            if out[i] == _MODULE_HEADER and out[i + 2] == "\t\t\t\t\tname = ModuleScienceConverter"]
    assert len(conv) == 1
    c_end = _block_end(out, conv[0])
    assert "\t\t\t\t\tIsActivated = False" in out[conv[0]:c_end], "research must be off"
    return out


def place_on_pad(vessel: List[str], template: List[str], ut: float) -> List[str]:
    out = list(vessel)
    assert _value(out, "sit") == "ORBITING", "the base vessel is ORBITING"
    assert _value(template, "sit") == "PRELAUNCH"
    for key in PAD_COPIED_KEYS:
        _set(out, key, _value(template, key))
    pad_surface = float(_value(template, "alt")) - float(_value(template, "hgt"))
    _set(out, "alt", _fmt(pad_surface + ROOT_TO_LOWEST_NODE))
    _set(out, "hgt", _fmt(ROOT_TO_LOWEST_NODE))
    _set(out, "met", "0")
    _set(out, "distanceTraveled", "0")
    _set(out, "lct", _fmt(ut - PRELAUNCH_LCT_LEAD))
    _set(out, "lastUT", _fmt(ut))
    assert _value(out, "launchedFrom") == "LaunchPad"
    assert _value(out, "skipGroundPositioning") == "False"
    assert _value(out, "vesselSpawning") == "False"

    t_start, t_end = _orbit(template)
    orbit = list(template[t_start:t_end])
    eph_t = float(next(l for l in orbit if l.startswith("\t\t\t\tEPH = ")).split(" = ")[1])
    for i, line in enumerate(orbit):
        if line.startswith("\t\t\t\tLAN = "):
            lan = float(line.split(" = ")[1])
            lan = (lan + 360.0 * (ut - eph_t) / KERBIN_ROTATION_PERIOD) % 360.0
            orbit[i] = "\t\t\t\tLAN = " + _fmt(lan)
        elif line.startswith("\t\t\t\tEPH = "):
            orbit[i] = "\t\t\t\tEPH = " + _fmt(ut)
    o_start, o_end = _orbit(out)
    out[o_start:o_end] = orbit
    return out


def build_sfs(variant: str) -> str:
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    lines = base.replace(CRLF, LF).split(LF)
    blocks = vessel_blocks(lines)
    assert [b[2] for b in blocks] == [VESSEL_NAME], [b[2] for b in blocks]
    v_start, v_end, _ = blocks[0]
    vessel = seed_lab(lines[v_start:v_end], variant)
    if variant == PAD:
        tmpl = _read(os.path.join(SAVES, TEMPLATE_NAME, "persistent.sfs"))
        tlines = tmpl.replace(CRLF, LF).split(LF)
        tblocks = [b for b in vessel_blocks(tlines) if b[2] == TEMPLATE_VESSEL]
        assert len(tblocks) == 1
        ut_line = [l for l in lines if l.startswith("\t\tUT = ")]
        assert len(ut_line) == 1
        ut = float(ut_line[0].split(" = ")[1])
        vessel = place_on_pad(vessel, tlines[tblocks[0][0]:tblocks[0][1]], ut)
    lines[v_start:v_end] = vessel
    if variant == RELAY:
        hits = [i for i, l in enumerate(lines) if l == GROUND_STATIONS_ON]
        assert len(hits) == 1, "the base's enableGroundStations = True lines: %d" % len(hits)
        cn = [i for i, l in enumerate(lines) if l == "\t\tCommNetParams"]
        assert len(cn) == 1 and cn[0] < hits[0] < _block_end(lines, cn[0]), \
            "enableGroundStations must sit inside CommNetParams"
        lines[hits[0]] = GROUND_STATIONS_OFF
    assert "\t\tactiveVessel = 0" in lines, "the lab stays the active vessel"
    lines[lines.index("\tTitle = " + BASE_TITLE)] = "\tTitle = " + TITLES[variant]
    lines[lines.index("\tDescription = " + BASE_DESCRIPTION)] = \
        "\tDescription = " + DESCRIPTIONS[variant]
    return CRLF.join(lines)


def expected_files(variant: str) -> Dict[str, bytes]:
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = _read_bytes(path)
    files["persistent.sfs"] = build_sfs(variant).encode("utf-8")
    return files


def out_dir(variant: str) -> str:
    return os.path.join(SAVES, variant)


def committed_problems(variant: str) -> List[str]:
    want = expected_files(variant)
    have = {}
    target = out_dir(variant)
    if os.path.isdir(target):
        for root, _dirs, names in os.walk(target):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, target).replace(os.sep, "/")] = _read_bytes(path)
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
