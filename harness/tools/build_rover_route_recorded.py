#!/usr/bin/env python3
"""Finish the harvested `rover-route-recorded` fixture: the SUPPLY-ROUTE lane host.

WHY THIS FIXTURE EXISTS, AND WHY IT IS A HARVEST RATHER THAN A FLIGHT. Three
committed lanes need a recorded surface-to-surface supply run that no forge can
produce and no existing fixture carries:

  RVR-1 (`RVR-1-rover-route-proof`) needs a `ROUTE_CONNECTION_WINDOWS` node on
    the TARGET branch. Every route window in the committed corpus before this one
    is INITIATOR-branch (`transferTargetPid == recording.vesselPersistentId`),
    because `bdock-recorded` and `depot-route-recorded` both dock Kerbal X
    descendants that share one BAKED `persistentId`. That is why
    `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` and
    `RouteProof_CrossTreeCommittedPartner_HasEndpointProof` skip on BOTH recorded
    hosts and are named in H39/H40's MEASURED_SKIPPED rosters as a HARVEST
    requirement. This is that harvest: two rovers with DIFFERENT baked pids, so
    the window is target-branch and both cells find a subject.
  RVR-2 (`RVR-2-rover-route-create`) needs a SEALED, route-ELIGIBLE committed
    tree to drive `SealSlot` + `RouteCommand action=create` against. The save
    carries Parsek's own record that it prompted this tree as a candidate
    (`PROMPTED_ROUTE_CANDIDATES { treeId = <B> }`).
  RVR-3 (`RVR-3-route-lifecycle`) needs any loaded FLIGHT game with a live clock.

THE SOURCE. The operator's own hand-flown SANDBOX save `logistics-rover-A`,
collected on 2026-08-30 into
`.claude/worktrees/logs/2026-08-30_1106_rover-route/saves/logistics-rover-a`
(persistent.sfs 364 KB, 33 sidecar files), harvested from a scratch COPY. The
`duna-one-recorded` / `depot-route-recorded` provenance class.

NEVER NAME THE FIXTURE AFTER THE SOURCE SAVE. `run.py::stage_fixture` rmtree's
the same-named save inside the automation instance, so a fixture called
`logistics-rover-a` would delete the operator's hand-played save the first time
any scenario staged it. Hence `rover-route-recorded`, named for the LANE.

THE COLLECTED-LOG SHAPE, and the one restore it forces. `collect-logs.py` moves
the save's `Parsek/` to a SIBLING `parsek/` directory and leaves only
`Parsek/Recordings` behind in the save copy - the same shape
`build_duna_one_recorded.py` documents. So the scratch copy must have
`Parsek/GameState` restored from that sibling BEFORE the harvest runs, or the
fixture ships six missing ledger/baseline sidecars. `verify_tree` asserts the
six by name rather than trusting the operator's copy step.

INPUT. The output of

    python harness/tools/harvest_bdock_station.py \\
        --save-dir <scratch copy, with Parsek/GameState restored> \\
        --target-name rover-route-recorded \\
        --expect-situation PRELAUNCH --keep-parsek

i.e. this tool edits `harness/fixtures/saves/rover-route-recorded` IN PLACE. The
harvest did the generic half (title normalisation, the ONE `rewindSave` hint
clear, `Parsek/Saves` prune, the `.craft.txt` snapshot-mirror prune); everything
below is rover-route-specific.

`--expect-situation PRELAUNCH` IS ARMED AGAINST THE SOURCE, NOT THE RESULT, AND
THAT IS A DELIBERATE DEPARTURE FROM `build_depot_route_recorded.py`. There the
re-point's before and after were both ORBITING, so one token gated both. Here
they differ: the source save's `activeVessel = 10` is `rover fuel 0`, PRELAUNCH
on the Runway, and step 1 below re-points to `B`, LANDED. Passing LANDED at
harvest time would FAIL the gate on a healthy source; passing PRELAUNCH keeps it
a real gate (a source whose focus moved reds there), and the LANDED assertion on
the vessel that actually ends up focused lives in `_verify_active_vessel` below.
Do not "fix" this by widening the harvest gate to `PRELAUNCH,LANDED` - that would
accept either, which is exactly what neither half is allowed to do.

WHAT IT DOES, in order:

  1. THE ACTIVE VESSEL. Re-points `FLIGHTSTATE/activeVessel` from `rover fuel 0`
     at index 10 to `B` at index 7 - the TRANSPORT rover, the route's origin
     vessel and the tree RVR-2 seals and promotes. Two reasons, both load-bearing
     and neither cosmetic:
       * A PRELAUNCH-focused boot is the FRESH-ROLLOUT shape Parsek's recorder
         has a fast path for (`RecordingStore.SceneEntryFreshRolloutVesselPid`).
         A lane whose whole subject is committed-tree state should not open on
         the one vessel posture that invites the recorder to do something.
       * `B` is where an operator would be sitting to create this route.
     The index is RE-RESOLVED by name + persistentId; the constant is then
     asserted against it, so a re-harvest that reordered FLIGHTSTATE reds naming
     the new index instead of silently focusing whatever now sits at 7.
     NOTE the two RVR-1 cells do NOT read `FlightGlobals.ActiveVessel` at all -
     they walk `RecordingStore.CommittedTrees` - so this choice is free for
     THEM and is made for the reasons above.
  2. THE ADDONS SCAFFOLDING. The collected save has no `AddOns/` at all; the
     618-byte `DistantObject/Settings.cfg` every sibling fixture carries is
     copied from a RECORDED sibling, and `verify` re-checks its size AND the
     donor's bytes.
  3. THE ENDPOINT INVENTORY REPAIR (added 2026-09-01, after RVR-2 flight 1).
     Strips the TWO `STOREDPART` nodes that a ROUTE DELIVERY - not the recorded
     mission - placed into the endpoint vessel `rover fuel 0` (pid 2123618197),
     restoring the endpoint's slot occupancy to what it was before the operator
     hand-drove a Send Once over this save. Section "THE ENDPOINT INVENTORY
     REPAIR" below sets out the whole argument and the evidence.

WHAT IT DELIBERATELY DOES NOT DO:

  * NO TREE IS DROPPED, and the CROSS-TREE link is the reason. `73e50f1e` ("B")
    carries the route window; `6a2d7247` ("A") is what makes
    `RouteProof_CrossTreeCommittedPartner_HasEndpointProof` findable, because its
    root recording `3582d724` carries `vesselPersistentId = 2123618197`, the very
    pid the window names as its transfer target. Drop tree A and that cell goes
    straight back to Skip - the fixture would still LOOK right and would quietly
    stop paying half its debt. `verify_route_windows` asserts the link.
  * NO SIDECAR IS SWEPT. All five recordings have their family on disk and vice
    versa; there are no orphans (the harvest's own orphan sweep found none).
  * NO `.prec` IS REPAIRED, and that is a MEASUREMENT rather than an omission.
    The analyzer Forbid gate was run on the harvested bytes BEFORE anything else
    and read `FAIL=0 WARN=0 INFO=0 STALE=0 BASELINED=0 RED=0` - clean on the
    first pass, unlike `depot-route-recorded`, which needed a two-section INV2
    containment dedupe. There is nothing here to repair.
  * NOTHING IS SEALED. Every one of the five RECORDING nodes is ALREADY
    `MergeState.Immutable`, which the codec spells by OMITTING the `mergeState`
    key (`RecordingTreeRecordCodec.SaveRewindToStagingMergeState` writes it only
    for a non-default value; the loader defaults a missing key to Immutable). So
    `RouteCandidateFinder.IsTreeFullySealed` is already true for BOTH trees, and
    RVR-2's `SealSlot tree=<B>` is expected to answer
    `alreadySealed=True remaining=0` - the idempotent no-op guard, which is a
    REAL assertion here only because the fixture is pinned to carry no
    `mergeState` key at all. `verify_seal_state` is that pin; without it the
    no-op guard would be untestable and a future re-harvest carrying an open
    provisional would turn RVR-2's `alreadySealed=True` into a silent lie.

THE ENDPOINT INVENTORY REPAIR (step 3), AND WHY A FIXTURE IS EDITED AT ALL.

WHAT WENT WRONG. RVR-2 flight 1 (`2026-09-01_2011_RVR-2-rover-route-create`) drove
the whole chain correctly - seal no-op, create, send-once, TimeJump, `DOCK
CROSSING confirmed` - and then cycle 0 answered `BLOCKED kind=DestinationFull
reason=stored-part:evaScienceKit` instead of delivering. The endpoint had no free
inventory slot. It had none because the SAVE ALREADY CARRIED A ROUTE DELIVERY:
the operator hand-created route `fd6ee2ff` over these same trees and drove one
Send Once at UT 750.06, BEFORE the game wrote the save that was later collected
and harvested. Its own log lines (collected KSP.log,
`.claude/worktrees/logs/2026-08-30_1106_rover-route/KSP.log`, 10:59:14) are:

    Delivery write: route=fd6ee2ff... dest=rover fuel 0 pid=2123618197
        resource=LiquidFuel requested=97.6 written=97.6 tankBefore=200
        tankAfter=297.6 capacity=400 path=loaded
    Inventory store: route=fd6ee2ff... part=evaChute        slot=part7/mod1/slot1 units=1/1
    Inventory store: route=fd6ee2ff... part=evaScienceKit    slot=part7/mod1/slot2 units=1/1

So the harvested endpoint is not the physical state a route-CREATION lane must
start from: it is that state plus one already-run delivery. A lane whose whole
subject is "create a route and watch it deliver" cannot start from a destination
the same delivery has already filled.

WHICH TWO NODES, AND HOW THEY ARE IDENTIFIED POSITIVELY (never by shape alone):
  * `RouteDeliveryPlanner.InventorySlotAddress.ToString` renders
    `part<PartIndex>/mod<ModuleIndex>/slot<SlotIndex>`, and the interface doc on
    `IDeliveryCapacityProbe.ProbeFirstEmptyInventorySlot` fixes the meaning of
    those indices: "vessel part order, then module order within the part, then
    ascending slot index". So `part7/mod1` is the EIGHTH `PART` of the endpoint
    vessel and the SECOND `MODULE` inside it.
  * In the committed bytes that is `ConformalStorageUnit` persistentId
    4218985329, whose MODULE list is `[ModuleCargoPart, ModuleInventoryPart,
    ModulePartVariants]` - index 1 IS the inventory module. The addressing is
    re-derived from the bytes by `_inventory_modules` below rather than trusted.
  * Its `STOREDPARTS` carried slot 0 `evaChute`, slot 1 `evaChute`, slot 2
    `evaScienceKit` - slots 1 and 2 are exactly the two the log names, by slot
    index AND by part name.
  * SLOT 0 IS PROVEN PRE-EXISTING RATHER THAN ASSUMED: the writer stores into
    the FIRST EMPTY slot in that same deterministic order, and parts 0..6 carry
    no inventory module at all, so `part7/mod1/slot1` can only have been chosen
    over slot 0 if slot 0 was already occupied when the delivery ran.
  * NOTHING ELSE IS TOUCHED. The delivery emitted exactly two `Inventory store`
    lines, both on `part7/mod1`; the second container (`part8/mod1`,
    persistentId 584475495: slot 0 `evaScienceKit` x2, slot 1
    `DeployedCentralStation` x1) is not named by any delivery line and is left
    verbatim. `verify_endpoint_inventory` pins BOTH containers, so a repair that
    reached into the wrong one reds.

THE `inventory = ` MIRROR MUST MOVE WITH THE SLOTS. KSP writes a
comma-joined `inventory = <partName>,...` line on the module that mirrors its
`STOREDPART` `partName`s in slot order, and omits it entirely when the module is
empty (measured across all six inventory modules in this save). Removing two
STOREDPART nodes without rewriting that line would leave a save whose module
claims three items and stores one.

WHAT IS DELIBERATELY *NOT* REVERTED, and it is the one asymmetry in this repair:
the LiquidFuel. The same delivery moved the endpoint from 200 to 297.6, so the
pinned `ENDPOINT_VESSEL_LIQUIDFUEL = 297.6 / 400` is post-delivery too. It is
KEPT, because 102.4 of headroom against a 97.6 manifest is what makes RVR-2's
two-cycle chain (cycle 1 fits, cycle 2 short by 92.8) derivable in TWO driven
cycles; reverting to 200 would need a three-cycle driver to reach the same
refusal and would buy nothing. The repair is therefore scoped to the half that
BLOCKED the lane, and this paragraph exists so the asymmetry is a decision on
the record rather than an oversight a later reader has to re-derive.

THE POST-REPAIR ARITHMETIC, and the slot capacity it rests on. Each
`ConformalStorageUnit` holds THREE slots. That is not a save property (it is the
part's `InventorySlots`), so it is MEASURED off the same collected log, whose
cycle-1 probe reads

    ProbeLoadedFirstEmpty: no empty slot on dest=rover fuel 0
        modulesScanned=2 slotsOccupied=5 slotsConsumed=1

i.e. 5 occupied + 1 consumed by that cycle's evaChute = 6 slots across 2 modules.
So, with 3 occupied after the repair (part7 slot 0; part8 slots 0 and 1):
  cycle 1: 3 free slots >= 2 manifest items, and 97.6 <= 102.4 of LF headroom
           -> DELIVERS. Endpoint goes to 395.2 / 400 with 5 slots occupied.
  cycle 2: 1 free slot < 2 items, AND 97.6 > 4.8 of remaining LF headroom
           -> BLOCKED kind=DestinationFull.
`RouteDestinationCapacityCheck.FirstShortToken` names the first SHORT RESOURCE
line before any inventory line, so cycle 2's `reason=` is expected to read
`LiquidFuel` rather than the `stored-part:evaScienceKit` flight 1 measured. That
ordering is the evaluator's, not the fixture's, so RVR-2 pins `BLOCKED
kind=DestinationFull` and leaves the detail to a regex until a flight settles it.

THE LEDGER IS NOT A SECOND COPY OF THIS STATE. The save's `Parsek/GameState`
ledger carries the operator's `RouteDispatched` row for `fd6ee2ff` (it replays as
`Processed RouteDispatched for route=fd6ee2ff... cycle=1` on every boot), but
`RouteModule` is OBSERVE-ONLY by contract - its own doc comment reads "A future
edit MUST NOT mutate vessel cargo from here" - so nothing re-applies the delivery
at load and the repair is stable. The ledger row is left alone: it is a true
record of something that happened.

THE ROUTE WINDOW PINS ARE BUILDER-SIDE ON PURPOSE, exactly as
`build_depot_route_recorded.py`'s ROUTE pins are: `harness/lib/saveparse.py` has
no `routes` facet and no route-window facet, so the shape of the one thing this
fixture exists for would otherwise be unpinned anywhere.

THERE IS NO `ROUTES` NODE AND NO `ROUTE_ORIGIN_PROOF` NODE, and both absences are
POSITIVE facts asserted below rather than accidents:
  * no `ROUTES`: the operator created the route AFTER this save was written, so
    the fixture is a route-CANDIDATE host, not a route host. That is precisely
    what RVR-2 needs - a save whose route does not exist yet, so `create` has
    something to do. (`depot-route-recorded` is the opposite fixture and the two
    are not interchangeable.)
  * no `ROUTE_ORIGIN_PROOF`: both trees launched from the Runway, and
    `RouteOriginProof`'s producer SKIPS proof for a KSC-site start by design
    (the in-game cell `RouteOriginProof_StartedOnRunway_ProducerSkipsProof` is
    the gate on that branch). A proof node here would mean the producer
    misclassified a KSC origin.

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture and
writes nothing. It is WIRED, not decorative:
`RoverRouteRecordedFixtureDriftTests` in
`harness/lib/test_build_rover_route_recorded.py` runs the same `verify_*`
functions in-process, so a hand-edit of the committed bytes reds in the harness
suite. Like its two templates it CANNOT re-run `build`: the input is a collected
operator save outside the repo that will never be committed.

Usage:
    python harness/tools/build_rover_route_recorded.py            # finish in place
    python harness/tools/build_rover_route_recorded.py --check    # verify only

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text node helpers, for the same reason
# `build_depot_route_recorded.py` imports them: a second implementation is a
# second thing to drift. The FILE I/O is deliberately NOT reused - those helpers
# normalise to CRLF on write, and `harvest_bdock_station.py` writes this save's
# `persistent.sfs` with an explicit LF-only newline. Keeping the harvest's own
# line endings means the committed bytes are still "what the tool chain wrote".
import build_career_pad_craft as base_builder  # noqa: E402

find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value

TARGET_NAME = "rover-route-recorded"

# The AddOns donor. Any of the ~30 fixtures carrying the 618-byte variant would
# do; the OTHER route fixture is named so the donor is a fixture of the same
# class and the same lane family.
ADDONS_DONOR_NAME = "depot-route-recorded"
ADDONS_REL = os.path.join("AddOns", "DistantObject", "Settings.cfg")
ADDONS_EXPECTED_BYTES = 618

# `Parsek/GameState` does NOT come through the harvest here: this source is a
# COLLECTED LOG, where collect-logs.py moves the save's `Parsek/` to a sibling
# `parsek/` and leaves only `Parsek/Recordings` behind. The operator restores the
# six files into the scratch copy before harvesting; this list is what makes a
# missed restore a red rather than a silently thinner fixture.
GAMESTATE_FILES = (
    "baseline_0.pgsb",
    "baseline_433.29999999989587.pgsb",
    "baseline_619.57999999972651.pgsb",
    "events.pgse",
    "ledger.pgld",
    "milestones.pgsm",
)

# --- the two trees, kept WHOLE ------------------------------------------
#
# Spelled out rather than derived from the save so that a re-harvest whose shape
# moved reds loudly here instead of silently shipping a different payload.
ENDPOINT_TREE_ID = "6a2d7247996a43159a7b9f7595de708d"     # "A", the DESTINATION rover
TRANSPORT_TREE_ID = "73e50f1e3aa24c299919016cd9e92269"    # "B", the TRANSPORT rover

# Tree "A": one recording, the destination rover's own launch + drive-out. Its
# `vesselPersistentId` is what makes the cross-tree cell findable.
ENDPOINT_TREE_RECORDING_IDS = (
    "3582d724892245c8939f6a354baff278",   # 0  the destination rover's launch
)
# Tree "B": launch -> dock-merged child (THE ROUTE WINDOW) -> post-undock tail,
# plus the separated endpoint child at treeOrder 3.
TRANSPORT_TREE_RECORDING_IDS = (
    "cf8d06fc7bf74e1a82bc70fc79290847",   # 0  the transport rover's launch
    "f2fb77ea5af34870bc08f5a0e9f0d78f",   # 1  THE DOCK MEMBER (route window)
    "4370a799d00644f68d9b4a2ca9f72d0c",   # 2  the transport after the undock
    "0996f1ba7c7b4d3a8d95cf8be77fbe6d",   # 3  the endpoint rover after the undock
)
KEEP_RECORDING_IDS = ENDPOINT_TREE_RECORDING_IDS + TRANSPORT_TREE_RECORDING_IDS

# --- the ROUTE WINDOW (the fixture's whole reason to exist) --------------
#
# `saveparse.py` has no route-window facet, so without this the one surface the
# fixture exists for would be unpinned everywhere. Every value below was read off
# the harvested bytes.
DOCK_MEMBER_RECORDING_ID = "f2fb77ea5af34870bc08f5a0e9f0d78f"
DOCK_MEMBER_VESSEL_PID = "313889796"          # the TRANSPORT rover "B"
ROUTE_WINDOW_PINS = {
    "windowId": "dock-513.539999999823-target-2123618197",
    "dockUT": "513.539999999823",
    "undockUT": "594.27999999974952",
    "transferTargetPid": "2123618197",
    "transferKind": "DockingPort",
    # 1 == Vessel.Situations.LANDED as `RouteConnectionWindow` serialises it. The
    # suite's first SURFACE route endpoint; both other route fixtures are orbital.
    "transferEndpointSituation": "1",
}
ROUTE_WINDOW_TARGET_PID = ROUTE_WINDOW_PINS["transferTargetPid"]
ENDPOINT_AT_DOCK_PINS = {
    "vesselPersistentId": ROUTE_WINDOW_TARGET_PID,
    "bodyName": "Kerbin",
    "latitude": "0.0055209707591019428",
    "longitude": "-74.726196706906393",
    "altitude": "65.978650289936922",
    "isSurface": "True",
}
# The measured transfer, dock -> undock, on both sides of the merge. THE NUMBER
# THAT MATTERS TO RVR-2 IS 97.6: the endpoint gained it and the transport lost
# it, so the delivery manifest a created route carries is 97.6 LiquidFuel plus
# three inventory items. Against the endpoint vessel's committed 297.6 / 400
# (102.4 free) that makes cycle 1 fit and cycle 2 short by 92.8 - which is the
# whole causal chain RVR-2 drives, derived from these bytes rather than guessed.
ROUTE_WINDOW_RESOURCE_ROWS = {
    "DOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "200", "400"),
    "UNDOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "102.39999999999861", "400"),
    "DOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "200", "400"),
    "UNDOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "297.59999999999843", "400"),
}
# (partName, quantity) per ITEM, in file order, for the four inventory nodes.
ROUTE_WINDOW_INVENTORY_ROWS = {
    "DOCK_TRANSPORT_INVENTORY": (("DeployedCentralStation", "1"),
                                 ("evaChute", "1"),
                                 ("evaScienceKit", "2")),
    "UNDOCK_TRANSPORT_INVENTORY": (("evaScienceKit", "1"),),
    "DOCK_ENDPOINT_INVENTORY": (("DeployedCentralStation", "1"),
                                ("evaChute", "1"),
                                ("evaScienceKit", "2")),
    "UNDOCK_ENDPOINT_INVENTORY": (("DeployedCentralStation", "1"),
                                  ("DeployedCentralStation", "1"),
                                  ("evaChute", "2"),
                                  ("evaScienceKit", "3")),
}

# Parsek's OWN record that it offered this tree as a route candidate. It is the
# closest thing the bytes hold to a statement that `RouteAnalysisEngine` found
# the tree ELIGIBLE in the live session, which is the precondition
# `RouteCommand action=create` re-derives at run time. Not a proof (the engine
# re-runs), but a fixture that lost it would be a fixture whose RVR-2 create is
# expected to reject.
PROMPTED_CANDIDATE_TREE_ID = TRANSPORT_TREE_ID

# --- the active vessel (step 1) -----------------------------------------
ACTIVE_VESSEL_INDEX = 7
ACTIVE_VESSEL_NAME = "B"
ACTIVE_VESSEL_PID = DOCK_MEMBER_VESSEL_PID
ACTIVE_VESSEL_SITUATION = "LANDED"
# The vessel the source save left focused; asserted as NOT the one at the
# re-pointed index, so a re-harvest that reordered FLIGHTSTATE cannot silently
# re-point back at it.
SOURCE_ACTIVE_VESSEL_NAME = "rover fuel 0"

# THE THREE REAL VESSELS, and the pid arithmetic that decides RVR-2's outcome.
#
# `rover fuel 0` carries persistentId 2123618197 - THE SAME BAKED PID as the
# recorded destination rover `A` in tree `6a2d7247`, because KSP bakes
# `persistentId` into the .craft and reuses it on every launch (see
# .claude/CLAUDE.md -> "persistentId is craft-baked, NOT launch-unique"). Their
# `pid` GUIDs differ conclusively (0c322ddb... recorded vs 836ca8fa... live), so
# this is a genuine collision and not one launch.
#
# IT IS ALSO WHAT MAKES THE FIXTURE WORK, so do not "fix" it. The route window's
# `transferTargetPid` is 2123618197, and `RouteEndpointResolver.TryResolveEndpoint`
# resolves by `FlightGlobals.FindVessel(pid)` with NO guid gate and NO loaded
# gate - so a driven route's STOP resolves to `rover fuel 0`, 5.4 km south of the
# focus and therefore UNLOADED, and `RouteOrchestrator.ApplyDelivery` takes
# `path=unloaded` (`LiveDeliveryWriters.WriteResourceUnloaded` /
# `WriteInventoryUnloaded`, which write `ProtoPartResourceSnapshot.amount` and a
# `STOREDPART` ConfigNode respectively). That is a DELIVERING path, not a
# refusing one. The live `A` at 2875537755 is the Parsek-spawned destination the
# operator actually docked with; it is NOT the endpoint the route names.
REQUIRED_VESSELS = (
    # (name, persistentId, type, situation, guid)
    (ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID, "Rover", "LANDED",
     "3edd6bc7967c4e2ca0feb9138d116b6d"),
    ("A", "2875537755", "Rover", "LANDED", "dec134a694674799b9faedd3af2ff2ab"),
    (SOURCE_ACTIVE_VESSEL_NAME, ROUTE_WINDOW_TARGET_PID, "Probe", "PRELAUNCH",
     "836ca8fa1e5f4571abf9291afd1b43f9"),
)
# 11 FLIGHTSTATE VESSEL nodes: the three above plus eight stock asteroids the
# source save's DiscoverableObjects scenario had already spawned. The asteroids
# are kept verbatim - pruning them would move every index the re-point resolves
# against for no benefit, and no lane reads them.
EXPECTED_VESSEL_COUNT = 11
EXPECTED_REAL_VESSEL_COUNT = 3

# The endpoint vessel's committed LiquidFuel, which is what makes RVR-2's
# two-cycle chain derivable from the fixture instead of guessed. 400 - 297.6 =
# 102.4 free >= the 97.6 manifest (cycle 1 fits), leaving 4.8 free < 97.6
# (cycle 2 is short by 92.8 and blocks DestinationFull).
#
# THIS NUMBER IS ITSELF POST-DELIVERY (the operator's hand-driven Send Once moved
# it 200 -> 297.6) AND IS KEPT ON PURPOSE - see "THE ENDPOINT INVENTORY REPAIR"
# in the module docstring for why the repair reverts the slots and not the tank.
ENDPOINT_VESSEL_LIQUIDFUEL = (297.5999999999984, 400.0)
ENDPOINT_VESSEL_LIQUIDFUEL_EPS = 1e-6

# --- the endpoint inventory repair (step 3) ------------------------------
#
# The two containers on `rover fuel 0`, addressed the way
# `RouteDeliveryPlanner.InventorySlotAddress` addresses them: (part index within
# the vessel, module index within the part). Both are `ConformalStorageUnit`.
ENDPOINT_INVENTORY_MODULES = ((7, 1, "4218985329"), (8, 1, "584475495"))
# `part7/mod1` is the container the delivery wrote into, named verbatim by the
# two `Inventory store:` lines quoted in the module docstring.
ROUTE_DELIVERED_MODULE = ENDPOINT_INVENTORY_MODULES[0]
# (slotIndex, partName) of the two STOREDPART nodes the delivery placed, as the
# log names them: `slot=part7/mod1/slot1` evaChute and `slot=part7/mod1/slot2`
# evaScienceKit. Stripped by step 3; asserted ABSENT afterwards.
ROUTE_DELIVERED_SLOTS = (("1", "evaChute"), ("2", "evaScienceKit"))
# What each container must hold AFTER the repair: (slotIndex, partName,
# quantity), in file order. `part7/mod1` keeps only the slot-0 evaChute the
# delivery's own first-empty-slot choice proves was already there; `part8/mod1`
# is untouched, and is pinned so a repair that reached into the wrong container
# reds here rather than on a flight.
ENDPOINT_INVENTORY_AFTER = {
    (7, 1): (("0", "evaChute", "1"),),
    (8, 1): (("0", "evaScienceKit", "2"), ("1", "DeployedCentralStation", "1")),
}
# The pre-repair shape of the polluted container, so step 3 refuses to run on
# bytes it does not recognise instead of deleting whatever sits at slots 1 / 2.
ENDPOINT_INVENTORY_BEFORE_REPAIR = (("0", "evaChute", "1"),
                                    ("1", "evaChute", "1"),
                                    ("2", "evaScienceKit", "1"))
# `InventorySlots` per `ConformalStorageUnit`. NOT a save property - MEASURED off
# the collected flight log's `ProbeLoadedFirstEmpty: ... modulesScanned=2
# slotsOccupied=5 slotsConsumed=1` (5 + 1 = 6 across the two modules). Used only
# to state the post-repair free-slot arithmetic RVR-2's cycle 1 rests on.
ENDPOINT_INVENTORY_SLOTS_PER_MODULE = 3
# How many inventory items ONE cycle must find slots for. MEASURED, not derived
# from the window's four ITEM lists: both the operator's create and RVR-2's
# driven create print `stops=1 stop-resources=1 stop-inventory=2` on their `Built
# route` line, and the operator's delivering cycle emitted exactly two
# `Inventory store:` lines (evaChute, evaScienceKit).
ROUTE_MANIFEST_INVENTORY_ITEMS = 2

# Harvest exhaust and derived data no fixture may carry. `Saves` and `analysis`
# are the two worth naming: the source carries `Parsek/Saves` (three
# `parsek_rw_*.sfs` plus a `parsek_career_start.sfs`, all pruned by the harvest,
# with the one `rewindSave` hint that referenced them cleared), and running
# `analyze-recordings.ps1` against the fixture WRITES an `analysis/` directory
# into it that must not be committed.
FORBIDDEN_DIR_NAMES = ("_quarantine", "Saves", "Backup", "RewindPoints", "Ships",
                       "analysis")
FORBIDDEN_FILE_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")
FORBIDDEN_FILE_PREFIXES = ("quicksave",)

# One recording legitimately carries no vessel snapshot of its own, so the
# per-family completeness check below is stated as a floor plus this exemption
# rather than as "every family has all five".
NO_VESSEL_CRAFT_RECORDING_IDS = ("cf8d06fc7bf74e1a82bc70fc79290847",)
NO_GHOST_CRAFT_RECORDING_IDS = ()
# 5 x .prec + 5 x .pann + 4 x _vessel.craft + 5 x _ghost.craft.
EXPECTED_AUTHORITATIVE_SIDECARS = 19


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
    """(name, pid, guid, type, sit) per FLIGHTSTATE VESSEL, in `activeVessel` order.

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


