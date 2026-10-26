#!/usr/bin/env python3
"""Build the committed `CS Orbiter.craft` BY CONSTRUCTION.

CS-4's craft (the scripted campaign, docs/dev/autotest-roadmap.md "The scripted campaign
program", stage "Reach orbit and come home"). It flies from `cs-career-mc1` (CS-3b's
produced save) after the lane researches Engineering 101 and Survivability, so every part
is on `start`, `basicRocketry`, `engineering101` or `survivability`
(docs/dev/research/stock-tech-tree-part-unlocks.md), and the craft fits the level-0
facilities: 30 VAB parts, 18 t and 20 m on the launch pad.

THE STACK, top to bottom (an Orbiter One derivative with the tier-locked parts swapped):
    Mk16 parachute            stage 0 (never staged by the lane; the mission deploys it)
    Mk1 pod (root)
    Heat Shield 1.25 m        staging DISABLED (its own decoupler is an omni jettison)
    TD-12                     stage 1: drops the service section (the mission's one
                              reentry stage, reentryStageCount = 1)
    N x FL-T100
    LV-T45 Swivel             stage 2, with the TD-12 below it (hot stage)
    TD-12                     stage 2: drops the spent Hammer B
    RT-10 Hammer B            stage 3, with the TD-12 below it
    TD-12                     stage 3: drops the spent Hammer A
    RT-10 Hammer A + 4 fins   stage 4: the launch
MechJeb's autostage is limited to stage 2 by the lane, so it never fires stage 1.

THE HEAT SHIELD DOES NOT SEPARATE THE POD. HeatShield1's ModuleDecouple is
`isOmniDecoupler = true` with `stagingEnabled = False` (the "Jettison Heat Shield" menu
item): staged, it would throw the shield off the pod AND off the tanks. Stock's Science
Jr. is the pattern taken here: shield (staging disabled) on the pod, a TD-12 on the
shield's `direct` node. The CS-4 plan's "HeatShield1 (S1, decouples the pod)" is wrong
on this point; the extra TD-12 costs one part, 0.04 t and 200 funds.

WHAT IS COPIED AND WHAT IS DERIVED (build_dd1_craft.py's contract):
  COPIED - each PART's tail (EVENTS / ACTIONS / PARTDATA / MODULE* / RESOURCE*) is lifted
  BYTE-FOR-BYTE out of a stock craft KSP wrote; TAILS names the donor and occurrence, and
  `--verify-donors <Ships dir>` re-extracts every tail from the install and compares. No
  tail line is rewritten. The donors were chosen for their stored state, not for
  convenience:
    - the Hammer comes from Ships/SPH/Stratolauncher.craft because it is the only stock
      Hammer at thrustPercentage = 100 (Kerbal 1 carries 50, Orbiter One 51, AeroEquus 60);
    - the Swivel comes from Orbiter One's UPPER Swivel because it is thrustPercentage = 100
      with its shroud on (`isJettisoned = False`), the configuration of a Swivel with a
      decoupler below it (Kerbal 1's Swivel is throttled to 65);
    - the heat shield comes from Science Jr. because its ModuleDecouple is staging
      DISABLED (PT Series Munsplorer's is enabled); it carries Ablator 100 of 200, which
      is stock's own load and ample for a low Kerbin orbit return;
    - the chute (automateSafeDeploy = 0, opens only when safe), pod, TD-12 and FL-T100
      come from Kerbal 1 (1.12.0), the fins from the Jumping Flea (1.12.0).
  DERIVED - the scalar header of each PART (pos / attPos0 / attN / srfN / links / istg /
  dstg / sidx / sqor). Stack placement is GS-1's formula
      child.pos = parent.pos + parent_node_offset - child_opposite_node_offset
  with every offset read off a donor craft's own pipe-format attN token, except the
  Hammer's BOTTOM node, which no stock craft attaches: -1.56208 is
  solidBoosterRT-10_v2.cfg's node_stack_bottom, used as is because that part's
  rescaleFactor and scale are both 1 (the parachuteSingle rescale trap does not apply).
  Fins use the +X surface family (psi = 180 - azimuth), validated on the Jumping Flea's
  own fins (azimuth 90 -> rot (0, 0.7071, 0, 0.7071)), at the Flea's measured radius on
  the same 1.25 m booster diameter.

THE NUMBERS (`--report` prints them; the unit gate asserts the limits). Masses and costs
are the 1.12.5 part cfgs (GameData/Squad/Parts) with each tail's own resource load:
KSP's part cost includes full tanks, so a partly loaded part costs its cfg cost minus the
missing resource's unitCost (the shield's 100 missing Ablator, -50). Isp and thrust are
the cfgs' atmosphereCurve / maxThrust (vacuum; sea-level thrust = maxThrust * IspASL /
IspVac).

ASSUMPTIONS A FLIGHT CAN FALSIFY: the root height (cosmetic), the 0.3 m chute allowance in
the height figure, the fin radius and band (cosmetic), and whether MechJeb's classic
ascent can fly a 1.2 TWR solid first stage with no gimbal (the reading run's question).

Usage:
    python harness/tools/build_cs_orbiter_craft.py --check
    python harness/tools/build_cs_orbiter_craft.py --write
    python harness/tools/build_cs_orbiter_craft.py --report [--tanks N]
    python harness/tools/build_cs_orbiter_craft.py --verify-donors <stock-tree Ships dir>

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
OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-career-mc1",
                   "Ships", "VAB", "CS Orbiter.craft")
DEFAULT_DONOR_SHIPS = os.path.join(os.path.dirname(ROOT), "automation", "stock-tree",
                                   "Ships")

SHIP_NAME = "CS Orbiter"
DEFAULT_TANKS = 12

# Level-0 facility limits (the lane flies with every facility at level 0 but
# Mission Control): VAB parts, launch pad mass (t) and height (m).
LIMIT_PARTS = 30
LIMIT_MASS_T = 18.0
LIMIT_HEIGHT_M = 20.0
# The CS-4 plan's floors for the committed craft.
MIN_DELTA_V = 3700.0
MIN_LIFTOFF_TWR = 1.2

G0 = 9.80665
ROOT_Y = 20.0
FIN_RADIUS = 0.622972488      # the Jumping Flea's fins on its 1.25 m booster
FIN_DY = -1.0                 # fin band below Hammer A's centre
CHUTE_ALLOWANCE = 0.3         # Mk16 pack above the pod's top node (height figure only)

# (part, donor path under Ships/, occurrence of that part in file order).
DONORS = {
    "mk1pod.v2": ("VAB/Kerbal 1.craft", 0),
    "parachuteSingle": ("VAB/Kerbal 1.craft", 0),
    "HeatShield1": ("VAB/Science Jr.craft", 0),
    "Decoupler.1": ("VAB/Kerbal 1.craft", 0),
    "fuelTankSmallFlat": ("VAB/Kerbal 1.craft", 0),
    "liquidEngine2": ("VAB/Orbiter One.craft", 0),
    "solidBooster.v2": ("SPH/Stratolauncher.craft", 0),
    "basicFin": ("VAB/Jumping Flea.craft", 0),
}

# EFFECTIVE stack-node offsets (m), each read off a donor's pipe-format attN token
# unless noted.
NODE_OFFSET = {
    # Kerbal 1: attN = bottom,...|-0.4050379|0 and attN = top,...|0.6423756|0
    "mk1pod.v2": {"top": 0.6423756, "bottom": -0.4050379},
    # Kerbal 1: attN = bottom,mk1pod...|-0.01508113|0
    "parachuteSingle": {"bottom": -0.01508113},
    # Science Jr.: attN = direct,Decoupler.1...|0|0 and attN = top,mk1pod...|0.022|0
    "HeatShield1": {"top": 0.022, "direct": 0.0},
    # Kerbal 1: attN = top,...|0.05|0 and attN = bottom,...|-0.05|0
    "Decoupler.1": {"top": 0.05, "bottom": -0.05},
    # Kerbal 1: attN = top,...|0.3125|0 and attN = bottom,...|-0.3125|0
    "fuelTankSmallFlat": {"top": 0.3125, "bottom": -0.3125},
    # Orbiter One: attN = top,...|0.9018263|0 and attN = bottom,Decoupler.1...|-0.7179225|0
    "liquidEngine2": {"top": 0.9018263, "bottom": -0.7179225},
    # Stratolauncher: attN = top,Decoupler.1...|1.281837|0; bottom from the cfg (header).
    "solidBooster.v2": {"top": 1.281837, "bottom": -1.56208},
}

# 1.12.5 part cfgs: dry mass (t), cost with FULL resources, and engine data.
PART_CFG = {
    "mk1pod.v2": {"mass": 0.8, "cost": 600},              # Command/mk1pod_v2/mk1Pod_v2.cfg
    "parachuteSingle": {"mass": 0.1, "cost": 422},        # Utility/parachuteMk1
    "HeatShield1": {"mass": 0.1, "cost": 300},            # Aero/HeatShield/HeatShield1.cfg
    "Decoupler.1": {"mass": 0.04, "cost": 200},           # Coupling/Decoupler_1.cfg
    "fuelTankSmallFlat": {"mass": 0.0625, "cost": 150},   # FuelTank/Size1_Tanks/fuelTankT100
    "liquidEngine2": {"mass": 1.5, "cost": 1200,          # Engine/liquidEngineLV-T45
                      "thrust": 215.0, "isp_vac": 320.0, "isp_asl": 250.0},
    "solidBooster.v2": {"mass": 0.75, "cost": 400,        # Engine/Size1_SRBs/RT-10_v2
                        "thrust": 227.0, "isp_vac": 195.0, "isp_asl": 170.0},
    "basicFin": {"mass": 0.01, "cost": 25},               # Aero/basicFin
}
# Squad/Resources/ResourcesGeneric.cfg: (density t/unit, unitCost).
RESOURCES = {
    "LiquidFuel": (0.005, 0.8), "Oxidizer": (0.005, 0.18), "SolidFuel": (0.0075, 0.6),
    "MonoPropellant": (0.004, 1.2), "Ablator": (0.001, 0.5), "ElectricCharge": (0.0, 0.0),
}

# THE STAGING TABLE. KSP fires stages in DESCENDING istg order.
STAGE_LAUNCH = 4          # Hammer A
STAGE_HAMMER_B = 3        # TD-12 (drops Hammer A) + Hammer B
STAGE_SWIVEL = 2          # TD-12 (drops Hammer B) + Swivel
STAGE_POD_SEPARATION = 1  # TD-12 under the heat shield (drops the service section)
STAGE_CHUTE = 0

# Tails lifted BYTE-FOR-BYTE from the donor named above each entry (see DONORS and
# --verify-donors). Never edit one by hand.
TAILS = {
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock, KSP-written]
    'mk1pod.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCommand\n\t\tisEnabled = True\n\t\thibernation = False\n\t\thibernateOnWarp = False\n\t\tactiveControlPointName = _default\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tHibernateToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleReactionWheel\n\t\tisEnabled = True\n\t\tactuatorModeCycle = 0\n\t\tauthorityLimiter = 100\n\t\tstateString = Active\n\t\tstagingEnabled = True\n\t\tWheelState = Active\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCycleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDeactivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleColorChanger\n\t\tisEnabled = True\n\t\tanimState = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = Light\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceContainer\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCollectAllAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleConductionMultiplier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleLiftingSurface\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FlagDecal\n\t\tisEnabled = True\n\t\tflagDisplayed = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 50\n\t\tmaxAmount = 50\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = MonoPropellant\n\t\tamount = 10\n\t\tmaxAmount = 10\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock, KSP-written]
    'parachuteSingle': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleParachute\n\t\tisEnabled = True\n\t\tpersistentState = STOWED\n\t\tanimTime = 0\n\t\tminAirPressureToOpen = 0.0399999991\n\t\tdeployAltitude = 1000\n\t\tspreadAngle = 7\n\t\tautomateSafeDeploy = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tCutAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDragModifier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDragModifier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Ships/VAB/Science Jr.craft, occurrence 0  [stock, KSP-written]
    'HeatShield1': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = fairing\n\t\tisJettisoned = True\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = False\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAblator\n\t\tisEnabled = True\n\t\tnominalAmountRecip = -1\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleColorChanger\n\t\tisEnabled = True\n\t\tanimState = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDecouple\n\t\tisEnabled = True\n\t\tejectionForcePercent = 100\n\t\tisDecoupled = False\n\t\tstagingEnabled = False\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDecoupleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleLiftingSurface\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = Ablator\n\t\tamount = 100\n\t\tmaxAmount = 200\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock, KSP-written]
    'Decoupler.1': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDecouple\n\t\tisEnabled = True\n\t\tejectionForcePercent = 100\n\t\tisDecoupled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDecoupleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleToggleCrossfeed\n\t\tisEnabled = True\n\t\tcrossfeedStatus = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tEnableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDisableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock, KSP-written]
    'fuelTankSmallFlat': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tselectedVariant = BlackAndWhite\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 45\n\t\tmaxAmount = 45\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 55\n\t\tmaxAmount = 55\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- Ships/VAB/Orbiter One.craft, occurrence 0  [stock, KSP-written]
    'liquidEngine2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = fairing\n\t\tisJettisoned = False\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAlternator\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Ships/SPH/Stratolauncher.craft, occurrence 0  [stock, KSP-written]
    'solidBooster.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = shroud\n\t\tisJettisoned = True\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = SolidFuel\n\t\tamount = 375\n\t\tmaxAmount = 375\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- Ships/VAB/Jumping Flea.craft, occurrence 0  [stock, KSP-written]
    'basicFin': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleLiftingSurface\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}


def _fmt(v):
    v = round(float(v), 9)
    if v == 0:
        return "0"     # also folds a rounded -0.0 (cos 270 deg) into "0"
    s = repr(round(v, 9))
    return s[:-2] if s.endswith(".0") else s


def _vec(x, y, z):
    return "%s,%s,%s" % (_fmt(x), _fmt(y), _fmt(z))


def _quat_y(psi_deg):
    h = math.radians(psi_deg) / 2.0
    return ",".join(_fmt(round(c, 9)) for c in (0.0, math.sin(h), 0.0, math.cos(h)))


def tail_resources(name):
    """[(resource, amount, maxAmount)] from the part's donor tail."""
    out = []
    for blk in re.findall(r"\tRESOURCE\n\t\{\n(.*?)\n\t\}", TAILS[name], re.S):
        res = re.search(r"\t\tname = (\S+)", blk).group(1)
        amt = float(re.search(r"\t\tamount = (\S+)", blk).group(1))
        mx = float(re.search(r"\t\tmaxAmount = (\S+)", blk).group(1))
        out.append((res, amt, mx))
    return out


