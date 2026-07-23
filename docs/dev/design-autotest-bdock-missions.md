# B-DOCK station + interceptor dock / transfer / undock (bdock_dock_transfer) - design + fixture

Status: DESIGN ONLY (branch `autotest-bdock-design`, 2026-07-23). NOTHING here has
flown. This is the preparation design for the FIRST multi-vessel Parsek autotest
mission: a two-piece flight that launches a Station to Kerbin orbit, launches the
SAME craft again as an Interceptor, rendezvous + docks it to the Station,
transfers fuel in both directions, then undocks - the ENTRY POINT to verifying
Parsek's logistics routes (dock + transfer + undock recording contract). Every
threshold below is a tolerance / budget, never a golden trajectory, and every
numeric budget is ESTIMATED from arithmetic, not measured. The `mlib` machine and
the `mission_runner` action handlers this design specifies are NOT implemented
here; this doc is the plan a follow-up implementation PR applies.

Authorities read (verified, not assumed, except where flagged):
- The pinned KRPC.MechJeb v0.8.1 rendezvous / docking / target / planner surface
  (`darchambault/KRPC.MechJeb` v0.8.1 `RendezvousAutopilot.cs`,
  `DockingAutopilot.cs`, `MechJeb.cs`, cross-checked against the umbrella genhis
  0.7.1 clone `mods/KRPC.MechJeb` and the runner's live-flown `operation_transfer`
  usage) - GROUND TRUTH 1, section 4.1.
- The pinned kRPC v0.5.4 SpaceCenter surface (`mods/krpc` at tag `v0.5.4`:
  `LaunchVessel`, `TargetVessel` / `TargetDockingPort`, `ResourceTransfer.Start`,
  `DockingPort` / `DockingPortState`) - GROUND TRUTH 2, section 4.2.
- Parsek dock / undock recording contract (`docs/dev/dock-undock-recording-structure.md`),
  cross-tree foreign-dock model (`docs/dev/design-mission-crosstree-dock.md`,
  M-MIS-8), the craft-baked-`persistentId` + `VesselLaunchIdentity` guid
  discriminator (`.claude/CLAUDE.md`), `RouteConnectionWindow` /
  `RouteProofCapture` - GROUND TRUTH 3, section 4.3.
- The B2 / B5 mission machine and shells (`harness/missions/lib/mlib.py`,
  `b2_lko_ascent.py`, `mission_runner.py`), the B2 / B5 scenario specs, the B7
  design (`docs/dev/design-autotest-b7-duna.md`) for shape, the coverage registry
  (`harness/coverage/registry.toml`), the committed fixture skeletons
  (`harness/fixtures/saves/b2-lko-craft`, `gloops-airshow`).

House rules: ASCII only, no em dashes. Fail-closed NaN semantics everywhere (a
NaN telemetry read never satisfies a gate). Comments explain constraints, not
history.

---

## 0. What B-DOCK is, in one paragraph

B-DOCK is the first Parsek mission that flies TWO vessels in ONE session and
exercises the dock / transfer / undock recording pipeline that all logistics-route
verification is built on. Both vessels are the SAME stock craft ("Kerbal X", the
docking variant: `mk1-3pod` + a top `dockingPort2` Clamp-O-Tron, 8x `RCSBlock.v2`
fed by 4x `rcsTankRadialLong` MonoPropellant, a Mainsail core with 6 radial
`liquidEngine2` boosters and a `liquidEngine2-2.v2` upper). Piece 1 (Station)
launches, ascends to a ~110 km circular park, and is COMMITTED as its own tree.
Piece 2 (Interceptor) launches the same craft again into a lower phasing orbit,
runs MechJeb's rendezvous autopilot to close, MechJeb's docking autopilot to hard
dock, drives two kRPC `ResourceTransfer`s (LiquidFuel one way, MonoPropellant the
other), undocks, and commits. The Parsek-correctness payoff (section 7): a
cross-tree Dock branch point, an authoritative `onVesselsUndocking` split, a
completed `RouteConnectionWindow` whose recorded resource deltas match the
commanded transfers, and - because both launches are the same `.craft` and so bake
the SAME `persistentId` (3620499050) - a live stress test of the
`VesselLaunchIdentity` guid discriminator that separates the two vessels when
their pids collide.

---

## 1. Feasibility

**Ascent + orbit.** The Kerbal X reaches LKO with margin on the Mainsail core;
B2 flew this exact craft to an 80 km circular park (B2 PASS 2026-07-20). B-DOCK
parks the Station at ~110 km and the Interceptor at a ~90 km phasing orbit
(section 6). Both are inside the B2-proven ascent envelope; the modest apoapsis
bump costs a longer circularize coast, not more stages.

**Rendezvous + docking dv.** After circularization the Interceptor upper stage
retains the same ~1500 m/s the B5 survey measured on this craft. A co-planar LKO
rendezvous from a 20 km phasing altitude offset is ~100-250 m/s of MechJeb
plane / phasing / approach burns; the final docking approach is RCS monoprop, not
main-engine dv. Feasible with wide margin. **LIVE-PROVE P1:** fly it and read the
post-circularize stage dv on the Interceptor; if a bad phasing draw needs many
phasing orbits the dv can climb, but the craft's reserve is large.

**Monoprop budget (the docking-specific constraint).** The docking autopilot and
the final approach run on the 4x `rcsTankRadialLong` MonoPropellant load. A single
LKO hard-dock from ~100 m at the default MechJeb approach speed is a few units of
monoprop; the craft carries far more. The REAL monoprop risk is a docking-AP stall
that thrashes RCS for minutes (section 6 give-ups cap it). **LIVE-PROVE P2:**
read `monopropellant` remaining after the dock; if a stall pattern drains it,
lower `docking_autopilot.speed_limit` or add an RCS-out give-up escalation.

**Session length.** Ascent x2 (~250 s each) + circularize x2 + rendezvous
(phasing orbits under rails warp, ~1-3 orbits) + docking approach (~60-180 s at
1x, no warp) + two transfers (kRPC transfers run server-side over a few seconds
each) + undock + settle. WALL estimate ~1600-2400 s (section 6 budgets, ESTIMATED).
This is the longest B-mission but shorter than B5/B7 because there is no
interplanetary coast.

