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
    python harness/tools/build_cs_orbiter_craft.py --variant mun --check|--write|--report
    python harness/tools/build_cs_orbiter_craft.py --variant lander --check|--write|--report
    python harness/tools/build_cs_orbiter_craft.py --variant munsci --check|--write|--report
    python harness/tools/build_cs_orbiter_craft.py --variant solar --check|--write|--report

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
        # The TAILS key (the part name unless one part carries two donor tails) and
        # the attach overrides a child of a ROTATED parent needs (its attPos0 /
        # attRot0 are in the parent's frame); None keeps the world-frame defaults.
        self.tail = name
        self.att_pos0 = None
        self.att_rot0 = None

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
           "\tattPos0 = %s" % (_vec(*p.att_pos0) if p.att_pos0 is not None
                                 else _vec(*rel)),
           "\trot = %s" % p.rot,
           "\tattRot = 0,0,0,1",
           "\tattRot0 = %s" % (p.att_rot0 if p.att_rot0 is not None
                                 else (p.rot if p.attm else "0,0,0,1")),
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
    out.append(TAILS[p.tail])
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
    for key, (rel, occ) in sorted(DONORS.items()):
        name = key.split("@", 1)[0]
        got = _donor_tail(ships_dir, name, rel, occ)
        if got is None:
            out.append("%s: no occurrence %d in %s" % (name, occ, rel))
        elif got != TAILS[key]:
            out.append("%s: tail differs from %s occurrence %d" % (key, rel, occ))
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



# ---------------------------------------------------------------------------
# THE MUN VARIANT (`--variant mun`): `CS Mun Flyby`, CS-5's craft.
#
# It flies from `cs-career-ts1` (CS-4b's produced save): `start`, `basicRocketry`,
# `engineering101`, `survivability`, `generalRocketry` and `stability` researched,
# the Launch Pad at level 1 and the VAB at level 0. The limits are stock's own
# (decompiled GameVariables, KSP 1.12.5): GetPartCountLimit(VAB 0.0) = 30,
# GetCraftMassLimit(pad 0.5) = 140 t, GetCraftSizeLimit(pad 0.5) = 28 x 36 x 28 m.
#
# THE STACK, top to bottom (Science Jr.'s layout - radial Thumpers on a Swivel
# core - grown into a two-stage Mun craft):
#     Mk16 parachute            stage 0 (the mission deploys it)
#     Mk1 pod (root)
#     Heat Shield 1.25 m        staging DISABLED (as on CS Orbiter)
#     TD-12                     stage 1: drops the service section (the return's
#                               one owed stage, returnStageCount = 1)
#     4 x FL-T200
#     LV-T45 Swivel (upper)     stage 2, with the TD-12 below it (hot stage)
#     TD-12                     stage 2: drops the spent core
#     11 x FL-T200
#     LV-T45 Swivel (core)      stage 4 (the launch)
#     2 x TT-38K on the core    stage 3: drops the Thumpers
#     2 x BACC Thumper + cones  stage 4 (the launch), thrust limited to 60%
# MechJeb's autostage is limited to stage 2, so it never fires stage 1.
#
# DONORS, by stored state: the Thumper from GDLV3 (1.12.0) because no stock craft
# carries one at 100% (GDLV3 60, Science Jr. 50) and 60% is what lifts this stack
# at about 1.3; the core Swivel from Orbiter One's LOWER Swivel (thrustPercentage
# 100, nothing below it, shroud off); the FL-T200 from ComSat Lx (1.12.0); the
# TT-38K and the nose cone from Kerbal 1 (1.12.0). Every other tail is CS
# Orbiter's.
#
# RADIAL GEOMETRY. Science Jr. carries this exact pair (a Thumper on a TT-38K on a
# 1.25 m core), so its measured attach is used: the decoupler 0.630977273 m off
# the core axis, the Thumper at (-0.796787977, -0.338456988, 0) in the
# decoupler's own frame with attRot0 yaw(-90). Radial parts take Kerbal 1's yaw
# convention (decoupler yaw = 180 - azimuth), the one CS Orbiter's fins use, and a
# child of a ROTATED parent writes its attPos0 / attRot0 in that parent's frame
# (Kerbal 1's +x Hammer: world offset (+0.786, -0.493, 0), attPos0 (-0.786,
# -0.493, 0) under a yaw(180) decoupler).
#
# ASSUMPTIONS A FLIGHT CAN FALSIFY: the decoupler height on the core (cosmetic up
# to the pad contact), whether MechJeb's classic ascent flies a 1.3 TWR stack with
# only the core Swivel's 3 degree gimbal and no fins, and the crew's mass (not in
# the figures, as on CS Orbiter).
# ---------------------------------------------------------------------------

MUN_SHIP_NAME = "CS Mun Flyby"
MUN_OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-career-ts1",
                       "Ships", "VAB", "CS Mun Flyby.craft")
MUN_UPPER_TANKS = 4
MUN_CORE_TANKS = 11
# The core tank the TT-38Ks ride, counted from the BOTTOM (0 = the lowest): the
# one that puts the Thumper nozzles level with the core Swivel's.
MUN_DECOUPLER_TANK_FROM_BOTTOM = 2

MUN_LIMIT_PARTS = 30          # GetPartCountLimit(VAB level 0)
MUN_LIMIT_MASS_T = 140.0      # GetCraftMassLimit(Launch Pad level 1)
MUN_LIMIT_HEIGHT_M = 36.0     # GetCraftSizeLimit(Launch Pad level 1).y
# The CS-5 budget: about 3400 m/s to a low Kerbin orbit, 860 for the trans-Munar
# injection, up to 200 of corrections and a return trim, plus margin.
MUN_MIN_DELTA_V = 4800.0
MUN_MIN_UPPER_DELTA_V = 1500.0
MUN_MIN_LIFTOFF_TWR = 1.25

MUN_DEC_RADIUS = 0.630977273                          # Science Jr. TT-38K attPos0
MUN_BOOSTER_LOCAL = (-0.796787977, -0.338456988, 0.0)  # Science Jr. Thumper attPos0
MUN_BOOSTER_LOCAL_PSI = -90.0                          # Science Jr. Thumper attRot0
MUN_AZIMUTHS = (0.0, 180.0)

MUN_STAGE_LAUNCH = 4          # core Swivel + both Thumpers
MUN_STAGE_BOOSTER_DROP = 3    # the two TT-38Ks
MUN_STAGE_CORE_DROP = 2       # TD-12 (drops the core) + the upper Swivel
MUN_STAGE_POD_SEPARATION = 1  # TD-12 under the heat shield
MUN_STAGE_CHUTE = 0

NODE_OFFSET.update({
    # Orbiter One: attN = top,...|0.55525|0 and attN = bottom,...|-0.55525|0
    "fuelTankSmall": {"top": 0.55525, "bottom": -0.55525},
    # Science Jr.: attN = top,noseCone...|3.92|0; bottom from the cfg
    # (solidBoosterBACC.cfg node_stack_bottom -3.914617, rescaleFactor 1).
    "solidBooster1-1": {"top": 3.92, "bottom": -3.914617},
    # Kerbal 1 / Science Jr.: attN = bottom01,...|0|0
    "noseCone": {"bottom01": 0.0},
})

PART_CFG.update({
    "fuelTankSmall": {"mass": 0.125, "cost": 275},        # FuelTank/Size1_Tanks/fuelTankT200
    "solidBooster1-1": {"mass": 1.5, "cost": 850,         # Engine/solidBoosterBACC
                        "thrust": 300.0, "isp_vac": 210.0, "isp_asl": 175.0},
    "radialDecoupler": {"mass": 0.025, "cost": 600},      # Utility/decouplerRadialTT-38K
    "noseCone": {"mass": 0.03, "cost": 240},              # Aero/aerodynamicNoseCone
})

MUN_DONORS = {
    'fuelTankSmall': ('VAB/ComSat Lx.craft', 0),
    'liquidEngine2@core': ('VAB/Orbiter One.craft', 1),
    'solidBooster1-1': ('VAB/GDLV3.craft', 0),
    'radialDecoupler': ('VAB/Kerbal 1.craft', 0),
    'noseCone': ('VAB/Kerbal 1.craft', 0),
}
MUN_TAILS = {
    # <- Ships/VAB/ComSat Lx.craft, occurrence 0  [stock, KSP-written]
    'fuelTankSmall': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tselectedVariant = BlackAndWhite\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 90\n\t\tmaxAmount = 90\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 110\n\t\tmaxAmount = 110\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- Ships/VAB/Orbiter One.craft, occurrence 1  [stock, KSP-written]
    'liquidEngine2@core': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = fairing\n\t\tisJettisoned = True\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAlternator\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 0\n\t\tmaxAmount = 0.12\n\t\tflowState = True\n\t\tisTweakable = False\n\t\thideFlow = True\n\t\tisVisible = False\n\t\tflowMode = Both\n\t}',
    # <- Ships/VAB/GDLV3.craft, occurrence 0  [stock, KSP-written]
    'solidBooster1-1': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 60\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = SolidFuel\n\t\tamount = 820\n\t\tmaxAmount = 820\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock, KSP-written]
    'radialDecoupler': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAnchoredDecoupler\n\t\tisEnabled = True\n\t\tejectionForcePercent = 100\n\t\tisDecoupled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDecoupleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleToggleCrossfeed\n\t\tisEnabled = True\n\t\tcrossfeedStatus = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tEnableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDisableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock, KSP-written]
    'noseCone': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tselectedVariant = BlackAndWhite\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}

DONORS.update(MUN_DONORS)
TAILS.update(MUN_TAILS)


def _yaw_vec(psi_deg, v):
    """``v`` rotated by yaw(psi) about +Y (the rotation _quat_y writes)."""
    a = math.radians(psi_deg)
    x, y, z = v
    return (x * math.cos(a) + z * math.sin(a), y, -x * math.sin(a) + z * math.cos(a))


def _thrust_fraction(tail_key):
    m = re.search(r"thrustPercentage = (\S+)", TAILS[tail_key])
    return float(m.group(1)) / 100.0 if m else 1.0


def _pmass(p):
    return PART_CFG[p.name]["mass"] + sum(a * RESOURCES[r][0]
                                          for r, a, _ in tail_resources(p.tail))


def _pcost(p):
    return PART_CFG[p.name]["cost"] - sum((m - a) * RESOURCES[r][1]
                                          for r, a, m in tail_resources(p.tail))


def _pprop(p):
    return sum(a * RESOURCES[r][0] for r, a, _ in tail_resources(p.tail)
               if r in ("LiquidFuel", "Oxidizer", "SolidFuel"))


