#!/usr/bin/env python3
"""Build the committed `AIR Drone Carrier.craft` BY CONSTRUCTION, as a DERIVATIVE.

WHAT IT IS FOR. AIR-3 (todo NEXT-TESTS-TO-BUILD-2026-11-11 item 3b) needs two aircraft of
ONE recording tree in the air at once, both loaded. A switch to a vessel outside the tree
does not add it, so the second vessel has to come out of the first one's tree by a split
in the air. This craft is the stock Mallard (the plane AIR-1 flies green) carrying a small
parachute drone under its right wing on a radial decoupler. The mission flies the Mallard
off the Runway, releases the drone over the sea (a JointBreak split: the drone becomes a
loaded background member of the Mallard's tree), lets it hang under its Mk16, then turns
back and flies the Mallard into it.

WHY A DERIVATIVE OF THE MALLARD AND NOT TWO PLANES. Joining two planes needs a surface
attachment onto Mk3 parts with no stock example to lift; a top-mounted target risks the
tail at release; and a powered target cannot be flown once it is not the active vessel.
A drone under a canopy holds a line on its own: it sinks a few metres a second and,
with no wind in KSP, barely drifts once the canopy is open.

WHAT IS COPIED AND WHAT IS DERIVED (build_kerbal_x_sweep_craft.py's contract):

  COPIED VERBATIM - the base is the committed
  `harness/fixtures/saves/gs1-two-stage-pad/Ships/VAB/Mallard.craft` (the stock Mallard,
  a version 1.2.0 file in the OLD attach-token format, where an `attN` / `srfN` names the
  other part only, with no node offsets). Every one of its 38 PART blocks is kept byte for
  byte except the two edits listed under DERIVED. Every appended part's tail (EVENTS /
  ACTIONS / PARTDATA / MODULE / RESOURCE) is lifted from a committed craft KSP wrote:
  `radialDecoupler1-2` and `fuelTank` from `harness/fixtures/ships/Kerbal X.craft` (one
  of its booster pairs), `probeCoreOcto2.v2` and `parachuteSingle` from
  `harness/fixtures/ships/GS1 Auto-Chute Booster.craft` (whose own tails came from stock
  craft, see build_gs1_craft.py). The donors are read at build time, so a change to a
  donor reds the drift gate here too.

  DERIVED - (1) the ship name and description; (2) the two JetEngines move from istg 0 to
  istg 1, so the FIRST stage click lights the engines (Parsek's first-staging-on-the-pad
  auto-record trigger, AIR-1's road) and the SECOND fires the decoupler and arms the Mk16;
  (3) one `link =` line on the right wing (a child not linked from its parent is dropped at
  craft load, silently; build_kerbal_x_sweep_craft.py learned it the hard way); (4) the
  four appended PART headers; (5) the Mk16's `deployAltitude` goes from the stock 1000 to
  CHUTE_DEPLOY_ALTITUDE (2400 m): the drone is released at about 2500 m, so the canopy
  opens fully seconds after the release instead of 1500 m lower, which leaves the chaser
  about 450 s of a slowly sinking target (three passes). The fuel tank is EMPTY (both
  `amount =` lines 0): it is the drone's body, 0.25 t, and keeps the drone light enough
  to hang under one Mk16. MEASURED (two flights, 2026-11-17, deployAltitude 1500 and a
  release at 1617 m): the Mk16 semi-deploys at the release and the drone falls almost
  freely; it reads fully deployed about 4.5 s in, but slows only about 9 s in, about 380 m
  down; from then on it sinks 4.6 - 4.8 m/s (1200 m to 170 m) and drifts no more.

THE GEOMETRY. The decoupler and the tank keep exactly the RELATIVE pose they have in
Kerbal X (decoupler origin on the core, tank centre 0.789 m out and 0.098 m down its
axis, same rotation), and the whole pair is turned by one rotation R (-90 deg about X)
that takes Kerbal X's "outward" (-Z) to world DOWN and the tank's axis (+Y) to world AFT
(-Z). So the decoupler's attach face points UP at the wing, the tank hangs below it lying
fore-aft, and the probe core and the Mk16 stack on the tank's top node, pointing aft
(trailing, so the canopy streams behind at release). Stack offsets are read off real
`attN` tokens: the tank's top node 0.981724977 (Kerbal X), the Octo2's +/-0.0610621 and
the Mk16's bottom -0.01508113 (build_gs1_craft.py NODE_OFFSET).

ASSUMPTIONS A LIVE RUN CAN FALSIFY, in the order they would bite:
  A1 THE WING'S UNDERSIDE. The decoupler sits at (5.0, 8.72, -1.0), 2.9 m outboard of
     the right wing's root, under the wing's mid-plane, which at that point reads about
     9.03 from the wing's own rotation (local Z is world up, 2 deg dihedral, 3.6 deg
     incidence). The wing's thickness is not in any craft file, so the decoupler is
     placed 0.1 - 0.3 m BELOW the estimated underside: a visible gap is cosmetic, an
     overlap could kick the drone at release. A drone floating under the wing is this.
  A2 GROUND CLEARANCE. The tank's lowest point is about y 7.31, half a metre above the
     Mk3 fuselage's bottom (6.79), so it clears the runway whenever the fuselage does.
     A drone that strikes the runway on the takeoff roll is this number.
  A3 THE OLD TOKEN FORMAT. The appended parts use the base file's 1.2.0 token format
     (`attN = top,<part>`, `srfN = srfAttach,<part>`) because the loader picks the
     parser by the file's version. A part missing from the launched vessel (the
     recorder's census, or the mission's `releaseConfirmFrames` flake reading no drone
     after the second click) is this.
  A4 STAGING. istg 1 lights the engines, istg 0 fires the decoupler and arms the Mk16.
     A drone that drops with the engine start is this table.
  A5 THE VESSEL NAME. KSP names a split-off probe-cored vessel "<ship> Probe" (GS-1 read
     "GS1 Auto-Chute Booster Probe"), so the drone is expected as
     "AIR Drone Carrier Probe" (the mission's `targetVesselName`).

Usage:
    python harness/tools/build_air_drone_carrier.py --check
    python harness/tools/build_air_drone_carrier.py --write

Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir))
VAB = os.path.join(ROOT, "harness", "fixtures", "saves", "gs1-two-stage-pad", "Ships", "VAB")
BASE = os.path.join(VAB, "Mallard.craft")
KERBAL_X = os.path.join(ROOT, "harness", "fixtures", "ships", "Kerbal X.craft")
GS1 = os.path.join(ROOT, "harness", "fixtures", "ships", "GS1 Auto-Chute Booster.craft")
SHIP_NAME = "AIR Drone Carrier"
OUT = os.path.join(VAB, SHIP_NAME + ".craft")

# The right wing (+X) of the stock Mallard, the drone's parent.
WING = "airlinerMainWing_4290606124"
# The two engines that move to istg 1.
ENGINES = ("JetEngine_4293834828", "JetEngine_4293796280")
ENGINE_ISTG = 1
DRONE_ISTG = 0
CHUTE_DEPLOY_ALTITUDE = 2400

# The decoupler's origin (assumption A1).
ANCHOR = (5.0, 8.72, -1.0)

# Kerbal X's booster pair whose relative pose is copied (decoupler, tank).
KX_DECOUPLER = "radialDecoupler1-2_4294042434"
KX_TANK = "fuelTank_4294017080"

# Effective stack offsets, read off committed attN tokens.
TANK_TOP = 0.981724977          # Kerbal X fuelTank `attN = top,..._0|0.981724977|0`
OCTO_HALF = 0.0610621           # build_gs1_craft.py NODE_OFFSET probeCoreOcto2.v2
CHUTE_BOTTOM = -0.01508113      # build_gs1_craft.py NODE_OFFSET parachuteSingle

# R = -90 deg about X: (x, y, z) -> (x, z, -y).
Q_R = (-math.sqrt(0.5), 0.0, 0.0, math.sqrt(0.5))

_UID = {"decoupler": 4200700001, "tank": 4200700002, "probe": 4200700003,
        "chute": 4200700004}


def _qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def _qrot(q, v):
    r = _qmul(_qmul(q, (v[0], v[1], v[2], 0.0)), (-q[0], -q[1], -q[2], q[3]))
    return r[:3]


def _qinv(q):
    return (-q[0], -q[1], -q[2], q[3])


def _fmt(v):
    v = round(float(v), 7)
    if v == 0:
        return "0"
    s = repr(v)
    return s[:-2] if s.endswith(".0") else s


def _vec(v):
    return ",".join(_fmt(c) for c in v)


def _read(path):
    with open(path, "r", encoding="utf-8-sig", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _blocks(text):
    """{part token: (header lines, tail text)} for every PART in ``text``."""
    out = {}
    for blk in text.split("\nPART\n{")[1:]:
        m = re.search(r"^\tpart = (\S+)$", blk, re.M)
        i = blk.index("\n\tEVENTS\n")
        out[m.group(1)] = (blk[:i], blk[i + 1:].rsplit("\n}", 1)[0])
    return out


def _first_tail(blocks, name):
    for token, (_head, tail) in blocks.items():
        if token.rsplit("_", 1)[0] == name:
            return tail
    raise SystemExit("donor has no %s" % name)


def _header_vec(head, key):
    m = re.search(r"^\t%s = (\S+)$" % key, head, re.M)
    return tuple(float(x) for x in m.group(1).split(","))


def _empty_tank(tail):
    return re.sub(r"^\t\tamount = \S+$", "\t\tamount = 0", tail, flags=re.M)


def _chute_altitude(tail):
    out, n = re.subn(r"^\t\tdeployAltitude = \S+$",
                     "\t\tdeployAltitude = %d" % CHUTE_DEPLOY_ALTITUDE, tail, flags=re.M)
    if n != 1:
        raise SystemExit("the Mk16 donor has no single deployAltitude")
    return out


def _uid(key, name):
    return "%s_%d" % (name, _UID[key])


def _part(token, pos, rot, parent_pos, parent_rot, istg, attm, links=(),
          attn=(), srfn=None, tail=""):
    """One PART block in the base file's 1.2.0 header shape (no persistentId: the
    base carries none and KSP assigns one at load)."""
    rel = _qrot(_qinv(parent_rot), tuple(p - q for p, q in zip(pos, parent_pos)))
    rel_rot = _qmul(_qinv(parent_rot), rot)
    lines = ["PART", "{", "\tpart = %s" % token, "\tpartName = Part",
             "\tpos = %s" % _vec(pos), "\tattPos = 0,0,0", "\tattPos0 = %s" % _vec(rel),
             "\trot = %s" % _vec(rot), "\tattRot = 0,0,0,1",
             "\tattRot0 = %s" % _vec(rel_rot), "\tmir = 1,1,1", "\tsymMethod = Radial",
             "\tautostrutMode = Off", "\tistg = %d" % istg, "\tresPri = 0",
             "\tdstg = 0", "\tsidx = -1", "\tsqor = -1", "\tsepI = 0",
             "\tattm = %d" % attm, "\tmodCost = 0", "\tmodMass = 0",
             "\tmodSize = 0,0,0"]
    lines += ["\tlink = %s" % l for l in links]
    lines += ["\tattN = %s,%s" % (node, other) for node, other in attn]
    if srfn:
        lines.append("\tsrfN = srfAttach,%s" % srfn)
    lines.append(tail)
    lines.append("}")
    return "\n".join(lines)


def layout():
    """The four drone parts as (key, name, pos, rot), derived from Kerbal X's pair."""
    kx = _blocks(_read(KERBAL_X))
    dec_head = kx[KX_DECOUPLER][0]
    tank_head = kx[KX_TANK][0]
    kx_dec_pos = _header_vec(dec_head, "pos")
    kx_tank_pos = _header_vec(tank_head, "pos")
    kx_dec_rot = _header_vec(dec_head, "rot")
    kx_tank_rot = _header_vec(tank_head, "rot")
    d = tuple(t - c for t, c in zip(kx_tank_pos, kx_dec_pos))
    dec_pos = ANCHOR
    tank_pos = tuple(a + b for a, b in zip(ANCHOR, _qrot(Q_R, d)))
    dec_rot = _qmul(Q_R, kx_dec_rot)
    tank_rot = _qmul(Q_R, kx_tank_rot)
    axis = _qrot(tank_rot, (0.0, 1.0, 0.0))      # the tank's top direction, world
    probe_pos = tuple(p + a * (TANK_TOP + OCTO_HALF) for p, a in zip(tank_pos, axis))
    chute_pos = tuple(p + a * (OCTO_HALF - CHUTE_BOTTOM) for p, a in zip(probe_pos, axis))
    return [("decoupler", "radialDecoupler1-2", dec_pos, dec_rot),
            ("tank", "fuelTank", tank_pos, tank_rot),
            ("probe", "probeCoreOcto2.v2", probe_pos, tank_rot),
            ("chute", "parachuteSingle", chute_pos, tank_rot)]


