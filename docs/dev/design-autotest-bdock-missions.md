# B-DOCK station + interceptor dock / transfer / undock (bdock_dock_transfer) - design + fixture

Status: DESIGN ONLY (branch `autotest-bdock-design`, 2026-07-23; revised after the
first Fable clean-context review). NOTHING here has flown. This is the preparation
design for the FIRST multi-vessel Parsek autotest mission: a two-piece flight that
flies a Station to Kerbin orbit, launches the SAME craft again as an Interceptor,
rendezvous + docks it to the Station, transfers fuel in both directions, then
undocks - the ENTRY POINT to verifying Parsek's logistics routes (dock + transfer
+ undock recording contract). Every threshold below is a tolerance / budget, never
a golden trajectory, and every numeric budget is ESTIMATED from arithmetic, not
measured. The `mlib` machine and the `mission_runner` action handlers this design
specifies are NOT implemented here; this doc is the plan a follow-up implementation
PR applies.

Authorities read (verified, not assumed, except where flagged):
- The pinned KRPC.MechJeb v0.8.1 rendezvous / docking / target / planner surface
  (`darchambault/KRPC.MechJeb` v0.8.1 `RendezvousAutopilot.cs`,
  `DockingAutopilot.cs`, `MechJeb.cs`, cross-checked against the umbrella genhis
  0.7.1 clone and the runner's live-flown `operation_transfer` usage) - GROUND
  TRUTH 1, section 4.1.
- The pinned kRPC v0.5.4 SpaceCenter surface (`mods/krpc` at tag `v0.5.4`:
  `LaunchVessel`, `TargetVessel` / `TargetDockingPort`, `ResourceTransfer.Start`,
  `DockingPort` / `DockingPortState`) - GROUND TRUTH 2, section 4.2.
- The Parsek command-seam LoadGame focusability gate
  (`Source/Parsek/TestCommands/TestCommandLoadGame.cs:48` `IsLoadedGameFocusable`)
  and the runner's active-vessel dereference + vessel-lost streak
  (`harness/missions/mission_runner.py` `read_snapshot`), which together decide the
  fixture model (section 2).
- Parsek dock / undock recording contract (`docs/dev/dock-undock-recording-structure.md`),
  cross-tree foreign-dock model (`docs/dev/design-mission-crosstree-dock.md`,
  M-MIS-8), the craft-baked-`persistentId` + `VesselLaunchIdentity` guid
  discriminator (`.claude/CLAUDE.md`), `RouteConnectionWindow` /
  `RouteProofCapture` (`Source/Parsek/RouteProofCapture.cs`), the removed
  commit-time route modal (`Source/Parsek/Logistics/RouteRunPrompt.cs`,
  `RouteCandidateFinder.cs`) - GROUND TRUTH 3, section 4.3.
- The B2 / B5 mission machine and shells (`harness/missions/lib/mlib.py`,
  `b2_lko_ascent.py`, `mission_runner.py`), the B1 / B2 / B5 scenario specs, the
  B7 design for shape, the coverage registry (`harness/coverage/registry.toml`),
  the committed fixture skeletons (`b1-pad-craft`, `b2-lko-craft`, `gloops-airshow`).

House rules: ASCII only, no em dashes. Fail-closed NaN semantics everywhere (a NaN
telemetry read never satisfies a gate). Comments explain constraints, not history.

---

## 0. What B-DOCK is, in one paragraph

B-DOCK is the first Parsek mission that flies TWO vessels in ONE session and
exercises the dock / transfer / undock recording pipeline that all logistics-route
verification is built on. Both vessels are the SAME stock craft ("Kerbal X", the
docking variant: `mk1-3pod` + a top `dockingPort2` Clamp-O-Tron, 8x `RCSBlock.v2`
fed by 4x `rcsTankRadialLong` MonoPropellant, a Mainsail core with 6 radial
`liquidEngine2` boosters and a `liquidEngine2-2.v2` upper). Piece 1 (Station)
starts pre-placed on the pad (the B2 fixture shape, section 2), flies the B2-proven
ascent to a ~110 km circular park, and is COMMITTED as its own tree. Piece 2
(Interceptor) launches the same craft again via `launch_vessel` into a lower
phasing orbit, runs MechJeb's rendezvous autopilot to close, MechJeb's docking
autopilot to hard dock, drives two kRPC `ResourceTransfer`s (LiquidFuel one way,
MonoPropellant the other), undocks, and commits. The Parsek-correctness payoff
(section 6): a cross-tree Dock branch point, an authoritative `onVesselsUndocking`
split, a completed `RouteConnectionWindow` whose recorded resource deltas match the
commanded transfers, and a recorded-identity check that both trees carry the
correct launch-unique `RecordedVesselGuid`s even though both are the same `.craft`
(baked pid 3620499050). Because the Station is alive in orbit when the Interceptor
launches, KSP's pid-collision rule (regenerate on collision with a currently-LIVE
vessel) means the two vessels almost certainly end up with DIFFERENT live pids, so
the guid discriminator is only weakly exercised here (the pids already separate the
trees); the strong guid path is hit only if KSP does NOT regenerate (section 6
invariant 6 is two-branch on the observed outcome).

---

## 1. Feasibility

**Ascent + orbit.** The stock Kerbal X reaches LKO with margin on its Mainsail
core; B2 flew the STOCK (non-docking) Kerbal X to an 80 km circular park (B2 PASS
2026-07-20). B-DOCK's craft is the DOCKING VARIANT (adds the top Clamp-O-Tron, 8
RCS blocks, 4 monoprop tanks - more mass, more drag), but it keeps the same
Mainsail-core two-stage architecture, so LKO is expected feasible; this is not
"measured on this craft" and stays a P1 live-prove, not a proven number. B-DOCK
parks the Station at ~110 km and the Interceptor at a ~90 km phasing orbit
(section 3.3). The modest apoapsis bump over B2's 80 km costs a longer circularize
coast, not more stages.

**Rendezvous + docking dv.** After circularization the Interceptor upper stage
retains a large reserve (the same architecture family B5 flew to the Mun and back).
A co-planar LKO rendezvous from a ~20 km phasing-altitude offset is ~100-250 m/s of
MechJeb plane / phasing / approach burns; the final docking approach is RCS
monoprop, not main-engine dv. Feasible with wide margin. **LIVE-PROVE P1:** fly it
and read the post-circularize stage dv on the Interceptor; a bad phasing draw
needing many phasing orbits can climb the dv.

**Monoprop budget (the docking-specific constraint).** The docking autopilot and
the final approach run on the 4x `rcsTankRadialLong` MonoPropellant load, which is
large. The REAL monoprop risk is a docking-AP stall that thrashes RCS for minutes
(section 5.3 give-ups cap it). **LIVE-PROVE P2:** read `monopropellant` remaining
after the dock; if a stall pattern drains it, lower `docking_autopilot.speed_limit`
or add an RCS-out give-up escalation.

