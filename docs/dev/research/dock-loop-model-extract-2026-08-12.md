# Dock / Undock Recording Topology and Mission-Loop Playback — Extraction Report

**Purpose.** Documentation extraction (no critique, no fixes) of (a) how Parsek records docks and
undocks, including across mission trees, (b) how "what belongs to this mission" is derived, and
(c) what a player sees when one mission loops while its vessels docked with / undocked from
another mission's vessels. Written so a downstream analyst can reason about correctness and UX
without reading the code. Every non-obvious claim carries a `file:line` citation.

Read on 2026-08-12 against the working tree at `/home/user/Parsek` (branch: repo HEAD).

---

## 0. Vocabulary and the three independent layers

Three layers, coupled only by ids (`docs/dev/dock-undock-recording-structure.md:9-17`):

1. **KSP vessels** — each has a `persistentId` (PID). PIDs are **craft-baked, not launch-unique**
   (`.claude/CLAUDE.md`); the launch-unique discriminator is `Recording.RecordedVesselGuid`
   (KSP's `Vessel.id`).
2. **`Recording`** — one flat time series (trajectory points + structural events + snapshots) for
   one vessel identity over one interval. Carries `VesselPersistentId`, `RecordedVesselGuid`,
   `ExplicitStartUT` / `ExplicitEndUT`, `TerminalStateValue`, `ParentBranchPointId`,
   `ChildBranchPointId`, `TreeId`, `ChainId`/`ChainIndex`/`ChainBranch`, `IsDebris`,
   `ParentAnchorRecordingId`, `RouteConnectionWindows`.
3. **`BranchPoint`** — the typed topology edge (`Source/Parsek/BranchPoint.cs:30-58`):
   `Id`, `UT`, `Type`, `ParentRecordingIds[]`, `ChildRecordingIds[]`, plus per-type metadata
   (`SplitCause` / `DecouplerPartId` for splits, `MergeCause` / `TargetVesselPersistentId` for
   merges, `BreakupCause` …, `RewindPointId`).

`BranchPointType` numeric values (`Source/Parsek/BranchPoint.cs:6-27`):
`Undock=0, EVA=1, Dock=2, Board=3, JointBreak=4, Launch=5, Breakup=6, Terminal=7,
VesselSwitchContinuation=8`.

> **Doc inconsistency worth noting to the analyst:** `docs/dev/dock-undock-recording-structure.md:373`
> and `:378` describe the persisted branch points as `type=2 (Dock)` and `type=3 (Undock)`. `type=2`
> is Dock (correct), but `type=3` is **Board**; Undock is `type=0`. Also `:15` lists the enum
> members and omits `JointBreak` and `Launch`.

**`RecordingTree`** (`Source/Parsek/RecordingTree.cs:8-37`) holds `Recordings` (id → recording),
`BranchPoints`, `RootRecordingId`, `ActiveRecordingId`, and two **runtime-only, rebuilt-on-load**
structures: `BackgroundMap` (vessel PID → recording id, for vessels this tree is background-
recording right now) and `RecordedVesselPids`.

`BackgroundMap` eligibility is the load-bearing predicate for dock topology
(`Source/Parsek/RecordingTree.cs:433-442`): a recording is background-map eligible only when
`VesselPersistentId != 0` **and** `TerminalStateValue == null` **and** it has no next chain segment
**and** `ChildBranchPointId == null` **and** it is not the active recording and does not share the
active recording's PID. Rebuilt wholesale by `RebuildBackgroundMap()`
(`Source/Parsek/RecordingTree.cs:376-431`).

---

## 1. Recording topology for docks and undocks

### 1.1 The KSP event contract (what Parsek must work around)

- **Dock:** `GameEvents.onPartCouple(from, to)`. `data.to.vessel` is the survivor; its PID becomes
  the merged PID. `data.from.vessel` is absorbed and destroyed shortly after. There is a short
  pre-reparent window in which both sides are still distinct objects
  (`docs/dev/dock-undock-recording-structure.md:25-33`).
- **Undock:** inside one `Part.Undock()` call, KSP fires `onPartUndock(part)` **first and exactly
  once** (part still on the combined vessel, transient PID), destroys the joint, creates the new
  `Vessel`, then fires `onVesselsUndocking(oldVessel, newVessel)` **last and unconditionally** with
  final PIDs (`docs/dev/dock-undock-recording-structure.md:43-52`). `onVesselsUndocking` is the
  authoritative split signal; the older transient-PID discovery coroutine
  (`DeferredHandleTransientUndock`) was removed and §6.4 / §7.3 of that doc are explicitly marked
  historical.

### 1.2 Undock (a SPLIT branch: 1 parent → 2 children)

Live path: `OnPartUndock` (snapshot + `pendingUndockRootPartSeed` only, `ParsekFlight.cs:10994`
comment) → `OnVesselsUndocking` (`Source/Parsek/ParsekFlight.cs:11040`) stops the recorder for a
chain boundary and queues `DeferredUndockBranch` (`:6501`) → after one frame,
`CreateSplitBranch(BranchPointType.Undock, activeVessel, backgroundVessel, branchUT)` (`:6569`).

Which half is which is decided by **focus after the deferral**, not by which PID is new
(`ParsekFlight.cs:6519-6540`, `SegmentBoundaryLogic.ResolveUndockBackgroundPid`
`Source/Parsek/SegmentBoundaryLogic.cs:217-228`): the recorder follows `FlightGlobals.ActiveVessel`
(the "active child"); the other side is backgrounded. Ambiguous focus backgrounds the new vessel.

Bail-outs that produce **no branch at all** (the recorder simply resumes on the merged child):
the background half is not found (`:6551`), or fails `IsTrackableVessel` — i.e. it is debris
(`:6559`), or branch dedup rejects a duplicate at the same UT (`:6543`,
`CheckBranchDeduplication` `:6480-6499`).

`CreateSplitBranch` (`Source/Parsek/ParsekFlight.cs:5354-5538`) then:

1. Flushes the stopped recorder's capture into the **parent** recording (`activeTree.ActiveRecordingId`)
   and advances its end to `branchUT` (`:5381`).
2. Snapshots both halves (`:5412-5413`) and, for `Undock`, closes the parent's latest open
   `RouteConnectionWindow` from the two side-scoped snapshots (`:5415-5428`).
3. Builds the branch data via the pure `BuildSplitBranchData` (`:4725-4788`):
   - `BranchPoint { Type=Undock, UT=branchUT, ParentRecordingIds=[parent], ChildRecordingIds=[activeChildId, backgroundChildId] }`
     — **child[0] is the continuing / focused vessel by convention** (`:4743`).
   - `activeChild`: fresh GUID id, `VesselPersistentId = activeVessel.persistentId`,
     `ParentBranchPointId = bp.Id`, `ExplicitStartUT = branchUT`, `Generation = parentGeneration`.
   - `backgroundChild`: same but `Generation = parentGeneration + 1`.
   - **Neither child gets `ParentAnchorRecordingId`, and neither is `IsDebris`.** Only the EVA path
     sets `ParentRecordingId` on the kerbal child (`:4772-4785`). This is the resolved gap 3 in
     `docs/dev/design-mission-abstractions.md:608-619`.
4. Sets `parent.ChildBranchPointId = bp.Id` (`:5459`), adds BP + both children to the tree
   (`:5462-5464`), sets `activeTree.ActiveRecordingId = activeChild.RecordingId` (`:5467`), puts the
   background half into `BackgroundMap` and notifies `BackgroundRecorder` (`:5485-5489`), and starts
   a fresh `FlightRecorder` on the active child with `isPromotion: true` (`:5508-5510`).
5. For Undock / EVA, may author a RewindPoint for the split (`:5534-5537`).

The parent's `TerminalStateValue` is **not** stamped at an undock (`docs/dev/dock-undock-recording-structure.md:137`);
it is simply closed and continues through the branch point.

### 1.3 Dock, SAME tree (a MERGE branch: 2 parents → 1 child)

`OnPartCouple` (`Source/Parsek/ParsekFlight.cs:10619`) does, in order:

1. Classifies the coupling producer (dock vs claw/grapple vs unknown; EVA-grab detection)
   (`:10643-10650`).
2. Appends a structural-event `TrajectoryPoint` at the exact dock UT on both the focused and the
   background recorders (`:10666-10674`).
3. Captures the **pre-couple partner snapshot** and the **pre-couple self snapshot** while
   `data.from.vessel != data.to.vessel` (`:10682-10731`) — required so the route window's endpoint
   baselines are not inflated by the merged vessel.
4. Resolves the route/partner PID from the event and gates eligibility: partner PID ≠ 0, ≠ self, and
   (pre-couple snapshot captured **or** the partner has a known recording in the active tree or in
   committed recordings) (`:10770-10787`). EVA grabs zero the target
   (`SuppressRouteWindowForEvaGrab`, `:5161-5173`).
5. Stops the recorder synchronously (`:10799`) and arms `pendingTreeDockMerge` +
   `pendingDockMergedPid` / `pendingDockAbsorbedPid` / `pendingDockRouteTargetPid` /
   `pendingDockTransferKind` / `pendingDockAsTarget` (`:10802-10807`).

There is a second, rarer **retroactive** path (`:10818-10929`) for when `OnPhysicsFrame` already
stopped the recorder before the couple event arrived; it mirrors the same resolution.

Next frame, `HandleTreeDockMerge` (`:12108-12166`) resolves the parents:

- `activeParentId = activeTree.ActiveRecordingId`.
- `bgParentId` = `activeTree.BackgroundMap[absorbedPid]`, else `BackgroundMap[mergedPid]`
  (`:12124-12127`). **This is the only way a dock becomes a two-parent merge.**

then calls `CreateMergeBranch` (`:6082-6336`), which:

1. Flushes the stopped recorder into the active parent and stamps
   `TerminalStateValue = Docked` (or `Boarded`) on it (`:6099-6108`).
2. If a background co-parent exists: sets its `ExplicitEndUT = mergeUT`, stamps it `Docked`,
   removes it from `BackgroundMap`, and notifies the background recorder (`:6112-6128`). **So the
   co-parent's line ENDS at the dock.**
3. Builds the merge data via the pure `BuildMergeBranchData` (`:5113-5151`):
   - `BranchPoint { Type=Dock, UT=mergeUT, ParentRecordingIds=[all parents], ChildRecordingIds=[childId], MergeCause="DOCK", TargetVesselPersistentId=routeTargetPid }`.
   - `mergedChild`: fresh GUID, `VesselPersistentId = mergedVesselPid` (the survivor =
     `data.to.vessel.persistentId`), `ParentBranchPointId = bp.Id`, `ExplicitStartUT = mergeUT`,
     `TransferTargetVesselPid` + `TransferKind` when route-eligible.
4. Snapshots the merged vessel and puts it on the child as both `VesselSnapshot` and
   `GhostVisualSnapshot` (`:6141-6145`) — **this is why the docked stretch plays back as a single
   combined-stack ghost.**
5. Opens the `RouteConnectionWindow` on the merged child when `Type==Dock && routeTargetPid != 0`
   (`:6231-6252`).
6. Sets `ChildBranchPointId` on every parent (`:6263-6271`), adds BP + child to the tree
   (`:6274-6275`), sets `ActiveRecordingId = mergedChild` (`:6315`), restarts the recorder with
   `isPromotion: true` (`:6318-6320`).
7. Marks any committed terminal leaf whose vessel was just absorbed as
   `TerminalSpawnSupersededByRecordingId = mergedChild.RecordingId` (`:6292-6312` →
   `RecordingStore.MarkTerminalSpawnSupersededByDockMerge`,
   `Source/Parsek/RecordingStore.SupersedeTerminalSpawn.cs:120-185`) so the absorbed vessel is not
   later re-materialised at the runway ("phantom rover"). Guid-gated for craft-baked PIDs; skipped
   when the target survived as the merged vessel.

Recording continues uninterrupted from the player's view; in the data model the trajectory is cut
at the dock UT (`docs/dev/dock-undock-recording-structure.md:109`).

### 1.4 Dock, DIFFERENT trees (the "foreign" / cross-tree shape)

Nothing new is recorded. The shape is simply the **single-parent** case of §1.3:

- `bgParentId == null` because the partner is not in `activeTree.BackgroundMap`, so
  `ParentRecordingIds` has exactly one entry — the controller's own pre-dock recording
  (`docs/dev/dock-undock-recording-structure.md:94`, `ParsekFlight.cs:6093-6096`).
- The partner is identified **only** by `BranchPoint.TargetVesselPersistentId` (= the couple
  event's partner PID, `ParsekFlight.cs:5133`).
- **The partner's own recording is not touched at all**: no `ExplicitEndUT` change, no terminal
  stamp, no `ChildBranchPointId`. Its tree is unaware of the dock. The only cross-write is the
  terminal-spawn supersession in §1.3 step 7.
- The merged child, the docked stretch, and everything after it live in the **controller's** tree.

**Critical dependency the analyst must know:** `TargetVesselPersistentId` is fed by
`pendingDockRouteTargetPid`, i.e. the **route-eligibility-gated** partner PID
(`ParsekFlight.cs:10785-10787`, `:12135`, `:6139`). If eligibility fails (partner PID 0, partner ==
self, neither a pre-couple snapshot nor a known recording) or the couple is an EVA grab, the branch
point records `TargetVesselPersistentId = 0` — and then **every downstream cross-tree derivation is
blind to the dock** (§3, §4). A claw grab records `MergeCause="DOCK"` with a `Grapple` connection
kind (`docs/dev/todo-and-known-bugs.md`, M-MIS-10 coverage note ~line 10693).

### 1.5 The same shape also appears for a SAME-tree dock across sessions

`BackgroundMap` eligibility requires `TerminalStateValue == null`
(`Source/Parsek/RecordingTree.cs:437`). Commit / scene exit stamps a terminal state on **every**
leaf (`Source/Parsek/ParsekFlight.Finalization.cs:449-457` for background leaves,
`:188-199` and `:214-269` for the active recording). Therefore, after a commit, a previously
background-tracked sibling is **not** background-map eligible on reload.

Consequence: if two vessels of the *same* committed tree dock in a *later* session, the co-parent
lookup misses and the dock records as the **single-parent** shape with
`TargetVesselPersistentId = partner PID`, exactly like a foreign dock — but because the claim lives
in *its own* tree, the cross-tree derivation in §3 explicitly skips it
(`Source/Parsek/MissionCrossTreeDock.cs:61-64`). This is stated here as an extracted fact; §2 step 6
shows it biting the AB/CD scenario.

### 1.6 Which tree a later flight lands in (needed for §2)

One `activeTree` at a time. On a stock Fly / Switch-To click, an armed
`StockActionIntentMarker` is consumed by `TryConsumeStockActionIntent`
(`Source/Parsek/ParsekFlight.cs:8579-8706`), which picks one of three branches in order
(`:8684-8705`):

1. **committed-spawned clone** (`TryRouteCommittedSpawnedClone`, `:8750`): the focused vessel
   matches a committed tree by `VesselPersistentId` (guid-gated) or `SpawnedVesselPersistentId`
   (`TryFindCommittedTreeMatchingVessel`, `:8943-8950`). A **clone of that committed tree becomes
   `activeTree`** and a `VesselSwitchContinuation` branch + recording is appended under the
   focused vessel's terminal leaf (`:8830-8865`). → the vessel's line continues across sessions.
2. **BG-member continuation** — the focused PID is in the live `activeTree.BackgroundMap` (`:8696-8701`).
3. **standalone** (`StartStandaloneContinuationSegment`, `:9306-9390`): attaches a **disconnected
   root recording to whatever `activeTree` is currently live**, and only creates a brand-new tree
   when `activeTree == null` (`:9321-9333`).

So: **trees accrete.** The controller's tree absorbs each subsequent merge and each subsequent
switch-continuation; a partner tree keeps only what it recorded while it was the active tree.

---

## 2. The AB / CD scenario, step by step

Scenario: AB and CD are launched as two missions. AB undocks into A and B. B docks CD → BCD. Later
D undocks from BCD → BC and D. Then A docks D → AD. Net physical result: {AB, CD} → {BC, AD}.

I walk the **natural play** (each launch is its own flight session; scene exits/commits between
phases), and flag where the outcome depends on session boundaries.

Notation: `T1` = AB's tree, `T2` = CD's tree. `R(x)` = a recording.

### Step 1 — AB launches

- New tree `T1`; root recording `R(AB)` with `VesselPersistentId = pid(AB)`,
  `RecordedVesselGuid = guid(AB-launch)`, `TreeId = T1`.
- One default Mission per tree is auto-created (`MissionStore.EnsureDefaultsForTrees`; contract in
  `docs/dev/design-mission-abstractions.md:223-227`), everything included.

### Step 2 — CD launches (separate session)

- New tree `T2`; root `R(CD)`, `pid(CD)`, `guid(CD-launch)`. Its own default Mission.
- On scene exit, `T2` commits and every leaf gets a terminal state
  (`ParsekFlight.Finalization.cs:449-457`) — typically `Orbiting`.

### Step 3 — AB undocks into A and B (inside T1's session)

Per §1.2:

```
R(AB)  ExplicitEndUT = undockUT, ChildBranchPointId = BP_u1     (no terminal stamp)
   |
 BP_u1  Type=Undock(0)  SplitCause="UNDOCK"  DecouplerPartId=<port>
   |         ParentRecordingIds=[R(AB)]
   |         ChildRecordingIds=[R(focused half), R(other half)]     <- child[0] = focused
   +--> R(B)   pid = focused half's pid, Generation = g            ACTIVE, recorder follows
   \--> R(A)   pid = other half's pid,   Generation = g+1          BackgroundMap[pid(A)] = R(A)
```

(Which of A / B is child[0] depends on focus after the one-frame deferral, `:6519-6527`.) Assume the
player keeps flying B.

Cross-links written: `ParentBranchPointId` on both children; `ChildBranchPointId` on `R(AB)`;
`BackgroundMap[pid(A)] = R(A)`. **No** `ParentAnchorRecordingId`, **no** `IsDebris`, **no**
`anchorRecordingId` (that field is a `TrackSection` playback concern for parent-anchored / Relative
sections, not a dock/undock topology field — `.claude/CLAUDE.md`).

### Step 4 — B docks CD → BCD

Recorder is on `R(B)` in `T1`. CD is a loaded vessel from committed `T2`; nothing adopts a nearby
foreign vessel into the active tree (no adoption path exists — `grep -i adopt` over
`Source/Parsek` yields only finalization-cache / crew / guid backfill adoption), so
`BackgroundMap` has no entry for `pid(CD)` and the co-parent lookup misses (`:12124-12127`).

```
T1:  R(B)  ExplicitEndUT = dockUT, TerminalStateValue = Docked, ChildBranchPointId = BP_d1
       |
     BP_d1  Type=Dock(2)  MergeCause="DOCK"
       |       ParentRecordingIds=[R(B)]                 <- SINGLE parent
       |       TargetVesselPersistentId = pid(CD)        <- the only link to T2
       |       ChildRecordingIds=[R(BCD)]
       +--> R(BCD)  VesselPersistentId = mergedPid
                    ExplicitStartUT = dockUT
                    VesselSnapshot / GhostVisualSnapshot = merged combined vessel
                    TransferTargetVesselPid = pid(CD), TransferKind = DockingPort
                    RouteConnectionWindows = [ open window: DockUT set, UndockUT = NaN ]

T2:  UNCHANGED. R(CD) keeps its committed terminal (Orbiting) and its recorded end.
     Side effect only: R(CD) (or the absorbed leaf) may get
     TerminalSpawnSupersededByRecordingId = R(BCD)   (ParsekFlight.cs:6292-6312)
```

**Which PID `mergedPid` is matters downstream.** `mergedPid = data.to.vessel.persistentId`
(the survivor). If CD was the dock target (`data.to`), `mergedPid == pid(CD)` and the merged child
*carries the partner's PID*; if B was the target, `mergedPid == pid(B)` and CD's PID disappears
from the live game entirely. Both variants occur in practice; the 2026-05-18 playtest is the
"target keeps its own PID" variant (`docs/dev/dock-undock-recording-structure.md:277-298`), and the
code comments in `MissionCrossTreeDock.cs:540-543` and `:606-611` name both explicitly.

### Step 5 — D undocks from BCD → BC and D

Per §1.2, inside `T1` (BCD is `T1`'s active recording):

```
T1:  R(BCD)  ExplicitEndUT = undockUT, ChildBranchPointId = BP_u2
             RouteConnectionWindows[0] COMPLETED (UndockUT + post-undock manifests)
                                                     (ParsekFlight.cs:5415-5428)
       |
     BP_u2  Type=Undock(0)  SplitCause="UNDOCK"
       |       ParentRecordingIds=[R(BCD)]
       |       ChildRecordingIds=[R(BC), R(D)]     (child[0] = focused half)
       +--> R(BC)  pid = oldVessel pid (the merged pid survives on the old vessel)
       \--> R(D)   pid = a FRESH pid KSP minted at the split
                   BackgroundMap[pid(D)] = R(D)
```

Note the PID arithmetic: `oldVessel` keeps the merged PID; `newVessel` gets a brand-new PID
(`docs/dev/dock-undock-recording-structure.md:48`, `:321`). So **`pid(D)` is a PID that never
existed before this undock**, and there is no recorded link from `R(D)` back to `R(CD)` — not by
PID, not by guid, not by branch point. The only evidence that D came from CD is the human-level
knowledge that CD's parts were in the stack.

### Step 6 — A docks D → AD

A and D are now both leaves of committed `T1` (A from step 3, D from step 5), both carrying terminal
states from the commit.

Player flies A (TS Fly / KSC Fly / Map Switch-To). Per §1.6 branch 1, `activeTree` becomes a
**clone of committed `T1`**, with a `VesselSwitchContinuation` BP + `R(A')` under `R(A)`. Then the
dock fires:

- `BackgroundMap[pid(D)]` — **miss**, because `R(D)` carries a terminal state and so fails
  `IsBackgroundMapEligible` (`RecordingTree.cs:437`).

```
T1(clone):  R(A)  --BP_switch(VesselSwitchContinuation)--> R(A')
            R(A') ExplicitEndUT = dockUT2, TerminalStateValue = Docked, ChildBranchPointId = BP_d2
              |
            BP_d2  Type=Dock(2)  MergeCause="DOCK"
              |      ParentRecordingIds=[R(A')]              <- SINGLE parent again
              |      TargetVesselPersistentId = pid(D)
              |      ChildRecordingIds=[R(AD)]
              +--> R(AD)  merged snapshot, own RouteConnectionWindow, etc.

            R(D)  UNCHANGED: still ends at its own recorded end with its committed terminal.
                  No ChildBranchPointId, no ParentBranchPointId toward BP_d2.
```

**Two explicit representability statements for this step:**

1. **The dock is recorded (topology + target PID) but D's line is not joined to it.** `R(D)` has no
   edge to `BP_d2`; only `BP_d2.TargetVesselPersistentId` names it. So any traversal from D's
   through-line stops at D's recorded end.
2. **No cross-tree affordance can recover it either**, because `BP_d2` lives in the *same* tree as
   `R(D)`, and `MissionCrossTreeDock.FindLinks` scans only trees whose `Id` differs from the
   caller's (`Source/Parsek/MissionCrossTreeDock.cs:61-64`). A same-tree, cross-session dock is
   therefore **structurally invisible from the absorbed partner's side** — it is neither a
   two-parent merge nor a derivable foreign link. This is a genuine gap in the current model, stated
   with evidence, not a bug report.

### Step 7 — Final on-disk picture

```
T2  (CD's mission)   : R(CD) only — its solo pre-dock flight.
T1  (AB's mission)   : R(AB) -> BP_u1 -> { R(B), R(A) }
                       R(B)  -> BP_d1(Dock, target=pid(CD)) -> R(BCD)
                       R(BCD)-> BP_u2(Undock) -> { R(BC), R(D) }
                       R(A)  -> BP_switch -> R(A') -> BP_d2(Dock, target=pid(D)) -> R(AD)
```

The controller's tree accreted everything; the partner tree kept only the pre-dock leg. Note that
"which tree is the controller's" is decided purely by which vessel the recorder was on at each
couple event — it is not a property of the craft.

### Step 8 — Same-session variant (worth flagging)

If the player never leaves flight between phases, the outcomes change:

- Switching from CD to B while `T2` is the live, **uncommitted** `activeTree`: branch 1 requires the
  focused vessel to match a *committed* tree; branch 2 requires `T2.BackgroundMap` to hold `pid(B)`.
  If neither holds, `StartStandaloneContinuationSegment` attaches B's continuation as a
  **disconnected root inside `T2`** (`ParsekFlight.cs:9321-9361`) — B's flight then lives in CD's
  tree, and the later BCD dock is recorded there.
- Conversely, if D is still a live background member of the same session's active tree when A docks
  it, `BackgroundMap[pid(D)]` hits and the dock becomes a genuine **two-parent** merge with D's line
  properly closed at the dock (§1.3).

So the same six physical events can produce materially different topology depending on session
boundaries and switch routing. This should be treated as a first-class variable by the analyst.

---

## 3. Mission composition: what belongs to a mission

### 3.1 The abstraction stack

`docs/dev/design-mission-abstractions.md` defines six layers: recording (atom, post-optimizer) →
mission tree (a DAG scoped by `TreeId`) → main line (a *path*, a "spine") → mission subtree
(a selection) → **Mission** (a persisted, named selection + loop settings) → supply run/route
(deferred). Key rules (`:104-135`, `:631-650`):

- FORKS = controlled separations (Undock / EVA / JointBreak with `IsDebris=false` children).
- MERGES = Dock / Board; **same-tree** merges have two `ParentRecordingIds`; a **foreign** dock is
  single-parent and the relationship is reconstructed at read/playback time by PID linking.
- TWIGS = debris (`IsDebris=true`): never spine-eligible, never a UI row, always riding along.
- A path traverses a merge by following **its own** incoming line into the child; the co-parent is
  not pulled in.

### 3.2 Legs (`MissionStructureBuilder`)

`Source/Parsek/MissionStructure.cs:122-217`: one `MissionLeg` per **non-debris** recording
(`:139-143`); intra-run sequence edges group env-split legs by `(ChainId, ChainBranch)` ordered by
`ChainIndex` (`:258-291`); cross-run edges come from `BranchPoints` (`:331-392`).

Two fields matter for docks:

- `MissionLeg.OriginBranchPointType` / `OriginCause` — the branch point that created this leg
  (`:378-379`). A merged child therefore carries `OriginBranchPointType = Dock`.
- `MissionLeg.IsBranchContinuation` — set on `bp.ChildRecordingIds[0]` (`:366-371`), i.e. the
  recorder's "continuing vessel" convention, which resolves the previously-nondeterministic
  undock continuation pick.

### 3.3 Through-lines (`MissionThroughLineBuilder`)

`Source/Parsek/MissionThroughLine.cs:38-122`. A through-line merges every leg of one continuous
controlled vessel into one entry; things that left it hang off as child through-lines.
`ContinuationSuccessor` (`:124-149`) = `SequenceNextId` if present, else, among the branch children
that are non-anchored and non-EVA, the one flagged `IsBranchContinuation`, else the first controlled
child by the structure's deterministic sort.

**Observation for the two-parent dock case:** both dock parents list the merged child as a branch
child, and both would pick it as their continuation successor, so the merged child appears in the
`MemberLegIds` of **both** parents' through-lines (`Build` gives each head its own `walked` set,
`:71-78`; there is no cross-head visited set). The duplicate does not reach the loop unit: the
composition builder's shared `visited` set gives the merged child's interval to exactly one owner
(`MissionComposition.cs:98`, `:137-138`, `:145-150`), and `ComputeTrimmedMemberWindows` both
intersects with the owner's window and drops duplicate committed indices
(`MissionLoopUnitBuilder.cs:1472-1481`). Recorded here because it is a real read-model asymmetry an
analyst may otherwise trip on.

### 3.4 Composition intervals and the dock sub-interval (`MissionCompositionBuilder`)

`Source/Parsek/MissionComposition.cs:68-385`. A `MissionCompositionNode` is one physical vessel over
one **structural interval**, labeled `"pod x1, probe x1, crew x3"`, with `HeadLegId` (the selection
key), `OwnerHeadId` (the through-line it slices), `StartUT`/`EndUT`, `StartEvent`/`EndEvent`,
`IsSelectable`.

Interval edges (M-MIS-5):

- **Structural peels** (a controller separating: decouple / undock / EVA-as-structural) end an
  interval and start the survivor's. Structural `/segN` ordinals are computed over structural edges
  **only** (`:227-239`), so a dock never renumbers an existing key.
- **Merge edges**: a run member at index ≥ 1 whose `OriginBranchPointType` is `Dock` or `Board`
  contributes an interval edge at its `StartUT`, **subdividing** the structural interval it falls in
  (`:180-206`, `:245-291`). Sub-interval keys after the first are `"<parentIntervalKey>@dockM"`
  (M = 1-based ordinal inside that structural interval, `:280-288`). A merge UT coincident with a
  structural edge or a run endpoint mints **no** `@dock` key (structural identity wins) but still
  applies the label rebase (`:242-244`, `:251`).
- **Label rebase (D2)**: the docked interval's composition is REBASED to the merge leg's own
  start-captured composition (the combined vessel) rather than being the head's start composition
  minus peels (`:401-496`, especially `:418-462`). If the merge leg carries no composition of its
  own, an **additive fallback** adds the other parents' start compositions instead, logged once per
  build (`:214-222`, `:443-460`).
- `StartEvent` / `EndEvent` on a dock edge read `"Docked"` / `"Boarded"`
  (`:559-564`, `:706-729`).
- A NaN merge `StartUT` fails closed: no edge, no rebase (`:191-200`).

### 3.5 Selection → render window (`MissionIntervalSelection`)

`Source/Parsek/MissionIntervalSelection.cs:35-80`. Persisted state is the set of **excluded**
interval keys (`Mission.ExcludedIntervalKeys`, `Source/Parsek/Mission.cs:29`); the included set is
derived live. An interval is included unless its `HeadLegId` is excluded (no cascade — intervals
toggle independently). Per vessel (`OwnerHeadId`) the render window is `[min start, max end]` over
its **included** intervals; a vessel with all intervals excluded is absent entirely.

### 3.6 Cross-tree link derivation (`MissionCrossTreeDock`, M-MIS-8)

Design: `docs/dev/design-mission-crosstree-dock.md`. Decision: the `Mission` stays **single-tree**
and gains exactly one sparse field, `IncludedForeignDockLinkIds` (a set of foreign
`BranchPoint.Id`s), default empty, written sorted, key omitted when empty
(`:32-47`, `Source/Parsek/Mission.cs:40`). Everything else is derived live.

- **`FindLinks(myTree, allTrees)`** (`Source/Parsek/MissionCrossTreeDock.cs:49-122`): scan every
  **other** tree's `BranchPoints` for `Type ∈ {Dock, Board}` with `TargetVesselPersistentId != 0`
  whose target PID matches a recording in **my** tree, guid-gated via
  `VesselLaunchIdentity.GuidsConclusivelyDiffer` (unknown guid → PID-only fallback, walker parity).
  Debris recordings never match (`:518-537`, rationale at `:512-517`: craft-baked PID collisions on
  guid-less debris would mint false affordances). The earliest-starting match wins. Output is
  sorted by `(DockUT, LinkId)`; one Verbose summary is emitted whenever a link derives **or** a
  guid-gate rejection happened (`:116-121`).
  The returned `ForeignDockLink` (`:18-29`) carries `LinkId` (the claiming BP GUID), `ForeignTreeId`,
  `DockUT`, `ClaimType`, `PartnerPid`, `PartnerLaunchGuid`, `ClaimedRecordingId`,
  `MergedChildRecordingId` (= `bp.ChildRecordingIds[0]`), `ForeignVesselName` (the merged stack's
  name).
- **`ComputePartnerJourneyLegIds(foreignTree, link)`** (`:134-166`): walk from the merged child
  forward — chain successor first (`FindChainSuccessor`, `:559-574`), else branch successor
  (`FindBranchSuccessor`, `:581-620`). At a fork it **prefers the child whose PID matches the
  partner** (guid-gated) — that is "the partner departing", or "the partner survived as the merged
  stack"; otherwise it follows the recorder's continuing-child convention
  (`ChildRecordingIds[0]`, if controlled) and presumes the partner is still aboard. Debris legs are
  walked through but not added (`:148-149`). Cycle-guarded. `departureFound` is logged; the design
  and the code comment both state that an exotic split moving the partner onto a
  non-matching, non-continuation child **mis-follows by design** in v1 — part-level membership
  tracking is out of scope (`docs/dev/design-mission-crosstree-dock.md:80-86`,
  `MissionCrossTreeDock.cs:127-132`).
- **Journey windows** (`ComputeJourneyWindowsByOwner`, `:178-226`): per foreign through-line, one
  window **per contiguous run** of journey legs — deliberately not `[min,max]`, so a partner that
  undocks and later re-docks the same line does not get the foreign vessel's own solo stretch in
  between offered as partner journey.
- **`IsJourneyNode`** (`:238-259`) / **`CollectJourneySelectableKeys`** (`:266-275`): a foreign
  composition node is a partner-journey interval iff it is selectable, owned by a line carrying
  journey legs, and contained in one of that line's runs (epsilon 1e-3, `:33-35`). Those keys are
  exactly the foreign keys a linked mission may hold in `ExcludedIntervalKeys` — the same set
  `MissionStore.ReconcileSelections` unions in so cross-seam exclusions are not dropped as stale
  (`Source/Parsek/MissionStore.cs:300-311`).
- **Membership merge** (`MergeForeignMemberWindows`, `:332-444`): resolve each included link, build
  the foreign tree's structure / view / composition once per tree, compute journey legs → journey
  windows → included render windows, then map every journey member leg to a committed index and a
  trimmed window, **first claimant wins** on a duplicate index. Returns the count added and reports
  `resolvedLinks` / `staleLinks`.

**UI surface** (`Source/Parsek/UI/MissionsWindowUI.cs:1070-1152`): one row per derived link,
`"Partner journey - <foreign vessel>"` plus the dock date and `Docked`/`Boarded`, with a toggle
bound to `IncludedForeignDockLinkIds` — **default OFF**, an explicit player action. Toggling it on
for an already-looping mission immediately calls `MissionStore.ClearLoopsConflictingWith`
(`:1100-1105`), because including a link widens the mission's spanned tree set. When included, the
journey's maximal foreign composition nodes render as indented child rows with the normal
per-interval checkboxes (`:1136-1150`, `DrawMaximalJourneyNodes` `:1156-1169`).

**One-loop-per-tree generalises to spanned tree sets**, enforced at three sites: `SetLoopEnabled`
(`Source/Parsek/MissionStore.cs:418-443`), `NormalizeOneLoopPerTree` after load, and the link
toggle above; the shared helper is `ClearLoopsConflictingWith`
(`Source/Parsek/MissionStore.cs:454-462`).

**Docked-guest handling, summarised.** There is no "guest" concept. From the controller's side the
guest is simply parts of the merged child (and, for logistics, a part-PID set inside the
`RouteConnectionWindow`). From the partner's side the guest relationship is a derived, opt-in,
single-PID-tracked link.

---

## 4. Loop playback semantics

### 4.1 What a loop unit is

`GhostPlaybackLogic.LoopUnit` (`Source/Parsek/GhostPlaybackLogic.SpanClock.cs:22-45`): an owner
index, a member index array, `SpanStartUT` / `SpanEndUT`, `CadenceSeconds` (span clock),
`OverlapCadenceSeconds` (true launch cadence), `PhaseAnchorUT`, a per-member trimmed
`MemberWindow` map, plus the optional periodicity / re-aim payloads. **All indices are positional
indices into `RecordingStore.CommittedRecordings`** — the alignment invariant shared by flight, KSC
and the Tracking Station (`docs/dev/design-mission-abstractions.md:381-388`).

Built by `MissionLoopUnitBuilder.TryBuildMissionUnit`
(`Source/Parsek/MissionLoopUnitBuilder.cs:152-499`):

1. Resolve the tree by `Mission.TreeId` (`:168-175`).
2. Build structure → through-line view → composition (`:179-181`).
3. `ComputeTrimmedMemberWindows` (`:191-193`, definition `:1435-1484`): per included vessel window,
   every member leg's committed index with window ∩ `[rec.StartUT, rec.EndUT]`; a member entirely
   outside is dropped; first-claimant wins on duplicate ids.
4. **Cross-tree merge**: if `IncludedForeignDockLinkIds` is non-empty, merge the partner-journey
   members in (`:200-206`).
5. Members sorted by trimmed start; span = `[min trimmed start, max trimmed end]` over **all**
   members from **both** trees (`:217-234`).
6. Two cadences (`:250-262`): `CadenceSeconds` = the user period **raised to at least the span**
   (Auto = span) so a single span instance never truncates — this is what the single-instance
   scenes (KSC, TS) consume; `OverlapCadenceSeconds` = the true launch period (Auto = the global
   auto-loop interval), cap-clamped by `MaxOverlapMissionInstances`. When the overlap cadence is
   shorter than the span, the flight engine overlaps the mission with itself.
7. Owner = earliest-start member (`:265`). Phase anchor = `Mission.LoopAnchorUT` (the loop-enable
   UT), floored at `spanEndUT` so a loop never relaunches before the first real play completes
   (`:267-285`).
8. **Cross-tree fail-closed** (`:350-364`): when `bodyInfo != null` **and** any foreign member
   joined, the entire periodicity / phase-lock / zero-drift-schedule / phasing-knob / re-aim /
   arrival-hold block is skipped, with an `Info` line
   (`"... cross-tree partner-journey member(s); periodicity/re-aim fail closed to faithful ..."`).
   The unit keeps the faithful base anchor + raw cadences. Rationale: constraint extraction only
   sees the mission's own tree — two trees are two launches, two pads, two constraint sets
   (`docs/dev/design-mission-crosstree-dock.md:120-130`).
9. `BuildSignature` folds in the sorted link ids **and every other tree's** branch/recording counts
   whenever any link is included, so a foreign-tree topology change rebuilds the cached unit
   (`:1563-1589`).

For single-tree missions with a `bodyInfo`, the unit may instead be phase-locked to faithful launch
windows `UT0 + k*P` and/or driven by a non-uniform zero-drift relaunch schedule
(`:365-467`; semantics in `docs/dev/design-mission-periodicity.md:89-135`, `:1239-1266`).

### 4.2 The span clock, and what renders when

`TryComputeSpanLoopUT` (`Source/Parsek/GhostPlaybackLogic.SpanClock.cs:1089-1126`, body in
`ComputeSpanLoopFrame` `:1142+`) maps live UT → `(loopUT inside the span, cycleIndex,
isInInterCycleTail)`. `DecideUnitMemberRender` (`:1785-1833`) then gives, **per member,
independently**:

- `SpanClockUnresolved` — before the span start or a degenerate span.
- `HiddenInterCycleTail` — cadence > span, the clock is parked at `spanEndUT` waiting: **hide all
  members**.
- `Render` — `IsLoopUTInMemberWindow(loopUT, memberStart, memberEnd)` (`:1749-1754`, ±
  `LoopTiming.BoundaryEpsilon`).
- `HiddenOutsideWindow` — otherwise.

There is **no cross-member selection**: any number of members render concurrently when the shared
clock is inside their windows (`:1769-1775`).

Engine dispatch: `GhostPlaybackEngine` intercepts every unit member **above** the per-recording loop
gate, because mission members carry no per-recording `LoopPlayback` flag
(`Source/Parsek/GhostPlaybackEngine.cs:1207-1223`) → `UpdateUnitMemberPlayback` (`:2182-2790+`):

- Per-member anchor gating still applies (`:2193-2205`).
- **Self-overlap branch** when `UnitMemberOverlaps(unit)` (`:2229-2338`): each member is reduced to
  a per-recording overlap loop over the existing `UpdateOverlapPlayback` machinery, with
  `scheduleStartUT = PhaseAnchorUT + (memberStart − spanStart)`, `playbackStartUT = memberStart`,
  `duration = memberEnd − memberStart`, `interval = OverlapCadenceSeconds`. Camera handoff follows
  the **watched** instance while it is still in flight, else the newest
  (`:2274-2313`, `TryComputeMissionInstanceSpanLoopUT` / `ComputeNewestMissionInstanceSpanLoopUT`,
  SpanClock `:1695-1740`).
- **Single-instance branch**: decision from `DecideUnitMemberRender` (`:2343-2350`); hide/destroy on
  `HiddenOutsideWindow` (`:2634-2643`) — except the **watched** member, which is hidden but never
  destroyed (`:2618-2633`), and the boundary-overlap secondary case (`:2599-2611`); a
  pre-payload-activation member is hidden too (`:2645-2694`); warp suppression hides the mesh
  (`:2696-2710`); a **cycle change destroys and rebuilds the ghost** and clears the completed-event
  dedup so events replay (`:2729-2737`); then the member is rendered at `spanLoopUT` by overriding
  the frame UT (`:2759-2766`).
- **Debris rides along** its parent leg via `ShouldSourceDebrisFromUnitSpan`
  (SpanClock `:2943-2950`), reached whenever the parent is a unit member — not only when the parent
  carries `LoopPlayback` (`GhostPlaybackEngine.cs:1806`, `:1822`). Debris is never a
  `MemberIndices` entry; the parent linkage is `RecordingStore.PopulateLoopSyncParentIndices`, i.e.
  the first non-debris same-tree recording whose `[StartUT, EndUT]` covers the debris start —
  independent of `ParentAnchorRecordingId` **and** of the Mission selection
  (`docs/dev/design-mission-abstractions.md:424-433`).
- Unit members are excluded from the global auto-loop launch queue by **unit membership**, not by
  the loop flag (`docs/dev/design-mission-abstractions.md:454-459`). Mission looping wins over
  per-recording looping.

Scene parity: KSC and the Tracking Station have no overlap machinery and always render a **single**
span instance at `CadenceSeconds` (`docs/dev/design-mission-abstractions.md:484-493`); self-overlap
is a flight-scene-only visual.

### 4.3 What a loop of ONE mission shows around a dock / undock

Answers to the specific questions, from the mechanisms above.

**(a) Does the mission end at the dock?** No. For the **controller's** mission, the pre-dock leg is
closed and stamped `TerminalState.Docked`, but the tree continues through the Dock branch point into
the merged child, which is a member of the same through-line (its `OriginBranchPointType = Dock`
subdivides the interval rather than terminating it). `Docked` / `Boarded` are **non-spawnable**
terminals (`Source/Parsek/GhostPlaybackLogic.cs:7138-7149`), so the pre-dock leg's playback simply
ends — nothing is spawned and no phantom vessel appears at that moment.

For the **co-parent** of a same-tree dock, its own line *does* end at the dock (`ExplicitEndUT =
mergeUT`, terminal `Docked`, `ChildBranchPointId` set, `:6112-6128`). The merged child is claimed by
one through-line (§3.3), so the other parent's window ends at the dock and the intersection in
`ComputeTrimmedMemberWindows` drops the merged child from that owner's members.

For the **foreign partner's** mission, its recorded line ends wherever its own recording ended,
which is typically **before** the dock UT — it was not being recorded during the dock.

**(b) Does the docked combined vessel render, and as whose ghost?** Yes — as the **merged child
recording's own ghost**, with the merged vessel's name and the **post-couple merged snapshot** as
its geometry (`ParsekFlight.cs:6131-6145`). It is not "A's ghost plus B's ghost"; it is one
combined-stack ghost. Both pre-dock parents' ghosts stop at the dock UT.

Because `IsLoopUTInMemberWindow` is epsilon-inclusive at both ends, the pre-dock member and the
merged member can both satisfy their windows on the single frame where `loopUT ≈ dockUT`
(SpanClock `:1749-1754`) — i.e. a one-frame overlap at the seam is expected by construction.

**(c) When does a ghost disappear at a dock?** The moment the shared clock leaves that member's
window: `HiddenOutsideWindow` → `DestroyGhost(... "chain-loop unit member outside its window")`
(`GhostPlaybackEngine.cs:2634-2640`), unless the member is the one the camera is watching, in which
case it is hidden-not-destroyed until the handoff lands (`:2618-2633`). The camera handoff itself is
`LogUnitTransitionIfChanged` → `CameraActionType.UnitHandoffRetarget`
(`GhostPlaybackEngine.cs:2356-2358`, `:3137-3175`), consumed by
`WatchModeController.HandleLoopCameraAction`.

**(d) After an undock, does the departed piece keep rendering inside this mission's loop?** For the
controller's mission: yes, if it is included. The undock's background child is a non-debris leg with
its own through-line (it is an **offshoot**, since it is not `ChildRecordingIds[0]`), so it has its
own selectable interval and, when included, its own member window starting at the undock UT. Both
post-undock halves render concurrently from the undock UT onward on the same shared clock. If the
player unchecks that offshoot's interval, the vessel disappears from the loop entirely (its render
window is absent, `MissionIntervalSelection.cs:17-21`).

**(e) Looping the PARTNER's mission with the link OFF (the default).** Only the partner's own
recorded legs render. The combined stack does not render in this loop at all (it is not a member),
the loop's span ends at the partner's own recorded end, and the ghost retires there. The docked
stretch is of course still visible in ordinary non-looping playback, because that is the
controller's committed recording rendering on the live clock
(`docs/dev/design-mission-crosstree-dock.md:167-168`).

**(f) Looping the PARTNER's mission with the link ON.** One shared span clock over members from
**both** trees, so the recorded dock alignment is preserved by construction
(`docs/dev/design-mission-crosstree-dock.md:110-118`). The sequence the player sees, per the
in-game test's pinned windows (§6):

```
loopUT:  spanStart .............................................. spanEnd
         [ partner's own solo legs ]      (gap)      [ combined stack ] [ partner offshoot ]
         T0 ............... preDockEnd            dockUT ....... undockUT ......... offshootEnd
```

- The gap `[partnerRecordedEnd, dockUT]` renders **nothing** for this mission: the partner was
  loitering, unrecorded. "Its ghost retires at its recorded end and the combined ghost appears at
  the dock UT. Chronological gaps are an accepted contract; no interpolation is invented"
  (`docs/dev/design-mission-crosstree-dock.md:163-166`; the contract itself is
  `docs/dev/design-mission-abstractions.md:162-165`).
- The **controller's own legs never join** this unit — so the approach that produced the dock is
  not rendered, and the combined stack appears out of nowhere at the dock UT.
- Periodicity / phase-lock / re-aim are **off** for this unit (fail closed, §4.1 step 8), so it is
  a faithful replay anchored at the loop-enable UT.
- One loop per **spanned tree set**: enabling this loop clears any loop on the linked foreign tree
  (§3.6).

**(g) In the AB/CD scenario specifically.** Looping CD's mission with its partner-journey link
included yields members: CD's own pre-dock legs + `R(BCD)` + whatever the journey walk follows past
`BP_u2`. Because the walk prefers a child whose PID equals `PartnerPid` and otherwise takes
`ChildRecordingIds[0]`, it follows **`R(BC)` (the continuing stack)** and **never `R(D)`**:
- if CD survived as the merged vessel, `pid(BC) == mergedPid == pid(CD) == PartnerPid` → explicit
  PID match on `R(BC)`;
- if CD was absorbed, no child matches and the walk takes `ChildRecordingIds[0] = R(BC)` anyway.
Either way `R(D)` — which physically contains CD's D half — is not in the partner journey, and the
`A→D` dock in step 6 is not reachable from CD's mission at all. This is the documented v1 limitation
("part-level membership tracking is out of scope") meeting a scenario where the partner's *parts*
split across both undock children.

---

## 5. Already-documented gaps and known bugs (dock / undock / loop / cross-tree)

From `docs/dev/design-mission-abstractions.md:594-627` ("Docking & undocking (v1)" known gaps):

1. **Dock is not an interval boundary** — **RESOLVED** by M-MIS-5 (the `@dock` sub-interval,
   `MissionComposition.cs:20-30`). The listed gap text is now historical.
2. **Docked composition is understated** — **RESOLVED** by the same change (the D2 label rebase),
   with two acknowledged residuals inside the *additive fallback* path only
   (`MissionComposition.cs:434-444`): a partner that shed a controller mid-leg overcounts; a named
   head base with a nameless partner undercounts by the partner's crew.
3. **Undock continuation vs offshoot nondeterminism** — **RESOLVED** by honoring
   `ChildRecordingIds[0]` via `IsBranchContinuation` (`:608-619`).
4. **Cross-tree foreign dock** — **RESOLVED** as M-MIS-8 / PR #1261 (`:620-627`,
   `docs/dev/todo-and-known-bugs.md:10569`).