def _repeated_child_nodes(lines: List[str], node: Tuple[int, int],
                          name: str) -> List[Tuple[int, int]]:
    """`child_nodes` is already repeat-safe; this is a readability alias used at
    the call sites that walk repeated ITEM / RESOURCE rows."""
    return child_nodes(lines, node, name)


# ---------------------------------------------------------------------------
# Step 1: the active-vessel re-point.
# ---------------------------------------------------------------------------


def repoint_active_vessel(lines: List[str]) -> Tuple[List[str], str]:
    """Point `FLIGHTSTATE/activeVessel` at the transport rover `B`.

    Returns (lines, note). The index is RE-RESOLVED from the VESSEL list by name
    + persistentId rather than taken from the constant, and the constant is then
    asserted against it - so a re-harvest that reordered FLIGHTSTATE reds naming
    the new index instead of silently focusing whatever now sits at 7."""
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
# Step 3: the endpoint inventory repair.
# ---------------------------------------------------------------------------


def endpoint_vessel_node(lines: List[str]) -> Optional[Tuple[int, int]]:
    """The FLIGHTSTATE VESSEL node of the route endpoint `rover fuel 0`.

    Resolved by persistentId rather than by index or name: the pid is what
    `RouteEndpointResolver.TryResolveEndpoint` itself resolves by, and it is the
    value the window's `transferTargetPid` names."""
    for record in vessel_records(lines):
        if record["pid"] == ROUTE_WINDOW_TARGET_PID:
            return record["span"]
    return None