def part_mass(name):
    return PART_CFG[name]["mass"] + sum(a * RESOURCES[r][0] for r, a, _ in tail_resources(name))


def part_cost(name):
    return PART_CFG[name]["cost"] - sum((m - a) * RESOURCES[r][1]
                                        for r, a, m in tail_resources(name))


def propellant_mass(name):
    return sum(a * RESOURCES[r][0] for r, a, _ in tail_resources(name)
               if r in ("LiquidFuel", "Oxidizer", "SolidFuel"))


class Part(object):
    def __init__(self, key, name, uid, y, istg=-1, dstg=0, sidx=-1, group="pod"):
        self.key, self.name, self.uid_n, self.y = key, name, uid, y
        self.pos = (0.0, y, 0.0)
        self.rot = "0,0,0,1"
        self.istg, self.dstg, self.sidx = istg, dstg, sidx
        self.sqor = istg if sidx >= 0 else -1
        self.attm = 0
        self.links, self.syms, self.attn = [], [], []
        self.srfn = None
        self.parent = None
        # Which stage's drop takes this part off the vessel: "pod" stays to the end.
        self.group = group

    @property
    def uid(self):
        return "%s_%d" % (self.name, self.uid_n)


def layout(tanks=DEFAULT_TANKS):
    """Every part, root first, with positions, links and attN derived."""
    uid = iter(range(4260000001, 4260000100))
    off = NODE_OFFSET

    def below(parent, pnode, key, name, cnode, **kw):
        y = parent.y + off[parent.name][pnode] - off[name][cnode]
        p = Part(key, name, next(uid), y, **kw)
        p.parent = parent
        parent.links.append(p.uid)
        parent.attn.append((pnode, p.uid, off[parent.name][pnode]))
        p.attn.append((cnode, parent.uid, off[name][cnode]))
        return p

    pod = Part("pod", "mk1pod.v2", next(uid), ROOT_Y, dstg=0)
    chute = Part("chute", "parachuteSingle", next(uid),
                 pod.y + off["mk1pod.v2"]["top"] - off["parachuteSingle"]["bottom"],
                 istg=STAGE_CHUTE, dstg=0, sidx=0)
    chute.parent = pod
    pod.links.append(chute.uid)
    shield = Part("shield", "HeatShield1", next(uid),
                  pod.y + off["mk1pod.v2"]["bottom"] - off["HeatShield1"]["top"], dstg=1)
    shield.parent = pod
    pod.links.append(shield.uid)
    # Donor order (Kerbal 1 / Science Jr.): bottom then top.
    pod.attn = [("bottom", shield.uid, off["mk1pod.v2"]["bottom"]),
                ("top", chute.uid, off["mk1pod.v2"]["top"])]
    chute.attn = [("bottom", pod.uid, off["parachuteSingle"]["bottom"])]
    dec1 = below(shield, "direct", "dec1", "Decoupler.1", "top",
                 istg=STAGE_POD_SEPARATION, dstg=3, sidx=0, group="service")
    shield.attn.append(("top", pod.uid, off["HeatShield1"]["top"]))
    prev, pnode = dec1, "bottom"
    tank_parts = []
    for i in range(tanks):
        t = below(prev, pnode, "tank%d" % i, "fuelTankSmallFlat", "top",
                  istg=STAGE_POD_SEPARATION, dstg=4, group="service")
        tank_parts.append(t)
        prev, pnode = t, "bottom"
    swivel = below(prev, pnode, "swivel", "liquidEngine2", "top",
                   istg=STAGE_SWIVEL, dstg=4, sidx=1, group="service")
    dec2 = below(swivel, "bottom", "dec2", "Decoupler.1", "top",
                 istg=STAGE_SWIVEL, dstg=5, sidx=0, group="hammerB")
    hammer_b = below(dec2, "bottom", "hammerB", "solidBooster.v2", "top",
                     istg=STAGE_HAMMER_B, dstg=6, sidx=1, group="hammerB")
    dec3 = below(hammer_b, "bottom", "dec3", "Decoupler.1", "top",
                 istg=STAGE_HAMMER_B, dstg=7, sidx=0, group="hammerA")
    hammer_a = below(dec3, "bottom", "hammerA", "solidBooster.v2", "top",
                     istg=STAGE_LAUNCH, dstg=8, sidx=0, group="hammerA")
    fins = []
    for i, az in enumerate((0.0, 90.0, 180.0, 270.0)):
        a = math.radians(az)
        f = Part("fin%d" % i, "basicFin", next(uid), hammer_a.y + FIN_DY,
                 istg=STAGE_HAMMER_B, dstg=8, group="hammerA")
        f.pos = (FIN_RADIUS * math.cos(a), hammer_a.y + FIN_DY, FIN_RADIUS * math.sin(a))
        f.rot = _quat_y(180.0 - az)
        f.attm = 1
        f.srfn = hammer_a.uid
        f.parent = hammer_a
        fins.append(f)
    for f in fins:
        f.syms = [o.uid for o in fins if o is not f]
    hammer_a.links += [f.uid for f in fins]
    return ([pod, chute, shield, dec1] + tank_parts
            + [swivel, dec2, hammer_b, dec3, hammer_a] + fins)


