# Parsek Dock Event Graph - Design Document

*Design specification for the cross-mission dock/undock narrative and loop-coherence fixes: unconditional partner stamping, same-tree dock-link derivation, a load-time global dock-event graph with bidirectional partner naming, a mission event digest, chapter grouping for accreted sub-stories, and loop seam markers.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies the "issue 2" fixes: recommendations #1 to #6 of the 2026-08-12 dock/loop-coherence analysis as committed scope, #7 (double-clock playtest + advisory) as a verification task, and #8 (D-provenance feasibility) as a spike with a go/no-go gate.*

**Status:** DESIGN (2026-08-12), pre-implementation
**Out of scope:** recommendation #9 (recorded part-set lineage at splits) is explicitly NOT in this scope; alternative (b) per-participant merge legs is rejected (analysis section 3.3); logistics route derivation across cross-tree pairs stays out (owned by `docs/dev/design-mission-crosstree-dock.md` section 7); the issue-1 presentation rework (staircase flattening, header summary) is owned by `docs/dev/research/mission-presentation-ux-analysis-2026-08-12.md` (the shared seam is section 8 of this doc).
**Related docs:** `docs/dev/research/crosstree-dock-loop-coherence-analysis-2026-08-12.md` (the authoritative analysis; its section numbers are cited as "analysis sN"), `docs/dev/research/dock-loop-model-extract-2026-08-12.md` (verified data-model extraction, "extract sN"), `docs/dev/dock-undock-recording-structure.md` (binding recorder contract), `docs/dev/design-mission-crosstree-dock.md` (M-MIS-8, extended here), `docs/dev/design-mission-abstractions.md` (mission layers, extended here).

---

## Open questions for the owner

Numbered decisions this design needs input on. Each carries a recommendation; the design below assumes the recommendation unless overridden. Status 2026-08-12: the two code-only questions (3, 10) are DECIDED per their recommendations by owner delegation; the remaining eight are player-facing. Of those, 5, 6 and 9 change playback behavior; 1, 2, 4, 7 and 8 are presentation defaults.