def mun_layout(upper=MUN_UPPER_TANKS, core=MUN_CORE_TANKS,
               dec_from_bottom=MUN_DECOUPLER_TANK_FROM_BOTTOM, upper_names=None,
               uid_base=4270000001):
    """Every part of CS Mun Flyby, root first, with positions, links and attN.
    ``upper_names`` (the upper stage's tank part names, top first; None = ``upper``
    FL-T200s) and ``uid_base`` are the munsci variant's; their defaults derive CS
    Mun Flyby."""
    uid = iter(range(uid_base, uid_base + 99))
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
                 istg=MUN_STAGE_CHUTE, dstg=0, sidx=0)
    chute.parent = pod
    pod.links.append(chute.uid)
    shield = Part("shield", "HeatShield1", next(uid),
                  pod.y + off["mk1pod.v2"]["bottom"] - off["HeatShield1"]["top"], dstg=1)
    shield.parent = pod
    pod.links.append(shield.uid)
    pod.attn = [("bottom", shield.uid, off["mk1pod.v2"]["bottom"]),
                ("top", chute.uid, off["mk1pod.v2"]["top"])]
    chute.attn = [("bottom", pod.uid, off["parachuteSingle"]["bottom"])]
    dec1 = below(shield, "direct", "dec1", "Decoupler.1", "top",
                 istg=MUN_STAGE_POD_SEPARATION, dstg=3, sidx=0, group="upper")
    shield.attn.append(("top", pod.uid, off["HeatShield1"]["top"]))
    prev, pnode = dec1, "bottom"
    upper_tanks = []
    for i, name in enumerate(upper_names or ["fuelTankSmall"] * upper):
        t = below(prev, pnode, "utank%d" % i, name, "top",
                  istg=MUN_STAGE_POD_SEPARATION, dstg=4, group="upper")
        upper_tanks.append(t)
        prev, pnode = t, "bottom"
    swivel_u = below(prev, pnode, "swivelUpper", "liquidEngine2", "top",
                     istg=MUN_STAGE_CORE_DROP, dstg=4, sidx=1, group="upper")
    dec2 = below(swivel_u, "bottom", "dec2", "Decoupler.1", "top",
                 istg=MUN_STAGE_CORE_DROP, dstg=5, sidx=0, group="core")
    prev, pnode = dec2, "bottom"
    core_tanks = []
    for i in range(core):
        t = below(prev, pnode, "ctank%d" % i, "fuelTankSmall", "top",
                  istg=MUN_STAGE_CORE_DROP, dstg=6, group="core")
        core_tanks.append(t)
        prev, pnode = t, "bottom"
    swivel_c = below(prev, pnode, "swivelCore", "liquidEngine2", "top",
                     istg=MUN_STAGE_LAUNCH, dstg=6, sidx=1, group="core")
    swivel_c.tail = "liquidEngine2@core"
    anchor = core_tanks[len(core_tanks) - 1 - dec_from_bottom]
    decs, boosters, cones = [], [], []
    for i, az in enumerate(MUN_AZIMUTHS):
        a = math.radians(az)
        psi_d = 180.0 - az
        d = Part("rdec%d" % i, "radialDecoupler", next(uid), anchor.y,
                 istg=MUN_STAGE_BOOSTER_DROP, dstg=7, sidx=0, group="core")
        d.pos = (MUN_DEC_RADIUS * math.cos(a), anchor.y, MUN_DEC_RADIUS * math.sin(a))
        d.rot = _quat_y(psi_d)
        d.attm = 1
        d.srfn = anchor.uid
        d.parent = anchor
        anchor.links.append(d.uid)
        off_w = _yaw_vec(psi_d, MUN_BOOSTER_LOCAL)
        b = Part("thumper%d" % i, "solidBooster1-1", next(uid), d.pos[1] + off_w[1],
                 istg=MUN_STAGE_LAUNCH, dstg=8, sidx=0, group="boosters")
        b.pos = tuple(c + o for c, o in zip(d.pos, off_w))
        b.rot = _quat_y(psi_d + MUN_BOOSTER_LOCAL_PSI)
        b.attm = 1
        b.srfn = d.uid
        b.parent = d
        b.att_pos0 = MUN_BOOSTER_LOCAL
        b.att_rot0 = _quat_y(MUN_BOOSTER_LOCAL_PSI)
        d.links.append(b.uid)
        c = Part("cone%d" % i, "noseCone", next(uid),
                 b.pos[1] + off["solidBooster1-1"]["top"] - off["noseCone"]["bottom01"],
                 istg=MUN_STAGE_BOOSTER_DROP, dstg=8, group="boosters")
        c.pos = (b.pos[0], c.y, b.pos[2])
        c.rot = b.rot
        c.parent = b
        c.att_rot0 = "0,0,0,1"
        b.links.append(c.uid)
        b.attn.append(("top", c.uid, off["solidBooster1-1"]["top"]))
        c.attn.append(("bottom01", b.uid, off["noseCone"]["bottom01"]))
        decs.append(d)
        boosters.append(b)
        cones.append(c)
    for group in (decs, boosters, cones):
        for p in group:
            p.syms = [o.uid for o in group if o is not p]
    return ([pod, chute, shield, dec1] + upper_tanks + [swivel_u, dec2] + core_tanks
            + [swivel_c] + decs + boosters + cones)


def mun_stack_height(parts):
    pod = parts[0]
    swivel_c = [p for p in parts if p.key == "swivelCore"][0]
    top = pod.y + NODE_OFFSET["mk1pod.v2"]["top"] + CHUTE_ALLOWANCE
    bottoms = [swivel_c.y + NODE_OFFSET["liquidEngine2"]["bottom"]]
    bottoms += [p.y + NODE_OFFSET["solidBooster1-1"]["bottom"]
                for p in parts if p.name == "solidBooster1-1"]
    return top - min(bottoms)


def mun_build():
    parts = mun_layout()
    xs = [p.pos[0] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % MUN_SHIP_NAME,
        "version = 1.12.5",
        "description = CS-5 craft (scripted campaign, Career): Mk1 pod, heat shield, "
        "%d FL-T200 on a Swivel over %d FL-T200 on a Swivel core, two Thumpers. Built by "
        "construction by harness/tools/build_cs_orbiter_craft.py --variant mun."
        % (MUN_UPPER_TANKS, MUN_CORE_TANKS),
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 1.25, mun_stack_height(parts),
                           max(zs) - min(zs) + 1.25),
        "steamPublishedFileId = 0",
        "persistentId = 2600550088",
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    body = [render_part(p, i) for i, p in enumerate(parts)]
    return "\n".join(header) + "\n" + "\n".join(body) + "\n"


def mun_figures(upper=MUN_UPPER_TANKS, core=MUN_CORE_TANKS, parts=None):
    """Mass, cost, height and the per-stage delta-v: the launch stage burns the
    Thumpers (at their tail's thrust limit) and the core Swivel together, at
    sea-level Isp; the core then finishes alone, then the upper stage, both in
    vacuum. KSP's thrust limiter scales the propellant flow with the thrust.
    ``parts`` (the munsci variant's layout) replaces the derived CS Mun Flyby."""
    parts = parts or mun_layout(upper, core)
    mass = sum(_pmass(p) for p in parts)
    cost = sum(_pcost(p) for p in parts)

    def grp(name):
        return [p for p in parts if p.group == name]

    def prop(ps):
        return sum(_pprop(p) for p in ps)

    def dry(ps):
        return sum(_pmass(p) - _pprop(p) for p in ps)

    sw = PART_CFG["liquidEngine2"]
    th = PART_CFG["solidBooster1-1"]
    frac = _thrust_fraction("solidBooster1-1")
    n_b = len([p for p in parts if p.name == "solidBooster1-1"])
    f_b = th["thrust"] * frac
    mdot_b = f_b / (th["isp_vac"] * G0)
    mdot_c = sw["thrust"] / (sw["isp_vac"] * G0)
    boosters, core_ps, upper_ps = grp("boosters"), grp("core"), grp("upper")
    burn_b = (prop(boosters) / n_b) / mdot_b
    core_used = min(prop(core_ps), mdot_c * burn_b)
    f_asl = n_b * f_b * th["isp_asl"] / th["isp_vac"] + sw["thrust"] * sw["isp_asl"] / sw["isp_vac"]
    f_vac = n_b * f_b + sw["thrust"]
    mdot = n_b * mdot_b + mdot_c
    stages = []
    m0 = mass
    m1 = m0 - prop(boosters) - core_used
    stages.append({"stage": "S4 Thumpers+core", "m0": m0, "m1": m1,
                   "dv_asl": f_asl / (mdot * G0) * G0 * math.log(m0 / m1),
                   "dv_vac": f_vac / (mdot * G0) * G0 * math.log(m0 / m1),
                   "twr_asl": f_asl / (m0 * G0), "burn_s": burn_b})
    m0 = m1 - dry(boosters)
    m1 = m0 - (prop(core_ps) - core_used)
    stages.append({"stage": "S2 core Swivel", "m0": m0, "m1": m1,
                   "dv_asl": sw["isp_asl"] * G0 * math.log(m0 / m1),
                   "dv_vac": sw["isp_vac"] * G0 * math.log(m0 / m1),
                   "twr_asl": sw["thrust"] * sw["isp_asl"] / sw["isp_vac"] / (m0 * G0),
                   "burn_s": (prop(core_ps) - core_used) / mdot_c})
    m0 = m1 - dry(core_ps)
    m1 = m0 - prop(upper_ps)
    stages.append({"stage": "S1 upper Swivel", "m0": m0, "m1": m1,
                   "dv_asl": sw["isp_asl"] * G0 * math.log(m0 / m1),
                   "dv_vac": sw["isp_vac"] * G0 * math.log(m0 / m1),
                   "twr_vac": sw["thrust"] / (m0 * G0),
                   "burn_s": prop(upper_ps) / mdot_c})
    return {
        "parts": len(parts), "mass": mass, "cost": cost,
        "height": mun_stack_height(parts), "stages": stages,
        "dv_total": stages[0]["dv_asl"] + stages[1]["dv_vac"] + stages[2]["dv_vac"],
        "dv_total_vac": sum(s["dv_vac"] for s in stages),
        "dv_upper": stages[2]["dv_vac"],
        "liftoff_twr": stages[0]["twr_asl"],
        "booster_thrust_fraction": frac,
        "reentry_mass": sum(_pmass(p) for p in grp("pod")),
    }


