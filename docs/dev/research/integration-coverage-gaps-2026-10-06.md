# Integration coverage gaps: logistics x rewind x ledger x playback (2026-10-06)

Status: RESEARCH. Inputs: a machine parse of all 367 harness specs (steps, expectations,
coverage claims) against the live-proven table in `docs/dev/autotest-status.md`, a read of the
design docs and the code paths where the subsystems hand state to each other, and an
adversarial code-read verification pass over the suspected defects that read turned up.
Nothing here was flown. File:line citations are against `main` at 2026-10-06.

## 1. The short version

The harness proves each feature well on its own and proves many PAIRS of the older
features together (Re-Fly x career, Re-Fly x warp, Re-Fly x quickload, loops x CommNet x
warp). Supply routes are the exception: they are tested almost entirely in isolation.

- **54 specs drive `RouteCommand`. None of them also drives `InvokeRewind` (Re-Fly),
  `WarpToUT` (real warp), `RealSpawn`, `MissionConfig` or `KscAction`.** The only route lane
  that rewinds at all is H58 (`InvokeRewindToLaunch`), on a SANDBOX save, against a tree it
  manufactured rather than the route's own.
- **Every lane that dispatches a route is a Kerbin surface rover** (RVR-2..20, H58, H59,
  V27M, GUI-20..23). No inter-body or moon route has ever dispatched or delivered live; the
  inter-body fixture is only rendered (B32, V26M, V26T) or photographed (GUI-3, GUI-25).
  `docs/dev/autotest-roadmap.md` says so itself (G10 residue).
- **The ledger oracle is never paired with a route or with a rewind.** 11 specs carry
  `[expectations.ledger]`, none is a route lane, `harness/lib/oracle.py` knows no route action
  types, and `hlib` refuses a ledger block on a rewind lane (the L4 deferral). Route funds
  are pinned only by log tokens (RVR-4 `DispatchDebit ... cost=7410`).
- **Route funds across a rewind have never been asserted live**: H58 is sandbox, and the
  in-game `RouteRedeliversAfterRewindPastDelivery` (run isolated by H38 / H39 / H40) skips its
  funds branch on those sandbox hosts (`RouteRewindRedeliveryInGameTest.cs:226-230`).
- Every live dispatch happened in FLIGHT after a seam `TimeJump`, which RVR-7 measured
  dispatches with the endpoints packed. No route has run under real rails warp, at the KSC
  or in the Tracking Station.

And the code read found real defects in exactly the uncovered space (section 3): the
untested combinations are not hypothetical risk.

PLAYER-LOOPING-REMOVAL (todo) makes the route the ONLY product loop path, so this is the
surface that most needs integration coverage next: about 50 lanes drive player loops today,
about 6 play a route-driven loop.

## 2. Coverage matrix (condensed)

LIVE = a flown, green harness lane. SYNTH = an in-game test run by a green lane, with the
second feature faked inside the test. HEADLESS = xUnit only.

| Combination | Today |
|---|---|
| Route x Re-Fly (real `InvokeRewind`) | NONE. SYNTH: H6 (`RouteRewindTimeline`), H38/H39/H40 (`RouteRedelivers...`). HEADLESS: `RouteRewindDormant*`, `RouteRewindStatusFidelity*`, `RouteRewindRedelivery*` |
| Route x Rewind-to-Launch | LIVE H58 (sandbox, foreign tree). Own backing tree: HEADLESS only (`RouteRewindDormantTests.Materialize_OccupiedTree_DropsDormantTwin`), blocked by ROUTE-REWIND-TO-LAUNCH-UNREACHABLE-ON-COMMITTED-FIXTURES |
| Route x F5/F9 back across a dispatch | NONE (H59 / V27M / V18T save and reload at the SAME instant only) |
| Route x stock Revert | HEADLESS `RouteRevertSafetyTests` |
| Route x career funds | LIVE RVR-4, RVR-17, GUI-23 (no rewind); the transport-subset costing path never ran in flight |
| Route x contracts / strategies / facility upgrades | HEADLESS `LedgerStateFuzzerTests` only |
| Route x crew (loop crew reservation) | HEADLESS `LoopRecordingCrewReservationTests` |
| Route x real warp / KSC / TS scenes | NONE live (TimeJump in FLIGHT only); HEADLESS `RouteInterBodyFireTests.WarpOverThreeWindows*`, `RouteLoopClockTests`; SYNTH H34 at SPACECENTER |
| Inter-body or moon route x dispatch | SYNTH H34 (real builder, faked row emitter); HEADLESS `RouteInterBodyFireTests` (17). NONE real |
| Inter-body route x anything (rewind, career, warp, spawn) | NONE |
| Route endpoint x Real Spawn Control / chain-tip / recovery | Endpoint removed or substituted: LIVE RVR-18, V27M. RSC or chain-tip recovery of an endpoint: NONE |
| Route x CommNet relay | NONE |
| Multi-stop / pickup / round-trip / escrow | SYNTH H38; LIVE RVR-7 (two-stop relay), H60 (escrow, one scene) |
| Route-driven loop ghosts | LIVE V18T, LT-4, H59, V27M; inter-body render only (B32, V26M, V26T) |