---

## 2. The fixture (harvested on this branch)

### 2.1 The model decision: vessel-less sandbox + launch_vessel x2 (RECOMMENDED)

B1 and B2 pre-place their vessel IN the fixture save (the operator flew the craft
onto the pad and quicksaved), so `LoadGame` drops straight into flight with the
craft ready. That model does not extend to a TWO-vessel mission: pre-placing both
a Station in orbit AND an Interceptor on the pad would need an operator
fixture-building session that flies the Station up, parks it, then builds the pad
craft - exactly the manual step the harness exists to remove, and a bespoke
save that no other mission reuses.

**Chosen model: a vessel-LESS sandbox save with the craft in `Ships/VAB`, and
BOTH launches driven by kRPC `SpaceCenter.launch_vessel` from inside the mission.**
One uniform launch mechanism, no pre-placed vessel, no operator fixture flight.
`launch_vessel("VAB", "Kerbal X", "LaunchPad")` (kRPC v0.5.4 signature, section
4.2) recovers anything on the pad and starts a fresh launch of the craft; calling
it twice (Station, then Interceptor after the Station is parked and committed)
produces two independent launches of the same `.craft` - which is exactly the
same-baked-pid identity test we want.

This is harvested as the committed fixture `harness/fixtures/saves/bdock-station-craft`
(section 2.3).

### 2.2 The fallback model (pre-placed Station + launch_vessel Interceptor)

If constructing a vessel-less save that boots cleanly proves too risky in
practice (LIVE-PROVE P3, section 9), fall back to the B2 shape for piece 1 only:
an operator pre-places a Station in a ~110 km circular orbit IN the fixture save
(one operator flight, reusable across every future dock mission), and the mission
launches ONLY the Interceptor via `launch_vessel`. This keeps piece 2 uniform and
removes the launch_vessel-into-orbit uncertainty for the passive vessel, at the
cost of one operator fixture flight and losing the "both launches identical
mechanism" property. Piece 1's ascent-recording coverage (D1 `auto-record-launch`
for the Station) is also lost in this fallback because the Station is not launched
in-session.

**RECOMMENDATION: ship the vessel-less model (2.1); keep 2.2 documented as the
tested fallback.** The vessel-less save is a legitimate KSP state (a fresh sandbox
game that has never launched anything is exactly this), it needs no operator
flight, and it maximizes coverage (both launches auto-record). The single risk is
headless-boot validity, which is a named live-prove item, not a design blocker.

### 2.3 What was harvested

`harness/fixtures/saves/bdock-station-craft/` was constructed on this branch from
the committed `gloops-airshow` skeleton (chosen because it already carries a
`Ships/VAB` directory and the full stock-sandbox scenario scaffolding), by:

- Removing both `VESSEL` nodes from `FLIGHTSTATE` (the stock asteroid and the
  gloops mk1-capsule), leaving an empty flight state.
- Setting `activeVessel = -1` (no active vessel).
- Freeing the one `state = Assigned` roster kerbal to `Available` (the pilot the
  removed capsule had crewed).
- Retitling `GAME.Title = bdock-station-craft (SANDBOX)`.
- Dropping the harvested craft into `Ships/VAB/Kerbal X.craft` (copied verbatim
  from the located clean candidate, 86 parts, verified 100% stock: every part
  name and every `ModuleXxx` in the file is a stock Squad module - the module
  scan returned only stock modules `ModuleAblator / ModuleCargoPart /
  ModuleControlSurface / ModuleInventoryPart / ModuleKerbNetAccess /
  ModuleTripLogger` alongside the obvious command / engine / RCS / docking /
  decouple / science modules).
- Pruning the stale `quicksave.sfs` / `quicksave.loadmeta` (the mission
  `LoadGame`s `persistent`) and setting `persistent.loadmeta vesselCount = 0`.

Final fixture tree: `AddOns/ Ships/VAB/Kerbal X.craft persistent.loadmeta
persistent.sfs`. It VALIDATES as ConfigNode-shaped text: brace balance returns to
depth 0, a recursive node parse yields the single top-level `GAME` node, zero
`VESSEL` tokens remain, `activeVessel = -1`, and no `state = Assigned` crew
remains. The craft's baked `persistentId = 3620499050` is intact (the identity
test depends on it).

**Live-boot validation is LIVE-PROVE P3** (section 9): a headless-constructed
vessel-less save is parse-valid but has not been booted into KSP. The first live
run of B-DOCK is the boot proof; if KSP rejects the stripped flight state, fall
back to model 2.2.

