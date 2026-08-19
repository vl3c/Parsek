#!/usr/bin/env python3
"""Build the `career-science-pad` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. `L3-career-science-recover` is the career-ledger lane's forge:
it flies the committed career pad craft, runs its science, TRANSMITS it, and
recovers the craft, so the produced save is a career that EARNED. Two runs on
2026-08-19 flew that profile correctly and still could not finish it. The
diagnosis is settled and is a fact about the CRAFT, not about the mission:

  kRPC filters transmitters by `IScienceDataTransmitter.CanTransmit()`, and
  stock `ModuleDataTransmitter.CanTransmit()` returns false for
  `antennaType == INTERNAL` BEFORE it consults CommNet at all. The Jumping Flea
  aboard `career-pad-craft` carries exactly one transmitter - `mk1pod.v2`'s
  built-in INTERNAL antenna - so stock forbids transmitting science from it.
  Nothing about CommNet, ground stations or ElectricCharge is involved.

`design-autotest-mission-library.md` Amendment A puts that class of terminal on
the "the fixture is wrong, and re-flying it changes nothing" side. This tool is
the fix: a SIBLING fixture whose craft carries a DIRECT antenna.

WHY A SIBLING RATHER THAN AN EDIT. Six committed specs already fly
`career-pad-craft` (CL-1, CL-2, CL-3, H26, L2, R7a) and two of them pin
measurements taken on that exact craft. Mutating it would re-open every one of
those. `career-pad-craft` is therefore NOT touched; this builds a new save
beside it, and `--check` re-verifies both the base's post-conditions and this
fixture's own.

WHAT IT SPLICES. Exactly three additive `PART` nodes into the single `VESSEL`
node of the committed `career-pad-craft` save, all surface-attached to the pod
(part index 0), all appended AFTER the existing eight so no existing index moves:

  8.  `SurfAntenna`  (Communotron 16-S)  - `antennaType = DIRECT`, the whole
      point. 0.015 t.
  9.  `batteryPack`  (Z-100, 100 EC)     - see the EC arithmetic below. 0.005 t.
  10. `batteryPack`  (Z-100, 100 EC)     - same. 0.005 t.

THE BATTERIES ARE NOT PADDING; the antenna alone would swap one terminal for
another. Stock charges `packetResourceCost` per `packetSize` Mits, and both
values come off the antenna, not the experiment:

  SurfAntenna  packetSize = 2 Mits   packetResourceCost = 12 EC
  mysteryGoo   baseValue 10 x dataScale 1 = 10 Mits -> 5 packets -> 60 EC (x2)
  crewReport   baseValue  5 x dataScale 1 =  5 Mits -> 3 packets -> 36 EC
  ----------------------------------------------------------------------------
  the three experiments aboard                                       156 EC

`mk1pod.v2` carries 50. The 2026-08-19 flight measured that 50 as UNSPENT at
touchdown (no solar panel, no RTG, and the reaction wheel drew nothing over the
whole 461 s profile), so 50 is what a transmit would actually have to spend -
enough for the crew report alone and for neither Goo. Two Z-100s take the craft
to 250 EC against a 156 EC worst case: every experiment transmits, with 94 EC
of margin for any drain a future flight does incur. `transmitMinScienceGain`
only needs ONE subject to credit, so the margin is defense in depth rather than
a prediction.

MASS AND DRAG. +0.025 t on a ~2.67 t craft (0.94%). All three parts are
`PhysicsSignificance = 1` (physicsless), so they add mass to the pod and no
rigidbody - but they are NOT aerodynamically free, and the measurement says so
louder than the mass does. The flight leg's only window is
`apoapsisWindowMeters = {min = 6000, max = 30000}`; the bare craft peaked at
19,990 m and this one peaked at 15,599.8 m on run `2026-08-19_1912`. A 22% loss
for a 0.94% mass increase is DRAG - three surface protrusions on a small craft
doing ~500 m/s low in the atmosphere. Both bounds survive with the floor cleared
by 2.6x, so nothing here needs changing; but a future tightening of that window
must be sized against 15,600, not against B1's 19,990.

CAREER LEGALITY IS BY CONSTRUCTION, NOT BY ASSUMPTION. Both part names are
already in this save's purchased-parts set (the `start` Tech node lists
`SurfAntenna` and `batteryPack`, which the ProbesBeforeCrew tree relocation put
there), and `verify` asserts that rather than trusting it.

WHAT IS NOT TOUCHED. Everything else comes from `career-pad-craft` unchanged:
`Mode = CAREER`, `Funding` 500000, `ResearchAndDevelopment` 100, `Reputation` 0,
the roster with Jebediah `Assigned` aboard, `MissingCrewsRespawn = False`, and
the inert `SCENARIO{name=ParsekScenario}` node without which a FLIGHT-route run
persists nothing (CL-1 flight 1 proved that, at the cost of a whole flight).
The eight original `PART` nodes are copied BYTE FOR BYTE, and `verify` asserts
that too, so a consumer reasoning from B1's measured flight profile still can.

Usage:
    python harness/tools/build_career_science_pad.py            # write it
    python harness/tools/build_career_science_pad.py --check    # verify only

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture.
It is WIRED, not decorative: `CareerSciencePadFixtureDriftTests` in
`harness/missions/lib/test_science_bench_recover.py` runs the same `verify`
in-process AND re-runs `build` over the CURRENT `career-pad-craft` bytes and
asserts byte-identity with the committed save, so a change to the base fixture
reds in the suite instead of drifting silently into a live flight.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import math
import os
import shutil
import sys
from typing import List, Optional, Sequence, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text helpers, and ONE copy of the career
# post-conditions. Importing the base builder rather than re-implementing it is
# what makes "the base's every post-condition still holds" a fact this file
# cannot drift away from.
import build_career_pad_craft as base_builder  # noqa: E402

read_lines = base_builder.read_lines
write_lines = base_builder.write_lines
find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_top_value = base_builder.set_top_value

BASE_NAME = "career-pad-craft"
TARGET_NAME = "career-science-pad"
CREW_NAME = base_builder.CREW_NAME

# The pod part the spliced parts surface-attach to, and the donor part whose
# position/rotation pair they are derived from. Index 2 is the -x Mystery Goo:
# already surface-attached to the pod at the ring this splice reuses, so its
# pose is a MEASURED valid pose rather than an invented one.
POD_INDEX = 0
POSE_DONOR_INDEX = 2

# EC arithmetic, restated as code so `verify` can gate on it. See the module
# docstring for the derivation.
TRANSMIT_EC_REQUIRED = 156.0

# `mid` (missionID) and `launchID` are per-vessel, not per-part: every existing
# part carries these, and a spliced part that did not would be a different
# vessel's part sitting in this one. Both are DERIVED from the pod
# (`vessel_identity` below) on BOTH sides - the splice writes what the pod
# carries, and `verify` re-reads the pod rather than a literal. A literal here
# would be compared against itself, so the "carries a foreign missionID" check
# would pass no matter what the base's vessel actually says.


class SplicePart(object):
    """One part to splice: what it is, where it goes, and what it persists."""

    def __init__(self, name, cid, uid, persistent_id, mass, yaw_degrees,
                 cargo_stackable, modules, resources):
        self.name = name
        self.cid = cid
        self.uid = uid
        self.persistent_id = persistent_id
        self.mass = mass
        self.yaw_degrees = yaw_degrees
        self.cargo_stackable = cargo_stackable
        self.modules = modules
        self.resources = resources


def _cargo_module(packed_volume):
    """A persisted `ModuleCargoPart` in the exact shape KSP writes one."""
    return [
        "name = ModuleCargoPart",
        "isEnabled = True",
        "packedVolume = %s" % packed_volume,
        "beingAttached = False",
        "beingSettled = False",
        "reinitResourcesOnStoreInVessel = False",
        "stagingEnabled = True",
        "EVENTS",
        "{",
        "}",
        "ACTIONS",
        "{",
        "}",
        "UPGRADESAPPLIED",
        "{",
        "}",
    ]


def _transmitter_module():
    """A persisted `ModuleDataTransmitter`, copied field for field off the one
    `mk1pod.v2` already persists in this same save.

    NOTHING about the antenna's CAPABILITY is persisted here, and that is the
    point: `antennaType`, `antennaPower`, `packetSize` and `packetResourceCost`
    are PREFAB config (`Squad/Parts/Utility/DirectAntennas/C16S.cfg`), read from
    `GameData` at load. A save that restated them would be a save that could
    disagree with the part it names."""
    return [
        "name = ModuleDataTransmitter",
        "isEnabled = True",
        "xmitIncomplete = False",
        "stagingEnabled = True",
        "canComm = True",
        "EVENTS",
        "{",
        "}",
        "ACTIONS",
        "{",
        "StartTransmissionAction",
        "{",
        "actionGroup = None",
        "active = False",
        "wasActiveBeforePartWasAdjusted = False",
        "}",
        "}",
        "UPGRADESAPPLIED",
        "{",
        "}",
    ]


def _electric_charge_resource(amount):
    """A persisted full `ElectricCharge` tank, in `mk1pod.v2`'s exact shape."""
    return [
        "name = ElectricCharge",
        "amount = %s" % amount,
        "maxAmount = %s" % amount,
        "flowState = True",
        "isTweakable = True",
        "hideFlow = False",
        "isVisible = True",
        "flowMode = Both",
    ]