Still-open items that bear on this area:

- **Multi-path reconvergence rendering** is explicitly still open: "Still open: how the multi-path
  (whole-mission) outline renders a reconvergence"
  (`docs/dev/design-mission-abstractions.md:714-719`, open question 6).
- **Env-split row collapsing** (open question 5, `:708-710`) — UI clarity only.
- **New topology defaults to INCLUDED** under the excluded-id persistence model (open question 3a,
  `:688-694`): a genuinely new branch added after a Mission was defined is included by default.
- **Cross-tree periodicity / phase-lock / re-aim** is deliberately unimplemented (fail closed);
  **logistics route derivation across a cross-tree pair** is out of scope (`RouteBackingMission`
  stays single-tree) (`docs/dev/design-mission-crosstree-dock.md:172-179`).
- **Part-level partner tracking through exotic splits** — out of scope
  (`docs/dev/design-mission-crosstree-dock.md:179`, code comment `MissionCrossTreeDock.cs:127-132`).
- **`ROUTE-CANDIDACY-GATED-ON-SEAL-NO-SEAM-PATH`** (`docs/dev/todo-and-known-bugs.md:640`): a fully
  green two-vessel docking flight (`BDOCK-1`) produces the `ROUTE_CONNECTION_WINDOWS` node on the
  docked-state recording but cannot produce a route-candidate tree, and no automation seam verb can
  seal one. Classified a capability gap in the automation surface, not a product defect.