def mun_problems():
    """Every post-condition of CS Mun Flyby, as strings (empty = good)."""
    out = []
    parts = mun_layout()
    f = mun_figures()
    if f["parts"] > MUN_LIMIT_PARTS:
        out.append("%d parts > the level-0 VAB limit %d" % (f["parts"], MUN_LIMIT_PARTS))
    if f["mass"] > MUN_LIMIT_MASS_T:
        out.append("%.3f t > the level-1 pad limit %.0f t" % (f["mass"], MUN_LIMIT_MASS_T))
    if f["height"] > MUN_LIMIT_HEIGHT_M:
        out.append("%.2f m > the level-1 pad height %.0f m"
                   % (f["height"], MUN_LIMIT_HEIGHT_M))
    if f["dv_total"] < MUN_MIN_DELTA_V:
        out.append("delta-v %.0f < %.0f" % (f["dv_total"], MUN_MIN_DELTA_V))
    if f["dv_upper"] < MUN_MIN_UPPER_DELTA_V:
        out.append("upper delta-v %.0f < %.0f" % (f["dv_upper"], MUN_MIN_UPPER_DELTA_V))
    if f["liftoff_twr"] < MUN_MIN_LIFTOFF_TWR:
        out.append("liftoff TWR %.2f < %.2f" % (f["liftoff_twr"], MUN_MIN_LIFTOFF_TWR))
    if stage_table(parts) != mun_stage_want():
        out.append("stage order %r != %r" % (stage_table(parts), mun_stage_want()))
    shield = [p for p in parts if p.name == "HeatShield1"][0]
    if shield.istg != -1:
        out.append("the heat shield must not be staged")
    for key in ("liquidEngine2", "liquidEngine2@core"):
        if _thrust_fraction(key) != 1.0:
            out.append("%s donor is not at thrustPercentage 100" % key)
    uids = [p.uid for p in parts]
    if len(set(uids)) != len(uids):
        out.append("duplicate part uid")
    for p in parts:
        for ref in p.links + [o for _, o, _ in p.attn] + ([p.srfn] if p.srfn else []):
            if ref not in uids:
                out.append("%s references undeclared %s" % (p.uid, ref))
    return out


def mun_stage_want():
    return {MUN_STAGE_LAUNCH: ["liquidEngine2", "solidBooster1-1", "solidBooster1-1"],
            MUN_STAGE_BOOSTER_DROP: ["radialDecoupler", "radialDecoupler"],
            MUN_STAGE_CORE_DROP: ["Decoupler.1", "liquidEngine2"],
            MUN_STAGE_POD_SEPARATION: ["Decoupler.1"],
            MUN_STAGE_CHUTE: ["parachuteSingle"]}


def mun_check():
    """[] when the committed CS Mun Flyby is byte-identical to a fresh derivation
    and every post-condition holds, else the problems."""
    out = mun_problems()
    if not os.path.exists(MUN_OUT):
        return out + ["MISSING %s - run --variant mun --write" % MUN_OUT]
    with open(MUN_OUT, encoding="utf-8", newline="") as fh:
        have = fh.read()
    if have != mun_build():
        out.append("DRIFT: %s differs from the derivation" % MUN_OUT)
    return out


def mun_report():
    f = mun_figures()
    print("%s: %d parts, %.3f t, %.2f m, cost %.0f (Thumpers at %.0f%%)"
          % (MUN_SHIP_NAME, f["parts"], f["mass"], f["height"], f["cost"],
             100.0 * f["booster_thrust_fraction"]))
    for s in f["stages"]:
        twr = s.get("twr_asl", s.get("twr_vac"))
        print("  %-18s m0 %.3f t -> m1 %.3f t  dv vac %.0f m/s (ASL %.0f)  TWR %.2f  "
              "burn %.0f s" % (s["stage"], s["m0"], s["m1"], s["dv_vac"], s["dv_asl"],
                               twr, s["burn_s"]))
    print("  total dv %.0f m/s (launch stage at ASL Isp), %.0f m/s all vacuum"
          % (f["dv_total"], f["dv_total_vac"]))
    print("  liftoff TWR %.3f (sea level); reentry mass %.3f t"
          % (f["liftoff_twr"], f["reentry_mass"]))



# ---------------------------------------------------------------------------
# THE LANDER VARIANT (`--variant lander`): `CS Mun Lander`, CS-7's craft.
#
# It flies from `cs-career-mun2` (CS-6's produced save) after the KSC walk researches
# Advanced Rocketry (45 science; the host holds 50.5) and upgrades the Astronaut Complex
# (EVA away from Kerbin and flags need it): every part is on `start`, `basicRocketry`,
# `engineering101`, `survivability`, `generalRocketry`, `stability` or `advRocketry`.
# The limits are CS-5's: VAB level 0 (30 parts), Launch Pad level 1 (140 t, 36 m).
#
# THE STACK, top to bottom (CS Mun Flyby's layout with a Terrier lander on top):
#     Mk16 parachute            stage 0 (the return tail deploys it)
#     Mk1 pod (root)
#     Heat Shield 1.25 m        staging DISABLED (as on CS Orbiter)
#     TD-12                     stage 1: drops the lander (the return's one owed stage)
#     2 x FL-T400 + 1 x FL-T200 the lander's tanks
#     3 x LT-05 on the FL-T200  never staged; MechJeb's landing extends them (Gear)
#     LV-909 Terrier            stage 2, with the TD-12 below it (hot stage)
#     TD-12                     stage 2: drops the spent core
#     7 x FL-T400
#     LV-T45 Swivel (core)      stage 4 (the launch)
#     3 x TT-38K on the core    stage 3: drops the Thumpers
#     3 x BACC Thumper + cones  stage 4 (the launch), thrust limited to 60%
# MechJeb's autostage is limited to stage 2, so it never fires stage 1, on Kerbin or on
# the Mun.
#
# WHY A TERRIER. The lander must stand on its legs: the LT-05 (the only leg on the
# researched tech; the LT-1 is on `landing`, 90 science behind flightControl) reaches
# about 0.8 m below its attach point (suspensionDistance 0.8), the Swivel's bell hangs
# 1.62 m below its tank and the Terrier's 0.82 m. The Terrier is also 1 t lighter, at
# Isp 345 in vacuum against 320. The legs ride the FL-T200 0.06 m above its bottom
# edge; if they still stop short of the bell, they stand as outriggers round it.
# ELECTRIC CHARGE: the Terrier has no alternator and nothing on the tech makes power,
# so from the core drop on the craft lives on the pod's 50 EC (CS-6's flown telemetry
# read at most a 2 EC dip per MechJeb attitude hold). The mission watches it.
#
# DONORS, by stored state: the Terrier and the FL-T400 from AeroEquus (1.12.0; the
# Terrier at thrustPercentage 100 with its shroud on and a TD-12 below it, exactly this
# stack), the LT-05 from Making History's Acapello (1.12.0, the only KSP-written LT-05
# in the install; its Gear action group is what MechJeb's landing fires). Every other
# tail is CS Mun Flyby's. The legs use the fins' +X surface family (the LT-05's attach
# normal is +X like the fin's and the TT-38K's): yaw 180 - azimuth, the attach point
# 0.0257 m out and 0.0514 m up in the leg's own frame (its cfg node_attach).
#
# ASSUMPTIONS A FLIGHT CAN FALSIFY: whether the LT-05s reach below the Terrier's bell,
# the pod's 50 EC across the Mun legs, whether MechJeb's classic ascent flies the
# 3-Thumper stack (liftoff TWR about 1.3, as CS Mun Flyby) and the Mun ascent, and the
# crew's mass (not in the figures).
# ---------------------------------------------------------------------------

LANDER_SHIP_NAME = "CS Mun Lander"
LANDER_OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-career-mun2",
                          "Ships", "VAB", "CS Mun Lander.craft")
LANDER_UPPER_T400 = 2
LANDER_UPPER_T200 = 1
LANDER_CORE_TANKS = 7
# The core FL-T400 the TT-38Ks ride, counted from the BOTTOM: the one that puts the
# Thumper nozzles just above the core Swivel's (0.17 m).
LANDER_DECOUPLER_TANK_FROM_BOTTOM = 1
LANDER_BOOSTER_AZIMUTHS = (0.0, 120.0, 240.0)
LANDER_LEG_AZIMUTHS = (60.0, 180.0, 300.0)
# The LT-05's own attach point (landingLegLT-5.cfg node_attach), in its frame.
LANDER_LEG_NODE = (0.0256775, 0.05144107, 0.0)
# The FL-T400 / FL-T200 wall radius, and how far above the FL-T200's bottom edge the
# legs attach.
LANDER_TANK_RADIUS = 0.625
LANDER_LEG_ABOVE_BOTTOM = 0.06

# The CS-7 budget. Lower stages: about 3130 m/s to a low Kerbin orbit on this basis
# (CS-6 flew CS Mun Flyby's 2707 m/s lower stages plus about 420 m/s of its upper Swivel
# to orbit). The lander from orbit: the trans-Munar injection 860, corrections up to
# 300, the capture 280, the descent 700, the ascent 650, the departure 220 (CS-6
# measured 216.9) and a return trim 100: about 3110, plus margin.
LANDER_MIN_DELTA_V = 6600.0
LANDER_MIN_LOWER_DELTA_V = 3130.0
LANDER_MIN_UPPER_DELTA_V = 3500.0
LANDER_MIN_LIFTOFF_TWR = 1.25
LANDER_MIN_MUN_TWR = 3.0
MUN_SURFACE_G = 1.63

LANDER_STAGE_LAUNCH = 4          # core Swivel + the three Thumpers
LANDER_STAGE_BOOSTER_DROP = 3    # the three TT-38Ks
LANDER_STAGE_CORE_DROP = 2       # TD-12 (drops the core) + the Terrier
LANDER_STAGE_POD_SEPARATION = 1  # TD-12 under the heat shield
LANDER_STAGE_CHUTE = 0

NODE_OFFSET.update({
    # AeroEquus: attN = top,...|0.981725|0 and attN = bottom,...|-0.9125|0
    "fuelTank": {"top": 0.981725, "bottom": -0.9125},
    # AeroEquus: attN = top,fuelTank...|0|0 and attN = bottom,Decoupler.1...|-0.81781|0
    "liquidEngine3.v2": {"top": 0.0, "bottom": -0.81781},
})

PART_CFG.update({
    "fuelTank": {"mass": 0.25, "cost": 500},             # FuelTank/Size1_Tanks/fuelTankT400
    "liquidEngine3.v2": {"mass": 0.5, "cost": 390,       # Engine/liquidEngineLV-909_v2
                         "thrust": 60.0, "isp_vac": 345.0, "isp_asl": 85.0},
    "miniLandingLeg": {"mass": 0.015, "cost": 200},      # Utility/landingLegLT-5
})