**Session length.** Station ascent (~250 s) + circularize + Interceptor
`launch_vessel` + ascent (~250 s) + circularize + rendezvous (phasing orbits under
rails warp, ~1-3 orbits) + docking approach (~60-180 s at 1x, no warp) + two
transfers (server-side, a few seconds each) + undock + settle. WALL estimate
~1600-2400 s (section 5.4 budgets, ESTIMATED). The longest B-mission, but shorter
than B5/B7 because there is no interplanetary coast.

---

## 2. The fixture

### 2.1 The model decision: PRE-PLACED Station + launch_vessel Interceptor (PRIMARY)

The vessel-less-save-plus-two-`launch_vessel`s model (an earlier draft's primary)
was PROVEN to fail deterministically on run 1 inside Parsek's own seams, for two
independent reasons, both verified against source:

- **BLOCKER 1 - LoadGame rejects a vessel-less save.** The command-seam LoadGame
  verb only completes when the loaded game is FOCUSABLE, and
  `TestCommandLoadGame.IsLoadedGameFocusable` returns false unless `activeVesselIdx
  >= 0 && activeVesselIdx < protoVesselCount` (`TestCommandLoadGame.cs:52-54`). A
  vessel-less save has `activeVessel = -1` and zero proto-vessels, so LoadGame
  returns load-failed and the run never starts.
- **BLOCKER 2 - the runner has no no-vessel telemetry mode.** `mission_runner`'s
  `read_snapshot` dereferences `sc.active_vessel` every poll
  (`mission_runner.py:448,591`); at the space center with no active vessel this
  raises, and a short consecutive-failure streak escalates to a `vessel_lost`
  terminal snapshot (`mission_runner.py:574-587`), FLAKING the mission in ~1.5 s
  before the first `launch_vessel`.

Fixing the vessel-less model needs NEW seam work (a LoadGame no-vessel extension +
a runner PRELAUNCH no-vessel telemetry/dispatch mode) that is out of scope for a
first docking lane.

**Chosen model: the B2 fixture shape - a crewed docking-variant Kerbal X PRE-PLACED
on the pad - for piece 1, and `launch_vessel` for the Interceptor (piece 2).** The
pre-placed Station always has an active vessel, so BOTH blockers vanish with ZERO
new seam work: LoadGame drops into flight on the Station exactly as B1/B2 do, and
the runner's active-vessel telemetry works from frame 1. Piece 1 flies the Station
from the pad to orbit (the B2 ascent path); piece 2 launches the Interceptor via
`launch_vessel` from the now-clear pad. The Interceptor's `launch_vessel` still
exercises D1 `auto-record-launch`; the Station's pad ascent also auto-records on its
first staging (the B2 precedent cites D1 `auto-record-launch` for exactly this
pre-placed-pad-craft path), so the auto-record cell is RETAINED for both pieces, not
lost.

### 2.2 The vessel-less model (demoted to documented future work)

The vessel-less-save-plus-`launch_vessel`-x2 model (one uniform launch mechanism, no
operator fixture flight, both launches auto-recording symmetrically) is attractive
but requires the two seam extensions named under BLOCKER 1/2:

1. A LoadGame no-vessel extension: `IsLoadedGameFocusable` (or a new
   allow-space-center-boot flag on the LoadGame verb) must accept a settled
   SPACECENTER scene with no active vessel as a successful boot, instead of
   requiring a focusable flight.
2. A runner PRELAUNCH no-vessel mode: `read_snapshot` must tolerate
   `sc.active_vessel` being absent (emit a benign PRELAUNCH snapshot, not a
   vessel_lost streak) until the first `launch_vessel` produces an active vessel,
   and `perform` must dispatch `ACTION_LAUNCH_VESSEL` with no active vessel.

Documented here as the future enhancement that would unify both launches and add
the Station's launch as a second `launch_vessel` cell; NOT built in the first lane.

### 2.3 What was harvested on this branch

`harness/fixtures/saves/bdock-station-craft/` carries the harvested craft in
`Ships/VAB/Kerbal X.craft` (copied verbatim from the located clean candidate, 86
parts, verified 100% stock: every part name and every `ModuleXxx` in the file is a
stock Squad module - the module scan returned only stock modules `ModuleAblator /
ModuleCargoPart / ModuleControlSurface / ModuleInventoryPart / ModuleKerbNetAccess /
ModuleTripLogger` alongside the obvious command / engine / RCS / docking / decouple
/ science modules). The craft's baked `persistentId = 3620499050` is intact.

The pre-placed Station VESSEL itself is OPERATOR-STAMPED, following the exact
provenance of the committed B1 / B2 fixtures ("Fixture committed (operator
2026-07-19)"): a KSP-authored flight VESSEL landed at the pad cannot be forged
headlessly from a `.craft` (the source save and its quicksaves contain no 86-part
Kerbal X on a pad to transplant - only an 8-part pad craft and a 56-part orbiting
"Depot"), so the operator loads this save, launches the docking Kerbal X onto the
LaunchPad, and quicksaves it as `persistent` to complete the fixture - one operator
flight, reusable by every future dock mission. The committed save is the
operator-build BASE (a valid stock sandbox at the space center + the craft in
`Ships/VAB`), constructed from the `gloops-airshow` skeleton (its `Ships/VAB` + full
stock scenario scaffolding), with both `VESSEL` nodes stripped from `FLIGHTSTATE`,
`activeVessel = -1`, the one `Assigned` roster kerbal freed to `Available`, the
title set to `bdock-station-craft (SANDBOX)`, the stale `quicksave.*` pruned, and
`loadmeta vesselCount = 0`. It VALIDATES as ConfigNode-shaped text (brace balance
returns to depth 0, a recursive node parse yields the single top-level `GAME` node,
zero `VESSEL` tokens, `activeVessel = -1`, no `Assigned` crew).

**Fixture-build is PENDING-OPERATOR (LIVE-PROVE P3):** the operator stamps the
pre-placed Station and confirms the completed fixture boots into flight on the
Station (the B1/B2 path, known-good), producing the committed pre-placed fixture.
Until then the committed save is the base + the craft, exactly the state B1/B2 were
in before their operator build.

---

## 3. Mission architecture

### 3.1 One two-piece scenario, not two

**Decision: ONE scenario `B-DOCK-1` running ONE mission module
`bdock_dock_transfer`, flying both pieces in one session.** Splitting into a "launch
the station" scenario and a "dock to a pre-existing station" scenario would need the
station-scenario to leave a committed-orbit fixture for the dock-scenario to consume
- a bespoke cross-scenario fixture - and would lose the same-session, same-craft
identity test (both vessels must be alive at once for the cross-tree link and the
guid check to matter). The dock / transfer / undock cycle is only meaningful with
both vessels co-present, so it is one mission.

### 3.2 The Station-tree handling decision (commit before the Interceptor launch)

When the Interceptor launches, `launch_vessel` calls
`FlightDriver.StartWithNewLaunch`, a FLIGHT -> FLIGHT scene reload. Two questions:
what happens to the Station's active recording tree across that reload, and what
does the later cross-tree dock need the Station to be?

**Decision: COMMIT the Station tree before launching the Interceptor.** Rationale
from the two contracts:

- **Cross-tree foreign dock (M-MIS-8).** The pad-launched Station and the
  `launch_vessel` Interceptor are two INDEPENDENT trees. When the Interceptor (the
  controller, tree TA) docks to the Station, the recorder authors a SINGLE-parent
  `BranchPointType.Dock` in TA whose `TargetVesselPersistentId` is the Station's
  pid, and the Station's own pre-dock flight is a SEPARATE committed tree TB
  (`design-mission-crosstree-dock.md` section 1). For TB to exist and be walkable,
  the Station flight must be a committed recording, not a discarded or half-open
  one.
- **Route-candidate classification (D10).** The route analyzer detects the candidate
  on the TRANSPORT's tree (TA), but the route window's endpoint identity
  (`EndpointPartPersistentIds`, `DockEndpointResources`) resolves the Station's
  pre-dock parts through the pre-couple partner snapshot or the partner's committed
  `VesselSnapshot` (dock-undock doc section 6.1). A committed Station recording is
  what makes the endpoint side resolvable.

**Why NOT keep the Station as a background recording under TA.** There is NO
nearby-foreign-vessel BG admission: every `BackgroundMap` write is story-membership
(staging debris, controlled-decoupled children - `.claude/CLAUDE.md` BG note). The
Station is an independent launch, not a child of the Interceptor, so it is never
folded into TA as a BG child. (Its only genuine BG coverage is the POST-UNDOCK
departing Station half, section 6 invariant 3.)

**Plumbing for the mid-mission commit (implementation-PR concern, enumerated).** The
Station's TB must be committed at the Station -> Interceptor handoff, INSIDE the
mission phase. Route 1 (RECOMMENDED): a new mission action
`ACTION_PARSEK_COMMIT_TREE` that the runner maps to the Parsek command-seam
`CommitTree` verb (M-A2, armed via `PARSEK_TEST_COMMANDS`). This is deadlock-free
(the command seam uses a per-instance lock and journal-deduped command ids), but the
runner plumbing must be spelled out, not hand-waved as "one-frame":
- the runner writes a `CommitTree` command with a reserved, monotonic command-id
  into the seam's request channel (the `--params` command-id reservation the M-A2
  seam already uses), then