1. **Board merges.** v1 stamps the partner pid unconditionally on Dock merges only. Board merges pass no target pid at all today (`HandleTreeBoardMerge`, `ParsekFlight.cs:12260-12266` calls `CreateMergeBranch` with the default 0), so stamping them needs its own plumbing, and it would activate two consumer arms that are unreachable in production today (`GhostChainWalker` Board claims; `MissionCrossTreeDock.FindLinks` Board links, whose test `FindLinks_BoardClaim_Offered` passes against a hand-built fixture only). Recommendation: defer Board stamping; the graph models Board nodes as UnstampedZero and names them "(kerbal boarded)" from the EVA branch data instead. Include Board in PR1 only if you want boarding partners named now.
2. **EVA-grab couples.** `SuppressRouteWindowForEvaGrab` (`ParsekFlight.cs:5161-5173`) zeroes the partner pid when the couple involves an EVA kerbal. Recommendation: keep that suppression for the branch stamp too in v1 (an EVA grab is a kerbal-scale event, EVA already has its own branch-point type, and stamping it would mint ghost-chain claims keyed on kerbal pids). Override only if you want "grabbed by Bob Kerman" rows.
3. **GhostChainWalker widening.** DECIDED 2026-08-12 (owner delegated code-only questions to the recommendation): accept the widening for Dock, and add one Verbose line in `ScanBranchPointClaims` when a claim resolves no launch guid, so a spurious suppression is diagnosable. Context: unconditional stamping enlarges the claim population `GhostChainWalker.ScanBranchPointClaims` (`GhostChainWalker.cs:257`, `:298`) sees: docks whose partner failed both eligibility disjuncts get chains today's build never minted, and a partner with no recording anywhere yields a pid-only chain (null launch guid, so the #976 guid drop never fires) feeding the un-guid-gated `MergeCrossTreeLinks` (`:487-574`). Pid-only chains already exist today (snapshot-captured partners with no recording), so this is a marginal enlargement of an existing exposure, and the newly stamped population is small (partner pid nonzero, no snapshot, no known recording). The rejected alternative (gate the walker to claims whose pid resolves to a committed recording) would also have changed behavior for today's snapshot-only-partner stamps.
4. **NoMatch partner display.** A stamped dock whose partner pid resolves to no recording (in any tree) can either render "Docked with an unrecorded vessel" or stay silent like today. Recommendation: render the generic text in the digest only (it is honest and cheap), keep table rows silent.
5. **Seam marker surface and duration (R2/R3).** Proposal: one `ScreenMessages` line per loop cycle at the seam crossing, plus a ghost-label badge while `spanLoopUT` is inside `[seamUT, seamUT + 10s of loop time]`. No render-side tinting (analysis R4 rationale: ghost visual budget is per-frame x per-ghost). Confirm surface and the 10s window.
6. **Chapter exclusion vs new topology.** Excluding a chapter expands to explicit `ExcludedIntervalKeys`. A genuinely new branch recorded later inside that chapter's subtree defaults to INCLUDED (the standing open-question-3a contract, `design-mission-abstractions.md:688-694`), so it reappears in the loop despite the excluded chapter. Recommendation: accept and warn-log at reconcile ("chapter 'X' has new included topology"), consistent with 3a; auto-extending the exclusion would be a silent write to player selection state.
7. **Digest surfaces.** The event digest renders as a collapsed-by-default foldout per mission in the Missions tab (issue-1 T2.3's placement). Recommendation: Missions tab only in v1; the Recordings tab gets partner naming (tooltip/cell text) but no digest.
8. **Same-tree recovered links get no include-toggle.** A recovered same-tree link (the A->D case) names the partner, feeds the digest and chapters, and drives seam markers, but mints no "Partner journey"-style selection row: both sides already live in the same tree and are already selectable. Confirm that no selection affordance is wanted for the same-tree case.
9. **Advisory gate (#7).** The double-clock advisory (R6) ships only if the two-loop playtest (section 7.8) confirms a player-visible double render. If the playtest shows the collision is not visible in practice, we document the finding and drop the advisory. Confirm this gate.
10. **#8 spike GO threshold.** DECIDED 2026-08-12 (owner delegated code-only questions to the recommendation): the section 9.4 criterion stands as written - exact part-pid preservation on the BDOCK-1 fixture, plus a >= 90% / <= 50% overlap separation between the departing and continuing child.

---

## 1. Introduction

The recording layer around docks is sound: trajectories are right, windows partition at branch UTs, and the merged-stack ghost is visually honest. What is broken is the *narrative*: the data model is single-writer (one recorder, one active tree) while a dock is a two-party event. Everything the recorder saw is recorded well; everything about the other party is a pid sidecar, an opt-in derivation, or nothing. The analysis (s1.3) scores seven invariants and finds three derivation blind spots, one genuine data-model hole, and a UI that never names either side of a dock to the other.

This document commits the fixes that need no schema change:

- **#1** stamp `BranchPoint.TargetVesselPersistentId` unconditionally at dock, decoupled from route eligibility (today the fact is observed at `ParsekFlight.cs:10770-10787` and discarded when the eligibility gate fails).
- **#2** same-tree dock-link derivation, recovering docks like A->D from the absorbed side (today `MissionCrossTreeDock.FindLinks` skips the caller's own tree at `MissionCrossTreeDock.cs:61-63`, and `BackgroundMap` eligibility excludes terminal-stamped leaves at `RecordingTree.cs:437`, so a same-tree cross-session dock is invisible from the absorbed side).
- **#3** a load-time global dock-event graph (pure derivation module) plus bidirectional partner naming in the Missions and Recordings tabs.
- **#4** a mission event digest: chronological dock/undock story rows with named partners and GoTo cross-navigation. This IS issue-1's T2.3 in its upgraded form; one feature, not two (seam in section 8).
- **#5** chapter grouping in mission selection for accreted sub-stories, with one-click include/exclude expanding to explicit `ExcludedIntervalKeys` sets.
- **#6** loop seam markers R2/R3 and gap statements R5 (analysis s5).
- **#7** (verification task) the two-loop double-render playtest, then the R6 advisory if confirmed.
- **#8** (spike) D-provenance feasibility via route-window part-pid sets intersected with split-child snapshot part pids, with an explicit go/no-go gate.

### 1.1 What the player sees

| Situation | Today | After this design |
|-----------|-------|-------------------|
| B docks a foreign station CD; player opens mission AB | Composition label silently grows; no partner named anywhere | Interval row and digest read "Docked with CD (mission 'CD Freighter')" with a GoTo |
| Player opens mission CD Freighter | Nothing about the dock unless the opt-in link row is found and understood | Digest row: "B (mission 'AB') docked with this vessel; combined flight recorded there ->" |
| A docks D in a later session (same tree) | Dock recorded from A's side only; D's line just ends; nothing names D | Both sides named; digest shows the dock from both lines; D's line-end gets an R3 label |
| EVA grab, or dock to a vessel with no recording at dock time | `TargetVesselPersistentId = 0`; permanently underivable | Non-EVA docks stamp the pid; if the partner's tree is committed later, the link derives retroactively |
| Mission 1 loops; B's ghost doubles in size at dockUT | Unexplained; CD's half materializes with no label | R2 seam marker: "joined by CD - see mission 'CD Freighter'" |
| Mission 2 loops with link off; CD's ghost vanishes at its recorded end | Silent vanish, then nothing | R3 label at the window end: "docked to B - continues in mission 'AB'"; digest states the loiter gap (R5) |
| Mission 1 contains the whole accreted AD storyline | Indistinguishable rows silently inflating the mission | Chapter header "After docking with CD: D departed" / "Continuation: A -> AD" with a one-click include toggle |
| Two missions sharing a dock both loop | Same matter can render twice under two clocks, silently | (After the playtest confirms visibility) advisory at loop-enable time |

### 1.2 Worked example: the AB/CD scenario

Final on-disk topology (extract s2 step 7; T1 = AB's tree, T2 = CD's tree):

```
T2: R(CD) only - its solo pre-dock flight.
T1: R(AB) -> BP_u1(Undock) -> { R(B), R(A) }
    R(B)  -> BP_d1(Dock, target=pid(CD)) -> R(BCD)
    R(BCD)-> BP_u2(Undock) -> { R(BC), R(D) }
    R(A)  -> BP_switch(VesselSwitchContinuation) -> R(A') -> BP_d2(Dock, target=pid(D)) -> R(AD)
```

Graph nodes built from this (section 6): BP_d1 resolves CrossTree (pid(CD) + guid matches R(CD) in T2); BP_d2 resolves SameTreeRecovered (pid(D) + guid matches R(D) in T1, and R(D) is not a parent of BP_d2); BP_u1/BP_u2 are Undock nodes with no partner resolution. Target digest rendering (analysis s4.3):

```
Mission "AB"                                Mission "CD Freighter"
  Y1 D10  Launched (A+B)                      Y1 D08  Launched (C+D)
  Y1 D12  A and B undocked                    Y1 D14  <- B (mission AB) docked; combined
  Y1 D14  B docked with CD (CD Freighter) ->          flight recorded in "AB"  [Show journey]
  Y1 D16  D undocked from the station                 (loiter, 2d - not recorded)
  Y1 D20  A docked with D (from CD Freighter) ->
```

Everything in the left column and all but the last line of the right column is derivable from #1 + #2 + #3. The right column's missing last line ("D departed - story continues in AB") is the one item gated on D-provenance (#8): mission 2's journey walk follows R(BC), never R(D), because the walk is pid-based and pid(D) was freshly minted at BP_u2 (extract s2 step 5).

---

## 2. Design Philosophy

1. **The recording substrate is right; do not touch it.** Window partitioning at branch UTs, the merged-snapshot combined ghost, and the accepted-gap contract are load-bearing and correct (analysis s6 "explicitly not recommended"). Every fix here is a derivation or a label on top.
2. **Derive, never migrate.** Recording schema stays at format 1 / generation 4. The only recorder change (#1) writes an existing sparse key on new recordings; old recordings keep their zeros and degrade to exactly today's behavior. No migration paths, no compatibility shims.
3. **Identity is guid-gated, always.** A bare `persistentId` match is never proof of identity (pids are craft-baked). Every resolution in the graph routes through `VesselLaunchIdentity.GuidsConclusivelyDiffer` with the walker's unknown-guid pid-only fallback, exactly as `MissionCrossTreeDock.FindClaimedRecording` does (`MissionCrossTreeDock.cs:518-537`).
4. **The graph is pure and parameter-injected.** Like `MissionCrossTreeDock` and `GhostChainWalker`, the derivation core takes the tree list as a parameter and is headless-testable. The host cache supplies `RecordingStore.CommittedTrees` and an ERS-derived visibility predicate; the module itself never reads the store (ERS routing consequence: no allowlist entry needed, section 6.5).
5. **Nothing in the playback path consumes the graph except the opt-in features.** Ghost positioning, window math, spawn decisions, and the span clock are untouched. Seam markers are precomputed at unit build; the per-frame cost is one UT-window check.
6. **Build at load/refresh, never per frame.** The graph rebuilds on a topology signature change (the `MissionLoopUnitBuilder.BuildSignature` pattern); UI reads are dictionary lookups.
7. **Missions are narrative custody, not matter closure.** The player-facing contract (analysis s4.1): "a mission records whatever you fly; when your vessel docks with another mission's vessel, the flight from that moment is recorded by the mission you're flying - the other mission shows the dock and links to it." The UI's job is to make that sentence true at every transfer point.
8. **Degrade explicitly.** Every resolution failure mode has a named status and a defined UI rendering (section 6.4). Silence is only allowed where today is also silent.

---

## 3. Terminology

| Term | Definition |
|------|------------|
| Dock event node | One Dock/Board/Undock `BranchPoint` lifted into the graph, with its partner resolution attached. Not a new persisted entity. |
| Partner resolution | The classification of who the other party of a merge was: two-parent same-tree, cross-tree resolved, same-tree recovered, unstamped zero, no match, guid rejected. |
| Same-tree recovered link | A single-parent Dock/Board branch point whose target pid resolves (guid-gated) to a recording in the SAME tree that is not one of the branch point's parents: the A->D shape (extract s1.5). |
| Chapter | A sub-story of an accreted tree rooted at a `VesselSwitchContinuation` child or at a departure following a foreign-partner dock; rendered as a group header with a one-click include toggle. |
| Seam marker | A precomputed (UT window, kind, strings) descriptor attached to a loop unit; rendered when the shared span clock is inside the window. R2 = merge seam, R3 = line-end-at-dock. |
| Gap statement | The R5 string naming an unrecorded span ("loiter, 2d - not recorded") in the digest and partner-journey rows. Gaps are stated, never interpolated. |
| Digest row | One chronological entry of a mission's story: UT, verb, partner description, GoTo target. |

Design-concept-to-code mapping: DockEventGraph -> new `Source/Parsek/DockEventGraph.cs` (pure core) + `Source/Parsek/DockEventGraphCache.cs` (host cache); same-tree derivation -> new method region in `Source/Parsek/MissionCrossTreeDock.cs`; stamp decoupling -> `ParsekFlight.BuildMergeBranchData` and the pending-dock fields; seam markers -> `MissionLoopUnitBuilder` (computation) + `GhostPlaybackEngine`/`ParsekPlaybackPolicy` (emission); chapters + digest + naming -> `MissionsWindowUI` / `RecordingsTableUI` consuming the graph.

---

## 4. Existing Systems: What Changes vs What's New

| Component | Current behavior | Required change | Complexity |
|-----------|------------------|-----------------|------------|
| `ParsekFlight.cs` OnPartCouple (`:10770-10787`, retro path `:10888-10901`) | One gated pid (`pendingDockRouteTargetPid`) feeds branch stamp, route window, TransferKind, phantom supersede, endpoint proof | Add ungated `pendingDockPartnerPid` (= `ResolveDockPartnerPidFromEvent`, EVA-suppressed per Q2); thread to `BuildMergeBranchData`'s `branchTargetPid` local (`:5123`) only | Low |
| `ParsekFlight.BuildMergeBranchData` (`:5113-5151`) | `routeTargetPid`/`branchTargetPid` both = the gated parameter | New parameter (default 0) assigned to `branchTargetPid`/`:5133`; `:5144-5147` stay on the gated value | Low |
| `MissionCrossTreeDock.cs` | `FindLinks` scans other trees only | New `FindSameTreeDockClaims` sibling (tree-inequality inverted, parent-exclusion guard) | Medium |
| NEW `DockEventGraph.cs` / `DockEventGraphCache.cs` | - | Pure graph build + consumer API + signature-cached host | Medium |
| `MissionLoopUnitBuilder.cs` | Builds `LoopUnit` members/windows | Also computes `LoopSeamMarkers` for the unit (pure, from graph + member windows) | Medium |
| `GhostPlaybackEngine.cs` / `GhostPlaybackEvents.cs` / `ParsekPlaybackPolicy.cs` | No seam awareness | Engine raises a seam event on window entry (UT check only); policy renders ScreenMessage + label badge. Engine stays Recording-free (markers carry plain strings) | Medium |
| `UI/MissionsWindowUI.cs` | Partner-journey rows unnamed beyond vessel name; no digest; no chapters | Partner naming strings, chapter group rows, digest foldout (rendering shared with issue-1, section 8) | Medium |
| `UI/RecordingsTableUI.cs` | No dock partner info | Partner name in dock-boundary tooltip/cell via graph | Low |
| `GhostChainWalker.cs` | Claims from stamped BPs | No code change (Q3 recommendation); one new Verbose line on null-guid claims | Low |
| `MissionStore.cs` | Reconcile drops stale links | Also warn-logs new-topology-inside-excluded-chapter (Q6) | Low |

Explicitly unchanged: `RecordingTree.IsBackgroundMapEligible`, `CreateSplitBranch`, route-window capture (`RouteProofCapture`), `EffectiveState`, span-clock math, `MergeCrossTreeLinks`, the one-loop-per-spanned-set enforcement.

---

## 5. Data Model

### 5.1 New runtime-only types (never persisted)

```
DockEventNode (class; graph node, one per Dock/Board/Undock BranchPoint)
  BranchPointId: string        - the BP GUID (stable across optimizer churn)
  TreeId: string               - tree owning the BP
  UT: double                   - bp.UT
  Kind: BranchPointType        - Dock / Board / Undock
  MergeCause: string           - "DOCK" / "BOARD" (null for Undock)
  ParentRecordingIds: List<string>  - bp.ParentRecordingIds (copy)
  ChildRecordingIds: List<string>   - bp.ChildRecordingIds (copy; [0] = continuing child)
  PartnerPid: uint             - bp.TargetVesselPersistentId (0 for Undock nodes)
  Partner: DockPartnerResolution
```

```
DockPartnerResolution (struct)
  Status: DockPartnerStatus
  PartnerRecordingId: string   - resolved claimed recording (null unless resolved)
  PartnerTreeId: string        - tree of the claimed recording
  PartnerVesselName: string    - display name of the claimed recording's vessel
  PartnerLaunchGuid: string    - claimed recording's RecordedVesselGuid (may be null)
```

```
DockPartnerStatus (enum, explicit values for log stability)
  TwoParentSameTree = 0   - bp has two ParentRecordingIds; partner = the other parent
  CrossTree = 1           - single parent; pid+guid resolved in another committed tree
  SameTreeRecovered = 2   - single parent; pid+guid resolved in the SAME tree (A->D shape)
  UnstampedZero = 3       - PartnerPid == 0 (old recordings, EVA grabs, Board in v1)
  NoMatch = 4             - PartnerPid != 0 but no non-debris recording matches anywhere
  GuidRejected = 5        - pid matched but launch guids conclusively differ (logged)
```

```
DockEventGraph (class; the build output)
  Nodes: List<DockEventNode>                              - sorted by (UT, BranchPointId)
  NodesByBranchPointId: Dictionary<string, DockEventNode>
  NodesByTreeId: Dictionary<string, List<DockEventNode>>  - includes nodes RESOLVED INTO the tree
                                                            (a CrossTree node appears under both trees)
  ParticipantsByRecordingId: Dictionary<string, List<DockEventNode>>
  DockConnectedTreePairs: HashSet<(string, string)>       - for the R6 advisory
  BuildSignature: string
```

```
MissionEventRow (struct; digest row, consumed by issue-1's renderer)
  UT: double
  Verb: string                 - "Launched", "Undocked", "Docked with", "Boarded", "Gap", ...
  SubjectName: string          - the vessel on this mission's side
  PartnerText: string          - "CD (mission 'CD Freighter')" or "" / generic per Q4
  GapSeconds: double           - > 0 only on R5 gap rows
  GoToRecordingId: string      - reveal target (partner side), null when unresolved
  GoToMissionId: string        - partner mission id, null when unresolved
  SourceBranchPointId: string  - provenance for tests and logs
```

```
ChapterRoot (struct)
  Kind: ChapterKind            - SwitchContinuation = 0 / ForeignDockDeparture = 1
  RootRecordingId: string      - the chapter's first recording (R(A') or R(D))
  SourceBranchPointId: string  - the BP that starts the chapter
  Title: string                - "Continuation: <vessel>" / "After docking with <partner>: <vessel> departed"
```

```
LoopSeamMarker (struct; attached to GhostPlaybackLogic.LoopUnit, optional list)
  SeamUT: double               - the dock UT in recorded time
  WindowEndUT: double          - SeamUT + badge duration (Q5)
  Kind: SeamMarkerKind         - MergeAppear = 0 (R2) / DockedVanish = 1 (R3)
  MemberIndex: int             - the member whose ghost carries the badge
  Text: string                 - fully formatted; engine passes it through opaquely
```

Class-vs-struct rationale: nodes and the graph are classes (shared references across indexes); rows/markers/roots are small immutable value carriers.

### 5.2 Changes to existing types

**`GhostPlaybackLogic.LoopUnit`** (`GhostPlaybackLogic.SpanClock.cs:22-45`): one new optional field `SeamMarkers: List<LoopSeamMarker>` (null = none; default null keeps every existing construction site and test byte-identical). Runtime-only; the unit is never persisted.

**No `Mission` change.** Chapters expand to explicit `ExcludedIntervalKeys` entries; no new persisted field, no new exclusion namespace (the non-cascading contract of `MissionIntervalSelection` is respected, not changed).

**No `BranchPoint` / `Recording` shape change.** #1 changes only which values an existing field receives on NEW recordings.

### 5.3 Serialization

Nothing new is persisted. The one persistence-visible effect: `targetVesselPid` (sparse key, written only when nonzero, `RecordingTree.cs:621-622`, read leniently at `:711-717`) becomes present on more Dock branch points in new saves. The key predates this change and every build can read it; format 1 / generation 4 stay. Old saves load with 0 (field default) and every consumer degrades per section 6.4. The graph, digest rows, chapters, and seam markers are runtime-only and never written to any save.

---

## 6. Behavior: the dock-event graph (#1, #2, #3)

### 6.1 Unconditional partner stamping (#1)

**What changes.** At `OnPartCouple`, the partner pid is already resolved ungated by `ResolveDockPartnerPidFromEvent` (`ParsekFlight.cs:5898-5906`: whichever of from/to pid is not self) BEFORE the eligibility gate. Today the gated result is stored in one field, `pendingDockRouteTargetPid`, which fans out through `HandleTreeDockMerge` (`:12135`) and `CreateMergeBranch` to five surfaces. `BuildMergeBranchData` already splits the parameter into two locals (`:5122-5123`), assigned identically. The change:

1. New field `pendingDockPartnerPid`, set next to the existing assignment at `:10805` (live path) and `:10915` (retroactive path) to `partnerPidFromEvent` after self/zero filtering and the EVA suppression (Q2), WITHOUT the `partnerSnapshotCaptured || partnerKnown` disjunct.
2. Thread it as a new parameter (default 0) through `HandleTreeDockMerge` -> `CreateMergeBranch` -> `BuildMergeBranchData`, where it is assigned to `branchTargetPid` (`:5123`) and hence `BranchPoint.TargetVesselPersistentId` (`:5133`).
3. Everything else keeps the GATED value: `Recording.TransferTargetVesselPid` / `TransferKind` (`:5144-5147`), the route-window build gate (`:6147`), the endpoint-proof capture (`:5296-5299`), and the phantom-rover supersede call (`:6292-6294`). The supersede call is not a route surface but currently rides the gated variable; rewiring it to the ungated pid would newly fire suppression on non-route docks and is deliberately NOT done here (a possible follow-up, out of scope).
4. Board merges: unchanged in v1 (Q1). Cleanup: `pendingDockPartnerPid` resets alongside the existing pending fields (`:12151-2163`).

**Consumer audit.** Verified against the full repo (2026-08-12 audit); `TargetVesselPersistentId` on `BranchPoint` has exactly two semantic consumers plus plumbing. The name also exists as an UNRELATED field on `StockActionIntentMarker` and `ReFlySessionMarker`; those sites must not be touched.

| Consumer | Reads | Assumes target != 0 => route-eligible? | Verdict under unconditional stamping |
|----------|-------|----------------------------------------|--------------------------------------|
| `MissionCrossTreeDock.FindLinks` (`:69-73`) | offer gate for partner-journey links | No (partner identity only) | Safe; intentionally widened. New links appear only when the pid resolves to a non-debris, guid-compatible recording in the subject tree, so recording-less partners mint nothing |
| `GhostChainWalker.ScanBranchPointClaims` (`:257`, `:298`) | chain dictionary key (spawn suppression, cross-tree merge) | No (doc comment: "claiming events with a non-zero TargetVesselPersistentId") | Safe semantically; behavior-widening per Q3. Recording-less partners yield pid-only chains (null guid) feeding un-guid-gated `MergeCrossTreeLinks:487-574`; marginal enlargement of an existing exposure |
| `RecordingStore.SupersedeTerminalSpawn` (`:120-187`) | takes `absorbedPid` as a parameter, never the BP field; guid-gated internally (`:159-162`) | n/a | Safe; unchanged (still fed the gated value, see step 3 above) |
| `ParsekScenario.HydrationRepair.CloneBranchPoint` (`:864`) | verbatim field copy; repair predicate compares parent/child id lists only | No | Safe |
| `RecordingTree.cs` `:113` clone, `:621-622` save, `:716` load, `:641`/`:735` logs | value-transparent plumbing | No | Safe; sparse key written more often (save-diff noise only) |
| `Logistics/` (all files) | zero reads of the BP field; routes derive from `Recording.TransferTargetVesselPid` / `RouteConnectionWindow` | n/a | Fully insulated |
| `Analyzer/` (all 13 rules) | zero reads; `Inv7TreeTopology` never walks `tree.BranchPoints` | n/a | No rule fires or changes verdict |
| UI | zero direct reads; `MissionsWindowUI` renders link fields, never the pid | n/a | Safe |
| Type-only readers (`GhostingTriggerClassifier:96`, `RouteHarvestAnalysis:625`, `MissionComposition:189/:720`, `MissionRouteStructureList:372`, `Rendering/AnchorCandidateBuilder:179/:230`, `Rendering/AnchorPropagator:313`, `SupersedeCommit:1251`) | `bp.Type` only | n/a | Safe |

**Tests pinned on gated behavior** (update in PR1, only the named lines): `MergeEventDetectionTests.cs:54` and `:102` (assert 0 on no-target merges; survive if the new parameter defaults to 0), `:209` (drop the bp-target line only; `:210-211` TransferTargetVesselPid==0/TransferKind==None remain the test's point), `ClawCoupleRecordingTests.cs:106` (drop the bp-target line only; `:107-108` remain). `MissionCrossTreeDockTests.cs:242` (`FindLinks_ZeroTargetPid_Skipped`) mutates the fixture directly and stays valid.

**Retroactive derivability.** The concrete new capability: dock to a vessel that has no recording at dock time; the player later flies and commits that vessel's tree. Today the branch point carries 0 forever. After #1 it carries the pid, so the next graph rebuild resolves the link. This is the analysis's I2-iii fix.

### 6.2 Same-tree dock-link derivation (#2)

New pure method in `MissionCrossTreeDock` (same file, mirroring `FindLinks`):

```
FindSameTreeDockClaims(RecordingTree tree) -> List<SameTreeDockClaim>
  SameTreeDockClaim { BranchPointId, DockUT, ClaimType, PartnerPid,
                      ClaimedRecordingId, MergedChildRecordingId }
```

Scan the tree's OWN `BranchPoints` for `Type in {Dock, Board}` with `TargetVesselPersistentId != 0` and a single parent (`ParentRecordingIds.Count == 1`; a two-parent merge already closes the partner's line and needs no recovery). Resolve the claimed recording with the exact `FindClaimedRecording` rules (non-debris, pid match, guid-gated via `VesselLaunchIdentity.GuidsConclusivelyDiffer`, earliest `StartUT` wins) restricted to the same tree, plus two guards `FindLinks` does not need:

1. **Parent exclusion:** the claimed recording must not be in `bp.ParentRecordingIds` (otherwise this is the recorder's own incoming line, not a recovered partner).
2. **Merged-child exclusion:** the claimed recording must not be `bp.ChildRecordingIds[0]` and must not START at the dock UT (the merged child and post-dock continuations carry the surviving pid when the partner won the pid contest, extract s2 step 4; claiming them would name the stack as its own partner). Earliest-start preference makes this near-automatic; the explicit guard makes it testable.

Output feeds the graph only. No selection affordance (Q8), no loop-unit membership change, no journey walk: both sides of a same-tree link are already selectable members of the same tree. The A->D case resolves as: BP_d2 (target = pid(D)) claims R(D), which is terminal-stamped and parentless relative to BP_d2.

### 6.3 Graph build algorithm (#3)

`DockEventGraph.Build(IReadOnlyList<RecordingTree> trees, Func<string, bool> isRecordingVisible)` - pure, one synchronous pass:

1. For each tree, for each `BranchPoint` with `Type in {Dock, Board, Undock}`: create a `DockEventNode` (copying id lists). Undock nodes get `Partner.Status = UnstampedZero` and no resolution.
2. For each merge node, resolve the partner, first match wins in this order:
   a. `ParentRecordingIds.Count == 2` -> `TwoParentSameTree`. The partner RELATIVE TO A VIEWER is the other parent; the node stores both parents and `TryDescribePartner` picks at query time.
   b. `PartnerPid == 0` -> `UnstampedZero`.
   c. Cross-tree resolution: for each OTHER tree, apply the `FindLinks` claim rule (resolve the foreign launch guid via `ResolveLaunchGuidForPid` semantics against the OWNING tree, then `FindClaimedRecording` against the candidate tree). Earliest-starting match across trees wins -> `CrossTree`.
   d. Same-tree resolution per section 6.2 -> `SameTreeRecovered`.
   e. Any pid match rejected by a conclusive guid mismatch, with no other match -> `GuidRejected` (counted and logged; the one case where an affordance vanishes with no other trace, mirroring `FindLinks`' logging rationale).
   f. Otherwise -> `NoMatch`.
3. Visibility: a node whose merged child (or, for Undock, whose parent) fails `isRecordingVisible` is still BUILT (topology is tree-level; there is no supersede filter over branch points) but flagged; digest/naming/chapter consumers skip flagged nodes so a re-fly-superseded dock does not surface phantom story rows. The predicate is supplied by the host from `EffectiveState.ComputeERS()` ids; the pure core never touches the store.
4. Indexes: `NodesByTreeId` registers a node under its owning tree AND under `Partner.PartnerTreeId` when that differs (so mission CD's digest sees BP_d1). `ParticipantsByRecordingId` registers every parent, the merged child, and the resolved partner recording. `DockConnectedTreePairs` collects `(TreeId, PartnerTreeId)` pairs from `CrossTree` nodes.
5. One Verbose summary line (section 12).

Complexity: O(sum BranchPoints x resolution scan). Resolution scans tree recording dictionaries; with per-tree pid->recordings prebuilt maps the pass is linear in recordings. Committed stores are hundreds of recordings, tens of branch points; the build is well under a millisecond and runs only on signature change.

**Host cache** (`DockEventGraphCache`, static): `GetOrBuild()` recomputes the signature = `RecordingStore.StateVersion` + `ParsekScenario.SupersedeStateVersion` + per-tree `(BranchPoints.Count, Recordings.Count)`, rebuilds on mismatch. Callers: UI draw paths (per-frame dictionary lookups against the cached graph), `MissionLoopUnitBuilder` (seam markers), reconcile. Reads `RecordingStore.CommittedTrees` (a tree-level surface with no ERS filter defined over it; mechanically outside the ERS grep gate, and semantically coherent since ERS is a recording-level set) and derives the visibility predicate through `EffectiveState.ComputeERS()` (which routes correctly). Result: **no `ers-els-audit-allowlist.txt` entry needed**; this is the `MissionCrossTreeDock`/`GhostChainWalker` precedent. If a future consumer ever needs committed-LIST indices from the graph, that consumer inherits the `RouteOrchestrator` alignment rationale and must be allowlisted itself; the graph API therefore exposes recording IDS only, never indices.

### 6.4 Degradation table

The hard constraint restated: old recordings with `TargetVesselPersistentId = 0` degrade to exactly today's behavior.

| Input state | Node status | Naming (both tabs) | Digest | Chapters | Seam markers |
|-------------|-------------|--------------------|--------|----------|--------------|
| Old recording, target = 0 | UnstampedZero | none (today's behavior) | row shows verb + date, no partner text | no foreign-departure chapter (switch-continuation chapters still derive; they never needed the pid) | R2/R3 fire with generic text ("joined by another vessel") since the seam itself is real; text has no partner name |
| Target != 0, partner recording exists, guid unknown on either side | CrossTree / SameTreeRecovered (pid-only fallback, walker parity) | named | named | derives | named |
| Target != 0, guids conclusively differ | GuidRejected | none | verb only | no | generic |
| Target != 0, no recording anywhere | NoMatch | none (tables); digest per Q4 | generic text per Q4 | no | generic |
| EVA grab (Q2 default) | UnstampedZero | none | EVA rows come from the EVA branch type, not from the merge pid | no | n/a |
| Board merge (Q1 default) | UnstampedZero | "(boarded)" from branch type | verb only | no | n/a |
| Two-parent same-tree merge | TwoParentSameTree | named (this is issue-1's T1.4 case, generalized) | named | no (same-tree, both sides present) | R3 applies to the co-parent's line end when the merged child is excluded |
| Partner recording superseded (re-fly) | resolved but visibility-flagged | skipped | skipped | root skipped | marker built only from live members |

### 6.5 Bidirectional partner naming (consumer API)

```
DockEventGraph.TryDescribePartner(
    graph, branchPointId, viewerRecordingId, missionNameResolver)
  -> DockPartnerDescription { PartnerVesselName, PartnerMissionName,
                              PartnerRecordingId, PartnerMissionId, Status }
```

`viewerRecordingId` decides direction: for a TwoParentSameTree node the partner is the parent that is not on the viewer's line; for CrossTree/SameTreeRecovered nodes, if the viewer is the claimed recording the partner is the merged stack (controller side), else the claimed recording (partner side). `missionNameResolver` is a `Func<string treeId, string recordingId, string>` supplied by the UI layer (resolving through `MissionStore`), keeping the core free of Mission references.

Surfaces: `MissionsWindowUI` interval rows and link rows ("Docked with CD (mission 'CD Freighter')"); `RecordingsTableUI` dock-boundary cells/tooltips. The controller side, today the worse side (recordings-UI extract s7.11: no name at all), gets naming from the same call.

---

## 7. Behavior: consumers (#4, #5, #6, #7)

### 7.1 Mission event digest (#4 = issue-1 T2.3, one feature)

`DockEventGraph.BuildEventDigest(graph, tree, missionSelection, missionNameResolver) -> List<MissionEventRow>`, pure:

1. Launch row from the tree root(s) (earliest recording per disconnected root).
2. One row per graph node registered under the tree (owning or resolved-into), skipping visibility-flagged nodes and debris-only undocks: "A and B undocked", "B docked with CD (mission 'CD Freighter')", "<- B (mission 'AB') docked with this vessel; combined flight recorded there".
3. R5 gap rows: when a partner-journey link is included and the claimed recording's end precedes the dock UT by more than a threshold (60s), emit `Verb = "Gap"` with `GapSeconds` ("loiter, 2d - not recorded"). Same rule for any member window whose start is a dock the mission cannot render.
4. Terminal row per through-line end (verb from the terminal state).
5. Rows sorted by UT; `GoToRecordingId`/`GoToMissionId` populated from the resolution so the renderer wires GoTo through the existing `MissionsWindowUI.RevealMissionForRecording` plumbing.

Rendering (foldout placement, row layout, GoTo affordance) is issue-1's; the row contract is this struct (section 8).

### 7.2 Chapter grouping (#5)

`DockEventGraph.CollectChapterRoots(graph, tree) -> List<ChapterRoot>`, pure. Chapter roots:

1. **SwitchContinuation:** every child recording of a `VesselSwitchContinuation` branch point (the A' -> AD storyline). Derivable from tree topology alone; no pid needed.
2. **ForeignDockDeparture:** every non-continuation (not `ChildRecordingIds[0]`), non-debris child of an Undock node whose parent recording is the merged child of a Dock/Board node with `Partner.Status in {CrossTree, SameTreeRecovered}` (D-after-undock: "the piece that departed after the rendezvous"). This is a heuristic ("departed after a foreign dock", not "made of foreign matter" - that is #8's question) and is labeled as such in the title string.

UI: `MissionsWindowUI` renders a group header row per chapter above the chapter's vessel rows, title from `ChapterRoot.Title`, with one tri-state include checkbox (all / mixed / none). Toggling calls:

```
DockEventGraph.ExpandChapterToIntervalKeys(chapterRoot, compositionRoots, structure)
  -> HashSet<string>
```

which walks the through-lines reachable downstream from `RootRecordingId` (following branch children, skipping debris) and collects every selectable composition interval key they own (head keys, `/segN`, `@dockM`). Exclude = union those keys into `Mission.ExcludedIntervalKeys`; include = remove them. **The non-cascading contract is respected, not changed:** the expansion writes explicit keys through the existing `ExcludedIntervalKeys` mechanism; individual interval checkboxes keep exactly their current semantics, and `ReconcileSelections` keeps validating keys as today (plus the Q6 warn line). No new persisted state.

### 7.3 Loop seam markers R2/R3 (#6)

Computed at unit build time in `MissionLoopUnitBuilder` after member windows are final, from the cached graph:

- **R2 (merge seam, partner not a member):** for each Dock node whose merged child IS a unit member and whose resolved partner recording (or, for TwoParentSameTree, co-parent) is NOT a member: emit `MergeAppear` at `SeamUT = dockUT`, `MemberIndex` = the merged child's member index, `Text = "joined by <partner> - see mission '<Y>'"` (generic text on unresolved statuses per section 6.4).
- **R3 (line ends at a dock):** for each member whose trimmed window END coincides (epsilon `LoopTiming.BoundaryEpsilon`) with a Dock node's UT where the merged child is NOT a member (partner mission with link off; excluded `@dock` interval): emit `DockedVanish` at that UT on that member, `Text = "docked to <X> - continues in mission '<Y>'"`.

Markers are sorted by `SeamUT` and stored on the unit. Runtime: `GhostPlaybackEngine.UpdateUnitMemberPlayback`, in the existing per-member pass, checks whether `spanLoopUT` entered a marker window (an index cursor over the sorted list; O(1) amortized, one comparison per member per frame - within the "UT-window check" budget). On entry it raises a `SeamMarkerEvent` through `GhostPlaybackEvents` carrying the marker's strings; once per cycle (dedup by `(markerIndex, unitCycle)`, cleared on cycle change alongside the existing completed-event dedup at `GhostPlaybackEngine.cs:2729-2737`). `ParsekPlaybackPolicy` renders: one `ScreenMessages` line + the ghost-label badge for the window duration (Q5). The engine stays Recording-free: markers carry preformatted strings.

R2/R3 do not fire in KSC/TS in v1 (single-instance scenes; ScreenMessages there would be ambient noise) - flight scene only.

### 7.4 Gap statements R5

String emission only, two sites: the partner-journey row in `MissionsWindowUI` (append "(loiter, <duration> - not recorded)" when the claimed recording's end < dock UT) and the digest gap rows (section 7.1). Formatting via the existing duration formatter, InvariantCulture.

### 7.5 What loops do NOT change

R1 and R7 (analysis s5) are current behavior, kept and documented: shared clock with independent windows; no cross-member selection; cycle restart destroys/rebuilds ghosts; watched member hidden-not-destroyed until camera handoff. R4's decision is adopted: foreign guests render normally, un-tinted; guest-ness is a Missions-tab grouping concern (chapters), never a render-side treatment.

### 7.6 Missions/Recordings tab naming

Both tabs consume `TryDescribePartner`. Missions tab: the dock interval row's Start-event cell (issue-1 T1.4 slot) and the link rows. Recordings tab: the chain-block dock boundary tooltip. No new windows.

### 7.7 R6 advisory (#7, gated on the playtest)

If Q9's playtest confirms visibility: at `MissionStore.SetLoopEnabled`, when the enabling mission's tree is dock-connected (via `DockConnectedTreePairs`, transitively) to another mission's tree that already loops, post a `ScreenMessages` advisory ("'CD Freighter' loops the same docked flight - ghosts may appear twice") and an Info log. NO hard enforcement: extending `ClearLoopsConflictingWith` to graph-connected trees would regress the pinned "two disjoint-tree missions may loop concurrently" behavior (`CrossTreeDockLoopUnitInGameTest` pinned behavior #4) and punish the common harmless case.

### 7.8 The double-clock playtest (verification task)

Before building 7.7: reproduce analysis s2(e) live. Fixture: BDOCK-1's recorded save (or the synthetic AB/CD injection); enable mission 1's loop (T1) and mission 2's loop (T2, link off) simultaneously; observe whether the BCD-stretch ghost (T1 clock) and the CD ghost (T2 clock) are concurrently visible at diverged replay times in flight/map. Deliverable: a short research note with screenshots/log excerpts, filed under `docs/dev/research/`, and the Q9 decision. The analysis marks this UNVERIFIED; no code ships on an unverified severity claim.

---

## 8. The issue-1 seam (T1.4 / T2.3)

Two workstreams touch the same surfaces; the split:

| Artifact | Owner | Contract |
|----------|-------|----------|
| `DockEventGraph.cs`, `DockEventGraphCache.cs`, same-tree derivation, stamp decoupling, seam markers, chapter derivation + expansion | issue-2 (this doc) | - |
| `DockPartnerDescription` + `TryDescribePartner` signature | issue-2, defined in this doc | issue-1's T1.4 renders through this call and does NOT implement its own branch-point parent lookup |
| `MissionEventRow` struct + `BuildEventDigest` | issue-2 | issue-1's T2.3 renders these rows; T2.3 and #4 are ONE feature |
| `MissionsWindowUI.cs` header block, interval-row label refactors (T1.3), tooltips (T1.5), digest foldout rendering + GoTo wiring | issue-1 | consumes the two contracts above |
| `MissionsWindowUI.cs` chapter group rows (new draw region), link-row partner naming strings | issue-2 | placed in a separate `DrawChapterRows` region to avoid textual collision with issue-1's row refactor |
| `RecordingsTableUI.cs` dock tooltip | issue-2 | small, isolated cell change |

**Landing order.** PR1/PR2 (recorder + derivation) have no UI contact and land first in any order. If issue-1's Tier-1 lands before the graph (PR3): T1.4 ships as a thin helper with the FINAL `TryDescribePartner` signature handling only the TwoParentSameTree case (a local `BranchPoints` parent lookup); PR3 replaces the helper's internals, zero call-site churn. If issue-1 wants T2.3 before PR5: it renders interval-boundary rows only, and swaps its row source to `BuildEventDigest` when PR5 lands; the row struct is already fixed by this doc so the renderer does not churn. Coordination rule: neither workstream edits the other's regions; shared types live in the graph files (issue-2's).

---

## 9. The #8 spike: D-provenance feasibility

### 9.1 Question

Can "D is made of CD's parts" be derived from data already on disk, fixing (a) mission 2's journey walk mis-following to R(BC) at BP_u2, and (b) the digest's missing "D departed - story continues in 'AB'" line?

### 9.2 Data candidates

`RouteConnectionWindow` on the merged child persists partner-scoped part-pid sets (`TRANSPORT_PART_PIDS` / `ENDPOINT_PART_PIDS`, `dock-undock-recording-structure.md` s8 layout, s9 invariant 4: "part-PID sets must be partner-scoped, not merged-scoped"). Undock children receive snapshots at `CreateSplitBranch` (`ParsekFlight.cs:5412-5413`). Proposed classifier: intersect an undock child's snapshot part-pid set with the window's partner set; high overlap = "made of partner matter".

### 9.3 Verification steps (1-2 days)

1. **Code read:** trace the undock-child snapshot path (`CreateSplitBranch` -> `VesselSpawner.TryBackupSnapshot` / `CollectPartPersistentIds`) and answer: do split-child snapshots preserve LIVE part `persistentId`s, or are pids re-minted/synthesized? Caution: `VesselSnapshotBuilder.AddPart` assigns synthetic pids (`100000 + idx*1111`), but that is the TEST generator; the live capture path must be verified independently, and ghost-visual snapshots may differ from vessel snapshots. Also verify which snapshot (VesselSnapshot vs GhostVisualSnapshot) survives on committed undock children after optimization.
2. **Fixture check:** run BDOCK-1 (or reuse its recorded fixture; note the machine lock contract in `.claude/CLAUDE.md` before any harness run) and inspect the produced save: does the departing child's snapshot pid set sit inside the window's partner set, and does the continuing child's not?
3. **Collision analysis:** part pids are craft-baked like vessel pids. Scope every intersection to one merged stack (same tree, the window on the direct dock ancestor), and require the dock and undock to bracket the child (window `DockUT <= childStartUT <= UndockUT + epsilon`), so cross-launch pid collisions cannot enter the comparison.

### 9.4 Go/no-go criterion

**GO** iff all three hold: (a) split-child snapshots preserve live part pids exactly (on the BDOCK-1 fixture, every child snapshot pid is an element of the merged vessel's pid set); (b) the classifier separates cleanly on the fixture: departing child overlap with the partner set >= 90%, continuing child overlap <= 50% (thresholds per Q10); (c) the classification is computable at graph build time from persisted data only (no live vessel, no per-frame work). **NO-GO** if pids are re-minted, the sets are absent on committed recordings, or the overlap is ambiguous.

On GO: a design addendum to this doc specifying `ClassifyUndockChildProvenance` feeding (i) `FindBranchSuccessor`'s fork decision (partner-matter child preferred over the pid match / first-controlled fallback) and (ii) the digest's continuation line - implementation as its own follow-up scope, not this one. On NO-GO: document the dead end in the addendum, keep the journey walk as-is (its mis-follow stays "logged, acceptable v1" per `design-mission-crosstree-dock.md` s3), and the decision on #9 (recorded lineage) goes back to the owner - #9 remains out of scope regardless.

---

## 10. Edge Cases

1. **Old recording, target = 0.** Node = UnstampedZero; every consumer degrades per section 6.4; behavior identical to today. Guarded by dedicated degradation tests.
2. **EVA grab couple.** Stamp suppressed (Q2); node UnstampedZero; EVA narrative comes from the EVA branch type.
3. **Claw grab (`MergeCause="DOCK"`, Grapple kind).** Stamped like any dock; digest verb stays "Docked with" in v1 (the known cosmetic `MergeCause` gap, `todo-and-known-bugs.md` M-MIS-10 note, is not widened here).
4. **Board merge.** Unstamped in v1 (Q1); digest verb "Boarded" from branch type.
5. **Partner survived as the merged vessel (mergedPid == partner pid).** The same-tree guard (6.2 guard 2) prevents the merged child claiming itself; cross-tree resolution is unaffected (the claimed recording lives in the other tree and starts before the dock).
6. **Target pid equals a co-parent's pid on a two-parent merge.** Resolution order 6.3(2a) classifies TwoParentSameTree first; the same-tree scanner only sees single-parent BPs. No double resolution.
7. **Guid-less recordings on either side.** Pid-only fallback (walker parity); resolution proceeds; `GuidsConclusivelyDiffer` returns false on unknown.
8. **Craft-baked pid collision across launches.** Guid gate rejects (GuidRejected, logged). A guid-less collision resolves pid-only - same accepted residual as `FindLinks` today, bounded by the debris exclusion and earliest-start preference.
9. **Partner tree committed AFTER the dock.** NoMatch at first build; `RecordingStore.StateVersion` changes on commit; rebuild resolves CrossTree. The retroactive-derivability case of 6.1.
10. **Partner tree deleted / subtree superseded by re-fly.** Stale nodes drop on rebuild (deletion) or get visibility-flagged (supersede); digest/naming/chapters skip them; the existing `staleLinks` handling in `ReconcileSelections` is untouched.
11. **NotCommitted provisional trees (active re-fly).** The graph builds over committed trees only; the provisional's dock events appear after commit. No marker/naming for in-flight recordings.
12. **Parked (Limbo) trees during OnLoad.** Graph consumers defer exactly as `ReconcileSelections` does (`design-mission-crosstree-dock.md` s5c); the cache host does not rebuild mid-OnLoad while a parked tree is uncommitted.
13. **Disconnected roots (post-switch recordings with no incoming edge).** Digest emits a Launch-like row per root; chapters of kind SwitchContinuation require the BP, so a truly disconnected root is a plain root row, not a chapter.
14. **Multiple docks inside one structural interval.** `@dockM` ordinals already exist (`MissionComposition.cs:280-288`); digest rows are per-BP so each dock gets its own row; chapter expansion collects all `@dockM` keys.
15. **Merge UT coincident with a structural edge (no `@dock` key minted).** Digest still shows the dock row (BP-driven, not key-driven); chapter expansion relies on keys that exist, so nothing dangles.
16. **Seam marker at the window boundary epsilon.** R3 matches window end within `LoopTiming.BoundaryEpsilon`; the one-frame overlap where both pre-dock and merged members render (SpanClock `:1749-1754`) must not double-fire - dedup by `(markerIndex, unitCycle)`.
17. **Watched member hidden-not-destroyed at its window end.** R3's ScreenMessage still fires (event is clock-driven, not destroy-driven); the label badge attaches only while the ghost object exists.
18. **Excluded `@dock` interval with link on (pinned behavior #9 of the in-game test).** The merged child is not a member; R3 fires on the pre-dock member's window end with the partner text; R2 does not (no member appears).
19. **Cadence > span (inter-cycle tail).** All members hidden; markers cannot fire (`spanLoopUT` never enters a window during the tail); the cursor resets on cycle change.
20. **Self-overlap (flight scene, multiple staggered instances).** v1 emits markers for the single-instance branch only; the overlap branch (per-recording overlap loops) skips marker checks entirely (documented limitation - overlap instances already accept reduced fidelity, and per-instance markers would multiply ScreenMessages).
21. **Two looping missions, both with markers.** Independent units, independent marker lists; no cross-unit state. The R6 advisory (if shipped) fires at enable time only.
22. **Chapter excluded, then new topology recorded inside it.** Default-included per open-question-3a; warn-logged at reconcile (Q6).
23. **Chapter root recording id churned by the optimizer.** Through-line heads keep the earliest segment's id under split/merge (`design-mission-abstractions.md` open question 3b), and chapter roots are branch children (heads by construction), so expansion keys stay stable. Guarded by a test over `RecordingOptimizer.SplitAtSection`.
24. **Digest of a tree with zero dock events.** Launch + terminal rows only; foldout renders "(no events)" - no crash on empty graph slices.
25. **Same-tree claim where the claimed recording is debris.** Excluded (debris never matches, same as `FindLinks`); prevents false chapters/naming off colliding debris pids.

---

## 11. What Doesn't Change

- Recording schema: format 1 / generation 4; no new keys, no migration paths.
- The recorder's dock/undock event handling: `OnPartCouple` capture order, `onVesselsUndocking` authority, `CreateSplitBranch`/`CreateMergeBranch` shapes, route-window lifecycle and its gate, `TransferTargetVesselPid`/`TransferKind` population, phantom-rover supersede inputs.
- `RecordingTree.IsBackgroundMapEligible` and `BackgroundMap` semantics.
- Window partitioning at branch UTs; the merged-ghost render contract (one combined-stack ghost from the post-couple snapshot); `Docked`/`Boarded` non-spawnable terminals.
- Span-clock math, `DecideUnitMemberRender`, member windows, cross-tree fail-closed periodicity, one-loop-per-spanned-set enforcement, and the pinned "two disjoint-tree missions may loop concurrently" behavior.
- `MissionCrossTreeDock.FindLinks` semantics and the partner-journey walk (until/unless #8 goes GO in its own follow-up scope).
- `Mission` persistence shape; `ExcludedIntervalKeys` non-cascading semantics; the sparse `foreignDockLink` codec and its byte-identity pins.
- `EffectiveState` ERS/ELS computation and the grep gate; this design adds no allowlist entry.
- Everything asserted by `CrossTreeDockLoopUnitInGameTest` and `MissionDockCompositionRuntimeTest` (both `Category = "Missions"`).

## 12. Out of Scope

- #9 recorded part-set lineage at splits (deferred until #8 resolves and #1-#6 have soaked; owner decision).
- Alternative (b): per-participant legs through merges (rejected, analysis s3.3).
- Hard one-loop enforcement across dock-graph-connected trees (regresses pinned behavior).
- Cross-tree periodicity / phase-lock / re-aim (stays fail-closed).
- Logistics route derivation across cross-tree pairs (`RouteBackingMission` stays single-tree).
- Rewiring the phantom-rover supersede to the ungated pid (noted follow-up, 6.1 step 3).
- Ghost render-side guest treatment (tint/transparency) - R4's rejected option.
- KSC/TS seam markers (flight-scene only in v1).

---

## 13. Backward Compatibility

No format or generation change. Old saves on the new build: branch points load with `TargetVesselPersistentId = 0` where they were 0; the graph classifies UnstampedZero; every surface degrades to today's behavior (section 6.4 table). New saves on an old build: the `targetVesselPid` key is read by every existing build (`RecordingTree.cs:711-717` predates this design); an old build simply ignores the wider population the same way it ignores today's stamps it has no consumer for. No migration stance needed because no contract changed; this is the "one current contract" rule holding, not an exception to it.

---

## 14. Performance Budget

| Operation | Frequency | Budget | Justification |
|-----------|-----------|--------|---------------|
| Graph build | on topology signature change only (load, commit, supersede, delete) | < 5 ms at 1000 recordings / 100 branch points | linear pass with prebuilt pid maps; never per-frame |
| UI naming / digest / chapter reads | per frame while a window is open | dictionary lookups against the cached graph; digest list cached per mission per signature | mirrors the composition pipeline's frame-cache pattern |
| Seam marker computation | at loop-unit build (already signature-cached) | O(nodes + members) | piggybacks the existing builder |
| Seam marker runtime check | per unit member per frame | one double comparison via a sorted-cursor; zero allocations | the hard constraint: no per-ghost-per-frame work beyond a UT-window check |
| Marker emission | once per (marker, cycle) | one ScreenMessage + label string swap | dedup set cleared with the existing cycle-change dedup |

---

## 15. Diagnostic Logging

Format `[Parsek][LEVEL][Subsystem] message`; batch-counter convention for loops; InvariantCulture.

### 15.1 Subsystem tags

| Tag | Owns |
|-----|------|
| `[DockGraph]` | graph build summaries, resolution decisions, degradation counters |
| `[Flight]` | the stamp decision at dock (existing tag, one extended line) |
| `[Mission]` | chapter expansion, digest build, reconcile warnings (existing tag) |
| `[SeamMarker]` | marker computation summaries and per-cycle emissions |

### 15.2 Logged events

- **Stamp decision** (Verbose, `[Flight]`, per dock): extend the existing route-partner resolve line (`ParsekFlight.cs:10789-10794`) with `partnerPidStamped=<pid>` so the gated and ungated values are both visible in one line. What it catches: a dock whose stamp silently diverged from the event pid.
- **Graph build** (Verbose, `[DockGraph]`, once per rebuild): `nodes=N dock=A board=B undock=C twoParent=t crossTree=x sameTreeRecovered=s zero=z noMatch=n guidRejected=g visibilityFlagged=v ms=<t>`. Guid rejections additionally get one Warn-free Verbose line each naming the BP and pids (the affordance-vanishes-without-trace rationale, mirroring `FindLinks`).
- **Same-tree recovery** (Verbose, `[DockGraph]`, per recovered link on rebuild, bounded by branch-point count): `sameTreeRecovered bp=<id> tree=<id> partnerPid=<pid> claimed=<recId>`.
- **Chapter expansion** (Info, `[Mission]`, per toggle): `chapter '<title>' <include|exclude> keys=<count> mission='<name>'` - the player-action audit line.
- **Reconcile chapter warning** (Warn, `[Mission]`, Q6): `chapter '<title>' has new included topology (keys=<n>) after exclusion`.
- **Seam marker computation** (Verbose, `[SeamMarker]`, per unit build): `unit tree=<id> markers=<n> r2=<a> r3=<b>`.
- **Seam marker emission** (Verbose rate-limited, `[SeamMarker]`, per marker per cycle): `emit kind=<R2|R3> memberIdx=<i> cycle=<c> seamUT=<ut>` - proves the runtime path fired without spamming per frame.
- **GhostChainWalker null-guid claim** (Verbose, existing `[Chain]`-family tag, Q3): one line when a claim resolves no launch guid, so a spurious pid-only suppression is diagnosable from the log alone.

Every new decision branch above has a log line; there are no silent fallbacks (the UnstampedZero path is the deliberate exception - it is the today-behavior path and is counted in the build summary instead of logged per node).

---

## 16. Test Plan

Every test names the regression it catches. Pure cores are `internal static`; log assertions use `ParsekLog.TestSinkForTesting`; classes touching `RecordingStore` take `[Collection("Sequential")]`.

### 16.1 Unit tests (`Source/Parsek.Tests`)

- **DockStampDecouplingTests** - `BuildMergeBranchData` with (gated=0, partner=pid): BP stamped, `TransferTargetVesselPid`/`TransferKind` stay zero/None; with (gated=pid, partner=pid): identical to today's output. Catches: the stamp leaking into route surfaces, or the decoupling silently dropping the stamp.
- **Updated pins** - `MergeEventDetectionTests.cs:54/:102/:209`, `ClawCoupleRecordingTests.cs:106` per section 6.1 (only the bp-target lines change; the route-proof assertions stay).
- **SameTreeDockClaimTests** - A->D fixture (ScenarioWriter/RecordingBuilder-style synthetic tree mirroring extract s2): claim derives; parent-exclusion guard (target pid == incoming parent's pid -> no claim); merged-child guard (partner survived as merged pid -> no self-claim); debris exclusion; guid rejection; two-parent BPs skipped. Catches: the recovered link claiming the wrong line, the exact failure class the guards exist for.
- **DockEventGraphTests** - build over the full AB/CD two-tree fixture: node count and every `DockPartnerStatus` reached at least once; BP_d1 = CrossTree, BP_d2 = SameTreeRecovered; `NodesByTreeId` registers BP_d1 under both trees; `DockConnectedTreePairs` contains (T1, T2). Degradation rows: target=0 node present with UnstampedZero and empty description. Visibility flag: superseded merged child -> node flagged, digest skips. Catches: resolution-order bugs and the degradation contract.
- **TryDescribePartnerTests** - direction correctness on all resolvable statuses (viewer = claimed vs viewer = controller side; two-parent viewer picks the other parent). Catches: naming the viewer's own vessel as its partner.
- **EventDigestTests** - AB/CD fixture: row order by UT, both-column verbs, gap row with `GapSeconds` when claimed end < dock UT, GoTo ids populated, empty-tree digest. Catches: unordered or partner-less story rows.
- **ChapterTests** - roots: A' (SwitchContinuation) and D (ForeignDockDeparture); D NOT a root when the ancestor dock is UnstampedZero; `ExpandChapterToIntervalKeys` returns the exact expected key set including `@dockM` keys; expansion stable across `RecordingOptimizer.SplitAtSection` (edge case 23). Catches: chapter exclusion silently missing downstream intervals.
- **SeamMarkerTests** - unit fixture from the in-game test's member windows: R2 emitted for the docked stretch when the partner has no member legs; R3 emitted when `@dock` excluded (edge case 18); neither double-fires at the epsilon boundary; marker list sorted; cursor logic pure-tested. Catches: markers firing every frame or on the wrong member.
- **Log-assertion tests** - graph build summary line, stamp-extended line, chapter expansion Info, seam emission line (sink capture per the canonical `RewindLoggingTests` pattern). Catches: silent removal of the diagnostics this design depends on.
- **GrepAuditTests stays green** - the new files read no `.CommittedRecordings` (pure/parameter-injected); no allowlist change. A deliberate test asserts `DockEventGraph.cs` contains no `RecordingStore.` token (source-text gate, mirroring the UI wiring gates).

### 16.2 In-game tests

One new cell, minimal by design: **`DockEventGraphInGameTest`** (`Category = "Missions"`, `Scene = GameScenes.SPACECENTER`) - commit the AB/CD synthetic trees through the real `RecordingStore.CommitTree` (reusing `CrossTreeDockLoopUnitInGameTest`'s fixture builders and its sidecar-reaper teardown), build the graph through `DockEventGraphCache` against the LIVE committed store, assert BP_d1/BP_d2 statuses and one digest row per column, then assert the cache does NOT rebuild on a second call with an unchanged store (signature stability). Catches: the graph working headless but mis-reading the live store or rebuilding per call.

**Tally trap (mandatory, same commit):** `Missions` is tally-pinned by `harness/scenarios/M1-mission-loop-unit.toml:145` (`BATCH_COMPLETE v1 total=12 passed=5 failed=0 skipped=7`). Adding this SPACECENTER cell moves the pin to `total=13 passed=6 skipped=7`; update the TOML (and its comment mirror at `:120`) in the same commit or `CommittedBatchTallySourceSyncTests` reds locally. Seam markers get NO in-game cell in v1 (their pure core is fully unit-tested; the visual is collected opportunistically like the M-MIS-8 per-scene confirmations) - this deliberately avoids touching the `GhostPlayback` pin (`S1.4-injected-playback.toml:143`, total=42).

### 16.3 Harness scenario

Not warranted for v1. `BDOCK-1-station-interceptor` already flies the cross-tree dock recording pipeline and will exercise the PR1 stamp on its next run (the stamp line lands in the collected KSP.log; the save-parse surface reads only `id/type/parentId/childId/rewindPointId`, so no expectation churn). The genuinely uncovered shape is the same-tree cross-session dock (the A->D flight); note a candidate `BDOCK-2` (fly, commit, switch-fly the offshoot, dock it) in `docs/dev/todo-and-known-bugs.md` as future coverage, but the derivation itself is fully pinned by the synthetic in-game cell, so a full flight is not a merge gate for this scope.

### 16.4 Spike deliverables (#8)

Not tests: a research note with the 9.3 evidence and the 9.4 verdict. If GO, the addendum defines its own test plan.

---

## 17. Implementation Sequence (PR by PR)

Ordering is rollback-safe: every PR is additive, independently revertable, and nothing later depends on a PR that changes player-visible behavior silently.

| PR | Scope | Depends on | Risk / rollback |
|----|-------|------------|-----------------|
| PR1 | #1 stamp decoupling: `pendingDockPartnerPid`, `BuildMergeBranchData` parameter, 4 test-pin updates, stamp log line, Q3's walker Verbose line. Docs: CHANGELOG + `dock-undock-recording-structure.md` s3/s9 note that the stamp is now unconditional | - | Low. Revert = new recordings stamp gated again; no reader breaks either way |
| PR2 | #2 `FindSameTreeDockClaims` + unit tests. Pure, unconsumed | - (parallel with PR1) | Trivial; dead code until PR3 |
| PR3 | #3 `DockEventGraph` + `DockEventGraphCache` + `TryDescribePartner` + naming in both tabs + the in-game cell + the M1 tally bump. Supersedes issue-1's interim T1.4 helper internals if that landed first | PR1, PR2 (runtime value; compiles without) | Low: derivation + labels. Revert removes labels only |
| PR4 | #4 digest provider + Missions-tab foldout (rendering coordinated with issue-1 per section 8) | PR3 | Low |
| PR5 | #5 chapters: roots, expansion, UI rows, reconcile warn | PR3 (parallel with PR4) | Medium (selection writes); expansion writes explicit keys through the existing mechanism, so revert leaves valid keys |
| PR6 | #6 seam markers: builder computation, `LoopUnit.SeamMarkers`, engine event + policy rendering, R5 strings | PR3 for names (can ship with generic placeholders before it) | Medium (flight-scene lifecycle); markers are additive events; revert restores silent seams |
| PR7 | #7 playtest note (no code), then R6 advisory iff confirmed (Q9) | PR3 (adjacency) | Trivial |
| PR8 | #8 spike: research note + go/no-go verdict (+ addendum if GO). No product code | PR3 conceptually; independently schedulable | None |

Cross-cutting per-commit duties (project rules): CHANGELOG entry per behavior-changing PR; `todo-and-known-bugs.md` updates (close I2-ii/I2-iii items, add the BDOCK-2 candidate, the phantom-supersede follow-up, and the Q-decisions as taken); `design-mission-crosstree-dock.md` gets a pointer note in PR2 (its s1 claim "persists the couple-event partner pid exactly" becomes true only after PR1 - today it over-promises, a pre-existing doc-accuracy gap this design closes).

---

## 18. Addendum (2026-08-13): #8 spike resolved GO - ClassifyUndockChildProvenance

The section 9 spike ran on 2026-08-13 and resolved **GO** on all three 9.4 clauses;
evidence in `docs/dev/research/dprovenance-spike-2026-08-13.md` (perfect 100%/0%
separation on the BDOCK-1 fixture; window sets exactly partner-scoped on disk; pid
preservation proven end-to-end through commit and harvest). This addendum specifies the
follow-up implementation; it is NOT part of PR sequence steps 1-7 and lands as its own
scope after they soak.

### 19.1 The classifier

```
ClassifyUndockChildProvenance(tree, undockBp, childRecording) -> UndockChildProvenance
  UndockChildProvenance (enum): Unclassified = 0, RecorderMatter = 1, PartnerMatter = 2, Mixed = 3
```

Pure, in the dock-graph family. Preconditions (all must hold, else Unclassified):
the undock's parent recording carries a completed `RouteConnectionWindow` whose
`[DockUT, UndockUT]` brackets the child's StartUT (epsilon `LoopTiming.BoundaryEpsilon`);
the child has a non-null persisted `VesselSnapshot`; the window's TRANSPORT and
ENDPOINT pid sets are non-empty and disjoint. Classification: let p = |childPids and
ENDPOINT| / |childPids|. p >= 0.9 -> PartnerMatter; p <= 0.1 -> RecorderMatter
(symmetric to the transport set); otherwise Mixed. Same-merged-stack scoping is
structural (the window lives on the child's own parent recording), so craft-baked pid
collisions cannot enter the comparison. Unclassified and Mixed degrade to today's
behavior at every consumer.

### 19.2 Consumers (exactly two)

1. **Journey-walk fork decision**: `FindBranchSuccessor` prefers, above the current
   pid-match rule, a child classified PartnerMatter when the link's claimed side is the
   partner (and dually skips a PartnerMatter child when walking the recorder's own
   line). The current pid preference stays as the fallback for Unclassified/Mixed. This
   fixes the D case: mission 2's journey follows R(D), and the pinned
   `[AB, B1]`-style walks are unchanged wherever the classifier agrees with the pid rule.
2. **Digest continuation line**: the partner-side digest gains the design 1.2
   right-column closing row ("D departed - story continues in mission 'AB' ->") for
   PartnerMatter children, with GoTo ids.

### 19.3 Consequence for #9

The recorded part-set lineage (#9) remains out of scope and is now likely unnecessary
for the docked-partner case: the derived classifier covers it from data already on
disk. #9 would only add value for non-dock splits (decouplers between two future
missions), which no current feature needs.

---

## 19. References

- `docs/dev/research/crosstree-dock-loop-coherence-analysis-2026-08-12.md` - the authoritative analysis; s6 recommendation numbering used throughout.
- `docs/dev/research/dock-loop-model-extract-2026-08-12.md` - verified extraction; the AB/CD walk and the pinned-test inventory.
- `docs/dev/research/mission-presentation-ux-analysis-2026-08-12.md` - issue-1; T1.4/T2.3 seam partner.
- `docs/dev/dock-undock-recording-structure.md` - binding recorder contract (s9 invariants).
- `docs/dev/design-mission-crosstree-dock.md`, `docs/dev/design-mission-abstractions.md` - binding design docs extended here.
- Source ground truth verified this session: `ParsekFlight.cs:5113-5151`, `:5898-5906`, `:10770-10794`, `:12108-12166`; `MissionCrossTreeDock.cs:49-122`, `:518-620`; `RecordingTree.cs:433-442`, `:588-735`; `GhostPlaybackLogic.SpanClock.cs:1749-1833`; `MissionLoopUnitBuilder.cs:185-234`, `:350-364`; `GhostChainWalker.cs:194-300`, `:487-574`; `scripts/grep-audit-ers-els.ps1:65-92`; `harness/scenarios/M1-mission-loop-unit.toml:145`, `BDOCK-1-station-interceptor.toml:50-51`.