- **`MergeCause` never records `"CLAW"`** — a claw grab records `MergeCause="DOCK"`; `BranchPoint.cs`
  lists `"CLAW"` as an intended value but `GetMergeCauseForBranchType` only emits `DOCK`/`BOARD`.
  Filed as cosmetic (`docs/dev/todo-and-known-bugs.md`, M-MIS-10 coverage-run-2 note, ~line 10694).
- **M-MIS-10 operator-observation cells** for docking-adjacent archetypes (claw couples;
  round-trip resupply render half) are `PENDING-OPERATOR` — the automated coverage exists, the
  in-game observation does not (`docs/dev/todo-and-known-bugs.md` ~lines 10674-10695).
- The dock/undock structure doc's own §9 invariants list (`docs/dev/dock-undock-recording-structure.md:383-393`)
  is the binding contract list for anyone touching this code (pre-couple capture before reparent;
  undock driven by `onVesselsUndocking`; idempotent route-window completion; partner-scoped part-PID
  sets; structural snapshots are `TrajectoryPoint`s; one `CreateSplitBranch` sink).

Two gaps this extraction surfaced that I did **not** find already written down (stated as findings,
not as recommendations):

- **A same-tree dock in a later session records as the single-parent "foreign" shape** and is
  derivable from neither side: `BackgroundMap` eligibility excludes terminal-stamped leaves
  (`RecordingTree.cs:437`), and `MissionCrossTreeDock.FindLinks` skips same-tree claims
  (`MissionCrossTreeDock.cs:61-64`). §2 step 6.