LANDER_DONORS = {
    "fuelTank": ("VAB/AeroEquus.craft", 0),
    "liquidEngine3.v2": ("VAB/AeroEquus.craft", 0),
    "miniLandingLeg": ("../GameData/SquadExpansion/MakingHistory/Ships/VAB/Acapello.craft", 0),
}
LANDER_TAILS = {
    # <- Ships/VAB/AeroEquus.craft, occurrence 0  [stock 1.12.0, KSP-written]
    'fuelTank': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tselectedVariant = BlackAndWhite\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 180\n\t\tmaxAmount = 180\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 220\n\t\tmaxAmount = 220\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- Ships/VAB/AeroEquus.craft, occurrence 0  [stock 1.12.0, KSP-written]
    'liquidEngine3.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = ShortShroud\n\t\tisJettisoned = False\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- GameData/SquadExpansion/MakingHistory/Ships/VAB/Acapello.craft, occurrence 0  [stock 1.12.0, KSP-written]
    'miniLandingLeg': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelBase\n\t\tisEnabled = True\n\t\twheelType = LEG\n\t\tisGrounded = False\n\t\tautoFriction = False\n\t\tfrictionMultiplier = 1.10000002\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tActAutoFrictionToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelSuspension\n\t\tisEnabled = True\n\t\tspringTweakable = 1\n\t\tdamperTweakable = 1\n\t\tautoSpringDamper = True\n\t\tsuspensionPos = (-1, -1, -1)\n\t\tautoBoost = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelDeployment\n\t\tisEnabled = True\n\t\tshieldedCanDeploy = False\n\t\tstateDisplayString = Retracted\n\t\tstateString = Retracted\n\t\tstagingEnabled = True\n\t\tposition = 0\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tActionToggle\n\t\t\t{\n\t\t\t\tactionGroup = Gear\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelLock\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleWheelDamage\n\t\tisEnabled = True\n\t\tisDamaged = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}

DONORS.update(LANDER_DONORS)
TAILS.update(LANDER_TAILS)


def lander_layout(upper400=LANDER_UPPER_T400, upper200=LANDER_UPPER_T200,
                  core=LANDER_CORE_TANKS, dec_from_bottom=LANDER_DECOUPLER_TANK_FROM_BOTTOM,
                  uid_base=4280000001, cones=True, leg_azimuths=LANDER_LEG_AZIMUTHS):
    """Every part of CS Mun Lander, root first, with positions, links and attN.
    ``uid_base``, ``cones`` (False: the Thumpers fly without nose cones) and
    ``leg_azimuths`` are the solar variant's; their defaults derive CS Mun Lander."""
    uid = iter(range(uid_base, uid_base + 99))
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
                 istg=LANDER_STAGE_CHUTE, dstg=0, sidx=0)
    chute.parent = pod
    pod.links.append(chute.uid)
    shield = Part("shield", "HeatShield1", next(uid),
                  pod.y + off["mk1pod.v2"]["bottom"] - off["HeatShield1"]["top"], dstg=1)
    shield.parent = pod
    pod.links.append(shield.uid)
    pod.attn = [("bottom", shield.uid, off["mk1pod.v2"]["bottom"]),
                ("top", chute.uid, off["mk1pod.v2"]["top"])]
    chute.attn = [("bottom", pod.uid, off["parachuteSingle"]["bottom"])]
    dec1 = below(shield, "direct", "dec1", "Decoupler.1", "top",
                 istg=LANDER_STAGE_POD_SEPARATION, dstg=3, sidx=0, group="upper")
    shield.attn.append(("top", pod.uid, off["HeatShield1"]["top"]))
    prev, pnode = dec1, "bottom"
    upper_tanks = []
    for i, name in enumerate(["fuelTank"] * upper400 + ["fuelTankSmall"] * upper200):
        t = below(prev, pnode, "utank%d" % i, name, "top",
                  istg=LANDER_STAGE_POD_SEPARATION, dstg=4, group="upper")
        upper_tanks.append(t)
        prev, pnode = t, "bottom"
    leg_tank = upper_tanks[-1]
    terrier = below(prev, pnode, "terrier", "liquidEngine3.v2", "top",
                    istg=LANDER_STAGE_CORE_DROP, dstg=4, sidx=1, group="upper")
    surface_y = leg_tank.y + off[leg_tank.name]["bottom"] + LANDER_LEG_ABOVE_BOTTOM
    legs = []
    for i, az in enumerate(leg_azimuths):
        a = math.radians(az)
        psi = 180.0 - az
        node_w = _yaw_vec(psi, LANDER_LEG_NODE)
        leg = Part("leg%d" % i, "miniLandingLeg", next(uid), surface_y - node_w[1],
                   istg=LANDER_STAGE_POD_SEPARATION, dstg=4, group="upper")
        leg.pos = (LANDER_TANK_RADIUS * math.cos(a) - node_w[0], surface_y - node_w[1],
                   LANDER_TANK_RADIUS * math.sin(a) - node_w[2])
        leg.rot = _quat_y(psi)
        leg.attm = 1
        leg.srfn = leg_tank.uid
        leg.parent = leg_tank
        leg_tank.links.append(leg.uid)
        legs.append(leg)
    for leg in legs:
        leg.syms = [o.uid for o in legs if o is not leg]
    dec2 = below(terrier, "bottom", "dec2", "Decoupler.1", "top",
                 istg=LANDER_STAGE_CORE_DROP, dstg=5, sidx=0, group="core")
    prev, pnode = dec2, "bottom"
    core_tanks = []
    for i in range(core):
        t = below(prev, pnode, "ctank%d" % i, "fuelTank", "top",
                  istg=LANDER_STAGE_CORE_DROP, dstg=6, group="core")
        core_tanks.append(t)
        prev, pnode = t, "bottom"
    swivel_c = below(prev, pnode, "swivelCore", "liquidEngine2", "top",
                     istg=LANDER_STAGE_LAUNCH, dstg=6, sidx=1, group="core")
    swivel_c.tail = "liquidEngine2@core"
    anchor = core_tanks[len(core_tanks) - 1 - dec_from_bottom]
    decs, boosters, cone_parts = [], [], []
    for i, az in enumerate(LANDER_BOOSTER_AZIMUTHS):
        a = math.radians(az)
        psi_d = 180.0 - az
        d = Part("rdec%d" % i, "radialDecoupler", next(uid), anchor.y,
                 istg=LANDER_STAGE_BOOSTER_DROP, dstg=7, sidx=0, group="core")
        d.pos = (MUN_DEC_RADIUS * math.cos(a), anchor.y, MUN_DEC_RADIUS * math.sin(a))
        d.rot = _quat_y(psi_d)
        d.attm = 1
        d.srfn = anchor.uid
        d.parent = anchor
        anchor.links.append(d.uid)
        off_w = _yaw_vec(psi_d, MUN_BOOSTER_LOCAL)
        b = Part("thumper%d" % i, "solidBooster1-1", next(uid), d.pos[1] + off_w[1],
                 istg=LANDER_STAGE_LAUNCH, dstg=8, sidx=0, group="boosters")
        b.pos = tuple(c + o for c, o in zip(d.pos, off_w))
        b.rot = _quat_y(psi_d + MUN_BOOSTER_LOCAL_PSI)
        b.attm = 1
        b.srfn = d.uid
        b.parent = d
        b.att_pos0 = MUN_BOOSTER_LOCAL
        b.att_rot0 = _quat_y(MUN_BOOSTER_LOCAL_PSI)
        d.links.append(b.uid)
        decs.append(d)
        boosters.append(b)
        if not cones:
            continue
        c = Part("cone%d" % i, "noseCone", next(uid),
                 b.pos[1] + off["solidBooster1-1"]["top"] - off["noseCone"]["bottom01"],
                 istg=LANDER_STAGE_BOOSTER_DROP, dstg=8, group="boosters")
        c.pos = (b.pos[0], c.y, b.pos[2])
        c.rot = b.rot
        c.parent = b
        c.att_rot0 = "0,0,0,1"
        b.links.append(c.uid)
        b.attn.append(("top", c.uid, off["solidBooster1-1"]["top"]))
        c.attn.append(("bottom01", b.uid, off["noseCone"]["bottom01"]))
        cone_parts.append(c)
    for group in (decs, boosters, cone_parts):
        for p in group:
            p.syms = [o.uid for o in group if o is not p]
    return ([pod, chute, shield, dec1] + upper_tanks + legs + [terrier, dec2] + core_tanks
            + [swivel_c] + decs + boosters + cone_parts)


def lander_stack_height(parts):
    pod = parts[0]
    swivel_c = [p for p in parts if p.key == "swivelCore"][0]
    top = pod.y + NODE_OFFSET["mk1pod.v2"]["top"] + CHUTE_ALLOWANCE
    bottoms = [swivel_c.y + NODE_OFFSET["liquidEngine2"]["bottom"]]
    bottoms += [p.y + NODE_OFFSET["solidBooster1-1"]["bottom"]
                for p in parts if p.name == "solidBooster1-1"]
    return top - min(bottoms)


def lander_leg_clearance(parts=None):
    """How far (m) the Terrier's bottom node hangs below the legs' attach points. The
    LT-05 reaches about its suspensionDistance (0.8 m) below its attach point, so a
    clearance under that lets the legs touch down before the bell."""
    parts = parts or lander_layout()
    terrier = [p for p in parts if p.name == "liquidEngine3.v2"][0]
    leg = [p for p in parts if p.name == "miniLandingLeg"][0]
    attach_y = leg.pos[1] + _yaw_vec(180.0, LANDER_LEG_NODE)[1]
    return attach_y - (terrier.y + NODE_OFFSET["liquidEngine3.v2"]["bottom"])


def lander_build():
    parts = lander_layout()
    xs = [p.pos[0] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % LANDER_SHIP_NAME,
        "version = 1.12.5",
        "description = CS-7 craft (scripted campaign, Career): Mk1 pod, heat shield, "
        "%d FL-T400 and %d FL-T200 on a Terrier with three LT-05 legs, over %d FL-T400 "
        "on a Swivel core, three Thumpers. Built by construction by "
        "harness/tools/build_cs_orbiter_craft.py --variant lander."
        % (LANDER_UPPER_T400, LANDER_UPPER_T200, LANDER_CORE_TANKS),
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 1.25, lander_stack_height(parts),
                           max(zs) - min(zs) + 1.25),
        "steamPublishedFileId = 0",
        "persistentId = 2600660099",
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    body = [render_part(p, i) for i, p in enumerate(parts)]
    return "\n".join(header) + "\n" + "\n".join(body) + "\n"