# The three parts. `uid` (flightID) and `persistentId` must be unique within the
# vessel and are asserted so by `verify`; the values below are fixed literals
# rather than derived hashes so the fixture is reproducible byte for byte.
SPLICE_PARTS: Tuple[SplicePart, ...] = (
    SplicePart(
        name="SurfAntenna",
        cid="4294300001",
        uid="1900000001",
        persistent_id="1900000001",
        mass="0.0149999997",
        yaw_degrees=90.0,
        # ModuleCargoPart { stackableQuantity = 4, packedVolume = 5 }
        cargo_stackable="4",
        modules=[_transmitter_module(), _cargo_module("5")],
        resources=[],
    ),
    SplicePart(
        name="batteryPack",
        cid="4294300002",
        uid="1900000002",
        persistent_id="1900000002",
        mass="0.00499999989",
        yaw_degrees=-90.0,
        cargo_stackable="1",
        modules=[_cargo_module("35")],
        resources=[_electric_charge_resource("100")],
    ),
    SplicePart(
        name="batteryPack",
        cid="4294300003",
        uid="1900000003",
        persistent_id="1900000003",
        mass="0.00499999989",
        yaw_degrees=45.0,
        cargo_stackable="1",
        modules=[_cargo_module("35")],
        resources=[_electric_charge_resource("100")],
    ),
)


