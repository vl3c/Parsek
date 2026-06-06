# Logistics: Non-KSC Origin Ownership Proposal (undocked-start origins)

Status: design proposal, deferred (post-v0). Not started.

Remaining-work item: roadmap.md "Non-KSC origin cargo debit, docked + undocked (M/L)" (roadmap.md:414). Context: design doc section 17.1 (supporting systems required for future work) and the section 17 deferral "Non-KSC undocked-start origins" (docs/parsek-logistics-supply-routes-design.md:1389).

This note answers one open design question: how should a route prove and handle ORIGIN OWNERSHIP for a non-KSC, undocked-start origin, WITHOUT inventing an abstract "warehouse"? It is grounded in how the v0 logistics code on the logistics-v0-implementation line actually works, not in the design doc's aspirational wording. File:line references are to that source tree.

---

## 1. Problem statement

### 1.1 What v0 actually does (grounded in the code, not the doc's aspirations)

The design doc describes two origin kinds, but the code as merged only fully implements one of them (KSC funds), and the "docked-depot physical debit" is in fact a captured-but-stubbed path. This is the single most important thing to get right before designing forward.

Origin classification happens in `RouteBuilder.BuildRoute` (Source/Parsek/Logistics/RouteBuilder.cs:305-354):

- KSC origin: `originRec.LaunchSiteName` non-empty AND `StartBodyName == "Kerbin"`. Sets `Origin.VesselPersistentId = 0`, `IsKscOrigin = true`.
- Non-KSC origin: requires `originRec.RouteOriginProof != null && RouteOriginProof.StartDockedOriginVesselPid != 0`. Sets `Origin.VesselPersistentId = StartDockedOriginVesselPid`, `IsSurface = false`, lat/lon/alt = 0 (with a comment: "depot vessel coords are not captured ... scheduler resolves them from the live vessel at dispatch time").
- Anything else: `RejectReason = "endpoint-missing"`.

The KSC funds path is real and complete. Per cycle, `RouteOrchestrator.EmitDispatchDebit` (Source/Parsek/Logistics/RouteOrchestrator.cs:1032 and following) computes `ComputeDispatchFundsCostForRoute` (parts plus resources via `RouteFundsCalculator.ComputeDispatchFundsCost`, costed through `PartLoader` / `PartResourceLibrary` in `LiveRouteRuntimeEnvironment.LookupPartCost` / `LookupResourceUnitCost`), writes a `RouteCargoDebited` row carrying `RouteKscFundsCost`, and `FundsModule.ProcessRouteCargoDebited` (Source/Parsek/GameActions/FundsModule.cs:533) subtracts it from the running balance. There is even a deferred recovery-credit system (`RouteRunCostCalculator`, `RouteRecoveryCredited`, `EmitPendingRecoveryCredit`) that mirrors the stock recovery credit.

The non-KSC physical debit is NOT implemented. The eligibility gate `LiveRouteRuntimeEnvironment.OriginHasCargo` (Source/Parsek/Logistics/LiveRouteRuntimeEnvironment.cs:87-116) returns, for any non-KSC route:

```
lackingResource = "non-ksc-origin-unsupported-in-v0";
return false;   // holds the route in WaitingForResources forever
```

The class XML doc states this explicitly: "non-KSC origins return false with reason 'non-ksc-origin-unsupported-in-v0' so those routes hold in WaitingForResources until live origin-cargo gating ships (post-v0)." A grep of the entire Source/Parsek tree for any origin-debit writer (`DebitOrigin`, `WriteOriginResource`, `originVessel`, and similar) returns no file matches. `LiveDeliveryWriters` only ever writes to the destination. So today a non-KSC route can be created (the proof is captured and serialized) but can never dispatch.

What IS built for the docked origin is purely the proof-capture plus persistence:

- `RouteProofCapture.BuildStartRouteOriginProof` (Source/Parsek/RouteProofCapture.cs:138) detects the start-docked partner via `TryResolveStartDockedOriginPartner` (a pure resolver over "externally parented parts", that is parts whose `part.parent.vessel != activeVessel`), requiring exactly one distinct non-zero, non-PRELAUNCH partner PID, else it classifies the skip reason (`NoExternalCoupling`, `ActiveVesselPrelaunch`, `PartnerAmbiguous`, and so on).
- It captures `StartDockedOriginVesselPid` plus start/end transport resource and inventory manifests scoped to the transport part-PID set, stored on `Recording.RouteOriginProof` (Source/Parsek/Recording.cs:270).
- `RouteProofHasher` folds `StartDockedOriginVesselPid` into the proof hash; `RouteEndpointResolver.TryResolveEndpoint` can already resolve a non-KSC origin by PID (and a surface fallback exists, though the docked-origin path sets `IsSurface = false`).

