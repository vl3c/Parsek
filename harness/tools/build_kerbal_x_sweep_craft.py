#!/usr/bin/env python3
"""Build the committed `Kerbal X Sweep.craft` BY CONSTRUCTION, as a DERIVATIVE.

GS-6 revision 2. The base is the committed `harness/fixtures/ships/Kerbal X.craft`
copied VERBATIM - every PART block, every scalar - so the whole `kx_rewind_watch`
staging plan (istg 6/5/4/3/2, boosterStageCount=3, istg 1/0 never pressed) is
inherited unchanged and GS-4 keeps flying the untouched original. This script only
APPENDS parts, and only to the TOP STACK (the pod side of the istg=2 decoupler),
because PART-SWEEP runs after the core discard and only that stack survives to it.

WHAT IS COPIED AND WHAT IS DERIVED, the build_gs1_craft.py contract verbatim:

  COPIED - every appended PART's tail (EVENTS / ACTIONS / PARTDATA / MODULE* /
  RESOURCE*) is lifted BYTE-FOR-BYTE out of a craft KSP itself wrote. TAILS below
  records which donor each came from. No MODULE block here is invented, so no
  module-index mismatch can be authored in.

  DERIVED - the scalar header of each appended PART (pos / rot / srfN / attN /
  istg). Radial placement uses the azimuth -> rotation formula build_gs1_craft.py
  validated against stock craft; the one stack attachment uses
  `child.pos = parent_node_world - child_opposite_node_offset` with offsets read
  off real `attN` tokens rather than computed from part cfgs.

WHY EVERY ADDED PART IS RADIAL EXCEPT THE FAIRING. The surviving top stack has
exactly ONE free stack node - `dockingPort2`'s top - and it is a Clamp-O-Tron Jr,
0.625 m. That node is spent on the fairing base. Everything else therefore had to
be surface-attachable, which is why the EFFECTS-node engine is the ANT
(`microEngine.v2`, attachRules `1,1,1,1,0`) and NOT the Spark
(`liquidEngineMini.v2`, `1,0,1,0,0` - stack only, and an engine carries only a
`top` node, so it can never hang off a node that points up). The Ant needs no
propellant either: the sweep fires it with `engines-on`
(`mlib.ACTION_SET_ENGINES_ACTIVE` -> kRPC `Engine.active`), and MODULE ACTIVATION
is what the recorder reads, so no tank was added.

FAMILIES THIS CRAFT ADDS over the committed Kerbal X, one row per Parsek
part-event family the GS-6 sweep can now fire:

  parachuteRadial  x2  ParachuteSemiDeployed / ParachuteDeployed / ParachuteCut
                       ARM ONLY on this profile - see
                       GS6-CHUTE-TWO-PHASE-NEEDS-A-DESCENT-VARIANT.
  landingLeg1      x3  GearDeployed / GearRetracted        (control.gear)
  spotLight1       x2  LightOn / LightOff                  (control.lights)
  FuelCell         x1  ConverterActivated / Deactivated    (ResourceConverter)
  microEngine.v2   x1  EngineIgnited / EngineShutdown on an EFFECTS-node engine,
                       beside the craft's seven legacy `fx_*` LV-T45s and Mainsail
  fairingSize1     x1  FairingJettisoned, istg=3 so the ascent's third
                       booster-pair drop jettisons it in-run

NO CARGO BAY. `ServiceBay.125.v2` appears in no stock craft and in no
`GameData/Squad/Ships` craft (checked), so its tail cannot be lifted, and the only
bay tails that exist are Mk2/Mk3 spaceplane fuselage sections that cannot hang off
a 0.625 m node. See GS6-CARGOBAY-NEEDS-A-HARVESTED-SERVICEBAY-TAIL.

ASSUMPTIONS A LIVE RUN CAN FALSIFY, in the order they would bite:
  A1 RADIAL RADIUS 1.25 - the X200-16 (`Rockomax16.BW`) is a 2.5 m tank, so its
     surface sits at 1.25 m. A part visibly floating off the tank or sunk inside
     it is this number, and it is COSMETIC: the events fire either way.
  A2 ROTATION - the azimuth formula orients each radial part's attach axis inward.
     Getting it wrong tilts a lamp or a leg; it does not change which family fires.
  A3 FAIRING GEOMETRY - the lifted XSECTION shell is ComSat Lx's shape, not this
     stack's, so the fairing looks wrong while being functionally a real fairing
     that jettisons. Cosmetic, and called out so nobody reads it as a defect.
  A4 istg=3 ON THE FAIRING - if the fairing jettisons at the wrong moment this is
     the number, not the staging plan (which is untouched).

Usage:
    python harness/tools/build_kerbal_x_sweep_craft.py --check
    python harness/tools/build_kerbal_x_sweep_craft.py --write
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir))
BASE = os.path.join(ROOT, "harness", "fixtures", "ships", "Kerbal X.craft")
OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "gs1-two-stage-pad",
                   "Ships", "VAB", "Kerbal X Sweep.craft")

SHIP_NAME = "Kerbal X Sweep"
RADIAL_RADIUS = 1.25
HOST = "Rockomax16.BW"
NOSE_HOST = "dockingPort2"
NOSE_HOST_TOP_OFFSET = 0.282883197
FAIRING_BOTTOM_OFFSET = -0.2
FAIRING_ISTG = 3


# Tails lifted BYTE-FOR-BYTE from the donor craft named beside each entry.
TAILS = {
    # <- Rocket-power VTOL.craft
    'parachuteRadial': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleParachute\n\t\tisEnabled = True\n\t\tpersistentState = STOWED\n\t\tanimTime = 0\n\t\tminAirPressureToOpen = 0.00999999978\n\t\tdeployAltitude = 500\n\t\tspreadAngle = 7\n\t\tautomateSafeDeploy = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = Abort\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tCutAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDragModifier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDragModifier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- PT Series Munsplorer.craft
    'landingLeg1': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelBase\n\t\tisEnabled = True\n\t\twheelType = LEG\n\t\tisGrounded = False\n\t\tautoFriction = False\n\t\tfrictionMultiplier = 1\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tActAutoFrictionToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelSuspension\n\t\tisEnabled = True\n\t\tspringTweakable = 1\n\t\tdamperTweakable = 1\n\t\tautoSpringDamper = True\n\t\tsuspensionPos = (-1, -1, -1)\n\t\tautoBoost = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelDeployment\n\t\tisEnabled = True\n\t\tshieldedCanDeploy = False\n\t\tstateDisplayString = Retracted\n\t\tstateString = Retracted\n\t\tstagingEnabled = True\n\t\tposition = 0\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tActionToggle\n\t\t\t{\n\t\t\t\tactionGroup = Gear\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelLock\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelBogey\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelDamage\n\t\tisEnabled = True\n\t\tisDamaged = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Bug-E Buggy.craft
    'spotLight1': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleLight\n\t\tisEnabled = True\n\t\tisOn = False\n\t\tuiWriteLock = False\n\t\tlightR = 1\n\t\tlightG = 1\n\t\tlightB = 1\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleLightAction\n\t\t\t{\n\t\t\t\tactionGroup = Light\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLightOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLightOffAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- ComSat Lx.craft
    'fairingSize1': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleProceduralFairing\n\t\tisEnabled = True\n\t\tinterstageCraftID = 0\n\t\tnArcs = 2\n\t\tejectionForce = 100\n\t\tuseClamshell = False\n\t\tstagingEnabled = True\n\t\tfsm = st_idle\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployFairingAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tXSECTION\n\t\t{\n\t\t\th = 0\n\t\t\tr = 0.625\n\t\t}\n\t\tXSECTION\n\t\t{\n\t\t\th = 1.1307373\n\t\t\tr = 0.519284248\n\t\t}\n\t\tXSECTION\n\t\t{\n\t\t\th = 1.86900806\n\t\t\tr = 0.200000003\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoBay\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNode\n\t\tisEnabled = True\n\t\tspawnState = False\n\t\tvisibilityState = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleStructuralNodeToggle\n\t\tisEnabled = True\n\t\tshowMesh = True\n\t\tshowNodes = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Orbiter 1A.craft
    'FuelCell': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleResourceConverter\n\t\tisEnabled = True\n\t\tEfficiencyBonus = 1\n\t\tIsActivated = False\n\t\tstagingEnabled = True\n\t\tlastUpdateTime = 0\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStopResourceConverterAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tStartResourceConverterAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleResourceConverterAction\n\t\t\t{\n\t\t\t\tactionGroup = Custom04\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 50\n\t\tmaxAmount = 50\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- ComSat Lx.craft
    'microEngine.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEnginesFX\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}


# Radial placements: (part, azimuth degrees, dy from the host centre).
RADIAL = [
    ("parachuteRadial", 45.0, 0.35),
    ("parachuteRadial", 225.0, 0.35),
    ("landingLeg1", 90.0, -0.60),
    ("landingLeg1", 210.0, -0.60),
    ("landingLeg1", 330.0, -0.60),
    ("spotLight1", 0.0, 0.10),
    ("spotLight1", 180.0, 0.10),
    ("FuelCell", 135.0, 0.10),
    ("microEngine.v2", 315.0, -0.20),
]

# Local attach axis per part, driving the azimuth -> rotation formula. Read off
# each part cfg's `node_attach` direction rather than assumed: spotLight1 is
# `0,0,-1` (local -Z), which is the case build_gs1_craft.py validated on stock.
SRF_AXIS = {
    "parachuteRadial": "-Z", "spotLight1": "-Z", "landingLeg1": "-Z",
    "FuelCell": "-Z", "microEngine.v2": "-Z",
}


def _fmt(v):
    v = float(v)
    return "%d" % int(v) if v == int(v) else repr(v)


def _vec(x, y, z):
    return "%s,%s,%s" % (_fmt(x), _fmt(y), _fmt(z))


def _quat_y(psi_deg):
    h = math.radians(psi_deg) / 2.0
    return (0.0, math.sin(h), 0.0, math.cos(h))


def srf_rotation(part, azimuth):
    psi = 90.0 - azimuth if SRF_AXIS[part] == "-Z" else 180.0 - azimuth
    return ",".join(_fmt(c) for c in _quat_y(psi))


def _host(base_text, name):
    for blk in base_text.split("\nPART\n{")[1:]:
        m = re.search(r"^\tpart = (\S+)$", blk, re.M)
        if not m or m.group(1).rsplit("_", 1)[0] != name:
            continue
        p = re.search(r"^\tpos = (\S+)$", blk, re.M)
        return m.group(1), tuple(float(x) for x in p.group(1).split(","))
    raise SystemExit("base craft has no %s" % name)


def _block(part, uid, pos, rel, rot, istg, srfn=None, attn=None):
    L = ["PART", "{", "\tpart = %s_%d" % (part, uid), "\tpartName = Part",
         "\tpersistentId = %d" % (900000000 + uid % 1000000),
         "\tpos = %s" % _vec(*pos), "\tattPos = 0,0,0",
         "\tattPos0 = %s" % _vec(*rel), "\trot = %s" % rot,
         "\tattRot = 0,0,0,1", "\tattRot0 = %s" % rot, "\tmir = 1,1,1",
         "\tsymMethod = Radial", "\tautostrutMode = Off",
         "\trigidAttachment = False", "\tistg = %d" % istg, "\tresPri = 0",
         "\tdstg = 0", "\tsidx = -1", "\tsqor = -1", "\tsepI = -1",
         "\tattm = %d" % (0 if attn else 1), "\tmodCost = 0", "\tmodMass = 0",
         "\tmodSize = 0,0,0"]
    if attn:
        L.append("\tattN = %s" % attn)
    if srfn:
        L.append("\tsrfN = srfAttach,%s" % srfn)
    L.append(TAILS[part])
    L.append("}")
    return "\n".join(L)


def build():
    base = open(BASE, encoding="utf-8").read().replace("\r\n", "\n")
    host_tok, host_pos = _host(base, HOST)
    nose_tok, nose_pos = _host(base, NOSE_HOST)

    out = base.rstrip("\n")
    out = re.sub(r"^ship = .*$", "ship = %s" % SHIP_NAME, out, count=1, flags=re.M)
    out = re.sub(r"^description = .*$",
                 "description = GS-6 part-event sweep craft. The committed Kerbal X "
                 "with a radial parachute pair, three landing legs, two lights, a "
                 "fuel cell and an EFFECTS-node Ant on the surviving top stack, plus "
                 "a fairing base on the docking node staged at istg 3. Built by "
                 "construction by harness/tools/build_kerbal_x_sweep_craft.py.",
                 out, count=1, flags=re.M)

    uid = 4200500000
    blocks = []
    for part, az, dy in RADIAL:
        uid += 137
        a = math.radians(az)
        pos = (host_pos[0] + RADIAL_RADIUS * math.cos(a),
               host_pos[1] + dy,
               host_pos[2] + RADIAL_RADIUS * math.sin(a))
        rel = (pos[0] - host_pos[0], pos[1] - host_pos[1], pos[2] - host_pos[2])
        blocks.append(_block(part, uid, pos, rel, srf_rotation(part, az), istg=-1,
                             srfn="%s,,0|0|0,%s|0|0,0|0|0"
                                  % (host_tok, _fmt(RADIAL_RADIUS))))

    uid += 137
    fy = nose_pos[1] + NOSE_HOST_TOP_OFFSET - FAIRING_BOTTOM_OFFSET
    blocks.append(_block("fairingSize1", uid, (nose_pos[0], fy, nose_pos[2]),
                         (0.0, fy - nose_pos[1], 0.0), "0,0,0,1",
                         istg=FAIRING_ISTG,
                         attn="bottom,%s_0|%s|0"
                              % (nose_tok, _fmt(FAIRING_BOTTOM_OFFSET))))
    return out + "\n" + "\n".join(blocks) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args(argv)
    text = build()
    if a.write:
        os.makedirs(os.path.dirname(OUT), exist_ok=True)
        with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote %s (%d lines)" % (OUT, text.count("\n")))
        return 0
    if not os.path.exists(OUT):
        print("MISSING %s - run --write" % OUT)
        return 1
    have = open(OUT, encoding="utf-8").read().replace("\r\n", "\n")
    print("OK committed craft matches the derivation" if have == text
          else "DRIFT: committed craft differs from the derivation")
    return 0 if have == text else 1


if __name__ == "__main__":
    sys.exit(main())