def render_part(p, index):
    rel = p.pos if p.parent is None else tuple(a - b for a, b in zip(p.pos, p.parent.pos))
    out = ["PART", "{",
           "\tpart = %s" % p.uid,
           "\tpartName = Part",
           "\tpersistentId = %d" % (1700000000 + index * 7717),
           "\tpos = %s" % _vec(*p.pos),
           "\tattPos = 0,0,0",
           "\tattPos0 = %s" % _vec(*rel),
           "\trot = %s" % p.rot,
           "\tattRot = 0,0,0,1",
           "\tattRot0 = %s" % (p.rot if p.attm else "0,0,0,1"),
           "\tmir = 1,1,1",
           "\tsymMethod = Radial",
           "\tautostrutMode = Off",
           "\trigidAttachment = False",
           "\tistg = %d" % p.istg,
           "\tresPri = 0",
           "\tdstg = %d" % p.dstg,
           "\tsidx = %d" % p.sidx,
           "\tsqor = %d" % p.sqor,
           "\tsepI = -1",
           "\tattm = %d" % p.attm,
           "\tmodCost = 0",
           "\tmodMass = 0",
           "\tmodSize = 0,0,0"]
    out += ["\tlink = %s" % l for l in p.links]
    out += ["\tsym = %s" % s for s in p.syms]
    out += ["\tattN = %s,%s_0|%s|0" % (node, other, _fmt(dy)) for node, other, dy in p.attn]
    if p.srfn:
        out.append("\tsrfN = srfAttach,%s" % p.srfn)
    out.append(TAILS[p.name])
    out.append("}")
    return "\n".join(out)