def _inventory_modules(lines: List[str],
                       vessel: Tuple[int, int]) -> List[Tuple[int, int, Tuple[int, int], str]]:
    """(partIndex, moduleIndex, moduleSpan, partPersistentId) per inventory module.

    The indices are re-derived from the bytes in exactly the order
    `IDeliveryCapacityProbe.ProbeFirstEmptyInventorySlot` documents ("vessel part
    order, then module order within the part"), so a `part7/mod1` token out of a
    KSP.log addresses the same node here that it addressed live."""
    out: List[Tuple[int, int, Tuple[int, int], str]] = []
    for part_index, part in enumerate(child_nodes(lines, vessel, "PART")):
        for module_index, module in enumerate(child_nodes(lines, part, "MODULE")):
            if get_value(lines, module, "name") == "ModuleInventoryPart":
                out.append((part_index, module_index, module,
                            get_value(lines, part, "persistentId") or ""))
    return out


def _stored_part_rows(lines: List[str],
                      module: Tuple[int, int]) -> List[Tuple[str, str, str, Tuple[int, int]]]:
    """(slotIndex, partName, quantity, span) per STOREDPART, in file order."""
    holders = child_nodes(lines, module, "STOREDPARTS")
    if not holders:
        return []
    rows = []
    for node in child_nodes(lines, holders[0], "STOREDPART"):
        rows.append((get_value(lines, node, "slotIndex") or "",
                     get_value(lines, node, "partName") or "",
                     get_value(lines, node, "quantity") or "",
                     node))
    return rows


