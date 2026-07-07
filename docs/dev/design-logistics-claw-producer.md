# Design: Claw/Grapple Connection Producer (logistics)

Status: DESIGN. Lifts the "non-docking connection producers" out-of-scope-by-doctrine item
(design doc 17.1 Tier 3 / 19.4 / 19.6), greenlit for revisit 2026-07-07, for the claw
(`ModuleGrappleNode`, Advanced Grabbing Unit) ONLY. Stock crossfeed / fuel-line stays out
(section 7). Foundation: `docs/dev/research/claw-grapple-coupling-internals.md` (Phase 0
decompile findings; cited below as "findings N.M").

Goal: a Supply Run whose transfer happens across a claw couple, including asteroid-mining
runs (PotatoRoid grabs), can become a Supply Route with the same proof-of-work rigor as
docking routes.

## 1. What the decompile changed about the problem

The Tier 3 entry assumed each producer needs "its own detection of endpoint PID, connection
start, connection end, and cargo delta". The decompile (findings 1-2) showed the claw needs
almost none of that: `ModuleGrappleNode` couples through `Part.Couple` and releases through
`Part.Undock`, the exact primitives docking ports use, with identical event order
(`onPartCouple` first, `onVesselsUndocking` last-and-unconditional). Parsek's event pipeline
(`ParsekFlight.OnPartCouple` / `OnPartUndock` / `OnVesselsUndocking`) has NO docking-module
filter anywhere (verified: zero references to `ModuleDockingNode` in `Source/Parsek`), so a
claw couple ALREADY flows end to end today: branch point, partner snapshots, connection
window, undock completion.

What is wrong today is narrower than "unsupported":

1. **Mislabeling.** The two window/branch construction sites hardcode
   `RouteConnectionKind.DockingPort` (`ParsekFlight.cs` merge-data call and the
   `BuildDockRouteConnectionWindow` call), and `BuildDockRouteConnectionWindow` itself
   defaults `None -> DockingPort`. A claw couple is stamped as a dock; so is ANY modded
   couple (KAS, etc.). The design doc's stated v1 edge rule "Non-DockingPort connection
   kind -> validation rejects" was never implemented because nothing ever stamped a
   non-dock kind.
2. **Empty-window admission.** The per-window no-delivery-AND-no-load gate
   (`RouteAnalysisStatus.NoDeliveryManifest`) REJECTS the run. A zero-transfer window is a
   workflow smell for docks, but it is the NORMAL shape of an asteroid grab: asteroids
   carry no `PartResource`s (findings 4), so both the delivery manifest
   (`min(endpointGain, transportLoss)`) and the load manifest
   (`min(endpointLoss, transportGain)`) are structurally empty even while drills produce
   Ore mid-window (that gain is witnessed by M2 harvest windows and the run-level
   flow-closure `harvested` term, not by window corners).
3. **Zero verification.** M-MIS-10 flags claw couples as the highest-risk unverified cell:
   no in-game coverage of the claw branch point, PotatoRoid snapshot part-name path, or
   asteroid ghost-visual build.

So the producer abstraction is a CLASSIFICATION seam plus producer-aware admission, not a
parallel detection stack.

## 2. The producer seam

### 2.1 Classification (capture side)

New pure static `ConnectionProducerClassifier` (own file, xUnit-testable core with the
module scan injected):

```
ConnectionProducerClassifier.Classify(Part from, Part to) -> RouteConnectionKind
```

- Both endpoints carry `ModuleDockingNode` -> `DockingPort`.
- Either endpoint carries `ModuleGrappleNode` -> `Grapple` (findings 1.3: the grabbed side
  is an arbitrary part; which side is `from` depends on `Vessel.GetDominantVessel`, so BOTH
  ends are tested and no module is expected on the non-claw side).
- Anything else -> `Unknown` (fail closed; modded coupling producers are not silently
  treated as docks anymore).

Called once, in `ParsekFlight.OnPartCouple`, on the live event parts (the only moment both
parts still resolve). The kind rides the existing pending-dock-merge state
(new `pendingDockTransferKind` alongside `pendingDockRouteTargetPid`, reset where the
pending PIDs are reset) into the two construction sites:

- `BuildMergeBranchData(..., transferKind)` stamps `Recording.TransferKind` truthfully.
- `RouteProofCapture.BuildDockRouteConnectionWindow(..., transferKind)` stamps
  `RouteConnectionWindow.TransferKind` truthfully.
