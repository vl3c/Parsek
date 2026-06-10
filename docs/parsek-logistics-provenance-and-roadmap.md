# Parsek — Logistics: Provenance Model and Roadmap to Feature-Complete

*Defines the atomic route model (provenance → transfer → disposition), the flow-closure invariant that decides route validity, and the milestone sequence from shipped v0 (Parsek 0.10.x) to a logistics system that is useful, fun, complete, and functional for any route shape the player flies — including resources from other mods, so resource-gathering-to-hubs-to-colonies gameplay works through the transport network.*

**Status:** ACCEPTED — design direction approved, and every implementation claim verified against the code (2026-06-10, post-0.10.1 main). Section 7 records the verification results; the body below states only verified facts.

**Relation to other documents:**
- `parsek-logistics-supply-routes-design.md` remains the canonical logistics design for the SHIPPED v0 system. Its section 0 (route-on-Missions architecture) is authoritative and is NOT changed by this document. This document supersedes its section 17 / 17.1 sequencing (deferred work and supporting systems): the system analysis there stays valid, but the priority order and milestone grouping now live here.
- The Missions subsystem remains LOCKED (section 0.2 of the logistics doc). Nothing in this roadmap edits Missions; every integration is a logistics-side consumer.
- The Tier 1–4 list under Phase 13 in `docs/roadmap.md` is replaced by the milestone sequence in section 4 (the roadmap mirrors it).
- `docs/dev/logistics-origin-ownership-proposal.md` (the undocked-start origin prover, Candidates A–D) is RETIRED by the provenance doctrine in section 2.2. Its lasting contribution — the origin-debit primitive analysis (`LiveOriginDebitWriters`, the `OriginHasCargo` un-stub, the resolver reuse) — is absorbed into milestone M1.

---

## 1. The target gameplay, stated once

The player should be able to fly any supply run they can imagine — any direction of transfer, any number of stops, any origin, any bodies, any resources including modded ones — commit the recording, and have the network either run it as a route or tell them exactly why not, in player language. Networks emerge from composition: a mining base feeds a hub by one route, the hub feeds a colony by another, availability gating chains them. No route shape that a player can physically fly and that the system can witness should be rejected for being an unanticipated shape.

The corollary that keeps this simple rather than sprawling: route validity is decided by ONE rule (flow closure, section 2.4), not by an enumeration of supported mission profiles.

---

## 2. The atomic route model

### 2.1 The atom

A route is, atomically:

```
provenance events  →  transfer events  →  disposition
(where cargo came  (dock windows where  (what happened to
 from, witnessed)   cargo moved)         the transport)
```

Origin is CAUSAL, not positional. The recording's start location is irrelevant to cargo accounting; what matters is the witnessed event that put each unit of cargo on the transport. A tug that lives docked at a station, flies empty to a refinery, loads, returns, and delivers has the refinery as the origin of the delivered fuel — not the station where the recording started.

### 2.2 Provenance taxonomy

Every unit of cargo on the transport has exactly one of three witnessed provenances:

1. **Launch cargo** — the transport left the launch site with it. Debit: KSC dispatch funds (Career) per the shipped `KscDispatchFundsCost` path; no physical debit. SHIPPED in v0. Verified property: the per-cycle funds charge (`RouteOrchestrator.ComputeDispatchFundsCostForRoute` → `RouteFundsCalculator.ComputeDispatchFundsCost`) walks the FIRST source recording's vessel snapshot, which is captured at that recording's STOP — the dock chain boundary for a `[launch..dock]` transport recording. So the resource term is what is still aboard at dock arrival (the launch load minus pre-dock transit burn) plus parts dry cost (`unitCost × amount` per resource). Cargo loaded in LATER windows happens after that snapshot, so it is structurally outside the funds charge — flow accounting cannot double-charge it; the only open funds question is the charge BASIS (dock-arrival contents vs the launch manifest), decided in M2 once full-run manifests exist (section 2.4).
2. **Loaded** — a recorded connection window in which cargo flowed FROM another vessel ONTO the transport (the reverse of v0's delivery direction). Debit: that source vessel's tanks/inventory, per cycle. Requires the transfer-direction model (M3) and the origin-debit primitive (M1: `LiveOriginDebitWriters` + un-stubbing `LiveRouteRuntimeEnvironment.OriginHasCargo`, today `false` with reason `non-ksc-origin-unsupported-in-v0`).
3. **Harvested** — recorded ISRU activity during the run (drills, converters) producing cargo on the transport between windows. Debit: none; the environment provided it. NEW capture work (M2). Verified: the existing snapshots do NOT bracket this — `RouteOriginProof` start/end transport manifests are only populated for start-docked runs (`RouteProofCapture.BuildStartRouteOriginProof` requires an externally-parented partner at recording start), and nothing in the recorder tracks `ModuleResourceHarvester` / `ModuleResourceConverter` activity. Harvested provenance therefore needs (a) full-run transport-scoped start/end manifests captured for ALL runs, not just start-docked ones, and (b) a harvest attribution surface (either a harvest-window capture analogous to `RouteConnectionWindow`, or per-window bracketing that pins each between-window gain to harvester operation). Without attribution, an untracked transport gain is indistinguishable from a glitch and must fail closure.

**Undocked-start is not a provenance.** A recording that begins with a full transport not docked to anything, where the cargo's source was never witnessed, is an incoherent route shape, not a missing feature. The resolution is a workflow rule surfaced in the rejection reason: start the supply run docked to the origin (making it a Loaded provenance via the start-docked window / `RouteOriginProof.StartDockedOriginVesselPid`), or record the mining (Harvested), or accept it as launch cargo if launched from KSC. The "undocked-start origin prover" deferred-work item (design doc section 17; `logistics-origin-ownership-proposal.md`) is RETIRED by this rule — there is nothing to prove. Verified: the rejection-reason mechanism already carries player-language guidance — `RouteAnalysisStatus` (enum) maps through `RouteCreationFormatters.FormatRejectMessage` and `LogisticsRejectPresentation.DescribeNearMiss` into the Logistics window's near-miss list, so the workflow rule ships as one new `RouteAnalysisStatus` value plus formatter text.

A note kept from the retired proposal: a tanker that DECOUPLES from a base (separation-anchored departure) is witnessable in principle — the cargo sat on the base's combined vessel at the separation frame. If players hit the undocked-start rejection in practice and the workflow rule proves too strict, the revisit path is a witnessed "separated-with" loading variant anchored to the controlled-decouple event, not a positional prover. Deliberately out of scope for every milestone below.

### 2.3 Disposition taxonomy

What happens to the transport after its last transfer window:

1. **Recovered** — lands on Kerbin, recovery credit applies. SHIPPED (constant-deferred recovery credit, `RouteRecoveryCredited` ledger row); precise per-run landing timing is an M6 refinement.
2. **Expended** — destroyed, crashed, discarded in atmosphere, deorbited. Nothing owed, nothing credited beyond what already applies. Clean.
3. **Persisting** — the transport remains in the world (stays docked, lands and stays, parks in orbit). DOCTRINE: **route transports never materialize.** A route's only real effects are ledger entries and cargo mutations; replaying a persisting disposition per cycle would mean a real vessel accumulating per cycle, which is vessel spawning, not logistics. A run whose transport persists is still a valid route — the transport's ghost simply ends with the rendered segment and no vessel spawns. Verified: no code path spawns a real vessel from a route's backing recording. The guarantee is architectural — `RouteBackingMission.ComputeExcludedIntervalKeys` excludes the post-dock tail from the route's `LoopUnit`, so excluded recordings never render and never reach the playback-completion event that feeds `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd`; the spawn decision itself has no route-aware gate and does not need one. A cheap regression pin (assert route-driven loop units never produce a spawn decision) is an M4 test task, since multi-stop widens the rendered window.

Module delivery that stays docked (delivering station segments, base modules) is therefore NOT a route in this model — it is remote vessel construction, a separate deferred feature ("transfer kind = the vessel itself"). The atom accommodates it later; it is deliberately out of scope here.

### 2.4 The flow-closure invariant (route validity in one rule)

> Per resource (and per exact inventory payload item): launched + Σ loaded + Σ harvested = Σ delivered + consumed + residual, where every loaded/delivered term is a witnessed connection window and every harvested term is a witnessed gain. A recording whose flows close is a valid route; one whose flows do not close is rejected with the specific unaccounted window/quantity named.

Notes:
- The per-window scoped manifests already state how much flowed at each window (matched transport-loss / endpoint-gain via `RouteAnalysisEngine.BuildResourceDeliveryManifest`, and the reverse for loading). There is NO allocation problem and NO proportional-split heuristic: attribution is read off the witnessed windows, summed, and checked for closure.
- Consumption between windows draws from the onboard pool as fungible. Debits are defined by what left each source at its window, not by which units were later burned; fungibility never needs resolving.
- `consumed` and `residual` come from full-run transport-scoped start/end manifests, which M2 generalizes to all runs (today they exist only on start-docked `RouteOriginProof`).
- Cost restatement, verified against the code: the per-cycle funds charge never sees loaded-en-route cargo (it walks the first source recording's end-of-recording snapshot, taken at the dock boundary; see 2.2 item 1), so flow accounting does not risk double-charging loaded cargo as funds — the loaded windows debit their physical sources instead. Against the closure equation, the charge's resource term is dock-arrival contents, i.e. the launched term MINUS pre-window consumption (today's charge under-counts burned transit fuel relative to old design §5.2's stated "parts plus used/delivered" intent); once M2 lands full-run manifests, the basis can be restated cleanly on the launched term or deliberately kept as-is — an M2 decision, not a blocker. One correction to the old design doc: the shipped `CostManifest` is NOT the full-run positive-decrease described in design §5.2; `RouteBuilder` copies the window's delivery manifest into it. Under flow accounting `CostManifest` is replaced by the per-provenance terms of the closure equation (launched / per-window loaded / harvested), and the funds charge keys on the launched term (subject to the basis decision above).
- This rule is simultaneously the analysis spec and the player-facing error message. Every rejection names the window or quantity that fails closure, through the same `RouteAnalysisStatus` → formatter → near-miss surface verified in 2.2.

### 2.5 Multi-origin runs (ALLOWED)

Load 100 at depot A, 200 at depot B, deliver 300 at the station — valid. Flow accounting handles N loaded-provenance windows natively. Two semantics are locked now:

1. **All-or-nothing dispatch.** A cycle dispatches only when EVERY provenance source can cover its recorded contribution; otherwise the route holds in `WaitingForResources` naming the short source. No partial dispatch: the recording transferred what it transferred; delivering less is a different mission than the one witnessed. (Replay fidelity is the epistemic basis of the whole system.) Verified reconciliation with the shipped destination side: the implemented per-cycle gate order (`RouteDispatchEvaluator.CheckEligibility`) is sources-valid → endpoint resolution → origin cargo → funds → destination capacity, where a destination with ZERO capacity blocks the cycle (`DestinationFull`) and a partially-full destination does not — the per-resource clamp happens at delivery time in `RouteDeliveryPlanner.PrepareDelivery`. All-or-nothing source gating slots into the existing origin-cargo step (every source must cover its recorded outflow); the destination behavior is unchanged (zero capacity blocks, partial capacity clamps and wastes). Note the old design doc §6.1 listed destination before origin; the code order above is the contract.
2. **Reserve at dispatch, debit at window phase.** All sources are gated together at dispatch; the physical debit for each loaded window applies when the route loop clock crosses that window's recorded phase (the loading analogue of DEL-2). Between dispatch and the window phase, the contribution is escrowed against the source so a competing route cannot drain it mid-run and strand an in-flight cycle. Verified host decision: the ledger reservation machinery is funds-specific (balance-projection inside `FundsModule`; generalizing it to per-resource-per-vessel would mean a parallel projection walker for one use case), so cargo escrow is a lightweight `RouteStore`-owned reservation map (route id → vessel pid → held amounts), allocated when the cycle's sources are gated, released at each window's debit or on cycle abort, reverted with the route rows on tombstone. Timing note, verified: in the shipped loop-route path `RouteOrchestrator.EmitLoopCycle` fires the debit at the DOCK-PHASE crossing (not at cycle start), so for a single-window route there is no dispatch-to-debit gap today; the escrow window first becomes real with multi-window runs (M3/M4), which is when the reservation map lands.

Consequence accepted: **a crashed disposition still debits.** If the run loaded 200 from A and ends in a crash, A loses 200 per cycle — the outflow was witnessed; the cargo's fate afterward is the route's loss, not the depot's refund. The route UI's per-cycle flow display and hold reasons (M6) make a bleeding route visible.

---

## 3. Scenario families the atom covers

| # | Scenario | Provenance | Needs |
|---|----------|------------|-------|
| 1 | Pad-to-base rover; KSC-to-orbit resupply | Launch cargo | SHIPPED |
| 2 | Hub-and-spoke chains (KSC→LKO depot→Mun station→Mun base) | Launch + Loaded | Origin-debit primitive (M1) |
| 3 | Shuttle/tanker (station↔refinery, one recording) | Loaded mid-run | Direction model + multi-window + flow attribution (M3/M4) |
| 4 | Mining route (drill during run, deliver) | Harvested | Harvest provenance (M2) |
| 5 | Sell-at-KSC (pick up at depot, fly home, recover with cargo) | Loaded → Recovered | Pickup (M3) + existing recovery credit. Verified: the recovery credit is the delta in trajectory-point funds, i.e. KSP's own stock recovery valuation, which already includes onboard resources — recovered cargo IS credited. A test, not a system. |
| 6 | Multi-origin consolidation (A + B → station) | N× Loaded | Flow attribution + section 2.5 semantics (M3/M4) |
| 7 | Interplanetary any-of-the-above | any | Re-aim seam activation (M5) |
| 8 | Modded-resource versions of all of the above | any | Resource generality (M2) |

Out of scope by doctrine: persisting-transport accumulation (section 2.3), module/vessel delivery (remote construction, deferred), crew delivery (cannot reuse the cargo path; orthogonal to the resource network; deferred), claw/grapple and fuel-line producers (open-ended KSP API investigation; docking covers the proof mechanic; revisit on demand), undocked-start origin proving (retired, section 2.2).

---

## 4. Milestones

Ordered by gameplay-per-effort. The two shared foundations from logistics doc 17.1 (transfer-direction model; multi-window model) are built as explicit milestones, each paid for by the first feature that needs it. Milestone 6 runs partly in parallel with all others.

### M1 — The network unlock: non-KSC origin debit

The single highest-value item: turns isolated deliveries into a network, because hub-and-colony gameplay in delivery-only form needs nothing else. Chained routes (logistics doc A.3) start working; `WaitingForResources` already gates route B on route A having fed the hub; the network emerges from composition.

- Build `LiveOriginDebitWriters` (the mirror of destination `LiveDeliveryWriters`: same loaded `PartResource.amount` / unloaded `ProtoPartResourceSnapshot` write split, removing from the origin), plus the ledger-backed physical `RouteCargoDebited` applier path through `RouteModule` so the debit is revert-safe (rewind recompute + re-fly tombstone), exactly like the funds debit.
- Un-stub `LiveRouteRuntimeEnvironment.OriginHasCargo` (today: `false` with reason `non-ksc-origin-unsupported-in-v0`): resolve `route.Origin` through the existing `RouteEndpointResolver` call already wired in `RouteDispatchEvaluator.CheckEligibility`, sum the live origin manifest, compare against the route's recorded outflow.
- Docked-start origins only — `RouteOriginProof.StartDockedOriginVesselPid` is already captured and serialized; this milestone makes such routes dispatch. Known capture gap to fix here: `RouteBuilder` leaves the docked origin's `RouteEndpoint` with `IsSurface = false` and zeroed coordinates, so resolution is PID-only; populate the descriptor (situation + coordinates at recording start) so surface-base origins get the same rebuild fallback destinations have.
- Add the undocked-start workflow rejection: a new `RouteAnalysisStatus` value whose formatter text states the rule (start docked to the origin / record the mining / launch from KSC), replacing the retired prover item.
- Pull **dispatch priority** forward from the old Tier 1 into M1: verified, today's ordering is not even FIFO-by-`NextDispatchUT` — `RouteOrchestrator.Tick` processes routes in `RouteStore` commit-list order, so contention at a shared hub would resolve by creation order. Add a persisted priority field on `Route` (additive codec) and a deterministic comparator (priority, then `NextDispatchUT`, then route id) applied to the tick snapshot.
- Build the **scenario-lifecycle test harness** (old Tier 4; open todo item "Logistics scenario-lifecycle coverage relies on source-text gates") alongside M1, not after: origin debit is the first feature mutating non-KSC vessel state per cycle (loaded AND unloaded vessels) and deserves lifecycle-level `OnSave`/`OnLoad` tests rather than grep gates.

### M2 — Resource generality: mod resources + harvest provenance

Earlier than the old tier order implied, because the colony gameplay lives in mods (MKS, USI-LS, EL RocketParts, Karbonite) and the capture path is resource-agnostic by construction. Verified audit result: the pipeline has NO stock-name behavioral assumptions — the only hardcoded resource names are the intentional `ElectricCharge` / `IntakeAir` exclusions (mirrored in `RouteAnalysisEngine.IsIgnoredResource` and `VesselSpawner.ExtractResourceManifest`), manifests are name-keyed dictionaries, and `LiveRouteRuntimeEnvironment.LookupResourceUnitCost` resolves ANY `PartResourceLibrary` definition (modded included), returning 0 for undefined names. So M2 is fixtures + the stated rule + one new provenance kind:

- **Transferability rule, stated:** any resource with a `PartResourceDefinition` is routable; ElectricCharge and IntakeAir stay excluded as environmental noise; an undefined resource name in a snapshot is excluded and logged. Zero-cost defined resources route normally (they simply contribute 0 to the funds charge).
- Community Resource Pack test fixtures (manifest capture, analysis, delivery writers, funds cost with modded `unitCost`) — covers most of the mod ecosystem in one move.
- **Harvested provenance** (section 2.2 item 3): generalize the full-run transport-scoped start/end manifest capture to all runs (today start-docked only), add the harvest attribution capture (harvest windows or bracketed gains pinned to harvester activity), and teach the analysis engine to admit witnessed harvested gains into closure.
- **Stated non-goal, in the doc explicitly:** background production. Stock runs no converters on unloaded vessels; logistics reads what is physically in the tank at dispatch evaluation (already true). Whether tanks refill between cycles is the player's problem or a background-processing mod's (Phase 16 mod compat). Without this sentence, the first MKS user files it as a logistics bug.

### M3 — Direction generality: pickup, then mixed windows

The generalized transfer-direction model, paid for by pickup:

- Source-side connection capture (mirror of delivery capture: cargo left the ENDPOINT part set and arrived on the TRANSPORT part set across the window — the same `RouteConnectionWindow` scoped-manifest mechanics, opposite sign).
- Source availability probe (mirror of `LiveDeliveryCapacityProbe`).
- Reverse-direction writers (debit endpoint, credit transport — at window phase per section 2.5; note the loop path already fires effects at the dock-phase crossing via `EmitLoopCycle`).
- `RouteAnalysisEngine.HasResourcePickup` stops rejecting reverse flow (`MixedPickupDelivery`) and classifies it; flow accounting (section 2.4) replaces the assume-start-loaded analysis.
- Exact stock-slot / `InventoryPayloadItem` identity tests extended to the pickup direction.
- Then mixed pickup/delivery windows: per-window bidirectional manifest in `RouteConnectionWindow`, two-direction applier in `RouteOrchestrator` at one dock crossing.
- Multi-origin semantics (section 2.5) land here: all-or-nothing source gating in `RouteDispatchEvaluator` + the `RouteStore` cargo-escrow reservation map.

### M4 — Shape generality: multi-stop, then round-trip

The multi-window model, paid for by multi-stop:

- `RouteAnalysisEngine` accepts and orders N windows (today both analysis entry points return `MultipleConnectionWindows` on a second completed window); `RouteLoopClock`-driven delivery fires at EACH window's recorded phase; `RouteEndpointResolver` resolves one endpoint per stop. `Stops` / `SegmentIndexBefore` / `DeliveryOffsetSeconds` are already reserved in the save shape, so codec cost is small.
- Round-trip immediately after, per the resequencing note in 17.1: with direction (M3) and multi-window (M4) built, round-trip is a thin `LinkedRouteId` chain-constraint scheduler (A completes, then B dispatches), not a system.
- Missions-boundary verification result: in always-tree mode a dock ALWAYS splits recordings (the dock-merged child is a separate recording), so multi-stop END-trims — render `[launch..dockB]`, skipping docked stretches at A — are expressible with today's interval boundaries; `RouteBackingMission.ComputeExcludedIntervalKeys` already generalizes (exclude everything at/after the LAST delivery dock). What remains out: shapes that START mid-recording ("undock → undock" shuttle runs whose run begins inside a pre-dock recording) hit the locked Missions layer's gap 1 (dock is not an interval boundary INSIDE a recording; `design-mission-abstractions.md` "Docking & undocking (v1)"). That is a documented limitation surfaced in the rejection reason, NOT a Missions edit.
- Regression pin from 2.3: assert route-driven loop units never produce a real-vessel spawn decision, now that the rendered window widens past the first dock.

### M5 — Reach: inter-body activation

Wire the existing no-op seam: `RouteLoopClock` already threads the backing unit's `RelaunchSchedule` + `LoiterCuts` into `GhostPlaybackLogic.TryComputeSpanLoopUT` (logistics doc 0.9). Enable routes whose backing Mission carries the Missions-layer synodic / re-aim schedule; delivery fires on the same re-aimed launch UTs the ghost renders on. Post-v0 cadence rule applies: `CadenceMultiplier` becomes a modulo on the scheduled-launch index rather than a multiplier on a fixed interval (already specified in 0.9). No new solver; the locked Missions layer owns the math. This completes "any route the player can fly, the network can run."

### M6 — "Simple and it works": legibility (parallel track)

Half of "no matter what the player does, it should work" is never leaving the player staring at a silent route.

- **Every non-dispatching route says why, in player language, in the Logistics window.** Partially shipped: candidate near-misses already surface reasons (the `LogisticsRejectPresentation` near-miss list); the remaining work is the same treatment for LIVE route hold states (`WaitingForResources` naming the short source, `DestinationFull` naming the full resource, flow-closure rejections naming the unaccounted window per 2.4).
- Structure list window (old Tier 1; reads already-recorded data — `TrackSections` / `SegmentEvents` / `PartEvents` / `RouteConnectionWindow` / `Stops`).
- Candidate intent helper (per-tree flag consumed by `RouteCandidateFinder`).
- Precise per-run recovery landing (second recovery clock keyed on recorded recovery UT, replacing the constant deferred credit; recovery-credit plan OQ1).
- Map-view route lines (design doc §17 "Map view integration" deferral — never in the old tier list; schedule opportunistically).
- Per-cycle flow display on the route row (what each cycle debits where and delivers where — the visibility that makes the crashed-disposition bleed of section 2.5 a player choice rather than a surprise).

### Milestone → old tier mapping

| Old tier item | Milestone |
|---|---|
| Dispatch priority | M1 (pulled forward) |
| Scenario-lifecycle test harness (Tier 4) | M1 (pulled forward) |
| Non-KSC origin debit (docked) | M1 |
| Non-KSC origin debit (undocked-start prover) | RETIRED (section 2.2) |
| Pickup routes; mixed windows | M3 |
| Multi-stop; round-trip linking | M4 |
| Inter-body re-aimed routes | M5 |
| Structure list window; candidate intent helper; precise recovery landing | M6 |
| Map view integration (§17 deferral, not in the tier list) | M6 |
| Non-docking connection producers; crew delivery | Out of scope by doctrine (revisit on demand) |
| "Dispatch now" | Already RETIRED (subsumed by Send Once), unchanged |
| Dock-side-baseline edge case | Already SHIPPED in 0.10.1, unchanged |

---

## 5. Completeness test

After M5, "complete" has a concrete form: any committed recording whose flows close (section 2.4) becomes a route — any provenance mix, any number of windows in either direction, any stops, any bodies, any defined resources — and any recording whose flows do not close is rejected with the specific unaccounted window named. Claw/grapple, crew delivery, persisting-transport materialization, and undocked-start origin proving remain out by doctrine, each with a stated reason, not by accident.

---

## 6. Doctrine summary (the rules that keep this small)

1. **One validity rule:** flow closure (2.4), not a profile enumeration.
2. **Origin is causal:** provenance is the witnessed event, never the start position (2.1–2.2).
3. **Undocked-start is a workflow error,** not a feature gap (2.2).
4. **Route transports never materialize** (2.3).
5. **All-or-nothing at the sources, clamp at the destination** (2.5).
6. **Reserve at dispatch, debit at window phase;** a crashed run still debits (2.5).
7. **Any defined resource routes;** background production is explicitly not logistics' problem (M2).
8. **The locked Missions layer owns periodicity;** logistics activates seams, never edits Missions (M5).
9. **Every non-dispatching route names its reason in player language** (M6).

---

## 7. Verification results (2026-06-10)

The DRAFT carried ten `VERIFY` items; all were checked against the code (post-0.10.1 main). Verdicts, with the corrections folded into the body above:

1. **Harvest bracketing (2.2):** NOT covered by existing snapshots. `RouteOriginProof` start/end transport manifests exist only for start-docked runs (`FlightRecorder.CaptureStartRouteOriginProofIfDocked` → `RouteProofCapture.BuildStartRouteOriginProof`); no ISRU module activity is recorded anywhere in the recorder. M2 adds full-run manifests for all runs + a harvest attribution capture.
2. **Rejection guidance (2.2):** CONFIRMED. `RouteAnalysisStatus` → `RouteCreationFormatters.FormatRejectMessage` (`Source/Parsek/Logistics/RouteCreationFormatters.cs`) → `LogisticsRejectPresentation.DescribeNearMiss` → the Logistics window near-miss list. New reasons are one enum value + formatter text.
3. **No-spawn (2.3):** CONFIRMED, architecturally. Route-excluded recordings never become `LoopUnit` members (`RouteBackingMission.ComputeExcludedIntervalKeys` → `MissionIntervalSelection.ComputeRenderWindows`), so they never reach a playback completion that feeds `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd`. No route-aware spawn gate exists or is needed; M4 adds a regression pin.
4. **Cost restatement (2.4):** CONFIRMED in conclusion, corrected in mechanism. The funds charge (`RouteOrchestrator.ComputeDispatchFundsCostForRoute` → `RouteFundsCalculator.ComputeDispatchFundsCost`) walks the FIRST source recording's vessel snapshot, captured at that recording's stop — the dock chain boundary for a `[launch..dock]` run — so the resource term is dock-arrival contents (launch load minus pre-dock burn), not the launch manifest. The no-double-charge conclusion holds: later-window loaded cargo is structurally outside the charge. Corrections: the shipped `CostManifest` is a copy of the window delivery manifest (`RouteBuilder`), not the design doc §5.2 full-run decrease, and §5.2's "parts plus used/delivered" funds wording overstates the resource term; flow accounting replaces `CostManifest` with per-provenance terms and decides the charge basis in M2.
5. **Gating order (2.5):** Reconciled against the IMPLEMENTED order — `RouteDispatchEvaluator.CheckEligibility`: sources → endpoint → origin cargo → funds → destination capacity. Zero-capacity destination blocks the cycle; partial capacity clamps per-resource at delivery (`RouteDeliveryPlanner.PrepareDelivery`). All-or-nothing source gating extends the origin step; destination behavior unchanged. (Old doc §6.1's destination-before-origin order is stale.)
6. **Escrow host (2.5):** Lightweight `RouteStore`-owned per-vessel reservation map. The ledger reservation machinery is funds-specific (`FundsModule` balance projection) and not worth generalizing for one consumer. Also verified: the loop path debits at the dock-phase crossing (`RouteOrchestrator.EmitLoopCycle`), so escrow only becomes live with multi-window runs.
7. **Recovery credit values cargo (scenario 5):** CONFIRMED. The credit is the recording's trajectory-point funds delta at recovery (`LedgerOrchestrator` recovery pairing; summed by `RouteRunCostCalculator.SumRecoveredCredits`), i.e. KSP's stock recovery valuation, which includes onboard resources.
8. **Stock-name audit (M2):** PASSES. Only `ElectricCharge` / `IntakeAir` are hardcoded, as intentional exclusions; manifests are name-keyed; `LookupResourceUnitCost` resolves any `PartResourceLibrary` definition (modded included), 0 for undefined.
9. **Missions dock-boundary gap (M4):** End-trims `[launch..dockN]` are expressible today (docks always split recordings in always-tree mode); mid-recording START-trims (undock→undock shuttle shapes) hit gap 1 and stay a documented limitation.
10. **Name cross-check:** All symbols verified as named, including `OriginHasCargo` + `non-ksc-origin-unsupported-in-v0`, `StartDockedOriginVesselPid`, `HasResourcePickup`, `TrySendOneCycleNow`, `CadenceMultiplier` / `ApplyMultiplier`, `Stops` / `SegmentIndexBefore` / `DeliveryOffsetSeconds` / `LinkedRouteId`, the `RouteLoopClock` → `TryComputeSpanLoopUT` schedule passthrough, `RouteRecoveryCredited` / `RouteDispatched` / `RouteCargoDebited` / `RouteCargoDelivered`, and the `RouteStatus` enum (matches design §4.5). One naming correction: the connection window carries `DockUT` / `UndockUT`; `RecordedDockUT` lives on `Route`. One behavioral correction: competing-route ordering is `RouteStore` commit-list order, not FIFO-by-`NextDispatchUT` as the old doc's 10.12/10.13 claim — folded into M1's dispatch-priority rationale.