- polls the seam's response channel for that id under a BOUNDED wait; a response of
  OK advances the machine, an ERROR or a poll-budget expiry FLAKES the mission
  (driver-INVALID, retryable) - never PARSEK-FAILs. The machine's "one-frame
  waypoint" for the commit is therefore a BOUNDED-WAIT phase, not a literal single
  frame.
Route 2 (rely on an auto-commit across the `launch_vessel` scene reload) is NOT
trusted: the analogous far-vessel Switch-To reload BYPASSES the SceneExit FLIGHT ->
FLIGHT filter (`MapFocusObjectOnSelectPatch` Case B), so the auto-behavior on a
`StartWithNewLaunch` reload is UNKNOWN (LIVE-PROVE P4). Route 1 is the fail-safe.

### 3.3 Phase flow (both pieces)

```
                        --- PIECE 1: STATION (pre-placed on the pad) ---
PRELAUNCH
  (LoadGame drops into flight on the pre-placed Station, B2 shape. Active vessel
   present from frame 1.)

PRELAUNCH -> STATION-ASCENT -> STATION-CIRCULARIZE -> STATION-ORBIT
  (the B2-proven MJ ascent to the ~110 km park. autoRecordOnLaunch starts the
   Station recording on first staging. reachedOrbit evidence: apo/peri within
   window, ecc < max.)

STATION-ORBIT -> STATION-COMMIT   (bounded-wait, section 3.2 route 1)
  (ACTION_PARSEK_COMMIT_TREE -> command-seam CommitTree, poll for OK. TB is
   committed. Capture the Station's kRPC VESSEL HANDLE + its top-docking-port part
   HANDLE now, while it is the active vessel - NOT its name or pid, see section 5.1
   / P9.)

                     --- PIECE 2: INTERCEPTOR (launch_vessel) ---
STATION-COMMIT -> INT-LAUNCH
  (ACTION_LAUNCH_VESSEL "Kerbal X". Scene reload; the Station goes on-rails. A FRESH
   recording starts on the Interceptor -> a NEW tree TA. The Interceptor bakes the
   SAME pid 3620499050, but the Station is a currently-LIVE vessel, so KSP almost
   certainly regenerates the Interceptor's live pid on collision - the two trees
   then carry DIFFERENT pids (section 6 invariant 6). Read both live pids here - P5.)

INT-LAUNCH -> INT-ASCENT -> INT-CIRCULARIZE -> INT-PHASING-ORBIT
  (MJ ascent to the ~90 km phasing park, BELOW the Station so it phases faster.)

INT-PHASING-ORBIT -> SET-TARGET
  (ACTION_SET_TARGET_VESSEL = the captured Station HANDLE. The rendezvous AP and the
   MechJeb TargetController now read this target. precondition:
   target_controller.normal_target_exists.)

SET-TARGET -> RENDEZVOUS
  (ACTION_MJ_ENABLE_RENDEZVOUS with desired_distance = approachDistanceMeters and
   max_phasing_orbits capped. Done evidence: the rendezvous AP's Enabled latch flips
   FALSE (it disables itself when finished - mirrors mj_ascent_complete), AND
   target_distance <= approachDistanceMeters. Warps its phasing legs on rails.)

RENDEZVOUS -> MATCH-VELOCITY
  (the rendezvous AP's own terminal match, or ACTION_MJ_KILL_REL_VEL. Done evidence:
   target_rel_speed <= matchSpeedMetersPerSec.)

MATCH-VELOCITY -> DOCK
  (ACTION_SET_TARGET_DOCKING_PORT = the captured Station Clamp-O-Tron HANDLE, then
   ACTION_MJ_ENABLE_DOCKING with speed_limit = dockSpeedMetersPerSec. Done evidence:
   the docking-port state == Docked AND the docking AP's Enabled latch flips FALSE.
   On dock, KSP fires onPartCouple -> Parsek authors the cross-tree Dock branch in
   TA + opens the RouteConnectionWindow.)

DOCK -> TRANSFER
  (two commanded transfers, opposite directions and different resources:
     T1: ACTION_START_RESOURCE_TRANSFER LiquidFuel, transport tank -> station tank,
         amount = transferAmountLf (a small fixed amount well under min(source,
         dest-free), section 5.1 / Q5).
     T2: ACTION_START_RESOURCE_TRANSFER MonoPropellant, station tank -> transport
         tank, amount = transferAmountMp.
   Done evidence: both transfers report complete with transferred amount within
   tolerance of commanded.)

TRANSFER -> UNDOCK
  (ACTION_UNDOCK the Clamp-O-Tron. KSP fires onPartUndock then, authoritatively,
   onVesselsUndocking(oldVessel, newVessel) -> Parsek authors the Undock split
   branch in TA and completes the RouteConnectionWindow. Done evidence: vessel_count
   INCREASED by one AND the port state != Docked - Ready alone is only SOFT evidence
   because the port lingers in Undocking while the halves are still inside
   ReengageDistance.)

UNDOCK -> TERMINAL
  (ACTION_CANCEL_WARP + settle. The single post-mission CommitTree seam step commits
   TA. End state: two separate vessels in Kerbin orbit, TB (Station) + TA
   (Interceptor with the Dock/Undock cross-tree link) both committed.)
```