- **`BranchPoint.TargetVesselPersistentId` is gated on route eligibility**
  (`ParsekFlight.cs:10782-10787` → `:12135` → `:6139` → `:5133`), so the Missions cross-tree
  affordance silently does not exist for docks whose route target was zeroed (EVA grab; partner with
  neither a pre-couple snapshot nor a known recording). §1.4.

---

## 6. What the two named in-game tests actually pin

### `CrossTreeDockLoopUnitInGameTest` (`Source/Parsek/InGameTests/CrossTreeDockLoopUnitInGameTest.cs`)

Category `Missions`, scene `SPACECENTER` (`:85-87`). It is the M-MIS-8 merge gate, replacing the
manual two-vessel docking playtest. Fixtures: partner tree `tb` = B's solo flight
`[T0, T0+100]` (`:430-437`); controller tree `ta` = A `[T0, dock]` → Dock BP (target = B's PID) →
`AB` `[dock, undock]` → Undock BP with children `[A1 (continuing), B1 (departing, PID = B's)]`
(`:439-488`). Both trees registered through the real `RecordingStore.CommitTree`; UTs
`T0=5,000,000`, `preDockEnd=T0+100`, `dock=T0+150`, `undock=T0+300`, `offshootEnd=T0+380`,
`foreignContEnd=T0+400`, `loopEnable=T0+1000` (`:67-78`).

Pinned behaviors:

1. **Link discovery** over the real committed trees (`:170-183`): the link derives with
   `ForeignTreeId=ta`, `DockUT`, `PartnerPid = B`, `ClaimedRecordingId = B0` (B's own pre-dock
   recording), `MergedChildRecordingId = AB`, `ClaimType = Dock`.
2. **Direction**: the controller-side tree is **never** offered its own dock as a partner journey
   (`:187-191`).
3. **Journey walk** = exactly 2 legs, `[AB, B1]` — the docked stretch then B's departing offshoot,
   **never A's continuation** (`:195-198`).
4. **Include mutation + spanned-set one-loop enforcement at BOTH sites**: two disjoint-tree
   missions loop concurrently before the link exists (`:203-206`); adding the link on an
   already-looping mission clears the foreign tree's loop via `ClearLoopsConflictingWith`
   (`clearedSameTree=0`, `clearedCrossTree=1`, `:211-219`); `NormalizeOneLoopPerTree(trees)` clears
   a hand-set conflict, first-in-list surviving (`:223-231`).
5. **The real `MissionLoopUnitBuilder.Build` with the LIVE `FlightGlobalsBodyInfo.Instance`**
   (`:236-241`): exactly one unit; owner = B's pre-dock index; **3 members in trimmed-start order:
   B pre-dock, docked stretch, B offshoot**; A's own legs never join (`:254-262`).
6. **Exact member windows** (`:268-273`): `B0 = [T0, preDockEnd]`; docked stretch =
   `[dockUT, undockUT]`; offshoot = `[undockUT, offshootEnd]`. This is the concrete proof of the
   §4.3(f) gap: `[preDockEnd, dockUT]` is covered by no member.
7. **One shared span clock across both trees** (`:277-280`): span = `[T0, offshootEnd]`,
   `CadenceSeconds` = the span (the Sec sentinel is raised).
8. **Fail-closed periodicity, field by field** (`:285-294`): `PhaseAnchorUT == loopEnableUT` (no
   phase-lock snap), `RelaunchSchedule == null`, no `ReaimPlan` / `ReaimSchedule`, `IsReaim` false,
   `LoiterCuts == null`, `ArrivalHoldSeconds == 0`, `LaunchHoldEngaged` false,
   `TransferMemberIndex == -1`; plus the logged reason line containing
   `"cross-tree partner-journey member(s)"` and `"fail closed"` (`:298-310`).
9. **Cross-seam checkbox trim** (`:315-348`): the docked stretch keys as an `@dock` sub-interval of
   **A's** line; B's offshoot keys as `idB1`; **A's own pre-dock line is never offered**; excluding
   the docked key rebuilds the unit with 2 members (`B0`, `B1`).
10. **Sparse codec round-trip** through the production `MissionStore.Save`/`Load` (`:353-370`): the
    linked mission writes exactly one `foreignDockLink` key = the claiming BP GUID; a link-free
    mission writes **none** (pre-feature byte identity); the link and the loop flag survive reload.

Explicitly **not** gated (`:43-48`): the per-scene visual confirmation (partner pre-dock ghost,
combined ghost at the dock, offshoot after undock, in FLIGHT / KSC / TS) — to be collected
opportunistically in ordinary play.

### `MissionDockCompositionRuntimeTest` (`Source/Parsek/InGameTests/MissionDockCompositionRuntimeTest.cs`)

Category `Missions`, scene `FLIGHT` (`:16-17`). Self-contained synthetic tree
(launch `[1000,2000]` pod×1 → Dock BP @2000 → docked `[2000,3000]` pod×1+probe×1 → Undock BP @3000 →
survivor + depot). Pins that the **production** `MissionStructureBuilder` →
`MissionCompositionBuilder` chain running inside KSP:

- yields exactly one composition root, whose `EndEvent == "Docked"` (`:48-53`);
- surfaces the docked interval as key **`launch@dock1`** (`:56-61`);
- with `StartEvent == "Docked"` (`:62-63`);
- spanning exactly `[2000, 3000]` — i.e. starting at the merge UT (`:64-65`);
- labeled with the **rebased combined** composition `"pod x1, probe x1"` (`:66-68`);
- and **independently selectable** (`:69-70`).

---

## 7. Quick reference — the files that own each piece

| Concern | File(s) |
|---|---|
| Dock live path | `Source/Parsek/ParsekFlight.cs:10619` (`OnPartCouple`), `:12108` (`HandleTreeDockMerge`), `:6082` (`CreateMergeBranch`), `:5113` (`BuildMergeBranchData`) |
| Undock live path | `ParsekFlight.cs:11040` (`OnVesselsUndocking`), `:6501` (`DeferredUndockBranch`), `:5354` (`CreateSplitBranch`), `:4725` (`BuildSplitBranchData`) |
| Focus/side resolution | `Source/Parsek/SegmentBoundaryLogic.cs:217` (`ResolveUndockBackgroundPid`) |
| Tree container + BackgroundMap rules | `Source/Parsek/RecordingTree.cs:8`, `:376`, `:433` |
| Branch point type + metadata | `Source/Parsek/BranchPoint.cs` |
| Terminal stamping at commit / scene exit | `Source/Parsek/ParsekFlight.Finalization.cs:188`, `:449` |
| Route window lifecycle | `Source/Parsek/RouteProofCapture.cs`, `RouteProofMetadata.cs`; doc §6 |
| Dock-merge phantom suppression | `Source/Parsek/RecordingStore.SupersedeTerminalSpawn.cs:120` |
| Playback cross-tree PID chains | `Source/Parsek/GhostChainWalker.cs:244` (`ScanBranchPointClaims`), `:487` (`MergeCrossTreeLinks`) |
| Spawn-at-end terminal gate | `Source/Parsek/GhostPlaybackLogic.cs:7138` (`IsSpawnableTerminal`) |
| Mission legs | `Source/Parsek/MissionStructure.cs:122` |
| Through-lines | `Source/Parsek/MissionThroughLine.cs:38`, `:124` |
| Composition intervals + `@dock` | `Source/Parsek/MissionComposition.cs:68`, `:180`, `:245`, `:401` |
| Selection → render window | `Source/Parsek/MissionIntervalSelection.cs:35` |
| Cross-tree derivation | `Source/Parsek/MissionCrossTreeDock.cs` (all) |
| Loop unit builder | `Source/Parsek/MissionLoopUnitBuilder.cs:152`, `:1435`, `:1502` |
| Span clock + render decision | `Source/Parsek/GhostPlaybackLogic.SpanClock.cs:22`, `:1089`, `:1695`, `:1749`, `:1785`, `:2943` |
| Engine dispatch | `Source/Parsek/GhostPlaybackEngine.cs:1207`, `:2182`, `:1806` |
| Mission entity + store | `Source/Parsek/Mission.cs`, `Source/Parsek/MissionStore.cs:132`, `:418`, `:454` |
| Missions window (partner-journey rows) | `Source/Parsek/UI/MissionsWindowUI.cs:1070` |
| Authoritative docs | `docs/dev/dock-undock-recording-structure.md`, `docs/dev/design-mission-crosstree-dock.md`, `docs/dev/design-mission-abstractions.md`, `docs/dev/design-mission-periodicity.md` |