The craft is ALSO the fixture's payload verbatim; there is no separate
`harness/fixtures/` craft copy outside the save, matching the `gloops-airshow`
layout (the craft lives in the save's `Ships/VAB`, not a sibling directory).

---

## 3. Mission architecture

### 3.1 One two-piece scenario, not two

**Decision: ONE scenario `B-DOCK-1` running ONE mission module
`bdock_dock_transfer`, flying both pieces in one session.** Splitting into a
"launch the station" scenario and a "dock to a pre-existing station" scenario
would need the station-scenario to leave a committed-orbit fixture for the
dock-scenario to consume - reintroducing the operator-built cross-scenario
fixture the vessel-less model exists to avoid, and losing the same-session,
same-pid identity test (the two vessels must be alive at once for the guid
discriminator to matter). The dock / transfer / undock cycle is only meaningful
with both vessels co-present, so it is one mission.

### 3.2 The Station-tree handling decision (commit before the Interceptor launch)

When the Interceptor launches, `launch_vessel` calls
`FlightDriver.StartWithNewLaunch`, a FLIGHT -> FLIGHT scene reload. Two questions:
what happens to the Station's active recording tree across that reload, and what
does the later cross-tree dock need the Station to be?

**Decision: COMMIT the Station tree before launching the Interceptor.** Rationale
from the two contracts:

- **Cross-tree foreign dock (M-MIS-8).** Two INDEPENDENT launches are two
  INDEPENDENT trees. When the Interceptor (the controller, tree TA) docks to the
  Station, the recorder authors a SINGLE-parent `BranchPointType.Dock` in TA whose
  `TargetVesselPersistentId` is the Station's pid, and the Station's own pre-dock
  flight is a SEPARATE committed tree TB
  (`design-mission-crosstree-dock.md` section 1). The cross-tree link is derived
  at read time from TA's Dock branch pointing at a recording pid that lives in
  TB, guid-gated. For TB to exist and be walkable, the Station flight must be a
  committed recording, not a discarded or half-open one.
- **Route-candidate classification (D10).** The route analyzer detects the
  candidate on the TRANSPORT's tree (TA), but the route window's endpoint
  identity (`EndpointPartPersistentIds`, `DockEndpointResources`) resolves the
  Station's pre-dock parts through the pre-couple partner snapshot or the
  partner's committed `VesselSnapshot` (dock-undock doc section 6.1). A committed
  Station recording (a `docked-depot-origin` endpoint, D10) is what makes the
  endpoint side resolvable.

**Why NOT keep the Station as a background recording under TA.** BG recording is
for vessels that are physically a PART of the active flight's story - staging
debris, controlled-decoupled children, nearby vessels in physics range
(`bg-recording`, D5). The Station is an independent launch, not a child of the
Interceptor. Folding it into TA as a BG child would mismodel two independent
physical objects as one tree and would not produce the cross-tree Dock link the
whole logistics pipeline is built to read. (The Station IS legitimately
BG-recorded for the brief docked window once the Interceptor brings it into
physics range - that BG coverage lands in TA's merged child, which is correct and
separate from TB.)

**Plumbing (implementation-PR concern, flagged).** The mid-mission commit of TB
must happen at the Station -> Interceptor handoff, INSIDE the mission phase. Two
routes, to be chosen in the implementation PR:
1. A new mission action `ACTION_PARSEK_COMMIT_TREE` that the runner maps to the
   Parsek command-seam `CommitTree` verb (the M-A2 command seam, armed via
   `PARSEK_TEST_COMMANDS`), emitted by the machine at the handoff. Keeps the whole
   two-piece in one `phase = "mission"` step. RECOMMENDED.
2. Rely on Parsek auto-committing the active tree on the `launch_vessel` scene
   reload. The `MapFocusObjectOnSelectPatch` Case B note in `.claude/CLAUDE.md`
   shows a far-vessel Switch-To scene reload BYPASSES the SceneExit FLIGHT ->
   FLIGHT filter, so the auto-behavior on a `StartWithNewLaunch` reload is
   UNKNOWN. **LIVE-PROVE P4:** observe what the Station tree becomes across the
   Interceptor launch; do not depend on an unproven auto-commit. Route 1 is the
   fail-safe (explicit commit) and is the design's recommendation.

### 3.3 Phase flow (both pieces)

```
                        --- PIECE 1: STATION ---
PRELAUNCH
  (mission opens on the vessel-less save at the space center; no active vessel.)

PRELAUNCH -> STATION-LAUNCH
  (ACTION_LAUNCH_VESSEL "Kerbal X". autoRecordOnLaunch is on -> Parsek starts a
   recording on the fresh Station. reachedVessel evidence: an active vessel with
   the craft's part count appears.)

STATION-LAUNCH -> STATION-ASCENT -> STATION-CIRCULARIZE -> STATION-ORBIT
  (the B2-proven MJ ascent machine to the ~110 km park. reachedOrbit evidence:
   apo/peri within window, ecc < max.)

STATION-ORBIT -> STATION-COMMIT
  (one-frame: ACTION_PARSEK_COMMIT_TREE. TB is committed. Record the Station's
   pid + guid + docking-port part id for the later target + endpoint identity.)

                     --- PIECE 2: INTERCEPTOR ---
STATION-COMMIT -> INT-LAUNCH
  (ACTION_LAUNCH_VESSEL "Kerbal X" again. Scene reload; Station goes on-rails.
   A FRESH recording starts on the Interceptor -> a NEW tree TA. The Interceptor
   bakes the SAME pid 3620499050; KSP regenerates it ONLY on a live collision -
   whether it does is LIVE-PROVE P5. Capture the Interceptor's guid: it is FRESH
   per launch and is the discriminator.)

INT-LAUNCH -> INT-ASCENT -> INT-CIRCULARIZE -> INT-PHASING-ORBIT
  (MJ ascent to the ~90 km phasing park, BELOW the Station so it phases faster.)

INT-PHASING-ORBIT -> SET-TARGET
  (one-frame: ACTION_SET_TARGET_VESSEL = the Station. The rendezvous AP and the
   MechJeb TargetController now read this target. reachedTarget precondition:
   target_controller.normal_target_exists.)

SET-TARGET -> RENDEZVOUS
  (ACTION_MJ_ENABLE_RENDEZVOUS with desired_distance = approachDistanceMeters and
   max_phasing_orbits capped. The AP raises/matches the orbit and closes.
   RENDEZVOUS warps its phasing legs on rails. Done evidence: target_distance <=
   approachDistanceMeters AND rendezvous_autopilot idle/finished status.)

RENDEZVOUS -> MATCH-VELOCITY
  (ACTION_MJ_KILL_REL_VEL or the rendezvous AP's own terminal match. Done
   evidence: target_relative_speed <= matchSpeedMetersPerSec.)

MATCH-VELOCITY -> DOCK
  (ACTION_SET_TARGET_DOCKING_PORT = the Station's top Clamp-O-Tron, then
   ACTION_MJ_ENABLE_DOCKING with speed_limit = dockSpeedMetersPerSec. The docking
   AP closes on RCS monoprop and hard docks. Done evidence: the docking port
   state == Docked. On dock, KSP fires onPartCouple -> Parsek authors the
   cross-tree Dock branch in TA + opens the RouteConnectionWindow.)

DOCK -> TRANSFER
  (two commanded transfers "various ways":
     T1: ACTION_START_RESOURCE_TRANSFER LiquidFuel, transport tank -> station
         tank, amount = transferAmountLf. Poll until complete.
     T2: ACTION_START_RESOURCE_TRANSFER MonoPropellant, station tank -> transport
         tank, amount = transferAmountMp. Poll until complete.
   Done evidence: both transfers report complete with transferred amount within
   tolerance of commanded.)

TRANSFER -> UNDOCK
  (ACTION_UNDOCK the Clamp-O-Tron. KSP fires onPartUndock then, authoritatively,
   onVesselsUndocking(oldVessel, newVessel) -> Parsek authors the Undock split
   branch in TA and completes the RouteConnectionWindow. Done evidence: the port
   returns to Ready AND two distinct vessels exist.)

UNDOCK -> TERMINAL
  (ACTION_CANCEL_WARP + settle. The post-mission CommitTree seam step commits TA.
   End state: two separate vessels in Kerbin orbit, TB (Station) + TA (Interceptor
   with the Dock/Undock cross-tree link) both committed.)
```

Survival is the contract: any vessel-lost / frozen terminal in ANY phase is an
ASSERT-FAIL loss (the B-family invariant, unchanged). A rendezvous / docking
stall is a bounded give-up FLAKE (section 6), not a loss.

---

## 4. Ground truths (verified vs assumed)

### 4.1 GROUND TRUTH 1 - KRPC.MechJeb v0.8.1 rendezvous + docking exposure

**VERIFIED** against the pinned `darchambault/KRPC.MechJeb` v0.8.1 source
(fetched: `RendezvousAutopilot.cs`, `DockingAutopilot.cs`, `MechJeb.cs`) AND
cross-checked against the umbrella genhis 0.7.1 clone. Both autopilots exist and
are exposed; v0.8.1's surface for these two classes is byte-identical to 0.7.1.

`mechjeb.rendezvous_autopilot` (`MuMech.MechJebModuleRendezvousAutopilot`):
- `enabled` (bool, get/set) - inherited from `KRPCComputerModule`; **this is the
  start/stop**. Set True to run the rendezvous.
- `desired_distance` (double) - the terminal approach distance the AP closes to.
- `max_phasing_orbits` (double) - cap on phasing orbits (bounds the worst-case
  wait; see P6).
- `status` (string) - human status; a give-up diagnosability channel.

`mechjeb.docking_autopilot` (`MuMech.MechJebModuleDockingAutopilot`):
- `enabled` (bool, get/set) - the start/stop.
- `status` (string).
- `speed_limit` (double) - approach speed cap (the monoprop-budget knob, P2).
- `force_roll` (bool) + `roll` (double) - port roll alignment.
- `override_safe_distance` (bool) + `overriden_safe_distance` (double).
- `override_target_size` (bool) + `overriden_target_size` (double).
- `safe_distance` (float, read-only) + `target_size` (float, read-only).

`mechjeb.target_controller` (`MuMech.MechJebModuleTargetController`) - the docking
/ rendezvous TELEMETRY source (VERIFIED, TargetController.cs):
- `normal_target_exists` (bool) - a vessel/port target is set.
- `distance` (float, metres to target) - the closest-approach / range channel.
- `relative_velocity` (Tuple3) - relative velocity vector; its magnitude is the
  MATCH-VELOCITY evidence.
- `relative_position` (Tuple3), `docking_axis` (Tuple3), `can_align` (bool),
  `target_orbit` (Orbit).

The MechJeb `TargetController` mirrors KSP's own `FlightGlobals.VesselTarget`, so
setting the target via the kRPC SpaceCenter setters (section 4.2) drives both the
game target and MechJeb's rendezvous / docking APs.

**Discriminator caveat (why the cross-check matters):** the runner's live-flown
`operation_transfer` uses `op.capture` / `op.plan_capture` / `op.rendezvous`,
which do NOT exist in genhis 0.7.1's `OperationTransfer.cs` (that had only
`InterceptOnly` / `PeriodOffset` / `SimpleTransfer`). So v0.8.1 DID diverge from
0.7.1 for the maneuver operations - which is why the two autopilot classes were
re-verified directly against the v0.8.1 source and not assumed from the umbrella
clone. For the two autopilot classes specifically, v0.8.1 == 0.7.1.