def lander_figures(parts=None):
    """Mass, cost, height and the per-stage delta-v, on mun_figures' model: the launch
    stage burns the Thumpers (at their tail's thrust limit) and the core Swivel together
    at sea-level Isp, the core finishes alone in vacuum, then the Terrier lander.
    ``parts`` (the solar variant's layout) replaces the derived CS Mun Lander."""
    parts = parts or lander_layout()
    mass = sum(_pmass(p) for p in parts)
    cost = sum(_pcost(p) for p in parts)

    def grp(name):
        return [p for p in parts if p.group == name]

    def prop(ps):
        return sum(_pprop(p) for p in ps)

    def dry(ps):
        return sum(_pmass(p) - _pprop(p) for p in ps)

    sw = PART_CFG["liquidEngine2"]
    te = PART_CFG["liquidEngine3.v2"]
    th = PART_CFG["solidBooster1-1"]
    frac = _thrust_fraction("solidBooster1-1")
    n_b = len([p for p in parts if p.name == "solidBooster1-1"])
    f_b = th["thrust"] * frac
    mdot_b = f_b / (th["isp_vac"] * G0)
    mdot_c = sw["thrust"] / (sw["isp_vac"] * G0)
    boosters, core_ps, upper_ps = grp("boosters"), grp("core"), grp("upper")
    burn_b = (prop(boosters) / n_b) / mdot_b
    core_used = min(prop(core_ps), mdot_c * burn_b)
    f_asl = (n_b * f_b * th["isp_asl"] / th["isp_vac"]
             + sw["thrust"] * sw["isp_asl"] / sw["isp_vac"])
    f_vac = n_b * f_b + sw["thrust"]
    mdot = n_b * mdot_b + mdot_c
    stages = []
    m0 = mass
    m1 = m0 - prop(boosters) - core_used
    stages.append({"stage": "S4 Thumpers+core", "m0": m0, "m1": m1,
                   "dv_asl": f_asl / (mdot * G0) * G0 * math.log(m0 / m1),
                   "dv_vac": f_vac / (mdot * G0) * G0 * math.log(m0 / m1),
                   "twr_asl": f_asl / (m0 * G0), "burn_s": burn_b})
    m0 = m1 - dry(boosters)
    m1 = m0 - (prop(core_ps) - core_used)
    stages.append({"stage": "S2 core Swivel", "m0": m0, "m1": m1,
                   "dv_asl": sw["isp_asl"] * G0 * math.log(m0 / m1),
                   "dv_vac": sw["isp_vac"] * G0 * math.log(m0 / m1),
                   "twr_vac": sw["thrust"] / (m0 * G0),
                   "burn_s": (prop(core_ps) - core_used) / mdot_c})
    m0 = m1 - dry(core_ps)
    m1 = m0 - prop(upper_ps)
    stages.append({"stage": "S1 Terrier lander", "m0": m0, "m1": m1,
                   "dv_asl": te["isp_asl"] * G0 * math.log(m0 / m1),
                   "dv_vac": te["isp_vac"] * G0 * math.log(m0 / m1),
                   "twr_vac": te["thrust"] / (m0 * G0),
                   "burn_s": prop(upper_ps) / (te["thrust"] / (te["isp_vac"] * G0))})
    return {
        "parts": len(parts), "mass": mass, "cost": cost,
        "height": lander_stack_height(parts), "stages": stages,
        "dv_total": stages[0]["dv_asl"] + stages[1]["dv_vac"] + stages[2]["dv_vac"],
        "dv_total_vac": sum(s["dv_vac"] for s in stages),
        "dv_lower": stages[0]["dv_asl"] + stages[1]["dv_vac"],
        "dv_upper": stages[2]["dv_vac"],
        "liftoff_twr": stages[0]["twr_asl"],
        "mun_twr": te["thrust"] / (stages[2]["m0"] * MUN_SURFACE_G),
        "booster_thrust_fraction": frac,
        "leg_clearance": lander_leg_clearance(parts),
        "reentry_mass": sum(_pmass(p) for p in grp("pod")),
    }


def lander_stage_want():
    return {LANDER_STAGE_LAUNCH: ["liquidEngine2", "solidBooster1-1", "solidBooster1-1",
                                 "solidBooster1-1"],
            LANDER_STAGE_BOOSTER_DROP: ["radialDecoupler"] * 3,
            LANDER_STAGE_CORE_DROP: ["Decoupler.1", "liquidEngine3.v2"],
            LANDER_STAGE_POD_SEPARATION: ["Decoupler.1"],
            LANDER_STAGE_CHUTE: ["parachuteSingle"]}


def lander_problems():
    """Every post-condition of CS Mun Lander, as strings (empty = good)."""
    out = []
    parts = lander_layout()
    f = lander_figures()
    if f["parts"] > MUN_LIMIT_PARTS:
        out.append("%d parts > the level-0 VAB limit %d" % (f["parts"], MUN_LIMIT_PARTS))
    if f["mass"] > MUN_LIMIT_MASS_T:
        out.append("%.3f t > the level-1 pad limit %.0f t" % (f["mass"], MUN_LIMIT_MASS_T))
    if f["height"] > MUN_LIMIT_HEIGHT_M:
        out.append("%.2f m > the level-1 pad height %.0f m"
                   % (f["height"], MUN_LIMIT_HEIGHT_M))
    for key, floor, label in (("dv_total", LANDER_MIN_DELTA_V, "delta-v"),
                              ("dv_lower", LANDER_MIN_LOWER_DELTA_V, "lower delta-v"),
                              ("dv_upper", LANDER_MIN_UPPER_DELTA_V, "lander delta-v"),
                              ("liftoff_twr", LANDER_MIN_LIFTOFF_TWR, "liftoff TWR"),
                              ("mun_twr", LANDER_MIN_MUN_TWR, "Mun TWR")):
        if f[key] < floor:
            out.append("%s %.2f < %.2f" % (label, f[key], floor))
    if stage_table(parts) != lander_stage_want():
        out.append("stage order %r != %r" % (stage_table(parts), lander_stage_want()))
    shield = [p for p in parts if p.name == "HeatShield1"][0]
    if shield.istg != -1:
        out.append("the heat shield must not be staged")
    for key in ("liquidEngine2@core", "liquidEngine3.v2"):
        if _thrust_fraction(key) != 1.0:
            out.append("%s donor is not at thrustPercentage 100" % key)
    if "actionGroup = Gear" not in TAILS["miniLandingLeg"]:
        out.append("the LT-05 must deploy on the Gear action group (MechJeb's landing)")
    uids = [p.uid for p in parts]
    if len(set(uids)) != len(uids):
        out.append("duplicate part uid")
    for p in parts:
        for ref in p.links + [o for _, o, _ in p.attn] + ([p.srfn] if p.srfn else []):
            if ref not in uids:
                out.append("%s references undeclared %s" % (p.uid, ref))
    return out


def lander_check():
    """[] when the committed CS Mun Lander is byte-identical to a fresh derivation and
    every post-condition holds, else the problems."""
    out = lander_problems()
    if not os.path.exists(LANDER_OUT):
        return out + ["MISSING %s - run --variant lander --write" % LANDER_OUT]
    with open(LANDER_OUT, encoding="utf-8", newline="") as fh:
        have = fh.read()
    if have != lander_build():
        out.append("DRIFT: %s differs from the derivation" % LANDER_OUT)
    return out


def lander_report():
    f = lander_figures()
    print("%s: %d parts, %.3f t, %.2f m, cost %.0f (Thumpers at %.0f%%)"
          % (LANDER_SHIP_NAME, f["parts"], f["mass"], f["height"], f["cost"],
             100.0 * f["booster_thrust_fraction"]))
    for s in f["stages"]:
        twr = s.get("twr_asl", s.get("twr_vac"))
        print("  %-18s m0 %.3f t -> m1 %.3f t  dv vac %.0f m/s (ASL %.0f)  TWR %.2f  "
              "burn %.0f s" % (s["stage"], s["m0"], s["m1"], s["dv_vac"], s["dv_asl"],
                               twr, s["burn_s"]))
    print("  total dv %.0f m/s (launch stage at ASL Isp), lower %.0f, lander %.0f"
          % (f["dv_total"], f["dv_lower"], f["dv_upper"]))
    print("  liftoff TWR %.3f (sea level); Mun TWR %.2f (lander full); leg clearance "
          "%.2f m; reentry mass %.3f t"
          % (f["liftoff_twr"], f["mun_twr"], f["leg_clearance"], f["reentry_mass"]))



# ---------------------------------------------------------------------------
# THE MUN SCIENCE VARIANT (`--variant munsci`): `CS Mun Science`, CS-6c's craft.
#
# It flies from `cs-career-mun3` (CS-6b's produced save): `start`, `basicRocketry`,
# `engineering101`, `survivability`, `generalRocketry`, `stability` and `advRocketry`
# researched, the Launch Pad at level 1 and the VAB at level 0 (30 parts).
#
# THE STACK is CS Mun Flyby's (CS-5 flew it to a Mun flyby, CS-6 to a Mun orbit and
# back), with two edits:
#   - the upper stage's 4 x FL-T200 become 2 x FL-T400 (advRocketry): the same 360
#     LiquidFuel / 440 Oxidizer and the same dry mass, two parts fewer;
#   - the pod carries the science: two Mystery Goo canisters (start) and two 2HOT
#     thermometers (engineering101), surface-attached to the Mk1 pod. Each part holds
#     one reading and the pod's own container takes data only from a boarding
#     kerbal, so one Goo and one thermometer read high Mun space and the other two
#     low Mun space; everything rides the pod through the reentry to the recovery.
# 30 parts, the VAB's limit.
#
# DONORS, by stored state: the Goo tail and its attach from AeroEquus (1.12.0), the
# one stock craft with Goo on a Mk1 pod (its pair at x = +/-0.2997 m, 0.3645 m up the
# pod; AeroEquus also nudged them with the offset gizmo, attPos / attRot, which this
# builder does not carry: the parts sit at the donor's attPos0 / attRot0, the attach
# KSP computed, with the 1e-8 m z noise dropped); the thermometer tail and its attach
# from Science Jr. (1.12.0, on its Mk1 pod at azimuth -30 deg, 0.495 m up); the
# FL-T400 from AeroEquus (CS Mun Lander's donor). The second Goo and the second
# thermometer are the first ones turned 180 deg about the pod axis (position by
# _yaw_vec, rotation by quat_y(180) * donor rotation), a symmetric pair each.
#
# THE HATCH. The Mk1 pod's airlock transform sits at (0.0008, 0.165, -0.581) in the
# pod's frame (Mk1Pod_v2.mu), azimuth -90 deg; a part over it obstructs the EVA. The
# Goo pair sits at 0 / 180 deg and the thermometers at -30 / 150 deg, every one at
# least 60 deg off the hatch (gated below).
#
# ASSUMPTIONS A FLIGHT CAN FALSIFY: that the Goo canisters and thermometers (maxTemp
# 1200 K each) survive a Mun-return reentry on the pod behind the 1.25 m heat shield
# (stock's own AeroEquus mounts its Goo there for an orbital return; the mission's
# scienceKeptToSplashdown row reads it), and CS Mun Flyby's own (see above).
# ---------------------------------------------------------------------------

MUNSCI_SHIP_NAME = "CS Mun Science"
MUNSCI_OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-career-mun3",
                          "Ships", "VAB", "CS Mun Science.craft")
MUNSCI_UPPER_NAMES = ["fuelTank", "fuelTank"]
MUNSCI_UID_BASE = 4290000001
# AeroEquus GooExperiment_4294265902: attPos0 (z noise 8.6e-9 dropped) and attRot0.
MUNSCI_GOO_POS = (0.299666911, 0.364496231, 0.0)
MUNSCI_GOO_ROT = (-0.00661472185, 0.707075894, -0.00661472743, -0.707075834)
# Science Jr. sensorThermometer_4294596382: attPos0 and attRot0.
MUNSCI_THERMO_POS = (0.305041492, 0.495138168, -0.176115841)
MUNSCI_THERMO_ROT = (-0.121066101, 0.495090187, -0.0698975325, -0.857521474)
# The Mk1 pod's airlock (Mk1Pod_v2.mu transform `airlock`), and the least azimuth a
# science part keeps from it.
MUNSCI_HATCH_POS = (0.0008, 0.165, -0.581)
MUNSCI_HATCH_CLEARANCE_DEG = 45.0
# The two runs' parts: two Goo and two thermometers.
MUNSCI_SCIENCE_PARTS = {"GooExperiment": 2, "sensorThermometer": 2}

