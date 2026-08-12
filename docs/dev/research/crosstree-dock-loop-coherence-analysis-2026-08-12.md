# Dock/Undock Topology, Mission Boundaries, and Loop Coherence — Systems Analysis

**Scope.** Issue 2 of 2: whether Parsek's recording/mission model can represent and communicate the AB/CD scenario ({AB, CD} → {BC, AD} via four dock/undock events), and whether looping a single mission renders a coherent sub-world. Builds on the extraction at `dock-loop-model-extract-2026-08-12.md`; every load-bearing claim below was re-verified in source (file:line refs given). Coordinates with the issue-1 analysis (`mission-presentation-ux-analysis-2026-08-12.md`); its proposals are referenced as T1.x/T2.x/T3 and not re-proposed here.

**Verification status of inherited key findings** (all confirmed in source this session):

| Claim | Verified at |
|---|---|
| Dock is two-parent only when partner is in live `activeTree.BackgroundMap` | `Source/Parsek/ParsekFlight.cs:12118-12127` |
| `BackgroundMap` eligibility requires `TerminalStateValue == null` (so committed leaves never qualify) | `Source/Parsek/RecordingTree.cs:433-442` |
| `BranchPoint.TargetVesselPersistentId` is fed by the **route-eligibility-gated** partner PID | `ParsekFlight.cs:10770-10787` → `:12131-12135` |
| Cross-tree link derivation scans **only other trees** (same-tree claims skipped) | `Source/Parsek/MissionCrossTreeDock.cs:58-63` |
| Partner-journey walk prefers PID-match child, else first controlled child (= `ChildRecordingIds[0]` by recorder convention) | `MissionCrossTreeDock.cs:599-619` |
| Loop members render independently iff shared span clock is in their own window; no cross-member selection | `Source/Parsek/GhostPlaybackLogic.SpanClock.cs:1749-1833` |
| `Docked`/`Boarded` are non-spawnable terminals | `Source/Parsek/GhostPlaybackLogic.cs:7138-7149` |
| Cross-tree members fail periodicity/re-aim closed; one shared span clock over both trees | `Source/Parsek/MissionLoopUnitBuilder.cs:195-234`, `:350-364` |
| Both dock parents' through-lines contain the merged child (heads = legs nobody continues into; each head walks its own set) | `Source/Parsek/MissionThroughLine.cs:44-82`, `:124-149` |

---

## 1. Formal model and invariants

### 1.1 Entities

- **Part world-line**: a physical part's existence over UT. Not modeled by Parsek (no part-level lineage).
- **Physical assembly** `A(t)`: an equivalence class of parts under "rigidly joined at UT t". Assemblies split (undock/decouple/breakup) and merge (dock/board/claw). The *player's* narrative objects. Identity across a merge/split is a matter of parts, not of names or PIDs.
- **Controlled vessel**: a KSP `Vessel` — `persistentId` (craft-baked, collision-prone) + `Vessel.id` Guid (launch-unique). One assembly at one time = one vessel, but vessel identity does not survive merges (absorbed vessel destroyed) and mints fresh at splits (`newVessel` gets a brand-new PID — `docs/dev/dock-undock-recording-structure.md:48`).
- **Recording**: one vessel identity over one interval — trajectory + events + snapshot. The rendering atom.
- **BranchPoint**: typed topology edge inside one tree (`Source/Parsek/BranchPoint.cs`). Splits: 1 parent → 2 children. Merges: 1-or-2 parents → 1 child, plus a `TargetVesselPersistentId` sidecar for the un-owned partner.
- **RecordingTree**: the container. Membership rule (verified, §1.6 of extract): *a tree owns whatever was recorded while it was the active tree* — trees accrete by recorder location, not by matter.
- **Mission**: a named selection over exactly **one** tree (`Mission.TreeId`) + excluded interval keys + opt-in foreign dock links + loop settings.
- **Through-line**: derived path of one continuing controlled vessel (env-splits + continuation children merged).
- **Loop unit**: owner + member committed-indices + per-member trimmed windows + one shared span clock (`GhostPlaybackLogic.SpanClock.cs:22-45`).

