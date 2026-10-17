#!/usr/bin/env python3
"""Build the committed LABX Orbital Lab .craft BY CONSTRUCTION.

WHY THIS SCRIPT EXISTS. The science-lab (ModuleScienceLab / ModuleScienceConverter)
flight lanes need a crewed Mobile Processing Lab in low Kerbin orbit, with a
scientist seated IN the lab, experiments aboard to feed it, and enough steady
electric charge for it to research. No stock craft and no committed fixture carries
a Large.Crewed.Lab at all, and no KSP-written file on the dev machine holds a lab
PART node either (the only matches are tech-tree `part = Large.Crewed.Lab` lines in
career saves). So the craft is authored here, deterministically, by the method
build_gs1_craft.py and build_dd1_craft.py established: this file IS the derivation
record, `--check` re-derives and compares against the committed bytes, and
`test_labx_orbital_lab_craft.py::CraftDriftTests` runs both in-process so a hand
edit to the .craft (or a drifted derivation) reds in the unit suite instead of in a
live flight. LABX-1-lab-orbit-ascent is the lane that flies it.

WHAT IS DERIVED AND WHAT IS COPIED, stated exactly:

  COPIED VERBATIM -- every PART's tail (EVENTS / ACTIONS / PARTDATA / MODULE* /
  RESOURCE*) is lifted byte-for-byte out of a craft KSP itself wrote, EXCEPT the
  lab's (A4). The source of each is named above its TAILS entry. Donors were
  chosen by MODULE-sequence match against the automation instance's stock part
  cfgs: sixteen of the seventeen donor tails carry the cfg's module list exactly or
  short only a trailing ModuleCargoPart, or (the OKTO) the cfg list followed by
  the ModuleScienceExperiment the instance's ConfigCache appends and the
  ModuleTripLogger KSP adds to every command part at load; the mk1pod.v2 tail is
  GS-1's Jumping Flea tail, already flown. NO tail line is rewritten: every donor already carries stock
  defaults (full propellant, engines unignited, thrustPercentage 100, every
  actionGroup None except the mk1 pod's stock `ModuleColorChanger` Light binding),
  which verify() re-asserts.

  DERIVED -- the scalar header of each PART (pos / rot / attPos0 / attN / srfN /
  istg / links). Stack arithmetic is GS-1's validated formula
      child.pos = parent.pos + parent_node_offset - child_opposite_node_offset
  with every stack-node offset read off a KSP-written craft's PIPE-FORMAT attN token
  (never a part cfg -- the science.module `scale = 0.1` trap: its cfg says 4.9 /
  -4.10, the craft token says 0.6125 / -0.5125), EXCEPT the lab's (A5).
  attPos0 is the offset from the PARENT part (KSP's own convention, read off
  Kerbal X / Kerbal 2 / Science Jr: e.g. Kerbal X's Poodle carries attPos0
  0,-0.92,0, its offset below the X200-16 it hangs from), and the root's own pos
  on the root. The tender (A12) stacks UPWARD from the pod's top node by the
  mirror formula (`_stack_up`), and its one flipped part (the upper Jr port,
  rot 1,0,0,0) meets its twin top node to top node, as Crater Crawler's pair
  does: there the two port origins sit 0.2948 apart, twice the 0.1474114 top-node
  offset, which is what the formula gives.

  RADIAL PLACEMENT. Two kinds, both from KSP-written evidence:
  (a) COPIED RELATIVE TRANSFORMS. The 2HOT thermometer and PresMat barometer sit
      on the mk1 pod, and the Mystery Goo on the SC-9001, at EXACTLY the
      parent-relative offset and rotation stock Science Jr.craft gives them on the
      same parent parts (its pod and its science.module both carry identity
      rotation, as ours do). Nothing about those three is computed. The
      Communotron 16-S (A11) is the same kind, from the operator's c1 career
      craft L5-B4-U1-C1.craft (KSP 1.12.5), whose mk1pod.v2 also carries identity
      rotation: its rot is copied verbatim and its parent-relative offset is that
      craft's pos(antenna) - pos(pod). That donor had nudged the antenna with the
      editor offset gizmo (attPos 0,-0.0228,-0.0790 over attPos0 0,0.1741,0.5020);
      KSP places a loaded part at `pos`, and pos - parent.pos equals attPos0 +
      attPos on the donor to float precision, so the header here writes the sum
      as attPos0 with attPos 0,0,0, which is what placing it there without the
      gizmo would have written.
  (b) AZIMUTH RINGS on the X200-16, via one formula
          q(az) = Ry(-az) * q_ref
      where q_ref is the part's own rotation as KSP wrote it at azimuth 0 and az is
      measured from +X toward +Z (the rotation that carries +X onto the azimuth
      direction is Ry(-az) in Unity's left-handed frame). VALIDATED, exactly, up to
      the quaternion double cover:
        - Z-400 battery (q_ref = Ry(-90), i.e. psi = -90 - az): Kerbal X's two
          batteries on this very X200-16 at az 0 / 180, AND Orbiter 1A's three on
          an X200-16 at az 90 / -30 / -150.
        - PB-NUK RTG (q_ref = (0.5, 0.5, -0.5, 0.5), its +Y radially outward):
          Ion-Powered Space Probe's RTG at az 180 is (-0.5, 0.5, -0.5, -0.5),
          which is this formula's output negated.
      (GS-1 / DD1 named the battery's family "-Z, psi = 90 - az"; that family is
      the OX-STAT's, whose node_attach points +Z. The battery's node_attach points
      -Z and KSP writes psi = -90 - az for it. Per-part q_ref measured from a
      KSP-written instance is the rule here, not a family table.)

HOW A FAILED FLIGHT IS DIAGNOSED WITHOUT RE-DERIVING ANY OF THIS. The assumptions a
live run can falsify, in the order they would bite:

  A1  ROOT-RELATIVE ORIGIN. The pod is placed at y = 25 (Kerbal X puts its pod at
      ~23.5). Editor-space height is cosmetic; KSP re-seats the craft on the pad.
      Only the DELTAS matter.
  A2  CREW SEATING (the lab lanes depend on this, not just this flight). kRPC's
      launch_vessel(crew=[names]) builds a scratch roster of the named Available
      kerbals and calls stock KerbalRoster.DefaultCrewForVessel, which (decompiled,
      KSP 1.12.5) fills ONLY parts that are control sources (or carry a KerbalSeat)
      and puts a PILOT in the first seat it fills; kRPC then seats every named
      kerbal left over in the first empty seat in PART order
      (SpaceCenter.cs LaunchConfig.AddCrewToManifest, mods/krpc). This craft has
      exactly ONE control source with crew capacity -- the ONE-seat Mk1 pod, the
      root and first PART -- and exactly one other crewable part, the lab (2
      seats). So with crew = ["Valentina Kerman", "Bob Kerman"]: Valentina (the
      pilot) takes the pod, Bob (the scientist) is the leftover and lands in lab
      seat 0. That is why the pod is a Mk1 and not a Mk1-3: a 3-seat pod would
      absorb both names and leave the lab empty. If a lab lane finds Bob in the
      pod, this analysis is what was wrong. The tender's OKTO (A12) is a SECOND
      control source but has NO seat (CrewCapacity 0, minimumCrew 0): the fill
      has nothing to put there and kRPC's leftover pass finds no empty seat in
      it, so the pod stays the only seated control source and the seating above
      is unchanged.
  A3  STAGING. `istg` is the authority KSP reads. Two stages only, DD1's shape:
      istg 1 ignites the Mainsail (the pad click); istg 0 fires the TD-25
      (Decoupler.2) AND ignites the Poodle (hot-stage, which MechJeb autostage
      drives when the Mainsail flames out). The Jumbo-64 carries istg 0 (its
      separator's stage); every part above the decoupler carries istg -1 (never
      staged). There is NO stage after istg 0, so nothing can drop the lab. The
      tender (A12) carries istg -1 throughout and its two docking ports carry
      ModuleDockingNode stagingEnabled = False (the cfg default, kept by the donor
      tail): a docking node stages only when stagingEnabled is set
      (ModuleDockingNode.OnActive / StagingEnabled, decompiled), so no stage and
      no MechJeb autostage can separate the tender.
  A4  THE LAB TAIL IS AUTHORED AND CARRIES NO MODULE BLOCK. No donor exists. KSP's
      craft loader instantiates every module from the part prefab and only loads
      values onto modules a MODULE node names (Part.LoadModule, decompiled), so a
      part with no MODULE nodes loads with every module at its cfg default:
      ModuleScienceContainer / ModuleScienceLab (no data) / ModuleScienceConverter
      (not researching) / ModuleKerbNetAccess / ModuleExperienceManagement /
      FlagDecal / ModuleColorChanger / ModuleInventoryPart (empty). Nothing is
      invented that could mis-index. A lab that loads with odd state is this.
  A5  LAB STACK OFFSETS from the cfg (top +1.825, bottom -1.831; rescaleFactor 1,
      no `scale`, so no rescale ambiguity), not from a craft token (none exists).
      A visible gap or overlap at the lab's ends is this number.
  A6  RADII. Batteries at r = 1.20783 (Kerbal X, same tank). RTGs at
      r = 1.20783 + 0.3268025: the RTG's attach node sits 0.3268025 down its own
      axis, and on Ion-Powered Space Probe the node-surface radius this implies
      (0.66336 - 0.32680 = 0.33656) agrees with the thermometer's surface radius
      on the same OKTO (0.33565) to a millimetre. Radial parts clip freely;
      a floating or sunk RTG is cosmetic unless the joint fails.
  A7  DV / TWR (every mass, Isp and thrust from the 1.12.5 part cfgs; see
      `performance()` and verify()'s floor checks): upper stage wet 16.435 t
      (the 16-S 0.015 t and the tender 0.15 t included), dry 8.435 t, Poodle Isp
      350 vac -> 2,289 m/s vac, vac TWR 1.55 at ignition. Pad mass 58.595 t,
      Mainsail ASL thrust 1500 * 285/310 = 1,379 kN -> pad TWR 2.40; booster vac
      dv = 310 * g0 * ln(58.595/26.595) = 2,401 m/s (2,208 at the ASL Isp).
      Total ~4,691 m/s vac against ~3,400 to a 100 km LKO. THE STAGING
      TIMING THE FLIGHT DEPENDS ON: the booster must burn out DURING the ascent
      (it carries well under the ~3,000+ m/s a MechJeb ascent spends before its
      apoapsis latch), so the decoupler fires before CIRCULARIZE and the
      `stackAttached` row (vessel_count must not rise during CIRCULARIZE / PARK)
      holds. A booster still burning at the apoapsis latch is this estimate.
      TWR 2.40 is high-normal; MechJeb flies it at full throttle.
  A8  POWER. Eight RTGs, 8 * 0.75 = 6.0 EC/s, sun-independent and non-deployable,
      against the lab converter's 5 EC/s research draw (ModuleScienceConverter
      powerRequirement = 5); 4 * 400 EC Z-400 + the pod's 50 EC buffer the
      reaction wheels and any data-processing burst; the tender's OKTO 50 EC +
      Z-200 200 EC are its own once it separates (OKTO draw ~0.05 EC/s). NO
      deployable part anywhere (no panels, no deploying antenna): a lab lane
      never has to click anything open.
  A9  COMM. The Communotron 16-S (`SurfAntenna`; C16S.cfg: antennaType DIRECT,
      antennaPower 500000, packetSize 2 Mits every 0.6 s at 12 EC per packet) on
      the pod is the science link. The Mk1 pod's own ModuleDataTransmitter is NOT:
      it is antennaType INTERNAL, ModuleDataTransmitter.CanTransmit() returns
      false for INTERNAL and CommNetVessel.GetBestTransmitter skips it, so without
      a DIRECT or RELAY part the lab's TransmitScience has nothing to send through
      (the first flown LABX shape had none). Range to a KSC ground station is
      sqrt(500000 x DSN power): ~31,600 km at the level-1 tracking station, far
      beyond a 100 km LKO. The link is still occluded behind Kerbin for part of
      each orbit unless the save enables extra ground stations. Power: a
      transmission draws ~20 EC/s against the RTGs' 6.0, so a long one runs down
      the 1,650 EC of batteries (~115 s, ~380 Mits) before it stalls. The 16-S is
      NON-deploying (no ModuleDeployableAntenna), so nothing has to open before a
      transmission and nothing extended can break off during the ascent.
  A10 CONTROL. Two ModuleCommand parts: the pod (the ROOT, so the launch control
      point) and the tender's OKTO, aligned with it (upright, same axis). Torque:
      the pod's own wheel, the OKTO's 0.3 kN-m, and the RW-L asasmodule1-2
      directly under the lab; pilot SAS from Valentina; Mainsail and Poodle
      gimbals. No fins: a mass-forward 2.5 m stack, DD1's finless precedent flew
      green under MechJeb. On the automation instance ModuleManager adds a
      MechJebCore to every probe core and pod, so this vessel carries two cores;
      MechJeb steers by the vessel's control point, not by the core's part.
  A11 THE ANTENNA PLACEMENT. The 16-S sits on the pod at azimuth +90 (+Z), 0.15 m
      above the pod's origin, clear of the 2HOT (az -30) and PresMat (az -150)
      and of the tender's Jr port on the pod's top node. It is on the
      never-staged upper stage (istg -1, above the decoupler), so no stage drops
      it. Surface-attached at the upper end of the stack, it takes the ascent's
      airflow and heating: 0.015 t, drag 0.2, maxTemp 2000 K, non-deploying. An
      antenna lost during the ascent (26 parts expected after the booster drop,
      25 seen) is this placement.
  A12 THE PROBE TENDER (the Re-Fly separation and the dock / undock partner the
      lab lanes need). Parsek authors a Re-Fly rewind point only at a split with
      TWO controllable sides, and the booster drop has one, so the tender is a
      second controllable vessel-to-be riding on the lab vessel: on the pod's top
      node a Clamp-O-Tron Jr (dockingPort3, upright), a second Jr FLIPPED (rot
      1,0,0,0) and joined to it face to face exactly as stock Crater Crawler's
      pair is, then an OKTO probe core (probeCoreOcto.v2: ModuleCommand with no
      seat, ModuleSAS, ModuleReactionWheel, 50 EC, INTERNAL antenna 5000) and a
      Z-200 (batteryBankMini, 200 EC) as the nose. 0.15 t in all. The Jr fits
      both the pod's top node and the 0.625 m OKTO. No decoupler anywhere in
      it. Ports joined in the editor load PreAttached: ModuleDockingNode's
      PreAttached state arms the port's own Undock event, and Undock() on a
      PreAttached port calls Decouple() -> Part.decouple() (decompiled, KSP
      1.12.5), so the FIRST separation is a decouple-shaped split (onPartJointBreak
      / a new vessel), not a Part.Undock / onVesselsUndocking event; it happens
      only when commanded (the M-A2 seam's `PartAction button=undock`, or kRPC
      DockingPort.undock()). After it the two Jr ports are plain Ready ports, so a
      re-dock and a later undock are the true dock / undock pair. The tender
      keeps a CommNet link for control through the OKTO's INTERNAL antenna
      (control, unlike science transmission, accepts an INTERNAL antenna) and
      needs no antenna part. Ascent heating: the OKTO's maxTemp is 1,200 K, the
      lowest aboard, shielded by the Z-200 above it; a tender lost in the ascent
      is this.

Usage:
    python harness/tools/build_lab_orbit_craft.py --check     # verify committed bytes
    python harness/tools/build_lab_orbit_craft.py --write     # regenerate the file

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)                       # harness/

# The craft is committed ONCE, in the shared library at harness/fixtures/ships/, and
# `run.py::stage_fixture` overlays it into each consuming save's Ships/VAB at stage
# time (the directory kRPC launch_vessel resolves <save>/Ships/VAB/<craftName>.craft
# against). Consumers are declared in harness/fixtures/shared-ships.toml:
# bdock-forge-base (LABX-1 launches it from there), lab-lko-sandbox (LABX-1's
# harvested produced save, which arrived holding a byte-identical second copy - the
# PROMOTION moment, the route GS1 Auto-Chute Booster and Logi Cargo Rig took) and
# lab-lko-science (build_lab_lko_science.py, the science-mode host of the lab lanes).
SHIP_NAME = "LABX Orbital Lab"
CRAFT_PATH = os.path.join(_HARNESS_ROOT, "fixtures", "ships", SHIP_NAME + ".craft")

# Root placement (assumption A1).
ROOT_Y = 25.0

# EFFECTIVE stack-node offsets. (part name) -> {"top": dy, "bottom": dy}. Every one
# is a KSP-written craft's pipe-format attN token except the lab's (A5).
NODE_OFFSET = {
    # Jumping Flea: attN = top,...|0.6423756|0 and attN = bottom,...|-0.4050379|0
    "mk1pod.v2":               {"top": 0.6423756, "bottom": -0.4050379},
    # Science Jr: attN = top,...|0.6125|0 and attN = bottom,...|-0.5125|0 (the cfg's
    # 4.9 / -4.10 are pre-`scale = 0.1`; c1 Science Module S agrees: 0.612500012)
    "science.module":          {"top": 0.6125, "bottom": -0.5125},
    # largeCrewedLab.cfg node_stack_top / node_stack_bottom, rescaleFactor 1 (A5)
    "Large.Crewed.Lab":        {"top": 1.825, "bottom": -1.831},
    # Kerbal X: attN = top,...|0.25|0 and attN = bottom,...|-0.25|0
    "asasmodule1-2":           {"top": 0.25, "bottom": -0.25},
    # Kerbal X: attN = top,...|0.920000017|0 and bottom |-0.920000017|0
    "Rockomax16.BW":           {"top": 0.92, "bottom": -0.92},
    # Kerbal X: attN = top,...|0|0 (origin AT the top node) and bottom |-1.5|0
    "liquidEngine2-2.v2":      {"top": 0.0, "bottom": -1.5},
    # Kerbal X: attN = top,...|0.100000001|0 and bottom |-0.100000001|0
    "Decoupler.2":             {"top": 0.1, "bottom": -0.1},
    # Duna Rocket: attN = top,...|3.73000002|0, bottom |-3.73000002|0 (GDLV3: 3.73)
    "Rockomax64.BW":           {"top": 3.73, "bottom": -3.73},
    # Kerbal X: attN = top,...|1.01358998|0 (default variant; nothing below it)
    "liquidEngineMainsail.v2": {"top": 1.01359},
    # THE TENDER (A12), stacked ABOVE the pod.
    # Crater Crawler: attN = top,dockingPort3_..|0.1474114|0 and bottom |0|0 (the
    # origin is AT the bottom node; the docking face is the top node)
    "dockingPort3":            {"top": 0.1474114, "bottom": 0.0},
    # c1 R2-B2-S4: attN = top,...|0.187081799|0 and bottom |-0.187081799|0
    "probeCoreOcto.v2":        {"top": 0.1870818, "bottom": -0.1870818},
    # c1 R2-B2-S4: attN = top,...|0.100000001|0 and bottom |-0.100000001|0
    "batteryBankMini":         {"top": 0.1, "bottom": -0.1},
}

# A stack part flipped end for end (rot 180 deg about X, written by KSP as 1,0,0,0:
# Crater Crawler's upper dockingPort3 of its top-to-top pair). Its local top node
# then points DOWN, so attached by its top it sits ABOVE the part below it.
ROT_FLIPPED = "1,0,0,0"

# COPIED RELATIVE TRANSFORMS (radial kind (a)): parent-relative attPos0 and rot,
# verbatim from stock VAB/Science Jr.craft, whose mk1pod.v2 and science.module both
# carry rot 0,0,0,1 exactly as ours do.
COPIED_RADIALS = {
    # SurfAntenna_4293783042 on mk1pod.v2_4294122274, from c1's
    # Ships/VAB/L5-B4-U1-C1.craft (KSP 1.12.5), NOT Science Jr.craft: rot verbatim,
    # offset = pos(antenna) - pos(pod) = (0.00810668711 - 0.00810668711,
    # 15.7130451 - 15.5617275, 0.377105951 - -0.045910798), the donor's
    # attPos0 + attPos (module docstring, radial kind (a)).
    "ant": ("SurfAntenna", "pod",
            (0.0, 0.1513176, 0.423016749),
            "0.425192654,-0.564987779,-0.425192654,0.564987779"),
    # sensorThermometer_4294596382 on mk1pod.v2_4294611124
    "thermo": ("sensorThermometer", "pod",
               (0.305041492, 0.495138168, -0.176115841),
               "-0.121066101,0.495090187,-0.0698975325,-0.857521474"),
    # sensorBarometer_4294606568 on mk1pod.v2_4294611124
    "baro": ("sensorBarometer", "pod",
             (-0.305362612, 0.503364563, -0.176301226),
             "-0.121066146,-0.495090157,0.0698975548,-0.857521474"),
    # GooExperiment_4293903456 on science.module_4293904034
    "goo": ("GooExperiment", "sci",
            (0.529656887, 0.111205101, 8.56816769e-08),
            "0,0.707106471,0,-0.707107186"),
}

# AZIMUTH-RING parts (radial kind (b)): the part's own rotation as KSP wrote it at
# azimuth 0, and the measured radius on the 2.5 m X200-16 (assumption A6).
RTG_ATTACH_NODE_DEPTH = 0.3268025          # RTG.cfg node_attach y, rescaleFactor 1
BATTERY_RADIUS = 1.20783                   # Kerbal X, Z-400 on this X200-16
RTG_RADIUS = BATTERY_RADIUS + RTG_ATTACH_NODE_DEPTH
Q_REF = {
    # Kerbal X ksp.r.largeBatteryPack at az 0: 0,0.707106829,0,-0.707106709 = -Ry(-90)
    "ksp.r.largeBatteryPack": None,        # computed as Ry(-90), see ring_rotation
    # Ion-Powered Space Probe rtg_4294504102 at az 0
    "rtg": (0.5, 0.5, -0.5, 0.5),
}
BATTERY_AZIMUTHS = (0.0, 90.0, 180.0, 270.0)
BATTERY_DY = 0.45
RTG_AZIMUTHS = tuple(22.5 + 45.0 * i for i in range(8))
RTG_DY = -0.35

# Deterministic part ids. KSP parses the `part = <name>_<uid>` suffix and every
# persistentId with System.UInt32.Parse (DD1's first forge died on a 43xxxxxxxx id),
# so all of them stay under 4,294,967,295.
_KEYS = (["pod", "thermo", "baro", "sci", "goo", "lab", "wheel", "x16"]
         + ["batt%d" % i for i in range(len(BATTERY_AZIMUTHS))]
         + ["rtg%d" % i for i in range(len(RTG_AZIMUTHS))]
         + ["poodle", "dec", "x64", "mainsail"])
# Parts added after the first flown shape, APPENDED (never folded into the sorted
# enumeration) so every part of that shape keeps its uid and persistentId.
_ADDED_KEYS = ["ant", "dockA", "dockB", "okto", "z200"]
_PART_UID = {k: 4260000001 + i for i, k in enumerate(_KEYS + _ADDED_KEYS)}
_PERSISTENT_ID = {k: 1700000000 + i * 7717
                  for i, k in enumerate(sorted(_KEYS) + _ADDED_KEYS)}
SHIP_PERSISTENT_ID = 2200110055
assert max(_PART_UID.values()) <= 0xFFFFFFFF, "part uid exceeds UInt32"
assert max(_PERSISTENT_ID.values()) <= 0xFFFFFFFF, "persistentId exceeds UInt32"
assert SHIP_PERSISTENT_ID <= 0xFFFFFFFF, "ship persistentId exceeds UInt32"
assert len(set(_PART_UID.values())) == len(_PART_UID), "duplicate part uid"
assert len(set(_PERSISTENT_ID.values())) == len(_PERSISTENT_ID), "duplicate persistentId"

# THE STAGING TABLE (assumption A3). KSP fires stages in DESCENDING istg order.
STAGE_IGNITE = 1
STAGE_SEPARATE = 0

# Crew capacities (part cfgs) and the command-part set, for the seating contract
# (assumption A2): the ONLY control source with seats must have exactly ONE seat
# and come first, and the lab must be the only other crewable part.
CREW_CAPACITY = {"mk1pod.v2": 1, "Large.Crewed.Lab": 2}
CREW_NAMES = ("Valentina Kerman", "Bob Kerman")

# Every ModuleCommand part and its seats (part cfg CrewCapacity). The tender's OKTO
# is a control source with NO seat (probeCoreOcto_v2.cfg CrewCapacity 0, minimumCrew
# 0), so DefaultCrewForVessel has nothing to fill there and kRPC's leftover fill
# finds no empty seat in it: the A2 seating is unchanged (A12).
COMMAND_SEATS = {"mk1pod.v2": 1, "probeCoreOcto.v2": 0}

# The tender (A12), bottom to top above the pod. Never staged.
TENDER_PARTS = ("dockingPort3", "probeCoreOcto.v2", "batteryBankMini")

# The antennaType of every part aboard that carries a ModuleDataTransmitter. The
# value lives in the part cfg, not in the craft's MODULE node, so it is pinned here
# (assumption A9). INTERNAL cannot send science (CanTransmit() is false), so
# verify() requires at least one DIRECT or RELAY entry aboard that never stages.
TRANSMITTER_TYPE = {
    "mk1pod.v2": "INTERNAL",            # mk1pod_v2.cfg ModuleDataTransmitter
    "SurfAntenna": "DIRECT",            # C16S.cfg, antennaPower 500000
    "probeCoreOcto.v2": "INTERNAL",     # probeCoreOcto_v2.cfg, antennaPower 5000
}
SCIENCE_LINK_TYPES = ("DIRECT", "RELAY")

# ---------------------------------------------------------------------------
# Performance arithmetic (assumption A7). Masses / Isp / thrust from the KSP 1.12.5
# part cfgs; propellant masses from the donor tails' full loads (LF and Ox 0.005 t
# per unit, MonoPropellant 0.004 t).
# ---------------------------------------------------------------------------
G0 = 9.80665
PART_WET_MASS = {
    "mk1pod.v2": 0.8 + 10 * 0.004,
    "sensorThermometer": 0.005,
    "sensorBarometer": 0.005,
    "SurfAntenna": 0.015,
    "dockingPort3": 0.02,
    "probeCoreOcto.v2": 0.1,
    "batteryBankMini": 0.01,
    "science.module": 0.2,
    "GooExperiment": 0.05,
    "Large.Crewed.Lab": 3.5,
    "asasmodule1-2": 0.2,
    "Rockomax16.BW": 1.0 + (720 + 880) * 0.005,
    "ksp.r.largeBatteryPack": 0.02,
    "rtg": 0.08,
    "liquidEngine2-2.v2": 1.75,
    "Decoupler.2": 0.16,
    "Rockomax64.BW": 4.0 + (2880 + 3520) * 0.005,
    "liquidEngineMainsail.v2": 6.0,
}
PROPELLANT_MASS = {"Rockomax16.BW": 8.0, "Rockomax64.BW": 32.0}
POODLE = {"thrust_vac": 250.0, "isp_vac": 350.0}
MAINSAIL = {"thrust_vac": 1500.0, "isp_vac": 310.0, "isp_asl": 285.0}
RTG_EC_PER_SECOND = 0.75
LAB_RESEARCH_EC_PER_SECOND = 5.0

# Floors verify() holds the arithmetic to (task brief: TWR > 1.3, ~3,400 m/s+ vac).
MIN_PAD_TWR = 1.3
MIN_TOTAL_VAC_DV = 3400.0


def _fmt(v: float) -> str:
    """Format a coordinate the way KSP writes one: shortest round-trippable form,
    no exponent for the magnitudes this craft uses."""
    if v == 0:
        return "0"
    s = repr(round(v, 9))
    if "e" in s or "E" in s:
        s = ("%.9f" % v).rstrip("0").rstrip(".")
    if s.endswith(".0"):
        s = s[:-2]
    if s == "-0":
        return "0"
    return s


def _vec(x: float, y: float, z: float) -> str:
    return "%s,%s,%s" % (_fmt(x), _fmt(y), _fmt(z))


def _qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def _ry(deg: float):
    h = math.radians(deg) / 2.0
    return (0.0, math.sin(h), 0.0, math.cos(h))


def _fmt_quat(q) -> str:
    return ",".join(_fmt(round(c, 9)) for c in q)


def ring_rotation(part_name: str, azimuth_deg: float) -> str:
    """q(az) = Ry(-az) * q_ref (module docstring, radial kind (b))."""
    q_ref = Q_REF[part_name]
    if q_ref is None:
        q_ref = _ry(-90.0)
    return _fmt_quat(_qmul(_ry(-azimuth_deg), q_ref))


def ring_position(center, azimuth_deg: float, dy: float, radius: float):
    a = math.radians(azimuth_deg)
    return (center[0] + radius * math.cos(a), center[1] + dy,
            center[2] + radius * math.sin(a))


class Part(object):
    """One authored PART: identity, geometry, staging, and links."""

    def __init__(self, key, name, pos, rot="0,0,0,1", istg=-1, dstg=0, sidx=-1,
                 sqor=-1, attm=0, parent=None):
        self.key = key
        self.name = name
        self.pos = pos
        self.rot = rot
        self.istg = istg
        self.dstg = dstg
        self.sidx = sidx
        self.sqor = sqor
        self.attm = attm
        self.parent = parent
        self.links = []
        self.syms = []
        self.attn = []
        self.srfn = None

    @property
    def uid(self):
        return "%s_%d" % (self.name, _PART_UID[self.key])


def _stack_down(parent: Part, key: str, name: str, **kw) -> Part:
    """Place ``name`` below ``parent`` on the stack axis and wire both attNs."""
    y = (parent.pos[1] + NODE_OFFSET[parent.name]["bottom"]
         - NODE_OFFSET[name]["top"])
    child = Part(key, name, (0.0, y, 0.0), parent=parent, **kw)
    parent.links.append(child.uid)
    parent.attn.append(("bottom", child.uid, NODE_OFFSET[parent.name]["bottom"]))
    child.attn.append(("top", parent.uid, NODE_OFFSET[name]["top"]))
    return child


def _stack_up(parent: Part, key: str, name: str, flipped: bool = False,
              **kw) -> Part:
    """Place ``name`` ABOVE ``parent`` on the stack axis, on the parent's TOP node.

    Upright child: its bottom node meets the parent's top node, so
        child.y = parent.top_world - child.bottom.
    Flipped child (ROT_FLIPPED): its own TOP node points down and meets the parent's
    top node (a docking pair joins face to face), so
        child.y = parent.top_world + child.top.
    ``parent.top_world`` is the parent's top node in world space: parent.y + top for
    an upright parent, parent.y - bottom for a flipped one (its bottom node points
    up). attN tokens stay in each part's LOCAL frame, as KSP writes them (Crater
    Crawler's flipped port carries `top,...|0.1474114|0` like its upright twin).
    """
    if parent.rot == ROT_FLIPPED:
        parent_node, anchor = "bottom", parent.pos[1] - NODE_OFFSET[parent.name]["bottom"]
    else:
        parent_node, anchor = "top", parent.pos[1] + NODE_OFFSET[parent.name]["top"]
    if flipped:
        child_node = "top"
        y = anchor + NODE_OFFSET[name]["top"]
        child = Part(key, name, (0.0, y, 0.0), rot=ROT_FLIPPED, parent=parent, **kw)
    else:
        child_node = "bottom"
        y = anchor - NODE_OFFSET[name]["bottom"]
        child = Part(key, name, (0.0, y, 0.0), parent=parent, **kw)
    parent.links.append(child.uid)
    parent.attn.append((parent_node, child.uid, NODE_OFFSET[parent.name][parent_node]))
    child.attn.append((child_node, parent.uid, NODE_OFFSET[name][child_node]))
    return child


def _surface(parent: Part, key: str, name: str, pos, rot: str, **kw) -> Part:
    p = Part(key, name, pos, rot=rot, attm=1, parent=parent, **kw)
    p.srfn = parent.uid
    parent.links.append(p.uid)
    return p


def layout():
    """The full ordered part list, every position derived. Element 0 is the root
    (KSP takes the first PART as the root) and PART order is also kRPC's seat-fill
    order (assumption A2), so the pod precedes the lab."""
    pod = Part("pod", "mk1pod.v2", (0.0, ROOT_Y, 0.0))

    copied = {}
    for key, (name, _parent_key, rel, rot) in sorted(COPIED_RADIALS.items()):
        copied[key] = (name, rel, rot)

    def copied_radial(key, parent):
        name, rel, rot = copied[key]
        pos = (parent.pos[0] + rel[0], parent.pos[1] + rel[1], parent.pos[2] + rel[2])
        return _surface(parent, key, name, pos, rot)

    thermo = copied_radial("thermo", pod)
    baro = copied_radial("baro", pod)
    ant = copied_radial("ant", pod)
    # THE TENDER (A12): pod top -> Jr port (upright) -> Jr port (flipped, face to
    # face) -> OKTO -> Z-200. Never staged; separation is an explicit undock.
    dock_a = _stack_up(pod, "dockA", "dockingPort3")
    dock_b = _stack_up(dock_a, "dockB", "dockingPort3", flipped=True)
    okto = _stack_up(dock_b, "okto", "probeCoreOcto.v2")
    z200 = _stack_up(okto, "z200", "batteryBankMini")
    sci = _stack_down(pod, "sci", "science.module")
    goo = copied_radial("goo", sci)
    lab = _stack_down(sci, "lab", "Large.Crewed.Lab")
    wheel = _stack_down(lab, "wheel", "asasmodule1-2")
    x16 = _stack_down(wheel, "x16", "Rockomax16.BW")

    batts = []
    for i, az in enumerate(BATTERY_AZIMUTHS):
        batts.append(_surface(
            x16, "batt%d" % i, "ksp.r.largeBatteryPack",
            ring_position(x16.pos, az, BATTERY_DY, BATTERY_RADIUS),
            ring_rotation("ksp.r.largeBatteryPack", az)))
    rtgs = []
    for i, az in enumerate(RTG_AZIMUTHS):
        rtgs.append(_surface(
            x16, "rtg%d" % i, "rtg",
            ring_position(x16.pos, az, RTG_DY, RTG_RADIUS),
            ring_rotation("rtg", az)))
    for group in (batts, rtgs):
        for p in group:
            p.syms = [o.uid for o in group if o is not p]

    poodle = _stack_down(x16, "poodle", "liquidEngine2-2.v2",
                         istg=STAGE_SEPARATE, dstg=1, sidx=1, sqor=STAGE_SEPARATE)
    dec = _stack_down(poodle, "dec", "Decoupler.2",
                      istg=STAGE_SEPARATE, dstg=2, sidx=0, sqor=STAGE_SEPARATE)
    x64 = _stack_down(dec, "x64", "Rockomax64.BW", istg=STAGE_SEPARATE, dstg=3)
    mainsail = _stack_down(x64, "mainsail", "liquidEngineMainsail.v2",
                           istg=STAGE_IGNITE, dstg=3, sidx=0, sqor=STAGE_IGNITE)

    return ([pod, thermo, baro, ant, dock_a, dock_b, okto, z200, sci, goo, lab, wheel,
             x16] + batts + rtgs
            + [poodle, dec, x64, mainsail])


def render_part(part: Part) -> str:
    # attPos0: the offset from the PARENT (KSP's convention); the root's own pos.
    if part.parent is None:
        rel = part.pos
    else:
        rel = tuple(part.pos[i] - part.parent.pos[i] for i in range(3))
    out = ["PART", "{"]
    out.append("\tpart = %s" % part.uid)
    out.append("\tpartName = Part")
    out.append("\tpersistentId = %d" % _PERSISTENT_ID[part.key])
    out.append("\tpos = %s" % _vec(*part.pos))
    out.append("\tattPos = 0,0,0")
    out.append("\tattPos0 = %s" % _vec(*rel))
    out.append("\trot = %s" % part.rot)
    out.append("\tattRot = 0,0,0,1")
    # attRot0 = the part's own rot: a surface part's attach rotation, a flipped stack
    # part's 1,0,0,0 (Crater Crawler's flipped port writes exactly that), identity
    # for every upright stack part.
    out.append("\tattRot0 = %s" % part.rot)
    out.append("\tmir = 1,1,1")
    out.append("\tsymMethod = Radial")
    out.append("\tautostrutMode = Off")
    out.append("\trigidAttachment = False")
    out.append("\tistg = %d" % part.istg)
    out.append("\tresPri = 0")
    out.append("\tdstg = %d" % part.dstg)
    out.append("\tsidx = %d" % part.sidx)
    out.append("\tsqor = %d" % part.sqor)
    out.append("\tsepI = -1")
    out.append("\tattm = %d" % part.attm)
    out.append("\tmodCost = 0")
    out.append("\tmodMass = 0")
    out.append("\tmodSize = 0,0,0")
    for link in part.links:
        out.append("\tlink = %s" % link)
    for sym in part.syms:
        out.append("\tsym = %s" % sym)
    for node_id, other, dy in sorted(part.attn):
        out.append("\tattN = %s,%s_0|%s|0" % (node_id, other, _fmt(dy)))
    if part.srfn:
        out.append("\tsrfN = srfAttach,%s" % part.srfn)
    out.append(TAILS[part.name])
    out.append("}")
    return "\n".join(out)


def build():
    """Render the whole .craft as a list of lines."""
    parts = layout()
    xs = [p.pos[0] for p in parts]
    ys = [p.pos[1] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % SHIP_NAME,
        "version = 1.12.5",
        "description = LABX Orbital Lab. Crewed Mobile Processing Lab for low Kerbin "
        "orbit: one-seat Mk1 pod (pilot) over an SC-9001 Science Jr, the MPL-LG-2 "
        "lab (the second named crew, the scientist, seats here), an RW-L reaction "
        "wheel, and an X200-16 / Poodle orbital stage carrying eight RTGs and four "
        "Z-400s; Goo, 2HOT and PresMat aboard; a Communotron 16-S on the pod sends "
        "science home; a probe tender (OKTO + Z-200) rides above the pod on a "
        "Clamp-O-Tron Jr pair and leaves only by an explicit undock. Jumbo-64 / "
        "Mainsail booster, hot-staged. No deployables, no action groups. Built by "
        "construction by "
        "harness/tools/build_lab_orbit_craft.py.",
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 2.5, max(ys) - min(ys) + 2.0,
                           max(zs) - min(zs) + 2.5),
        "steamPublishedFileId = 0",
        "persistentId = %d" % SHIP_PERSISTENT_ID,
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    body = [render_part(p) for p in parts]
    return ("\n".join(header) + "\n" + "\n".join(body) + "\n").split("\n")


def performance():
    """The A7 arithmetic, from PART_WET_MASS over the layout's part census."""
    parts = layout()
    upper = [p for p in parts if p.istg == -1 or p.name == "liquidEngine2-2.v2"]
    booster = [p for p in parts if p not in upper]
    m_upper = sum(PART_WET_MASS[p.name] for p in upper)
    m_upper_dry = m_upper - PROPELLANT_MASS["Rockomax16.BW"]
    m_booster = sum(PART_WET_MASS[p.name] for p in booster)
    m_pad = m_upper + m_booster
    m_burnout = m_pad - PROPELLANT_MASS["Rockomax64.BW"]
    asl_thrust = MAINSAIL["thrust_vac"] * MAINSAIL["isp_asl"] / MAINSAIL["isp_vac"]
    dv_upper = POODLE["isp_vac"] * G0 * math.log(m_upper / m_upper_dry)
    dv_booster_vac = MAINSAIL["isp_vac"] * G0 * math.log(m_pad / m_burnout)
    dv_booster_asl = MAINSAIL["isp_asl"] * G0 * math.log(m_pad / m_burnout)
    ec = sum(1 for p in parts if p.name == "rtg") * RTG_EC_PER_SECOND
    return {
        "upperWet": m_upper, "upperDry": m_upper_dry, "padMass": m_pad,
        "boosterBurnoutMass": m_burnout,
        "padTwr": asl_thrust / (m_pad * G0),
        "upperVacTwr": POODLE["thrust_vac"] / (m_upper * G0),
        "upperVacDv": dv_upper, "boosterVacDv": dv_booster_vac,
        "boosterAslDv": dv_booster_asl, "totalVacDv": dv_upper + dv_booster_vac,
        "ecPerSecond": ec,
    }


