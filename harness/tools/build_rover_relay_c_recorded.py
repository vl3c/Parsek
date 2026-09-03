#!/usr/bin/env python3
"""Finish the harvested `rover-relay-c-recorded` fixture: the WRONG-PROOF RELAY host.

WHY THIS FIXTURE EXISTS, AND WHY IT IS NOT A DUPLICATE OF `rover-relay-recorded`.
It is the suite's only committed save that carries PERSISTED
`ROUTE_ORIGIN_PROOF` NODES THAT NAME THE WRONG ORIGIN - two of them, one per
dock hop, written by the 2026-09-02 undock binder (PR #1618) before the analysis
learned to derive the origin from the PICKUP WINDOW. Every other route fixture in
the corpus carries ZERO proof nodes (`rover-route-recorded` skips on the KSC-site
start; `rover-relay-recorded` skipped because neither docked half was a
player-typed depot). This one carries two, and both are wrong, so it is the only
bytes on which the analysis-side OVERRIDE path - "ignore a bound proof that
disagrees with the window, derive the origin from where the cargo came FROM" -
can be driven at all.

THE TWO WRONG PROOFS, quoted from the source flight's own collected KSP.log
(`logs/2026-09-03_0026_rover-c/KSP.log`, lines 22361 and 25769, one per undock):

    RouteOriginProof bound at undock: recording=39ac117a8a8b4d61b1296983e7d538a8
        ut=212.54000000003492 binding=BoundToHalfB recoveredFromStopStamp=0
        originHalf=B originRoot=3466447829 originName='C' originType=3
        originPid=612987736 guidDecision=Stamped transportRoot=549109006
        transportParts=16 pickup=Carried pickupValidated=0
        pickupDelta=[LiquidFuel=-154.4;inv:-3] startRes=1 undockRes=1 startInv=3
        undockInv=1

    RouteOriginProof bound at undock: recording=b9df0ee00fd84831a0d9619b4e34fc97
        ut=335.319999999985 binding=BoundToHalfB recoveredFromStopStamp=0
        originHalf=B originRoot=701791207 originName='A' originType=3
        originPid=4280917262 guidDecision=Stamped transportRoot=3466447829
        transportParts=16 pickup=Carried pickupValidated=0
        pickupDelta=[LiquidFuel=-200.0;inv:-4] startRes=1 undockRes=1 startInv=4
        undockInv=3

READ THE TWO `originName=` VALUES AGAINST WHAT ACTUALLY HAPPENED, because that
is the whole subject:

  * HOP 1 (dock at B, UT 155.82 -> undock 212.54) is the PICKUP: rover C took
    +154.4 LiquidFuel and 3 stored items OUT OF rover B. The correct origin is
    **B**. The binder bound **C** - the TRANSPORT ITSELF - as origin
    (`originName='C' originPid=612987736`), and named B as the transport
    (`transportRoot=549109006`, which is B's root part). The two halves are
    exactly inverted.
  * HOP 2 (dock at A, UT 274.18 -> undock 335.32) is the DELIVERY: C put 200
    LiquidFuel and 4 items INTO rover A. There is no pickup here at all, and the
    binder bound **A** - the DESTINATION - as origin
    (`originName='A' originPid=4280917262`).

BOTH SAY `pickup=Carried pickupValidated=0`, i.e. the binder itself recorded that
it never validated the pickup; it bound the half it could see at the undock
(`binding=BoundToHalfB`) and stamped the result. WHY THE INVERSION HAPPENED AT
HOP 1 rather than being a coin flip: at the hop-1 dock KSP resolved the COMBINED
vessel to B's identity (see THE IDENTITY SWAP below), so "half B" of that seam is
C, not B.

THESE BYTES ARE THE OVERRIDE'S REGRESSION SUBJECT. A fixture whose proofs agreed
with the windows would exercise nothing: the analysis would reach the same answer
by reading the proof it was given. The value here is that the proof and the window
DISAGREE on both hops, in two different ways (transport-as-origin, and
destination-as-origin), so an analysis that trusts the proof produces a route from
C to A on hop 1 and a route out of A on hop 2, and an analysis that derives the
origin from the pickup window produces exactly ONE candidate: source B,
destination A. DO NOT "REPAIR" THE PROOFS. Stripping them would turn this fixture
into a second, slightly different copy of `rover-relay-recorded` and retire the
only subject the override has.

-----------------------------------------------------------------------------
THE SOURCE. The operator's own hand-flown SANDBOX save `logistics-rover-c`, flown
2026-09-02, collected into `logs/2026-09-03_0026_rover-c/`. Three identical
16-part rovers named A, B and C on the KSC shore (Kerbin, all LANDED, one
`probeStackSmall` command part, two `ConformalStorageUnit` inventory containers of
three slots each, and one `dockingPort2`; no grapple, no converter, no drill).
C drove to B, docked at UT 155.82, loaded +154.4 LiquidFuel (B 200 -> 45.6) and
3 stored items, undocked at UT 212.54, drove to A, docked at UT 274.18, unloaded
200 LiquidFuel (A 200 -> 400) and 4 items, undocked at UT 335.32 and drove off.
The save was written at UT 410.40 from the SPACE CENTER. Harvested from a scratch
COPY, the `duna-one-recorded` / `depot-route-recorded` / `rover-route-recorded` /
`rover-relay-recorded` provenance class.

NEVER NAME THE FIXTURE AFTER THE SOURCE SAVE. `run.py::stage_fixture` rmtree's the
same-named save inside the automation instance, so a fixture called
`logistics-rover-c` would delete the operator's hand-played save the first time any
scenario staged it. Hence `rover-relay-c-recorded`, named for the LANE.

INPUT. The output of

    python harness/tools/harvest_bdock_station.py \\
        --save-dir <scratch copy> \\
        --target-name rover-relay-c-recorded \\
        --expect-situation ORBITING --keep-parsek

i.e. this tool edits `harness/fixtures/saves/rover-relay-c-recorded` IN PLACE. The
harvest did the generic half (title normalisation, TWO `rewindSave` hint clears,
`Parsek/Saves` prune, the `.craft.txt` snapshot-mirror prune); everything below is
relay-specific. `--force` was NOT passed and must not be: the situation gate is
the only thing between a clobbered source and a silently wrong fixture.

`--expect-situation ORBITING` LOOKS WRONG FOR A LANDED-ROVER FIXTURE AND IS
CORRECT, for the reason `build_rover_route_recorded.py` and
`build_rover_relay_recorded.py` both set out: the gate is armed against the
SOURCE, never against the RESULT. This source was saved from the SPACE CENTER, so
KSP left `FLIGHTSTATE/activeVessel = 0` pointing at `Ast. RQL-681`, a stock
DiscoverableObjects asteroid in solar orbit - and step 1 below re-points it to
rover `C`, LANDED. Passing LANDED at harvest time would FAIL the gate on a
HEALTHY source; passing ORBITING keeps it a real gate (a source whose index-0
vessel moved reds there), and the LANDED assertion on the vessel that actually
ends up focused lives in `_verify_active_vessel` below. Do NOT widen the harvest
gate to `ORBITING,LANDED` - that accepts either, which is what neither half is
allowed to do.

WHAT IT DOES, in order:

  1. THE ACTIVE VESSEL. Re-points `FLIGHTSTATE/activeVessel` from the asteroid at
     index 0 to rover `C` at index 5 - the TRANSPORT rover, the relay's own vessel
     and the one whose tree every lane addresses. Same three reasons as the
     sibling: an asteroid-focused boot IS focusable (`TestCommandLoadGame`'s
     `IsLoadedGameFocusable` accepts index 0) and would boot the fixture into deep
     space with all three rovers unloaded; C is where an operator would be sitting;
     and C is LANDED rather than PRELAUNCH, so it is not the fresh-rollout shape
     `RecordingStore.SceneEntryFreshRolloutVesselPid` has a fast path for. The
     index is RE-RESOLVED by name + persistentId and the constant is then asserted
     against it, so a re-harvest that reordered FLIGHTSTATE reds naming the new
     index instead of silently focusing whatever now sits at 5.
  2. THE ADDONS SCAFFOLDING. The collected save has no `AddOns/` at all; the
     618-byte `DistantObject/Settings.cfg` every sibling fixture carries is copied
     from `rover-route-recorded` (a fixture of the same class and lane family), and
     `verify_tree` re-checks its size AND the donor's bytes.
  3. THE START-OF-CYCLE ENDPOINT REPAIR. See THE REPAIR below. Both PHYSICAL
     ENDPOINTS are restored, in FLIGHTSTATE only, to the state their OWN recorded
     window holds at ITS dock; the transport and the whole Parsek payload are left
     untouched.

WHAT IT DELIBERATELY DOES NOT DO:

  * NO PROOF IS STRIPPED OR CORRECTED. See the top of this file.
  * NO TREE IS DROPPED. The three-tree forest is the point: `c4c72bdf` (rover A's
    own launch) and `6c0c38a7` (rover B's) are what make the relay's two ENDPOINTS
    committed recordings rather than bare pids. Window 2 names pid 2123618197,
    carried ONLY by `c4c72bdf`'s root recording; window 1 names pid 90564594,
    carried by `6c0c38a7`'s root recording AND by two members of the relay tree
    (see THE IDENTITY SWAP). Drop either origin tree and the fixture still LOOKS
    right - same window count, same branch points - while the cross-tree partner
    link goes to Skip.
  * NO SPACE OBJECT IS PRUNED. The six stock asteroids are kept verbatim, on the
    siblings' precedent: pruning them would move every index the re-point resolves
    against for no benefit, and no lane reads them.
  * NO SIDECAR IS SWEPT. All ten recordings have their family on disk and vice
    versa (the harvest's own orphan sweep found none).
  * NOTHING IS SEALED, and nothing needs to be. Every one of the ten RECORDING
    nodes is ALREADY `MergeState.Immutable`, which the codec spells by OMITTING
    the `mergeState` key (`RecordingTreeRecordCodec.SaveRewindToStagingMergeState`
    writes it only for a non-default value; the loader defaults a missing key to
    Immutable). So `RouteCandidateFinder.IsTreeFullySealed` is already true for all
    three trees and a `SealSlot tree=<relay>` step is expected to answer
    `total=8 sealed=0 remaining=0 alreadySealed=True` - the idempotent no-op guard.
    `verify_seal_state` is that pin, and it matters for the same reason it does on
    the sibling: `ClassifyCreateRefusal` walks found -> dismissed -> sealed ->
    eligible and returns the FIRST failure, so an unsealed tree would refuse
    `tree-not-sealed` and a create lane would prove nothing about candidacy.
  * NO PARSEK PAYLOAD IS EDITED BY THE REPAIR. Step 3 touches FLIGHTSTATE and
    nothing else - no recording, no window, no branch point, no origin proof. The
    windows are the repair's INPUT, so editing one would make the repair
    unfalsifiable. `verify_start_of_cycle_endpoints` re-reads them.
  * NO ROUTE STATE IS REVERTED, because there is none: no route was EVER created
    over this save (0 `ROUTES`, 0 `PROMPTED_ROUTE_CANDIDATES`, 0
    `DISMISSED_ROUTE_CANDIDATES`, 0 route ledger actions), so nothing in the
    FLIGHTSTATE is route output. `verify_no_route_state` states that as a positive
    fact. What step 3 reverts is the OPERATOR'S OWN hand-flown relay, which is a
    different thing.

-----------------------------------------------------------------------------
THE REPAIR: THE FIXTURE IS STAGED AT START-OF-CYCLE.

WHY IT IS NEEDED. A route REPLAYS a recorded run against the CURRENT live
endpoints. This save was written AFTER the relay finished, so the endpoints had
already absorbed it and a replay had nowhere to take cargo from or put it:

    B (pid 90564594)    LiquidFuel  45.6 / 400   1 of 6 slots   as harvested
    A (pid 4280917262)  LiquidFuel 400.0 / 400   6 of 6 slots   as harvested

`RouteDispatchEvaluator.CheckEligibility` walks endpoints (step 5) -> origin cargo
(6) -> funds (7) -> destination capacity (8) and returns the FIRST failure, and
both step 6 (`RouteOriginCargoCheck.HasRequired`, all-or-nothing) and step 8
(`RouteDestinationCapacityCheck.HasCapacityForAllStops`, all-or-nothing through
`plan.IsPartial`) were false on the harvested bytes. Any driven cycle BLOCKED and
emitted nothing.

WHAT IT DOES, AND WHY IT IS NOT AN INVENTION. Both PHYSICAL ENDPOINTS are restored
to the state THEIR OWN WINDOW recorded at ITS dock - not to a chosen number:

    B  <- window 0's DOCK_ENDPOINT_RESOURCES / DOCK_ENDPOINT_INVENTORY
    A  <- window 1's

so both hold LiquidFuel 200 / 400 and three of six inventory slots, and the
STOREDPART bytes written are LIFTED VERBATIM out of the window snapshots (inner
`persistentId` included), re-indented three tabs from the snapshot depth to the
FLIGHTSTATE depth. C, THE TRANSPORT, IS LEFT EXACTLY AS SAVED: transport credit is
bookkeeping, the pickup writer removes from the SOURCE only and the delivery writer
stores the recorded snapshot into the DESTINATION, so nothing a dispatch reads
touches the transport's own hold.

THE PRECEDENT. `build_rover_route_recorded.py` step 3 strips the two `STOREDPART`
nodes a hand-driven Send Once had already delivered into ITS endpoint, for exactly
this reason and after exactly this failure (RVR-2 flight 1 red on the delivery
tokens because the harvested endpoint had no free slot). Same class of edit, same
justification, one difference: there the polluting delivery was a ROUTE's, here it
is the operator's own hand-flown relay.

WHERE THE PARTS GO, and this is the half that was nearly got wrong. The window
snapshot records a `slotIndex` but not WHICH container, and both rovers have two.
The placement table `CRAFT_AUTHORED_INVENTORY_LAYOUT` is derived from the bytes on
two vessels independently - by inner `PART persistentId` on rover A, corroborated
by what is left on C and by B's own surviving kit - and the derivation is written
out at the constant, because A holds a station at slot 1 in BOTH containers and a
slot-index-only rule picks the WRONG one.

WHAT THE REPAIR MAKES TRUE, asserted in `verify_start_of_cycle_endpoints` against
the windows rather than against literals: B holds at least the 154.4 pickup
manifest, A has at least the 200 delivery manifest of headroom (exactly 200, since
`RouteDeliveryPlanner` takes `Math.Min(requested, freeCapacity)` and only reports
partial when that is SHORT, an exact fit is a full fit), and A's three free slots
cover the delivery's three distinct KINDS. Those are the two gates RVR-7 forbids
the hold tokens of, so if any of them stops holding that lane reds on a forbid and
this cell says why.

WHAT IS STILL NOT AVAILABLE FROM THESE BYTES: a SECOND completing cycle. After
cycle 0 the endpoints are spent again exactly as the operator left them, so RVR-7
drives ONE cycle by design rather than RVR-2's two.

-----------------------------------------------------------------------------
THE IDENTITY SWAP AT HOP 1, which is what makes this fixture structurally
different from `rover-relay-recorded` rather than a re-flight of it.

When C docked to B at UT 155.82, KSP resolved the COMBINED vessel to **B's**
identity: the dock-member recording `39ac117a` carries `vesselPersistentId =
90564594` and `recordedVesselGuid = 389423171250456f...` - both B's - where the
sibling's hop-1 dock member carried the TRANSPORT's. Three consequences, each
load-bearing somewhere:

  (a) WINDOW 1 IS INITIATOR-BRANCH. Its `transferTargetPid` (90564594) EQUALS the
      carrying recording's `vesselPersistentId`. The sibling's two windows are both
      TARGET-branch. `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` and its
      cross-tree sibling therefore have ONE target-branch subject here (window 2),
      not two. Do not copy the sibling's "two target-branch windows" claim onto this
      fixture.
  (b) THE ORIGIN BINDER INVERTED THE HALVES. `binding=BoundToHalfB` picked C,
      because half B of that seam IS C once the merged vessel took B's name. That
      is the mechanism behind wrong proof 1.
  (c) THE RELAY TREE'S ROOT HAS NO `_vessel.craft`. `8604fbc7` (C's launch, guid
      `c2abd29e...`) ends at the hop-1 dock and Parsek writes no vessel snapshot for
      a recording whose vessel was consumed by a dock merge - the same shape as the
      sibling's `31e84302`, which was pruned of nothing either (checked in BOTH
      operator sources before any harvest ran). The difference is that the sibling's
      merged vessel kept C's guid, so the `recordedVesselGuid` correlator in
      `CommittedFixtureMirrorTests` could see a same-launch sibling; here it cannot,
      which is why that cell grew a third, dock-merge-parent exemption in the same
      commit as this fixture.

-----------------------------------------------------------------------------
`--check` re-runs every post-condition against the ALREADY COMMITTED fixture and
writes nothing. It is WIRED, not decorative:
`RoverRelayCRecordedFixtureDriftTests` in
`harness/lib/test_build_rover_relay_c_recorded.py` runs the same `verify_*`
functions in-process, so a hand-edit of the committed bytes reds in the harness
suite. Like its templates it CANNOT re-run `build`: the input is a collected
operator save outside the repo that will never be committed.

Usage:
    python harness/tools/build_rover_relay_c_recorded.py            # finish in place
    python harness/tools/build_rover_relay_c_recorded.py --check    # verify only

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import math
import os
import shutil
import sys
from typing import Dict, List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")
_LIB = os.path.join(_HARNESS_ROOT, "lib")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
if _LIB not in sys.path:
    sys.path.insert(0, _LIB)

# The shared save parser, for the route facts, read in the SAME vocabulary a
# scenario declares through `[expectations.routes]`.
import saveparse  # noqa: E402
# The SHARED FLIGHTSTATE patcher. Step 3 below (the start-of-cycle repair) and the
# scenario-side `[[fixture.liveState]]` stage step do the SAME edit - lift a
# STOREDPART block out of a route window's DOCK_ENDPOINT_INVENTORY, re-indent it,
# splice it into a ModuleInventoryPart and rewrite the slot-ascending `inventory`
# CSV - so the edit is implemented ONCE, there, and both sides call it. See that
# module's header for why (the CSV/indent logic was got wrong twice while it was
# being written, and a second copy could regress silently).
import savepatch  # noqa: E402

# ONE copy of the ConfigNode-text node helpers, for the reason every sibling
# builder imports them: a second implementation is a second thing to drift. The
# FILE I/O is deliberately NOT reused - those helpers normalise to CRLF on write,
# and `harvest_bdock_station.py` writes this save's `persistent.sfs` with an
# explicit LF-only newline. Keeping the harvest's own line endings means the
# committed bytes are still "what the tool chain wrote".
import build_career_pad_craft as base_builder  # noqa: E402

find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value

TARGET_NAME = "rover-relay-c-recorded"

# The AddOns donor: the same fixture the sibling relay borrows from, so the donor
# is a fixture of the same class and the same lane family.
ADDONS_DONOR_NAME = "rover-route-recorded"
ADDONS_REL = os.path.join("AddOns", "DistantObject", "Settings.cfg")
ADDONS_EXPECTED_BYTES = 618

# `Parsek/GameState` comes through the harvest INTACT. Pinned by name so a
# re-harvest that lost the ledger reds loudly rather than shipping a thinner
# fixture: four `MilestonesModule` baselines plus the three journals.
GAMESTATE_FILES = (
    "baseline_0.pgsb",
    "baseline_106.39999999999597.pgsb",
    "baseline_375.63999999994832.pgsb",
    "baseline_48.800000000002029.pgsb",
    "events.pgse",
    "ledger.pgld",
    "milestones.pgsm",
)

# --- the three trees, kept WHOLE ----------------------------------------
#
# Spelled out rather than derived from the save so that a re-harvest whose shape
# moved reds loudly here instead of silently shipping a different payload.
ENDPOINT_A_TREE_ID = "c4c72bdf589f4e77ae76b92acca18ff2"   # rover A's own launch
ENDPOINT_B_TREE_ID = "6c0c38a787af4edea8ed915103edeb79"   # rover B's own launch
RELAY_TREE_ID = "88c012a6eed94bf09ff73397a4a31410"        # rover C, the relay

ENDPOINT_A_TREE_RECORDING_IDS = (
    "2ce8804f5f5b4bfdb4e9483cf827c593",   # 0  rover A's launch; pid 2123618197
)
ENDPOINT_B_TREE_RECORDING_IDS = (
    "4a31577192894f9ab7390db3f00bfc35",   # 0  rover B's launch; pid 90564594
)
# The relay: C's launch -> dock member at B (WINDOW 1) -> the two undock children
# -> dock member at A (WINDOW 2) -> the two undock children, the second of which
# is a two-segment chain on rover A.
RELAY_TREE_RECORDING_IDS = (
    "8604fbc77d54482eae83424b7e401954",   # 0  rover C's launch;      pid 4061189560
    "39ac117a8a8b4d61b1296983e7d538a8",   # 1  DOCK MEMBER at B (w1); pid 90564594
    "9fed706a8b85498e9f20a06aa80c3464",   # 2  rover B after undock 1; pid 90564594
    "5c8476924adb4a1d8bf0215034b69e78",   # 3  C's drive from B to A;  pid 612987736
    "b9df0ee00fd84831a0d9619b4e34fc97",   # 4  DOCK MEMBER at A (w2); pid 612987736
    "ec4bf428ea0048adbeaede46aa2f6b49",   # 5  rover C after undock 2; pid 612987736
    "a597f168e5d24e4f94f0803f80246832",   # 6  rover A after undock 2; chainIndex 0
    "4a61a530e8784a2c9322f00d18ab422f",   # 7  the same chain, chainIndex 1
)
KEEP_RECORDING_IDS = (ENDPOINT_A_TREE_RECORDING_IDS
                      + ENDPOINT_B_TREE_RECORDING_IDS
                      + RELAY_TREE_RECORDING_IDS)

# Tree id -> its recording ids in `treeOrder`, in the order the trees appear in
# the save. The order is asserted, so a re-harvest that reordered them reds.
TREES_IN_FILE_ORDER = (
    (ENDPOINT_A_TREE_ID, ENDPOINT_A_TREE_RECORDING_IDS),
    (ENDPOINT_B_TREE_ID, ENDPOINT_B_TREE_RECORDING_IDS),
    (RELAY_TREE_ID, RELAY_TREE_RECORDING_IDS),
)

# The relay tree's active recording, which is what `SealSlot`'s `total=` counts
# over and what a lane addressing this tree pins as `total=8`.
RELAY_TREE_ACTIVE_RECORDING_ID = "ec4bf428ea0048adbeaede46aa2f6b49"

# --- the TWO route windows ----------------------------------------------
#
# `saveparse.py` has no route-window facet, so without this the surfaces the
# fixture exists for would be unpinned everywhere. Every value below was read off
# the harvested bytes.
TRANSPORT_LIVE_PID = "612987736"          # rover C after the first undock
ENDPOINT_B_LIVE_PID = "90564594"          # rover B, unchanged across the relay
ENDPOINT_A_LIVE_PID = "4280917262"        # rover A AFTER Part.Undock re-pidded it
ENDPOINT_A_RECORDED_PID = "2123618197"    # rover A as its own launch recorded it

# (carrying recordingId, {scalar pins}, {ENDPOINT_AT_DOCK pins}) per window, in
# file order.
#
# `branch` is INITIATOR when `transferTargetPid` equals the carrying recording's
# `vesselPersistentId` and TARGET otherwise, and the pair (INITIATOR, TARGET) is
# itself a pin: window 1 is INITIATOR-branch only because KSP resolved the hop-1
# merged vessel to B's identity (see the header's IDENTITY SWAP section), which is
# the single structural difference from `rover-relay-recorded`.
ROUTE_WINDOWS = (
    {
        "recordingId": "39ac117a8a8b4d61b1296983e7d538a8",
        "treeId": RELAY_TREE_ID,
        "carrierPid": ENDPOINT_B_LIVE_PID,
        "branch": "INITIATOR",
        "pins": {
            "windowId": "dock-155.8200000000059-target-90564594",
            "dockUT": "155.8200000000059",
            "undockUT": "212.54000000003492",
            "transferTargetPid": ENDPOINT_B_LIVE_PID,
            "transferKind": "DockingPort",
            # 1 == Vessel.Situations.LANDED as `RouteConnectionWindow` serialises
            # it: both hops are SURFACE endpoints.
            "transferEndpointSituation": "1",
        },
        "endpoint": {
            "vesselPersistentId": ENDPOINT_B_LIVE_PID,
            "bodyName": "Kerbin",
            "latitude": "-0.1329467389109866",
            "longitude": "-74.726558010654912",
            "altitude": "65.9754490024643",
            "isSurface": "True",
        },
        # The cross-tree partner recording that carries the target pid. Window 1's
        # pid is ALSO carried by two relay-tree members (the identity swap), so the
        # assertion below is "the named partner is among the holders and lives in a
        # different tree", not "exactly one holder".
        "partner": (ENDPOINT_B_TREE_ID, ENDPOINT_B_TREE_RECORDING_IDS[0]),
    },
    {
        "recordingId": "b9df0ee00fd84831a0d9619b4e34fc97",
        "treeId": RELAY_TREE_ID,
        "carrierPid": TRANSPORT_LIVE_PID,
        "branch": "TARGET",
        "pins": {
            "windowId": "dock-274.18000000004059-target-2123618197",
            "dockUT": "274.18000000004059",
            "undockUT": "335.319999999985",
            "transferTargetPid": ENDPOINT_A_RECORDED_PID,
            "transferKind": "DockingPort",
            "transferEndpointSituation": "1",
        },
        "endpoint": {
            "vesselPersistentId": ENDPOINT_A_RECORDED_PID,
            "bodyName": "Kerbin",
            "latitude": "-0.087457906992319118",
            "longitude": "-74.779455049093031",
            "altitude": "65.952989621204324",
            "isSurface": "True",
        },
        "partner": (ENDPOINT_A_TREE_ID, ENDPOINT_A_TREE_RECORDING_IDS[0]),
    },
)

# (name, amount, maxAmount) per RESOURCE row, per window, per node.
#
# THE RESOURCE DELTAS BALANCE ON BOTH HOPS, and the DIRECTION of each is the whole
# reason one route falls out of two windows:
#   hop 1 (PICKUP)   transport 200 -> 354.4      endpoint B 200 -> 45.6
#                    = +154.4 onto the transport, -154.4 off B
#   hop 2 (DELIVERY) transport 354.4 -> 154.4    endpoint A 200 -> 400
#                    = -200 off the transport, +200 into A
# A window whose transport GAINS is a pickup and its endpoint is the SOURCE; a
# window whose transport LOSES is a delivery and its endpoint is the DESTINATION.
# That is the derivation the persisted proofs contradict, and `verify_flow_
# directions` asserts it out of the bytes rather than restating it.
ROUTE_WINDOW_RESOURCE_ROWS = (
    {
        "DOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "200", "400"),
        "UNDOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "354.3999999999952", "400"),
        "DOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "200", "400"),
        "UNDOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "45.59999999999814", "400"),
    },
    {
        "DOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "354.3999999999952", "400"),
        "UNDOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "154.39999999999196", "400"),
        "DOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "200", "400"),
        "UNDOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "400", "400"),
    },
)

# The four stored-part identity hashes the two windows use.
#
# THE TWO STATION HASHES ARE ONE PHYSICAL PART, BEFORE AND AFTER A LIVE MOVE - the
# same `ModuleGroundExpControl.OnSave` re-hash the sibling fixture holds
# (`5072997a...` craft-authored, `5bcde9ad...` after stock's
# `StoreCargoPartAtSlot(Part, int)` rebuilt a live `ProtoPartSnapshot`). SINCE
# PR #1620 THAT NO LONGER DECIDES ANYTHING: stored cargo is matched BY KIND (part
# name + variant + per-resource fill bucket, module state ignored), so the split is
# now inert here and the hashes are pinned only so a re-harvest that changed the
# payload reds. Do not re-derive a refusal from them.
HASH_STATION_ORIGINAL = ("5072997aa689d51fd423864e118d4ad8"
                         "9c4092ba6d904b9bab404f9cbb71563e")
HASH_STATION_MOVED = ("5bcde9ad10a86f2c4a30a7d53640dd29"
                      "df965bb77e0d22d96317676da9088d46")
HASH_EVA_CHUTE = ("67867f6519592202edd36ee53418f3cc"
                  "42542d7748c1f69937322e469c6e70de")
HASH_EVA_SCIENCE_KIT = ("796e8060227ad96e2631434dfb169f8f"
                        "f6a96f8b51888f532d3cee8eba4602f5")

# (partName, quantity, identityHash) per ITEM, in file order, per window.
#
# BY KIND, the two windows read:
#   hop 1  transport station 1 -> 2, chute 1 -> 2, kit 2 -> 3   (+1 / +1 / +1)
#          endpoint  station 1 -> 0, chute 1 -> 0, kit 2 -> 1   (-1 / -1 / -1)
#   hop 2  transport station 2 -> 1, chute 2 -> 1, kit 3 -> 1   (-1 / -1 / -2)
#          endpoint  station 1 -> 2, chute 1 -> 2, kit 2 -> 4   (+1 / +1 / +2)
# i.e. a CLOSED pickup at B and a CLOSED delivery at A in both dimensions, which is
# what makes the relay admissible at all under the kind-matching rule.
ROUTE_WINDOW_INVENTORY_ROWS = (
    {
        "DOCK_TRANSPORT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("evaChute", "1", HASH_EVA_CHUTE),
            ("evaScienceKit", "2", HASH_EVA_SCIENCE_KIT)),
        "UNDOCK_TRANSPORT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("DeployedCentralStation", "1", HASH_STATION_MOVED),
            ("evaChute", "2", HASH_EVA_CHUTE),
            ("evaScienceKit", "3", HASH_EVA_SCIENCE_KIT)),
        "DOCK_ENDPOINT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("evaChute", "1", HASH_EVA_CHUTE),
            ("evaScienceKit", "2", HASH_EVA_SCIENCE_KIT)),
        "UNDOCK_ENDPOINT_INVENTORY": (
            ("evaScienceKit", "1", HASH_EVA_SCIENCE_KIT),),
    },
    {
        "DOCK_TRANSPORT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("DeployedCentralStation", "1", HASH_STATION_MOVED),
            ("evaChute", "2", HASH_EVA_CHUTE),
            ("evaScienceKit", "3", HASH_EVA_SCIENCE_KIT)),
        "UNDOCK_TRANSPORT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_MOVED),
            ("evaChute", "1", HASH_EVA_CHUTE),
            ("evaScienceKit", "1", HASH_EVA_SCIENCE_KIT)),
        "DOCK_ENDPOINT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("evaChute", "1", HASH_EVA_CHUTE),
            ("evaScienceKit", "2", HASH_EVA_SCIENCE_KIT)),
        "UNDOCK_ENDPOINT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("DeployedCentralStation", "1", HASH_STATION_MOVED),
            ("evaChute", "2", HASH_EVA_CHUTE),
            ("evaScienceKit", "4", HASH_EVA_SCIENCE_KIT)),
    },
)

# --- the TWO WRONG origin proofs (the fixture's whole reason to exist) ---
#
# One per dock hop, keyed by the recording that carries it, with EVERY scalar the
# node persists. `startDockedOriginVesselPid` / `startDockedOriginVesselName` are
# the two that are WRONG, and they are wrong in two DIFFERENT ways, which is what
# makes the pair worth more than either alone: hop 1 bound the TRANSPORT and hop 2
# bound the DESTINATION.
ROUTE_ORIGIN_PROOFS = (
    {
        "recordingId": "39ac117a8a8b4d61b1296983e7d538a8",
        "correctOriginPid": ENDPOINT_B_LIVE_PID,     # B, the pickup source
        "correctOriginName": "B",
        "pins": {
            # WRONG: this is rover C, the transport itself.
            "startDockedOriginVesselPid": TRANSPORT_LIVE_PID,
            "startDockedOriginRootPartUId": "3466447829",
            "startDockedOriginVesselName": "C",
            "startDockedOriginVesselType": "3",
            # ... and the transport slot names B's root part, so the two halves
            # are exactly inverted.
            "startDockedTransportRootPartUId": "549109006",
            "startDockedTransportVesselType": "5",
            "startDockedOriginBindState": "BoundAtUndock",
            "startDockedOriginPickupValidated": "False",
            "startDockedOriginPickupKind": "Carried",
            "startDockedOriginBodyName": "Kerbin",
            "startDockedOriginLatitude": "-0.13295146560966181",
            "startDockedOriginLongitude": "-74.726557564650747",
            "startDockedOriginAltitude": "65.97386717563495",
            "startDockedOriginIsSurface": "True",
            "startDockedOriginSituation": "1",
        },
    },
    {
        "recordingId": "b9df0ee00fd84831a0d9619b4e34fc97",
        # Hop 2 is a pure DELIVERY: it has no pickup source at all, so there is no
        # correct origin for this window and the proof should not exist.
        "correctOriginPid": None,
        "correctOriginName": None,
        "pins": {
            # WRONG: this is rover A, the DESTINATION of the delivery.
            "startDockedOriginVesselPid": ENDPOINT_A_LIVE_PID,
            "startDockedOriginRootPartUId": "701791207",
            "startDockedOriginVesselName": "A",
            "startDockedOriginVesselType": "3",
            "startDockedTransportRootPartUId": "3466447829",
            "startDockedTransportVesselType": "3",
            "startDockedOriginBindState": "BoundAtUndock",
            "startDockedOriginPickupValidated": "False",
            "startDockedOriginPickupKind": "Carried",
            "startDockedOriginBodyName": "Kerbin",
            "startDockedOriginLatitude": "-0.087438601012930439",
            "startDockedOriginLongitude": "-74.77938996394073",
            "startDockedOriginAltitude": "65.962671786313877",
            "startDockedOriginIsSurface": "True",
            "startDockedOriginSituation": "1",
        },
    },
)
EXPECTED_ORIGIN_PROOF_COUNT = len(ROUTE_ORIGIN_PROOFS)

# --- the four branch points ---------------------------------------------
#
# (mergeCause-or-None, ut, parentId, targetVesselPid-or-None) per BRANCH_POINT on
# the relay tree, in file order. `type` 2 is the DOCK merge, `type` 0 the undock
# split. Pinned because `[expectations.recordings.structure] branchPoints` counts
# them by KIND and a re-harvest that lost a hop would still read "4 nodes"; what
# makes the relay a RELAY is the ALTERNATION and the parent chain.
BRANCH_POINTS = (
    ("DOCK", "155.8200000000059", "8604fbc77d54482eae83424b7e401954", "90564594"),
    (None, "212.54000000003492", "39ac117a8a8b4d61b1296983e7d538a8", None),
    ("DOCK", "274.18000000004059", "5c8476924adb4a1d8bf0215034b69e78", "2123618197"),
    (None, "335.319999999985", "b9df0ee00fd84831a0d9619b4e34fc97", None),
)

# --- the active vessel (step 1) -----------------------------------------
ACTIVE_VESSEL_INDEX = 5
ACTIVE_VESSEL_NAME = "C"
ACTIVE_VESSEL_PID = TRANSPORT_LIVE_PID
ACTIVE_VESSEL_SITUATION = "LANDED"
# What the source save left focused, asserted as NOT the vessel at the re-pointed
# index, so a re-harvest that reordered FLIGHTSTATE cannot silently re-point back.
SOURCE_ACTIVE_VESSEL_NAME = "Ast. RQL-681"

# THE THREE REAL VESSELS: (name, persistentId, type, situation, guid).
#
# NOTE THE TYPES. B is a `Rover`; C and A are `Probe`s. Neither is `Base` or
# `Station`, which is exactly why the PRE-#1618 producer would have written no
# proof at all here (the `rover-relay-recorded` shape) and why the undock binder
# that DID write two is the thing this fixture holds.
#
# NOTE THE PID SPLIT ACROSS THE UNDOCKS. Window 2 names pid 2123618197 (rover A as
# its own launch recorded it) but the LIVE rover A carries 4280917262, because
# `Part.Undock` re-pids the separated half. Unlike the sibling this does NOT strand
# the endpoint: `RouteEndpointResolver` falls back to a great-circle proximity
# search bounded by `RouteOrchestrator.SurfaceProximityRadiusMeters = 500`, and the
# live A sits ~9 m from the window's recorded `ENDPOINT_AT_DOCK` coordinates, so
# the endpoint resolves through the SurfaceProximity step. Window 1's target pid
# 90564594 is unchanged and resolves on the pid step.
REQUIRED_VESSELS = (
    (ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID, "Probe", "LANDED",
     "f5b1164112844e4fa2cbcd5dc292dc92"),
    ("B", ENDPOINT_B_LIVE_PID, "Rover", "LANDED",
     "389423171250456faca0cd7ec134bc94"),
    ("A", ENDPOINT_A_LIVE_PID, "Probe", "LANDED",
     "88943c03a148437cb3aa9be7b9d891c0"),
)
# 9 FLIGHTSTATE VESSEL nodes: the three rovers plus six stock asteroids the source
# save's DiscoverableObjects scenario had already spawned. The asteroids are kept
# verbatim - pruning them would move every index the re-point resolves against for
# no benefit, and no lane reads them.
EXPECTED_VESSEL_COUNT = 9
EXPECTED_REAL_VESSEL_COUNT = 3

# The save clock, and the three rovers' LiquidFuel / ElectricCharge AFTER THE
# START-OF-CYCLE REPAIR (step 3).
#
# THE TWO 200 / 400 READINGS ARE THE REPAIR'S OUTPUT, not the source save's: B and
# A are both restored to the LiquidFuel level their OWN window recorded at ITS
# dock. C is left exactly as saved at 154.4 / 400, which additionally satisfies the
# `Logistics` fixture requirement `UnloadedFuelVesselFixture` states
# (`reason = "no-liquidfuel-resource"` unless the active vessel carries a
# LiquidFuel RESOURCE node with positive capacity). Every rover carries exactly ONE
# LiquidFuel RESOURCE node (`mk2FuselageShortLiquid`), which the repair asserts
# before it writes.
SAVE_UT = 410.3999999999167
SAVE_UT_EPS = 1e-6
VESSEL_RESOURCES = {
    # persistentId -> {resource: (amount, maxAmount)}
    TRANSPORT_LIVE_PID: {"LiquidFuel": (154.39999999999196, 400.0),
                         "ElectricCharge": (913.1710189763058, 1000.0)},
    ENDPOINT_B_LIVE_PID: {"LiquidFuel": (200.0, 400.0),
                          "ElectricCharge": (1000.0, 1000.0)},
    ENDPOINT_A_LIVE_PID: {"LiquidFuel": (200.0, 400.0),
                          "ElectricCharge": (1000.0, 1000.0)},
}
VESSEL_RESOURCE_EPS = 1e-6

# Inventory occupancy AFTER the repair, pid -> (STOREDPART count, container count).
# Six slots per rover (two `ConformalStorageUnit` containers of three), so three
# occupied leaves three free on both endpoints. `InventorySlots` is a part-config
# property and not a save property, so the per-container 3 is not readable here;
# what IS readable is that every `slotIndex` is in {0, 1, 2} across two containers,
# which is asserted below.
VESSEL_INVENTORY = {
    TRANSPORT_LIVE_PID: (3, 2),
    ENDPOINT_B_LIVE_PID: (3, 2),
    ENDPOINT_A_LIVE_PID: (3, 2),
}
INVENTORY_CONTAINER_SLOTS = 3

# --- step 3: the START-OF-CYCLE REPAIR ----------------------------------
#
# (live vessel pid, window index) per repaired endpoint. Both are restored FROM
# THAT WINDOW'S OWN `DOCK_ENDPOINT_RESOURCES` / `DOCK_ENDPOINT_INVENTORY`
# snapshot, which is why no number below is a literal: the repair reads the
# fixture's own bytes. C is NOT in this table and is left exactly as saved.
REPAIR_TARGETS = (
    (ENDPOINT_B_LIVE_PID, 0),   # rover B, the PICKUP source, window 0
    (ENDPOINT_A_LIVE_PID, 1),   # rover A, the DELIVERY destination, window 1
)

# THE CRAFT-AUTHORED INVENTORY LAYOUT: (container index in FILE order, slotIndex,
# partName). All three rovers come off ONE craft file, so all three launched with
# this layout, and the repair places the restored stored parts into it.
#
# IT IS DERIVED FROM THE BYTES ON TWO VESSELS INDEPENDENTLY, not guessed, and the
# derivation is worth keeping because the obvious guess is WRONG:
#
#   (a) ROVER A, BY INNER `PART persistentId`. Window 1's `DOCK_ENDPOINT_INVENTORY`
#       snapshot carries chute `2518951171`, station `166246753`, kit `3419185178`.
#       Live A holds SIX stored parts, and exactly those three pids appear at
#       container 0 slot 0 (chute), container 1 slot 1 (station) and container 1
#       slot 0 (kit). The other three - container 0 slot 1 (station), container 0
#       slot 2 (kit), container 1 slot 2 (chute) - carry pids the window never saw
#       and are the four units the relay DELIVERED.
#       A SLOT-INDEX-ONLY RULE PICKS THE WRONG STATION. Both containers hold a
#       station at slot 1; the pid says the ORIGINAL is container 1's, and the
#       delivered one is container 0's. The two also differ in size (165 vs 166
#       lines): the delivered station went through a live `StoreCargoPartAtSlot`
#       and picked up `ModuleGroundExpControl.OnSave`'s `canComm = False`.
#   (b) ROVER C, BY WHAT IS LEFT. C's own chute sits at container 0 slot 0 and its
#       kit at container 1 slot 0, matching (a); the station it started with is
#       gone (delivered to A) and its container 1 slot 1 is empty, which is where
#       (a) says a station lives. The station C still holds sits at container 0
#       slot 2 - the first free slot - because it is the one it TOOK from B.
#   (c) ROVER B corroborates the kit: its one surviving stored part is a kit at
#       container 1 slot 0. (Its pid is NOT the window's - stock re-stamps the
#       remainder when one unit is taken off a stack - which is exactly why the
#       repair rewrites the node from the snapshot instead of editing a quantity.)
#
# THE VALUES LIVE IN `harness/lib/savepatch.py`, NOT HERE, and this is an ALIAS.
# The scenario-side `[[fixture.liveState]]` stage step restores an endpoint from
# a window snapshot through the SAME functions this builder uses, so it needs the
# same layout; two copies of a table derived this carefully would be two things
# to drift, and the drift would be invisible (both sides would still produce a
# save that loads). The derivation above stays here, next to the flight it was
# read off; the table it describes is one object shared by both callers.
CRAFT_AUTHORED_INVENTORY_LAYOUT = savepatch.INVENTORY_LAYOUTS[TARGET_NAME]

# Straight-line separations between the three LANDED rovers, in metres, computed
# from their own FLIGHTSTATE lat / lon / alt against Kerbin's radius. Pinned to
# one decimal with a 1 m tolerance: the point is the SCALE (hundreds of metres,
# far outside the ~200 m dock range and well inside physics range), not the digit.
KERBIN_RADIUS_M = 600000.0
VESSEL_SEPARATIONS = {
    ("A", "B"): 730.9,
    ("A", "C"): 313.1,
    ("B", "C"): 1041.0,
}
VESSEL_SEPARATION_TOLERANCE_M = 1.0

# Harvest exhaust and derived data no fixture may carry. `Saves` and `analysis`
# are the two worth naming: the source carries `Parsek/Saves` (FIVE `parsek_rw_*`
# plus a `parsek_career_start.sfs`, all pruned by the harvest, with the TWO
# `rewindSave` hints that referenced two of them cleared), and running
# `analyze-recordings.ps1` against the fixture WRITES an `analysis/` directory into
# it that must not be committed.
FORBIDDEN_DIR_NAMES = ("_quarantine", "Saves", "Backup", "RewindPoints", "Ships",
                       "analysis")
FORBIDDEN_FILE_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")
FORBIDDEN_FILE_PREFIXES = ("quicksave",)

# One recording legitimately carries no vessel snapshot of its own: the relay
# tree's ROOT, whose vessel was consumed by the hop-1 dock merge. Verified absent
# in the OPERATOR'S OWN SOURCE before any harvest ran, so nothing was pruned. See
# the header's IDENTITY SWAP (c).
NO_VESSEL_CRAFT_RECORDING_IDS = ("8604fbc77d54482eae83424b7e401954",)
NO_GHOST_CRAFT_RECORDING_IDS = ()
# 10 x .prec + 10 x .pann + 9 x _vessel.craft + 10 x _ghost.craft.
EXPECTED_AUTHORITATIVE_SIDECARS = 39


# ---------------------------------------------------------------------------
# File I/O that preserves the harvest's LF line endings.
# ---------------------------------------------------------------------------


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def read_lines(path: str) -> List[str]:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


def write_lines(path: str, lines: List[str]) -> None:
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write("\n".join(lines))


def parsek_scenario(lines: List[str]) -> Optional[Tuple[int, int]]:
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == "ParsekScenario":
            return node
        i = node[1]


def flightstate_node(lines: List[str]) -> Optional[Tuple[int, int]]:
    return find_node(lines, "FLIGHTSTATE")


def vessel_records(lines: List[str]) -> List[dict]:
    """(name, pid, guid, type, sit, lat/lon/alt) per FLIGHTSTATE VESSEL, in
    `activeVessel` order.

    Scoped to FLIGHTSTATE's DIRECT children, for the reason
    `harvest_bdock_station.flightstate_span` spells out: `activeVessel` is an
    index into exactly those nodes, and a VESSEL node living anywhere else in the
    save would shift every index by one."""
    fs = flightstate_node(lines)
    if fs is None:
        return []
    out = []
    for node in child_nodes(lines, fs, "VESSEL"):
        out.append({"name": get_value(lines, node, "name"),
                    "pid": get_value(lines, node, "persistentId"),
                    "guid": get_value(lines, node, "pid"),
                    "type": get_value(lines, node, "type"),
                    "sit": get_value(lines, node, "sit"),
                    "lat": get_value(lines, node, "lat"),
                    "lon": get_value(lines, node, "lon"),
                    "alt": get_value(lines, node, "alt"),
                    "span": node})
    return out


def _stem(name: str) -> str:
    """The recording id a sidecar file belongs to.

    `.txt` IS STRIPPED FIRST (the `depot-route-recorded` correction): without it
    `X_ghost.craft.txt` falls through to `name.split('.')[0]` and reads as a
    family called `X_ghost`."""
    if name.endswith(".txt"):
        name = name[:-len(".txt")]
    for suffix in ("_vessel.craft", "_ghost.craft"):
        if name.endswith(suffix):
            return name[:-len(suffix)]
    return name.split(".")[0]


# ---------------------------------------------------------------------------
# Step 1: the active-vessel re-point.
# ---------------------------------------------------------------------------


def repoint_active_vessel(lines: List[str]) -> Tuple[List[str], str]:
    """Point `FLIGHTSTATE/activeVessel` at the transport rover `C`.

    Returns (lines, note). The index is RE-RESOLVED from the VESSEL list by name
    + persistentId rather than taken from the constant, and the constant is then
    asserted against it - so a re-harvest that reordered FLIGHTSTATE reds naming
    the new index instead of silently focusing whatever now sits at 5."""
    out = list(lines)
    fs = flightstate_node(out)
    if fs is None:
        raise SystemExit("harvested save has no FLIGHTSTATE node")

    records = vessel_records(out)
    if len(records) != EXPECTED_VESSEL_COUNT:
        raise SystemExit(
            "FLIGHTSTATE carries %d VESSEL nodes, expected %d - the source save "
            "moved and every index in this recipe must be re-derived"
            % (len(records), EXPECTED_VESSEL_COUNT))

    matches = [i for i, r in enumerate(records)
               if r["name"] == ACTIVE_VESSEL_NAME and r["pid"] == ACTIVE_VESSEL_PID]
    if len(matches) != 1:
        raise SystemExit(
            "expected exactly one VESSEL named %r with persistentId %s, found %d"
            % (ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID, len(matches)))
    index = matches[0]
    if index != ACTIVE_VESSEL_INDEX:
        raise SystemExit(
            "%r resolves to FLIGHTSTATE index %d, not the documented %d - "
            "re-derive ACTIVE_VESSEL_INDEX before writing"
            % (ACTIVE_VESSEL_NAME, index, ACTIVE_VESSEL_INDEX))
    if records[index]["sit"] != ACTIVE_VESSEL_SITUATION:
        raise SystemExit(
            "%r is %r, expected %r" % (ACTIVE_VESSEL_NAME, records[index]["sit"],
                                       ACTIVE_VESSEL_SITUATION))

    before = get_value(out, fs, "activeVessel")
    if not set_value(out, fs, "activeVessel", str(index)):
        raise SystemExit("FLIGHTSTATE has no activeVessel value to rewrite")
    return out, "activeVessel %s -> %d (%r, pid %s, %s)" % (
        before, index, ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID,
        ACTIVE_VESSEL_SITUATION)


# ---------------------------------------------------------------------------
# Step 3: the START-OF-CYCLE REPAIR.
# ---------------------------------------------------------------------------


# THE FOUR SHARED HELPERS ARE ALIASES, NOT COPIES. `savepatch.py` owns the
# implementation (see the import note at the top of this file); binding the names
# here keeps this module's call sites and its `--check` post-conditions reading
# the way they always have, while making a drift between the build-time repair
# and the scenario-side `[[fixture.liveState]]` stage step IMPOSSIBLE rather than
# merely discouraged. `test_savepatch.py` asserts the identity with `is`.
_inventory_modules = savepatch.inventory_modules


# The snapshot-lift and module-key indents, same aliasing rule as above.
SNAPSHOT_INDENT_STRIP = savepatch.SNAPSHOT_INDENT_STRIP
MODULE_KEY_INDENT = savepatch.MODULE_KEY_INDENT
# The depth a STOREDPART node itself sits at inside `STOREDPARTS`.
STOREDPART_INDENT = "\t\t\t\t\t\t"


_window_dock_endpoint_stored_parts = savepatch.dock_endpoint_stored_parts


def repair_start_of_cycle_endpoints(lines: List[str]) -> Tuple[List[str], List[str]]:
    """Restore BOTH physical endpoints to their own window's DOCK-TIME state.

    WHY A FIXTURE IS ALLOWED TO DO THIS, and the precedent it follows.
    `build_rover_route_recorded.py` step 3 strips the two `STOREDPART` nodes a
    hand-driven Send Once had already delivered into ITS endpoint, for exactly
    this reason: a route REPLAYS a recorded run against the CURRENT live
    endpoints, so a fixture harvested after the run has already consumed the
    headroom the replay needs. The subject a dispatch lane wants is the state at
    the START OF THE NEXT CYCLE, and that state is not invented - it is the
    dock-time state the fixture's OWN windows recorded.

    WHAT IS TOUCHED AND WHAT IS NOT. FLIGHTSTATE only, and only the two ENDPOINT
    rovers:
      * B (the PICKUP source) goes back to window 0's `DOCK_ENDPOINT_RESOURCES`
        LiquidFuel and window 0's `DOCK_ENDPOINT_INVENTORY` - the three items C
        took from it.
      * A (the DELIVERY destination) goes back to window 1's, freeing the slots
        the four delivered units occupy.
      * C (the transport) is left EXACTLY as saved. Transport credit is
        bookkeeping: the pickup writer removes from the SOURCE only, and the
        delivery writer stores the recorded snapshot into the DESTINATION, so
        nothing in a dispatch reads the transport's own hold.
      * THE PARSEK PAYLOAD IS NEVER TOUCHED. No recording, no window, no branch
        point, no origin proof. The windows are the repair's INPUT, so editing one
        would make the repair unfalsifiable.

    Returns (lines, notes)."""
    out = list(lines)
    notes: List[str] = []

    scn = parsek_scenario(out)
    if scn is None:
        raise SystemExit("harvested save has no ParsekScenario node")
    windows, _rec_pids = _window_records(out, scn)
    if len(windows) != len(ROUTE_WINDOWS):
        raise SystemExit("expected %d route windows, found %d"
                         % (len(ROUTE_WINDOWS), len(windows)))

    # Bottom-up over the FLIGHTSTATE vessels so an earlier vessel's edit cannot
    # invalidate a later vessel's span.
    targets = []
    for pid, window_index in REPAIR_TARGETS:
        records = [r for r in vessel_records(out) if r["pid"] == pid]
        if len(records) != 1:
            raise SystemExit("expected exactly one FLIGHTSTATE vessel with "
                             "persistentId %s, found %d" % (pid, len(records)))
        targets.append((records[0]["span"][0], pid, window_index))
    targets.sort(reverse=True)

    for _start, pid, window_index in targets:
        window = windows[window_index][3]
        record = [r for r in vessel_records(out) if r["pid"] == pid][0]
        name = record["name"]

        # --- the tank ---------------------------------------------------
        want_amount = None
        for holder in child_nodes(out, window, "DOCK_ENDPOINT_RESOURCES"):
            for row in child_nodes(out, holder, "RESOURCE"):
                if get_value(out, row, "name") == "LiquidFuel":
                    want_amount = get_value(out, row, "amount")
        if want_amount is None:
            raise SystemExit("window %d has no DOCK_ENDPOINT_RESOURCES LiquidFuel "
                             "row to restore %s from" % (window_index, name))

        tanks = []
        for part in child_nodes(out, record["span"], "PART"):
            for res in child_nodes(out, part, "RESOURCE"):
                if get_value(out, res, "name") == "LiquidFuel":
                    tanks.append(res)
        if len(tanks) != 1:
            raise SystemExit(
                "%s carries %d LiquidFuel RESOURCE node(s), expected exactly 1 - "
                "the repair would have to decide how to split the restore"
                % (name, len(tanks)))
        before = get_value(out, tanks[0], "amount")
        if not set_value(out, tanks[0], "amount", want_amount):
            raise SystemExit("%s's LiquidFuel RESOURCE has no amount to rewrite"
                             % name)
        notes.append("%s LiquidFuel %s -> %s (window %d dock endpoint)"
                     % (name, before, want_amount, window_index))

        # --- the containers ---------------------------------------------
        # The placement is planned by the SHARED planner (savepatch), so the
        # build-time repair and the scenario-side `restore-dock-endpoint:<N>`
        # mode cannot disagree about where a snapshot item lands. Its faults are
        # LiveStatePatchError; this tool's contract is a clean SystemExit, so
        # they are re-raised as one rather than surfacing as a traceback.
        try:
            stored = _window_dock_endpoint_stored_parts(out, window)
            placement = savepatch.plan_container_entries(
                stored, CRAFT_AUTHORED_INVENTORY_LAYOUT,
                "window %d's DOCK_ENDPOINT_INVENTORY" % window_index)
        except savepatch.LiveStatePatchError as ex:
            raise SystemExit(str(ex))
        if len(placement) > len(_inventory_modules(out, record["span"])):
            raise SystemExit("%s has fewer inventory containers than the layout "
                             "addresses" % name)

        # Bottom-up again, for the same span reason.
        modules = list(enumerate(_inventory_modules(out, record["span"])))
        for container_index, module in reversed(modules):
            try:
                out = _rewrite_container(out, module,
                                         placement.get(container_index, []))
            except savepatch.LiveStatePatchError as ex:
                raise SystemExit("%s: %s" % (name, ex))
        notes.append("%s inventory -> %d stored part(s) from window %d's dock "
                     "snapshot" % (name, len(stored), window_index))

    return out, notes


_rewrite_container = savepatch.rewrite_container


# ---------------------------------------------------------------------------
# Post-conditions. Run on the freshly finished fixture AND on --check.
# ---------------------------------------------------------------------------


def verify_save(lines: List[str]) -> List[str]:
    """Failure strings for the save half of the fixture (empty = all hold)."""
    problems: List[str] = []

    scn = parsek_scenario(lines)
    if scn is None:
        return ["no ParsekScenario SCENARIO node"]

    trees = child_nodes(lines, scn, "RECORDING_TREE")
    ids = [get_value(lines, t, "id") for t in trees]
    want_ids = [tid for tid, _recs in TREES_IN_FILE_ORDER]
    if ids != want_ids:
        problems.append("RECORDING_TREE ids are %r, expected the three kept trees %r"
                        % (ids, want_ids))
    else:
        for tree, (tree_id, want) in zip(trees, TREES_IN_FILE_ORDER):
            recs = child_nodes(lines, tree, "RECORDING")
            got = [get_value(lines, r, "recordingId") for r in recs]
            if got != list(want):
                problems.append("tree %s carries recordings %r, expected %r"
                                % (tree_id, got, list(want)))
            orders = [get_value(lines, r, "treeOrder") for r in recs]
            if orders != [str(i) for i in range(len(orders))]:
                problems.append("tree %s treeOrders are %r, expected 0..%d"
                                % (tree_id, orders, len(orders) - 1))
        relay = trees[want_ids.index(RELAY_TREE_ID)]
        active = get_value(lines, relay, "activeRecordingId")
        if active != RELAY_TREE_ACTIVE_RECORDING_ID:
            problems.append(
                "the relay tree's activeRecordingId is %r, expected %r"
                % (active, RELAY_TREE_ACTIVE_RECORDING_ID))

    # Every schema generation in the save must be the current one; a stray older
    # value would be a recording RecordingStore rejects at load.
    gens = {line.strip().split("=", 1)[1].strip() for line in lines
            if line.strip().startswith("recordingSchemaGeneration = ")}
    if gens != {"4"}:
        problems.append("recordingSchemaGeneration values are %r, expected {'4'}"
                        % (sorted(gens),))

    # The harvester clears these; INV9's dangling-hint WARN depends on it, and
    # `CommittedFixtureRewindSaveTests` forbids both the payload and the pointer.
    dangling = [i for i, line in enumerate(lines, 1)
                if line.strip().startswith("rewindSave = parsek_rw_")]
    if dangling:
        problems.append("a rewindSave = parsek_rw_* hint survived at line(s) %s"
                        % (dangling,))

    for name in ("RECORDING_SUPERSEDES", "LEDGER_TOMBSTONES", "REWIND_POINTS",
                 "REWIND_RETIREMENTS"):
        if child_nodes(lines, scn, name):
            problems.append("ParsekScenario carries a %s node" % name)

    problems += verify_no_route_state(lines, scn)
    problems += verify_seal_state(lines, scn)
    problems += verify_branch_points(lines, scn)
    problems += verify_route_windows(lines, scn)
    problems += verify_flow_directions(lines, scn)
    problems += verify_wrong_origin_proofs(lines, scn)
    problems += _verify_active_vessel(lines)
    problems += verify_start_of_cycle_endpoints(lines)
    problems += verify_geometry(lines)
    return problems


def verify_no_route_state(lines: List[str],
                          scn: Tuple[int, int]) -> List[str]:
    """NO ROUTE WAS EVER CREATED OVER THESE BYTES, AS THREE POSITIVE ABSENCES.

      (a) NO `ROUTES` NODE. That is what gives a create lane something to do:
          with one, `RouteCommand action=create` would answer
          `candidate-already-promoted` through `IsSourceAlreadyPromoted`.
      (b) NO `PROMPTED_ROUTE_CANDIDATES`. The DLL that produced this save is the
          one that wrote the two wrong proofs and had not yet learned to derive
          the origin from the pickup window, so it never offered the relay tree.
          A prompted row appearing after a re-harvest would mean the offer was
          made by a different DLL and the fixture is no longer the override's
          subject.
      (c) NO `DISMISSED_ROUTE_CANDIDATES`. A dismissed tree is skipped by the
          finder BEFORE the analysis runs, so a dismissal row would make a create
          answer `candidate-dismissed` - a different verdict for a different
          reason, with every other facet still reading correct.

    NOTE WHAT IS DELIBERATELY *NOT* ASSERTED HERE, and it is the whole difference
    from `build_rover_relay_recorded.py::verify_no_route_state`: the ABSENCE of
    `ROUTE_ORIGIN_PROOF`. This fixture carries TWO, both wrong, and that is its
    product. `verify_wrong_origin_proofs` pins them."""
    problems: List[str] = []

    snap = saveparse.parse_parsek_scenario("\n".join(lines))
    if not snap.parsed:
        return problems + ["saveparse could not read the save: %s" % snap.error]

    facet = saveparse.observed_routes_facets(snap)
    if facet["count"] or facet["dormant"]:
        problems.append(
            "ParsekScenario carries %d committed / %d dormant route(s) - NO route "
            "was ever created over this save, and a create lane's whole product is "
            "the ADMISSION of one" % (facet["count"], facet["dormant"]))
    if snap.prompted_candidate_tree_ids:
        problems.append(
            "PROMPTED_ROUTE_CANDIDATES names %r - the DLL that wrote these bytes "
            "never offered the relay tree, so a prompted row means the save came "
            "from a different build" % (list(snap.prompted_candidate_tree_ids),))
    if snap.dismissed_candidate_tree_ids:
        problems.append(
            "ParsekScenario carries DISMISSED_ROUTE_CANDIDATES %r - a dismissed "
            "tree is skipped BEFORE the analysis, so a create would answer "
            "candidate-dismissed rather than reaching candidacy at all"
            % (list(snap.dismissed_candidate_tree_ids),))
    return problems


def verify_seal_state(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """THE SEAL PIN.

    `RecordingTreeRecordCodec` OMITS `mergeState` for the default
    `MergeState.Immutable` and the loader defaults a missing key back to it, so
    "every recording is Immutable" is spelled in the bytes as "no RECORDING node
    carries a mergeState key at all". That makes
    `RouteCandidateFinder.IsTreeFullySealed` true for all three trees WITHOUT any
    seal being driven, which is the no-op shape a `SealSlot` step asserts.

    IT IS WHAT MAKES A CREATE VERDICT ATTRIBUTABLE. `ClassifyCreateRefusal` walks
    found -> dismissed -> sealed -> eligible and returns the FIRST failure, so an
    unsealed tree would refuse `tree-not-sealed` and the lane would prove nothing
    about candidacy either way.

    Stated as an absence over the WHOLE save rather than per node, because a stray
    key anywhere - including on a tree no lane addresses - would flip the same
    predicate."""
    problems: List[str] = []
    stray = [i for i, line in enumerate(lines, 1)
             if line.strip().startswith("mergeState = ")]
    if stray:
        problems.append(
            "a mergeState key survives at line(s) %s: at least one recording is "
            "NOT Immutable, so IsTreeFullySealed is false and any "
            "`SealSlot ... alreadySealed=True` pin is a lie" % (stray,))

    # And the same claim from the other side: the count of RECORDING nodes the
    # seal predicate would walk, so a fixture that lost recordings cannot pass the
    # absence check vacuously. The RELAY tree's own count is asserted separately
    # because a lane pins it as `total=8`.
    total = 0
    relay_total = None
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        count = len(child_nodes(lines, tree, "RECORDING"))
        total += count
        if get_value(lines, tree, "id") == RELAY_TREE_ID:
            relay_total = count
    if total != len(KEEP_RECORDING_IDS):
        problems.append("the three trees carry %d RECORDING node(s), expected %d"
                        % (total, len(KEEP_RECORDING_IDS)))
    if relay_total != len(RELAY_TREE_RECORDING_IDS):
        problems.append(
            "the relay tree carries %r RECORDING node(s), expected %d - a lane "
            "pins `sealslot complete ... total=%d`"
            % (relay_total, len(RELAY_TREE_RECORDING_IDS),
               len(RELAY_TREE_RECORDING_IDS)))
    return problems


def verify_branch_points(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """THE FOUR-HOP TOPOLOGY, pinned by KIND and by parent rather than by count.

    `[expectations.recordings.structure] branchPoints` counts Dock / Undock, and a
    re-harvest that lost one hop and gained a different one would still read four
    nodes. What makes the relay a RELAY is the ALTERNATION and the parent chain:
    dock at B -> undock -> dock at A -> undock, each one's parent being the
    previous one's product."""
    problems: List[str] = []
    got = []
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        tree_id = get_value(lines, tree, "id")
        for node in child_nodes(lines, tree, "BRANCH_POINT"):
            if tree_id != RELAY_TREE_ID:
                problems.append("tree %s carries a BRANCH_POINT; only the relay "
                                "tree may" % tree_id)
            got.append((get_value(lines, node, "mergeCause"),
                        get_value(lines, node, "ut"),
                        get_value(lines, node, "parentId"),
                        get_value(lines, node, "targetVesselPid")))
    if got != [tuple(b) for b in BRANCH_POINTS]:
        problems.append("BRANCH_POINTs are %r, expected %r"
                        % (got, [tuple(b) for b in BRANCH_POINTS]))
    return problems