# ---------------------------------------------------------------------------
# Pose derivation.
#
# The spliced parts are placed by ROTATING the donor Goo's measured pose about
# the pod's own +Y axis, rather than by inventing coordinates. The donor sits on
# the pod's surface-attach ring at -x; a yaw of +90 / -90 / +45 degrees walks it
# around that ring to +z / -z / the -x..+z midpoint, which are the free azimuths
# (the two Goos hold -x and +x). Both the position AND the rotation are carried
# through the same yaw, so the parts keep the donor's surface-normal alignment.
#
# The pose is COSMETIC - all three parts are physicsless and none is
# orientation-sensitive - but deriving it costs nothing and a derived pose
# cannot be subtly wrong the way a typed one can.
# ---------------------------------------------------------------------------


def _parse_vector3(text: str) -> Tuple[float, float, float]:
    parts = [p.strip() for p in text.split(",")]
    if len(parts) != 3:
        raise SystemExit("expected a 3-component vector, got %r" % text)
    return (float(parts[0]), float(parts[1]), float(parts[2]))


def _parse_quaternion(text: str) -> Tuple[float, float, float, float]:
    parts = [p.strip() for p in text.split(",")]
    if len(parts) != 4:
        raise SystemExit("expected a 4-component quaternion, got %r" % text)
    return (float(parts[0]), float(parts[1]), float(parts[2]), float(parts[3]))


def _format_float(value: float) -> str:
    """KSP writes Unity floats; round to float precision and drop -0.0."""
    rounded = float("%.9g" % value)
    if rounded == 0.0:
        rounded = 0.0
    text = repr(rounded)
    if text.endswith(".0"):
        text = text[:-2]
    return text