def strip_route_delivered_storedparts(lines: List[str]) -> Tuple[List[str], str]:
    """Remove the two STOREDPART nodes the operator's hand-driven route delivered.

    IDEMPOTENT and FAIL-LOUD, in that order: bytes already carrying the repaired
    shape are a no-op, bytes carrying the known polluted shape are repaired, and
    ANYTHING ELSE aborts rather than deleting whatever happens to sit at slots 1
    and 2. A re-harvest whose endpoint moved must re-derive this recipe from its
    own flight log, not inherit two slot numbers from this one."""
    out = list(lines)
    vessel = endpoint_vessel_node(out)
    if vessel is None:
        raise SystemExit("no FLIGHTSTATE VESSEL with persistentId %s (the route "
                         "endpoint) to repair" % ROUTE_WINDOW_TARGET_PID)

    modules = _inventory_modules(out, vessel)
    addresses = [(p, m) for p, m, _span, _pid in modules]
    want_addresses = [(p, m) for p, m, _pid in ENDPOINT_INVENTORY_MODULES]
    if addresses != want_addresses:
        raise SystemExit(
            "the endpoint vessel carries inventory modules at %r, expected %r - "
            "the `partN/modM` addressing the delivery log uses no longer resolves"
            % (addresses, want_addresses))
    for (part_index, module_index, _span, pid), (_p, _m, want_pid) in zip(
            modules, ENDPOINT_INVENTORY_MODULES):
        if pid != want_pid:
            raise SystemExit(
                "part%d/mod%d belongs to part persistentId %s, expected %s"
                % (part_index, module_index, pid, want_pid))

    target_part, target_module, _pid = ROUTE_DELIVERED_MODULE
    module = [span for p, m, span, _ in modules
              if (p, m) == (target_part, target_module)][0]
    rows = _stored_part_rows(out, module)
    shape = tuple((slot, name, qty) for slot, name, qty, _span in rows)

    if shape == ENDPOINT_INVENTORY_AFTER[(target_part, target_module)]:
        return out, ("part%d/mod%d already repaired (%d stored part(s))"
                     % (target_part, target_module, len(rows)))
    if shape != ENDPOINT_INVENTORY_BEFORE_REPAIR:
        raise SystemExit(
            "part%d/mod%d holds %r, which is neither the polluted shape %r nor "
            "the repaired one %r - re-derive the repair from this save's own "
            "`Inventory store:` log lines"
            % (target_part, target_module, shape, ENDPOINT_INVENTORY_BEFORE_REPAIR,
               ENDPOINT_INVENTORY_AFTER[(target_part, target_module)]))

    # Delete bottom-up so the earlier spans stay valid.
    removed = []
    for slot, name in reversed(ROUTE_DELIVERED_SLOTS):
        match = [span for s, n, _q, span in rows if s == slot and n == name]
        if len(match) != 1:
            raise SystemExit("expected exactly one STOREDPART at slot %s named %s, "
                             "found %d" % (slot, name, len(match)))
        start, end = match[0]
        del out[start:end]
        removed.append("slot%s/%s" % (slot, name))

    # The `inventory = ` mirror must move with the slots (KSP writes it as the
    # module's partNames in slot order and omits it when the module is empty).
    vessel = endpoint_vessel_node(out)
    module = [span for p, m, span, _ in _inventory_modules(out, vessel)
              if (p, m) == (target_part, target_module)][0]
    survivors = [name for _slot, name, _qty, _span in _stored_part_rows(out, module)]
    if not set_value(out, module, "inventory", ",".join(survivors)):
        raise SystemExit("part%d/mod%d has no `inventory` line to rewrite"
                         % (target_part, target_module))

    return out, ("part%d/mod%d: dropped %s; inventory -> %s"
                 % (target_part, target_module, " + ".join(reversed(removed)),
                    ",".join(survivors)))


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
    if ids != [ENDPOINT_TREE_ID, TRANSPORT_TREE_ID]:
        problems.append("RECORDING_TREE ids are %r, expected the two kept trees %r"
                        % (ids, [ENDPOINT_TREE_ID, TRANSPORT_TREE_ID]))
    else:
        for tree, want in ((trees[0], ENDPOINT_TREE_RECORDING_IDS),
                           (trees[1], TRANSPORT_TREE_RECORDING_IDS)):
            recs = child_nodes(lines, tree, "RECORDING")
            got = [get_value(lines, r, "recordingId") for r in recs]
            if got != list(want):
                problems.append(
                    "tree %s carries recordings %r, expected %r"
                    % (get_value(lines, tree, "id"), got, list(want)))
            orders = [get_value(lines, r, "treeOrder") for r in recs]
            if orders != [str(i) for i in range(len(orders))]:
                problems.append("tree %s treeOrders are %r, expected 0..%d"
                                % (get_value(lines, tree, "id"), orders,
                                   len(orders) - 1))

    # THE NO-ROUTE ASSERTION. This fixture is a route CANDIDATE host: the
    # operator created the route AFTER the save was written, which is exactly
    # what gives RVR-2's `RouteCommand action=create` something to do.
    if child_nodes(lines, scn, "ROUTES"):
        problems.append(
            "ParsekScenario carries a ROUTES node - this fixture is the route "
            "CANDIDATE host and RVR-2's create would answer "
            "candidate-already-promoted")

    for name in ("RECORDING_SUPERSEDES", "LEDGER_TOMBSTONES", "REWIND_POINTS",
                 "REWIND_RETIREMENTS"):
        if child_nodes(lines, scn, name):
            problems.append("ParsekScenario carries a %s node" % name)

    prompted = child_nodes(lines, scn, "PROMPTED_ROUTE_CANDIDATES")
    if len(prompted) != 1:
        problems.append("expected exactly 1 PROMPTED_ROUTE_CANDIDATES node, "
                        "found %d" % len(prompted))
    else:
        got = get_value(lines, prompted[0], "treeId")
        if got != PROMPTED_CANDIDATE_TREE_ID:
            problems.append(
                "PROMPTED_ROUTE_CANDIDATES names tree %r, expected %r - Parsek's "
                "own record that it found this tree route-ELIGIBLE is the "
                "closest the bytes come to RVR-2's create precondition"
                % (got, PROMPTED_CANDIDATE_TREE_ID))

    # Every schema generation in the save must be the current one; a stray older
    # value would be a recording RecordingStore rejects at load.
    gens = {line.strip().split("=", 1)[1].strip() for line in lines
            if line.strip().startswith("recordingSchemaGeneration = ")}
    if gens != {"4"}:
        problems.append("recordingSchemaGeneration values are %r, expected {'4'}"
                        % (sorted(gens),))

    # The harvester clears these; INV9's dangling-hint WARN depends on it.
    dangling = [i for i, line in enumerate(lines, 1)
                if line.strip().startswith("rewindSave = parsek_rw_")]
    if dangling:
        problems.append("a rewindSave = parsek_rw_* hint survived at line(s) %s"
                        % (dangling,))

    # THE ORIGIN-PROOF ABSENCE, as a positive fact. Both trees launched from the
    # Runway, and the producer skips proof for a KSC-site start by design.
    proofs = [i for i, line in enumerate(lines, 1)
              if line.strip() == "ROUTE_ORIGIN_PROOF"]
    if proofs:
        problems.append(
            "save carries a ROUTE_ORIGIN_PROOF node at line(s) %s - both trees "
            "start at the Runway, so the producer is expected to skip proof "
            "(RouteOriginProof_StartedOnRunway_ProducerSkipsProof is that gate)"
            % (proofs,))

    problems += verify_seal_state(lines, scn)
    problems += _verify_active_vessel(lines)
    problems += verify_route_windows(lines, scn)
    problems += verify_endpoint_inventory(lines)
    return problems