# ---------------------------------------------------------------------------
# Post-conditions. `verify` is the `--check` path and runs in-process in the unit
# suite, so a hand-edited craft reds there rather than in a flight.
# ---------------------------------------------------------------------------

EXPECTED_PARTS = {
    "mk1pod.v2": 1, "sensorThermometer": 1, "sensorBarometer": 1,
    "SurfAntenna": 1, "dockingPort3": 2, "probeCoreOcto.v2": 1, "batteryBankMini": 1,
    "science.module": 1, "GooExperiment": 1, "Large.Crewed.Lab": 1,
    "asasmodule1-2": 1, "Rockomax16.BW": 1, "ksp.r.largeBatteryPack": 4,
    "rtg": 8, "liquidEngine2-2.v2": 1, "Decoupler.2": 1, "Rockomax64.BW": 1,
    "liquidEngineMainsail.v2": 1,
}


def read_lines(path: str):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


def part_records(lines):
    """[(partName, {key: value})] for every PART, scalar header fields only, plus
    the raw body text under the key ``__body__``."""
    records = []
    current = None
    body = None
    depth = 0
    for line in lines:
        s = line.strip()
        if s == "PART" and current is None:
            current = {}
            body = []
            depth = 0
            continue
        if current is None:
            continue
        body.append(line)
        if s == "{":
            depth += 1
            continue
        if s == "}":
            depth -= 1
            if depth == 0:
                current["__body__"] = "\n".join(body)
                records.append(current)
                current = None
            continue
        if depth == 1 and " = " in s:
            k, v = s.split(" = ", 1)
            if k in ("link", "sym", "attN"):
                current.setdefault(k, []).append(v)
            else:
                current[k] = v
    return [(r.get("part", "?").rsplit("_", 1)[0], r) for r in records]