def stack_height(parts):
    pod = parts[0]
    hammer_a = [p for p in parts if p.key == "hammerA"][0]
    top = pod.y + NODE_OFFSET["mk1pod.v2"]["top"] + CHUTE_ALLOWANCE
    bottom = hammer_a.y + NODE_OFFSET["solidBooster.v2"]["bottom"]
    return top - bottom


def build(tanks=DEFAULT_TANKS):
    parts = layout(tanks)
    xs = [p.pos[0] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % SHIP_NAME,
        "version = 1.12.5",
        "description = CS-4 craft (scripted campaign, Career): Mk1 pod, heat shield, "
        "%d FL-T100 on a Swivel, two Hammer boosters. Built by construction by "
        "harness/tools/build_cs_orbiter_craft.py." % tanks,
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 1.25, stack_height(parts),
                           max(zs) - min(zs) + 1.25),
        "steamPublishedFileId = 0",
        "persistentId = 2600440077",
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    body = [render_part(p, i) for i, p in enumerate(parts)]
    return "\n".join(header) + "\n" + "\n".join(body) + "\n"


# ---------------------------------------------------------------------------
# The numbers: mass, cost, per-stage delta-v, liftoff TWR.
# ---------------------------------------------------------------------------

def figures(tanks=DEFAULT_TANKS):
    parts = layout(tanks)
    mass = sum(part_mass(p.name) for p in parts)
    cost = sum(part_cost(p.name) for p in parts)

    def group_mass(group):
        return sum(part_mass(p.name) for p in parts if p.group == group)

    def group_dry(group):
        return sum(part_mass(p.name) - propellant_mass(p.name)
                   for p in parts if p.group == group)

    def prop(group):
        return sum(propellant_mass(p.name) for p in parts if p.group == group)

    hammer = PART_CFG["solidBooster.v2"]
    swivel = PART_CFG["liquidEngine2"]
    stages = []
    m0 = mass
    for label, group, eng in (("S4 Hammer A", "hammerA", hammer),
                              ("S3 Hammer B", "hammerB", hammer),
                              ("S2 Swivel", "service", swivel)):
        m1 = m0 - prop(group)
        ratio = math.log(m0 / m1)
        stages.append({"stage": label, "m0": m0, "m1": m1,
                       "dv_vac": eng["isp_vac"] * G0 * ratio,
                       "dv_asl": eng["isp_asl"] * G0 * ratio,
                       "twr_vac": eng["thrust"] / (m0 * G0)})
        m0 = m1 - group_dry(group)          # the drop: the spent group leaves
    liftoff_twr = (hammer["thrust"] * hammer["isp_asl"] / hammer["isp_vac"]) / (mass * G0)
    return {
        "parts": len(parts), "mass": mass, "cost": cost,
        "height": stack_height(parts), "stages": stages,
        # The launch stage at sea-level Isp, the rest in vacuum: what the ascent gets.
        "dv_total": stages[0]["dv_asl"] + stages[1]["dv_vac"] + stages[2]["dv_vac"],
        "dv_total_vac": sum(s["dv_vac"] for s in stages),
        "liftoff_twr": liftoff_twr,
        "reentry_mass": group_mass("pod"),
    }