def yaw_pose_about_y(position: Tuple[float, float, float],
                     rotation: Tuple[float, float, float, float],
                     degrees: float):
    """Rotate a (position, rotation) pair by `degrees` about the +Y axis.

    Position uses Unity's left-handed Y-up convention, so a +Y yaw of theta maps
    (x, z) -> (x cos + z sin, -x sin + z cos). Rotation is the Hamilton product
    q_yaw * q, which is the same rotation applied on the same side."""
    theta = math.radians(degrees)
    cos_t = math.cos(theta)
    sin_t = math.sin(theta)
    px, py, pz = position
    new_position = (px * cos_t + pz * sin_t, py, -px * sin_t + pz * cos_t)

    half = theta / 2.0
    qx1, qy1, qz1, qw1 = (0.0, math.sin(half), 0.0, math.cos(half))
    qx2, qy2, qz2, qw2 = rotation
    new_rotation = (
        qw1 * qx2 + qx1 * qw2 + qy1 * qz2 - qz1 * qy2,
        qw1 * qy2 - qx1 * qz2 + qy1 * qw2 + qz1 * qx2,
        qw1 * qz2 + qx1 * qy2 - qy1 * qx2 + qz1 * qw2,
        qw1 * qw2 - qx1 * qx2 - qy1 * qy2 - qz1 * qz2,
    )
    return new_position, new_rotation


# ---------------------------------------------------------------------------
# The splice.
# ---------------------------------------------------------------------------


def part_nodes(lines: List[str], vessel: Tuple[int, int]) -> List[Tuple[int, int]]:
    """Every direct `PART` child of a `VESSEL` node."""
    return child_nodes(lines, vessel, "PART")


def vessel_identity(lines: List[str],
                    parts: Sequence[Tuple[int, int]]) -> Tuple[str, str]:
    """The `(mid, launchID)` pair the vessel's POD carries.

    The single source of truth for both the splice and `verify`: read from
    `parts[POD_INDEX]`, which the splice never touches and which the drift cell
    asserts byte-identical to the base's. Raises when either is missing, because
    a spliced part with no missionID is a different vessel's part."""
    pod = parts[POD_INDEX]
    mid = get_value(lines, pod, "mid")
    launch_id = get_value(lines, pod, "launchID")
    if mid is None or launch_id is None:
        raise SystemExit("pod part %d carries no mid/launchID to derive from"
                         % POD_INDEX)
    return mid, launch_id


def _indent(depth: int) -> str:
    return "\t" * depth


def _render_block(body: Sequence[str], depth: int) -> List[str]:
    """Render a pre-flattened node body, re-indenting by brace nesting.

    The module/resource bodies above are written as flat token lists so their
    shape is readable at the call site; this restores the tab indentation a .sfs
    carries."""
    out: List[str] = []
    level = depth
    for token in body:
        if token == "}":
            level -= 1
        out.append("%s%s" % (_indent(level), token))
        if token == "{":
            level += 1
    return out


def render_part(spec: SplicePart, donor_position, donor_rotation,
                vessel_mid: str, launch_id: str, depth: int = 3) -> List[str]:
    """Render one spliced `PART` node as .sfs lines at the given tab depth.

    `vessel_mid` / `launch_id` come from `vessel_identity`, so the spliced parts
    inherit the vessel's own ids rather than a literal restated here."""
    position, rotation = yaw_pose_about_y(donor_position, donor_rotation,
                                          spec.yaw_degrees)
    pad = _indent(depth)
    inner = _indent(depth + 1)
    out = [pad + "PART", pad + "{"]

    # Ordered exactly as KSP writes a surface-attached part, so a diff against a
    # real save reads cleanly.
    scalars = [
        ("name", spec.name),
        ("cid", spec.cid),
        ("uid", spec.uid),
        ("mid", vessel_mid),
        ("persistentId", spec.persistent_id),
        ("launchID", launch_id),
        ("parent", str(POD_INDEX)),
        ("position", ",".join(_format_float(v) for v in position)),
        ("rotation", ",".join(_format_float(v) for v in rotation)),
        ("mirror", "1,1,1"),
        ("symMethod", "Radial"),
        # istg -1 / sidx -1 / sqor -1: not staged. These parts must never enter
        # the staging sequence - the flight leg's chute-arming logic counts on
        # the stage list B1 measured.
        ("istg", "-1"),
        ("resPri", "0"),
        ("dstg", "0"),
        ("sqor", "-1"),
        ("sepI", "-1"),
        ("sidx", "-1"),
        # attm = 1 is surface attachment; the matching `srfN` follows below.
        ("attm", "1"),
        ("sameVesselCollision", "False"),
        ("srfN", "srfAttach, %d" % POD_INDEX),
        ("mass", spec.mass),
        ("shielded", "False"),
        # The pad thermal/pressure readings the surrounding parts carry. A
        # PRELAUNCH save is written seconds after rollout, so every part on this
        # craft sits within a few hundredths of ambient.
        ("temp", "310.11348692727825"),
        ("tempExt", "310.26214957198528"),
        ("tempExtUnexp", "310.26168628562647"),
        ("staticPressureAtm", "0.9889501670011025"),
        # KSP's default explosionPotential; neither part's cfg overrides it.
        ("expt", "0.5"),
        ("state", "0"),
        ("PreFailState", "0"),
        ("attached", "True"),
        ("autostrutMode", "Off"),
        ("rigidAttachment", "False"),
        ("flag", "Squad/Flags/default"),
        # Root transform name. Neither part carries ModulePartVariants, so the
        # model root keeps the part's own name (the pod's `_default` is a
        # variant-swapped root, which is why it differs).
        ("rTrf", spec.name),
        ("modCost", "0"),
        ("modMass", "0"),
        ("moduleCargoStackableQuantity", spec.cargo_stackable),
    ]
    for key, value in scalars:
        out.append("%s%s = %s" % (inner, key, value))

    out.extend([inner + "EVENTS", inner + "{", inner + "}"])
    out.extend([inner + "ACTIONS", inner + "{", inner + "}"])
    out.extend([inner + "PARTDATA", inner + "{", inner + "}"])
    for resource in spec.resources:
        out.append(inner + "RESOURCE")
        out.append(inner + "{")
        out.extend(_render_block(resource, depth + 2))
        out.append(inner + "}")
    for module in spec.modules:
        out.append(inner + "MODULE")
        out.append(inner + "{")
        out.extend(_render_block(module, depth + 2))
        out.append(inner + "}")
    out.append(pad + "}")
    return out