**The planner-composed fallback is available but NOT needed.** Because the
rendezvous AP IS exposed, the primary path is `rendezvous_autopilot.enabled`. Had
it been absent, the fallback (B7 finding-3) is `operation_transfer` with
`rendezvous = True` (the targeted-intercept flag, VERIFIED present) for the
phasing intercept plus `operation_kill_rel_vel` (VERIFIED exposed on the
ManeuverPlanner: `mechjeb.maneuver_planner.operation_kill_rel_vel`) for the
match-velocity node, then course corrections. This fallback stays documented as
the P6-hardening path if the rendezvous AP proves unreliable on this 20+ t upper
stage in flight.

### 4.2 GROUND TRUTH 2 - kRPC v0.5.4 SpaceCenter surface

**VERIFIED** against `mods/krpc` at tag `v0.5.4` (`git show v0.5.4:...`).

- `SpaceCenter.launch_vessel(craft_directory, name, launch_site, recover=True)` -
  the v0.5.4 signature is 4-arg (NO crew list, NO flag URL; those were added
  after 0.5.4). It closes dialogs, runs pre-flight checks, recovers any vessel
  already on `launch_site` when `recover=True`, then `FlightDriver
  .StartWithNewLaunch(...)` - a scene reload into flight on the new craft.
  `launch_vessel_from_vab(name, recover=True)` is the "VAB" / "LaunchPad" shortcut.
  Craft path resolution is `<save>/Ships/<craft_directory>/<name>.craft`, so
  `name = "Kerbal X"` resolves the harvested fixture craft. The launch focuses
  the new vessel as active. NOTE: at v0.5.4 there is no crew argument, so the
  `mk1-3pod` launches with its default manifest (KSP fills the pod per the game's
  crew-assignment rules); crew is not a mission gate.
- `SpaceCenter.target_vessel` (settable Vessel) and `SpaceCenter.target_docking_port`
  (settable DockingPort) - both present at v0.5.4. These set the game target that
  the rendezvous / docking APs read.