Survival is the contract: any vessel-lost / frozen terminal in ANY phase is an
ASSERT-FAIL loss (the B-family invariant, unchanged). A rendezvous / docking stall
is a bounded give-up FLAKE (section 5.3), not a loss.

**Commit-step accounting (MINOR 11).** There are exactly TWO commits: the
mid-mission Station commit via `ACTION_PARSEK_COMMIT_TREE` (route 1, inside the
mission phase) and exactly ONE post-mission `cmd = "CommitTree"` seam step for the
Interceptor tree TA. Do NOT add a second post-mission `CommitTree` - the Station
tree is already committed mid-mission, and a second seam `CommitTree` with no active
tree returns ERROR and reds the run.

---

## 4. Ground truths (verified vs assumed)

### 4.1 GROUND TRUTH 1 - KRPC.MechJeb v0.8.1 rendezvous + docking exposure

**VERIFIED** against the pinned `darchambault/KRPC.MechJeb` v0.8.1 source
(`RendezvousAutopilot.cs`, `DockingAutopilot.cs`, `MechJeb.cs`) AND cross-checked
against the umbrella genhis 0.7.1 clone. Both autopilots exist and are exposed;
v0.8.1's surface for these two classes is byte-identical to 0.7.1.

`mechjeb.rendezvous_autopilot` (`MuMech.MechJebModuleRendezvousAutopilot`):
- `enabled` (bool, get/set) - inherited from `KRPCComputerModule`; the start/stop.
  The AP disables itself (Enabled -> False) when it finishes, so the DONE evidence
  is the Enabled LATCH flipping false (mirroring `mj_ascent_complete`), not a parse
  of the localized `status` string.
- `desired_distance` (double) - the terminal approach distance the AP closes to.
- `max_phasing_orbits` (double) - cap on phasing orbits (bounds the worst-case wait;
  P6).
- `status` (string) - human status; a give-up DIAGNOSABILITY channel only.

`mechjeb.docking_autopilot` (`MuMech.MechJebModuleDockingAutopilot`):
- `enabled` (bool, get/set) - the start/stop; the DONE evidence is again the Enabled
  latch flipping false (plus the port state == Docked).
- `status` (string) - diagnosability only.
- `speed_limit` (double) - approach speed cap (the monoprop-budget knob, P2).
- `force_roll` (bool) + `roll` (double) - port roll alignment.
- `override_safe_distance` / `overriden_safe_distance`, `override_target_size` /
  `overriden_target_size`, `safe_distance` (float ro), `target_size` (float ro).

`mechjeb.target_controller` (`MuMech.MechJebModuleTargetController`) - the docking /
rendezvous TELEMETRY source (VERIFIED, TargetController.cs):
- `normal_target_exists` (bool) - a vessel/port target is set.
- `distance` (float, metres to target) - the range / closest-approach channel.
- `relative_velocity` (Tuple3) - its magnitude is the MATCH-VELOCITY evidence.
- `relative_position` (Tuple3), `docking_axis` (Tuple3), `can_align` (bool),
  `target_orbit` (Orbit).

The MechJeb `TargetController` mirrors KSP's own `FlightGlobals.VesselTarget`, so
setting the target via the kRPC SpaceCenter setters (section 4.2) drives both the
game target and MechJeb's rendezvous / docking APs.

**Discriminator caveat (why the cross-check matters):** the runner's live-flown
`operation_transfer` uses `op.capture` / `op.plan_capture` / `op.rendezvous`, which
do NOT exist in genhis 0.7.1's `OperationTransfer.cs` (that had only `InterceptOnly`
/ `PeriodOffset` / `SimpleTransfer`). So v0.8.1 DID diverge from 0.7.1 for the
maneuver operations - which is why the two autopilot classes were re-verified
DIRECTLY against the v0.8.1 source, not assumed from the umbrella clone.

**The planner-composed fallback is available but NOT the primary path.** Because the
rendezvous AP IS exposed, the primary path is `rendezvous_autopilot.enabled`. The
fallback (B7 finding-3) is `operation_transfer` with `rendezvous = True` (the
targeted-intercept flag, VERIFIED present) for the phasing intercept plus
`operation_kill_rel_vel` (VERIFIED exposed:
`mechjeb.maneuver_planner.operation_kill_rel_vel`) for the match-velocity node, then
course corrections. Kept documented as the P6-hardening path.

### 4.2 GROUND TRUTH 2 - kRPC v0.5.4 SpaceCenter surface

**VERIFIED** against `mods/krpc` at tag `v0.5.4` (`git show v0.5.4:...`).

- `SpaceCenter.launch_vessel(craft_directory, name, launch_site, recover=True,
  crew=None, flag_url="")` - the v0.5.4 signature IS 6-arg (a crew list AND a flag
  url, both defaulted). It closes dialogs, runs pre-flight checks, recovers any
  vessel already on `launch_site` when `recover=True`, then
  `FlightDriver.StartWithNewLaunch(...)` - a scene reload into flight on the new
  craft, focused as active. Craft path resolution is
  `<save>/Ships/<craft_directory>/<name>.craft`, so `name = "Kerbal X"` resolves the
  harvested fixture craft. The `crew` param (default None) lets the mission seed the
  `mk1-3pod` explicitly if a crewed Interceptor is wanted; None launches KSP's
  default manifest. `launch_vessel_from_vab(name, recover=True)` is the "VAB" /
  "LaunchPad" shortcut.