Already LIVE, for contrast: Re-Fly x career (CL-3, CL-4, RB-1), Re-Fly x warp (RF-12W),
Re-Fly x quickload (S4.4), Re-Fly x Rewind-to-Launch (RF-4, RF-14, RF-15), Rewind-to-Launch x
hard career (HC-1, HC-2), loops x Re-Fly / Rewind-to-Launch (S1.5, R1, RF-8, LF-2, GS-12),
loops x CommNet x warp (CN-1, CN-3), Real Spawn Control x warp (RSC-1).

## 3. Defects the code read turned up (verified by an adversarial pass)

Each claim below was traced through its full caller set and checked for a guard elsewhere.
CONFIRMED means no guard was found; none has been reproduced in a test or a flight yet.

### 3.1 CONFIRMED: F9, stock Revert and "Discard Re-fly" skip the route reconcile

- Routes live in memory and are loaded from the save only on a COLD load
  (`ParsekScenario.cs:4464`); an in-session load returns at `:4320` ("returned-revert" /
  "returned-scene-change") before any route code, and the in-session branch never reads the
  `.sfs` ROUTES node that OnSave writes.
- The route reconcile (`RouteRewindClassifier.ReconcileStoreAtRewind`, plus
  `Ledger.RetireFutureRouteActionsAtRewind`) has exactly two callers: the go-back rewind
  (`ParsekScenario.cs:4906-4914`) and the Re-Fly bundle (`ReconciliationBundle.cs:316`).
- Stock Revert only prunes ledger rows (`Ledger.PruneOrphanActionsAfterUT`,
  `ParsekScenario.cs:4096`); F9 prunes nothing (future rows kept under the current-UT
  cutoff); `RevertInterceptor.DiscardReFlyHandler` reloads the Rewind Point quicksave with no
  `RewindContext`, so `HandleRewindOnLoad` never runs.
- Nothing resets `LastObservedLoopCycleIndex` on these loads, and
  `RouteLoopClock.TryGetOwedDockCrossing` requires `dockCycleIndex > lastObserved`; the
  dispatch dedup keys on (route, cycle) without UT (`RouteOrchestrator.cs:5001-5036`);
  `EmitPendingRecoveryCredit` (`:5191-5276`) pays without checking that its dispatch row
  survived.
- Player effect: after F9 the funds rows survive while the cargo reverts (paid, never
  delivered); after a Revert the re-flown cycles are swallowed and a pruned dispatch's
  recovery credit can still pay out (free funds). Design 10.6's "stock revert/load restores
  route state from .sfs" is not what the code does; the "option C" ruling covers discards
  with NO LoadGame, not these paths.
- Test: mostly headless (`RouteLoopDeliveryFireTests` helpers for the swallowed cycle,
  `RouteRecoveryCreditTests` for the orphan credit, a source-text gate for the OnLoad wiring
  like `RouteGoBackRewindReconcileTests`); end to end with existing verbs:
  `SaveGame` -> `RouteCommand send-once` -> `TimeJump` -> `LoadGame` -> `TimeJump` on a career
  route host.

### 3.2 CONFIRMED: a chain-ghosted surface endpoint is permanently re-pointed to a neighbour