def stage_table(parts):
    """{istg: sorted part names} over the STAGED parts (sidx >= 0)."""
    table = {}
    for p in parts:
        if p.sidx >= 0:
            table.setdefault(p.istg, []).append(p.name)
    return {k: sorted(v) for k, v in table.items()}


def problems(tanks=DEFAULT_TANKS):
    """Every post-condition of the craft, as strings (empty = good)."""
    out = []
    parts = layout(tanks)
    f = figures(tanks)
    if f["parts"] > LIMIT_PARTS:
        out.append("%d parts > the level-0 VAB limit %d" % (f["parts"], LIMIT_PARTS))
    if f["mass"] > LIMIT_MASS_T:
        out.append("%.3f t > the level-0 pad limit %.0f t" % (f["mass"], LIMIT_MASS_T))
    if f["height"] > LIMIT_HEIGHT_M:
        out.append("%.2f m > the level-0 pad height %.0f m" % (f["height"], LIMIT_HEIGHT_M))
    want = {STAGE_LAUNCH: ["solidBooster.v2"],
            STAGE_HAMMER_B: ["Decoupler.1", "solidBooster.v2"],
            STAGE_SWIVEL: ["Decoupler.1", "liquidEngine2"],
            STAGE_POD_SEPARATION: ["Decoupler.1"],
            STAGE_CHUTE: ["parachuteSingle"]}
    if stage_table(parts) != want:
        out.append("stage order %r != %r" % (stage_table(parts), want))
    if not re.search(
            r"name = ModuleDecouple\n(?:\t\t.*\n)*?\t\tstagingEnabled = False",
            TAILS["HeatShield1"]):
        out.append("the heat shield's decoupler must be staging-disabled")
    shield = [p for p in parts if p.name == "HeatShield1"][0]
    if shield.istg != -1:
        out.append("the heat shield must not be staged")
    for name in ("liquidEngine2", "solidBooster.v2"):
        m = re.search(r"thrustPercentage = (\S+)", TAILS[name])
        if not m or m.group(1) != "100":
            out.append("%s donor is not at thrustPercentage 100" % name)
    if "automateSafeDeploy = 0" not in TAILS["parachuteSingle"]:
        out.append("the Mk16 must carry automateSafeDeploy = 0")
    if "actionGroup = Custom" in "".join(TAILS.values()):
        out.append("no tail may bind a custom action group")
    uids = [p.uid for p in parts]
    if len(set(uids)) != len(uids):
        out.append("duplicate part uid")
    for p in parts:
        for ref in p.links + [o for _, o, _ in p.attn] + ([p.srfn] if p.srfn else []):
            if ref not in uids:
                out.append("%s references undeclared %s" % (p.uid, ref))
    return out