def verify_endpoint_inventory(lines: List[str]) -> List[str]:
    """THE REPAIR PIN (step 3), and RVR-2's cycle-1 DELIVERY rests entirely on it.

    Four claims, in the order a reader needs them:
      (a) the endpoint carries exactly the two inventory containers the
          `partN/modM` addressing resolves against, at the same part pids;
      (b) NEITHER route-delivered STOREDPART survives anywhere on the vessel -
          stated over the whole vessel rather than over one module, so a
          re-harvest that moved the delivery into the other container cannot
          pass vacuously;
      (c) both containers hold exactly the pinned surviving rows, including the
          UNTOUCHED second container, and the `inventory = ` mirror agrees with
          them slot for slot;
      (d) the free-slot arithmetic RVR-2's cycle 1 needs actually holds, so the
          numbers and the conclusion cannot drift apart.
    """
    problems: List[str] = []
    vessel = endpoint_vessel_node(lines)
    if vessel is None:
        return ["no FLIGHTSTATE VESSEL with persistentId %s (the route endpoint)"
                % ROUTE_WINDOW_TARGET_PID]

    modules = _inventory_modules(lines, vessel)
    got = [(p, m, pid) for p, m, _span, pid in modules]
    if got != [tuple(x) for x in ENDPOINT_INVENTORY_MODULES]:
        return problems + [
            "the endpoint's inventory containers are %r, expected %r - the "
            "`partN/modM` addressing the delivery log uses no longer resolves"
            % (got, [tuple(x) for x in ENDPOINT_INVENTORY_MODULES])]

    occupied = 0
    for part_index, module_index, module, _pid in modules:
        rows = _stored_part_rows(lines, module)
        occupied += len(rows)
        shape = tuple((slot, name, qty) for slot, name, qty, _span in rows)
        want = ENDPOINT_INVENTORY_AFTER[(part_index, module_index)]
        if shape != want:
            problems.append("part%d/mod%d holds %r, expected %r"
                            % (part_index, module_index, shape, want))
        mirror = get_value(lines, module, "inventory")
        want_mirror = ",".join(name for _slot, name, _qty in want)
        if mirror != want_mirror:
            problems.append(
                "part%d/mod%d `inventory` reads %r, expected %r - KSP writes that "
                "line as the module's partNames in slot order, so it must move "
                "with the STOREDPART set"
                % (part_index, module_index, mirror, want_mirror))

    # (b) THE ABSENCE, over the WHOLE vessel.
    for part_index, module_index, module, _pid in modules:
        for slot, name, _qty, _span in _stored_part_rows(lines, module):
            for want_slot, want_name in ROUTE_DELIVERED_SLOTS:
                if (part_index, module_index) == ROUTE_DELIVERED_MODULE[:2] \
                        and slot == want_slot and name == want_name:
                    problems.append(
                        "part%d/mod%d/slot%s still holds the route-delivered %s - "
                        "the endpoint is polluted by the operator's hand-driven "
                        "Send Once and RVR-2's cycle 1 will block instead of "
                        "delivering" % (part_index, module_index, slot, name))

    # (d) THE ARITHMETIC.
    capacity = len(modules) * ENDPOINT_INVENTORY_SLOTS_PER_MODULE
    free = capacity - occupied
    if free < ROUTE_MANIFEST_INVENTORY_ITEMS:
        problems.append(
            "the endpoint has %d free inventory slot(s) of %d against a %d-item "
            "manifest - cycle 1 would BLOCK kind=DestinationFull instead of "
            "delivering, which is the exact failure this repair exists to undo"
            % (free, capacity, ROUTE_MANIFEST_INVENTORY_ITEMS))
    if free - ROUTE_MANIFEST_INVENTORY_ITEMS >= ROUTE_MANIFEST_INVENTORY_ITEMS:
        problems.append(
            "the endpoint has %d free inventory slot(s), enough for TWO cycles - "
            "RVR-2's cycle-2 block would then rest on the LiquidFuel half alone"
            % free)
    return problems