- After a rewind the Ghost Chain Rule despawns a base that a committed dock claims
  (`VesselGhoster.cs:75`). The endpoint resolver misses the root-part and pid steps and falls
  through to the surface proximity step (`RouteEndpointResolver.cs:383-445`), which excludes
  only ghost-map vessels and the route's own transports and then calls
  `RouteEndpointTransfer.ApplyTransfers`, a PERSISTED rebind. No Logistics file asks whether
  the endpoint is ghosted by a chain.
- The resolver also runs from the Logistics window draw (`LogisticsWindowUI.cs` callers), so
  just opening the window after the rewind can trigger it. When the base respawns at the
  chain tip with its identity preserved, the route already points at the neighbour's root
  part, which wins at the first resolver step: the redirection is permanent.
- Proximity radius: code 500 m (`RouteOrchestrator.cs:46`); design 7.1 says 50 m, 10.1 says
  500 m (doc drift).
- Test: a pure "endpoint is chain-ghosted -> hold, do not proximity-rebind" predicate beside
  `RouteEndpointTransferTests`; end to end needs a Mun- or Kerbin-surface base with a rover
  parked within 500 m and a committed dock to rewind before.

### 3.3 CONFIRMED (path): a recovery credit is written into the ledger during OnLoad

- `RouteStore.FlushPendingRecoveryCreditOnSourceProblem` (`RouteStore.cs:1885-1911`) reads
  `Planetarium.GetUniversalTime()` directly and never consults
  `ParsekScenario.IsOnLoadInProgress`; `EmitPendingRecoveryCredit` then adds a ledger row and
  credits funds live. The cold-load `RevalidateSources("OnLoad")` (`ParsekScenario.cs:4465`)
  can reach it when a route loads Active with a pending credit and finds a source missing
  (a sidecar failure, a schema-generation reject), at a point where the clock can read 0.
- A row at UT 0 is never removed by the strict-after-cutoff rewind retire, so funds stay
  inflated. This contradicts the stated no-ledger-writes-during-load contract
  (`ParsekScenario.cs:310-321`).
- Test: needs a UT / environment seam (headless, `Planetarium` throws and masks it) or an
  in-game test.

### 3.4 CONFIRMED: multi-stop depot escrow is lost on a scene switch

- `ClearAllEscrow` runs on every scene switch (`ParsekScenario.cs:8365`); the only
  re-establish site (`RouteOrchestrator.cs:1531`) sits behind the no-due-window early return
  in `ProcessMultiStopCrossings`. Between two windows of a dispatched multi-stop cycle a
  competing route can drain the reserved cargo, and window B picks up short after its
  dispatch was already paid. H60 proves escrow only within one scene.
- Test: headless with `RouteCargoEscrowTests` / `RouteEscrowFireTests` (dispatch, clear,
  tick a competitor between windows, expect a hold).

### 3.5 CONFIRMED: a docked visitor receives (or pays) the station's cargo

- The resolver deliberately returns the docked composite (`RouteEndpointResolver.cs:283-310`)
  and `LiveDeliveryWriters` walks every part of the composite in vessel order (`:150-175`,
  `:251-310`), so a visiting tanker's empty tanks fill first and leave with the cargo when it
  undocks; an origin debit can drain a docked visitor instead of the depot.
- Test: needs an in-game test, or a pure part-subset selector (which does not exist yet).

### 3.6 DESIGNED (record, do not fix without a ruling)

- Re-Flying a pre-dock member of a route's backing tree halts the route
  (`MissingSourceRecording` / `SourceChanged`); recovery credit is frozen to creation-time
  ids. Specified (design 0 M-MIS-9, 10.16): the player recreates the route. Harsh, but it
  should get a lane that pins the halt and its explanation.
- Delivered cargo depends on warp rate: both single- and multi-stop routes collapse missed
  cycles and fire once (pinned by `RouteLoopDeliveryFireTests.WarpJump_FiresOnce_SnapsForward`
  and `RouteMultiStopFireTests.WarpPastCyclesAndWindows_FiresEachDueWindowOnce_BumpsOnce`).
  Design 10.7 still says every due cycle is processed: doc drift.