So the real v0 ownership model is: KSC equals funds, proven by launch-site name; docked depot equals proof captured and PID-resolvable, but the per-cycle debit is a stub. The recorded deferral is at docs/parsek-logistics-supply-routes-design.md:1389:

> Non-KSC undocked-start origins: v1 non-KSC routes require the Supply Run to start docked to a real origin depot. Common patterns where a tanker launches from a Minmus surface base, drives/flies away undocked, and later docks to a destination are deferred until origin ownership can be proven without inventing a warehouse.

### 1.2 Exactly why the undocked start breaks the ownership/debit model

The KSC path and the docked-depot path each have a concrete thing the recurring debit is anchored to, captured at recording time and re-resolvable each cycle:

- KSC: anchored by the launch-site name on the origin recording (`LaunchSiteName` plus `StartBodyName == Kerbin`). The per-cycle debit hits the treasury (`Funding.Instance`), an abstract-but-stock global resource KSP already owns. No re-resolution is needed because funds are global.
- Docked depot: anchored by a docking joint at recording start, which yields a single externally-parented partner PID (`StartDockedOriginVesselPid`). The per-cycle debit (intended) hits the partner vessel's tanks/inventory, resolved by PID via `RouteEndpointResolver` each cycle (PID lookup plus surface-proximity fallback).
- Undocked start: anchored by nothing. No joint, no partner PID, no launch site. The per-cycle debit target is unknown: there is no captured vessel to debit from. Impossible with v0 data.

The break is specifically:

1. No ownership anchor is captured. `RouteProofCapture` resolves the origin partner only from a docking joint ("externally parented parts"). A tanker that launched/separated and drove away has no externally-parented part at recording start, so `TryResolveStartDockedOriginPartner` returns `NoExternalCoupling` and no `RouteOriginProof` is written at all. The route then fails creation with `"endpoint-missing"`. There is simply no field that names the base.

2. No re-resolvable identity for the source. Even if we hand-waved "the base the tanker came from," a base is not identified by anything stable in the recording. KSP `persistentId` is craft-baked and not launch-unique (see the CLAUDE.md gotcha), so a bare PID is not proof of "same physical base." A surface base is identified in v0 only by the endpoint resolver's `(body, lat, lon, alt)` plus situation, and the tanker's recording captures the tanker's departure trajectory, not the base's identity.

3. The "what to debit" is decoupled from "where it came from." In the docked case, the cargo physically left a joint-connected partner, so the cost manifest provably came out of that partner. In the undocked case, the cargo was already in the tanker's tanks at launch. We can see what the tanker carried (the start transport manifest), but we cannot prove the base ever held it, so debiting the base each cycle would be inventing cargo the base may never have produced. That is precisely the "no infinite cargo glitch" / "stock proof-of-work" violations the design forbids (principles 4-5).

The deferral phrase "without inventing a warehouse" means: do not introduce an abstract infinite (or self-refilling) source keyed to a location. Any acceptable mechanism must debit a real, persistent, resolvable stock vessel whose stock-tracked resources actually go down.

A secondary but real blocker: even the docked-depot debit it would mirror is itself unbuilt, so whatever we pick must also be the thing that finally builds the live origin-debit writer (the mirror of `LiveDeliveryWriters` for the origin side, plus replacing the `OriginHasCargo` stub).

---

## 2. Candidate mechanisms

All four assume we first build the missing origin-debit primitive: an origin-side analogue of `LiveDeliveryWriters` (call it `LiveOriginDebitWriters`) that, given a resolved origin Vessel (loaded or unloaded) and the route's `CostManifest` / `InventoryCostManifest`, removes resources/inventory from that vessel using the same loaded (`Part.TransferResource`) versus unloaded (`ProtoPartResourceSnapshot.amount`) split the destination writer already uses, plus a `RouteCargoDebited`-physical applier path through the ledger (`RouteModule` or a new resource module) so it is revert-safe per design principle 11 and edge case 10.6. This primitive is shared by every candidate below; the candidates differ only in how the origin vessel is proven and resolved. Note: the KSC funds path does not need this primitive; it debits the treasury. The candidates concern only physical, non-KSC origins.

### Candidate A: launch/separation-anchored owner (recorded source vessel equals owner)