- `SpaceCenter.target_vessel` (settable Vessel) and `SpaceCenter.target_docking_port`
  (settable DockingPort) - both present at v0.5.4. These set the game target the APs
  read. They take kRPC OBJECT HANDLES, not names/pids (see P9).
- `ResourceTransfer.start(from_part, to_part, resource, max_amount)` returns a
  `ResourceTransfer` with `complete` (bool), `amount` (float, transferred so far),
  `total_amount` (float). The server ticks it each fixed update; the client polls
  `complete` / `amount`. It moves at most `max_amount`, bounded by source
  availability and destination free space.
- `DockingPort.state` -> `DockingPortState` enum `{Ready, Docked, Docking,
  Undocking, Shielded, Moving}`. `DockingPort.undock()` returns the new `Vessel`
  from the split (throws if not `Docked`; polls internally until the split
  completes). `DockingPort.docked_part`, `DockingPort.reengage_distance`.

**No closest-approach RPC is needed.** kRPC v0.5.4 has no dedicated closest-approach
procedure; the rendezvous telemetry comes from `target_controller.distance` /
`relative_velocity` (section 4.1). The existing `next_pe` / `next_body` snapshot
fields (patched-conic arrival) are NOT the docking channel and are not reused.

### 4.3 GROUND TRUTH 3 - Parsek recording contracts B-DOCK exercises

**VERIFIED** against `docs/dev/dock-undock-recording-structure.md`,
`design-mission-crosstree-dock.md`, `.claude/CLAUDE.md`, `RouteProofCapture.cs`,
`RouteRunPrompt.cs`.

- **Dock = merge branch.** `onPartCouple` -> Parsek captures the pre-couple partner
  snapshot (endpoint's pre-dock resources), appends a Dock structural-event
  snapshot, closes the parent(s) at `ExplicitEndUT = dockUT` with
  `TerminalState.Docked`, and next frame `CreateMergeBranch` builds a
  `BranchPointType.Dock` (persisted `type = 2`, `mergeCause = DOCK`, `targetVesselPid
  = <partner pid>`) whose single child is the merged-vessel recording carrying the
  open `RouteConnectionWindow`. In the cross-tree case the branch has ONE
  `ParentRecordingIds` entry (transport) + `TargetVesselPersistentId` = the Station
  pid.
- **Undock = split branch, driven by `onVesselsUndocking`.** `onPartUndock` fires
  FIRST (once, transient pre-reparent pid) and only captures the Undock structural
  snapshot + origin seed; `onVesselsUndocking(oldVessel, newVessel)` fires LAST,
  unconditionally, with FINAL pids, and drives `DeferredUndockBranch ->
  CreateSplitBranch(BranchPointType.Undock, ...)` (persisted `type = 3`, `splitCause
  = UNDOCK`) plus `TryCompleteLatestRouteConnectionWindow`. The active vessel
  continues into one child; the OTHER half goes to the BACKGROUND recorder
  (dock-undock doc section 4). Do NOT gate on a second `onPartUndock`.
- **RouteConnectionWindow.** Opened at dock (when `routeTargetVesselPid != 0`) with
  `DockTransportResources` / `DockEndpointResources` frozen; completed at undock with
  `UndockTransportResources` / `UndockEndpointResources`. Net cargo = `Undock* -
  Dock*` per side, conservation-mirrored. **Logging reality (MAJOR 6):**
  `RouteProofCapture` only Verbose-logs COUNTS at completion (`transportRes=N
  endpointRes=N` at `RouteProofCapture.cs:615-619`), NOT the per-resource delta
  amounts - so the recorded-delta contract has NO observable surface today (section 6
  invariant 5 requires the implementation PR to add one).
- **Craft-baked pid + guid discriminator.** `persistentId` is baked in the `.craft`
  (3620499050) and reused verbatim on every launch; KSP regenerates it ONLY on
  collision with a currently-LIVE vessel. The Station is live in orbit when the
  Interceptor launches, so KSP almost certainly regenerates the Interceptor's live
  pid -> the two trees carry DIFFERENT pids (the EXPECTED branch). The launch-unique
  discriminator is KSP's `Vessel.id` guid (`Recording.RecordedVesselGuid`, compared
  via `VesselLaunchIdentity`); the cross-tree Dock link is guid-gated. When the pids
  already differ the guid is only WEAKLY load-bearing; the strong guid path is hit
  only if KSP does NOT regenerate (section 6 invariant 6 is two-branch).
- **ResourceManifest deltas.** `VesselSpawner.ExtractResourceManifest` +
  `ResourceManifest.ComputeResourceDelta` produce the per-resource deltas the route
  window records.
- **The commit-time "Create Supply Route?" modal was REMOVED**
  (`RouteRunPrompt.cs:14`, `ParsekScenario.cs:1012`): route candidates now surface in
  the Logistics window via `RouteCandidateFinder`, not a commit-time dialog
  (section 6 invariant 7).

---

## 5. Machine placement: a NEW mlib module, reusing shared infrastructure

**Decision: a dedicated `bdock_dock_transfer` mission with a NEW machine
(`bdock_decide` / `BDockParams` / `BDockState` / a `BDOCK_*` phase enum) in
`mlib.py`, NOT an extension of the B5 machine.** The rendezvous / dock / transfer /
undock phases are a DIFFERENT SHAPE from B5's single-vessel ascend -> transfer-burn
-> coast -> flyby: B5's transitions key on SOI body changes, apoapsis / eccentricity
floors, and time-to-SOI; B-DOCK's key on target distance, relative speed,
docking-port state, transfer completion, and the two-vessel launch sequence.
Grafting a second vessel + a target + transfers onto `b5_decide` would bloat the
most-tested decision function with a mostly-disjoint branch set and risk the
B5/B6/B7 byte-identical guarantee. What IS shared is reused via `mission_runner` +
the shared mlib helpers: the ascent + circularize machinery (literally B2), the
connect-retry, the warp watchdog + frozen detector, the give-up / flake budget
pattern, the `TelemetrySnapshot`, the deterministic result JSON, and the
`resolve_flight_verdict` / assertion-evaluator seam. Both ascent legs emit the same
B2/B5 ascent ACTIONs; only the phase machine is new.

### 5.1 New ACTIONs (runner spec; the runner is owned by another agent)