def _add_links(text, host_token, child_tokens):
    """Insert `link = <child>` lines into the host PART block after its last `link`
    (or after `modSize`), before its first `sym` / `attN` / `srfN`."""
    i = text.index("\tpart = " + host_token + "\n")
    j = text.index("\n}", i)
    rows = text[i:j].split("\n")
    at = None
    for k, row in enumerate(rows):
        if row.startswith("\tmodSize = ") or row.startswith("\tlink = "):
            at = k + 1
    if at is None:
        raise SystemExit("cannot place link= in " + host_token)
    rows[at:at] = ["\tlink = " + t for t in child_tokens]
    return text[:i] + "\n".join(rows) + text[j:]


def _set_istg(text, token, istg):
    i = text.index("\tpart = " + token + "\n")
    j = text.index("\n}", i)
    blk = text[i:j]
    new, n = re.subn(r"^\tistg = -?[0-9]+$", "\tistg = %d" % istg, blk, count=1,
                     flags=re.M)
    if n != 1:
        raise SystemExit("no istg in " + token)
    return text[:i] + new + text[j:]


def build():
    base = _read(BASE)
    wing_head = _blocks(base)[WING][0]
    wing_pos = _header_vec(wing_head, "pos")
    wing_rot = _header_vec(wing_head, "rot")
    kx = _blocks(_read(KERBAL_X))
    gs1 = _blocks(_read(GS1))
    tails = {"radialDecoupler1-2": _first_tail(kx, "radialDecoupler1-2"),
             "fuelTank": _empty_tank(_first_tail(kx, "fuelTank")),
             "probeCoreOcto2.v2": _first_tail(gs1, "probeCoreOcto2.v2"),
             "parachuteSingle": _chute_altitude(_first_tail(gs1, "parachuteSingle"))}

    out = base.rstrip("\n")
    out = re.sub(r"^ship = .*$", "ship = %s" % SHIP_NAME, out, count=1, flags=re.M)
    out = re.sub(r"^description = .*$",
                 "description = AIR-3 drone carrier. The stock Mallard with a parachute "
                 "drone (an empty FL-T400, an Octo2 probe core and a Mk16) under its right "
                 "wing on a radial decoupler; stage 1 lights the engines, stage 0 drops the "
                 "drone and arms its canopy. Built by construction by "
                 "harness/tools/build_air_drone_carrier.py.",
                 out, count=1, flags=re.M)
    for token in ENGINES:
        out = _set_istg(out, token, ENGINE_ISTG)

    parts = {key: (name, pos, rot) for key, name, pos, rot in layout()}
    tok = {key: _uid(key, parts[key][0]) for key in parts}
    blocks = [
        _part(tok["decoupler"], parts["decoupler"][1], parts["decoupler"][2],
              wing_pos, wing_rot, DRONE_ISTG, 1, links=[tok["tank"]], srfn=WING,
              tail=tails["radialDecoupler1-2"]),
        _part(tok["tank"], parts["tank"][1], parts["tank"][2],
              parts["decoupler"][1], parts["decoupler"][2], DRONE_ISTG, 1,
              links=[tok["probe"]], attn=[("top", tok["probe"])],
              srfn=tok["decoupler"], tail=tails["fuelTank"]),
        _part(tok["probe"], parts["probe"][1], parts["probe"][2],
              parts["tank"][1], parts["tank"][2], DRONE_ISTG, 0,
              links=[tok["chute"]], attn=[("bottom", tok["tank"]), ("top", tok["chute"])],
              tail=tails["probeCoreOcto2.v2"]),
        _part(tok["chute"], parts["chute"][1], parts["chute"][2],
              parts["probe"][1], parts["probe"][2], DRONE_ISTG, 0,
              attn=[("bottom", tok["probe"])], tail=tails["parachuteSingle"]),
    ]
    out = _add_links(out, WING, [tok["decoupler"]])
    return out + "\n" + "\n".join(blocks) + "\n"