PART_CFG.update({
    "GooExperiment": {"mass": 0.05, "cost": 800},        # Science/GooExperiment
    "sensorThermometer": {"mass": 0.005, "cost": 900},   # Science/sensorThermometer
})

MUNSCI_DONORS = {
    "GooExperiment": ("VAB/AeroEquus.craft", 0),
    "sensorThermometer": ("VAB/Science Jr.craft", 0),
}
MUNSCI_TAILS = {
    # <- Ships/VAB/AeroEquus.craft, occurrence 0  [stock 1.12.0, KSP-written]
    "GooExperiment": '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAnimateGeneric\n\t\tisEnabled = True\n\t\taniState = LOCKED\n\t\tanimSwitch = True\n\t\tanimTime = 0\n\t\tanimSpeed = 1\n\t\tdeployPercent = 100\n\t\tanimationIsDisabled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Ships/VAB/Science Jr.craft, occurrence 0  [stock 1.12.0, KSP-written]
    "sensorThermometer": '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEnviroSensor\n\t\tisEnabled = True\n\t\tsensorActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}

DONORS.update(MUNSCI_DONORS)
TAILS.update(MUNSCI_TAILS)


def _quat_mul(a, b):
    """Hamilton product a * b of two (x, y, z, w) quaternions (Unity's operator *)."""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def _quat_tuple_y(psi_deg):
    h = math.radians(psi_deg) / 2.0
    return (0.0, math.sin(h), 0.0, math.cos(h))


def _quat_str(q):
    return ",".join(_fmt(round(c, 9)) for c in q)


def _azimuth_deg(pos):
    """The builder's azimuth of a pod-frame position: atan2(z, x), the convention the
    radial parts' (r cos a, y, r sin a) placement uses."""
    return math.degrees(math.atan2(pos[2], pos[0]))


def munsci_layout():
    """Every part of CS Mun Science, root first: CS Mun Flyby's layout with the
    FL-T400 upper stage, then the four science parts on the pod."""
    parts = mun_layout(upper_names=MUNSCI_UPPER_NAMES, uid_base=MUNSCI_UID_BASE)
    pod = parts[0]
    uid = iter(range(MUNSCI_UID_BASE + 900, MUNSCI_UID_BASE + 999))
    science = []
    for name, (pos, rot) in (("GooExperiment", (MUNSCI_GOO_POS, MUNSCI_GOO_ROT)),
                             ("sensorThermometer", (MUNSCI_THERMO_POS,
                                                    MUNSCI_THERMO_ROT))):
        pair = []
        for i, psi in enumerate((0.0, 180.0)):
            local = _yaw_vec(psi, pos)
            p = Part("%s%d" % (name, i), name, next(uid), pod.y + local[1])
            p.pos = tuple(c + o for c, o in zip(pod.pos, local))
            p.rot = _quat_str(_quat_mul(_quat_tuple_y(psi), rot))
            p.attm = 1
            p.srfn = pod.uid
            p.parent = pod
            pod.links.append(p.uid)
            pair.append(p)
        for p in pair:
            p.syms = [o.uid for o in pair if o is not p]
        science += pair
    return parts + science


def munsci_hatch_clearances(parts=None):
    """Each science part's azimuth distance (deg) from the pod's airlock."""
    parts = parts or munsci_layout()
    pod = parts[0]
    hatch = _azimuth_deg(MUNSCI_HATCH_POS)
    out = {}
    for p in parts:
        if p.name in MUNSCI_SCIENCE_PARTS:
            local = tuple(a - b for a, b in zip(p.pos, pod.pos))
            d = abs((_azimuth_deg(local) - hatch + 180.0) % 360.0 - 180.0)
            out[p.key] = d
    return out


def munsci_build():
    parts = munsci_layout()
    xs = [p.pos[0] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % MUNSCI_SHIP_NAME,
        "version = 1.12.5",
        "description = CS-6c craft (scripted campaign, Career): Mk1 pod with two Goo "
        "canisters and two 2HOT thermometers, heat shield, %d FL-T400 on a Swivel over "
        "%d FL-T200 on a Swivel core, two Thumpers. Built by construction by "
        "harness/tools/build_cs_orbiter_craft.py --variant munsci."
        % (len(MUNSCI_UPPER_NAMES), MUN_CORE_TANKS),
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 1.25, mun_stack_height(parts),
                           max(zs) - min(zs) + 1.25),
        "steamPublishedFileId = 0",
        "persistentId = 2600770011",
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    body = [render_part(p, i) for i, p in enumerate(parts)]
    return "\n".join(header) + "\n" + "\n".join(body) + "\n"


def munsci_figures():
    """mun_figures over CS Mun Science's layout (the science parts ride the pod
    group, so the upper stage carries them and the reentry mass counts them)."""
    return mun_figures(parts=munsci_layout())


def munsci_problems():
    """Every post-condition of CS Mun Science, as strings (empty = good)."""
    out = []
    parts = munsci_layout()
    f = munsci_figures()
    if f["parts"] > MUN_LIMIT_PARTS:
        out.append("%d parts > the level-0 VAB limit %d" % (f["parts"], MUN_LIMIT_PARTS))
    if f["mass"] > MUN_LIMIT_MASS_T:
        out.append("%.3f t > the level-1 pad limit %.0f t" % (f["mass"], MUN_LIMIT_MASS_T))
    if f["height"] > MUN_LIMIT_HEIGHT_M:
        out.append("%.2f m > the level-1 pad height %.0f m"
                   % (f["height"], MUN_LIMIT_HEIGHT_M))
    if f["dv_total"] < MUN_MIN_DELTA_V:
        out.append("delta-v %.0f < %.0f" % (f["dv_total"], MUN_MIN_DELTA_V))
    if f["dv_upper"] < MUN_MIN_UPPER_DELTA_V:
        out.append("upper delta-v %.0f < %.0f" % (f["dv_upper"], MUN_MIN_UPPER_DELTA_V))
    if f["liftoff_twr"] < MUN_MIN_LIFTOFF_TWR:
        out.append("liftoff TWR %.2f < %.2f" % (f["liftoff_twr"], MUN_MIN_LIFTOFF_TWR))
    if stage_table(parts) != mun_stage_want():
        out.append("stage order %r != %r" % (stage_table(parts), mun_stage_want()))
    shield = [p for p in parts if p.name == "HeatShield1"][0]
    if shield.istg != -1:
        out.append("the heat shield must not be staged")
    pod = parts[0]
    counts = {}
    for p in parts:
        if p.name in MUNSCI_SCIENCE_PARTS:
            counts[p.name] = counts.get(p.name, 0) + 1
            if p.parent is not pod or p.group != "pod" or p.sidx != -1:
                out.append("%s must ride the pod, unstaged" % p.key)
    if counts != MUNSCI_SCIENCE_PARTS:
        out.append("science parts %r != %r" % (counts, MUNSCI_SCIENCE_PARTS))
    for key, d in sorted(munsci_hatch_clearances(parts).items()):
        if d < MUNSCI_HATCH_CLEARANCE_DEG:
            out.append("%s sits %.0f deg off the hatch (< %.0f)"
                       % (key, d, MUNSCI_HATCH_CLEARANCE_DEG))
    uids = [p.uid for p in parts]
    if len(set(uids)) != len(uids):
        out.append("duplicate part uid")
    for p in parts:
        for ref in p.links + [o for _, o, _ in p.attn] + ([p.srfn] if p.srfn else []):
            if ref not in uids:
                out.append("%s references undeclared %s" % (p.uid, ref))
    return out


def munsci_check():
    """[] when the committed CS Mun Science is byte-identical to a fresh derivation
    and every post-condition holds, else the problems."""
    out = munsci_problems()
    if not os.path.exists(MUNSCI_OUT):
        return out + ["MISSING %s - run --variant munsci --write" % MUNSCI_OUT]
    with open(MUNSCI_OUT, encoding="utf-8", newline="") as fh:
        have = fh.read()
    if have != munsci_build():
        out.append("DRIFT: %s differs from the derivation" % MUNSCI_OUT)
    return out


def munsci_report():
    f = munsci_figures()
    print("%s: %d parts, %.3f t, %.2f m, cost %.0f (Thumpers at %.0f%%)"
          % (MUNSCI_SHIP_NAME, f["parts"], f["mass"], f["height"], f["cost"],
             100.0 * f["booster_thrust_fraction"]))
    for s in f["stages"]:
        twr = s.get("twr_asl", s.get("twr_vac"))
        print("  %-18s m0 %.3f t -> m1 %.3f t  dv vac %.0f m/s (ASL %.0f)  TWR %.2f  "
              "burn %.0f s" % (s["stage"], s["m0"], s["m1"], s["dv_vac"], s["dv_asl"],
                               twr, s["burn_s"]))
    print("  total dv %.0f m/s (launch stage at ASL Isp), %.0f m/s all vacuum"
          % (f["dv_total"], f["dv_total_vac"]))
    print("  liftoff TWR %.3f (sea level); reentry mass %.3f t"
          % (f["liftoff_twr"], f["reentry_mass"]))
    print("  hatch clearances (deg): %s" % ", ".join(
        "%s %.0f" % kv for kv in sorted(munsci_hatch_clearances().items())))



# ---------------------------------------------------------------------------
# THE SOLAR LANDER VARIANT (`--variant solar`): `CS Mun Solar Lander`, CS-7's re-cut.
#
# CS-7's first flight (`2026-10-27_2326`, both attempts) ended MISSION-FLAKE
# `electric-charge-exhausted` in MJ-ASCENT: after the core Swivel cut off at the
# target apoapsis (~53 km), MechJeb's coast hold on the long stack (the full lander
# on the empty core, 20 parts, ~22 m) saturated the Mk1 pod's reaction wheel
# (mk1Pod_v2.cfg rate 0.24 EC per axis) and drained the pod's 50 EC at a measured
# 0.42-0.47 EC/s; nothing on the lander stage makes power. CS-6c then researched
# basicScience (the Z-100) and electrics (the OX-STAT); this variant flies from its
# produced save, `cs-career-mun4`.
#
# THE EDITS to CS Mun Lander, within the VAB level-0 limit of 30 parts:
#   - the three Thumper nose cones come off (3 parts free; the cones are drag only;
#     the open Thumper tops cost an estimated 30-60 m/s of drag over the 70 s boost,
#     which the Terrier pays out of a 750 m/s margin);
#   - three OX-STAT panels on the lander's TOP FL-T400 at azimuths 90 / 210 / 330 deg
#     (each 60 deg or more off the pod's hatch at 270), lander-stage parts, so they
#     stay with the pod through every Mun phase and leave with the lander at the
#     pod's separation;
#   - one Z-100 on the second FL-T400 at azimuth 90 (opposite the hatch).
# Everything else is CS Mun Lander's derivation unchanged: the same tanks, the same
# Terrier, the LT-05s at the same place on the FL-T200 (so the same 0.88 m bell
# clearance), the same core and Thumpers, the same staging. 30 parts.
#
# DONORS, by stored state: both tails from Kerbal 1 (1.12.0), the stock craft that
# carries exactly these two parts on an FL-T400 (four OX-STATs and four Z-100s round
# its upper tank). Their attach is Kerbal 1's: the OX-STAT 0.6218 m out and 0.5751 m
# above the tank centre, the Z-100 0.6340 m out and 0.6109 m up. The rotation is the
# donor family's yaw: psi = 90 - azimuth for the OX-STAT (attach normal +Z) and
# 270 - azimuth for the Z-100 (attach normal -Z), each checked against all four of
# Kerbal 1's own placements (test_cs_orbiter_craft). The OX-STAT is static and
# non-deploying (its cfg sets isBreakable = false; the tail stores deployState
# EXTENDED): drag cannot break it, and its edge meets the airflow on the ascent, as
# on Kerbal 1 and Kerbal X.
#
# THE ELECTRIC-CHARGE BUDGET (`--report` prints the figures):
#   capacity 150 EC (the pod's 50 + the Z-100's 100), all of it aboard until the
#   pod's separation; the return after it flies on the pod's 50 and the chute.
#   generation, sun broadside to the stack axis at Kerbin's distance (the Mun's too):
#   0.303 to 0.350 EC/s, 0.334 on average over the roll angle (0.35 x the sum of the
#   lit panels' cosines); times the cosine of the sun's angle off broadside, and zero
#   in shadow or with the sun on the axis.
#   draw, by phase: the Kerbin ascent coast on the long stack 0.42-0.47 EC/s while
#   MechJeb holds (measured), about 150 game s out of rails, ~65 EC; the mission
#   releases the wheels below the atmosphere's top on that coast (mlib.mlr_decide),
#   leaving ~25 EC, and the core Swivel's alternator (6 EC/s at full throttle) refills
#   during the circularization's first seconds. The lander-only holds after the core
#   drop: CS-6c's flown upper stage (the same pod, 7.2 t against 7.35 t) drew 0.002
#   to 0.045 EC/s on its 1x and physics-warp coasts and nothing on rails. The powered
#   Mun phases (the descent ~350 s, the ascent ~300 s) are unmeasured on this stack:
#   bounded by the saturated 0.42 EC/s, ~20-60 EC at a few times the hold rate. The
#   surface stay holds SAS (B5's tip guard) at ~0.05-0.1 EC/s for ~5 min.
#   expected: ~60-120 EC used between two sunlit coasts, against 150 stored.
#   worst case: no sun from the descent to orbit and the wheel saturated end to end:
#   ~270 EC for the descent and ascent plus ~30 on the surface against 150 stored,
#   ~150 EC short late in the Mun ascent; the electric-charge watchdog names it.
#
# ASSUMPTIONS A FLIGHT CAN FALSIFY: the drag cost of the open Thumper tops, the
# powered-phase draw on the Mun, the sun's angle at the landing site, and CS Mun
# Lander's own (the leg reach, MechJeb's Mun ascent).
# ---------------------------------------------------------------------------

SOLAR_SHIP_NAME = "CS Mun Solar Lander"
SOLAR_OUT = os.path.join(ROOT, "harness", "fixtures", "saves", "cs-career-mun4",
                         "Ships", "VAB", "CS Mun Solar Lander.craft")
SOLAR_UID_BASE = 4291000001
# FOUR LEGS. Three LT-05s stand on a triangle whose nearest edge is half the feet's radius
# from the axis (about 0.36 m under a centre of mass about 1.7 m up), so the three-legged
# lander tips at about 12 deg, and the Mun's equatorial band reads 7 to 14 deg at the
# flown site (kRPC surface_height over 10 m, run 2026-10-31). Runs 2026-10-28_0432 attempt
# 2 and _0649 attempt 2 fell over on touchdown, and _0626 while Jeb was out and SAS off.
# A square's nearest edge is 0.71 of the feet's radius (about 0.51 m, about 17 deg). The
# level-0 VAB holds 30 parts and the upgrade costs more than the career has, so the fourth
# leg takes a panel's place: the flown minimum was 141.8 of 150 EC with three panels.
SOLAR_LEG_AZIMUTHS = (45.0, 135.0, 225.0, 315.0)
SOLAR_PANEL_AZIMUTHS = (0.0, 180.0)
SOLAR_BATTERY_AZIMUTH = 90.0
# Kerbal 1 solarPanels5_4293048744 (its +Z panel): attPos0 (1.3e-15 noise dropped) on
# its FL-T400, i.e. the radius and the height above the tank centre.
SOLAR_PANEL_RADIUS = 0.621777415
SOLAR_PANEL_DY = 0.575105727
# Kerbal 1 batteryPack_4293048212: attPos0 (-0.448293895, 0.610907614, -0.448293895).
SOLAR_BATTERY_RADIUS = math.hypot(0.448293895, 0.448293895)
SOLAR_BATTERY_DY = 0.610907614
# solarPanels5.cfg ModuleDeployableSolarPanel chargeRate (EC/s, full sun at 1 AU).
SOLAR_PANEL_CHARGE_RATE = 0.35
# CS-7 flight 2026-10-27_2326: the pod wheel's draw on the long-stack ascent coast.
SOLAR_MEASURED_HOLD_DRAW = 0.42
SOLAR_MIN_CAPACITY = 150.0
SOLAR_PARTS = {"solarPanels5": 2, "batteryPack": 1}

PART_CFG.update({
    "solarPanels5": {"mass": 0.005, "cost": 75},     # Electrical/radialFlatSolarPanel
    "batteryPack": {"mass": 0.005, "cost": 80},      # Electrical/z-100Battery
})

SOLAR_DONORS = {
    "solarPanels5": ("VAB/Kerbal 1.craft", 0),
    "batteryPack": ("VAB/Kerbal 1.craft", 0),
}
SOLAR_TAILS = {
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock 1.12.0, KSP-written]
    "solarPanels5": '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDeployableSolarPanel\n\t\tisEnabled = True\n\t\tefficiencyMult = 1\n\t\tlaunchUT = -1\n\t\tcurrentRotation = (0, -6.057208E-07, 0, 1)\n\t\tstoredAnimationTime = 0\n\t\tstoredAnimationSpeed = 0\n\t\tdeployState = EXTENDED\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tExtendPanelsAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tExtendAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tRetractAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock 1.12.0, KSP-written]
    "batteryPack": '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 100\n\t\tmaxAmount = 100\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
}