Proves ownership by: the recorder captures the vessel the tanker was physically attached to at the launch/separation event, that is, the same "externally-parented partner" detection that exists today, but anchored to the decoupler/launch-clamp/separation event instead of a dock joint. KSP fires `onPartUndock` / `onPartJointBreak` / decoupler events; a tanker that "separates from a Minmus base" does have a joint to the base an instant before separation. The recorder already tracks parent-anchored relationships extensively (`ParentAnchorRecordingId`, controlled-decoupled children; see CLAUDE.md). The base PID is the parent vessel's PID at the separation frame.

Debits per cycle, from where: the resolved base vessel's tanks/inventory (the `CostManifest`), via `LiveOriginDebitWriters`. Identical mechanics to the intended docked-depot debit; only the capture trigger differs (separation event versus start-already-docked).

Reuses/mirrors: `RouteProofCapture.TryResolveStartDockedOriginPartner` is reused almost verbatim. The candidate set ("externally parented parts") is the same shape; only the moment of evaluation moves from "recording start while docked" to "the frame of the first decouple/separation." `StartDockedOriginVesselPid` is reused as the field (rename to `OriginVesselPid` or add `OriginSeparationVesselPid`). `RouteEndpointResolver.TryResolveEndpoint` resolves the base by PID each cycle (this already works). `RouteProofHasher` already folds the PID in.

New pieces: (a) a recorder hook on the controlled-decouple/launch-clamp-release path that snapshots the parent vessel PID plus the transport's resource manifest at separation; Parsek already detects controlled decouples (it sets `ParentAnchorRecordingId` for `IsDebris=false` children), so this is wiring an existing signal into the origin-proof producer. (b) The origin-debit writer primitive. (c) Replace the `OriginHasCargo` stub to check the resolved base's live manifest.

Failure modes:

- Origin vessel gone (base recovered/destroyed): PID miss. Because the base is a surface vessel, set `IsSurface = true` (unlike the docked path, which sets false) so the existing surface-proximity fallback in `RouteEndpointResolver` applies; on total miss, `EndpointLost` plus retry backoff (mirrors edge case 10.3, already specified).
- Insufficient cargo at base: `OriginHasCargo` returns false, so `WaitingForResources`, no debit, ghost still flies but transfers nothing (mirrors edge case 10.4's "world looks busy" behavior on the origin side).
- Origin moved (player drove the base away): surface fallback is intentionally tight (`SurfaceProximityRadiusMeters = 500.0`, Source/Parsek/Logistics/RouteOrchestrator.cs:46). A base deliberately relocated loses the route, which is the documented correct behavior.
- Save/reload: PID plus manifest are serialized on the proof; resolution is re-run each cycle, so reload is transparent (edge case 10.17).
- Rewind/re-fly: the physical debit goes through the ledger as a `RouteCargoDebited`-physical row, so the existing epoch-isolation/tombstone machinery (`RouteModule`, design 10.6) reverses it, the same contract as the funds debit.

### Candidate B: require the origin base to itself be a committed Parsek recording

Proves ownership by: refusing to create an undocked-start route unless the base is also a committed recording/tree in the timeline (its own `Recording` with a known resource state). Ownership equals "this base is a thing Parsek has on record and whose resource ledger it can read." The tanker recording references the base recording's ID as the origin.

Debits per cycle, from where: the live vessel resolved from the base recording's current PID/identity, same `LiveOriginDebitWriters`. The base recording supplies the proof of existence and identity (and a `SourceRef`-style fingerprint), not the resource amount; the amount still comes from the live base vessel so we never invent cargo.

Reuses/mirrors: the entire `SourceRefs` / `RouteHasValidSourcesInErs` revalidation machinery. The base recording becomes an additional source ref, so `MissingSourceRecording` / `SourceChanged` already cover "base recording deleted/rewritten." `VesselLaunchIdentity` (guid-gated PID matching) gives a launch-unique identity for the base, defeating the craft-baked-PID trap.

New pieces: a UI/analysis step that picks which committed recording is the origin base and links it; a base-identity resolver that maps the base recording to the live vessel via `RecordedVesselGuid` / `VesselLaunchIdentity`; the debit writer.

Failure modes: richer than A because the base has a tracked identity. Base recording missing maps to `MissingSourceRecording` (existing). Base vessel not currently live maps to `EndpointLost` / `WaitingForResources`. Insufficient cargo maps to `WaitingForResources`. But: high creation friction (the player must have recorded the base as its own mission, which many will not have), and it conflates "I have a recording of the base" with "the base owns this cargo": the recording proves identity, not ownership of the specific manifest.

### Candidate C: pre-departure resource snapshot plus origin-side resolver mirroring RouteEndpointResolver

Proves ownership by: at the launch/separation moment, capture both the base PID and a `RouteEndpoint`-shaped origin descriptor (`body, lat, lon, alt, situation, PID`), that is, give the origin the same descriptor the destination endpoint already has. Then resolve the live origin each cycle through an `RouteOriginResolver` that is a near-clone of `RouteEndpointResolver` (PID-primary, surface-proximity fallback for surface bases, PID-only for orbital).

Debits per cycle, from where: the resolved origin vessel's tanks/inventory via `LiveOriginDebitWriters`. The "pre-departure snapshot" of the base is captured for diagnostics and a sanity gate (did the base actually hold this manifest at departure?), but the live debit is always against current live state so nothing is invented.

Reuses/mirrors: this is the highest-reuse option. `RouteEndpointResolver` is already written to be origin-capable. Its doc says "every stop, plus the origin when it is a vessel (non-KSC)", and `RouteDispatchEvaluator.CheckEligibility` already calls `env.TryResolveEndpoint(route.Origin, ...)` for non-KSC origins (Source/Parsek/Logistics/RouteDispatchEvaluator.cs:190-195). The only reason the origin path does not fully work is that the docked builder sets `IsSurface = false` and lat/lon/alt = 0. Candidate C fills those fields in at capture, so the existing resolver plus surface fallback "just work" for a surface base. No new resolver class is even strictly required; we extend capture to populate the existing `RouteEndpoint` on `Route.Origin`.

New pieces: capture of the origin `RouteEndpoint` coordinates/situation at separation (the recorder has the base's world position at that frame); optionally a `RouteOriginResolver` thin wrapper if we want origin-specific compatible-vessel rules (design 7.3 already defines "compatible origin fallback vessel"); the debit writer; the `OriginHasCargo` un-stub.

Failure modes: identical surface to Candidate A (they are close cousins), but with better origin-gone handling because the surface descriptor is captured, so the surface-proximity fallback genuinely applies to a rebuilt base (edge case 10.1/10.3 parity with destinations). Orbital origins (a tanker that separated from an orbital station and flew to another) get PID-only resolution, which is correct, since orbital coords drift.

### Candidate D: fail-closed, "origin must be a real, persistent vessel resolvable by PID near the recorded departure point"

Proves ownership by: the weakest claim that is still honest. At capture, record the departure PID plus departure point. At creation and each cycle, require that a real persistent vessel with that PID (or a surface-proximity match) exists near the recorded departure point; if not, the route cannot be created / cannot dispatch. No identity guesswork beyond PID plus proximity.

Debits per cycle, from where: that resolved vessel, via `LiveOriginDebitWriters`. If it cannot be resolved, the route fails closed (`EndpointLost` / refuse-create).

Reuses/mirrors: everything Candidate C reuses, but with stricter gates and no diagnostic snapshot. Effectively "Candidate C minus the pre-departure sanity snapshot, plus a hard fail-closed posture."

Failure modes: the most conservative. Anything ambiguous (PID craft-baked collision, no vessel near departure, base relocated) maps to fail closed, route never moves cargo. This is the safest against infinite-cargo bugs but the most likely to frustrate (a route silently refuses where the player expects it to work).

---

## 3. Tradeoff comparison

Realism / fidelity:
- A: High. Debits the actual base the tanker left, like the docked case.
- B: High but narrow. Only realistic if you recorded the base.
- C: High. Same as A, with proper surface-rebuild tolerance.
- D: Medium. Correct when it works, refuses otherwise.

Implementation cost:
- A: Medium. New recorder hook on separation; reuses partner resolver.
- B: High. Links base recordings, identity resolver, UI to pick base.
- C: Low to medium. Extends existing `RouteEndpoint` capture; resolver already origin-capable.
- D: Low. Strictest gates, least new logic.

New infra versus reuse:
- A: Reuses `TryResolveStartDockedOriginPartner` plus endpoint resolver; adds 1 capture hook.
- B: Adds identity-resolution plus source-ref linkage for the base.
- C: Maximal reuse. `RouteEndpointResolver` already calls origin; just populate fields.
- D: Maximal reuse, minimal new.

Interacts with `StartDockedOriginVesselPid`:
- A: Direct extension. Same PID field, different capture trigger; cleanest superset of the docked path.
- B: Orthogonal. Adds a parallel ownership concept.
- C: Direct extension. Fills the `RouteEndpoint` fields the docked path leaves zero.
- D: Direct extension, narrower.

Interacts with KSC funds path:
- A, B, C, D: None. The funds path is the physical-debit sibling and is untouched.

Edge-case robustness:
- A: Good. Inherits 10.1/10.3/10.4 once `IsSurface=true` set.
- B: Best identity story (guid-gated), worst usability.
- C: Best. Full destination-parity surface fallback.
- D: Safest against glitches, worst UX.

Risk of "inventing a warehouse":
- A, B, C, D: None. Real vessel, real stock debit in every option.

Biggest weakness:
- A: Capture trigger correctness across launch-clamp versus decoupler versus staging.
- B: Creation friction; ownership is not the same as "I recorded it."
- C: Needs careful surface-vs-orbital `IsSurface` capture.
- D: Refuses too often; poor discoverability.

The decisive observation: A, C, and D are not really competitors. They are the same mechanism at three confidence levels. All three (i) capture a base PID at the departure event, (ii) debit a live PID-resolved vessel through one shared origin-debit writer, and (iii) differ only in how much descriptor metadata they capture and how strict the gate is. Candidate C is Candidate A with the `RouteEndpoint` fully populated (so surface fallback works), and Candidate D is C with the diagnostic snapshot dropped and a fail-closed posture. B is the genuine outlier: it introduces a second, heavier ownership concept (base-as-recording) that adds creation friction without strengthening the actual debit (which still hits live state in every option).

---

## 4. Recommendation

Build Candidate C as the first cut, scoped down to its fail-closed core (which is exactly Candidate D), and keep Candidate A's capture trigger as the implementation of C's capture step. In other words: ship the union of A's capture plus C's resolver plus D's strictness, because they are the same code at different settings. Defer Candidate B entirely.

Why:

1. It finally builds the primitive every non-KSC route needs. The `OriginHasCargo` stub and the missing `LiveOriginDebitWriters` are the real blocker. Any candidate must build them; C builds them against the resolver that already exists and is already wired for origins (`RouteEndpointResolver`, called from `RouteDispatchEvaluator.CheckEligibility:190-195`). This is the smallest delta to a working physical debit, and it simultaneously unblocks the docked-depot path, which is itself stubbed today. One mechanism closes both the docked and undocked non-KSC gaps.

2. Maximal reuse, minimal new surface. The docked path leaves `RouteEndpoint{IsSurface=false, lat/lon/alt=0}` for the origin (RouteBuilder.cs:331-342). C's entire creation-side change is "populate those fields at capture." The resolver, the surface fallback, the PID lookup, the proof hash, the serialization, and the eligibility call site are all already present.

3. It is honest and warehouse-free by construction. Every cycle debits a live, PID-resolved, stock-tracked vessel whose tanks actually go down. If the base is gone or empty, the route waits or loses its endpoint exactly like every other endpoint in the system. Nothing is invented.

4. Fail-closed first keeps the blast radius small. Start with D's strictness (refuse to create / refuse to dispatch unless a real vessel resolves by PID or tight surface proximity at the departure point). This means the first shipped behavior cannot leak cargo. The pre-departure sanity snapshot and looser surface tolerance (the full C behavior) can be layered on once the debit writer is validated in-game.

Minimal first cut:

1. Origin-debit primitive (`LiveOriginDebitWriters` plus a ledger-backed physical `RouteCargoDebited` applier): mirror `LiveDeliveryWriters`' loaded/unloaded split, but removing from the origin. Route it through `RouteModule` so it is revert-safe (design 10.6). This is the load-bearing new code.
2. Replace the `OriginHasCargo` stub (LiveRouteRuntimeEnvironment.cs:104-115) with a real check: resolve `route.Origin` via the existing resolver, sum the live manifest, compare to `CostManifest`, then return true or WaitingForResources.
3. Capture trigger (Candidate A's mechanism): hook the controlled-decouple/launch-clamp-release path to populate a `RouteOriginProof` for the undocked-start case: base PID (reuse `TryResolveStartDockedOriginPartner` over the pre-separation parent set) plus a fully-populated origin `RouteEndpoint` (`IsSurface` from the base's situation, lat/lon/alt from the base's world position at separation).
4. `RouteBuilder` origin branch: accept the new proof shape and set `Origin.IsSurface = true` for surface bases so the existing surface fallback applies. Keep fail-closed: if the base cannot be resolved at creation, reject with a clear reason.

Deferred further:

- The pre-departure resource-state snapshot as a gate (the "did the base actually hold this?" sanity check). Capture it for diagnostics first, enforce later.
- Loosening surface proximity beyond the current tight 500 m.
- Orbital undocked-start origins (tanker separated from an orbital station): PID-only, no fallback. Ship after surface is proven, since orbital coordinate drift makes proximity meaningless.
- Candidate B (base-as-recording) entirely. Revisit only if players want a base whose future production gates the route, which is a different (logistics-chaining) feature.

---

## 5. Open questions / unknowns (DEFERRED decisions, not to be resolved now)

These are recorded-open. They are deliberately NOT being decided in this note; they need a player-facing decision or a KSP API investigation before any implementation begins.

1. DEFERRED: which separation event to anchor capture to. Launch clamps (`LaunchClamp` release), decouplers (`ModuleDecouple` / `ModuleAnchoredDecoupler`), and docking-port undock are three different KSP events. A Minmus base tanker could leave via any of them. Needs KSP API investigation: which events fire with a usable "parent vessel before separation" reference, and whether Parsek's existing controlled-decouple detection (the `ParentAnchorRecordingId`, `IsDebris=false` path) already covers all three. The `onPartUndock` / `onVesselsUndocking` ordering gotcha in CLAUDE.md is a precedent for how subtle this is. Do not resolve now.

2. DEFERRED: whether to guid-gate the base PID via `VesselLaunchIdentity`. `persistentId` is craft-baked and not launch-unique. The question is whether the origin proof should also capture `RecordedVesselGuid` for the base and gate PID matches through `VesselLaunchIdentity` (as the destination/identity code does), to avoid debiting the wrong same-craft vessel. This is a correctness call, not just polish: debiting the wrong base is an infinite-cargo-class bug. Recorded open; do not resolve now.

3. DEFERRED: the empty-base UX. When the resolved base lacks the cost manifest, the proposed behavior is `WaitingForResources` (route keeps trying, ghost still flies but transfers nothing), mirroring the docked-depot intent and edge case 10.4 / 10.3. The open decision is whether that quiet wait is the desired player experience for an undocked origin, versus a louder "your base ran dry" surfacing. Player-facing decision; do not resolve now.

Additional context items (lower priority, also open):

- Competing routes from one base (edge case 10.12). The doc specifies FIFO-by-`NextDispatchUT`. With a real per-cycle origin debit now actually firing, two undocked-origin routes sharing a base will genuinely contend. Confirm FIFO is acceptable for v1.x or whether the un-stubbed debit needs the deferred priority system sooner.
- Does the origin debit need a recovery-credit analogue? The KSC path has a deferred recovery credit (`EmitPendingRecoveryCredit`). Physical origin cargo is not "recovered", it is consumed, so presumably no credit. Confirm there is no symmetric credit expectation for the physical path before wiring the ledger module.

---

## Key files referenced

All paths under the logistics-v0-implementation source tree (Source/Parsek/...):

- Logistics/LiveRouteRuntimeEnvironment.cs: the `OriginHasCargo` stub (the actual blocker), lines 87-116.
- Logistics/RouteBuilder.cs:305-354: origin classification (KSC versus docked-depot); 331-342 the docked-origin `RouteEndpoint` left zero.
- RouteProofCapture.cs: `TryResolveStartDockedOriginPartner` / `BuildStartRouteOriginProof` (capture mechanism to extend), line 138 onward.
- RouteProofMetadata.cs:100-119: `RouteOriginProof` shape (`StartDockedOriginVesselPid`).
- Logistics/RouteEndpointResolver.cs: already origin-capable; surface fallback.
- Logistics/RouteDispatchEvaluator.cs:175-202: origin endpoint plus cargo gate.
- Logistics/RouteOrchestrator.cs: `EmitDispatchDebit` / `ApplyDelivery` (debit/deliver model to mirror for origin); SurfaceProximityRadiusMeters at line 46; EmitDispatchDebit at line 1032 onward.
- Logistics/LiveDeliveryWriters.cs: the destination writer to mirror as `LiveOriginDebitWriters`.
- Logistics/RouteFundsCalculator.cs: KSC funds cost (the other, fully-built path).
- GameActions/FundsModule.cs:533: `ProcessRouteCargoDebited`.
- docs/parsek-logistics-supply-routes-design.md:1389: the canonical deferral; 843-845 dispatch step 4-5; 1001-1003 edge case 10.3.