def _window_records(lines: List[str],
                    scn: Tuple[int, int]) -> Tuple[List[tuple], Dict[str, str]]:
    """([(treeId, recordingId, recordingPid, windowNode)], recordingId -> pid)."""
    windows = []
    rec_pids: Dict[str, str] = {}
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        tree_id = get_value(lines, tree, "id")
        for rec in child_nodes(lines, tree, "RECORDING"):
            rec_id = get_value(lines, rec, "recordingId")
            rec_pid = get_value(lines, rec, "vesselPersistentId")
            rec_pids[rec_id] = rec_pid
            for holder in child_nodes(lines, rec, "ROUTE_CONNECTION_WINDOWS"):
                for w in child_nodes(lines, holder, "WINDOW"):
                    windows.append((tree_id, rec_id, rec_pid, w))
    return windows, rec_pids


def _rec_tree(lines: List[str], scn: Tuple[int, int]) -> Dict[str, str]:
    """recordingId -> the id of the tree that carries it."""
    out: Dict[str, str] = {}
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        tree_id = get_value(lines, tree, "id")
        for rec in child_nodes(lines, tree, "RECORDING"):
            out[get_value(lines, rec, "recordingId")] = tree_id
    return out


def verify_route_windows(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """THE TWO-WINDOW PIN. `saveparse.py` has no route-window facet, so this is
    the only place the fixture's central surface is asserted.

    Per window it states:
      (a) which recording and tree carries it, and the carrier's own pid;
      (b) its scalar shape (ids, clocks, kind, LANDED situation) and its
          ENDPOINT_AT_DOCK coordinates;
      (c) its BRANCH - INITIATOR when `transferTargetPid` equals the carrying
          recording's `vesselPersistentId`, TARGET otherwise. The (INITIATOR,
          TARGET) pair is the structural signature of the hop-1 identity swap and
          is what makes this fixture different from `rover-relay-recorded`, where
          both windows are TARGET-branch;
      (d) that the named cross-tree partner recording carries the target pid and
          lives in a DIFFERENT tree. Stated as membership rather than as "exactly
          one holder" BECAUSE of (c): window 1's pid is also carried by two relay
          tree members;
      (e) the four RESOURCE rows and the four INVENTORY rows verbatim."""
    problems: List[str] = []
    windows, rec_pids = _window_records(lines, scn)
    rec_tree = _rec_tree(lines, scn)

    if len(windows) != len(ROUTE_WINDOWS):
        return problems + [
            "the save carries %d ROUTE_CONNECTION_WINDOWS WINDOW node(s), "
            "expected exactly %d" % (len(windows), len(ROUTE_WINDOWS))]

    for i, ((tree_id, rec_id, rec_pid, window), want) in enumerate(
            zip(windows, ROUTE_WINDOWS)):
        tag = "window %d (%s)" % (i, want["pins"]["windowId"])
        if rec_id != want["recordingId"]:
            problems.append("%s rides recording %r, expected %r"
                            % (tag, rec_id, want["recordingId"]))
        if tree_id != want["treeId"]:
            problems.append("%s rides tree %r, expected %r"
                            % (tag, tree_id, want["treeId"]))
        if rec_pid != want["carrierPid"]:
            problems.append("%s's carrier vesselPersistentId is %r, expected %r"
                            % (tag, rec_pid, want["carrierPid"]))

        for key, value in sorted(want["pins"].items()):
            got = get_value(lines, window, key)
            if got != value:
                problems.append("%s %s is %r, expected %r"
                                % (tag, key, got, value))

        # (c) THE BRANCH, stated as the predicate an in-game cell evaluates
        # rather than as two constants that happen to differ.
        target = get_value(lines, window, "transferTargetPid")
        branch = "INITIATOR" if target == rec_pid else "TARGET"
        if branch != want["branch"]:
            problems.append(
                "%s is %s-branch (transferTargetPid %r vs carrier pid %r), "
                "expected %s-branch - the hop-1 identity swap is what makes this "
                "fixture structurally different from rover-relay-recorded"
                % (tag, branch, target, rec_pid, want["branch"]))

        # (d) THE CROSS-TREE LINK, per window.
        want_tree, want_rec = want["partner"]
        holders = sorted(r for r, p in rec_pids.items() if p == target)
        if want_rec not in holders:
            problems.append(
                "%s's target pid %r is carried by recording(s) %r, which does not "
                "include the partner %r (tree %s must be kept WHOLE)"
                % (tag, target, holders, want_rec, want_tree))
        elif rec_tree.get(want_rec) != want_tree:
            problems.append(
                "%s's partner recording %r lives in tree %r, expected %r"
                % (tag, want_rec, rec_tree.get(want_rec), want_tree))
        elif rec_tree.get(want_rec) == tree_id:
            problems.append(
                "%s's partner recording %r lives in the SAME tree as the window, "
                "so there is no cross-tree link to walk" % (tag, want_rec))

        endpoints = child_nodes(lines, window, "ENDPOINT_AT_DOCK")
        if len(endpoints) != 1:
            problems.append("%s has %d ENDPOINT_AT_DOCK node(s), expected 1"
                            % (tag, len(endpoints)))
        else:
            for key, value in sorted(want["endpoint"].items()):
                got = get_value(lines, endpoints[0], key)
                if got != value:
                    problems.append("%s ENDPOINT_AT_DOCK %s is %r, expected %r"
                                    % (tag, key, got, value))

        for node_name, row in sorted(ROUTE_WINDOW_RESOURCE_ROWS[i].items()):
            holder = child_nodes(lines, window, node_name)
            if len(holder) != 1:
                problems.append("%s has %d %s node(s), expected 1"
                                % (tag, len(holder), node_name))
                continue
            rows = [tuple(get_value(lines, r, k)
                          for k in ("name", "amount", "maxAmount"))
                    for r in child_nodes(lines, holder[0], "RESOURCE")]
            if rows != [row]:
                problems.append("%s %s rows are %r, expected %r"
                                % (tag, node_name, rows, [row]))

        for node_name, want_rows in sorted(ROUTE_WINDOW_INVENTORY_ROWS[i].items()):
            holder = child_nodes(lines, window, node_name)
            if len(holder) != 1:
                problems.append("%s has %d %s node(s), expected 1"
                                % (tag, len(holder), node_name))
                continue
            rows = [(get_value(lines, it, "partName"),
                     get_value(lines, it, "quantity"),
                     get_value(lines, it, "identityHash"))
                    for it in child_nodes(lines, holder[0], "ITEM")]
            if rows != list(want_rows):
                problems.append("%s %s items are %r, expected %r"
                                % (tag, node_name, rows, list(want_rows)))
    return problems


def verify_flow_directions(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """THE PICKUP-THEN-DELIVERY SHAPE, DERIVED FROM THE BYTES.

    This is the claim the two persisted proofs contradict, so it is computed here
    rather than restated: over each window, the transport's LiquidFuel delta and
    the endpoint's must be equal and opposite, window 0's transport delta must be
    POSITIVE (a pickup - the endpoint B is the SOURCE) and window 1's NEGATIVE (a
    delivery - the endpoint A is the DESTINATION). The same directions hold in the
    inventory dimension, checked BY KIND (part name only, ignoring the identity
    hash) because that is the rule PR #1620 settled on.

    Together they are the whole derivation "source B, destination A" rests on. A
    re-harvest that flattened either direction would leave a fixture that still
    parses as a two-window relay while no longer supporting one route."""
    problems: List[str] = []
    windows, _rec_pids = _window_records(lines, scn)
    if len(windows) != len(ROUTE_WINDOWS):
        return ["cannot derive flow directions: %d window(s), expected %d"
                % (len(windows), len(ROUTE_WINDOWS))]

    def resource_total(window, node_name: str) -> float:
        total = 0.0
        for holder in child_nodes(lines, window, node_name):
            for row in child_nodes(lines, holder, "RESOURCE"):
                if get_value(lines, row, "name") != "LiquidFuel":
                    continue
                try:
                    total += float(get_value(lines, row, "amount") or "0")
                except ValueError:
                    pass
        return total

    def kind_totals(window, node_name: str) -> Dict[str, int]:
        out: Dict[str, int] = {}
        for holder in child_nodes(lines, window, node_name):
            for item in child_nodes(lines, holder, "ITEM"):
                key = get_value(lines, item, "partName") or ""
                try:
                    qty = int(get_value(lines, item, "quantity") or "0")
                except ValueError:
                    qty = 0
                out[key] = out.get(key, 0) + qty
        return out

    want_signs = (1.0, -1.0)          # window 0 pickup, window 1 delivery
    want_labels = ("PICKUP", "DELIVERY")
    for i, (_tree, _rec, _pid, window) in enumerate(windows):
        transport = (resource_total(window, "UNDOCK_TRANSPORT_RESOURCES")
                     - resource_total(window, "DOCK_TRANSPORT_RESOURCES"))
        endpoint = (resource_total(window, "UNDOCK_ENDPOINT_RESOURCES")
                    - resource_total(window, "DOCK_ENDPOINT_RESOURCES"))
        if abs(transport + endpoint) > 1e-6:
            problems.append(
                "window %d's LiquidFuel deltas do not balance (transport %+.6f, "
                "endpoint %+.6f) - a window whose two halves disagree witnesses "
                "no transfer at all" % (i, transport, endpoint))
        if abs(transport) < 1e-6:
            problems.append("window %d moved no LiquidFuel at all" % i)
        elif (transport > 0.0) != (want_signs[i] > 0.0):
            problems.append(
                "window %d is not a %s: the transport's LiquidFuel delta is "
                "%+.6f, expected the opposite sign - the 'source B, destination A' "
                "derivation this fixture exists for no longer holds"
                % (i, want_labels[i], transport))

        t_dock = kind_totals(window, "DOCK_TRANSPORT_INVENTORY")
        t_undock = kind_totals(window, "UNDOCK_TRANSPORT_INVENTORY")
        e_dock = kind_totals(window, "DOCK_ENDPOINT_INVENTORY")
        e_undock = kind_totals(window, "UNDOCK_ENDPOINT_INVENTORY")
        kinds = sorted(set(t_dock) | set(t_undock) | set(e_dock) | set(e_undock))
        moved = 0
        for kind in kinds:
            t_delta = t_undock.get(kind, 0) - t_dock.get(kind, 0)
            e_delta = e_undock.get(kind, 0) - e_dock.get(kind, 0)
            if t_delta + e_delta != 0:
                problems.append(
                    "window %d's %s does not close BY KIND (transport %+d, "
                    "endpoint %+d) - since PR #1620 an unclosed kind is what "
                    "would make the window MixedPickupDelivery again"
                    % (i, kind, t_delta, e_delta))
            if t_delta != 0:
                moved += 1
                if (t_delta > 0) != (want_signs[i] > 0.0):
                    problems.append(
                        "window %d's %s moves the wrong way for a %s (transport "
                        "%+d)" % (i, kind, want_labels[i], t_delta))
        if moved == 0:
            problems.append("window %d moved no stored inventory at all" % i)
    return problems


def verify_wrong_origin_proofs(lines: List[str],
                               scn: Tuple[int, int]) -> List[str]:
    """THE TWO WRONG PROOFS, AS THE CLAIM THEY MAKE ABOUT IDENTITY.

    This is the fixture's product, so it is asserted three ways rather than as a
    node count:

      (a) EXACTLY TWO `ROUTE_ORIGIN_PROOF` nodes exist, one per dock hop, on the
          two dock-member recordings named in `ROUTE_ORIGIN_PROOFS`. Zero would
          mean the save came from a pre-#1618 build (the `rover-relay-recorded`
          shape) and there is nothing to override; three would mean a hop was
          recorded that these bytes do not contain.
      (b) EVERY SCALAR the node persists matches, including the two that are
          WRONG. Pinned as a whole node rather than as the two interesting fields
          so a re-harvest that changed the bind state, the pickup kind or the
          recorded coordinates reds here.
      (c) THE PROOFS ARE STILL WRONG, computed rather than asserted: hop 1's bound
          origin pid must NOT be the pickup source B (the correct answer), and hop
          2's bound origin pid must be the DESTINATION of a delivery window (a
          window with no pickup has no correct origin at all). A save whose proofs
          became correct is a save recorded by a fixed binder, and it retires the
          only subject the analysis-side override has - so it must red LOUDLY
          rather than pass as an improvement."""
    problems: List[str] = []

    proof_lines = [i for i, line in enumerate(lines, 1)
                   if line.strip() == "ROUTE_ORIGIN_PROOF"]
    if len(proof_lines) != EXPECTED_ORIGIN_PROOF_COUNT:
        problems.append(
            "save carries %d ROUTE_ORIGIN_PROOF node(s) at line(s) %s, expected "
            "exactly %d - the two WRONG proofs are this fixture's whole product"
            % (len(proof_lines), proof_lines, EXPECTED_ORIGIN_PROOF_COUNT))

    found: Dict[str, Tuple[int, int]] = {}
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        for rec in child_nodes(lines, tree, "RECORDING"):
            rec_id = get_value(lines, rec, "recordingId")
            for proof in child_nodes(lines, rec, "ROUTE_ORIGIN_PROOF"):
                if rec_id in found:
                    problems.append("recording %s carries more than one "
                                    "ROUTE_ORIGIN_PROOF" % rec_id)
                found[rec_id] = proof

    for want in ROUTE_ORIGIN_PROOFS:
        rec_id = want["recordingId"]
        proof = found.get(rec_id)
        if proof is None:
            problems.append(
                "recording %s carries no ROUTE_ORIGIN_PROOF - the two wrong "
                "proofs are this fixture's whole product" % rec_id)
            continue
        for key, value in sorted(want["pins"].items()):
            got = get_value(lines, proof, key)
            if got != value:
                problems.append("proof on %s: %s is %r, expected %r"
                                % (rec_id, key, got, value))

        bound = get_value(lines, proof, "startDockedOriginVesselPid")
        correct = want["correctOriginPid"]
        if correct is not None and bound == correct:
            problems.append(
                "proof on %s now binds the CORRECT origin pid %s (%s) - these "
                "bytes were harvested precisely because the undock binder bound "
                "the wrong half, and a corrected proof retires the only committed "
                "subject the pickup-window override has"
                % (rec_id, correct, want["correctOriginName"]))
        if correct is None and bound not in (ENDPOINT_A_LIVE_PID,
                                             ENDPOINT_A_RECORDED_PID):
            problems.append(
                "the delivery hop's proof on %s binds %r; it was harvested "
                "binding the DESTINATION rover A (%s), and that specific wrongness "
                "is what the override has to ignore"
                % (rec_id, bound, ENDPOINT_A_LIVE_PID))
    return problems


def _verify_active_vessel(lines: List[str]) -> List[str]:
    problems: List[str] = []
    fs = flightstate_node(lines)
    if fs is None:
        return ["save has no FLIGHTSTATE node"]

    records = vessel_records(lines)
    if len(records) != EXPECTED_VESSEL_COUNT:
        problems.append("FLIGHTSTATE carries %d VESSEL nodes, expected %d"
                        % (len(records), EXPECTED_VESSEL_COUNT))

    real = [r for r in records if r["type"] != "SpaceObject"]
    if len(real) != EXPECTED_REAL_VESSEL_COUNT:
        problems.append("FLIGHTSTATE carries %d non-asteroid vessel(s), expected "
                        "%d" % (len(real), EXPECTED_REAL_VESSEL_COUNT))

    raw = get_value(lines, fs, "activeVessel")
    try:
        index = int(raw)
    except (TypeError, ValueError):
        return problems + ["activeVessel is %r, not an index" % raw]

    if not 0 <= index < len(records):
        problems.append("activeVessel %d does not resolve to a VESSEL node" % index)
        return problems
    active = records[index]
    if (active["name"], active["pid"]) != (ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID):
        problems.append(
            "activeVessel %d is %r (pid %s), expected %r (pid %s) - the re-point "
            "did not happen, or FLIGHTSTATE was reordered"
            % (index, active["name"], active["pid"], ACTIVE_VESSEL_NAME,
               ACTIVE_VESSEL_PID))
    if active["name"] == SOURCE_ACTIVE_VESSEL_NAME:
        problems.append(
            "activeVessel is still the source save's %r, an ASTEROID in solar "
            "orbit - a boot there leaves all three rovers unloaded and every "
            "live-vessel Logistics guard skipping" % SOURCE_ACTIVE_VESSEL_NAME)
    if active["type"] == "SpaceObject":
        problems.append("activeVessel %d is a SpaceObject" % index)
    if active["sit"] != ACTIVE_VESSEL_SITUATION:
        problems.append("the active vessel is %r, expected %r"
                        % (active["sit"], ACTIVE_VESSEL_SITUATION))

    by_pid = {r["pid"]: r for r in records}
    for name, pid, vtype, sit, guid in REQUIRED_VESSELS:
        record = by_pid.get(pid)
        if record is None:
            problems.append("FLIGHTSTATE carries no vessel with persistentId %s "
                            "(%s)" % (pid, name))
            continue
        got = (record["name"], record["type"], record["sit"], record["guid"])
        if got != (name, vtype, sit, guid):
            problems.append("vessel %s is %r, expected %r"
                            % (pid, got, (name, vtype, sit, guid)))

    ut = get_value(lines, fs, "UT")
    try:
        if abs(float(ut) - SAVE_UT) > SAVE_UT_EPS:
            problems.append("the save clock is %r, expected %r" % (ut, SAVE_UT))
    except (TypeError, ValueError):
        problems.append("FLIGHTSTATE UT is %r, not a number" % ut)
    return problems


def verify_start_of_cycle_endpoints(lines: List[str]) -> List[str]:
    """THE REPAIR'S OUTPUT, ASSERTED AGAINST THE WINDOWS IT WAS DERIVED FROM.

    The fixture is STAGED AT START-OF-CYCLE: both physical endpoints hold the
    state their own window recorded at ITS dock, so a driven route has cargo to
    take from B and room to put it in A. Every number below is READ OUT OF THE
    SAVE'S OWN WINDOWS rather than restated, so the repair cannot silently drift
    from the recording it replays:

      (a) B's LiquidFuel == window 0's `DOCK_ENDPOINT_RESOURCES` amount.
      (b) A's LiquidFuel == window 1's.
      (c) B's occupied (slotIndex, partName, quantity) set == window 0's
          `DOCK_ENDPOINT_INVENTORY` set, and A's == window 1's.
      (d) C is UNTOUCHED - still 154.4 / 400 and still holding the three items the
          relay left it with, including the re-hashed station. A repair that
          reached the transport would be changing the party that a dispatch never
          reads.
      (e) THE LANE'S PREMISE, as arithmetic in the POSITIVE direction: B holds AT
          LEAST the window's pickup manifest, and A's free capacity is AT LEAST
          the window's delivery manifest, in both the resource and the slot
          dimension. These are the two all-or-nothing gates
          (`RouteOriginCargoCheck.HasRequired`, step 6, and
          `RouteDestinationCapacityCheck.HasCapacityForAllStops`, step 8) that
          RVR-7 forbids the hold tokens of; if either stops holding, that lane
          reds on a forbid and this cell says why.

    Slot capacity is asserted through the `slotIndex` values rather than through a
    constant, because `InventorySlots` is a part-config property and not a save
    property: every index must fall inside a `ConformalStorageUnit`'s three."""
    problems: List[str] = []
    records = {r["pid"]: r for r in vessel_records(lines)}
    scn = parsek_scenario(lines)
    if scn is None:
        return ["no ParsekScenario node to read the windows from"]
    windows, _rec_pids = _window_records(lines, scn)
    if len(windows) != len(ROUTE_WINDOWS):
        return ["cannot verify the repair: %d window(s), expected %d"
                % (len(windows), len(ROUTE_WINDOWS))]

    for pid, wanted in sorted(VESSEL_RESOURCES.items()):
        record = records.get(pid)
        if record is None:
            problems.append("FLIGHTSTATE carries no vessel with persistentId %s"
                            % pid)
            continue
        for resource, (want_a, want_m) in sorted(wanted.items()):
            amount, maximum = _sum_resource(lines, record["span"], resource)
            if (abs(amount - want_a) > VESSEL_RESOURCE_EPS
                    or abs(maximum - want_m) > VESSEL_RESOURCE_EPS):
                problems.append(
                    "vessel %s (%s) holds %s %r / %r, expected %r / %r"
                    % (pid, record["name"], resource, amount, maximum,
                       want_a, want_m))

    for pid, (want_stored, want_modules) in sorted(VESSEL_INVENTORY.items()):
        record = records.get(pid)
        if record is None:
            continue
        start, end = record["span"]
        stored = sum(1 for i in range(start, end)
                     if lines[i].strip() == "STOREDPART")
        modules = sum(1 for i in range(start, end)
                      if lines[i].strip() == "name = ModuleInventoryPart")
        if (stored, modules) != (want_stored, want_modules):
            problems.append(
                "vessel %s (%s) holds %d stored part(s) across %d inventory "
                "module(s), expected %d across %d"
                % (pid, record["name"], stored, modules, want_stored,
                   want_modules))
        slots = [lines[i].strip()[len("slotIndex = "):].strip()
                 for i in range(start, end)
                 if lines[i].strip().startswith("slotIndex = ")]
        bad = [s for s in slots
               if not s.isdigit() or int(s) >= INVENTORY_CONTAINER_SLOTS]
        if bad:
            problems.append(
                "vessel %s (%s) has slotIndex value(s) %r outside 0..%d - the "
                "two ConformalStorageUnit containers hold %d slots each"
                % (pid, record["name"], bad, INVENTORY_CONTAINER_SLOTS - 1,
                   INVENTORY_CONTAINER_SLOTS))

    # (a) / (b) / (c): each restored endpoint against ITS OWN window.
    for pid, window_index in REPAIR_TARGETS:
        record = records.get(pid)
        if record is None:
            continue
        window = windows[window_index][3]
        tag = "%s (pid %s, window %d)" % (record["name"], pid, window_index)

        want_amount = None
        for holder in child_nodes(lines, window, "DOCK_ENDPOINT_RESOURCES"):
            for row in child_nodes(lines, holder, "RESOURCE"):
                if get_value(lines, row, "name") == "LiquidFuel":
                    want_amount = float(get_value(lines, row, "amount"))
        amount, _maximum = _sum_resource(lines, record["span"], "LiquidFuel")
        if want_amount is None:
            problems.append("%s: the window has no DOCK_ENDPOINT_RESOURCES "
                            "LiquidFuel row" % tag)
        elif abs(amount - want_amount) > VESSEL_RESOURCE_EPS:
            problems.append(
                "%s holds LiquidFuel %r but its window recorded %r at the dock - "
                "the start-of-cycle repair did not take, or the window moved"
                % (tag, amount, want_amount))

        want_items = sorted(
            (int(slot), part, _stored_quantity(block))
            for part, slot, block in _window_dock_endpoint_stored_parts(lines, window))
        got_items = sorted(_live_stored_items(lines, record["span"]))
        if got_items != want_items:
            problems.append(
                "%s holds stored parts %r but its window recorded %r at the "
                "dock" % (tag, got_items, want_items))

    # (c2) THE CONTAINER SHAPE THE REPAIR WRITES, checked structurally rather
    # than by count. THIS CELL EXISTS BECAUSE THE FIRST DRAFT OF THE REPAIR GOT IT
    # WRONG in a way every count still passed: a lifted STOREDPART carries a
    # nested PART whose MODULEs write their own `stagingEnabled = True` at a
    # deeper indent, a prefix-only depth test anchored on one of those, and the
    # `inventory` CSV was spliced INTO the middle of a stored part - leaving the
    # module with no CSV at all and a 125-line stored part where every sibling has
    # 124. So the invariant is stated as KSP writes it: the CSV is the
    # slot-ascending part names of the container's own STOREDPARTs, ABSENT when
    # the container is empty, and every STOREDPART sits at the STOREDPARTS depth.
    for pid, _window_index in REPAIR_TARGETS:
        record = records.get(pid)
        if record is None:
            continue
        for index, module in enumerate(_inventory_modules(lines, record["span"])):
            items = []
            for holder in child_nodes(lines, module, "STOREDPARTS"):
                for stored in child_nodes(lines, holder, "STOREDPART"):
                    items.append((int(get_value(lines, stored, "slotIndex")),
                                  get_value(lines, stored, "partName")))
            items.sort()
            want_csv = ",".join(part for _slot, part in items) if items else None
            got_csv = get_value(lines, module, "inventory")
            if got_csv != want_csv:
                problems.append(
                    "%s container %d carries inventory = %r but holds %r - KSP "
                    "writes the slot-ascending part names, and omits the key "
                    "entirely for an empty container"
                    % (record["name"], index, got_csv, want_csv))
            depth_bad = [i for i in range(module[0], module[1])
                         if lines[i].strip() == "STOREDPART"
                         and lines[i] != STOREDPART_INDENT + "STOREDPART"]
            if depth_bad:
                problems.append(
                    "%s container %d has a STOREDPART at the wrong depth on "
                    "line(s) %s - the snapshot lift re-indents by exactly three "
                    "tabs" % (record["name"], index, depth_bad))

    # (e) THE PREMISE, in the positive direction.
    src = records.get(ENDPOINT_B_LIVE_PID)
    if src is not None:
        amount, _maximum = _sum_resource(lines, src["span"], "LiquidFuel")
        pickup = (float(ROUTE_WINDOW_RESOURCE_ROWS[0]["UNDOCK_TRANSPORT_RESOURCES"][1])
                  - float(ROUTE_WINDOW_RESOURCE_ROWS[0]["DOCK_TRANSPORT_RESOURCES"][1]))
        if amount + VESSEL_RESOURCE_EPS < pickup:
            problems.append(
                "rover B holds %r LiquidFuel against a %r pickup manifest - "
                "RouteOriginCargoCheck.HasRequired is all-or-nothing, so a driven "
                "cycle would block OriginLacksCargo and RVR-7's forbid would fire"
                % (amount, pickup))
    dest = records.get(ENDPOINT_A_LIVE_PID)
    if dest is not None:
        amount, maximum = _sum_resource(lines, dest["span"], "LiquidFuel")
        delivery = (float(ROUTE_WINDOW_RESOURCE_ROWS[1]["UNDOCK_ENDPOINT_RESOURCES"][1])
                    - float(ROUTE_WINDOW_RESOURCE_ROWS[1]["DOCK_ENDPOINT_RESOURCES"][1]))
        if (maximum - amount) + VESSEL_RESOURCE_EPS < delivery:
            problems.append(
                "rover A has %r of LiquidFuel headroom against a %r delivery "
                "manifest - RouteDestinationCapacityCheck is all-or-nothing, so a "
                "driven cycle would block DestinationFull and RVR-7's forbid "
                "would fire" % (maximum - amount, delivery))
        free_slots = (len(_inventory_modules(lines, dest["span"]))
                      * INVENTORY_CONTAINER_SLOTS
                      - len(_live_stored_items(lines, dest["span"])))
        # A delivery line occupies at most one slot per KIND (a stack absorbs the
        # rest), so the manifest's distinct kind count is the slot ceiling.
        kinds = {row[0] for row in ROUTE_WINDOW_INVENTORY_ROWS[1]["UNDOCK_ENDPOINT_INVENTORY"]}
        if free_slots < len(kinds):
            problems.append(
                "rover A has %d free inventory slot(s) against a %d-kind delivery "
                "manifest - the slot half of the destination-capacity gate would "
                "block" % (free_slots, len(kinds)))
    return problems


def _stored_quantity(block: List[str]) -> str:
    for line in block:
        text = line.strip()
        if text.startswith("quantity = "):
            return text[len("quantity = "):].strip()
    return "<none>"


def _live_stored_items(lines: List[str],
                       vessel: Tuple[int, int]) -> List[Tuple[int, str, str]]:
    """(slotIndex, partName, quantity) per STOREDPART on a FLIGHTSTATE vessel."""
    out = []
    for module in _inventory_modules(lines, vessel):
        for holder in child_nodes(lines, module, "STOREDPARTS"):
            for stored in child_nodes(lines, holder, "STOREDPART"):
                out.append((int(get_value(lines, stored, "slotIndex")),
                            get_value(lines, stored, "partName"),
                            get_value(lines, stored, "quantity")))
    return out


def _sum_resource(lines: List[str], vessel_node: Tuple[int, int],
                  resource_name: str) -> Tuple[float, float]:
    """(amount, maxAmount) summed over every RESOURCE node named
    ``resource_name`` anywhere inside ``vessel_node``. A plain line scan over the
    node's span: RESOURCE nodes nest inside PART nodes, so a direct-children walk
    would find none."""
    start, end = vessel_node
    amount = 0.0
    maximum = 0.0
    i = start
    while i < end:
        if lines[i].strip() == "RESOURCE":
            name = None
            a = m = 0.0
            for j in range(i + 1, min(i + 8, end)):
                s = lines[j].strip()
                if s.startswith("name = "):
                    name = s[len("name = "):].strip()
                elif s.startswith("amount = "):
                    a = float(s[len("amount = "):].strip())
                elif s.startswith("maxAmount = "):
                    m = float(s[len("maxAmount = "):].strip())
                elif s == "}":
                    break
            if name == resource_name:
                amount += a
                maximum += m
        i += 1
    return amount, maximum


def _world_position(lat: float, lon: float, alt: float) -> Tuple[float, float, float]:
    r = KERBIN_RADIUS_M + alt
    return (r * math.cos(math.radians(lat)) * math.cos(math.radians(lon)),
            r * math.cos(math.radians(lat)) * math.sin(math.radians(lon)),
            r * math.sin(math.radians(lat)))


def verify_geometry(lines: List[str]) -> List[str]:
    """THE THREE-ROVER LAYOUT, computed from the bytes rather than restated.

    The scale is what makes the fixture a SURFACE RELAY: hundreds of metres apart,
    far outside the ~200 m docking range (so the relay is a genuine drive) and
    well inside physics range of each other. A re-harvest that moved a rover
    changes which live-vessel guards find a subject, so the layout is a pin rather
    than prose.

    IT DOES NOT DECIDE THE WRITER PATH, and the authored version of this docstring
    said it did ("so a driven route over these bytes would take `path=loaded` where
    `rover-route-recorded`'s 5.4 km separation forces `path=unloaded`"). RVR-7's
    first census measured `path=unloaded` on EVERY writer over exactly these bytes:
    a seam `TimeJump` warps with the endpoints PACKED, so the load state at the
    DISPATCH TICK decides, not the separation. A player driving the relay by hand
    would see the loaded path."""
    problems: List[str] = []
    positions = {}
    for record in vessel_records(lines):
        if record["type"] == "SpaceObject":
            continue
        try:
            positions[record["name"]] = _world_position(
                float(record["lat"]), float(record["lon"]), float(record["alt"]))
        except (TypeError, ValueError):
            problems.append("vessel %r has unreadable lat / lon / alt"
                            % record["name"])
    for (a, b), want in sorted(VESSEL_SEPARATIONS.items()):
        if a not in positions or b not in positions:
            problems.append("cannot measure %s - %s: one of them is missing" % (a, b))
            continue
        got = math.dist(positions[a], positions[b])
        if abs(got - want) > VESSEL_SEPARATION_TOLERANCE_M:
            problems.append("%s - %s is %.2f m, expected %.1f m (+/- %.1f)"
                            % (a, b, got, want, VESSEL_SEPARATION_TOLERANCE_M))
    return problems


def verify_tree(fixture_dir: str) -> List[str]:
    """Failure strings for the FILE-TREE half of the fixture."""
    problems: List[str] = []

    recordings = os.path.join(fixture_dir, "Parsek", "Recordings")
    if not os.path.isdir(recordings):
        return ["fixture carries no Parsek/Recordings directory"]

    names = sorted(n for n in os.listdir(recordings)
                   if os.path.isfile(os.path.join(recordings, n)))
    stems = sorted({_stem(n) for n in names})
    if stems != sorted(KEEP_RECORDING_IDS):
        problems.append("sidecar families on disk are %r, expected the %d kept ids"
                        % (stems, len(KEEP_RECORDING_IDS)))

    for rid in KEEP_RECORDING_IDS:
        wanted = [".prec", ".prec.txt", ".pann"]
        if rid not in NO_VESSEL_CRAFT_RECORDING_IDS:
            wanted.append("_vessel.craft")
        if rid not in NO_GHOST_CRAFT_RECORDING_IDS:
            wanted.append("_ghost.craft")
        for suffix in wanted:
            path = os.path.join(recordings, rid + suffix)
            if not os.path.isfile(path) or os.path.getsize(path) == 0:
                problems.append("%s%s missing or empty" % (rid, suffix))

    authoritative = [n for n in names if not n.endswith(".txt")]
    if len(authoritative) != EXPECTED_AUTHORITATIVE_SIDECARS:
        problems.append("Parsek/Recordings carries %d authoritative sidecar(s), "
                        "expected %d" % (len(authoritative),
                                         EXPECTED_AUTHORITATIVE_SIDECARS))

    for dirpath, dirnames, filenames in os.walk(fixture_dir):
        for d in dirnames:
            if d in FORBIDDEN_DIR_NAMES:
                problems.append("fixture carries a forbidden directory %s"
                                % os.path.relpath(os.path.join(dirpath, d),
                                                  fixture_dir))
        for f in filenames:
            rel = os.path.relpath(os.path.join(dirpath, f), fixture_dir)
            if f.endswith(FORBIDDEN_FILE_SUFFIXES):
                problems.append("fixture carries a snapshot mirror %s" % rel)
            if f.startswith(FORBIDDEN_FILE_PREFIXES):
                problems.append("fixture carries a quicksave %s" % rel)

    gamestate = os.path.join(fixture_dir, "Parsek", "GameState")
    if not os.path.isdir(gamestate):
        problems.append("fixture carries no Parsek/GameState directory")
    else:
        got = sorted(os.listdir(gamestate))
        if got != sorted(GAMESTATE_FILES):
            problems.append("Parsek/GameState carries %r, expected %r"
                            % (got, sorted(GAMESTATE_FILES)))

    # `Ships/` is absent by construction: the collected save carried none, and
    # this is a RECORDED subject that launches nothing. Stated so a future
    # re-harvest that picks one up trips the forbidden-directory walk above rather
    # than quietly adding payload with no consumer.
    loadmeta = os.path.join(fixture_dir, "persistent.loadmeta")
    if not os.path.isfile(loadmeta):
        problems.append("fixture carries no persistent.loadmeta")

    addons = os.path.join(fixture_dir, ADDONS_REL)
    if not os.path.isfile(addons):
        problems.append("fixture carries no %s" % ADDONS_REL.replace("\\", "/"))
    else:
        size = os.path.getsize(addons)
        if size != ADDONS_EXPECTED_BYTES:
            problems.append("%s is %d bytes, expected %d"
                            % (ADDONS_REL.replace("\\", "/"), size,
                               ADDONS_EXPECTED_BYTES))
        donor = os.path.join(_SAVES, ADDONS_DONOR_NAME, ADDONS_REL)
        if os.path.isfile(donor) and _read_bytes(donor) != _read_bytes(addons):
            problems.append("%s differs from the %s donor's copy"
                            % (ADDONS_REL.replace("\\", "/"), ADDONS_DONOR_NAME))
    return problems


# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of finishing it")
    parser.add_argument("--target-name", default=TARGET_NAME)
    args = parser.parse_args(argv)

    fixture_dir = os.path.join(_SAVES, args.target_name)
    sfs = os.path.join(fixture_dir, "persistent.sfs")
    if not os.path.isfile(sfs):
        print("FAIL: %s does not exist (run harvest_bdock_station.py first)" % sfs)
        return 1

    if args.check:
        problems = verify_save(read_lines(sfs))
        problems += verify_tree(fixture_dir)
        for p in problems:
            print("FAIL: %s" % p)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    # --- 1: the active-vessel re-point ----------------------------------
    lines, note = repoint_active_vessel(read_lines(sfs))
    print("re-pointed %s" % note)

    # --- 3 (written with 1): the start-of-cycle endpoint repair ---------
    lines, repair_notes = repair_start_of_cycle_endpoints(lines)
    for repair_note in repair_notes:
        print("restored %s" % repair_note)
    write_lines(sfs, lines)

    # --- 2: AddOns ------------------------------------------------------
    donor = os.path.join(_SAVES, ADDONS_DONOR_NAME, ADDONS_REL)
    if not os.path.isfile(donor):
        print("FAIL: AddOns donor %s is missing" % donor)
        return 1
    addons_dst = os.path.join(fixture_dir, ADDONS_REL)
    os.makedirs(os.path.dirname(addons_dst), exist_ok=True)
    shutil.copy2(donor, addons_dst)
    print("restored %s from %s (%d bytes)"
          % (ADDONS_REL.replace("\\", "/"), ADDONS_DONOR_NAME,
             os.path.getsize(addons_dst)))

    problems = verify_save(read_lines(sfs))
    problems += verify_tree(fixture_dir)
    for p in problems:
        print("FAIL: %s" % p)
    if problems:
        return 1
    print("OK: wrote %s" % fixture_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