def verify(lines):
    """Every post-condition the craft must satisfy. Returns a list of problems
    (empty = good). Pure text, no KSP."""
    problems = []
    declared = set()
    for ln in lines:
        m = re.match(r"\s*part = ([^_\s]+_\d+)\s*$", ln)
        if m:
            declared.add(m.group(1))
    for ln in lines:
        m = re.match(r"\s*(link|sym) = ([^_\s]+_\d+)\s*$", ln)
        if m and m.group(2) not in declared:
            problems.append("%s references undeclared part %s"
                            % (m.group(1), m.group(2)))
        m = re.match(r"\s*(attN|srfN) = [^,]+,([^_\s|]+_\d+)", ln)
        if m and m.group(2) not in declared:
            problems.append("%s references undeclared part %s"
                            % (m.group(1), m.group(2)))
        m = re.match(r"\s*(?:part = [^_\s]+_|persistentId = )(\d+)\s*$", ln)
        if m and int(m.group(1)) > 0xFFFFFFFF:
            problems.append("id %s exceeds UInt32 (KSP parses it with "
                            "UInt32.Parse)" % m.group(1))
    text = "\n".join(lines)
    if not lines or lines[0] != "ship = %s" % SHIP_NAME:
        problems.append("first line must be `ship = %s`" % SHIP_NAME)
    if "version = 1.12.5" not in text:
        problems.append("craft must declare version = 1.12.5 (an older version "
                        "raises the compat dialog no unattended run can click)")
    if "type = VAB" not in text:
        problems.append("craft must be a VAB craft (type = VAB)")
    if any(ord(c) > 127 for c in text):
        problems.append("craft must be plain ASCII")

    records = part_records(lines)
    counts = {}
    for name, _rec in records:
        counts[name] = counts.get(name, 0) + 1
    if counts != EXPECTED_PARTS:
        problems.append("part census %r != expected %r" % (counts, EXPECTED_PARTS))
    by_name = {}
    for name, rec in records:
        by_name.setdefault(name, []).append(rec)

    # THE SEATING CONTRACT (assumption A2).
    if not records or records[0][0] != "mk1pod.v2":
        problems.append("the ROOT (first PART) must be the one-seat mk1pod.v2: it "
                        "is the root, the only seated control source, and the "
                        "first part kRPC fills")
    crewable = [name for name, _rec in records if name in CREW_CAPACITY]
    if crewable != ["mk1pod.v2", "Large.Crewed.Lab"]:
        problems.append("crewable parts in PART order must be exactly "
                        "[mk1pod.v2, Large.Crewed.Lab] so the second named crew "
                        "lands in the lab; got %r" % crewable)
    if CREW_CAPACITY.get("mk1pod.v2") != 1:
        problems.append("the pod must have exactly ONE seat, or it absorbs the "
                        "scientist")
    if sum(CREW_CAPACITY.values()) < len(CREW_NAMES):
        problems.append("not enough seats for the named crew")
    commands = [name for name, rec in records
                if "name = ModuleCommand" in rec["__body__"]]
    if sorted(commands) != sorted(COMMAND_SEATS):
        problems.append("ModuleCommand parts %r != expected %r (the pod and the "
                        "tender's probe core)" % (commands, sorted(COMMAND_SEATS)))
    seated_commands = [n for n in commands if COMMAND_SEATS.get(n, 0) > 0]
    if seated_commands != ["mk1pod.v2"]:
        problems.append("the pod must be the ONLY command part with seats, got %r: "
                        "a second seated control source changes the A2 fill"
                        % seated_commands)
    for name in commands:
        if COMMAND_SEATS.get(name, 0) != CREW_CAPACITY.get(name, 0):
            problems.append("COMMAND_SEATS / CREW_CAPACITY disagree on %s" % name)

    # THE TENDER (A12): two Jr ports joined face to face between the pod and the
    # probe core, neither one stageable, and every tender part never staged.
    ports = by_name.get("dockingPort3", [])
    uids = [r.get("part") for r in ports]
    if len(ports) == 2:
        for rec, other in ((ports[0], uids[1]), (ports[1], uids[0])):
            if not any(a.startswith("top,%s_0|" % other) for a in rec.get("attN", [])):
                problems.append("dockingPort3 %s must join its twin face to face "
                                "(attN top -> %s)" % (rec.get("part"), other))
    for name, rec in records:
        body = rec["__body__"]
        if "name = ModuleDockingNode" in body and "stagingEnabled = False" not in body:
            problems.append("%s: a ModuleDockingNode must carry stagingEnabled = False "
                            "(a stageable port is a stage MechJeb autostage can fire)"
                            % name)
    for name in TENDER_PARTS:
        stages = sorted({int(r["istg"]) for r in by_name.get(name, [])})
        if stages != [-1]:
            problems.append("tender part %s must be aboard and never stage, got istg "
                            "%r" % (name, stages))

    # THE LAB TAIL (assumption A4): authored, and it invents no module data.
    for rec in by_name.get("Large.Crewed.Lab", []):
        if "\tMODULE" in rec["__body__"] or "\tRESOURCE" in rec["__body__"]:
            problems.append("the lab tail must carry NO MODULE / RESOURCE block "
                            "(no donor exists; the prefab supplies every module)")

    def istg_of(name):
        return sorted({int(r["istg"]) for r in by_name.get(name, [])})

    # THE STAGING CONTRACT (assumption A3).
    if istg_of("liquidEngineMainsail.v2") != [STAGE_IGNITE]:
        problems.append("the Mainsail must ignite in istg %d, got %r"
                        % (STAGE_IGNITE, istg_of("liquidEngineMainsail.v2")))
    if istg_of("Decoupler.2") != [STAGE_SEPARATE]:
        problems.append("the TD-25 must fire in istg %d, got %r"
                        % (STAGE_SEPARATE, istg_of("Decoupler.2")))
    if istg_of("liquidEngine2-2.v2") != [STAGE_SEPARATE]:
        problems.append("the Poodle must hot-stage in the SAME istg as the TD-25 "
                        "(%d), got %r" % (STAGE_SEPARATE,
                                          istg_of("liquidEngine2-2.v2")))
    staged = {"liquidEngineMainsail.v2", "Decoupler.2", "liquidEngine2-2.v2",
              "Rockomax64.BW"}
    for name, rec in records:
        if name not in staged and int(rec["istg"]) != -1:
            problems.append("%s sits above the decoupler and must never stage "
                            "(istg -1), got %s" % (name, rec["istg"]))
    if max((int(r["istg"]) for _n, r in records), default=-1) != STAGE_IGNITE:
        problems.append("no part may stage above istg %d" % STAGE_IGNITE)

    # NO ACTION-GROUP BINDINGS beyond the pod's stock ModuleColorChanger Light.
    bound = [l.strip() for l in lines if l.strip().startswith("actionGroup = ")
             and l.strip() != "actionGroup = None"]
    if bound != ["actionGroup = Light"]:
        problems.append("action-group bindings %r; expected only the mk1 pod's "
                        "stock ModuleColorChanger Light" % bound)

    # FULL PROPELLANT LOADS at stock values (A7).
    for token, label in (("\t\tamount = 720", "X200-16 LiquidFuel 720"),
                         ("\t\tamount = 880", "X200-16 Oxidizer 880"),
                         ("\t\tamount = 2880", "Jumbo-64 LiquidFuel 2880"),
                         ("\t\tamount = 3520", "Jumbo-64 Oxidizer 3520")):
        if token not in text:
            problems.append("missing full propellant load: %s" % label)
    for engine in ("liquidEngine2-2.v2", "liquidEngineMainsail.v2"):
        for rec in by_name.get(engine, []):
            body = rec["__body__"]
            if ("thrustPercentage = 100" not in body
                    or "EngineIgnited = False" not in body):
                problems.append("%s must carry stock thrustPercentage 100 and an "
                                "unignited engine" % engine)

    # NO DEPLOYABLES (A8 / A9).
    for module in ("ModuleDeployableSolarPanel", "ModuleDeployableAntenna",
                   "ModuleDeployableRadiator"):
        if module in text:
            problems.append("a deployable crept in (%s); the craft must need no "
                            "click to open" % module)

    # THE SCIENCE LINK (A9): at least one DIRECT / RELAY transmitter aboard, on a
    # part that never stages; every transmitter-carrying part has a pinned type.
    science_links = []
    for name, rec in records:
        if "name = ModuleDataTransmitter" not in rec["__body__"]:
            continue
        kind = TRANSMITTER_TYPE.get(name)
        if kind is None:
            problems.append("%s carries a ModuleDataTransmitter with no pinned "
                            "antennaType in TRANSMITTER_TYPE" % name)
        elif kind in SCIENCE_LINK_TYPES:
            science_links.append((name, rec))
    if not science_links:
        problems.append("no DIRECT or RELAY ModuleDataTransmitter aboard: the pod's "
                        "INTERNAL antenna cannot transmit science (A9)")
    for name, rec in science_links:
        if int(rec["istg"]) != -1:
            problems.append("the science-link antenna %s must never stage (istg -1), "
                            "got %s" % (name, rec["istg"]))

    # Radial hosts.
    for name, host in (("sensorThermometer", "mk1pod.v2"),
                       ("sensorBarometer", "mk1pod.v2"),
                       ("SurfAntenna", "mk1pod.v2"),
                       ("GooExperiment", "science.module"),
                       ("ksp.r.largeBatteryPack", "Rockomax16.BW"),
                       ("rtg", "Rockomax16.BW")):
        for rec in by_name.get(name, []):
            if not rec.get("srfN", "").startswith("srfAttach,%s_" % host):
                problems.append("%s must be surface-attached to the %s, got %r"
                                % (name, host, rec.get("srfN")))

    # The A7 / A8 arithmetic floors.
    perf = performance()
    if perf["padTwr"] <= MIN_PAD_TWR:
        problems.append("pad TWR %.2f is not above %.2f"
                        % (perf["padTwr"], MIN_PAD_TWR))
    if perf["totalVacDv"] < MIN_TOTAL_VAC_DV:
        problems.append("total vacuum dv %.0f is below %.0f"
                        % (perf["totalVacDv"], MIN_TOTAL_VAC_DV))
    if perf["ecPerSecond"] < LAB_RESEARCH_EC_PER_SECOND:
        problems.append("RTG output %.2f EC/s is below the lab's %.1f EC/s "
                        "research draw" % (perf["ecPerSecond"],
                                           LAB_RESEARCH_EC_PER_SECOND))
    return problems