- Inter-body cadence reads the live station orbit and re-baselines on a basis flip (design
  D1); the every-Nth-window anchor resets on a rewind (`RouteRewindClassifier.cs:137`), a
  minor determinism gap design 0.9 does not mention.

### 3.7 REFUTED

- Respawn identity loss for orbital endpoints: the station respawns at its chain tip with
  identity preserved (`VesselGhoster.cs:310`). Residual to check: that the tip recording's
  snapshot is the station half, not the transport.
- Funds gate not reservation-aware: `KspStatePatcher.PatchFunds` writes the
  reservation-aware target into `Funding.Instance`, so the raw read is effectively reserved.

### 3.8 Unverified seams worth a lane

From the same read, not yet adjudicated: chain-tip snapshots that already contain route
cargo from the abandoned future (cargo created from nothing after a rewind); spawns deferred
until warp ends while routes keep ticking (cycles crossing a station's spawn UT held and
lost); no destination-capacity reservation between dispatch and a later window; a kerbal
swapped into a station during a dock inferred Dead or held; the Rewind Point quicksave is
written one frame after the split (a route crossing exactly at a staging event against the
strict-after-cutoff retire); a route loop unit and a foreign Mission partner journey
claiming the same recordings (only a Warn); a ghost CommNet relay serving a station dropping
out during Re-Fly suppression.

## 4. What the harness needs before the integration lanes can gate

1. **A ledger oracle that knows routes and survives a rewind** (the L4 deferral): route
   action types in `oracle.py`, and an expected-ledger model that applies the rewind cutoff
   (rows strictly after the cutoff retired, dormant routes, re-delivery exactly once).
   Without it every route x rewind lane can only pin log tokens.
2. **A mid-run route-state read-back.** The `saveParse` `[expectations.routes]` facet (26
   specs) already reads the PRODUCED save's ROUTES / DORMANT_ROUTES nodes at the end of a run.
   What the reload lanes need in addition is a seam read-back BETWEEN steps (status, cursor
   `LastObservedLoopCycleIndex`, completed cycles, pending recovery credit, endpoint ids)
   right after a `LoadGame` and before the next tick, plus a check that the facet exposes
   the cursor and pending-credit fields at all.
3. **Career route fixtures with a rewind handle**: a career twin of `bdock-recorded` (the one
   committed host that has both a route window and Rewind Points), and a route fixture that
   carries a launch quicksave (unblocks ROUTE-REWIND-TO-LAUNCH-UNREACHABLE).
4. **A moon / inter-body route that can dispatch**: `interbody-route-recorded`'s Paused
   KSC -> Mun route (transit 85,354 s) is the cheapest first live moon dispatch (activate,
   send-once, jump about a day). A Minmus and a Duna/Ike station route need harvesting; a
   career twin of the inter-body fixture is missing entirely.
5. **Real warp in route lanes**: `WarpToUT` exists (16 specs use it, none with a route); the
   new lanes should use it instead of `TimeJump` for at least one dispatch so the loaded
   writer path and KSC / TS ticking are exercised.
6. **OnFlightReady discipline**: REFLY-LANES-JUMP-BEFORE-FLIGHT-READY applies to every new
   route x Re-Fly lane (wait for the re-fly scene before any jump).

## 5. Proposed program

Order: pin the confirmed defects headless first (cheap, red before green), then build the
harness capabilities, then fly the integration lanes from simplest pair to full campaign.

### Phase A - headless red tests for the confirmed defects (no flights)

| Id | Pins | Host |
|---|---|---|
| A1 | F9 / Revert / Discard Re-fly leave cursors ahead -> replayed cycle swallowed; orphan recovery credit paid | `RouteLoopDeliveryFireTests`, `RouteRecoveryCreditTests`, a source-text gate on the three load paths |
| A2 | Chain-ghosted endpoint is not proximity-rebound | new predicate beside `RouteEndpointTransferTests` |
| A3 | Escrow survives a scene switch between windows | `RouteCargoEscrowTests` / `RouteEscrowFireTests` |
| A4 | No ledger write during OnLoad from the credit flush | a UT / in-load seam on `RouteStore` |
| A5 | Delivery writers touch only the endpoint's own parts | a pure part-subset selector + test |

Each lands with its fix (or a ruling that the behaviour is intended, then a doc fix).