def check():
    """[] when the committed craft is byte-identical to a fresh derivation and every
    post-condition holds, else the problems (the drift gate's contract)."""
    out = problems()
    if not os.path.exists(OUT):
        return out + ["MISSING %s - run --write" % OUT]
    with open(OUT, encoding="utf-8", newline="") as fh:
        have = fh.read()
    if have != build():
        out.append("DRIFT: %s differs from the derivation" % OUT)
    return out


def _donor_tail(ships_dir, name, rel, occurrence):
    path = os.path.join(ships_dir, *rel.split("/"))
    with open(path, encoding="latin-1", newline="") as fh:
        text = fh.read().replace("\r\n", "\n")
    found = []
    for blk in text.split("\nPART\n{\n")[1:]:
        m = re.match(r"\tpart = (\S+)_\d+\n", blk)
        if not m or m.group(1) != name:
            continue
        body = blk.split("\n}\n", 1)[0] if "\n}\n" in blk else blk.rsplit("\n}", 1)[0]
        found.append(body[body.index("\n\tEVENTS\n") + 1:])
    return found[occurrence] if len(found) > occurrence else None


def verify_donors(ships_dir):
    """[] when every embedded tail is byte-identical to its donor in ``ships_dir``."""
    out = []
    for name, (rel, occ) in sorted(DONORS.items()):
        got = _donor_tail(ships_dir, name, rel, occ)
        if got is None:
            out.append("%s: no occurrence %d in %s" % (name, occ, rel))
        elif got != TAILS[name]:
            out.append("%s: tail differs from %s occurrence %d" % (name, rel, occ))
    return out