- The `None -> DockingPort` defaulting inside both stays (it is what keeps old call sites
  and old recordings meaning "dock"), but every live capture path now passes an explicit kind.

**EVA grabs** (findings 1.4): a claw grabbing an EVA kerbal fires a real couple. Capture
classifies the couple `Grapple` but SKIPS building a route connection window when the
absorbed vessel `isEVA` (one Info log line). The recorder still branches (that is correct
recording behavior and out of logistics' scope); logistics simply records no transfer
evidence, so a run whose only "transfer" was a kerbal grab rejects with the existing
`MissingRouteProof` / `NoDeliveryManifest` reasons. No new window field, no hash impact.

**Not changed:** detection stays keyed on `onPartCouple` / `onVesselsUndocking`. The claw
fires no `onVesselDocking` (findings 1.3), and nothing in Parsek listens to it; the
findings note pins that as a permanent constraint on future capture work.

### 2.2 Admission (analysis side)

`RouteAnalysisEngine` gains exactly two producer-aware branches:

1. **Kind gate** (new, the doc's promised v1 rule, now real): a completed window whose
   `TransferKind` is not `DockingPort` and not `Grapple` rejects the run with new
   `RouteAnalysisStatus.UnsupportedConnectionKind = 10`, reject detail naming the kind.
   Pre-existing recordings are unaffected: their windows are stamped `DockingPort`
   (immutable witnesses), and the codec already round-trips all enum names
   (`RouteNodeCodec.ParseConnectionKind`; unparseable values -> `Unknown` -> reject, which
   is the fail-closed reading of a corrupt value).
2. **Empty-window rule branches by kind**: the per-window no-delivery-AND-no-load gate
   keeps REJECTING for `DockingPort` (unchanged v0..M6 behavior) and SKIPS the window as a
   non-stop for `Grapple` (one Diag log line). If after skipping, no stop-bearing window
   remains in the whole run, the existing `NoDeliveryManifest` reject fires (a run that
   transferred nothing anywhere is still not a route). A grapple window that DID transfer
   (fuel pumped across the claw on Easy/Normal presets, findings 5; inventory moved; crew
   is out of scope as before) builds manifests through the untouched builders and becomes
   a normal stop.

Everything else is already connection-agnostic and stays byte-identical: window collection
across the source path (M4a `AnalyzeTree`), DockUT ordering, direction classification, the
inventory rules, harvest gain check, flow closure (`launched + loaded + harvested -
delivered - residual`), endpoint proof (`HasEndpointProof` requires `!= None`, which both
admitted kinds satisfy), escrow/clamp semantics (19.2.5), and dispatch.

### 2.3 Sites keyed on docking today (the 17.1 enumeration)

Complete list of dock-specific keying found (everything else is event- or window-shaped):

| Site | Today | Change |
|---|---|---|
| `ParsekFlight` merge-data call | hardcodes `DockingPort` when routeTargetPid != 0 | pass classified kind |
| `ParsekFlight` window-build call | hardcodes `DockingPort` | pass classified kind |
| `BuildMergeBranchData` default param | `None -> DockingPort` | keep default, callers explicit |
| `RouteProofCapture.BuildDockRouteConnectionWindow` | `None -> DockingPort` | keep default, callers explicit |
| `RouteAnalysisEngine` per-window empty gate | reject always | branch by kind (2.2) |
| `RouteAnalysisEngine.HasEndpointProof` | `!= None` | unchanged (both kinds pass) |
| Formatters | no kind text at all | section 5 |

There are NO module-type-keyed capture sites; the "hardcoded dock path" the Tier 3 entry
anticipated turned out to be two hardcoded enum arguments.

## 3. Recorder parity (the M-MIS-5 P1 question)

Answered by inspection, no Missions-layer changes needed:

- A claw couple reaches `ParsekFlight.OnPartCouple` (module-agnostic) and produces a
  `BranchPointType.Dock` branch point with the same dual-evidence gating as docks; a claw
  release runs the standard `OnPartUndock -> OnVesselsUndocking -> DeferredUndockBranch ->
  CreateSplitBranch` split pipeline (findings 2.1: identical event contract).
- The M-MIS-5 P1 dock interval boundary keys sub-intervals as `<parentKey>@dockM` gated on
  `OriginBranchPointType == Dock || Board` (`MissionComposition.cs`). A claw-merged child
  carries `OriginBranchPointType == Dock`, so `@dockM` boundaries fire for claw couples
  BY CONSTRUCTION.
- Gap list therefore reduces to: (a) truthful `TransferKind` stamping (section 2.1);
  (b) verification, not code: the claw branch point, PotatoRoid snapshot part-name path,
  and asteroid ghost visuals have never been exercised in-game (M-MIS-10 cell #4). The
  in-game tests in section 6 plus the operator playtest cover (b).

The Missions layer stays read-only for logistics (doctrine rule 8); this feature ships
with a zero-diff on `Mission.cs` / `MissionComposition.cs` / `MissionStore.cs`.

## 4. Endpoints, asteroids, and the mid-run grab

### 4.1 Grappled-asteroid endpoints

The claw window's endpoint is the grabbed vessel at couple time (`EndpointAtDock` +
`TransferTargetVesselPid`), captured exactly as for docks. Asteroid specifics (findings 4):

- The asteroid vessel DIES at the grab (absorbed; ship always dominates `SpaceObject`) and
  a RELEASED asteroid is a NEW vessel with a fresh Guid and persistentId. An asteroid
  endpoint is therefore only resolvable live WHILE it stays the same vessel; after a
  release-and-regrab cycle, PID-only orbital resolution fails and the route enters the
  existing `EndpointLost` hold, naming its reason (M6). That is the correct fail-closed
  behavior and v1 accepts it; no identity-bridging via the PotatoRoid part `flightID` in v1
  (noted as the future lever if asteroid round-trips ever matter).
- In the dominant real scenario (mining run: grab -> drill -> release or keep -> fly to
  station -> dock -> deliver), the asteroid-side window is an empty non-stop (2.2), the
  station window is the delivery stop, and dispatch only resolves STOP endpoints, so the
  dead asteroid vessel never blocks dispatch.
- A run that STARTS grappled (recording begins at the grab branch) follows the M1
  docked-start origin path with the asteroid as origin partner; its origin cost manifest is
  inherently empty (no PartResources) and M2 harvest provenance carries the gains, which is
  the already-shipped "drills on an already-grappled asteroid" shape.

### 4.2 Mid-run grab: RESOLVED, not rejected

The M2 deferral ("a mid-run claw grab is a merge boundary, M4 territory", plan finding 15)
is retired by composition of things that have shipped since:

- The grab creates a merge boundary -> the claw window lives on the merged child recording.
- M4a `AnalyzeTree` collects completed windows across ALL source-path recordings and orders
  them by DockUT, so a run shaped "launch -> grab (window A) -> release -> dock at station
  (window B)" analyzes as a two-window run today.
- Harvest windows already close/reopen at the boundary (M2) and the flow-closure harvested
  term is window-interval based, not recording based.

What this design ADDS for the mid-run case is only what 2.2 already adds: the empty grab
window must be skippable rather than run-rejecting. A fixture test (section 6) pins the
full mid-run shape end to end. No new merge machinery, no M4 remnant.

### 4.3 Ambiguous cases and their named outcomes

| Case | Behavior | Named outcome |
|---|---|---|
| Modded/unknown coupling producer | window stamped `Unknown` | `UnsupportedConnectionKind` reject |
| EVA kerbal grab | no window built (2.1) | existing `MissingRouteProof`/`NoDeliveryManifest` |
| Pure grab-and-release run, nothing transferred | all windows skipped | `NoDeliveryManifest` reject |
| Same-vessel grapple | no couple event fires (findings 1.4) | invisible, correctly |
| Multi-claw on same target | second claw is a same-vessel grapple | one window, correct |
| Release while another claw still holds | split leaves part sets overlapping | existing disjoint-verification fails the window closed |
| Joint break / crash separation | no `onPartUndock` fires | window stays incomplete; incomplete windows are never collected |
| Claw `Decouple` action (modded activation) | `onPartDeCouple` family, not undock | window stays incomplete, fails closed |
| Corrupt/unparseable stored kind | codec yields `Unknown` | `UnsupportedConnectionKind` reject |

## 5. Player-visible text

- `RouteCreationFormatters`: stop/summary lines say the connection kind where it is not a
  dock ("grappled" vs "docked"); the route detail panel shows it per stop
  (`RouteStop.ConnectionKind` already exists and round-trips).
- New reject text: `UnsupportedConnectionKind` -> "This run's transfer used a connection
  type Parsek does not support for routes (<kind>). Docked and claw-grappled transfers are
  supported."
- The M6 hold presentation needs no new kinds (EndpointLost already names its reason).

## 6. Testing

Unit (xUnit):
- `ConnectionProducerClassifier` truth table (dock/dock, grapple/any, any/grapple, neither,
  null-safety), pure core.
- Admission: grapple window with real transfer -> Eligible stop; empty grapple window
  skipped + station delivery -> Eligible with one stop; ALL-empty windows -> `NoDeliveryManifest`;
  `Unknown` kind -> `UnsupportedConnectionKind` (+ reject detail text); dock empty window
  still rejects (regression pin); mid-run grab fixture per 4.2 end to end.
- Codec: `Grapple` and `Unknown` round-trip on window + recording + route stop
  (existing `ParseConnectionKind` covers names; pin it).
- Byte identity: `Hash_PreM3Recording_ByteStable` untouched (pre-existing recordings keep
  `DockingPort`); NEW pinned hash for a Grapple-stamped window (transferKind IS hashed,
  `RouteProofHasher` writes `(int)TransferKind`, so the new shape needs its own pin);
  save/load round-trip of an old-shape recording stays byte-identical.
- PotatoRoid part-name path: snapshot part-name resolution for `PotatoRoid` (no underscores,
  conversion no-op) pinned through the same seam the underscore-dot tests use.
- Generators: `RecordingBuilder.WithRouteConnectionWindow` gains a kind parameter (default
  `DockingPort` so every existing fixture is unchanged).

In-game (new category `LogisticsGrapple`, `Scene = FLIGHT`):
- Claw couple fires the recorder branch + truthful `Grapple` window stamp (synthesizable:
  spawn a two-vessel claw rig, force the couple, assert window kind + branch type from the
  live tree). Honest note: full physics capture (raycast contact at 0.06 m) is not
  reliably automatable; the automated test drives `ModuleGrappleNode.Grapple`-adjacent
  state or a scripted `Part.Couple` with claw parts, and the REAL contact capture is
  operator territory.
- PotatoRoid ghost-visual build + snapshot round-trip on a live asteroid if one can be
  spawned procedurally in the test (DiscoverableObjectsUtil); otherwise PENDING-OPERATOR.

Operator playtest (merge gate, PENDING-OPERATOR in the todo file): grab an asteroid or
derelict, transfer, release, commit, create the route, watch one delivery cycle
(M-MIS-10 cell #4 verification lands with it, label `mmis10-claw`).

## 7. Doctrine amendment (19.4 / 19.5 / 19.6) and stated non-goals

- 19.4 out-list and the 19.5 completeness sentence drop "claw/grapple" from the
  out-by-doctrine list; the completeness test becomes: any committed recording whose flows
  close becomes a route across BOTH supported connection producers (dock, claw).
- 19.6 rule 4 ("route transports never materialize") reads unchanged with Grapple included:
  a grapple transport is still a ghost re-run; nothing about the claw changes disposition.
- 19.6 gains a producer rule (rule 10): "Connection producers are classified, never
  inferred: a window is stamped by the producer that made it, unknown producers reject by
  name, and every admitted producer passes the same flow-closure rule."
- Stock crossfeed / fuel-line stays OUT, now with a sharper stated reason from the
  decompile: fuel lines and crossfeed move resources WITHOUT a couple event or vessel
  merge, so there is no connection window to corner-snapshot; they are continuous-flow, not
  window-shaped, and would need a different evidence mechanism entirely. `StockCrossfeed`
  remains a reserved enum value that nothing stamps and admission rejects.
- Crew delivery, persisting-transport materialization, undocked-start proving: unchanged,
  still out.
- Roadmap Phase 13 out-of-scope line updates to: "non-docking connection producers beyond
  the claw (fuel-line/crossfeed), crew delivery, persisting-transport materialization,
  undocked-start origin proving".

## 8. What this deliberately does not do (v1 fail-closed scope)

- No claw-specific detection machinery (no FSM watching, no `onVesselDocking`, no module
  state polling): the couple/release events are the whole producer.
- No identity bridging for released asteroids (4.1): `EndpointLost` is the answer.
- No crossfeed modeling: corners measure what actually moved (findings 5); a Moderate/Hard
  save where stock blocks pumping across the claw simply produces zero-delta fuel windows.
- No back-compat migration: pre-existing recordings keep their recorded `DockingPort`
  stamp (immutable witnesses), analyze exactly as before, and their proof hashes are pinned.
- No Missions-layer edits (rule 8).