DONORS.update(SOLAR_DONORS)
TAILS.update(SOLAR_TAILS)


def _radial_part(key, name, uid_n, parent, azimuth, radius, dy, psi):
    """A part surface-attached to ``parent`` (an unrotated stack tank) at ``azimuth``
    (the builder's atan2(z, x) convention), ``radius`` out and ``dy`` above its
    centre, yawed by ``psi``; a lander-stage part, never staged."""
    a = math.radians(azimuth)
    p = Part(key, name, uid_n, parent.y + dy, istg=LANDER_STAGE_POD_SEPARATION, dstg=4,
             group="upper")
    p.pos = (parent.pos[0] + radius * math.cos(a), parent.y + dy,
             parent.pos[2] + radius * math.sin(a))
    p.rot = _quat_y(psi)
    p.attm = 1
    p.srfn = parent.uid
    p.parent = parent
    parent.links.append(p.uid)
    return p


def solar_layout():
    """Every part of CS Mun Solar Lander, root first: CS Mun Lander's layout without
    the Thumper cones and with four legs, then the two OX-STATs on the top FL-T400 and
    the Z-100 on the second."""
    parts = lander_layout(uid_base=SOLAR_UID_BASE, cones=False,
                          leg_azimuths=SOLAR_LEG_AZIMUTHS)
    upper = [p for p in parts if p.group == "upper" and p.key.startswith("utank")]
    top, second = upper[0], upper[1]
    uid = iter(range(SOLAR_UID_BASE + 900, SOLAR_UID_BASE + 999))
    panels = [_radial_part("panel%d" % i, "solarPanels5", next(uid), top, az,
                           SOLAR_PANEL_RADIUS, SOLAR_PANEL_DY, 90.0 - az)
              for i, az in enumerate(SOLAR_PANEL_AZIMUTHS)]
    for p in panels:
        p.syms = [o.uid for o in panels if o is not p]
    battery = _radial_part("battery", "batteryPack", next(uid), second,
                           SOLAR_BATTERY_AZIMUTH, SOLAR_BATTERY_RADIUS, SOLAR_BATTERY_DY,
                           270.0 - SOLAR_BATTERY_AZIMUTH)
    return parts + panels + [battery]


def solar_hatch_clearances(parts=None):
    """Each panel's and the battery's azimuth distance (deg) from the pod's airlock."""
    parts = parts or solar_layout()
    hatch = _azimuth_deg(MUNSCI_HATCH_POS)
    out = {}
    for p in parts:
        if p.name in SOLAR_PARTS:
            local = tuple(a - b for a, b in zip(p.pos, p.parent.pos))
            out[p.key] = abs((_azimuth_deg(local) - hatch + 180.0) % 360.0 - 180.0)
    return out


def solar_ec_budget(parts=None):
    """The electric-charge figures: capacity (EC, from the tails), the panels' peak,
    and their output with the sun broadside to the stack over the roll angle (min,
    average, max, EC/s), plus the seconds of a saturated hold the capacity covers."""
    parts = parts or solar_layout()
    capacity = sum(a for p in parts for r, a, _ in tail_resources(p.tail)
                   if r == "ElectricCharge")
    azs = [_azimuth_deg(tuple(a - b for a, b in zip(p.pos, p.parent.pos)))
           for p in parts if p.name == "solarPanels5"]
    samples = []
    for step in range(3600):
        phi = step / 10.0
        lit = sum(max(0.0, math.cos(math.radians(az - phi))) for az in azs)
        samples.append(SOLAR_PANEL_CHARGE_RATE * lit)
    return {"capacity": capacity, "panels": len(azs),
            "peak": SOLAR_PANEL_CHARGE_RATE * len(azs),
            "broadside_min": min(samples), "broadside_max": max(samples),
            "broadside_avg": sum(samples) / len(samples),
            "saturated_hold_s": capacity / SOLAR_MEASURED_HOLD_DRAW}