| Action | Runner mapping (kRPC v0.5.4 / MechJeb v0.8.1) |
|---|---|
| `ACTION_LAUNCH_VESSEL` (text = craft name) | `sc.launch_vessel("VAB", name, "LaunchPad")`; wait for the new active vessel to settle. |
| `ACTION_PARSEK_COMMIT_TREE` | Parsek command-seam `CommitTree` (route 1, section 3.2): reserved command-id, bounded response poll, OK advances / ERROR or timeout flakes. |
| `ACTION_SET_TARGET_VESSEL` (a captured kRPC vessel HANDLE) | `sc.target_vessel = <station handle>`. HANDLE-based, never name/pid (P9). |
| `ACTION_SET_TARGET_DOCKING_PORT` (a captured kRPC port HANDLE) | `sc.target_docking_port = <station clamp-o-tron handle>`. |
| `ACTION_MJ_ENABLE_RENDEZVOUS` (value = desired distance, limit = max phasing orbits) | set `rendezvous_autopilot.desired_distance` / `.max_phasing_orbits`, then `.enabled = True`. |
| `ACTION_MJ_KILL_REL_VEL` | `maneuver_planner.operation_kill_rel_vel.make_nodes()` + execute; OR rely on the rendezvous AP terminal match. |
| `ACTION_MJ_ENABLE_DOCKING` (value = speed limit) | set `docking_autopilot.speed_limit`, then `.enabled = True`. |
| `ACTION_MJ_DISABLE_DOCKING` | `docking_autopilot.enabled = False` (give-up cleanup). |
| `ACTION_START_RESOURCE_TRANSFER` (text = resource, value = amount, from/to part HANDLEs) | `ResourceTransfer.start(from_part, to_part, resource, amount)`; the runner polls `.complete`. |
| `ACTION_UNDOCK` (a captured port HANDLE) | `port.undock()`. |

**Handle capture (P9).** kRPC v0.5.4 exposes no pid/guid on `Vessel`, both vessels
are literally named "Kerbal X", and Parsek's ghost `ProtoVessel`s can inject
same-named map entries - so target / transfer / undock selection MUST use kRPC object
HANDLES captured while the object is reachable (the Station vessel + its top docking
port captured during STATION-COMMIT while the Station is active; the transport /
station tank handles captured post-dock from the merged vessel's parts by resource +
by the pre-dock part-set split). Name/pid selection is forbidden in the driver;
pid/guid identity is the OFFLINE oracle's job (section 6), reading the persisted
recordings.

### 5.2 New TelemetrySnapshot fields (runner-populated, fail-closed defaults)

- `target_distance: float = nan` - `mj.target_controller.distance`; NaN fails the
  RENDEZVOUS-done gate closed.
- `target_rel_speed: float = nan` - `norm(mj.target_controller.relative_velocity)`;
  NaN fails MATCH-VELOCITY closed.
- `docking_state: str = ""` - the active/target docking port `state.name`; ""
  matches no gate.
- `target_set: bool = False` - `mj.target_controller.normal_target_exists`.
- `mj_rendezvous_enabled: bool = False` / `mj_docking_enabled: bool = False` -
  carried Enabled-latch evidence (done = latch flips False, NIT 15).
- `vessel_count: int = 0` - `len(sc.vessels)`; the UNDOCK split gate reads its
  INCREASE (a split raises the count) - load-bearing, with `docking_state != Docked`;
  `Ready` alone is only soft evidence (MINOR 10).
- `transfer_complete: bool = False` + `transfer_amount: float = nan` - the active
  `ResourceTransfer` poll (the runner owns the handle).
- `monopropellant: float = nan` - vessel-total MonoPropellant (the P2 channel).

Every new field defaults to a fail-closed sentinel so a runner that forgets to
populate one fails the gate rather than faking a satisfied condition.

### 5.3 Give-ups and watchdogs (docking has NEW stall classes)

Each phase carries a GAME-time budget and a wall watchdog; expiry FLAKES the mission
(driver-INVALID, retryable), never PARSEK-FAILs. New docking stall classes:

- **Rendezvous stall.** `max_phasing_orbits` bounds the AP; add a
  `rendezvousTimeoutSeconds` game-budget give-up and a "distance not shrinking over N
  polls" no-progress detector. Log the AP `status` for diagnosis.
- **Approach stall / RCS-out-of-monoprop.** During DOCK, if `docking_state` stays
  `Docking` past `dockTimeoutSeconds`, OR `monopropellant` hits ~0 while not yet
  `Docked`, `ACTION_MJ_DISABLE_DOCKING` and FLAKE (reason names monoprop-out vs
  approach-stall from the monoprop reading).
- **Port misalignment / bounce.** If `docking_state` oscillates `Docking` <-> `Ready`
  past a bounded retry count, flake with a misalignment reason (candidate hardening:
  `force_roll`).
- **Transfer stall.** If `transfer_complete` stays False past
  `transferTimeoutSeconds` with `transfer_amount` not advancing, flake (dry source or
  full destination).
- **Frozen / vessel-lost.** The shared frozen detector + `vessel_lost` terminal apply
  in every phase. The docking approach runs at 1x (no warp), so the frozen signature
  is genuinely frame-varying and does not false-trip; the RENDEZVOUS phasing legs
  warp on rails like B5's coast.

### 5.4 Budgets (all ESTIMATED, flagged)

GAME-time phase budgets: ascent 1200 / circularize 600 each leg (the ~110 / ~90 km
parks); `rendezvousTimeoutSeconds = 30000` (phasing orbits under warp advance game
time fast); `dockTimeoutSeconds = 600` (the 1x approach); `transferTimeoutSeconds =
120` each; undock/settle 120. WALL: mission phase ~2400 s (Station ascent ~250 +
circularize + Interceptor launch/ascent ~250 + circularize + rendezvous ~200 under
warp + docking ~120-180 at 1x + two transfers ~10 + undock/settle + margin); runtime
~3000 s (240 LoadGame + 2400 mission + the two commits + FlushAndQuit + margin,
following the B5 "post-mission seam steps must survive a full-budget mission"
lesson). Retry policy: once. Every number is arithmetic - re-time against the first
live run (P1, P2, P6, P8).

---

## 6. Recording-correctness oracle (what proves Parsek recorded it right)

MISSION-OK is a driver-validity result, ORTHOGONAL to Parsek correctness. The oracle
below reds a mis-recorded good flight. Each invariant names its observable artifact
and whether it is observable TODAY.

1. **Two trees with correct topology.** A committed Station tree TB (single ascent
   recording, terminal not-Docked - committed in orbit) and a committed Interceptor
   tree TA (ascent -> Dock merge child -> Undock split children). OBSERVABLE via the
   analyzer over the produced recordings + `[expectations.recordings]` + the
   persisted `RECORDING_TREE` / `BRANCH_POINT` shape.