- `ResourceTransfer.start(from_part, to_part, resource, max_amount)` returns a
  `ResourceTransfer` with `complete` (bool), `amount` (float, transferred so far),
  `total_amount` (float). The server ticks the transfer each fixed update; the
  client polls `complete` / `amount`. It moves at most `max_amount`, bounded by
  source availability and destination free space. Part handles come from
  `vessel.parts` filtered by resource.
- `DockingPort.state` -> `DockingPortState` enum `{Ready, Docked, Docking,
  Undocking, Shielded, Moving}`. `DockingPort.undock()` returns the new `Vessel`
  created by the split (throws if not `Docked`; polls internally until the split
  completes). `DockingPort.docked_part`, `DockingPort.reengage_distance`.

**No closest-approach RPC is needed.** kRPC v0.5.4 has no dedicated
closest-approach procedure; the rendezvous telemetry comes from MechJeb's
`target_controller.distance` / `relative_velocity` (section 4.1), which is the
range + relative-speed the machine gates on. The existing `next_pe` / `next_body`
snapshot fields (patched-conic arrival) are NOT the docking channel and are not
reused for rendezvous.

### 4.3 GROUND TRUTH 3 - Parsek recording contracts B-DOCK exercises

**VERIFIED** against `docs/dev/dock-undock-recording-structure.md`,
`design-mission-crosstree-dock.md`, and the `.claude/CLAUDE.md` identity section.

- **Dock = merge branch.** `onPartCouple` -> Parsek captures the pre-couple
  partner snapshot (endpoint's pre-dock resources), appends a Dock structural-
  event snapshot, closes the parent(s) at `ExplicitEndUT = dockUT` with
  `TerminalState.Docked`, and next frame `CreateMergeBranch` builds a
  `BranchPointType.Dock` (persisted `type = 2`, `mergeCause = DOCK`,
  `targetVesselPid = <partner pid>`) whose single child is the merged-vessel
  recording carrying the open `RouteConnectionWindow`. In the cross-tree case
  (independent Station tree) the branch point has ONE `ParentRecordingIds` entry
  (the transport side) and `TargetVesselPersistentId` = the Station pid.
- **Undock = split branch, driven by `onVesselsUndocking`.** `onPartUndock` fires
  FIRST (once, transient pre-reparent pid) and only captures the Undock structural
  snapshot + origin seed; `onVesselsUndocking(oldVessel, newVessel)` fires LAST,
  unconditionally, with FINAL pids, and drives `DeferredUndockBranch ->
  CreateSplitBranch(BranchPointType.Undock, ...)` (persisted `type = 3`,
  `splitCause = UNDOCK`) plus `TryCompleteLatestRouteConnectionWindow`. Do NOT
  gate the oracle on a second `onPartUndock`.
- **RouteConnectionWindow.** Opened at dock (when `routeTargetVesselPid != 0`)
  with `DockTransportResources` / `DockEndpointResources` frozen; completed at
  undock with `UndockTransportResources` / `UndockEndpointResources`. Net cargo =
  `Undock* - Dock*` per side, which by conservation sum to zero per resource. This
  is the recorded-delta the oracle checks against the commanded transfers.
- **Craft-baked pid + guid discriminator.** `persistentId` is baked in the
  `.craft` (3620499050 here) and reused verbatim on every launch; KSP regenerates
  it ONLY on collision with a currently-live vessel. Two launches of this craft
  therefore bake the SAME pid, and the launch-unique discriminator is KSP's
  `Vessel.id` guid (`Recording.RecordedVesselGuid`, compared via
  `VesselLaunchIdentity.LiveVesselIsRecordedLaunch` / `RecordingsShareLaunch`).
  The cross-tree Dock link is guid-gated (`GuidsConclusivelyDiffer`). B-DOCK is a
  live exercise of exactly this path: if KSP does NOT regenerate the Interceptor
  pid at launch (P5), the two live vessels share pid 3620499050 and ONLY the guid
  separates them - so a correct cross-tree link + a correct target resolution is a
  strong proof the discriminator works. If KSP DOES regenerate, the mission still
  covers the guid path via the recording-vs-recording comparison.
- **ResourceManifest deltas.** `VesselSpawner.ExtractResourceManifest` +
  `ResourceManifest.ComputeResourceDelta` produce the per-resource deltas the
  route window records; the oracle reads `DOCK_*_RESOURCES` / `UNDOCK_*_RESOURCES`
  from the persisted window.

---

## 5. Machine placement: a NEW mlib module, reusing shared infrastructure

**Decision: a dedicated `bdock_dock_transfer` mission with a NEW machine
(`bdock_decide` / `BDockParams` / `BDockState` / a `BDOCK_*` phase enum) in
`mlib.py`, NOT an extension of the B5 machine.** Argue it:

- The rendezvous / dock / transfer / undock phases are a DIFFERENT SHAPE from the
  B5 ascend -> transfer-burn -> coast -> flyby single-vessel flow. B5's phase
  transitions are keyed on SOI body changes, apoapsis / eccentricity floors, and
  time-to-SOI; B-DOCK's are keyed on target distance, relative speed, docking-port
  state, transfer completion, and the two-vessel launch sequence. Grafting five
  more params + a second vessel + a target + transfers onto `b5_decide` would
  bloat the single most-tested decision function with a mostly-disjoint branch set
  and risk the B5/B6/B7 byte-identical guarantee.
- What IS shared is the ASCENT + orbit machinery (MJ ascent to a park, the
  circularize node, the orbit-window evidence - literally B2), plus the
  cross-cutting infrastructure: the connect-retry, the warp watchdog + frozen
  detector, the give-up / flake budget pattern, the `TelemetrySnapshot`, the
  deterministic mission-result JSON, and the `resolve_flight_verdict` /
  assertion-evaluator seam. B-DOCK reuses ALL of that via `mission_runner` and the
  shared mlib helpers; only the phase machine is new.

Concretely: `bdock_decide` reuses the same ascent ACTIONS the B2/B5 machines emit
(`ACTION_MJ_SET_TARGET_APOAPSIS`, `ACTION_MJ_ENABLE_AUTOSTAGE`,
`ACTION_MJ_ENGAGE_ASCENT`, `ACTION_MJ_EXECUTE_CIRCULARIZATION`) for each of the
two ascent legs, and adds the new docking ACTIONS below. The ascent-evidence
helpers can be lifted to shared `internal` helpers if that keeps the two ascent
legs from duplicating B2's transition logic.

### 5.1 New ACTIONs (runner spec; the runner is owned by another agent)

| Action | Runner mapping (kRPC v0.5.4 / MechJeb v0.8.1) |
|---|---|
| `ACTION_LAUNCH_VESSEL` (text = craft name) | `sc.launch_vessel("VAB", name, "LaunchPad")`; wait for the new active vessel to settle. |
| `ACTION_PARSEK_COMMIT_TREE` | Parsek command-seam `CommitTree` (route 1, section 3.2). |
| `ACTION_SET_TARGET_VESSEL` (value = pid or a captured handle) | `sc.target_vessel = <station vessel>`. |
| `ACTION_SET_TARGET_DOCKING_PORT` (value = port part id) | `sc.target_docking_port = <station clamp-o-tron>`. |
| `ACTION_MJ_ENABLE_RENDEZVOUS` (value = desired distance, limit = max phasing orbits) | set `rendezvous_autopilot.desired_distance` / `.max_phasing_orbits`, then `.enabled = True`. |
| `ACTION_MJ_KILL_REL_VEL` | `maneuver_planner.operation_kill_rel_vel.make_nodes()` + execute; OR rely on the rendezvous AP terminal match. |
| `ACTION_MJ_ENABLE_DOCKING` (value = speed limit) | set `docking_autopilot.speed_limit`, then `.enabled = True`. |
| `ACTION_MJ_DISABLE_DOCKING` | `docking_autopilot.enabled = False` (give-up cleanup). |
| `ACTION_START_RESOURCE_TRANSFER` (text = resource, value = amount, plus from/to part handles) | `ResourceTransfer.start(from_part, to_part, resource, amount)`; the runner polls `.complete`. |
| `ACTION_UNDOCK` (value = port part id) | `docking_port.undock()`. |

### 5.2 New TelemetrySnapshot fields (runner-populated, fail-closed defaults)

- `target_distance: float = nan` - `mj.target_controller.distance`; NaN when no
  target / unreadable. RENDEZVOUS-done gate reads it; NaN never satisfies.
- `target_rel_speed: float = nan` - `norm(mj.target_controller.relative_velocity)`;
  NaN fails the MATCH-VELOCITY gate closed.
- `docking_state: str = ""` - the active/target docking port `state.name` (`Ready`
  / `Docking` / `Docked` / ...); "" matches no gate.
- `target_set: bool = False` - `mj.target_controller.normal_target_exists`.
- `vessel_count: int = 0` - `len(sc.vessels)`; the UNDOCK split gate reads it
  (a split raises the count).
- `transfer_complete: bool = False` + `transfer_amount: float = nan` - the active
  `ResourceTransfer` poll (the runner owns the handle; the machine reads the
  reduced booleans).
- `monopropellant: float = nan` - vessel-total MonoPropellant (the P2
  diagnosability channel; a stall drains it).

Every new field defaults to a fail-closed sentinel (`nan` / `""` / `False` / `0`)
so a runner that forgets to populate one fails the gate rather than faking a
satisfied condition (the B4 `ap_error` / B7 `body` fail-closed rationale).

### 5.3 Give-ups and watchdogs (docking has NEW stall classes)

Each phase carries a GAME-time budget and a wall watchdog; expiry FLAKES the
mission (driver-INVALID, retryable), never PARSEK-FAILs. New docking stall classes:

- **Rendezvous stall.** `max_phasing_orbits` bounds the AP itself; the machine
  adds a `rendezvousTimeoutSeconds` game-budget give-up and a "distance not
  shrinking over N polls" no-progress detector (target_distance monotone-stuck ->
  flake). The rendezvous-AP `status` string is logged for diagnosis.