def solar_build():
    parts = solar_layout()
    xs = [p.pos[0] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % SOLAR_SHIP_NAME,
        "version = 1.12.5",
        "description = CS-7 craft (scripted campaign, Career): Mk1 pod, heat shield, "
        "%d FL-T400 and %d FL-T200 on a Terrier with three LT-05 legs, three OX-STAT "
        "panels and a Z-100, over %d FL-T400 on a Swivel core, three Thumpers. Built by "
        "construction by harness/tools/build_cs_orbiter_craft.py --variant solar."
        % (LANDER_UPPER_T400, LANDER_UPPER_T200, LANDER_CORE_TANKS),
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 1.25, lander_stack_height(parts),
                           max(zs) - min(zs) + 1.25),
        "steamPublishedFileId = 0",
        "persistentId = 2600880022",
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    body = [render_part(p, i) for i, p in enumerate(parts)]
    return "\n".join(header) + "\n" + "\n".join(body) + "\n"


def solar_figures():
    """lander_figures over CS Mun Solar Lander's layout (the panels and the battery
    ride the lander stage), plus the electric-charge figures."""
    parts = solar_layout()
    f = lander_figures(parts=parts)
    f["ec"] = solar_ec_budget(parts)
    return f


def solar_problems():
    """Every post-condition of CS Mun Solar Lander, as strings (empty = good)."""
    out = []
    parts = solar_layout()
    f = solar_figures()
    if f["parts"] > MUN_LIMIT_PARTS:
        out.append("%d parts > the level-0 VAB limit %d" % (f["parts"], MUN_LIMIT_PARTS))
    if f["mass"] > MUN_LIMIT_MASS_T:
        out.append("%.3f t > the level-1 pad limit %.0f t" % (f["mass"], MUN_LIMIT_MASS_T))
    if f["height"] > MUN_LIMIT_HEIGHT_M:
        out.append("%.2f m > the level-1 pad height %.0f m"
                   % (f["height"], MUN_LIMIT_HEIGHT_M))
    for key, floor, label in (("dv_total", LANDER_MIN_DELTA_V, "delta-v"),
                              ("dv_lower", LANDER_MIN_LOWER_DELTA_V, "lower delta-v"),
                              ("dv_upper", LANDER_MIN_UPPER_DELTA_V, "lander delta-v"),
                              ("liftoff_twr", LANDER_MIN_LIFTOFF_TWR, "liftoff TWR"),
                              ("mun_twr", LANDER_MIN_MUN_TWR, "Mun TWR")):
        if f[key] < floor:
            out.append("%s %.2f < %.2f" % (label, f[key], floor))
    if stage_table(parts) != lander_stage_want():
        out.append("stage order %r != %r" % (stage_table(parts), lander_stage_want()))
    shield = [p for p in parts if p.name == "HeatShield1"][0]
    if shield.istg != -1:
        out.append("the heat shield must not be staged")
    if any(p.name == "noseCone" for p in parts):
        out.append("the Thumper cones must be off (their slots carry the power)")
    counts = {}
    for p in parts:
        if p.name in SOLAR_PARTS:
            counts[p.name] = counts.get(p.name, 0) + 1
            if (p.group != "upper" or p.sidx != -1
                    or not p.parent.name.startswith("fuelTank")):
                out.append("%s must ride a lander tank, unstaged" % p.key)
    if counts != SOLAR_PARTS:
        out.append("power parts %r != %r" % (counts, SOLAR_PARTS))
    for key, d in sorted(solar_hatch_clearances(parts).items()):
        if d < MUNSCI_HATCH_CLEARANCE_DEG:
            out.append("%s sits %.0f deg off the hatch (< %.0f)"
                       % (key, d, MUNSCI_HATCH_CLEARANCE_DEG))
    # CS Mun Lander's Terrier, to the last digit, and its legs' height on the same tank.
    if len([p for p in parts if p.name == "miniLandingLeg"]) != len(SOLAR_LEG_AZIMUTHS):
        out.append("the lander needs %d legs" % len(SOLAR_LEG_AZIMUTHS))
    want = {p.key: p.pos for p in lander_layout() if p.name == "liquidEngine3.v2"}
    have = {p.key: p.pos for p in parts if p.name == "liquidEngine3.v2"}
    if want != have:
        out.append("the Terrier moved: %r != %r" % (have, want))
    if abs(f["leg_clearance"] - lander_leg_clearance()) > 1e-9:
        out.append("leg clearance %.3f != CS Mun Lander's %.3f"
                   % (f["leg_clearance"], lander_leg_clearance()))
    if f["ec"]["capacity"] < SOLAR_MIN_CAPACITY:
        out.append("ElectricCharge capacity %.0f < %.0f"
                   % (f["ec"]["capacity"], SOLAR_MIN_CAPACITY))
    if "deployState = EXTENDED" not in TAILS["solarPanels5"]:
        out.append("the OX-STAT tail must be the static, extended panel")
    uids = [p.uid for p in parts]
    if len(set(uids)) != len(uids):
        out.append("duplicate part uid")
    for p in parts:
        for ref in p.links + [o for _, o, _ in p.attn] + ([p.srfn] if p.srfn else []):
            if ref not in uids:
                out.append("%s references undeclared %s" % (p.uid, ref))
    return out


def solar_check():
    """[] when the committed CS Mun Solar Lander is byte-identical to a fresh
    derivation and every post-condition holds, else the problems."""
    out = solar_problems()
    if not os.path.exists(SOLAR_OUT):
        return out + ["MISSING %s - run --variant solar --write" % SOLAR_OUT]
    with open(SOLAR_OUT, encoding="utf-8", newline="") as fh:
        have = fh.read()
    if have != solar_build():
        out.append("DRIFT: %s differs from the derivation" % SOLAR_OUT)
    return out


def solar_report():
    f = solar_figures()
    print("%s: %d parts, %.3f t, %.2f m, cost %.0f (Thumpers at %.0f%%, no cones)"
          % (SOLAR_SHIP_NAME, f["parts"], f["mass"], f["height"], f["cost"],
             100.0 * f["booster_thrust_fraction"]))
    for s in f["stages"]:
        twr = s.get("twr_asl", s.get("twr_vac"))
        print("  %-18s m0 %.3f t -> m1 %.3f t  dv vac %.0f m/s (ASL %.0f)  TWR %.2f  "
              "burn %.0f s" % (s["stage"], s["m0"], s["m1"], s["dv_vac"], s["dv_asl"],
                               twr, s["burn_s"]))
    print("  total dv %.0f m/s (launch stage at ASL Isp), lower %.0f, lander %.0f"
          % (f["dv_total"], f["dv_lower"], f["dv_upper"]))
    print("  liftoff TWR %.3f (sea level); Mun TWR %.2f (lander full); leg clearance "
          "%.2f m; reentry mass %.3f t"
          % (f["liftoff_twr"], f["mun_twr"], f["leg_clearance"], f["reentry_mass"]))
    ec = f["ec"]
    print("  ElectricCharge: capacity %.0f EC; %d OX-STAT peak %.3f EC/s; sun broadside "
          "%.3f-%.3f EC/s (avg %.3f); a saturated %.2f EC/s hold empties it in %.0f s"
          % (ec["capacity"], ec["panels"], ec["peak"], ec["broadside_min"],
             ec["broadside_max"], ec["broadside_avg"], SOLAR_MEASURED_HOLD_DRAW,
             ec["saturated_hold_s"]))
    print("  hatch clearances (deg): %s" % ", ".join(
        "%s %.0f" % kv for kv in sorted(solar_hatch_clearances().items())))


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
    ap.add_argument("--variant", choices=("orbiter", "mun", "lander", "munsci", "solar"),
                    default="orbiter",
                    help="orbiter = CS Orbiter (CS-4, cs-career-mc1); mun = CS Mun "
                    "Flyby (CS-5, cs-career-ts1); lander = CS Mun Lander (CS-7's first "
                    "cut, cs-career-mun2); munsci = CS Mun Science (CS-6c, "
                    "cs-career-mun3); solar = CS Mun Solar Lander (CS-7, cs-career-mun4)")
    a = ap.parse_args(argv)
    if a.variant == "solar" and not a.verify_donors:
        if a.tanks != DEFAULT_TANKS:
            ap.error("--tanks applies to the orbiter variant only")
        if a.report:
            solar_report()
            return 0
        if a.write:
            bad = solar_problems()
            if bad:
                print("\n".join(bad))
                return 1
            text = solar_build()
            os.makedirs(os.path.dirname(SOLAR_OUT), exist_ok=True)
            with open(SOLAR_OUT, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(text)
            print("wrote %s (%d lines)" % (SOLAR_OUT, text.count("\n")))
            solar_report()
            return 0
        bad = solar_check()
        print("\n".join(bad) or "OK committed craft matches the derivation")
        return 1 if bad else 0
    if a.variant == "munsci" and not a.verify_donors:
        if a.tanks != DEFAULT_TANKS:
            ap.error("--tanks applies to the orbiter variant only")
        if a.report:
            munsci_report()
            return 0
        if a.write:
            bad = munsci_problems()
            if bad:
                print("\n".join(bad))
                return 1
            text = munsci_build()
            os.makedirs(os.path.dirname(MUNSCI_OUT), exist_ok=True)
            with open(MUNSCI_OUT, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(text)
            print("wrote %s (%d lines)" % (MUNSCI_OUT, text.count("\n")))
            munsci_report()
            return 0
        bad = munsci_check()
        print("\n".join(bad) or "OK committed craft matches the derivation")
        return 1 if bad else 0
    if a.variant == "lander" and not a.verify_donors:
        if a.tanks != DEFAULT_TANKS:
            ap.error("--tanks applies to the orbiter variant only")
        if a.report:
            lander_report()
            return 0
        if a.write:
            bad = lander_problems()
            if bad:
                print("\n".join(bad))
                return 1
            text = lander_build()
            os.makedirs(os.path.dirname(LANDER_OUT), exist_ok=True)
            with open(LANDER_OUT, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(text)
            print("wrote %s (%d lines)" % (LANDER_OUT, text.count("\n")))
            lander_report()
            return 0
        bad = lander_check()
        print("\n".join(bad) or "OK committed craft matches the derivation")
        return 1 if bad else 0
    if a.variant == "mun" and not a.verify_donors:
        if a.tanks != DEFAULT_TANKS:
            ap.error("--tanks applies to the orbiter variant only")
        if a.report:
            mun_report()
            return 0
        if a.write:
            bad = mun_problems()
            if bad:
                print("\n".join(bad))
                return 1
            text = mun_build()
            os.makedirs(os.path.dirname(MUN_OUT), exist_ok=True)
            with open(MUN_OUT, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(text)
            print("wrote %s (%d lines)" % (MUN_OUT, text.count("\n")))
            mun_report()
            return 0
        bad = mun_check()
        print("\n".join(bad) or "OK committed craft matches the derivation")
        return 1 if bad else 0
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