2. **Cross-tree Dock link (M-MIS-8 shape).** TA carries a `BranchPointType.Dock`
   (`type = 2`) with `TargetVesselPersistentId` = the Station's live pid, single
   parent, single merged child. OBSERVABLE via the log contract `Tree branch created:
   type=Dock` + the persisted `targetVesselPid`. The cross-tree-link DERIVATION (TA's
   Dock pid matching a TB recording pid, guid-gated) is read-time logic; a HEADLESS
   assertion of link-resolvability is an analyzer-growth item (NOT observable today).
3. **Post-undock departing-half BG continuity.** There is NO pre-dock or docked BG
   recording of the Station (no nearby-foreign-vessel admission; docked it is one
   merged vessel). The genuine BG coverage is the POST-UNDOCK departing half: at the
   undock split the active vessel continues into one child and the OTHER half goes to
   the BACKGROUND recorder (dock-undock doc section 4). That split-child's trajectory
   must be continuous with no BG on-rails gaps mis-modeled as env TrackSections
   (`bg-on-rails` emits no env-classified sections). OBSERVABLE via the analyzer's
   BG-continuity rules over the post-undock background child. D5 `bg-recording` is
   citable via THIS window.
4. **Authoritative undock split.** TA carries a `BranchPointType.Undock` (`type = 3`)
   authored by `onVesselsUndocking`, two post-split children each with continuous
   coverage. OBSERVABLE via the log contract `OnVesselsUndocking:entry recordedPid=...
   oldPid=... newPid=...` + `Tree branch created: type=Undock` + the persisted
   two-child split shape. Must NOT require a second `onPartUndock`.
5. **Recorded resource deltas match the commanded transfers (the headline
   payoff).** The completed `RouteConnectionWindow` on TA's merged child must satisfy
   `UndockTransportResources - DockTransportResources` = the commanded LF/MP net
   (within a per-resource tolerance), endpoint side conservation-mirrored. **NOT
   observable today:** `RouteProofCapture` logs COUNTS only (section 4.3),
   `[expectations.route]` is a reserved no-op, and no analyzer route-delta rule
   exists. **The implementation PR MUST ship a minimal delta surface** - at
   route-window completion, an Info line emitting the per-resource
   `undockTransport-dockTransport` delta (and the endpoint mirror), plus a
   `[expectations.logContracts]` entry asserting it - so the FIRST PASS checks the
   delta contract, not just dock/undock topology. Deferring this leaves section 0's
   whole-lane payoff unchecked and is not acceptable.
6. **Recorded launch identity (same craft twice) - TWO-BRANCH on the observed pid
   outcome.** Read both live pids at Interceptor launch (P5):
   - EXPECTED branch (KSP regenerated the Interceptor pid, so pids DIFFER): TB and TA
     carry DIFFERENT `VesselPersistentId`s; the cross-tree link resolves by pid; the
     oracle ADDITIONALLY confirms the two recordings carry DIFFERENT
     `RecordedVesselGuid`s (proving Parsek captured the launch-unique guid even
     though it was not load-bearing here). This is the likely branch; invariant 6
     must NOT red it.
   - STRONG branch (KSP did NOT regenerate, pids COLLIDE at 3620499050): the two trees
     share the baked pid and ONLY the guid separates them - the strong
     guid-discriminator test; a correct cross-tree link + target resolution here
     proves the discriminator works.
   OBSERVABLE via the persisted recordings' `VesselPersistentId` + `RecordedVesselGuid`
   fields; a guid-gate LOG CONTRACT (confirm `GhostChainWalker.ScanBranchPointClaims`
   logs the guid gate, add a Verbose line if absent) is an implementation-PR item.
7. **Route-candidate classification of the resulting pair.** After commit, the route
   analyzer should detect the Interceptor->Station pair as a route candidate. The
   commit-time "Create Supply Route?" modal was REMOVED (section 4.3); candidates now
   surface in the Logistics window via `RouteCandidateFinder` / `RouteAnalysisEngine`.
   OBSERVABLE via those engines' log lines IF the mission drives to the analyzer
   trigger; a HEADLESS route-candidate assertion (no UI) is a candidate-registry /
   analyzer growth item (NOT observable headlessly today).

**NOT observable today (analyzer / registry / seam growth for the implementation
PR):** a headless cross-tree-link assertion (2), the route-window resource-delta
surface + logContract (5, REQUIRED not deferred), a guid-gate log contract (6), and a
headless route-candidate assertion (7). Items 2/6/7 are growth backlog; item 5 is a
REQUIRED first-PR deliverable so the delta contract is actually checked.

**`[expectations.recordings]` (MINOR 13).** Two trees, each ascent drops launch
clamps + boosters + nose cones as parent-anchored debris children (B2 uses `count =
{min=1, max=8}` for one ascent), and TA additionally gains the merged child + the two
undock children. So the window spans two trees: `count = {min=2, max=20}` (two main
recordings floor; two ascents' debris + merge + undock children ceiling, absorbing
per-run debris-timing variance exactly as B1/B2 do). The exact max is
debris-timing-variance-dependent and is widened, not tightened, to avoid redding on
staging variance.

---

## 7. Coverage cells

`dimensionsCovered` cites ONLY existing `harness/coverage/registry.toml` values:

- `D1 = ["auto-record-launch"]` - the Interceptor's `launch_vessel` (certain) and the
  Station's pad ascent (auto-records on first staging, the B2 precedent).
- `D3 = ["orbital-checkpoint"]` - both orbits.
- `D4 = ["atmospheric", "exo-propulsive"]` - the two ascents.
- `D5 = ["cross-tree-foreign-dock", "undock-split", "bg-recording"]` - the two
  independent trees docking, the authoritative split, and the POST-UNDOCK
  departing-half BG coverage (invariant 3).
- `D7 = ["dock-undock", "rcs"]` - the docking-port couple/undock part events and the
  RCS approach.
- `D8 = ["route"]` - the route ledger module engaged by the completed window.
- `D10 = ["candidate-detection", "ksc-origin", "dock-producer", "delivery", "pickup",
  "mixed-direction", "resource-cargo"]` - the route candidate; the Interceptor's KSC
  launch origin (`ksc-origin`, NOT `docked-depot-origin` - the transport launches
  from the pad, not from a docked depot); the docked-vessel producer (`dock-producer`,
  the registry's dock-KIND value); and the two-direction resource transfer (LF one
  way = delivery, MP the other = pickup / mixed-direction resource cargo).
- `D14 = ["kerbin", "warp-rails", "scene-flight", "sandbox"]` - the body, the
  phasing-orbit rails warp, the flight scene, the sandbox mode.