def build(base_lines: List[str], title: str) -> List[str]:
    """Return the spliced save. Pure over the base line list."""
    lines = list(base_lines)

    flight_state = find_node(lines, "FLIGHTSTATE")
    if flight_state is None:
        raise SystemExit("base has no FLIGHTSTATE node")
    vessels = child_nodes(lines, flight_state, "VESSEL")
    if len(vessels) != 1:
        raise SystemExit("expected exactly 1 VESSEL in the base, got %d"
                         % len(vessels))
    vessel = vessels[0]

    parts = part_nodes(lines, vessel)
    if len(parts) <= max(POD_INDEX, POSE_DONOR_INDEX):
        raise SystemExit("base vessel has only %d parts" % len(parts))
    if get_value(lines, parts[POD_INDEX], "name") != "mk1pod.v2":
        raise SystemExit("part %d is not the mk1pod.v2 the splice attaches to"
                         % POD_INDEX)
    donor = parts[POSE_DONOR_INDEX]
    if get_value(lines, donor, "name") != "GooExperiment":
        raise SystemExit("pose donor part %d is not a GooExperiment"
                         % POSE_DONOR_INDEX)
    donor_position = _parse_vector3(get_value(lines, donor, "position") or "")
    donor_rotation = _parse_quaternion(get_value(lines, donor, "rotation") or "")
    vessel_mid, launch_id = vessel_identity(lines, parts)

    # APPEND after the last existing PART, so every existing part index - and
    # therefore every `parent`, `srfN`, `attN` and `sym` reference in the save -
    # is untouched.
    insert_at = parts[-1][1]
    rendered: List[str] = []
    for spec in SPLICE_PARTS:
        rendered.extend(render_part(spec, donor_position, donor_rotation,
                                    vessel_mid, launch_id))
    lines[insert_at:insert_at] = rendered

    set_top_value(lines, "Title", title)
    return lines


# ---------------------------------------------------------------------------
# Post-conditions.
# ---------------------------------------------------------------------------


def _resource_total(lines: List[str], part: Tuple[int, int],
                    resource_name: str) -> float:
    total = 0.0
    for node in child_nodes(lines, part, "RESOURCE"):
        if get_value(lines, node, "name") != resource_name:
            continue
        raw = get_value(lines, node, "maxAmount")
        if raw is not None:
            total += float(raw)
    return total


def _purchased_parts(lines: List[str]) -> List[str]:
    """Every `part = <name>` under the ResearchAndDevelopment Tech nodes."""
    out: List[str] = []
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return out
        if get_value(lines, node, "name") == "ResearchAndDevelopment":
            for k in range(node[0], node[1]):
                stripped = lines[k].strip()
                if stripped.startswith("part = "):
                    out.append(stripped[len("part = "):].strip())
            return out
        i = node[1]