- **Approach stall / RCS-out-of-monoprop.** During DOCK, if `docking_state` stays
  `Docking` past `dockTimeoutSeconds`, OR `monopropellant` hits ~0 while not yet
  `Docked`, disable the docking AP (`ACTION_MJ_DISABLE_DOCKING`) and FLAKE
  (loss_reason names monoprop-out vs approach-stall from the monoprop reading).
- **Port misalignment / bounce.** If `docking_state` oscillates `Docking` <->
  `Ready` past a bounded retry count (the AP bouncing off), flake with a
  misalignment reason (candidate hardening: force-roll on).
- **Transfer stall.** If `transfer_complete` stays False past
  `transferTimeoutSeconds` with `transfer_amount` not advancing, flake (a stuck
  server-side transfer - dry source or full destination).
- **Frozen / vessel-lost.** The shared frozen detector + `vessel_lost` terminal
  apply in every phase (survival contract). Note the docking approach runs at 1x
  (no warp), so the frozen signature is genuinely frame-varying and does not
  false-trip; the RENDEZVOUS phasing legs warp on rails like B5's coast.

### 5.4 Budgets (all ESTIMATED, flagged)

GAME-time phase budgets: ascent 1200 / circularize 600 each leg (the ~110 / ~90 km
parks); `rendezvousTimeoutSeconds = 30000` (phasing orbits under warp advance game
time fast); `dockTimeoutSeconds = 600` (the 1x approach); `transferTimeoutSeconds =
120` each; undock/settle 120. WALL budgets: mission phase ~2400 s (two ascents
~250 s each + two circularizes + rendezvous ~200 s under warp + docking ~120-180 s
at 1x + two transfers ~10 s + undock/settle + margin); runtime ~3000 s (240
LoadGame + 2400 mission + CommitTree x2 + FlushAndQuit + margin, following the
B5 "post-mission seam steps must survive a full-budget mission" review lesson).
Retry policy: once (first flights are tuning flights). Every number here is
arithmetic, not measured - re-time against the first live run (P1, P2, P6).

---

## 6. Recording-correctness oracle (what proves Parsek recorded it right)

The mission flying (MISSION-OK) is a driver-validity result, ORTHOGONAL to Parsek
correctness. The oracle below is what reds a mis-recorded good flight. Each
invariant names its observable artifact and whether it is observable TODAY.

