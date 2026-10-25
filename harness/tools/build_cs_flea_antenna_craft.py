#!/usr/bin/env python3
"""Build the committed `Flea Antenna.craft` BY CONSTRUCTION, as a DERIVATIVE.

CS-2's craft (the scripted campaign, docs/dev/autotest-roadmap.md "The scripted campaign
program"). CS-1 measured that the stock Jumping Flea cannot transmit science: the Mk1 pod's
built-in antenna is `antennaType = INTERNAL`, and stock's `CommNetVessel.CanScience` is
`hasScienceAntenna && IsConnectedHome`, where only a non-INTERNAL antenna sets
`hasScienceAntenna`. CS-2 researches Engineering 101 (5 science), which unlocks the first
parts that can: the Communotron 16 / 16-S, and the 2HOT Thermometer beside them.

THE BASE is the shared `harness/fixtures/ships/Jumping Flea.craft`, copied VERBATIM: every
PART block and every scalar, so the flight half (b1 / science_bench_recover's hop) is the
one CS-1 flew. This script only APPENDS two radial parts to the RT-5 Flea booster and links
them from it.

WHY THE 16-S AND NOT THE 16. Both are on Engineering 101 and both are DIRECT antennas, so
either sets `hasScienceAntenna`. The Communotron 16 is a ModuleDeployableAntenna that is
RETRACTED as built and cannot reach KSC until something extends it; the mission library has
no extend action. The 16-S (`SurfAntenna`) is fixed, so the transmit needs no new verb.

WHY THE BOOSTER AND NOT THE POD. The pod's side already carries the two Goo units and its
top node the chute; the booster is a plain 1.25 m cylinder (radius 0.625, its three fins at
azimuths 90 / 210 / 330), so a radial part there needs only a yaw, no cone-slope tilt. The
Flea never stages the booster off, so both parts stay aboard to the recovery.

WHAT IS COPIED AND WHAT IS DERIVED (build_kerbal_x_sweep_craft.py's contract):
  COPIED - each appended PART's tail (EVENTS / ACTIONS / PARTDATA / MODULE*) is lifted
  BYTE-FOR-BYTE out of a stock craft KSP wrote; TAILS names the donor. No MODULE is invented.
  DERIVED - the scalar header (pos / rot / srfN / istg). Both parts attach along local -Z or
  local -Y with a yaw-only rotation, the azimuth -> yaw rule `psi = 90 - azimuth` that
  build_gs1_craft.py validated on stock craft (the 16-S donor, Two-Stage Lander, sits at
  azimuth 45 with yaw 45, which the rule reproduces).

TRANSMIT ORDER. kRPC enumerates experiments in part order, and these two parts are appended
LAST, so the pod's crew report is the first experiment `transmit_science` meets: a crew
report is 5 Mits, 30 EC at the 16-S's 6 EC per Mit, inside the pod's 50 EC (a Goo reading is
10 Mits, 60 EC, more than the craft holds).

ASSUMPTIONS A LIVE RUN CAN FALSIFY, COSMETIC ONLY: the 0.625 radius (a part floating off or
sunk into the booster) and the yaw (a part facing sideways). Neither changes what attaches.

Usage:
    python harness/tools/build_cs_flea_antenna_craft.py --check
    python harness/tools/build_cs_flea_antenna_craft.py --write
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir))
BASE = os.path.join(ROOT, "harness", "fixtures", "ships", "Jumping Flea.craft")
OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-science-flea",
                   "Ships", "VAB", "Flea Antenna.craft")

SHIP_NAME = "Flea Antenna"
HOST = "solidBooster.sm.v2"
RADIAL_RADIUS = 0.625

# Tails lifted BYTE-FOR-BYTE from the donor craft named beside each entry.
TAILS = {
    # <- Two-Stage Lander.craft  [stock Ships/VAB, KSP-written]
    'SurfAntenna': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Science Jr.craft  [stock Ships/VAB, KSP-written]
    'sensorThermometer': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEnviroSensor\n\t\tisEnabled = True\n\t\tsensorActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}

# Radial placements: (part, azimuth degrees, dy from the host centre, uid).
RADIAL = [
    ("SurfAntenna", 30.0, 0.20, 4200600137),
    ("sensorThermometer", 150.0, 0.20, 4200600274),
]


def _fmt(v):
    v = float(v)
    return "%d" % int(v) if v == int(v) else repr(round(v, 9))


def _vec(x, y, z):
    return "%s,%s,%s" % (_fmt(x), _fmt(y), _fmt(z))


def yaw_rotation(azimuth):
    """Yaw-only quaternion that turns a part's local attach axis toward the host axis
    at `azimuth` (degrees from +X toward +Z): psi = 90 - azimuth about +Y."""
    h = math.radians(90.0 - azimuth) / 2.0
    return ",".join(_fmt(c) for c in (0.0, math.sin(h), 0.0, math.cos(h)))


def _host(base_text, name):
    for blk in base_text.split("\nPART\n{")[1:]:
        m = re.search(r"^\tpart = (\S+)$", blk, re.M)
        if not m or m.group(1).rsplit("_", 1)[0] != name:
            continue
        p = re.search(r"^\tpos = (\S+)$", blk, re.M)
        return m.group(1), tuple(float(x) for x in p.group(1).split(","))
    raise SystemExit("base craft has no %s" % name)


def _block(part, uid, pos, rel, rot, host_tok):
    lines = ["PART", "{", "\tpart = %s_%d" % (part, uid), "\tpartName = Part",
             "\tpersistentId = %d" % (900000000 + uid % 1000000),
             "\tpos = %s" % _vec(*pos), "\tattPos = 0,0,0",
             "\tattPos0 = %s" % _vec(*rel), "\trot = %s" % rot,
             "\tattRot = 0,0,0,1", "\tattRot0 = %s" % rot, "\tmir = 1,1,1",
             "\tsymMethod = Radial", "\tautostrutMode = Off",
             "\trigidAttachment = False", "\tistg = -1", "\tresPri = 0",
             "\tdstg = 0", "\tsidx = -1", "\tsqor = -1", "\tsepI = -1",
             "\tattm = 1", "\tmodCost = 0", "\tmodMass = 0", "\tmodSize = 0,0,0",
             "\tsrfN = srfAttach,%s" % host_tok, TAILS[part], "}"]
    return "\n".join(lines)


def _add_links(text, host_token, child_tokens):
    """Insert `link = <child>` lines into the host PART block where KSP writes them:
    after the host's existing links, before its first `attN` / `srfN`. A child that its
    host does not link is dropped at craft load, silently (GS-6 run `2026-09-02_1505`)."""
    i = text.index("\tpart = " + host_token + "\n")
    j = text.index("\n}", i)
    rows = text[i:j].split("\n")
    at = None
    for k, row in enumerate(rows):
        if row.startswith("\tattN = ") or row.startswith("\tsrfN = "):
            at = k
            break
    if at is None:
        raise SystemExit("cannot place link= in " + host_token)
    rows[at:at] = ["\tlink = " + t for t in child_tokens]
    return text[:i] + "\n".join(rows) + text[j:]


def build():
    base = open(BASE, encoding="utf-8").read().replace("\r\n", "\n")
    host_tok, host_pos = _host(base, HOST)
    out = base.rstrip("\n")
    out = re.sub(r"^ship = .*$", "ship = %s" % SHIP_NAME, out, count=1, flags=re.M)
    out = re.sub(r"^description = .*$",
                 "description = CS-2 craft. The stock Jumping Flea with a Communotron 16-S "
                 "and a 2HOT Thermometer on the Flea booster (both Engineering 101). Built "
                 "by construction by harness/tools/build_cs_flea_antenna_craft.py.",
                 out, count=1, flags=re.M)
    blocks = []
    kids = []
    for part, az, dy, uid in RADIAL:
        a = math.radians(az)
        rel = (RADIAL_RADIUS * math.cos(a), dy, RADIAL_RADIUS * math.sin(a))
        pos = (host_pos[0] + rel[0], host_pos[1] + rel[1], host_pos[2] + rel[2])
        blocks.append(_block(part, uid, pos, rel, yaw_rotation(az), host_tok))
        kids.append("%s_%d" % (part, uid))
    out = _add_links(out, host_tok, kids)
    return out + "\n" + "\n".join(blocks) + "\n"


def check():
    """[] when the committed craft is byte-identical to a fresh derivation, else the
    problems as strings (the drift gate's contract)."""
    if not os.path.exists(OUT):
        return ["MISSING %s - run --write" % OUT]
    with open(OUT, encoding="utf-8", newline="") as fh:
        have = fh.read()
    return [] if have == build() else ["DRIFT: %s differs from the derivation" % OUT]


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args(argv)
    if a.write:
        text = build()
        os.makedirs(os.path.dirname(OUT), exist_ok=True)
        with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote %s (%d lines)" % (OUT, text.count("\n")))
        return 0
    problems = check()
    print("\n".join(problems) or "OK committed craft matches the derivation")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