def _has_module(lines: List[str], part: Tuple[int, int], module_name: str) -> bool:
    for node in child_nodes(lines, part, "MODULE"):
        if get_value(lines, node, "name") == module_name:
            return True
    return False


def verify(lines: List[str], base_lines: Optional[List[str]] = None,
           crew_name: str = CREW_NAME) -> List[str]:
    """Failure strings; empty means every post-condition holds.

    Layered deliberately: the BASE's post-conditions first (imported, never
    restated), then this fixture's own."""
    problems: List[str] = list(base_builder.verify(lines, crew_name))

    flight_state = find_node(lines, "FLIGHTSTATE")
    if flight_state is None:
        problems.append("no FLIGHTSTATE node")
        return problems
    vessels = child_nodes(lines, flight_state, "VESSEL")
    if len(vessels) != 1:
        problems.append("expected exactly 1 VESSEL, found %d" % len(vessels))
        return problems
    vessel = vessels[0]
    parts = part_nodes(lines, vessel)

    expected_count = 8 + len(SPLICE_PARTS)
    if len(parts) != expected_count:
        problems.append("vessel carries %d PART nodes, expected %d"
                        % (len(parts), expected_count))
        return problems

    # THE SPLICE IS PURELY ADDITIVE. The eight parts `career-pad-craft` flies
    # must be byte-identical here, so B1's measured flight profile still
    # transfers and the six specs that fly the base are unaffected by anything
    # this file does.
    if base_lines is not None:
        base_fs = find_node(base_lines, "FLIGHTSTATE")
        base_vessels = child_nodes(base_lines, base_fs, "VESSEL") if base_fs else []
        if len(base_vessels) == 1:
            base_parts = part_nodes(base_lines, base_vessels[0])
            if len(base_parts) != 8:
                problems.append("base carries %d PART nodes, expected 8"
                                % len(base_parts))
            else:
                for index, (base_part, part) in enumerate(zip(base_parts, parts)):
                    original = base_lines[base_part[0]:base_part[1]]
                    carried = lines[part[0]:part[1]]
                    if original != carried:
                        problems.append(
                            "part %d (%s) is not byte-identical to the base's"
                            % (index, get_value(lines, part, "name")))

    # Identity uniqueness. A duplicate flightID or persistentId is the failure
    # mode a hand-written PART node actually has, and it does not announce
    # itself: KSP loads the vessel and then two parts answer to one id.
    for key in ("uid", "persistentId"):
        seen = {}
        for index, part in enumerate(parts):
            value = get_value(lines, part, key)
            if value is None:
                problems.append("part %d has no %s" % (index, key))
                continue
            if value in seen:
                problems.append("%s %s is shared by parts %d and %d"
                                % (key, value, seen[value], index))
            seen[value] = index

    # Topology. Every parent / surface-attach reference must land inside the
    # part list, and the spliced parts must hang off the pod.
    for index, part in enumerate(parts):
        parent = get_value(lines, part, "parent")
        if parent is None or not parent.isdigit() or int(parent) >= len(parts):
            problems.append("part %d has out-of-range parent %r" % (index, parent))
        srf = get_value(lines, part, "srfN")
        if srf is not None:
            target = srf.split(",")[-1].strip()
            try:
                target_index = int(target)
            except ValueError:
                problems.append("part %d has unparsable srfN %r" % (index, srf))
                continue
            if target_index >= len(parts):
                problems.append("part %d srfN targets out-of-range part %d"
                                % (index, target_index))

    # DERIVED, never restated: the ids the spliced parts must carry are the ones
    # THIS vessel's pod carries. Comparing against a literal would compare the
    # splice's own constant with itself, and "carries a foreign missionID" would
    # then be true of nothing.
    expect_mid, expect_launch_id = vessel_identity(lines, parts)

    spliced = parts[8:]
    for offset, (spec, part) in enumerate(zip(SPLICE_PARTS, spliced)):
        index = 8 + offset
        name = get_value(lines, part, "name")
        if name != spec.name:
            problems.append("spliced part %d is %r, expected %r"
                            % (index, name, spec.name))
        if get_value(lines, part, "parent") != str(POD_INDEX):
            problems.append("spliced part %d does not hang off the pod" % index)
        if get_value(lines, part, "mid") != expect_mid:
            problems.append("spliced part %d carries a foreign missionID (%r, "
                            "the pod carries %r)"
                            % (index, get_value(lines, part, "mid"), expect_mid))
        if get_value(lines, part, "launchID") != expect_launch_id:
            problems.append("spliced part %d carries a foreign launchID (%r, "
                            "the pod carries %r)"
                            % (index, get_value(lines, part, "launchID"),
                               expect_launch_id))
        # A staged spliced part would edit the staging sequence the flight leg's
        # chute-arming logic was measured against.
        if get_value(lines, part, "istg") != "-1":
            problems.append("spliced part %d is staged (istg != -1)" % index)

    # THE POINT OF THE FIXTURE: a DIRECT antenna aboard. The part name is the
    # assertion, because `antennaType` lives in the part cfg and never in the
    # save - `SurfAntenna` IS `antennaType = DIRECT`.
    antennas = [p for p in parts if get_value(lines, p, "name") == "SurfAntenna"]
    if len(antennas) != 1:
        problems.append("expected exactly 1 SurfAntenna, found %d" % len(antennas))
    else:
        if not _has_module(lines, antennas[0], "ModuleDataTransmitter"):
            problems.append("the SurfAntenna persists no ModuleDataTransmitter: "
                            "kRPC enumerates transmitters, and a part that does "
                            "not persist the module is not one")

    # ...and the ElectricCharge to actually spend on it.
    electric_charge = sum(_resource_total(lines, p, "ElectricCharge") for p in parts)
    if electric_charge < TRANSMIT_EC_REQUIRED:
        problems.append("vessel carries %.0f EC, below the %.0f EC the three "
                        "experiments aboard cost to transmit through a "
                        "SurfAntenna (see the module docstring)"
                        % (electric_charge, TRANSMIT_EC_REQUIRED))

    # CAREER LEGALITY. A part the save has not purchased would fly, because a
    # persisted VESSEL loads regardless of unlock state - and would be a career
    # fixture quietly flying a part the career cannot build.
    purchased = set(_purchased_parts(lines))
    for spec in SPLICE_PARTS:
        if spec.name not in purchased:
            problems.append("%s is not in the save's purchased-parts set"
                            % spec.name)
    return problems


# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of writing it")
    parser.add_argument("--crew", default=CREW_NAME,
                        help="roster kerbal expected aboard (default: %s)"
                             % CREW_NAME)
    parser.add_argument("--target-name", default=TARGET_NAME)
    args = parser.parse_args(argv)

    base_dir = os.path.join(_SAVES, BASE_NAME)
    base_sfs = os.path.join(base_dir, "persistent.sfs")
    target_dir = os.path.join(_SAVES, args.target_name)
    target_sfs = os.path.join(target_dir, "persistent.sfs")

    if not os.path.isfile(base_sfs):
        print("FAIL: missing input fixture %s" % base_sfs)
        return 1
    base_lines = read_lines(base_sfs)

    if args.check:
        if not os.path.isfile(target_sfs):
            print("FAIL: %s does not exist" % target_sfs)
            return 1
        problems = verify(read_lines(target_sfs), base_lines, args.crew)
        for problem in problems:
            print("FAIL: %s" % problem)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    title = "%s (CAREER)" % args.target_name
    built = build(base_lines, title)
    problems = verify(built, base_lines, args.crew)
    if problems:
        for problem in problems:
            print("FAIL: %s" % problem)
        return 1

    os.makedirs(target_dir, exist_ok=True)
    write_lines(target_sfs, built)
    # The splice moves neither the vessel count nor the UT, so the base's
    # loadmeta is already correct; restamping through the same helper keeps the
    # two builders on one code path rather than two.
    write_lines(os.path.join(target_dir, "persistent.loadmeta"),
                base_builder.build_loadmeta(
                    read_lines(os.path.join(base_dir, "persistent.loadmeta")),
                    built))
    addons_src = os.path.join(base_dir, "AddOns")
    addons_dst = os.path.join(target_dir, "AddOns")
    if os.path.isdir(addons_src):
        shutil.rmtree(addons_dst, ignore_errors=True)
        shutil.copytree(addons_src, addons_dst)
    print("OK: wrote %s (base=%s parts spliced=%d)"
          % (target_dir, BASE_NAME, len(SPLICE_PARTS)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
