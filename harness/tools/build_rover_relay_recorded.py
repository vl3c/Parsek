#!/usr/bin/env python3
"""Finish the harvested `rover-relay-recorded` fixture: the UNTYPED-DEPOT RELAY host.

WHY THIS FIXTURE EXISTS. It is the suite's first committed save that carries a
COMPLETE, BALANCED, TWO-HOP SURFACE RELAY and STILL produces no route, for two
INDEPENDENT reasons that are both the product failing CLOSED by design. Every
other route fixture in the corpus is either a route CANDIDATE host
(`rover-route-recorded`) or a route HOST (`depot-route-recorded`); none of them
can exercise the REFUSING direction of the candidacy pipeline over bytes that
look, to every structural facet, exactly like an eligible supply run. This one
does: 3 committed trees, 9 recordings, 4 branch points, 2 route windows with
BALANCED resource deltas and 2 `ENDPOINT_AT_DOCK` nodes - and 0 routes,
0 prompted candidates, 0 `ROUTE_ORIGIN_PROOF` nodes.

THE TWO FAIL-CLOSED REASONS, both measured on the source flight's own KSP.log
(`.claude/worktrees/logs/2026-09-02_2041/KSP.log`, i.e. the umbrella `logs/`
folder that `collect-logs.py` writes):

  (1) NO ORIGIN PROOF, because NEITHER DOCKED HALF IS A PLAYER-TYPED DEPOT.
      The producer's own line, twice (log lines 20911 and 24463, one per dock):
          RouteOriginProof skipped: no depot half recId=1461186781
              vessel='rover C' seams=2 candidates=0 isEva=False (neither docked
              half is typed Base or Station, so no supply origin was recorded;
              set the depot's type in the tracking station)
      `seams=2 candidates=0` is the whole statement: the producer SAW both dock
      seams and admitted neither, because `Vessel.vesselType` on all three rovers
      is `Rover`. This is the standing todo entry
      ROUTE-ORIGIN-PROOF-REQUIRES-A-PLAYER-TYPED-DEPOT in
      `docs/dev/todo-and-known-bugs.md`, and this fixture is the first committed
      bytes that hold its output.

  (2) THE ANALYSIS REJECTS `MixedPickupDelivery`, AND THIS ONE IS A **PRODUCT
      DEFECT**, not an operator mistake. Measured at log line 28049, over the tree
      in exactly the shape this fixture ships (the same commit's
      `NotifyLedgerTreeCommitted: tree='rover C' recordings=7`):
          DeriveCandidates: trees=1 candidates=0 notSealed=0 ineligible=1
              alreadyPromoted=0 dismissed=0 [missingProof=0 unorderableWindows=0
              missingEndpoint=0 mixedPickup=1 noManifest=0 undockedStart=0
              untrackedGain=0 flowNotClosed=0 startTrimUnsupported=0
              unsupportedKind=0]
      THE PLAYER MOVED THE **SAME** PART, AND IT RE-HASHED IN TRANSIT. While
      docked at hop 1 the player moved one `DeployedCentralStation` and one
      `evaChute` out of `rover B`'s inventory into `rover C`'s. The chute closed
      cleanly (transport gain 1, endpoint loss 1, one identity). The station did
      not: it left B as `5072997a...` and arrived on C as `5bcde9ad...`.
      `RouteAnalysisEngine.HasUnwitnessedInventoryGain` matches by identity hash,
      so the gain had no endpoint loss to pair with, the window failed closed, and
      B's real loss is invisible in the other direction too.
      WHY IT RE-HASHED, from the sibling forensics that diffed the two persisted
      `STOREDPART_SNAPSHOT` nodes: the only difference the hash does NOT strip is
      one ADDED value inside `MODULE { name = ModuleGroundExpControl }`,
      `canComm = False`. Stock's `ModuleGroundExpControl.OnSave` writes that value
      ONLY from a live instance in FLIGHT, and a stock inventory move goes through
      `ModuleInventoryPart.StoreCargoPartAtSlot(Part, int)`, which builds a fresh
      `ProtoPartSnapshot` off the live part and therefore re-runs every module's
      `OnSave`. The editor-authored `STOREDPART` in the `.craft` never carries it.
      `VesselSpawner.ComputeInventoryPayloadIdentityHash` hashes module-level
      values BY DESIGN, so a value stock adds on the way through changes the
      identity of a part nobody swapped.
      FILED as LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-CARGO-MOVE in
      `docs/dev/todo-and-known-bugs.md` (OPEN, needs a design call). The class is
      wider than one part: any cargo part whose module writes a computed value in
      `OnSave` re-hashes on a live move.
      DO NOT "FIX" THE FIXTURE. These bytes are the regression subject, and RVR-5
      is the instrument: after a hash fix its `mixedPickup=1` /
      `candidate-ineligible` pins MUST be re-measured, because the window will
      then either admit or fail a later gate.

  THE TWO ARE ORDER-DEPENDENT IN THE LOG AND INDEPENDENT IN THE BYTES. The two
  EARLIER derive passes (log lines 15543 and 18645, both over single-recording
  trees) read `missingProof=1 mixedPickup=0`; only the final pass over the
  7-recording relay tree reads `missingProof=0 mixedPickup=1`. Do not read that
  as "the proof problem went away" - reason (1) is a producer-side absence that
  the analysis of THIS tree never reaches, because the mixed-pickup gate rejects
  first. Both are real, and a re-fly that fixes only one still produces no route:
  a typed-depot re-fly that moves any re-hashing cargo part is still refused.

WHERE `DeriveCandidates` ACTUALLY FIRES, because a lane header will want it and
getting it wrong costs a required token. It is NOT a load-time pass. Its only two
callers are `RouteRunPrompt` (the post-TREE-COMMIT prompt) and
`LogisticsWindowUI` (only while that window is open). The source flight's log
carries exactly THREE `DeriveCandidates` lines and all three sit inside a
merge-dialog commit (`MergeDialog ... User chose: Tree Merge` on the next line),
against SIX `Scenario OnLoad` lines that produce none. A driven headless run that
only LOADS this fixture will print no `DeriveCandidates` line at all, which is why
`RVR-5` pins the `routecommand create gate` line instead - that one re-derives the
same analysis synchronously inside the seam verb.

THE SOURCE. The operator's own hand-flown SANDBOX save `logistics-rover-B`, flown
2026-09-02: three identical 16-part rovers A, B and C on the KSC shore (Kerbin,
all LANDED, one `ModuleCommand` + one `dockingPort2` each, NO grapple). C drove to
B, docked at UT 218.22, loaded +200 LiquidFuel (B 200 -> 0), undocked at UT
276.00, drove ~780 m to A, docked at UT 340.12, unloaded 126.8 LiquidFuel
(A 200 -> 326.8), undocked at UT 402.50 and drove away. The save was written at
UT 443.64 from the SPACE CENTER with C's recording stopped. Harvested from a
scratch COPY, the `duna-one-recorded` / `depot-route-recorded` /
`rover-route-recorded` provenance class.

NEVER NAME THE FIXTURE AFTER THE SOURCE SAVE. `run.py::stage_fixture` rmtree's
the same-named save inside the automation instance, so a fixture called
`logistics-rover-b` would delete the operator's hand-played save the first time
any scenario staged it. Hence `rover-relay-recorded`, named for the LANE.

INPUT. The output of

    python harness/tools/harvest_bdock_station.py \\
        --save-dir <scratch copy> \\
        --target-name rover-relay-recorded \\
        --expect-situation ORBITING --keep-parsek

i.e. this tool edits `harness/fixtures/saves/rover-relay-recorded` IN PLACE. The
harvest did the generic half (title normalisation, TWO `rewindSave` hint clears,
`Parsek/Saves` prune, the `.craft.txt` snapshot-mirror prune); everything below is
relay-specific.

`--expect-situation ORBITING` LOOKS WRONG FOR A LANDED-ROVER FIXTURE AND IS
CORRECT, for the reason `build_rover_route_recorded.py` sets out at length: the
gate is armed against the SOURCE, never against the RESULT. This source was saved
from the SPACE CENTER, so KSP left `FLIGHTSTATE/activeVessel = 0` pointing at
`Ast. UYX-230`, a stock DiscoverableObjects asteroid in solar orbit - and step 1
below re-points it to `rover C`, LANDED. Passing LANDED at harvest time would FAIL
the gate on a HEALTHY source; passing ORBITING keeps it a real gate (a source
whose index-0 vessel moved reds there), and the LANDED assertion on the vessel
that actually ends up focused lives in `_verify_active_vessel` below. Do NOT widen
the harvest gate to `ORBITING,LANDED` - that accepts either, which is what neither
half is allowed to do.

WHAT IT DOES, in order:

  1. THE ACTIVE VESSEL. Re-points `FLIGHTSTATE/activeVessel` from the asteroid at
     index 0 to `rover C` at index 1 - the TRANSPORT rover, the relay's own
     vessel and the one whose tree both lanes address. Three reasons:
       * AN ASTEROID-FOCUSED BOOT IS NOT A HOST. `TestCommandLoadGame`'s
         `IsLoadedGameFocusable` accepts index 0 happily, so the fixture WOULD
         boot - into deep space 13.5 Gm out, with all three rovers unloaded and
         every live-vessel `Logistics` guard skipping. RVR-6's whole product is
         the census of those guards.
       * `rover C` is where an operator would be sitting to try this relay.
       * It is LANDED rather than PRELAUNCH, so it is not the fresh-rollout shape
         `RecordingStore.SceneEntryFreshRolloutVesselPid` has a fast path for -
         the same reason the sibling fixture re-points away from `rover fuel 0`.
     The index is RE-RESOLVED by name + persistentId; the constant is then
     asserted against it, so a re-harvest that reordered FLIGHTSTATE reds naming
     the new index instead of silently focusing whatever now sits at 1.
  2. THE ADDONS SCAFFOLDING. The collected save has no `AddOns/` at all; the
     618-byte `DistantObject/Settings.cfg` every sibling fixture carries is copied
     from `rover-route-recorded` (a fixture of the same class and lane family),
     and `verify_tree` re-checks its size AND the donor's bytes.

WHAT IT DELIBERATELY DOES NOT DO:

  * NO TREE IS DROPPED, and the THREE-TREE FOREST is the point rather than
    untidiness. `d33027c4` (rover B's own launch) and `c7324bee` (rover A's) are
    what make the relay's two ENDPOINTS committed recordings rather than bare
    pids: window 1 names pid 2123618197, carried by `d33027c4`'s root recording;
    window 2 names pid 831319732, carried by `c7324bee`'s. Drop either and the
    fixture still LOOKS right - same window count, same branch points - while the
    cross-tree partner link that `RouteProof_CrossTreeCommittedPartner_
    HasEndpointProof` walks silently goes to Skip on BOTH windows.
    `verify_route_windows` asserts the link for each window separately.
  * NO SPACE OBJECT IS PRUNED. The three stock asteroids are kept verbatim, on
    the sibling's precedent: pruning them would move every index the re-point
    resolves against for no benefit, and no lane reads them.
  * NO SIDECAR IS SWEPT. All nine recordings have their family on disk and vice
    versa (the harvest's own orphan sweep found none).
  * NOTHING IS SEALED, and nothing needs to be. Every one of the nine RECORDING
    nodes is ALREADY `MergeState.Immutable`, which the codec spells by OMITTING
    the `mergeState` key (`RecordingTreeRecordCodec.SaveRewindToStagingMergeState`
    writes it only for a non-default value; the loader defaults a missing key to
    Immutable). So `RouteCandidateFinder.IsTreeFullySealed` is already true for
    all three trees, and RVR-5's `SealSlot tree=<relay>` is expected to answer
    `total=7 sealed=0 remaining=0 alreadySealed=True` - the idempotent no-op
    guard. `verify_seal_state` is that pin. THIS MATTERS MORE HERE THAN ON THE
    SIBLING: RVR-5's product is a REFUSAL, and a refusal is only attributable to
    the analysis if the two cheap gates ahead of it
    (`ClassifyCreateRefusal`: found -> dismissed -> sealed -> eligible) are known
    to pass. An unsealed tree would refuse `tree-not-sealed` and the lane would
    prove nothing about candidacy at all.
  * NO ENDPOINT REPAIR, and that is a MEASUREMENT rather than an omission. The
    sibling needed one because the operator had hand-driven a route delivery over
    its trees before the save was written. Here no route was EVER created (0
    `ROUTES`, 0 `PROMPTED_ROUTE_CANDIDATES`, 0 route ledger actions), so nothing
    in the FLIGHTSTATE is post-delivery output and there is nothing to revert.
    `verify_no_route_state` states that as a positive fact.

THE GEOMETRY, pinned because RVR-6's census turns on it and because a re-harvest
that moved a rover would change which cells self-skip. Great-circle-free straight
line distances between the three LANDED rovers, computed from their own
FLIGHTSTATE lat / lon / alt against Kerbin's 600 km radius:
    A - B  783.46 m      A - C  336.16 m      B - C  982.81 m
All three are far outside the 200 m dock range and well inside physics range of
each other, which is why the relay is a DRIVE rather than a warp.
CORRECTED 2026-09-02: this used to add "and why any route driven over it would
take `path=loaded`, not the sibling's `path=unloaded`". SEPARATION DOES NOT DECIDE
THE WRITER PATH ON A DRIVEN LANE. RVR-7's first census measured `path=unloaded` on
every writer over the OTHER relay fixture, whose rovers are just as close, because
a seam `TimeJump` warps with the endpoints PACKED and it is the load state at the
DISPATCH TICK that decides. A player driving the relay by hand would see the
loaded path.

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture and
writes nothing. It is WIRED, not decorative:
`RoverRelayRecordedFixtureDriftTests` in
`harness/lib/test_build_rover_relay_recorded.py` runs the same `verify_*`
functions in-process, so a hand-edit of the committed bytes reds in the harness
suite. Like its templates it CANNOT re-run `build`: the input is a collected
operator save outside the repo that will never be committed.

Usage:
    python harness/tools/build_rover_relay_recorded.py            # finish in place
    python harness/tools/build_rover_relay_recorded.py --check    # verify only

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

# The shared save parser, for the route facts (the no-ROUTES assertion and the two
# sparse candidate-intent siblings), read in the SAME vocabulary a scenario
# declares through `[expectations.routes]`.
import saveparse  # noqa: E402

# ONE copy of the ConfigNode-text node helpers, for the reason both sibling
# builders import them: a second implementation is a second thing to drift. The
# FILE I/O is deliberately NOT reused - those helpers normalise to CRLF on write,
# and `harvest_bdock_station.py` writes this save's `persistent.sfs` with an
# explicit LF-only newline. Keeping the harvest's own line endings means the
# committed bytes are still "what the tool chain wrote".
import build_career_pad_craft as base_builder  # noqa: E402

find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value

TARGET_NAME = "rover-relay-recorded"

# The AddOns donor: the OTHER rover fixture, so the donor is a fixture of the same
# class and the same lane family.
ADDONS_DONOR_NAME = "rover-route-recorded"
ADDONS_REL = os.path.join("AddOns", "DistantObject", "Settings.cfg")
ADDONS_EXPECTED_BYTES = 618

# `Parsek/GameState` comes through the harvest INTACT here, unlike the sibling
# (whose source was a `collect-logs.py` folder with `Parsek/` moved aside). Pinned
# by name anyway, so a re-harvest that lost the ledger reds loudly rather than
# shipping a thinner fixture: four `MilestonesModule` baselines plus the three
# journals.
GAMESTATE_FILES = (
    "baseline_0.pgsb",
    "baseline_151.06000000000347.pgsb",
    "baseline_439.23999999989047.pgsb",
    "baseline_55.040000000003005.pgsb",
    "events.pgse",
    "ledger.pgld",
    "milestones.pgsm",
)

# --- the three trees, kept WHOLE ----------------------------------------
#
# Spelled out rather than derived from the save so that a re-harvest whose shape
# moved reds loudly here instead of silently shipping a different payload.
#
# The two ORIGIN trees are single-recording launches whose terminal spawn was
# absorbed by the relay: each carries `terminalSpawnSupersededBy` naming the
# relay tree's dock member. They are NOT superseded in the Rewind sense (there
# are zero `RECORDING_SUPERSEDES` rows) and both are ordinary committed trees.
ENDPOINT_B_TREE_ID = "d33027c48daf416c9c0c8ccca8697ae7"   # rover B's own launch
ENDPOINT_A_TREE_ID = "c7324bee5fd34ebfa897b84135dca5d9"   # rover A's own launch
RELAY_TREE_ID = "87fba47a981e4c86a598fe855a6e8113"        # `rover C`, the relay

ENDPOINT_B_TREE_RECORDING_IDS = (
    "073a1ed6fdbc411da694dfcc59bdbc9f",   # 0  rover B's launch; pid 2123618197
)
ENDPOINT_A_TREE_RECORDING_IDS = (
    "9511fa11878e413d9e4ea1861afae034",   # 0  rover A's launch; pid 831319732
)
# The relay: launch -> dock member (WINDOW 1) -> drive leg -> dock member
# (WINDOW 2) -> tail, plus the two separated partners at treeOrder 3 and 6.
RELAY_TREE_RECORDING_IDS = (
    "31e843024f3347dfafc030f8d64796be",   # 0  rover C's launch
    "e175776c7c614e0a893a15f5bf84ff2c",   # 1  DOCK MEMBER at B (window 1)
    "5f76d136e3dc4316bff71f4cfb0688a4",   # 2  the drive from B to A
    "49eaec92876041efa53deb1f5e5c96f4",   # 3  rover B after the first undock
    "e6cb44a7243d4377a5c6051c91636c0b",   # 4  DOCK MEMBER at A (window 2)
    "ff014f588ed640aaa8e48fbabc8a1c38",   # 5  rover C after the second undock
    "0f391265a0b2453ea94fccd5daa1febb",   # 6  rover A after the second undock
)
KEEP_RECORDING_IDS = (ENDPOINT_B_TREE_RECORDING_IDS
                      + ENDPOINT_A_TREE_RECORDING_IDS
                      + RELAY_TREE_RECORDING_IDS)

# Tree id -> its recording ids in `treeOrder`, in the order the trees appear in
# the save. The order is asserted, so a re-harvest that reordered them reds.
TREES_IN_FILE_ORDER = (
    (ENDPOINT_B_TREE_ID, ENDPOINT_B_TREE_RECORDING_IDS),
    (ENDPOINT_A_TREE_ID, ENDPOINT_A_TREE_RECORDING_IDS),
    (RELAY_TREE_ID, RELAY_TREE_RECORDING_IDS),
)

# The relay tree's active recording, which is what `SealSlot`'s `total=` counts
# over and what RVR-5 pins as `total=7`.
RELAY_TREE_ACTIVE_RECORDING_ID = "ff014f588ed640aaa8e48fbabc8a1c38"

# --- the TWO route windows (the fixture's whole reason to exist) ---------
#
# `saveparse.py` has no route-window facet, so without this the surfaces the
# fixture exists for would be unpinned everywhere. Every value below was read off
# the harvested bytes.
DOCK_MEMBER_VESSEL_PID = "1461186781"          # rover C, the transport, both hops

# (carrying recordingId, {scalar pins}, {ENDPOINT_AT_DOCK pins}) per window, in
# file order. BOTH windows are TARGET-branch (`transferTargetPid` differs from the
# carrying recording's `vesselPersistentId`), and both target pids are carried by
# a recording in a DIFFERENT committed tree - the property `rover-route-recorded`
# holds once and this fixture holds twice.
ROUTE_WINDOWS = (
    {
        "recordingId": "e175776c7c614e0a893a15f5bf84ff2c",
        "treeId": RELAY_TREE_ID,
        "pins": {
            "windowId": "dock-218.22000000003783-target-2123618197",
            "dockUT": "218.22000000003783",
            "undockUT": "276.00000000003894",
            "transferTargetPid": "2123618197",
            "transferKind": "DockingPort",
            # 1 == Vessel.Situations.LANDED as `RouteConnectionWindow` serialises
            # it: both hops are SURFACE endpoints.
            "transferEndpointSituation": "1",
        },
        "endpoint": {
            "vesselPersistentId": "2123618197",
            "bodyName": "Kerbin",
            "latitude": "-0.093523569607093682",
            "longitude": "-74.725342107344545",
            "altitude": "65.949262386420742",
            "isSurface": "True",
        },
        # The cross-tree partner recording that carries the target pid.
        "partner": (ENDPOINT_B_TREE_ID, ENDPOINT_B_TREE_RECORDING_IDS[0]),
    },
    {
        "recordingId": "e6cb44a7243d4377a5c6051c91636c0b",
        "treeId": RELAY_TREE_ID,
        "pins": {
            "windowId": "dock-340.11999999998062-target-831319732",
            "dockUT": "340.11999999998062",
            "undockUT": "402.49999999992389",
            "transferTargetPid": "831319732",
            "transferKind": "DockingPort",
            "transferEndpointSituation": "1",
        },
        "endpoint": {
            "vesselPersistentId": "831319732",
            "bodyName": "Kerbin",
            "latitude": "-0.11923496061088666",
            "longitude": "-74.795422768920943",
            "altitude": "65.970190355321392",
            "isSurface": "True",
        },
        "partner": (ENDPOINT_A_TREE_ID, ENDPOINT_A_TREE_RECORDING_IDS[0]),
    },
)

# (name, amount, maxAmount) per RESOURCE row, per window, per node.
#
# THE RESOURCE DELTAS BALANCE ON BOTH HOPS, and that is the half of the relay that
# the analysis has no complaint about:
#   hop 1  transport 200 -> 400        endpoint B 200 -> 0        = +200 / -200
#   hop 2  transport 400 -> 273.2      endpoint A 200 -> 326.8    = -126.8 / +126.8
# So a reader who sees `candidate-ineligible` must NOT conclude the resource
# bookkeeping failed. It did not; the INVENTORY half did (see
# ROUTE_WINDOW_INVENTORY_ROWS and verify_unwitnessed_inventory_gain).
ROUTE_WINDOW_RESOURCE_ROWS = (
    {
        "DOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "200", "400"),
        "UNDOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "399.99999999999085", "400"),
        "DOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "200", "400"),
        "UNDOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "0", "400"),
    },
    {
        "DOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "399.99999999999085", "400"),
        "UNDOCK_TRANSPORT_RESOURCES": ("LiquidFuel", "273.19999999999806", "400"),
        "DOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "200", "400"),
        "UNDOCK_ENDPOINT_RESOURCES": ("LiquidFuel", "326.79999999999677", "400"),
    },
)

# The four stored-part identity hashes the two windows use, named so the
# unwitnessed-gain argument below reads as identities rather than as hex.
#
# THE TWO STATION HASHES ARE ONE PHYSICAL PART, BEFORE AND AFTER A LIVE MOVE.
# `HASH_STATION_ORIGINAL` is what the craft-authored `STOREDPART` hashes to and
# is what BOTH rovers start holding (they share a craft file, so the collision is
# expected and harmless). `HASH_STATION_MOVED` is what the SAME part hashes to
# after stock's `StoreCargoPartAtSlot(Part, int)` re-serialised it through a live
# `ProtoPartSnapshot` and `ModuleGroundExpControl.OnSave` added `canComm = False`.
# Naming them "original" and "moved" rather than "A" and "B" is deliberate: they
# are not two objects.
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
# READ HOP 1's TRANSPORT PAIR AGAINST ITS ENDPOINT PAIR - that is the whole
# rejection. The transport ARRIVES holding one station at HASH_STATION_ORIGINAL
# (its own, from the shared craft file) and LEAVES holding TWO, the second at
# HASH_STATION_MOVED. The endpoint arrives holding one at HASH_STATION_ORIGINAL
# and leaves holding none. THE TWO STATION HASHES ARE THE SAME PART: the move
# re-serialised it through a live `ProtoPartSnapshot` and picked up a `canComm`
# value the craft-authored node never had. So by IDENTITY there is no endpoint
# loss to pair the gain with, and
# `RouteAnalysisEngine.HasUnwitnessedInventoryGain` fails the window closed with
# `RouteAnalysisStatus.MixedPickupDelivery`.
#
# CONTRAST THE OTHER TWO ITEMS, and it is what makes the defect legible rather
# than mysterious: `evaChute` closes cleanly (transport 1 -> 2, endpoint 1 -> 0,
# ONE identity) and so does `evaScienceKit`. Their modules write nothing new in
# `OnSave`, so their hashes survive the same move. Only the station re-hashes.
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
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("evaChute", "1", HASH_EVA_CHUTE)),
        "DOCK_ENDPOINT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("evaChute", "1", HASH_EVA_CHUTE),
            ("evaScienceKit", "2", HASH_EVA_SCIENCE_KIT)),
        "UNDOCK_ENDPOINT_INVENTORY": (
            ("DeployedCentralStation", "1", HASH_STATION_ORIGINAL),
            ("DeployedCentralStation", "1", HASH_STATION_MOVED),
            ("evaChute", "2", HASH_EVA_CHUTE),
            ("evaScienceKit", "5", HASH_EVA_SCIENCE_KIT)),
    },
)

# The window the unwitnessed gain rides (index into ROUTE_WINDOWS) and the hash
# that has no matching endpoint loss. Stated as constants so the argument in
# `verify_unwitnessed_inventory_gain` reads as a claim about identities.
UNWITNESSED_GAIN_WINDOW_INDEX = 0
UNWITNESSED_GAIN_HASH = HASH_STATION_MOVED
UNWITNESSED_GAIN_PART_NAME = "DeployedCentralStation"

# --- the four branch points ---------------------------------------------
#
# (mergeCause-or-None, ut, parentId, targetVesselPid-or-None) per BRANCH_POINT on
# the relay tree, in file order. `type` 2 is the DOCK merge, `type` 0 the undock
# split. Pinned because `[expectations.recordings.structure] branchPoints` counts
# them by KIND and a re-harvest that lost a hop would still read "4 nodes".
BRANCH_POINTS = (
    ("DOCK", "218.22000000003783", "31e843024f3347dfafc030f8d64796be", "2123618197"),
    (None, "276.00000000003894", "e175776c7c614e0a893a15f5bf84ff2c", None),
    ("DOCK", "340.11999999998062", "5f76d136e3dc4316bff71f4cfb0688a4", "831319732"),
    (None, "402.49999999992389", "e6cb44a7243d4377a5c6051c91636c0b", None),
)

# --- the active vessel (step 1) -----------------------------------------
ACTIVE_VESSEL_INDEX = 1
ACTIVE_VESSEL_NAME = "rover C"
ACTIVE_VESSEL_PID = DOCK_MEMBER_VESSEL_PID
ACTIVE_VESSEL_SITUATION = "LANDED"
# What the source save left focused, asserted as NOT the vessel at the re-pointed
# index, so a re-harvest that reordered FLIGHTSTATE cannot silently re-point back.
SOURCE_ACTIVE_VESSEL_NAME = "Ast. UYX-230"

# THE THREE REAL VESSELS: (name, persistentId, type, situation, guid).
#
# NOTE THE PID SPLIT ACROSS THE UNDOCKS, because it is the craft-baked-pid trap
# showing up as a fixture property in the OTHER direction from the sibling's. The
# two ENDPOINT recordings name pids 2123618197 (B) and 831319732 (A), but the LIVE
# rovers B and A carry 35783242 and 1625259141: `Part.Undock` re-pids the
# separated half, so the live vessel is not the pid its own recorded window names.
#
# CORRECTED 2026-09-03. This comment used to conclude "so a route driven over these
# bytes would find NO live endpoint at all", and named that as a third, independent
# reason the fixture could not host a delivery. THAT CONCLUSION IS WRONG and is
# struck. `RouteEndpointResolver.TryResolveEndpoint` does not resolve by pid alone:
# `NextEndpointStep` walks RootPart -> Pid -> SurfaceProximity, and the proximity
# step is a great-circle search bounded by
# `RouteOrchestrator.SurfaceProximityRadiusMeters = 500` against the window's own
# recorded `ENDPOINT_AT_DOCK` coordinates - which every one of these LANDED rovers
# is within metres of. Only the PID step misses. Nothing in either lane ever
# depended on the claim; it is corrected rather than deleted so the next reader
# does not re-derive it from the pid split alone.
REQUIRED_VESSELS = (
    (ACTIVE_VESSEL_NAME, ACTIVE_VESSEL_PID, "Rover", "LANDED",
     "ebb4fcf9704e4f79ba8a46f004f4f5c3"),
    ("rover B", "35783242", "Rover", "LANDED",
     "e5d9adf8032c420a94ebb18fc78574fa"),
    ("rover A", "1625259141", "Rover", "LANDED",
     "0da5b9eea8524960ae39f9ccb285da43"),
)
# 6 FLIGHTSTATE VESSEL nodes: the three rovers plus three stock asteroids the
# source save's DiscoverableObjects scenario had already spawned. The asteroids
# are kept verbatim - pruning them would move every index the re-point resolves
# against for no benefit, and no lane reads them.
EXPECTED_VESSEL_COUNT = 6
EXPECTED_REAL_VESSEL_COUNT = 3

# The save clock, and the three rovers' LiquidFuel / ElectricCharge.
#
# `rover C`'s 273.2 / 400 IS THE `Logistics` FIXTURE REQUIREMENT, not staging:
# `UnloadedFuelVesselFixture` returns `reason = "no-liquidfuel-resource"` and every
# unloaded-depot cell skips unless the active vessel carries a LiquidFuel RESOURCE
# node with positive capacity. `rover B`'s 0 / 400 is the relay's own output (it
# gave all 200 away) and is kept: a zero-amount tank is still a positive-CAPACITY
# tank, and it is what makes B the drained end of the relay.
SAVE_UT = 443.63999999988647
SAVE_UT_EPS = 1e-6
VESSEL_RESOURCES = {
    # persistentId -> {resource: (amount, maxAmount)}
    "1461186781": {"LiquidFuel": (273.19999999999806, 400.0),
                   "ElectricCharge": (922.3304992269995, 1000.0)},
    "35783242": {"LiquidFuel": (0.0, 400.0),
                 "ElectricCharge": (1000.0, 1000.0)},
    "1625259141": {"LiquidFuel": (326.79999999999677, 400.0),
                   "ElectricCharge": (1000.0, 1000.0)},
}
VESSEL_RESOURCE_EPS = 1e-6

# Straight-line separations between the three LANDED rovers, in metres, computed
# from their own FLIGHTSTATE lat / lon / alt against Kerbin's radius. Pinned to
# one decimal with a 1 m tolerance: the point is the SCALE (hundreds of metres,
# far outside the 200 m dock range and well inside physics range), not the digit.
KERBIN_RADIUS_M = 600000.0
VESSEL_SEPARATIONS = {
    ("rover A", "rover B"): 783.5,
    ("rover A", "rover C"): 336.2,
    ("rover B", "rover C"): 982.8,
}
VESSEL_SEPARATION_TOLERANCE_M = 1.0

# Harvest exhaust and derived data no fixture may carry. `Saves` and `analysis`
# are the two worth naming: the source carries `Parsek/Saves` (FIVE `parsek_rw_*`
# plus a `parsek_career_start.sfs`, all pruned by the harvest, with the TWO
# `rewindSave` hints that referenced two of them cleared - both of those two
# payloads DID exist in the source, so the prune is what would have made the
# hints dangle), and running
# `analyze-recordings.ps1` against the fixture WRITES an `analysis/` directory into
# it that must not be committed.
FORBIDDEN_DIR_NAMES = ("_quarantine", "Saves", "Backup", "RewindPoints", "Ships",
                       "analysis")
FORBIDDEN_FILE_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")
FORBIDDEN_FILE_PREFIXES = ("quicksave",)

# One recording legitimately carries no vessel snapshot of its own, so the
# per-family completeness check below is a floor plus this exemption rather than
# "every family has all four".
NO_VESSEL_CRAFT_RECORDING_IDS = ("31e843024f3347dfafc030f8d64796be",)
NO_GHOST_CRAFT_RECORDING_IDS = ()
# 9 x .prec + 9 x .pann + 8 x _vessel.craft + 9 x _ghost.craft.
EXPECTED_AUTHORITATIVE_SIDECARS = 35


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
    """Point `FLIGHTSTATE/activeVessel` at the transport rover `rover C`.

    Returns (lines, note). The index is RE-RESOLVED from the VESSEL list by name
    + persistentId rather than taken from the constant, and the constant is then
    asserted against it - so a re-harvest that reordered FLIGHTSTATE reds naming
    the new index instead of silently focusing whatever now sits at 1."""
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
    problems += verify_unwitnessed_inventory_gain(lines, scn)
    problems += _verify_active_vessel(lines)
    problems += verify_geometry(lines)
    return problems


def verify_no_route_state(lines: List[str],
                          scn: Tuple[int, int]) -> List[str]:
    """THE FIRST FAIL-CLOSED REASON, AS THREE POSITIVE ABSENCES.

    A relay this complete producing NOTHING is the fixture's whole claim, so the
    nothing is asserted rather than assumed:

      (a) NO `ROUTE_ORIGIN_PROOF` NODE ANYWHERE. The producer refused both dock
          seams because neither half is typed Base or Station - its own line reads
          `seams=2 candidates=0 ... (neither docked half is typed Base or Station
          ...)`. A proof node here would mean the producer had found a depot,
          which would make the whole lane measure something else.
          NOTE THIS IS A DIFFERENT ABSENCE FROM `rover-route-recorded`'s. There
          the producer skips because both trees start at a KSC site (the
          by-design KSC-origin skip); here it skips because it walked two real
          non-KSC dock seams and admitted neither. Same zero, different branch.
      (b) NO `ROUTES` NODE and NO `PROMPTED_ROUTE_CANDIDATES`. The second reason -
          `RouteAnalysisStatus.MixedPickupDelivery` - means Parsek never even
          OFFERED the relay tree as a candidate, so unlike `rover-route-recorded`
          (which carries a prompted-candidate row) there is no record of an offer.
          That difference IS the fixture: a prompted row here would contradict the
          measured `candidates=0`.
      (c) NO `DISMISSED_ROUTE_CANDIDATES`. A dismissed tree is skipped by the
          finder BEFORE the analysis runs, so a dismissal row would make RVR-5's
          refusal read `candidate-dismissed` instead of `candidate-ineligible` -
          the same REJECTED verdict for an entirely different reason.
    """
    problems: List[str] = []

    proofs = [i for i, line in enumerate(lines, 1)
              if line.strip() == "ROUTE_ORIGIN_PROOF"]
    if proofs:
        problems.append(
            "save carries a ROUTE_ORIGIN_PROOF node at line(s) %s - the producer "
            "refused both dock seams here (`no depot half ... seams=2 "
            "candidates=0`), so a proof node means a depot was found and the "
            "fixture is measuring something else" % (proofs,))

    snap = saveparse.parse_parsek_scenario("\n".join(lines))
    if not snap.parsed:
        return problems + ["saveparse could not read the save: %s" % snap.error]

    facet = saveparse.observed_routes_facets(snap)
    if facet["count"] or facet["dormant"]:
        problems.append(
            "ParsekScenario carries %d committed / %d dormant route(s) - NO route "
            "was ever created over this save, and RVR-5's whole product is the "
            "REFUSAL of a create" % (facet["count"], facet["dormant"]))
    if snap.prompted_candidate_tree_ids:
        problems.append(
            "PROMPTED_ROUTE_CANDIDATES names %r - Parsek never offered this relay "
            "as a candidate (the measured derive reads candidates=0), so a "
            "prompted row contradicts the fixture's own evidence"
            % (list(snap.prompted_candidate_tree_ids),))
    if snap.dismissed_candidate_tree_ids:
        problems.append(
            "ParsekScenario carries DISMISSED_ROUTE_CANDIDATES %r - a dismissed "
            "tree is skipped BEFORE the analysis, so RVR-5's refusal would read "
            "candidate-dismissed rather than candidate-ineligible"
            % (list(snap.dismissed_candidate_tree_ids),))
    return problems


def verify_seal_state(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """THE SEAL PIN, and RVR-5's ATTRIBUTION rests entirely on it.

    `RecordingTreeRecordCodec` OMITS `mergeState` for the default
    `MergeState.Immutable` and the loader defaults a missing key back to it, so
    "every recording is Immutable" is spelled in the bytes as "no RECORDING node
    carries a mergeState key at all". That makes
    `RouteCandidateFinder.IsTreeFullySealed` true for all three trees WITHOUT any
    seal being driven, which is the no-op shape RVR-5's SealSlot step asserts.

    IT MATTERS MORE HERE THAN ON THE SIBLING. `ClassifyCreateRefusal` walks
    found -> dismissed -> sealed -> eligible and returns the FIRST failure, so an
    unsealed tree would refuse `tree-not-sealed` and RVR-5 would prove nothing
    about candidacy. The refusal is only attributable to the ANALYSIS when the
    three gates ahead of it are known to pass.

    Stated as an absence over the WHOLE save rather than per node, because a stray
    key anywhere - including on a tree neither lane addresses - would flip the
    same predicate."""
    problems: List[str] = []
    stray = [i for i, line in enumerate(lines, 1)
             if line.strip().startswith("mergeState = ")]
    if stray:
        problems.append(
            "a mergeState key survives at line(s) %s: at least one recording is "
            "NOT Immutable, so IsTreeFullySealed is false and RVR-5's "
            "`SealSlot ... alreadySealed=True` pin is a lie" % (stray,))

    # And the same claim from the other side: the count of RECORDING nodes the
    # seal predicate would walk, so a fixture that lost recordings cannot pass the
    # absence check vacuously. The RELAY tree's own count is asserted separately
    # because RVR-5 pins it as `total=7`.
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
            "the relay tree carries %r RECORDING node(s), expected %d - RVR-5 "
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


def verify_route_windows(lines: List[str], scn: Tuple[int, int]) -> List[str]:
    """THE TWO-WINDOW PIN. `saveparse.py` has no route-window facet, so this is
    the only place the fixture's central surface is asserted.

    Per window it states:
      (a) which recording and tree carries it, and the carrier's own pid;
      (b) its scalar shape (ids, clocks, kind, LANDED situation) and its
          ENDPOINT_AT_DOCK coordinates;
      (c) that it is TARGET-branch - `transferTargetPid` differs from the carrying
          recording's `vesselPersistentId`. Both windows are, which is the
          property `rover-route-recorded` holds ONCE and this fixture holds TWICE;
      (d) that the target pid is carried by a recording in a DIFFERENT committed
          tree, i.e. the cross-tree partner link, named per window so dropping
          either origin tree reds against the window that needed it;
      (e) the four RESOURCE rows, whose deltas BALANCE on both hops - stated so a
          reader of RVR-5's refusal does not conclude the resource bookkeeping is
          what failed. It is not."""
    problems: List[str] = []
    windows, rec_pids = _window_records(lines, scn)

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
        if rec_pid != DOCK_MEMBER_VESSEL_PID:
            problems.append("%s's carrier vesselPersistentId is %r, expected %r"
                            % (tag, rec_pid, DOCK_MEMBER_VESSEL_PID))

        for key, value in sorted(want["pins"].items()):
            got = get_value(lines, window, key)
            if got != value:
                problems.append("%s %s is %r, expected %r"
                                % (tag, key, got, value))

        # (c) TARGET-BRANCH, stated as the predicate the in-game cell evaluates
        # rather than as two constants that happen to differ.
        target = get_value(lines, window, "transferTargetPid")
        if target == rec_pid:
            problems.append(
                "%s is INITIATOR-branch (transferTargetPid == the carrying "
                "recording's vesselPersistentId == %r)" % (tag, target))

        # (d) THE CROSS-TREE LINK, per window.
        want_tree, want_rec = want["partner"]
        holders = sorted(r for r, p in rec_pids.items() if p == target)
        if holders != [want_rec]:
            problems.append(
                "%s's target pid %r is carried by recording(s) %r, expected "
                "exactly %r (tree %s must be kept WHOLE)"
                % (tag, target, holders, [want_rec], want_tree))

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

    # THE RESOURCE DELTAS BALANCE, checked as arithmetic over the pinned rows so
    # the numbers and the claim cannot drift apart.
    for i in range(len(ROUTE_WINDOWS)):
        rows = ROUTE_WINDOW_RESOURCE_ROWS[i]
        transport = (float(rows["UNDOCK_TRANSPORT_RESOURCES"][1])
                     - float(rows["DOCK_TRANSPORT_RESOURCES"][1]))
        endpoint = (float(rows["UNDOCK_ENDPOINT_RESOURCES"][1])
                    - float(rows["DOCK_ENDPOINT_RESOURCES"][1]))
        if abs(transport + endpoint) > 1e-6:
            problems.append(
                "window %d's LiquidFuel deltas do not balance (transport %+.6f, "
                "endpoint %+.6f) - the relay's RESOURCE half is what this fixture "
                "shows working, so an imbalance changes what the refusal means"
                % (i, transport, endpoint))
        if abs(transport) < 1e-6:
            problems.append("window %d moved no LiquidFuel at all" % i)
    return problems


def verify_unwitnessed_inventory_gain(lines: List[str],
                                      scn: Tuple[int, int]) -> List[str]:
    """THE SECOND FAIL-CLOSED REASON, AS THE PREDICATE THE ENGINE EVALUATES.

    `RouteAnalysisEngine.HasUnwitnessedInventoryGain` matches a transport gain to
    an endpoint loss BY IDENTITY HASH, and rejects
    `RouteAnalysisStatus.MixedPickupDelivery` when a gain has none. The refusal is
    a claim about hashes, so it is asserted about hashes here rather than about
    part names or quantities.

    WHAT THIS GUARDS IS THE REGRESSION SUBJECT, NOT AN OPERATOR MISTAKE. The two
    station hashes are ONE physical part before and after a live move; the split
    is LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-CARGO-MOVE (OPEN). So
    the thing this cell protects against is the fixture being quietly "cleaned
    up": a re-harvest flown with NO inventory moved, or flown after the hash is
    fixed, produces a window with no unwitnessed gain and an ELIGIBLE tree - which
    would leave RVR-5's refusal pins asserting something the bytes no longer hold,
    and would silently retire the only committed subject the defect has.
    When the fix lands, this cell and RVR-5's pins are re-measured TOGETHER; do
    not relax either one alone.

    Read from the BYTES, not from the constants: the check re-derives the gain and
    the loss sets from the window's own four inventory nodes and requires the
    named hash to be gained by the transport and NOT lost by the endpoint."""
    problems: List[str] = []
    windows, _rec_pids = _window_records(lines, scn)
    if len(windows) <= UNWITNESSED_GAIN_WINDOW_INDEX:
        return ["the save carries no window %d to check the unwitnessed gain on"
                % UNWITNESSED_GAIN_WINDOW_INDEX]
    window = windows[UNWITNESSED_GAIN_WINDOW_INDEX][3]

    def totals(node_name: str) -> Dict[Tuple[str, str], int]:
        out: Dict[Tuple[str, str], int] = {}
        for holder in child_nodes(lines, window, node_name):
            for item in child_nodes(lines, holder, "ITEM"):
                key = (get_value(lines, item, "partName") or "",
                       get_value(lines, item, "identityHash") or "")
                try:
                    qty = int(get_value(lines, item, "quantity") or "0")
                except ValueError:
                    qty = 0
                out[key] = out.get(key, 0) + qty
        return out

    dock_t = totals("DOCK_TRANSPORT_INVENTORY")
    undock_t = totals("UNDOCK_TRANSPORT_INVENTORY")
    dock_e = totals("DOCK_ENDPOINT_INVENTORY")
    undock_e = totals("UNDOCK_ENDPOINT_INVENTORY")

    key = (UNWITNESSED_GAIN_PART_NAME, UNWITNESSED_GAIN_HASH)
    gained = undock_t.get(key, 0) - dock_t.get(key, 0)
    lost = dock_e.get(key, 0) - undock_e.get(key, 0)
    if gained <= 0:
        problems.append(
            "the transport does NOT gain %s at hash %s across window %d "
            "(delta %+d) - the MixedPickupDelivery rejection this fixture exists "
            "to hold has no cause in the bytes"
            % (UNWITNESSED_GAIN_PART_NAME, UNWITNESSED_GAIN_HASH[:8],
               UNWITNESSED_GAIN_WINDOW_INDEX, gained))
    if lost > 0:
        problems.append(
            "the endpoint LOSES %d of %s at hash %s across window %d, so the "
            "transport's gain IS witnessed and the analysis would no longer "
            "reject MixedPickupDelivery"
            % (lost, UNWITNESSED_GAIN_PART_NAME, UNWITNESSED_GAIN_HASH[:8],
               UNWITNESSED_GAIN_WINDOW_INDEX))

    # And the mirror half, so the claim is not "some hash is unmatched" but "the
    # SAME part left under a different identity": the endpoint really does give up
    # a station, at the pre-move hash, and that loss is what the gain should have
    # paired with.
    original = (UNWITNESSED_GAIN_PART_NAME, HASH_STATION_ORIGINAL)
    if dock_e.get(original, 0) - undock_e.get(original, 0) <= 0:
        problems.append(
            "the endpoint does not give up %s at hash %s across window %d - the "
            "re-hash argument (a real endpoint loss at the pre-move hash, paired "
            "with nothing because the gain arrived under a new one) no longer "
            "holds"
            % (UNWITNESSED_GAIN_PART_NAME, HASH_STATION_ORIGINAL[:8],
               UNWITNESSED_GAIN_WINDOW_INDEX))
    if HASH_STATION_MOVED == HASH_STATION_ORIGINAL:
        problems.append("the two station hashes are equal, so there is no "
                        "identity mismatch to reject")
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
            "activeVessel is still the source save's %r, an ASTEROID 13.5 Gm from "
            "the relay - a boot there leaves all three rovers unloaded and every "
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

    # THE PER-VESSEL RESOURCES, which decide which Logistics guards find a subject.
    for pid, wanted in sorted(VESSEL_RESOURCES.items()):
        record = by_pid.get(pid)
        if record is None:
            continue
        for resource, (want_a, want_m) in sorted(wanted.items()):
            amount, maximum = _sum_resource(lines, record["span"], resource)
            if (abs(amount - want_a) > VESSEL_RESOURCE_EPS
                    or abs(maximum - want_m) > VESSEL_RESOURCE_EPS):
                problems.append(
                    "vessel %s (%s) holds %s %r / %r, expected %r / %r"
                    % (pid, record["name"], resource, amount, maximum,
                       want_a, want_m))
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
    changes which live-vessel guards find a subject in RVR-6's census, so the
    layout is a pin rather than prose.

    IT DOES NOT DECIDE THE WRITER PATH. The authored version added "(so any driven
    route over these bytes would take `path=loaded`, unlike the sibling fixture's
    5.4 km `path=unloaded`)", and RVR-7's first census refuted it: a seam `TimeJump`
    warps with the endpoints PACKED, so the load state at the DISPATCH TICK decides,
    not the separation."""
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