def report(tanks):
    f = figures(tanks)
    print("CS Orbiter, %d x FL-T100: %d parts, %.3f t, %.2f m, cost %.0f"
          % (tanks, f["parts"], f["mass"], f["height"], f["cost"]))
    for s in f["stages"]:
        print("  %-12s m0 %.3f t -> m1 %.3f t  dv vac %.0f m/s (ASL %.0f)  TWR vac %.2f"
              % (s["stage"], s["m0"], s["m1"], s["dv_vac"], s["dv_asl"], s["twr_vac"]))
    print("  total dv %.0f m/s (launch stage at ASL Isp), %.0f m/s all vacuum"
          % (f["dv_total"], f["dv_total_vac"]))
    print("  liftoff TWR %.3f (sea level); reentry mass %.3f t"
          % (f["liftoff_twr"], f["reentry_mass"]))


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--report", action="store_true")
    ap.add_argument("--tanks", type=int, default=DEFAULT_TANKS,
                    help="FL-T100 count for --report only (the committed craft is %d)"
                    % DEFAULT_TANKS)
    ap.add_argument("--verify-donors", metavar="SHIPS_DIR", nargs="?",
                    const=DEFAULT_DONOR_SHIPS)
    a = ap.parse_args(argv)
    if a.tanks != DEFAULT_TANKS and not a.report:
        ap.error("--tanks applies to --report only")
    if a.report:
        report(a.tanks)
        return 0
    if a.verify_donors:
        bad = verify_donors(a.verify_donors)
        print("\n".join(bad) or "OK every tail matches its donor in %s" % a.verify_donors)
        return 1 if bad else 0
    if a.write:
        bad = problems()
        if bad:
            print("\n".join(bad))
            return 1
        text = build()
        os.makedirs(os.path.dirname(OUT), exist_ok=True)
        with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote %s (%d lines)" % (OUT, text.count("\n")))
        report(DEFAULT_TANKS)
        return 0
    bad = check()
    print("\n".join(bad) or "OK committed craft matches the derivation")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