1. **Two trees with correct topology.** After commit there must be a committed
   Station tree TB (single ascent recording, terminal not-Docked - it was
   committed in orbit) and a committed Interceptor tree TA (ascent -> Dock merge
   child -> Undock split children). OBSERVABLE via the analyzer over the produced
   recordings + the `[expectations.recordings]` count window and the persisted
   `RECORDING_TREE` / `BRANCH_POINT` shape.
2. **Cross-tree Dock link (M-MIS-8 shape).** TA carries a `BranchPointType.Dock`
   (`type = 2`) with `TargetVesselPersistentId` = the Station pid, single parent
   (the transport), single merged child. OBSERVABLE via the log contract `Tree
   branch created: type=Dock` + the persisted branch point's `targetVesselPid`.
   The cross-tree-link DERIVATION (TA's Dock pid matching a TB recording pid,
   guid-gated) is read-time logic; whether the analyzer today ASSERTS the link is
   resolvable is a candidate analyzer-growth item (NOT observable, see below).
3. **BG-recording integrity for the passive Station.** During the docked window
   the Station is BG-recorded into TA's merged child; its trajectory must be
   continuous with no BG on-rails gaps mis-modeled as env TrackSections
   (`bg-on-rails` emits no env-classified sections, per the `.claude/CLAUDE.md`
   BG note). OBSERVABLE via the analyzer's BG-continuity rules over the merged
   child.
4. **Authoritative undock split.** TA carries a `BranchPointType.Undock` (`type =
   3`) authored by `onVesselsUndocking`, with two post-split children each carrying
   continuous coverage. OBSERVABLE via the log contract `OnVesselsUndocking:entry
   recordedPid=... oldPid=... newPid=...` + `Tree branch created: type=Undock`,
   and the persisted two-child split shape. The oracle must NOT require a second
   `onPartUndock` (contract invariant 2).