**Genuine gaps (noted, NOT citable - the implementation PR grows the registry in the
SAME PR per rule N9):**
- D10 has no ORBITAL rendezvous / orbital-dock value; the whole orbital rendezvous +
  hard-dock flow (as opposed to a landed rover dock) is uncited beyond the generic
  cells - a candidate `D10` growth value (e.g. `orbital-rendezvous-dock`).
- D14 has no value for the same-craft-twice pid-collision identity axis; if a D18
  (ghost chains / identity) value is added for the guid-discriminator path, cite it
  there.

(The earlier draft's "no dock-KIND value" gap was WRONG - `dock-producer` exists in
D10 and is now cited; the remaining gap is only the D10-level orbital-dock value.)

---

## 8. Risks and live-prove list (P-numbered)

- **P1 - Rendezvous / dock stage-dv margin.** The docking VARIANT is heavier than the
  stock Kerbal X B2 flew; LKO + rendezvous is expected feasible but is not a measured
  number. Fly it, read post-circularize + post-dock stage dv.
- **P2 - Monoprop budget on the docking AP.** The 4x monoprop load is large, but a
  docking-AP stall thrashes RCS. Read `monopropellant` post-dock; the RCS-out give-up
  (5.3) caps the drain; lower `speed_limit` if it drains.
- **P3 - The pre-placed Station fixture is operator-stamped and boots into flight.**
  The committed save is the operator-build base + the craft; the operator launches the
  docking Kerbal X onto the pad, quicksaves as `persistent`, and confirms the
  completed fixture boots on the Station (the B1/B2 known-good path). (This REPLACES
  the earlier vessel-less-boot risk, which is now demoted to the future-work seam
  extensions of section 2.2.)
- **P4 - Station-tree survival across the Interceptor launch_vessel scene reload.**
  `StartWithNewLaunch` is a FLIGHT -> FLIGHT reload; the SceneExit filter behavior is
  unknown (the far-Switch-To analog bypasses it). Commit TB EXPLICITLY (route 1, 3.2)
  rather than trusting an auto-commit; live-observe what the tree becomes.
- **P5 - Same-craft pid outcome at the Interceptor launch.** Both launches bake pid
  3620499050. The Station is live in orbit at Interceptor launch, so KSP SHOULD
  regenerate the Interceptor's live pid (the EXPECTED different-pids branch). Read
  both live vessels' pids to confirm which branch of invariant 6 the run hits; the
  oracle must not ASSUME either.
- **P6 - Rendezvous AP robustness on the 20+ t upper stage.** MechJeb's rendezvous AP
  can be finicky with a heavy stack. If it does not converge within
  `max_phasing_orbits`, harden via the planner-composed fallback (4.1).
- **P7 - launch_vessel triggers Parsek auto-record (Interceptor).** The Interceptor
  half depends on `autoRecordOnLaunch` firing on the `launch_vessel`
  `StartWithNewLaunch` path (the Station half is the proven B2 pad-launch
  auto-record). Confirm the first live run auto-records the Interceptor; if the hook
  is missed, the mission must issue an explicit `StartRecording` command.
- **P8 - Session length under the wall budget.** The ~2400 s estimate is arithmetic;
  a slow rendezvous or a docking retry can blow it. Retry-once; re-time against the
  first run.
- **P9 - Handle-based target / transfer / undock selection.** kRPC v0.5.4 Vessel
  exposes no pid/guid, both vessels are named "Kerbal X", and ghost ProtoVessels can
  inject same-named entries - so selection MUST use captured kRPC handles (Station
  vessel + port captured while active; tanks captured post-dock by resource + pre-dock
  part-set). Confirm the handles survive the merge / reload.

---

## 9. Follow-on: the RouteCommand seam verb (out of scope)

The M-A2 command-seam design reserves a `RouteCommand` verb
(`design-autotest-seam-verbs-c1.md`: "logistics route dock/transfer/undock scripting
(D10)") that is declared but UNIMPLEMENTED. Once B-DOCK proves the recording side of
a dock / transfer / undock cycle is correct, `RouteCommand` becomes the seam for
driving Parsek's route CREATION and EXECUTION headlessly: accepting the route
candidate (now surfaced in the Logistics window via `RouteCandidateFinder`),
dispatching a route, and asserting the dispatched delivery against the recorded window
- closing the loop from "Parsek recorded the dock correctly" to "Parsek's route
engine acted on it correctly". That is a separate design + implementation, out of
scope here; B-DOCK is its prerequisite.

---

## 10. Open design questions (with recommendations)

**Q1. One mission phase or two?** The current step model runs a single `phase =
"mission"`. B-DOCK fits in one machine (section 3.3) with a mid-mission
`ACTION_PARSEK_COMMIT_TREE` bounded-wait (route 1). RECOMMENDATION: one mission, one
phase, the Station commit as a machine action. If the runner cannot cleanly poll the
command seam inside the kRPC mission loop, the fallback is two mission sub-specs with
a `cmd = "CommitTree"` step between - a bigger harness change; prefer the
single-machine route.

**Q2. Rendezvous AP vs planner-composed rendezvous as PRIMARY?** RECOMMENDATION:
rendezvous AP primary (exposed, least code), planner-composed (4.1) as the P6
hardening path chosen only if the AP proves unreliable in flight.

**Q3. DIY RCS closing loop vs docking AP for the final dock?** RECOMMENDATION: docking
AP (exposed, `speed_limit` the one knob). A DIY RCS closing loop is a larger,
finickier machine; keep it as the P6 fallback if the docking AP bounces (5.3
misalignment give-up).

**Q4. Which tanks / ports to transfer between and undock, and how to select them?**
RECOMMENDATION: capture kRPC HANDLES, never names/pids (P9): the Station vessel + its
top Clamp-O-Tron while the Station is active (STATION-COMMIT), then post-dock pick a
transport-side and a station-side tank by resource using the pre-dock part-set split
(mirroring the route window's `TransportPartPersistentIds` / `EndpointPartPersistentIds`).
Confirm the handles survive the merge.

**Q5. How many transfers / which resources, and how much?** RECOMMENDATION: exactly
two, opposite directions and different resources - LiquidFuel transport -> station
(delivery) and MonoPropellant station -> transport (pickup) - so the oracle exercises
both directions and two resource types in one flight. Size each as a SMALL fixed
amount comfortably under `min(commanded, source available, destination free space)`
so the transfer completes rather than stall-flaking on a dry source or full
destination (NIT 16); do NOT command more monoprop than the docking approach left in
the source tank.

**Q6. Fixture UT / phasing proximity.** This is an LKO rendezvous, not an
interplanetary one, so the fixture UT sets no transfer window (unlike B7).
RECOMMENDATION: keep the fixture UT as harvested; phasing is handled by launching the
Interceptor lower and letting the rendezvous AP phase up. No UT tuning needed.