### Phase B - two-feature integration lanes (one new axis at a time)

| Id | Combination | Host | Gates |
|---|---|---|---|
| IR-1 | Route x F9 back across a dispatch (career) | `rover-route-career` | funds after reload equal funds at save; one re-dispatch after the reload; cargo at the destination equals one delivery |
| IR-2 | Route x stock Revert to launch mid-cycle (career) | `rover-route-career` + a launch | no swallowed cycle, no orphan credit |
| IR-3 | Route x Re-Fly of ANOTHER tree that rewinds the clock under an active route (career) | career twin of `bdock-recorded` | rows after the cutoff retired; re-delivery exactly once; funds net |
| IR-4 | Route x Discard Re-fly | same | as IR-3, via the Esc > Revert dialog |
| IR-5 | First live moon dispatch | `interbody-route-recorded` (Mun route) | `DispatchDebit` / delivery tokens, station cargo read-back |
| IR-6 | Route under real warp across several cycles, ticking at the KSC and in the TS | `depot-route-recorded` | cycles fired == the design's collapse rule; no duplicate |
| IR-7 | Route x Rewind-to-Launch of the route's own backing tree | route fixture with a launch quicksave | dormant twin dropped; route resumes on re-commit |
| IR-8 | Surface route x ghost-chain rewind with a neighbour rover inside 500 m | Mun or Kerbin surface base fixture | endpoint not rebound; delivery resumes at the respawned base |
| IR-9 | Docked visitor during a delivery | depot or station fixture + a docked tanker | visitor tanks unchanged |
| IR-10 | Multi-stop route with a scene switch between windows and a competing route | `rover-relay-c-recorded` | no short pickup after a paid dispatch |
| IR-11 | Route x career commitments: a committed facility upgrade / contract / strategy competing with route costs in the committed future | `rover-route-career` + KSC actions | stock-screen block still explains the reservation; route holds rather than overdrawing |

### Phase C - three-feature and campaign lanes

| Id | Story | Why |
|---|---|---|
| IC-1 | Inter-body route to a Duna (or Ike) station, rewind across the transit, re-aim windows recomputed, funds and cargo checked by the oracle | the riskiest single chain: re-aim x rewind x ledger x long transit |
| IC-2 | Mun station route + Re-Fly of the station's resupply mission + F9 + warp | stacks 3.1 and 3.2 on one save |
| IC-3 | A long "career campaign" lane: build a Minmus depot, create two routes sharing it, accept a contract, upgrade a facility, Re-Fly an unrelated launch, quickload, warp several cycles, then run the ledger oracle over the whole career | the integration the owner cannot play by hand; one flight that exercises every seam in section 3 |
| IC-4 | Route-driven loop ghosts in FLIGHT after PLAYER-LOOPING-REMOVAL: watch, CommNet relay by the route ghost to a station, Real Spawn Control of an endpoint | the only loop path after the removal |

IC-3 is the target the others build up to; each Phase B lane removes one unknown from it so
a red campaign run names a seam rather than "something in the career broke".

## 6. Bookkeeping

- Filed in `docs/dev/todo-and-known-bugs.md`: the program pointer
  INTEGRATION-COVERAGE-LOGISTICS-REWIND-LEDGER; the confirmed defects
  ROUTE-STATE-NOT-RECONCILED-ON-F9-REVERT-DISCARD, ROUTE-ENDPOINT-CHAIN-GHOST-PROXIMITY-REBIND,
  ROUTE-RECOVERY-CREDIT-WRITTEN-DURING-ONLOAD, ROUTE-ESCROW-LOST-ON-SCENE-SWITCH and
  ROUTE-DELIVERY-INTO-DOCKED-VISITOR; and LOGISTICS-DESIGN-DRIFT-2026-10-06 for the 10.6 text.
- Corrected in the logistics design now: 7.1 records the shipped 500 m proximity radius, and
  10.7 records that loop routes collapse missed cycles after a warp.
- Registered as a program in `docs/dev/autotest-roadmap.md` ("The logistics integration
  program").
- Not stale after all: FIXTURE-DEPOT-ROUTE-RECORDED-LANE-PENDING still owes V18M (the
  FLIGHT-map half), although V18T / H40 / LT-4 fly that fixture.