5. **Recorded resource deltas match the commanded transfers.** The completed
   `RouteConnectionWindow` on TA's merged child must satisfy `UndockTransportResources
   - DockTransportResources` = the commanded LF/MP net (within a per-resource
   tolerance), and the endpoint side the conservation-mirror. OBSERVABLE via the
   persisted `ROUTE_CONNECTION_WINDOWS` block (`DOCK_*_RESOURCES` /
   `UNDOCK_*_RESOURCES`) + a new spec `[expectations.route]` block asserting the
   window is complete and the deltas fall in a window. **This expectations block
   does not exist yet** (implementation-PR item): the closest existing surface is
   the log contracts; a route-window expectations verifier is a harness growth
   item.
6. **Pid / guid identity (same craft twice).** The two committed trees carry the
   SAME baked pid but DIFFERENT `RecordedVesselGuid`s, and the cross-tree link
   resolves by guid, not by the colliding pid. OBSERVABLE via the persisted
   recordings' `RecordedVesselGuid` fields + a `VesselLaunchIdentity` log line if
   one is emitted at the dock-link claim (candidate log-contract growth - confirm
   `GhostChainWalker.ScanBranchPointClaims` logs the guid gate; if it does not,
   add a Verbose line - implementation-PR item).
7. **Route-candidate classification of the resulting pair.** After commit, the
   route analyzer should detect the Interceptor->Station pair as a
   `docked-depot-origin` delivery + pickup candidate (D10). OBSERVABLE today via
   the `RouteAnalysisEngine` "Create Supply Route?" dialog path / its log lines
   IF the mission drives the flight to the analyzer trigger; whether the harness
   can assert the candidate WITHOUT a UI interaction is a candidate-registry /
   analyzer growth item (NOT observable headlessly today).

**NOT observable today (analyzer / registry growth for the implementation PR):**
a headless assertion of the cross-tree link resolvability (2), a route-window
resource-delta expectations verifier (5), a guid-gate log contract (6), and a
headless route-candidate classification assertion (7). Each is a named growth item
the implementation PR files against the analyzer / harness, not a blocker for the
first flown proof (which relies on the log contracts + the persisted `.sfs` shape
+ the analyzer's existing rules).

---

## 7. Coverage cells

`dimensionsCovered` cites ONLY existing `harness/coverage/registry.toml` values:

- `D1 = ["auto-record-launch"]` - both launches auto-record (the vessel-less
  model's coverage win; the fallback loses the Station's cell).
- `D3 = ["orbital-checkpoint"]` - both orbits.
- `D4 = ["atmospheric", "exo-propulsive"]` - the two ascents.
- `D5 = ["cross-tree-foreign-dock", "undock-split", "bg-recording"]` - the two
  independent trees docking, the authoritative split, and the passive-vessel BG
  coverage.
- `D7 = ["dock-undock", "rcs"]` - the docking-port couple/undock part events and
  the RCS approach.
- `D8 = ["route"]` - the route ledger module engaged by the completed window.
- `D10 = ["candidate-detection", "docked-depot-origin", "delivery", "pickup",
  "mixed-direction", "resource-cargo"]` - the route candidate, the docked depot
  endpoint, and the two-direction resource transfer (LF one way = delivery, MP the
  other = pickup / mixed-direction resource cargo).
- `D14 = ["kerbin", "warp-rails", "scene-flight", "sandbox"]` - the body, the
  phasing-orbit rails warp, the flight scene, the sandbox mode.

**Genuine gaps (noted, NOT citable - the implementation PR grows the registry in
the SAME PR per rule N9):**
- D5 has `dock-merge-same-tree` and `cross-tree-foreign-dock`, but no value for
  "docking-port dock" as distinct from claw/board; the dock KIND is uncited.
- D10 has no "rendezvous" or "orbital-dock" value; the whole ORBITAL rendezvous +
  hard-dock flow (as opposed to a landed rover dock) is uncited beyond the generic
  cells - a candidate `D10` growth value (e.g. `orbital-rendezvous-dock`).
- D14 has no value for the same-craft-twice pid-collision identity axis; if a
  D18 (ghost chains / identity) value is added for the guid-discriminator live
  path, cite it there.

---

## 8. Risks and live-prove list (P-numbered)

- **P1 - Rendezvous / dock stage-dv margin.** ESTIMATED ~1500 m/s reserve minus
  ~100-250 m/s rendezvous is ample, but a bad phasing draw can climb. Fly it, read
  post-circularize + post-dock stage dv. Do not tune speculatively.
- **P2 - Monoprop budget on the docking AP.** The 4x `rcsTankRadialLong` load is
  large, but a docking-AP stall thrashes RCS. Read `monopropellant` post-dock; the
  RCS-out give-up (5.3) caps the drain. Lower `speed_limit` if a stall drains it.
- **P3 - Vessel-less fixture boots headlessly.** The constructed
  `bdock-station-craft` is parse-valid but UNBOOTED. First live run is the boot
  proof; on rejection, fall back to model 2.2 (pre-placed Station).
- **P4 - Station-tree survival across the Interceptor launch_vessel scene
  reload.** `StartWithNewLaunch` is a FLIGHT -> FLIGHT reload; the SceneExit
  filter behavior is unknown (the far-Switch-To analog bypasses it). Commit TB
  EXPLICITLY (route 1, 3.2) rather than trusting an auto-commit; live-observe what
  the tree becomes.
- **P5 - Same-craft pid collision handling.** Both launches bake pid 3620499050.
  Does KSP regenerate the Interceptor pid when the Station is live on-rails far
  away, or leave the collision for the guid to resolve? Read both live vessels'
  pids at Interceptor launch and the recordings' guids. Either outcome is a valid
  identity test; the design must not ASSUME regeneration.
- **P6 - Rendezvous AP robustness on the 20+ t upper stage.** MechJeb's rendezvous
  AP can be finicky with a heavy, high-drag-profile stack. If it fails to converge
  within `max_phasing_orbits`, harden via the planner-composed fallback (4.1:
  `operation_transfer rendezvous=True` + `operation_kill_rel_vel` + a
  `operation_course_correction` refine).
- **P7 - launch_vessel triggers Parsek auto-record.** The mission depends on
  `autoRecordOnLaunch` starting a recording on each `launch_vessel` launch (not
  just an editor-flow launch). Confirm the first live run auto-records BOTH
  launches; if `launch_vessel`'s `StartWithNewLaunch` path misses the
  auto-record hook, the mission must issue an explicit `StartRecording` command.
- **P8 - Session length under the wall budget.** The ~2400 s mission estimate is
  arithmetic; a slow rendezvous convergence or a docking retry can blow it. Size
  the retry-once policy and re-time against the first run.
- **P9 - target_docking_port selection.** Post-merge, selecting the CORRECT
  Station Clamp-O-Tron (not the Interceptor's own) for the undock, and the correct
  transport / station tanks for the transfers, depends on part-set bookkeeping
  captured pre-dock. Confirm the captured part handles survive the merge.

---

## 9. Follow-on: the RouteCommand seam verb (out of scope)

The M-A2 command-seam design reserves a `RouteCommand` verb
(`design-autotest-seam-verbs-c1.md`: "logistics route dock/transfer/undock
scripting (D10)") that is declared but UNIMPLEMENTED. Once B-DOCK proves the
recording side of a dock / transfer / undock cycle is correct, `RouteCommand`
becomes the seam for driving Parsek's route CREATION and EXECUTION headlessly:
accepting the "Create Supply Route?" candidate, dispatching a route, and asserting
the dispatched delivery against the recorded window - closing the loop from
"Parsek recorded the dock correctly" to "Parsek's route engine acted on it
correctly". That is a separate design + implementation, out of scope here; B-DOCK
is its prerequisite (a proven dock/transfer/undock recording is what a route
command would consume).

---

## 10. Open design questions (with recommendations)

**Q1. One mission phase or two?** The current step model runs a single `phase =
"mission"`. B-DOCK's two-piece flow fits in one machine (section 3.3) with a
mid-mission `ACTION_PARSEK_COMMIT_TREE`. RECOMMENDATION: one mission, one phase,
the commit as a machine action (route 1). If the runner cannot cleanly interleave
a command-seam call inside the kRPC mission loop, the fallback is two mission
sub-specs with a `cmd = "CommitTree"` step between - a bigger harness change;
prefer the single-machine route.

**Q2. Rendezvous AP vs planner-composed rendezvous as the PRIMARY path?**
RECOMMENDATION: rendezvous AP primary (it is exposed and is the least code), with
the planner-composed fallback (4.1) as the P6 hardening path chosen only if the AP
proves unreliable in flight. Do not build the fallback before the first flight
measures the AP.

**Q3. DIY RCS closing loop vs docking AP for the final dock?** RECOMMENDATION:
docking AP (exposed, `speed_limit` is the one knob that matters). A DIY RCS closing
loop (translate toward `target_controller.relative_position`, null
`relative_velocity`) is a larger, finickier machine; keep it as the P6 fallback if
the docking AP bounces (5.3 misalignment give-up).

**Q4. Which tanks / ports to transfer between and undock?** RECOMMENDATION:
capture the Interceptor's own part set + the Station's part set (its
`vessel.parts`) BEFORE the dock, then after merge pick a transport-side and a
station-side tank by resource for each transfer, and the Station's top
Clamp-O-Tron for the undock. This mirrors the route window's
`TransportPartPersistentIds` / `EndpointPartPersistentIds` split. Confirm the
handles survive the merge (P9).

**Q5. How many transfers / which resources for "various ways"?** RECOMMENDATION:
exactly two, in opposite directions and different resources - LiquidFuel
transport -> station (a delivery) and MonoPropellant station -> transport (a
pickup) - so the oracle exercises both route directions and two resource types
with one flight. A third (LF station -> transport) is a cheap addition if the
first run shows headroom, but two is enough to prove the mixed-direction delta
recording.

**Q6. Fixture UT / phasing proximity.** The vessel-less save's UT does not set a
transfer window (this is an LKO rendezvous, not an interplanetary one), so unlike
B7 there is no synodic-wait sensitivity. RECOMMENDATION: keep the fixture UT as
harvested; phasing is handled by launching the Interceptor lower and letting the
rendezvous AP phase up. No fixture UT tuning is needed.