def main(argv=None) -> int:
    p = argparse.ArgumentParser(
        prog="build_lab_orbit_craft",
        description="Build or verify the committed LABX Orbital Lab .craft.")
    p.add_argument("--check", action="store_true",
                   help="verify the COMMITTED craft against every post-condition "
                        "AND against a fresh rebuild; writes nothing")
    p.add_argument("--write", action="store_true",
                   help="regenerate the committed craft file")
    p.add_argument("--report", action="store_true",
                   help="print the A7 mass / dv / TWR arithmetic")
    args = p.parse_args(argv)
    if not (args.check or args.write or args.report):
        p.error("pass --check, --write or --report")

    if args.report:
        for k, v in sorted(performance().items()):
            sys.stdout.write("[LabxCraft] %s = %.3f\n" % (k, v))
        if not (args.check or args.write):
            return 0

    built = build()
    if args.write:
        os.makedirs(os.path.dirname(CRAFT_PATH), exist_ok=True)
        # CRLF, matching what KSP itself writes on Windows; read_lines normalizes,
        # so the drift comparison is line-based either way.
        with open(CRAFT_PATH, "w", encoding="utf-8", newline="\r\n") as fh:
            fh.write("\n".join(built))
        sys.stdout.write("[LabxCraft] wrote %s (%d lines)\n"
                         % (CRAFT_PATH, len(built)))

    problems = verify(built)
    if os.path.isfile(CRAFT_PATH):
        committed = read_lines(CRAFT_PATH)
        problems.extend(verify(committed))
        if committed != built:
            problems.append("the committed craft at %s has DRIFTED from what this "
                            "script produces; re-run with --write and commit, or "
                            "explain the divergence" % CRAFT_PATH)
    else:
        problems.append("committed craft not found at %s" % CRAFT_PATH)

    for problem in problems:
        sys.stdout.write("[LabxCraft] PROBLEM: %s\n" % problem)
    if problems:
        return 1
    sys.stdout.write("[LabxCraft] OK: %s satisfies every post-condition\n"
                     % CRAFT_PATH)
    return 0