# ---------------------------------------------------------------------------
# Post-conditions, run in-process by the unit suite (test_air3_drone_intercept.py).
# ---------------------------------------------------------------------------

def part_headers(text):
    """[(token, {key: [values]})] for every PART, scalar header lines only."""
    out = []
    for token, (head, _tail) in _blocks(text).items():
        d = {}
        for line in head.split("\n"):
            if line.startswith("\t") and not line.startswith("\t\t") and " = " in line:
                k, v = line[1:].split(" = ", 1)
                d.setdefault(k, []).append(v)
        out.append((token, d))
    return out


def verify(text):
    """Every post-condition the craft must satisfy; a list of problems (empty = good)."""
    problems = []
    if not text.startswith("ship = %s\n" % SHIP_NAME):
        problems.append("first line must be `ship = %s`" % SHIP_NAME)
    if "\nversion = 1.2.0\n" not in text:
        problems.append("the base's `version = 1.2.0` must stay (it picks the token parser)")
    headers = dict(part_headers(text))
    if len(headers) != 42:
        problems.append("expected 42 parts (38 Mallard + 4 drone), found %d" % len(headers))
    tokens = set(headers)
    for token, d in headers.items():
        for link in d.get("link", []):
            if link not in tokens:
                problems.append("%s links a missing part %s" % (token, link))
        for srf in d.get("srfN", []):
            if srf.split(",", 1)[1] not in tokens:
                problems.append("%s surface-attaches to a missing part" % token)
        for att in d.get("attN", []):
            if att.split(",", 1)[1] not in tokens:
                problems.append("%s stack-attaches to a missing part" % token)
    parent = {}
    for token, d in headers.items():
        for link in d.get("link", []):
            parent[link] = token
    roots = [t for t in tokens if t not in parent]
    if roots != ["mk3Cockpit.Shuttle_4292993932"]:
        problems.append("exactly one root (the Mk3 cockpit) expected, found %r" % roots)
    for token, d in headers.items():
        name = token.rsplit("_", 1)[0]
        istg = int(d["istg"][0])
        if name == "JetEngine" and istg != ENGINE_ISTG:
            problems.append("%s istg %d, want %d" % (token, istg, ENGINE_ISTG))
        if name in ("radialDecoupler1-2", "parachuteSingle") and istg != DRONE_ISTG:
            problems.append("%s istg %d, want %d" % (token, istg, DRONE_ISTG))
    dec = [t for t in tokens if t.startswith("radialDecoupler1-2_")]
    if len(dec) != 1 or parent.get(dec[0]) != WING:
        problems.append("the decoupler must be the right wing's child")
    if text.count("name = ModuleCommand") < 2:
        problems.append("both sides of the decoupler need a ModuleCommand")
    if text.count("name = ModuleParachute") != 1:
        problems.append("exactly one ModuleParachute (the drone's Mk16) expected")
    if ("\t\tdeployAltitude = %d\n" % CHUTE_DEPLOY_ALTITUDE) not in text:
        problems.append("the Mk16 must deploy fully at %d m" % CHUTE_DEPLOY_ALTITUDE)
    tank = [t for t in tokens if t.startswith("fuelTank_")]
    if len(tank) == 1:
        tail = _blocks(text)[tank[0]][1]
        if re.search(r"^\t\tamount = (?!0$)\S+$", tail, re.M):
            problems.append("the drone's tank must be empty")
    return problems


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args(argv)
    text = build()
    problems = verify(text)
    if problems:
        for p in problems:
            print("PROBLEM " + p)
        return 1
    if a.write:
        with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote %s (%d lines)" % (OUT, text.count("\n")))
        return 0
    if not os.path.exists(OUT):
        print("MISSING %s - run --write" % OUT)
        return 1
    have = _read(OUT)
    print("OK committed craft matches the derivation" if have == text
          else "DRIFT: committed craft differs from the derivation")
    return 0 if have == text else 1


if __name__ == "__main__":
    sys.exit(main())