def verify_seal_state(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """THE SEAL PIN, and RVR-2's `alreadySealed=True` rests entirely on it.

    `RecordingTreeRecordCodec` OMITS `mergeState` for the default
    `MergeState.Immutable` and the loader defaults a missing key back to it, so
    "every recording is Immutable" is spelled in the bytes as "no RECORDING node
    carries a mergeState key at all". That makes
    `RouteCandidateFinder.IsTreeFullySealed` true for both trees WITHOUT any
    seal being driven, which is the no-op shape RVR-2's SealSlot step asserts.

    Stated as an absence over the WHOLE save rather than per node, because a
    stray key anywhere - including on a tree this fixture does not promote -
    would flip the same predicate."""
    problems: List[str] = []
    stray = [i for i, line in enumerate(lines, 1)
             if line.strip().startswith("mergeState = ")]
    if stray:
        problems.append(
            "a mergeState key survives at line(s) %s: at least one recording is "
            "NOT Immutable, so IsTreeFullySealed is false and RVR-2's "
            "`SealSlot ... alreadySealed=True` pin is a lie" % (stray,))

    # And the same claim from the other side: the count of RECORDING nodes the
    # seal predicate would walk, so a fixture that lost recordings cannot pass
    # the absence check vacuously.
    total = 0
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        total += len(child_nodes(lines, tree, "RECORDING"))
    if total != len(KEEP_RECORDING_IDS):
        problems.append("the two trees carry %d RECORDING node(s), expected %d"
                        % (total, len(KEEP_RECORDING_IDS)))
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
            "activeVessel is still the source save's %r, whose PRELAUNCH posture "
            "is the fresh-rollout shape the re-point exists to avoid"
            % SOURCE_ACTIVE_VESSEL_NAME)
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

    # THE ENDPOINT'S HEADROOM, which is what makes RVR-2's two-cycle chain a
    # derivation rather than a guess.
    endpoint = by_pid.get(ROUTE_WINDOW_TARGET_PID)
    if endpoint is not None:
        amount, maximum = _sum_resource(lines, endpoint["span"], "LiquidFuel")
        want_a, want_m = ENDPOINT_VESSEL_LIQUIDFUEL
        if (abs(amount - want_a) > ENDPOINT_VESSEL_LIQUIDFUEL_EPS
                or abs(maximum - want_m) > ENDPOINT_VESSEL_LIQUIDFUEL_EPS):
            problems.append(
                "the route endpoint vessel (pid %s) holds LiquidFuel %r / %r, "
                "expected %r / %r - RVR-2's cycle-1-fits / cycle-2-blocks chain "
                "is derived from that headroom"
                % (ROUTE_WINDOW_TARGET_PID, amount, maximum, want_a, want_m))
    return problems


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