# ---------------------------------------------------------------------------
# PART TAILS. Each entry but the lab's is the EVENTS-onward span of one PART node,
# lifted byte-for-byte out of a craft KSP itself wrote (the source is named above
# it; "c1" is the operator's dev-install career, whose subassembly KSP 1.12.5
# wrote, chosen because its four science-part tails carry the stock cfg's exact
# module list including ModuleCargoPart). Nothing here is rewritten.
# ---------------------------------------------------------------------------
TAILS = {
    # AUTHORED (assumption A4): no KSP-written lab PART exists to copy, so the tail
    # carries only the three empty part-level blocks every PART has, and NO MODULE
    # block; the prefab supplies every module at its cfg default.
    'Large.Crewed.Lab': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}',
    # verbatim from stock VAB/Jumping Flea.craft (KSP 1.12.0)
    'mk1pod.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCommand\n\t\tisEnabled = True\n\t\thibernation = False\n\t\thibernateOnWarp = False\n\t\tactiveControlPointName = _default\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tHibernateToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleReactionWheel\n\t\tisEnabled = True\n\t\tactuatorModeCycle = 0\n\t\tauthorityLimiter = 100\n\t\tstateString = Active\n\t\tstagingEnabled = True\n\t\tWheelState = Active\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCycleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDeactivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleColorChanger\n\t\tisEnabled = True\n\t\tanimState = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = Light\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceContainer\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCollectAllAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleConductionMultiplier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleLiftingSurface\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FlagDecal\n\t\tisEnabled = True\n\t\tflagDisplayed = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 50\n\t\tmaxAmount = 50\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = MonoPropellant\n\t\tamount = 10\n\t\tmaxAmount = 10\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from Kerbal Space Program/saves/c1/Ships/VAB/L5-B4-U1-C1.craft (KSP 1.12.5)
    'SurfAntenna': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tAutostrutOff\n\t\t{\n\t\t\tactionGroup = None\n\t\t\tactive = False\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tAutostrutRoot\n\t\t{\n\t\t\tactionGroup = None\n\t\t\tactive = False\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tAutostrutHeaviest\n\t\t{\n\t\t\tactionGroup = None\n\t\t\tactive = False\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tAutostrutGrandparent\n\t\t{\n\t\t\tactionGroup = None\n\t\t\tactive = False\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tResourcesEnableFlow\n\t\t{\n\t\t\tactionGroup = None\n\t\t\tactive = False\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tResourcesDisableFlow\n\t\t{\n\t\t\tactionGroup = None\n\t\t\tactive = False\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tpackedVolume = 5\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock SPH/Crater Crawler.craft (KSP 1.12.0); both ports of its face-to-face pair carry this same tail
    'dockingPort3': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDockingNode\n\t\tisEnabled = True\n\t\tcrossfeed = True\n\t\tstagingEnabled = False\n\t\tstate = Ready\n\t\tdockUId = 0\n\t\tdockNodeIdx = 0\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tUndockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDecoupleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tEnableXFeedAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDisableXFeedAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleXFeedAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from Kerbal Space Program/saves/c1/Ships/VAB/R2-B2-S4.craft (KSP 1.12.5)
    'probeCoreOcto.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCommand\n\t\tisEnabled = True\n\t\thibernation = False\n\t\thibernateOnWarp = False\n\t\tactiveControlPointName = _default\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tHibernateToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleReactionWheel\n\t\tisEnabled = True\n\t\tactuatorModeCycle = 0\n\t\tauthorityLimiter = 100\n\t\tstateString = Active\n\t\tstagingEnabled = True\n\t\tWheelState = Active\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCycleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDeactivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSAS\n\t\tisEnabled = True\n\t\tstandaloneToggle = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleKerbNetAccess\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOpenKerbNetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 50\n\t\tmaxAmount = 50\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from Kerbal Space Program/saves/c1/Ships/VAB/R2-B2-S4.craft (KSP 1.12.5)
    'batteryBankMini': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 200\n\t\tmaxAmount = 200\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from Kerbal Space Program/saves/c1/Subassemblies/Science Module S.craft (KSP 1.12.5)
    'science.module': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAnimateGeneric\n\t\tisEnabled = True\n\t\taniState = LOCKED\n\t\tanimSwitch = True\n\t\tanimTime = 0\n\t\tanimSpeed = 1\n\t\tdeployPercent = 100\n\t\tanimationIsDisabled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tAXISGROUPS\n\t\t{\n\t\t\tdeployPercent\n\t\t\t{\n\t\t\t\taxisGroup = None\n\t\t\t\taxisIncremental = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\taxisSpeedMultiplier = 0\n\t\t\t\taxisInverted = None\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from Kerbal Space Program/saves/c1/Subassemblies/Science Module S.craft (KSP 1.12.5)
    'GooExperiment': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAnimateGeneric\n\t\tisEnabled = True\n\t\taniState = LOCKED\n\t\tanimSwitch = True\n\t\tanimTime = 0\n\t\tanimSpeed = 1\n\t\tdeployPercent = 100\n\t\tanimationIsDisabled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tAXISGROUPS\n\t\t{\n\t\t\tdeployPercent\n\t\t\t{\n\t\t\t\taxisGroup = None\n\t\t\t\taxisIncremental = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\taxisSpeedMultiplier = 0\n\t\t\t\taxisInverted = None\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from Kerbal Space Program/saves/c1/Subassemblies/Science Module S.craft (KSP 1.12.5)
    'sensorThermometer': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEnviroSensor\n\t\tisEnabled = True\n\t\tsensorActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from Kerbal Space Program/saves/c1/Subassemblies/Science Module S.craft (KSP 1.12.5)
    'sensorBarometer': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEnviroSensor\n\t\tisEnabled = True\n\t\tsensorActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from harness/fixtures/ships/Kerbal X.craft (KSP 1.12.5)
    'asasmodule1-2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleReactionWheel\n\t\tisEnabled = True\n\t\tactuatorModeCycle = 0\n\t\tauthorityLimiter = 100\n\t\tstateString = Active\n\t\tstagingEnabled = True\n\t\tWheelState = Active\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCycleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDeactivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from harness/fixtures/ships/Kerbal X.craft (KSP 1.12.5)
    'Rockomax16.BW': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 720\n\t\tmaxAmount = 720\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 880\n\t\tmaxAmount = 880\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from harness/fixtures/ships/Kerbal X.craft (KSP 1.12.5)
    'ksp.r.largeBatteryPack': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 400\n\t\tmaxAmount = 400\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/Ion-Powered Space Probe.craft (KSP 1.6.0)
    'rtg': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGenerator\n\t\tisEnabled = True\n\t\tgeneratorIsActive = False\n\t\tthrottle = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCoreHeat\n\t\tisEnabled = True\n\t\tCoreTempGoalAdjustment = 0\n\t\tCoreThermalEnergy = 0\n\t\tstagingEnabled = True\n\t\tlastUpdateTime = 0\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from harness/fixtures/ships/Kerbal X.craft (KSP 1.12.5)
    'liquidEngine2-2.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tindependentThrottle = False\n\t\tindependentThrottlePercentage = 0\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleThrottle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tAXISGROUPS\n\t\t{\n\t\t\tindependentThrottlePercentage\n\t\t\t{\n\t\t\t\taxisGroup = None\n\t\t\t\taxisIncremental = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\taxisSpeedMultiplier = 0\n\t\t\t\taxisInverted = None\n\t\t\t\toverrideIncremental0 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental1 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental2 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental3 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t}\n\t\t\tthrustPercentage\n\t\t\t{\n\t\t\t\taxisGroup = None\n\t\t\t\taxisIncremental = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\taxisSpeedMultiplier = 0\n\t\t\t\taxisInverted = None\n\t\t\t\toverrideIncremental0 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental1 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental2 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental3 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = Shroud1\n\t\tisJettisoned = False\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleLookAtConstraint\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAlternator\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from harness/fixtures/ships/Kerbal X.craft (KSP 1.12.5)
    'Decoupler.2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDecouple\n\t\tisEnabled = True\n\t\tejectionForcePercent = 100\n\t\tisDecoupled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDecoupleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleToggleCrossfeed\n\t\tisEnabled = True\n\t\tcrossfeedStatus = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tEnableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDisableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tbeingAttached = False\n\t\tbeingSettled = False\n\t\treinitResourcesOnStoreInVessel = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from harness/fixtures/ships/Duna Rocket.craft (KSP 1.12.1)
    'Rockomax64.BW': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 2880\n\t\tmaxAmount = 2880\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 3520\n\t\tmaxAmount = 3520\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from harness/fixtures/ships/Kerbal X.craft (KSP 1.12.5)
    'liquidEngineMainsail.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tindependentThrottle = False\n\t\tindependentThrottlePercentage = 0\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleThrottle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tAXISGROUPS\n\t\t{\n\t\t\tindependentThrottlePercentage\n\t\t\t{\n\t\t\t\taxisGroup = None\n\t\t\t\taxisIncremental = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\taxisSpeedMultiplier = 0\n\t\t\t\taxisInverted = None\n\t\t\t\toverrideIncremental0 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental1 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental2 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental3 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t}\n\t\t\tthrustPercentage\n\t\t\t{\n\t\t\t\taxisGroup = None\n\t\t\t\taxisIncremental = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\taxisSpeedMultiplier = 0\n\t\t\t\taxisInverted = None\n\t\t\t\toverrideIncremental0 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental1 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental2 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t\toverrideIncremental3 = Pitch, Yaw, Roll, TranslateX, TranslateY, TranslateZ, WheelSteer, WheelThrottle, Custom01, Custom02, Custom03, Custom04\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = fairing\n\t\tisJettisoned = True\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAlternator\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleLookAtConstraint\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}

if __name__ == "__main__":
    sys.exit(main())