### 1.2 Invariants that SHOULD hold (proposed contract)

- **I1 (Coverage/uniqueness)**: in any one playback context, every assembly that should be visible at UT t is rendered by exactly one recording — no unexplained gap, no double render.
- **I2 (Dock bilaterality)**: every dock has exactly two participants, and the event is *reachable* (renderable/derivable) from both participants' stories.
- **I3 (Continuity of matter)**: if assembly X is created at event e from parts of Y and Z, then Y's and Z's stories both reach e, and X's story references both. Dually for splits: each child's story references its material origin.
- **I4 (Mission narrative closure)**: everything a mission renders either belongs to its story or is explicitly labeled with a pointer to the story it does belong to.
- **I5 (Loop self-consistency)**: looping one mission renders a sub-world where every appearance/disappearance of a ghost is explained by an event that is itself rendered or labeled.
- **I6 (Clock uniqueness)**: one physical assembly is never concurrently rendered at two different replay times by two independent clocks.
- **I7 (Session invariance)**: the same physical event sequence produces the same topology regardless of scene exits and switch routing.

### 1.3 Scorecard against the implementation (AB/CD as running example)

| Invariant | Status | Where it breaks | Gap class |
|---|---|---|---|
| I1 within one tree, live clock | **Holds** | Windows partition at branch UTs; one-frame epsilon overlap at seams is by construction (`SpanClock.cs:1749-1754`) | — |
| I1 across trees, loops | **Violated** | (a) partner-journey gap `[partnerEnd, dockUT]` renders nothing (accepted contract, pinned by `CrossTreeDockLoopUnitInGameTest` window set); (b) CD's matter double-renderable under two clocks — see §2(e) | UI (a is documented); derivation+enforcement (b) |
| I2 | **Violated 3 ways** | (i) cross-tree dock: single parent + PID sidecar; partner side gets an *opt-in derived* link — bilaterality is conditional; (ii) same-tree cross-session dock (A→D): structurally invisible from D's side — `BackgroundMap` misses (`RecordingTree.cs:437`), and `FindLinks` skips same-tree (`MissionCrossTreeDock.cs:61-63`); (iii) route-ineligible dock: `TargetVesselPersistentId = 0` → **no derivation possible at all** (`ParsekFlight.cs:10782-10787`) | (i) derivation+UI; (ii) **derivation** (data exists: BP_d2 carries `target=pid(D)`, R(D) is in the same tree — a same-tree scan would recover it); (iii) **data-model** (the fact was observed and discarded) |
| I3 | **Violated** | R(D) has no recorded link to R(CD): D's PID is freshly minted at BP_u2, no guid, no part lineage. "CD's parts were in the stack" exists nowhere in data | **data-model** (part-level membership out of scope by design, `MissionCrossTreeDock.cs:127-132`) — but see §3.4 for a partial derivation via route-window part-PID sets |
| I4 | **Violated** | Mission 1 (= T1's default mission) renders BCD (contains CD's matter), D, and the whole AD storyline with zero labeling of provenance; mission 2 renders a composition jump with no partner name (controller side never names partner either — recordings-UI extract §7.11) | **UI** on top of derivation gaps |
| I5 | **Violated** | In mission 1's loop, B's ghost doubles in size at dockUT (CD's half materializes) with no rendered approach and no label; in mission 2's loop (link ON), the combined stack "appears out of nowhere at the dock UT" (design's own words, `design-mission-crosstree-dock.md:163-166`) | UI |
| I6 | **Violated (latent)** | Two disjoint-tree missions may loop concurrently (pinned behavior); mission 1's BCD ghost and mission 2's CD ghost carry the same physical matter on independent clocks | enforcement/derivation |
| I7 | **Violated** | Extract §2 step 8: same-session variants put B's flight in T2, or make the A→D dock a genuine two-parent merge. Topology is a function of session boundaries and focus | **data-model consequence** of "tree = recorder location" |

The pattern: **the data model is single-writer** (one recorder, one active tree) while the domain is multi-party (two assemblies per dock). Everything the recorder saw is recorded well; everything about the *other* party is either a PID sidecar, an opt-in derivation, or nothing.

---

## 2. The owner's playback questions, answered precisely

Setup: mission 1 = T1's default mission (all intervals included by default — new topology defaults to included, `design-mission-abstractions.md` open question 3a). Mission 2 = T2's. Final topology per extract §2 step 7 (T1 owns R(AB), R(A), R(B), R(BCD), R(BC), R(D), R(A'), R(AD); T2 owns R(CD) only).

**(a) Looping mission 1: does B's ghost end at the dock with CD?**
**YES — cleanly.** R(B)'s window ends at dockUT (`ExplicitEndUT = mergeUT`, terminal `Docked`, `ParsekFlight.cs` CreateMergeBranch); when the shared clock passes dockUT, `DecideUnitMemberRender` returns `HiddenOutsideWindow` and the ghost is destroyed (`GhostPlaybackEngine.cs:2634-2643`). `Docked` is non-spawnable (`GhostPlaybackLogic.cs:7138-7149`), so nothing phantom-spawns. But "ends" is misleading to the player: the *mission* does not end — see (b).

**(b) Does the combined BCD render, and how?**
**YES.** R(BCD) is a T1 recording; its interval is the `@dock` sub-interval on B's line (`MissionComposition.cs`), included by default. It renders as **one combined-stack ghost** built from the post-couple merged snapshot, carrying the merged vessel's name (`ParsekFlight.cs:6131-6145` per extract; pinned by `MissionDockCompositionRuntimeTest`). Visually: B's ghost vanishes and a bigger ghost containing CD's geometry appears at the same spot in the same frame (one-frame epsilon window overlap at the seam is by construction). There is **no visual or label distinction** that half of this ghost is another mission's vessel. The composition label rebases to the combined counts (D2 rebase) but names no partner.

**(c) Does D — foreign matter — appear inside mission 1's loop?**
**YES, and more than D.** R(D) is a non-debris undock child in T1, an offshoot through-line with its own selectable interval, included by default. From undockUT to D's recorded end, D's ghost renders on mission 1's clock. Furthermore R(A'), and R(AD) (the A→D dock and merged stack) are also T1 members — mission 1's loop replays **the entire accreted history**: A, B, BCD, BC, D, A-continuation, AD, concurrently where windows overlap. "Mission 1" is no longer AB's story; it is "everything the recorder touched while T1 was active". This is the deepest narrative incoherence, and it is a *direct* consequence of tree accretion plus default-included selection — not a playback bug. Playback itself is self-consistent (every ghost's appearance is explained by a rendered branch event *within the tree*); the incoherence is that the player was told this is "mission AB".

**(d) Does mission 2's loop show anything after the dock?**
- **Link OFF (default): NO.** T2 holds only R(CD)'s pre-dock leg. The loop span is CD's own recording; the ghost retires at its recorded end. The dock, the docked stretch, D's departure, and D's later fate are all invisible. (In *non-loop* live-clock playback the docked stretch is still visible in the world — but as T1's committed BCD recording, unattributed to mission 2.)
- **Link ON (opt-in "Partner journey" row): PARTIALLY.** Members become: CD's own legs + R(BCD) + the journey walk past BP_u2. The walk prefers the child whose PID matches `PartnerPid = pid(CD)`; if CD survived as the merged PID, that child is R(BC); if B survived, no child matches and the fallback takes the first controlled child = R(BC) anyway (`MissionCrossTreeDock.cs:599-619`). **Either way the journey follows R(BC) and never R(D)** — even though CD's matter split across both children (C went to BC, D went to D). Mission 2 can *never* learn D's fate or the A→D dock: BP_d2's target is `pid(D)`, a fresh PID with no recording in T2, so `FindLinks` for T2 never matches it. Verified: this is the documented v1 "part-level membership out of scope" limitation meeting a scenario that maximally exploits it.
- Additionally there is the rendered **gap** `[CD's recorded end, dockUT]`: nothing renders CD during its unrecorded loiter, then the combined stack appears at dockUT (accepted contract; pinned windows in `CrossTreeDockLoopUnitInGameTest`).

**(e) UT gaps / double rendering across loops with different clocks?**
- **Gaps**: yes, two kinds, both "accepted contract" today: the partner loiter gap above, and — inside mission 1 — CD's *approach* is never rendered (T1 has no CD legs), so the dock has only one rendered participant. I5 violation, UI-classifiable.
- **Double render, different clocks: structurally possible, not prevented.** Two verified routes: (i) mission 1 (T1) and mission 2 (T2) may loop **concurrently** — the one-loop-per-spanned-tree-set enforcement treats them as disjoint until a link is included (pinned behavior #4 in the in-game test). Mission 1's loop renders the BCD ghost (contains CD's parts) at T1's clock while mission 2's loop renders the CD ghost at T2's clock. The same physical matter renders twice, at two different replay times, possibly kilometers apart. (ii) per-recording `LoopPlayback` on R(CD) is a separate mechanism from mission loops; nothing couples it to mission 1's unit (unit-membership exclusion applies only to T1's members). **UNVERIFIED in-game**: no test or log pins either collision visually; the claim is structural (member sets and enforcement sites verified, no code path connects them). Flagged as a real but latent I6 violation — its player-facing severity depends on how often docked stretches of two looping missions coexist.
- **Rendered-by-nobody at a UT where the player expects visibility**: within one mission's loop, only the accepted gaps above. Across the whole game world on the live clock: no gap — T1's committed recordings cover the merged spans, and terminal-spawn supersession (`RecordingStore.SupersedeTerminalSpawn.cs`) correctly prevents the absorbed CD from re-materializing.

**(f) The owner's verbatim phrasing, answered directly.**
- *"Does the mission end when it was supposed to dock?"* The **controller's** mission does not end — it continues through the merge into the combined stack. The **partner's** mission ends at its own recorded end (usually before the dock), and continues only via the opt-in link, rendered from the other tree's data.
- *"Does the mission continue when one of its initial stages undocks and reappears by itself?"* If the undock was recorded in this mission's tree (D from mission 1's perspective): **yes** — the departed stage is an included offshoot and renders concurrently. If the stage's matter belongs to this mission but the undock happened in another tree (D from mission 2's perspective): **no, and unrecoverably so** under the current data — the walk mis-follows to BC by design.

---

## 3. The tree-accretion problem

### 3.1 Is "tree = wherever the recorder was" sustainable?

As a **recording substrate**: yes. It is simple, single-writer, crash-safe, and playback-correct — the trajectory data is right, windows partition cleanly, and the combined-ghost model is visually honest. As a **narrative and mission-boundary substrate**: no. Concretely, what breaks downstream:

1. **Mission membership drifts from player intent.** The default mission over T1 silently grows to contain CD's docked matter and the entire AD storyline. Mission 2 starves to a stub. "Mission" (a player concept) is bound to "tree" (a recorder artifact). The player renames T1's mission "Mun Station Assembly" and it also contains an unrelated later rendezvous, because the recorder happened to be there.
2. **Partner journeys are single-PID walks over a first-child convention.** `ComputePartnerJourneyLegIds` is correct for the archetype it was designed for (dock, ride, depart as one piece) and provably wrong when partner matter splits across an undock (the D case). The fallback `ChildRecordingIds[0]` encodes *recorder focus*, not matter.
3. **Same-tree cross-session docks are invisible from the absorbed side** (A→D): neither a two-parent merge (BackgroundMap misses terminal-stamped leaves) nor a derivable link (FindLinks skips own tree). Pure derivation gap — the data (`BP_d2.TargetVesselPersistentId = pid(D)`, R(D) in the same tree) is sufficient.
4. **Loop-unit spanning and the one-loop rule enforce the wrong equivalence.** The "spanned tree set" is computed from *included links*, but physical overlap exists regardless of link inclusion (§2e). Enforcement keyed on trees cannot see matter.
5. **Archive/delete/re-fly hostage-taking.** Mission 2's only access to its own vessel's post-dock history lives in T1. Deleting/retiring T1's subtree (re-fly supersede of BCD, tree deletion) silently orphans mission 2's link (handled gracefully as `staleLinks`, but the *story* is gone). The unit signature folding in foreign tree counts (`MissionLoopUnitBuilder.cs:1563-1589` per extract) keeps the *cache* correct; nothing keeps the *narrative* correct.
6. **I7 nondeterminism** makes all of the above session-boundary-dependent: the same six physical events yield materially different trees depending on when the player quits to the KSC (extract §2 step 8). No UI explanation can be written for a structure the player cannot predict.

### 3.2 Alternative (a): status quo + better derivations — **recommended**

Keep the recording substrate exactly as is. Add, at load/refresh time, a **global dock-event graph**: nodes = all Dock/Board/Undock branch points across all committed trees; edges = participant resolutions via PID + launch-guid (reusing `VesselLaunchIdentity` exactly as `FindLinks` does). Three derivation extensions:

- **Same-tree links**: scan a tree's *own* single-parent Dock BPs whose `TargetVesselPersistentId` resolves (guid-gated) to a terminal-stamped recording in the same tree → recovers A→D from D's side and the cross-session same-tree shape. ~A sibling of `FindLinks` with the tree-inequality check inverted plus a "claimed recording is not a BP parent" guard.
- **Unconditional partner stamping** (one small recorder change, see rec #1 in §6): decouple `BranchPoint.TargetVesselPersistentId` from route eligibility so the graph has no blind docks.
- **Bidirectional naming**: from any merge node, both participants (owner recording + claimed recording) are resolvable → both UIs can name partners.

**Migration cost against format 1 / generation 4**: zero schema impact for the graph itself (pure derivation) and for same-tree links (data exists). Unconditional stamping writes an *existing* field on *new* recordings only — old recordings simply keep their zeros and the derivation degrades exactly as today; no generation bump, no migration path (consistent with the "one current contract" rule). **Playback risk**: none — nothing in the playback path consumes the graph unless a UI/loop feature opts in. **What it buys the UI**: named partners both directions, a mission event ledger (§4), same-tree dock affordances, and an honest basis for loop markers (§5).

**What it cannot buy**: D's material provenance from CD (I3). See §3.4.

### 3.3 Alternative (b): per-participant legs through merges + shared dock-event entity

Each participant keeps its own recording across the docked span; a first-class DockEvent entity referenced from both trees. **Assessment: poor value.** It requires per-part assignment of the merged trajectory and snapshot (which parts render under which recording), doubles ghost cost during docked spans, and makes the combined stack two half-ghosts that must visually seam — the current single-merged-snapshot ghost is *more* visually correct, not less. Schema: new recording shape → generation bump → **invalidates every existing recording** (hard constraint: no migration paths). Playback risk high (the merge/window partition invariants are load-bearing across flight/KSC/TS). The only genuine gain over (a) is that each mission could loop "its own" vessel through the dock — which the shared-span-clock cross-tree unit already approximates. **Reject.**

### 3.4 Alternative (c): physical-assembly world-lines first-class; trees historical

Promote a derived (or recorded) part-membership lineage: at every merge/split, persist which part sets went where; assemblies become the primary narrative entities and trees become storage. This is the *complete* fix for I2/I3/I4/I7 — and it is the v2 the design docs already gesture at ("part-level membership tracking is out of scope" — scoping language, not rejection).

Two paths:
- **Recorded**: stamp part-PID sets per branch child at split time. Additive sparse field on `BranchPoint` or child recordings; new recordings only, defaults empty. Arguably no generation bump if strictly best-effort metadata (readers tolerate absence); a bump if any playback path depends on it. Cost: moderate recorder change + part-PID stability questions (part PIDs are craft-baked too — the same collision caveats apply, mitigated by same-launch scoping).
- **Derived, partially, today**: `RouteConnectionWindows` on the merged child already persist **partner-scoped part-PID sets** (binding invariant list, `docs/dev/dock-undock-recording-structure.md` §9). Intersecting the undock children's snapshot part PIDs with the stored partner set would classify D as "made of CD's parts" and fix the journey walk's fork decision *for docked-partner cases specifically*. **UNVERIFIED feasibility**: whether split-child snapshots preserve live part PIDs (ghost snapshots use synthetic PIDs `100000 + idx*1111`; whether the branch snapshots retain real PIDs needs a targeted check before committing to this). Flagged as investigation item #8 in §6.

**Verdict**: (a) now, (c)-derived as a follow-up investigation, (c)-recorded only if the derived path dead-ends, (b) never.

---

## 4. Narrative communication proposals

Coordinated with issue-1: T1.4 (name same-tree two-parent dock partners), T2.2 (through-line flattening), T2.3 (chronological event digest) are assumed; the following extends them to the cross-tree/accretion problem.

### 4.1 The learnable rule (proposed player-facing contract)

> **"A mission records whatever you fly. When your vessel docks with another mission's vessel, the flight from that moment is recorded by the mission you're flying — the other mission shows the dock and links to it."**

Corollaries the UI must make true:
1. Both sides always *show* the dock event with the partner named and dated — "Docked with **CD** (mission *CD Freighter*)" on mission 1's side; "**B** (mission *AB*) docked with this vessel — combined flight recorded there →" on mission 2's side. Requires §3.2's graph; the controller side is today the *worse* side (no name at all — recordings-UI extract §7.11), which is backwards: the side that owns the data shows the least.
2. The shared span is **owned by the flying mission** and *labeled as shared* in both: mission 1's BCD rows/ghost carry "with CD"; mission 2's partner-journey row remains the opt-in way to pull the span into its own loop. This is honest to the data model and requires no schema change.
3. **Guest chapters are grouped.** In mission 1's Missions-tab hierarchy, the sub-stories that begin at a dock with foreign matter or at a switch-continuation (the A′→AD storyline, D-after-undock) get a group header — "Chapter: A rendezvous with D (from *CD Freighter*)" — with a single include-toggle for the whole chapter. This makes tree accretion *visible and editable* instead of silently inflating the mission. Derivable from the dock graph + `VesselSwitchContinuation` BPs; pure UI + derivation.
4. **Cross-navigation, not duplication**: every dock line gets a GoTo affordance to the partner mission (the Timeline→Missions reveal plumbing already exists, `MissionsWindowUI.RevealMissionForRecording`).

### 4.2 What "mission boundary" should mean

Do not pretend missions are matter-closed — they cannot be under this data model, and alternative (b) that would make them so is rejected. Instead the boundary is **narrative custody**: a mission's story is (its launch) + (everything flown under it) + (named events where custody transferred in or out). Vessels don't "migrate between missions"; **custody of the recording does**, and the UI says so at each transfer point. This is one sentence a player can learn, and it is *true* of the implementation.

### 4.3 The AB/CD story as the UI should tell it (target rendering, event-digest form per T2.3)

```
Mission "AB"                                Mission "CD Freighter"
  Y1 D10  Launched (A+B)                      Y1 D08  Launched (C+D)
  Y1 D12  A and B undocked                    Y1 D14  ← B (mission AB) docked; combined
  Y1 D14  B docked with CD (CD Freighter) →           flight recorded in "AB"  [Show journey]
  Y1 D16  D undocked from the station                 (journey: rode as BCD until D left)
  Y1 D20  A docked with D (from CD Freighter)→        Y1 D16  D departed — story continues
  ...                                                 in "AB" →   [today: IMPOSSIBLE — needs §3.4]
```

Everything on the left is derivable today + rec #1/#2; the right column's last line is the one item gated on D-provenance (I3).

---

## 5. Loop-coherence rules (testable)

Proposed as the explicit contract for "loop one mission", stated so each is a unit/in-game assertable:

- **R1 (shared clock, independent windows)** — *current behavior, keep and document*: all included members replay on one span clock; a member renders iff the clock is inside its trimmed window; multiple members render concurrently. Test: exists (`DecideUnitMemberRender` unit tests + `CrossTreeDockLoopUnitInGameTest` windows).
- **R2 (merge seam, partner not a member)** — when the clock crosses a Dock BP whose co-participant has no member legs, the combined ghost appears at dockUT (current) **and** a seam marker is shown for the seam's duration or on the ghost label: "joined by ⟨partner⟩ — see mission ⟨Y⟩". Test: marker entity present for `loopUT ∈ [dockUT, dockUT+Δ]`; absent otherwise.
- **R3 (line ends at a dock)** — when a member's window ends at a Dock BP and the merged child is *not* a member (partner mission with link off; excluded `@dock` interval), the ghost's disappearance is labeled: "docked to ⟨X⟩ — continues in mission ⟨Y⟩" (map marker or last-frame ghost badge), instead of a silent vanish. Test: at `loopUT > dockUT + ε` ghost hidden (current) + label emitted once per cycle.
- **R4 (foreign guests render, badged)** — members whose matter provenance is foreign (D, the BCD stretch from mission 2's view) render normally on the shared clock (semantic correctness requires it — hiding them would break I5), but carry a "guest" affordance in the Missions tab (§4.1.3 chapter grouping) so exclusion is one click. Do **not** introduce transparency/tint variation on ghosts for guest-ness — ghost visual budget is per-frame × per-ghost (Visual & Recording Design Principle), and a UI-side grouping is cheaper and clearer than a render-side treatment.
- **R5 (gaps are stated, never interpolated)** — unrecorded spans render nothing (current accepted contract, keep); the partner-journey row and the event digest state the gap explicitly: "(loiter, ⟨duration⟩ — not recorded)". Test: string assertion on the row.
- **R6 (double-clock advisory)** — when two missions whose dock graphs share a merge node both have loops enabled, surface an advisory at loop-enable time ("'CD Freighter' loops the same docked flight — ghosts may appear twice") rather than hard-clearing. Hard enforcement via `ClearLoopsConflictingWith` extended to graph-connected trees would regress the pinned "two disjoint-tree missions loop concurrently" behavior and punish the common harmless case (pre-dock spans rarely visually collide); an advisory preserves both. Test: advisory emitted iff dock graph connects the two trees.
- **R7 (cycle restart)** — *current, keep*: cycle change destroys/rebuilds member ghosts and replays events; members with later windows re-appear at their window start each cycle; watched member is hidden-not-destroyed until camera handoff.

R1/R7 are documentation of existing behavior; R2/R3/R5 are UI emissions at already-computed seams; R4 depends on §4.1.3; R6 depends on the dock graph.

---

## 6. Prioritized recommendations

| # | Change | Problem solved | Evidence | Cost | Risk | Depends on |
|---|---|---|---|---|---|---|
| 1 | **Stamp `BranchPoint.TargetVesselPersistentId` unconditionally at dock** (separate the branch-point partner stamp from route eligibility; keep the route gate for route windows only) | Route-ineligible docks are permanently underivable (I2-iii) — the fact is observed then discarded | `ParsekFlight.cs:10782-10787` → `:12135` | Small: recorder change + tests; existing field, new recordings only, **no schema bump** | Low; audit downstream consumers that assume `target≠0 ⇒ route-eligible` | — |
| 2 | **Same-tree dock link derivation** (sibling of `FindLinks` scanning own-tree single-parent Dock BPs, guid-gated) | A→D unreachable from D's line; cross-session same-tree docks invisible (I2-ii) | `MissionCrossTreeDock.cs:61-63`, `RecordingTree.cs:437`, extract §2 step 6 | Moderate: derivation + tests + a UI row | Low (pure derivation) | — |
| 3 | **Global dock-event graph at load** + bidirectional partner naming in both tabs (controller side included) | I2/I4; the controller side currently shows *nothing* about who joined | recordings-UI extract §7.11; §3.2 above | Moderate: one derivation module + label plumbing | Low | #1, #2; **merges with issue-1 T1.4** (same-tree two-parent case is T1.4; this generalizes it) |
| 4 | **Mission event digest fed by the graph** — the §4.3 story view, docks/undocks with named partners and GoTo links | Q1/Q9 from issue-1; I4/I5 narrative closure | §4 | 3–5 days on top of #3 | Low | #3; **is issue-1 T2.3 upgraded** — do them as one feature |
| 5 | **Chapter grouping + one-click include for accreted sub-stories** (dock-with-foreign / switch-continuation roots become group headers in mission selection) | Mission 1 silently containing the AD storyline; tree accretion invisible/uneditable (I4) | §2(c), §4.1.3 | ~1 week (selection UI + cascade-to-interval-keys write-through) | Medium (selection semantics; respect the non-cascading `ExcludedIntervalKeys` contract by expanding chapters to explicit key sets) | Benefits from issue-1 T2.2 flattening; not blocked by it |
| 6 | **Loop seam markers R2/R3 + gap statement R5** | Ghosts appearing/vanishing/doubling with no explanation inside loops (I5) | §2(a,b,d); `design-mission-crosstree-dock.md:163-166` | ~1 week (marker emission at seam UTs; strings) | Low-medium (flight-scene marker lifecycle) | #3 for partner names; can ship with placeholders before #3 |
| 7 | **Double-clock advisory (R6)** | Latent I6 double render of shared matter under two clocks | §2(e) — structural, **UNVERIFIED in-game**; verify with a two-loop playtest first | Days | Low (advisory only) | #3 |
| 8 | **Investigate D-provenance via route-window part-PID ∩ split-child snapshot part PIDs** | I3 — the only path to "D came from CD" without new recorded data; would also fix the journey walk's fork choice | `dock-undock-recording-structure.md` §9 (partner-scoped part-PID sets persist); **feasibility UNVERIFIED** (do split snapshots keep real part PIDs?) | Investigation: 1–2 days; implementation if viable: 1–2 weeks | Medium (part-PID craft-baked collisions; scope to same merged stack) | #3 |
| 9 | **Long-term: recorded part-set lineage at splits** (alternative (c)-recorded) | Complete I3, and I7's practical sting | §3.4 | Large; possible generation-bump question; **defer** until #8 resolves and #1–6 have soaked | High | #8 outcome |

**Explicitly not recommended**: alternative (b) per-participant merge legs (§3.3 — high schema/playback cost, visually worse than the merged ghost, marginal gain); hard one-loop enforcement across dock-graph-connected trees (regresses pinned concurrent-loop behavior); any change to the recording substrate's window-partition or merged-ghost contracts (they are the part of this system that is *right*).

**Bottom line.** Playback is internally consistent and the owner's fears about *contradictions* are mostly unfounded — loops don't contradict, they *omit* and *accrete*. The recording layer is sound; the failures are (i) three derivation blind spots around who docked with whom (all fixable without touching the schema), (ii) one genuine data-model hole (part-level provenance — D's origin), and (iii) a UI that never tells either side of a dock the other side's name. The AB/CD scenario is the worst case precisely because it routes matter through all three at once. Recommendations #1–#6 close everything except D-provenance for roughly three to four weeks of work with no schema bump; #8 decides whether the last hole is closable from data already on disk.