def verify_route_windows(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """THE ROUTE-WINDOW PIN. `saveparse.py` has no route-window facet, so this is
    the only place the fixture's reason to exist is asserted.

    It states FOUR things, and the last two are the debts RVR-1 pays:
      (a) there is EXACTLY ONE window in the whole save, on the dock member;
      (b) its scalar shape (ids, clocks, kind, LANDED situation) and its
          ENDPOINT_AT_DOCK coordinates;
      (c) the window is TARGET-BRANCH - `transferTargetPid` differs from the
          carrying recording's `vesselPersistentId` - which is the predicate
          `RouteProof_ActiveAsTargetDockWindow_HasEndpointProof` searches for and
          which no other committed fixture satisfies;
      (d) the target pid is ALSO carried by a recording in the OTHER tree, which
          is the extra conjunct
          `RouteProof_CrossTreeCommittedPartner_HasEndpointProof` adds.
    """
    problems: List[str] = []

    windows = []          # (treeId, recordingId, recordingPid, node)
    rec_pids = {}         # recordingId -> vesselPersistentId
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        tree_id = get_value(lines, tree, "id")
        for rec in child_nodes(lines, tree, "RECORDING"):
            rec_id = get_value(lines, rec, "recordingId")
            rec_pid = get_value(lines, rec, "vesselPersistentId")
            rec_pids[rec_id] = rec_pid
            for holder in child_nodes(lines, rec, "ROUTE_CONNECTION_WINDOWS"):
                for w in child_nodes(lines, holder, "WINDOW"):
                    windows.append((tree_id, rec_id, rec_pid, w))

    if len(windows) != 1:
        return problems + [
            "the save carries %d ROUTE_CONNECTION_WINDOWS WINDOW node(s), "
            "expected exactly 1" % len(windows)]

    tree_id, rec_id, rec_pid, window = windows[0]
    if rec_id != DOCK_MEMBER_RECORDING_ID:
        problems.append("the route window rides recording %r, expected %r"
                        % (rec_id, DOCK_MEMBER_RECORDING_ID))
    if tree_id != TRANSPORT_TREE_ID:
        problems.append("the route window rides tree %r, expected %r"
                        % (tree_id, TRANSPORT_TREE_ID))
    if rec_pid != DOCK_MEMBER_VESSEL_PID:
        problems.append("the dock member's vesselPersistentId is %r, expected %r"
                        % (rec_pid, DOCK_MEMBER_VESSEL_PID))

    for key, want in sorted(ROUTE_WINDOW_PINS.items()):
        got = get_value(lines, window, key)
        if got != want:
            problems.append("route WINDOW %s is %r, expected %r" % (key, got, want))

    # (c) THE TARGET-BRANCH CLAIM, stated as the predicate the cell evaluates
    # rather than as two constants that happen to differ.
    target = get_value(lines, window, "transferTargetPid")
    if target == rec_pid:
        problems.append(
            "the window is INITIATOR-branch (transferTargetPid == the carrying "
            "recording's vesselPersistentId == %r): "
            "RouteProof_ActiveAsTargetDockWindow_HasEndpointProof would Skip, "
            "which is the whole debt this fixture exists to pay" % target)

    # (d) THE CROSS-TREE LINK.
    holders = sorted(r for r, p in rec_pids.items() if p == target)
    if not holders:
        problems.append(
            "no committed recording carries vesselPersistentId %r, so "
            "RouteProof_CrossTreeCommittedPartner_HasEndpointProof would Skip - "
            "tree %s must be kept WHOLE" % (target, ENDPOINT_TREE_ID))
    elif holders != [ENDPOINT_TREE_RECORDING_IDS[0]]:
        problems.append("recording(s) %r carry the target pid %r, expected %r"
                        % (holders, target, [ENDPOINT_TREE_RECORDING_IDS[0]]))

    endpoints = child_nodes(lines, window, "ENDPOINT_AT_DOCK")
    if len(endpoints) != 1:
        problems.append("route WINDOW has %d ENDPOINT_AT_DOCK node(s), expected 1"
                        % len(endpoints))
    else:
        for key, want in sorted(ENDPOINT_AT_DOCK_PINS.items()):
            got = get_value(lines, endpoints[0], key)
            if got != want:
                problems.append("ENDPOINT_AT_DOCK %s is %r, expected %r"
                                % (key, got, want))

    for node_name, want in sorted(ROUTE_WINDOW_RESOURCE_ROWS.items()):
        holder = child_nodes(lines, window, node_name)
        if len(holder) != 1:
            problems.append("route WINDOW has %d %s node(s), expected 1"
                            % (len(holder), node_name))
            continue
        rows = [tuple(get_value(lines, r, k) for k in
                      ("name", "amount", "maxAmount"))
                for r in _repeated_child_nodes(lines, holder[0], "RESOURCE")]
        if rows != [want]:
            problems.append("route WINDOW %s rows are %r, expected %r"
                            % (node_name, rows, [want]))

    for node_name, want in sorted(ROUTE_WINDOW_INVENTORY_ROWS.items()):
        holder = child_nodes(lines, window, node_name)
        if len(holder) != 1:
            problems.append("route WINDOW has %d %s node(s), expected 1"
                            % (len(holder), node_name))
            continue
        rows = [(get_value(lines, it, "partName"), get_value(lines, it, "quantity"))
                for it in _repeated_child_nodes(lines, holder[0], "ITEM")]
        if rows != list(want):
            problems.append("route WINDOW %s items are %r, expected %r"
                            % (node_name, rows, list(want)))
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
        problems.append("fixture carries no Parsek/GameState directory - the "
                        "collected-log harvest must restore it from the sibling "
                        "`parsek/GameState` before harvesting")
    else:
        got = sorted(os.listdir(gamestate))
        if got != sorted(GAMESTATE_FILES):
            problems.append("Parsek/GameState carries %r, expected %r"
                            % (got, sorted(GAMESTATE_FILES)))

    # `Ships/` is absent by construction: the collected save carried none, and
    # this is a RECORDED subject that launches nothing (exactly like
    # `duna-one-recorded` and `depot-route-recorded`). Stated so a future
    # re-harvest that picks one up trips the forbidden-directory walk above
    # rather than quietly adding payload with no consumer.
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
    write_lines(sfs, lines)
    print("re-pointed %s" % note)

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

    # --- 3: the endpoint inventory repair -------------------------------
    lines, note = strip_route_delivered_storedparts(read_lines(sfs))
    write_lines(sfs, lines)
    print("repaired endpoint inventory: %s" % note)

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
