# Recording System Extension Plan — Vessel Interaction Paradox

*Maps the vessel interaction paradox resolution (parsek-vessel-interaction-paradox.md) onto the existing Parsek codebase. Identifies what changes, what's new, and the detailed task breakdown for implementation.*

*Status: Implementation complete. 2989 tests pass (381 new). In-game testing pending for KSP runtime paths.*

---

## 1. What the Extension Proposes

The extension document resolves the **vessel interaction paradox**: what happens when a committed recording physically interacts with a pre-existing vessel (docking, undocking, collision, crew transfer, etc.), and the player then rewinds to before that interaction.

### 1.1 The Problem (Not Handled by Current System)

The current design (recording-system-design.md Section 11) assumes pre-existing vessels are always real and always unaffected:

> "During playback, the ghost approaches the real vessel. At the merge UT, the ghost despawns. The real vessel is unaffected."

This breaks when the player rewinds and tries to interact with the same pre-existing vessel *before* the committed recording's interaction time. The real vessel must be in the correct state for the committed recording to replay correctly — but the player might modify it first, creating a paradox.

### 1.2 The Resolution: Ghost Chains

The extension introduces the **ghost chain rule**: when a committed recording contains a physical interaction with a pre-existing vessel, that vessel becomes a ghost from rewind until the recording's chain tip resolves. Key concepts:

1. **Ghost Chain Rule** — Claimed vessels are ghosted from rewind UT until chain tip spawn. Multiple committed recordings chaining through a vessel lineage extend the ghost window.

2. **Ghosting Trigger Taxonomy** — Only physical state changes (structural, orbital, part state) trigger ghosting. Cosmetic changes (lights, animations) and observation (focus switch, camera) do not.

3. **Intermediate Spawn Suppression** — In multi-link chains (bare-S → S+A → S+A+B), only the final form spawns as real. Intermediate chain links stay as ghosts.

4. **Looped Recording Interactions** — First loop iteration is the "real run" (follows ghost chain rules, spawns real vessel at completion). All subsequent loops are visual-only.

5. **Spawn Collision Detection** — Bounding box overlap check at chain tip prevents physics explosions. Ghost extends via orbital propagation until overlap clears.

6. **Surface Terrain Correction** — Terrain raycast + physics settling for surface-stationary spawns, handling KSP's procedural terrain height differences between sessions.

7. **Self-Protecting Property** — Once a vessel is ghosted, no physics interaction is possible (ghosts have no colliders), so conflicting recordings are impossible by construction.

### 1.3 What This Changes About Parsek's Core Design

**This is a fundamental expansion of Parsek's scope.** The current core rule (Section 11.1) states:

> "Parsek NEVER modifies any parameter of a real persistent vessel."

The extension refines this: Parsek still never modifies a real vessel's state directly, but it now **hides real vessels** (converting them to ghosts) and **spawns replacement real vessels** at chain tips. This is a significant departure from the read-only design principle, introducing a new lifecycle:

```
Real vessel (from quicksave)
  → Ghosted (despawned, replaced by ghost GO from snapshot)
    → Ghost plays through chain (visual only)
      → Real vessel spawns at chain tip (from final-form snapshot, PID preserved)
```

---

## 2. Existing Systems: What Changes vs What's New

### 2.1 Systems That Need Modification

| Component | Current Behavior | Required Change | Complexity |
|-----------|-----------------|-----------------|------------|
| `ParsekFlight.cs` | Spawn-at-end via `ShouldSpawnAtRecordingEnd` + PID dedup | Chain-aware spawn: suppress intermediate links, ghost conversion on rewind, chain evaluation at scene load | High |
| `GhostPlaybackLogic.cs` | `ShouldSpawnAtRecordingEnd` checks PID dedup only. `ShouldSkipExternalVesselGhost` skips ghost if real vessel exists. | Spawn logic checks intermediate chain status. External vessel skip becomes chain-aware (ghosted vessel = don't skip). | Medium |
| `VesselSpawner.cs` | `RespawnVessel` always regenerates PID via `RegenerateVesselIdentity` | Chain-tip spawns must preserve snapshot PID (see Section 3.3). Support spawning at propagated position. Terrain raycast correction. | Medium |
| `Recording.cs` | Has `SpawnedVesselPersistentId` for simple spawn dedup | Needs `TerrainHeightAtEnd` field for surface endpoints | Low |
| `FlightRecorder.cs` | Records trajectory + events for focused vessel | Must record terrain height at surface-stationary segment endpoints | Low |
| `BackgroundRecorder.cs` | Records trajectory + events for physics-bubble vessels | Already captures events on external vessels (cross-perspective attribution works — see Section 4.5). No changes needed. | None |
| `RecordingStore.cs` | Serializes current recording format (v6) | New field: terrain height. Version bump. | Low |
| `ParsekScenario.cs` | Save/load for recordings + crew | Chain re-evaluation on any save load | Medium |
| `BranchPoint.cs` | Has `TargetVesselPersistentId` for MERGE events | Already sufficient — no changes needed | None |
| Warp spawn queue | `pendingSpawnRecordingIds` flushed on warp exit | Add position info, scope flush to physics bubble | Low |

### 2.2 New Systems Required

| Component | Purpose | Depends On |
|-----------|---------|------------|
| `GhostingTriggerClassifier.cs` | Static class — classifies which PartEvent/BranchPoint types trigger vessel ghosting | PartEvent, BranchPoint |
| `GhostChain.cs` | Data structure — represents a computed ghost chain (vessel PID, links, tip) | Recording, BranchPoint |
| `GhostChainWalker.cs` | Static class — walks all committed trees to compute ghost chains, find tips, detect intermediate links | RecordingTree, GhostingTriggerClassifier, GhostChain |
| `VesselGhoster.cs` | Manages real→ghost lifecycle: snapshot, despawn, create ghost GO, track state | KSP vessel APIs, GhostVisualBuilder |
| `SpawnCollisionDetector.cs` | Bounding box overlap detection at spawn time | VesselSpawner |
| `GhostExtender.cs` | Orbital propagation / surface persistence for ghosts past recording end | TrajectoryMath, OrbitSegment |
| `TerrainCorrector.cs` | Terrain raycast + clearance calculation for surface spawns | KSP terrain APIs |
| `SpawnWarningUI.cs` | Persistent on-screen warning for spawn collision | ParsekUI |

---

## 3. Mapping Extension Concepts to Existing Code

### 3.1 Ghost Chain Rule → Recording DAG Traversal

The ghost chain walker traverses ALL committed `RecordingTree` objects to find:
- All MERGE/SPLIT BranchPoints with `TargetVesselPersistentId` set
- All background recordings with ghosting-trigger PartEvents on external vessels
- Cross-tree chain links by matching leaf `VesselPersistentId` → subsequent MERGE `TargetVesselPersistentId`

**Existing infrastructure:** `RecordingTree` maintains a DAG of `Recording` objects connected by `BranchPoint` nodes. `BranchPoint.TargetVesselPersistentId` identifies claimed vessels. `RecordingStore.committedTrees` holds all committed trees. `RecordingStore.CommitTree` commits ALL recordings in a tree (including background vessel recordings) — verified in code.

**Gap:** No cross-tree traversal or claiming analysis exists. The chain walker is new.

### 3.2 Ghosting Trigger Taxonomy → Event Classification

The extension's trigger taxonomy (Section 5) maps to existing event types:

| Extension Category | Existing Code Equivalent | Triggers Ghosting? |
|---|---|---|
| Docking (MERGE) | `BranchPointType.Dock` | Yes |
| Undocking (SPLIT) | `BranchPointType.Undock`, `JointBreak` | Yes |
| Part destruction | `SegmentEventType.PartDestroyed` | Yes |
| Crew transfer | `BranchPointType.EVA`, `Board` | Yes |
| Engine burns | PartEvent `EngineIgnited`/`EngineThrottle` | Yes |
| Deploying/retracting parts | PartEvent `DeployableExtended`/`Retracted` | Yes |
| Toggling lights | PartEvent `LightOn`/`LightOff` | No |
| Focus switching | Focus log in session | No |
| SAS mode changes | Not recorded as discrete events | No |

**Resource transfers:** In stock KSP, resource transfers between vessels require docking (or AGU claw), both of which are already MERGE events that trigger ghosting. Resource-only transfers without structural interaction don't exist in stock KSP. No gap.

### 3.3 Spawn-at-End → Chain-Aware Spawn + PID Preservation

**Current flow** (`ParsekFlight.cs` ~line 5100):
```
Recording reaches EndUT
  → ShouldSpawnAtRecordingEnd checks PID dedup
  → VesselSpawner.RespawnVessel (always regenerates PID)
  → Sets SpawnedVesselPersistentId
```

**Required flow:**
```
Recording reaches EndUT
  → GhostChainWalker.IsIntermediateChainLink?
    → Yes: suppress spawn, continue ghost chain
    → No: proceed to spawn
  → SpawnCollisionDetector.CheckOverlap?
    → Overlap: block spawn, start ghost extension
    → Clear: proceed
  → TerrainCorrector.AdjustSurfaceSpawn? (if surface)
  → VesselSpawner.RespawnVessel(preserveIdentity: isChainTip)
```

**Critical: PID preservation for chain-tip spawns.** `VesselSpawner.RegenerateVesselIdentity` (line 910) assigns a new PID on every spawn. This breaks cross-tree chain linking:

1. R1 records A docking to S (PID=100). R1's leaf has `VesselPersistentId` = 100 (S's PID survived docking).
2. On rewind + chain resolution, VesselSpawner spawns S+A with a NEW PID (e.g. 567) because of `RegenerateVesselIdentity`.
3. Player then records R2 (B docking to S+A). R2's MERGE has `TargetVesselPersistentId` = 567.
4. On second rewind, chain walker sees: R1 claims PID=100, R2 claims PID=567. These are different PIDs — the chain is broken.

**Fix:** Chain-tip spawns must preserve the snapshot PID. Since VesselGhoster already despawned the original vessel, there is no PID collision — the PID is free. Add a `preserveIdentity` parameter to `VesselSpawner.RespawnVessel` that skips `RegenerateVesselIdentity`. Normal (non-chain) spawns continue to regenerate PIDs.

With PID preservation: R1's spawn has PID=100 (from snapshot) → R2's MERGE has `TargetVesselPersistentId` = 100 → chain walker matches R1's leaf (PID=100) to R2's MERGE target (PID=100) → chain extends correctly.

### 3.4 Ghost Conversion → Despawn + Ghost Replacement (Option A)

**Decision: Option A.** Despawn the real vessel, create a ghost GO from its snapshot. At chain tip, spawn fresh vessel from final-form snapshot.

Process:
1. Quicksave loads → real vessel S exists with PID=100
2. Chain walker identifies S as claimed
3. `VesselGhoster` captures S's snapshot via `VesselSpawner.TryBackupSnapshot`
4. `VesselGhoster` despawns S via vessel destroy/remove API
5. `VesselGhoster` creates ghost GO from captured snapshot (using existing `GhostVisualBuilder.BuildGhostFromSnapshot`)
6. Ghost follows background recording trajectory (or orbital propagation)
7. At chain tip: spawn final-form vessel, destroy ghost GO

Safety: quicksave is always available as full backup. Ghost conversion wrapped in try/catch — if it fails, fall back to current behavior (real vessel untouched, ghost approaches and despawns).

### 3.5 Cross-Perspective Attribution → Already Handled

**Verified in code.** `BackgroundRecorder.OnBackgroundPartDie` (line 203) and `PollPartEvents` (line 1711) capture all part events on background vessels. When vessel B collides with station S, `onPartDie` fires for S's parts, BackgroundRecorder catches it (S is in `tree.BackgroundMap`), and records the event on S's recording. At commit, S's recording (with the Destroyed event) is committed along with all tree recordings (`RecordingStore.CommitTree` line 288 commits ALL recordings). The chain walker scans S's committed recording, finds ghosting-trigger PartEvents, and claims S.

No new mechanism needed.

### 3.6 Warp Spawn Queue → Loaded vs Unloaded Spawning

**Correction from addendum Section 7.1:** The original paradox doc (Section 8.9) said spawns outside the physics bubble are "queued until the player enters their bubble." This is wrong. Unloaded spawns create a `ProtoVessel` entry immediately at the chain tip UT — the vessel appears in tracking station and map view right away. Only the visual conversion (ghost GO → real loaded Vessel) requires the player's physical presence.

**Loaded spawn (in physics bubble):** Full sequence — ghost replacement, bounding box check, ghost extension if blocked, terrain raycast, physics settling.

**Unloaded spawn (outside bubble):** Create ProtoVessel from tip recording's snapshot at correct orbital elements / surface coordinates. No Unity objects, no bounding box check. Vessel propagates on rails like any normal unloaded vessel.

**Impact on warp spawn queue:** `pendingSpawnRecordingIds` only queues loaded-bubble spawns. Unloaded spawns bypass the queue entirely.

### 3.7 Relative-State Time Jump → New Operation

*Source: addendum Sections 2-3.*

A **time jump** is a discrete UT skip (not a warp) that advances the game clock while keeping every vessel in the physics bubble at its exact current position. No vessel moves. No intermediate physics is simulated. Orbital epochs are adjusted so Keplerian propagation remains consistent with the new UT.

This solves the rendezvous-drift problem: after setting up a docking approach to ghost-S at 80m, normal time warp would separate the vessels through differential Keplerian drift. The time jump preserves the exact geometry.

**Sequence:**
1. Snapshot the bubble (positions, velocities, attitudes, orbital elements for all objects)
2. Set game clock to target UT
3. Epoch-shift each orbit (shift mean anomaly at epoch by jumpDelta; orbit shapes unchanged)
4. Process spawn queue (all chain tips crossed during jump)
5. Process game actions recalculation (science, funds, rep — same as warp exit)
6. Resume physics

**What changes:** planet rotation, sun angle, star field. **What doesn't:** vessel positions, velocities, relative positions, attitudes, approach geometry.

**Surface case:** Both vessels surface-fixed → body rotates with UT, carrying both. Relative positions preserved automatically. No epoch-shift needed.

**No existing code does this.** This is an entirely new operation requiring: `Planetarium` time-setting API, vessel orbit manipulation, spawn queue integration, game actions system trigger.

### 3.8 Selective Spawning UI

*Source: addendum Section 4.*

When the player is near ghosts, a spawn control panel shows:
- Chain tips with **[Sync & Spawn]** button (vessel name, spawn UT, recording chain)
- Intermediate links hidden (spawn suppressed)
- Loop iterations shown as informational only (no spawn button)

**Chronological constraint:** Selecting a chain tip also spawns all independent chain tips chronologically before it — a ghost cannot remain ghost past its tip. UI warns: "Also spawns: [name] at UT=[earlier tip]."

**Chained jumps:** Player can do multiple jumps in sequence. Each jump epoch-shifts whatever is currently in the bubble.

### 3.9 Trajectory Walkback for Spawn Deadlocks

*Source: addendum Section 6.7.*

**Problem:** Spawn blocked by immovable vessel (surface base, ground-anchored station). Standard "wait for player to move" doesn't work because the blocking vessel can't move. Permanent deadlock.

**Resolution:** After a timeout (e.g. 5s of persistent overlap), walk backward along the spawning ghost's recorded trajectory frame-by-frame, checking bounding box overlap at each position. Spawn at the latest non-overlapping position.

**Fallback:** If the entire trajectory overlaps (blocking vessel grew to cover full approach path), show a manual placement UI within a configurable radius.

**This extends Phase 6c** `SpawnCollisionDetector` with walkback logic.

### 3.10 Ghost World Presence (Map View, Tracking Station, CommNet)

*Source: addendum Sections 7.1-7.3.*

Ghosts are not just visual playback objects — they represent vessels that exist in the world pending chain resolution.

**Tracking station:** Ghosted vessels appear with a distinct icon. Info panel shows: vessel name, ghost status, chain tip UT, claiming recording.

**Map view:** Ghost orbit line in distinct style (different color or dashed). Vessel marker shows ghost status on hover. Player can set ghost as navigation target — approach vectors, closest approach, relative velocity all work.

**CommNet relay:** Antennas on ghosts relay signal, extending CommNet coverage. A relay constellation placed by a committed recording must provide coverage during the ghost window. Implementation: register CommNet nodes at ghost positions with antenna specs from recording data (part names, power ratings, combinability exponents).

**Recording data requirement:** Recordings must store antenna specs per vessel for CommNet ghost registration.

**For loaded ghosts:** Unity GO + CommNet node. **For unloaded ghosts:** CommNet node + tracking station entry + map marker only (no Unity object).

### 3.11 TIME_JUMP Recording Event

*Source: addendum Section 6.4.*

If the player is actively recording during a time jump, the trajectory has a gap from T0 to target UT. This is stored as a `TIME_JUMP` SegmentEvent with pre-jump and post-jump state vectors, so playback can handle the discontinuity (visual cut or interpolation).

**Impact:** New `SegmentEventType.TimeJump` enum value. Serialized as SEGMENT_EVENT with jump metadata.

---

## 4. Resolved Design Decisions

| # | Question | Decision | Rationale |
|---|----------|----------|-----------|
| 4.1 | Ghost conversion mechanism | **Option A — Despawn + Ghost Replacement** | Cleanest separation. Ghost is purely visual. Quicksave is full backup. Original vessel despawned = PID free for chain-tip spawn. |
| 4.2 | Claiming metadata | **Derive at runtime** | Scan committed trees for MERGE/SPLIT BranchPoints + background recordings with ghosting-trigger PartEvents. Trivial cost for typical tree sizes (tens of recordings). No new serialization, always correct by definition. |
| 4.3 | Resource transfers | **Non-issue** | Stock KSP resource transfers require docking (MERGE) or AGU (structural MERGE). No resource-only transfer exists without a structural interaction that already triggers ghosting. |
| 4.4 | Ghost visual identity | **Minimal: label only** | Full visual treatment (transparency, outlines, tint) deferred. But zero distinction is unworkable — players will EVA kerbals into ghosts and attempt to dock with them. Phase 6d adds a floating text label ("Ghost — spawns at UT=X") on all ghosts. Cheap, sufficient for awareness. Full visual treatment can be added later. |
| 4.5 | Cross-perspective attribution | **Already handled by existing BackgroundRecorder** | `onPartDie` + `PollPartEvents` capture events on all physics-bubble vessels. Committed tree includes background recordings. Chain walker reads them. No new code needed. |

---

## 5. Relationship to Known Bugs

| Bug | Relationship |
|-----|-------------|
| **#45 — Suborbital vessel spawn causes explosion** | Extension's terrain correction (8.12) and spawn collision detection (8.2) address related spawning issues. |
| **#49 — RealVesselExists O(n)** | Chain walker will call vessel existence checks. PID cache fix becomes more important. |
| **#55 — RELATIVE anchor triggers on debris** | If a ghosted vessel was a RELATIVE anchor, anchor validation must handle ghost state. |
| **#58 — Background recording requires debris persistence** | Ghost chains rely on background recording for trajectory. Fallback: orbital propagation from last known state. |

---

## 6. Implementation Phase Breakdown

### Phase 6a: Ghost Chain Infrastructure

**Goal:** Build the chain walker and claiming logic. Pure data model + traversal. No visual changes, no vessel conversion. Fully testable without Unity/KSP.

#### Task 6a-1: GhostingTriggerClassifier

**New file:** `GhostingTriggerClassifier.cs`

**Scope:**
- `internal static bool IsGhostingTrigger(PartEventType type)` — returns false for `LightOn`/`LightOff`, true for everything else
- `internal static bool IsClaimingBranchPoint(BranchPointType type)` — returns true for `Dock`, `Board`, `Undock`, `EVA`, `JointBreak`; false for `Launch`, `Terminal`, `Breakup`
- `internal static bool HasGhostingTriggerEvents(Recording rec)` — scans PartEvents for any ghosting trigger

**Tests:** `GhostingTriggerClassifierTests.cs`
- Every PartEventType classified correctly
- Every BranchPointType classified correctly
- Recording with only lights → false
- Recording with engine ignition → true
- Empty recording → false

**Done when:** All event types classified. `dotnet test` passes.

**Files:** 1 new + 1 test. Touches 0 existing.

#### Task 6a-2: GhostChain data model

**New file:** `GhostChain.cs`

**Scope:**
```csharp
internal struct ChainLink
{
    public string recordingId;   // recording that contains the interaction
    public string treeId;        // which committed tree
    public string branchPointId; // the claiming BranchPoint (null for background-event claims)
    public double ut;            // UT of the interaction
    public string interactionType; // "MERGE", "SPLIT", "BACKGROUND_EVENT"
}

internal class GhostChain
{
    public uint OriginalVesselPid;      // the pre-existing vessel's PID
    public List<ChainLink> Links;       // ordered by UT
    public double GhostStartUT;         // earliest UT vessel must be ghosted from (rewind UT)
    public double SpawnUT;              // chain tip UT (EndUT of tip recording)
    public string TipRecordingId;       // leaf recording at chain tip (has final-form snapshot)
    public string TipTreeId;            // tree containing tip recording
    public bool IsTerminated;           // true if chain ends in destruction/recovery (no spawn)
}
```

**Tests:** `GhostChainTests.cs`
- Construction, property access, Links ordering
- IsTerminated flag

**Done when:** Data structures compile. Basic tests pass.

**Files:** 1 new + 1 test. Touches 0 existing.

#### Task 6a-3: GhostChainWalker — core chain building

**New file:** `GhostChainWalker.cs`

**Scope:**
- `internal static Dictionary<uint, GhostChain> ComputeAllGhostChains(List<RecordingTree> committedTrees, double rewindUT)`
- Algorithm:
  1. Scan all committed trees for claiming BranchPoints (`IsClaimingBranchPoint` + `TargetVesselPersistentId != 0`)
  2. Scan all committed trees for background recordings with `HasGhostingTriggerEvents`
  3. Build per-vessel-PID claim lists, sorted by UT
  4. **Cross-tree linking:** for each chain, check if the tip recording's `VesselPersistentId` is claimed by a BranchPoint in another tree → extend chain
  5. Compute chain tip: the leaf recording of the last link
  6. Set `IsTerminated` if tip recording has `TerminalStateValue` = Destroyed/Recovered
- `internal static bool IsIntermediateChainLink(Dictionary<uint, GhostChain> chains, Recording rec)`
  - True if rec's EndUT matches a non-final link in any chain (suppress spawn)
- `internal static GhostChain FindChainForVessel(Dictionary<uint, GhostChain> chains, uint vesselPid)`

**Tests:** `GhostChainWalkerTests.cs`
- **Single-tree, single link:** R1 docks A to S → S claimed, chain tip = R1's leaf
- **Single-tree, destruction:** R1 crashes into S, destroys S → chain terminated, no spawn
- **Cross-tree, two links:** R1 (Tree1) docks A to S; R2 (Tree2) docks B to S+A → chain extends, tip = R2's leaf
- **Independent chains:** R1 docks A to S1, R2 docks B to S2 → two independent chains
- **Branching:** R1 has SPLIT, one branch docks to S1, other docks to S2 → two chains
- **Background-event claim:** S's background recording has Destroyed PartEvent → S claimed
- **No claims:** tree with no MERGE/SPLIT targeting external vessels → empty chain map
- **Intermediate link detection:** R1's leaf is intermediate (R2 extends chain) → suppressed
- **Looped recording:** first iteration at chain tip → spawn; subsequent → visual only
- **Recovery terminates:** R1 deorbits S, recovered → chain terminated

**Done when:** Chain walker produces correct chains for all test topologies. `dotnet test` passes.

**Files:** 1 new + 1 test. Touches 0 existing. Depends on 6a-1, 6a-2.

#### Task 6a-4: Chain-aware spawn suppression

**Scope:**
- Modify `GhostPlaybackLogic.cs`: add `internal static bool ShouldSuppressSpawnForChain(Dictionary<uint, GhostChain> chains, Recording rec)` — returns true if rec is an intermediate chain link
- Modify `ShouldSpawnAtRecordingEnd` signature or add a new wrapper that checks chain status before the existing PID-dedup logic

**Tests:** `ChainSpawnSuppressionTests.cs`
- Intermediate link: spawn suppressed
- Chain tip: spawn allowed
- Standalone recording (not in any chain): spawn allowed (existing behavior)
- Looped recording first iteration: spawn allowed
- Terminated chain (destruction): spawn suppressed (no vessel to spawn)

**Done when:** Spawn suppression wired in. All existing spawn tests still pass. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `GhostPlaybackLogic.cs`. Depends on 6a-3.

#### Task 6a-5: PID preservation for chain-tip spawns

**Scope:**
- Modify `VesselSpawner.RespawnVessel`: add `bool preserveIdentity = false` parameter. When true, skip `RegenerateVesselIdentity`.
- Modify `VesselSpawner.SpawnAtPosition`: same parameter.
- All existing callers pass default `false` (behavior unchanged).

**Tests:** extend existing `VesselSpawner` tests (or new `ChainTipSpawnTests.cs`)
- `preserveIdentity=false`: PID regenerated (existing behavior)
- `preserveIdentity=true`: spawned vessel has snapshot's original PID
- Non-chain spawn callers unchanged

**Done when:** Chain-tip spawns preserve PID. Normal spawns regenerate. All existing tests pass.

**Files:** 0 new + 1 test. Modifies `VesselSpawner.cs`. No dependencies within 6a.

---

### Phase 6b: Ghost Conversion + Chain-Aware Spawn

**Goal:** Convert real vessels to ghosts on rewind. Spawn final-form vessels at chain tips. This phase integrates the chain walker into the live playback system.

#### Task 6b-1: VesselGhoster — core lifecycle

**New file:** `VesselGhoster.cs`

**Scope:**
```csharp
internal class VesselGhoster
{
    // Runtime state
    private Dictionary<uint, GhostedVesselInfo> ghostedVessels;

    // Snapshot + despawn a real vessel, create ghost GO
    internal bool GhostVessel(uint vesselPid);

    // Check if a vessel is currently ghosted
    internal bool IsGhosted(uint vesselPid);

    // Get ghost GO for a ghosted vessel
    internal GameObject GetGhostGO(uint vesselPid);

    // Spawn final-form vessel at chain tip, destroy ghost GO
    internal uint SpawnAtChainTip(GhostChain chain);

    // Clean up all ghosted vessels (on scene exit)
    internal void CleanupAll();
}
```

- `GhostVessel`: calls `VesselSpawner.TryBackupSnapshot`, destroys real vessel via `vessel.Die()` / `FlightGlobals.RemoveVessel`, creates ghost GO via `GhostVisualBuilder.BuildGhostFromSnapshot`
- `SpawnAtChainTip`: calls `VesselSpawner.RespawnVessel(preserveIdentity: true)` with tip recording's snapshot, destroys ghost GO
- Wrapped in try/catch with logging — failure falls back to current behavior

**Tests:** `VesselGhosterTests.cs`
- Runtime state tracking (ghost/unghost)
- Cleanup removes all state
- Error handling (null vessel, missing snapshot)

**Done when:** Lifecycle management compiles. State tracking works. Error paths logged.

**Files:** 1 new + 1 test. Touches 0 existing. Depends on 6a-5.

#### Task 6b-2: Chain evaluation on scene load

**Scope:**
- Modify `ParsekFlight.cs` — in the flight scene initialization path (after quicksave load completes):
  1. Call `GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, currentUT)`
  2. For each chain: call `VesselGhoster.GhostVessel(chain.OriginalVesselPid)`
  3. Store computed chains for use by spawn-at-end logic
- Must run AFTER quicksave loads (vessel exists) but BEFORE player can interact
- Also run on any `ParsekScenario.OnLoad` (KSP native load)

**Tests:** Integration tests with synthetic committed trees
- Committed tree with MERGE → claimed vessel ghosted on scene load
- No committed trees → no ghosting
- Multiple independent chains → all claimed vessels ghosted

**Done when:** Claimed vessels become ghosts on scene load/rewind. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `ParsekFlight.cs`. Depends on 6a-3, 6b-1.

#### Task 6b-3a: Spawn suppression wiring in ParsekFlight

**Scope:**
- Modify `ParsekFlight.cs` spawn-at-end path (~line 5100):
  - Before existing `ShouldSpawnAtRecordingEnd` check, call `ShouldSuppressSpawnForChain`
  - If intermediate: skip spawn, log suppression
  - If chain tip: use `VesselGhoster.SpawnAtChainTip` instead of `VesselSpawner.SpawnOrRecoverIfTooClose`
  - Destroy chain ghost GO after spawn

**Tests:**
- Chain tip spawn fires at correct UT
- Intermediate spawn suppressed
- Standalone recording unchanged (existing behavior)

**Done when:** Chain-aware spawn suppression wired into playback loop. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `ParsekFlight.cs`. Depends on 6a-4, 6b-1, 6b-2.

#### Task 6b-3b: External vessel ghost skip becomes chain-aware

**Scope:**
- Modify `GhostPlaybackLogic.ShouldSkipExternalVesselGhost`: if vessel is ghosted (`VesselGhoster.IsGhosted(pid)`), do NOT skip — the ghost chain needs its own ghost from background recording data
- This is a separate concern from spawn suppression: it controls whether a ghost GO is created for a vessel, not whether it spawns as real

**Tests:**
- Ghosted vessel: ghost skip bypassed → ghost GO created
- Non-ghosted external vessel: existing skip behavior preserved
- Vessel becomes real after chain tip: skip resumes

**Done when:** External vessel ghost skip is chain-aware. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `GhostPlaybackLogic.cs`. Depends on 6b-1.

#### Task 6b-4: Chain ghost trajectory playback

**Scope:**
- Chain ghost must be positioned each frame using background recording trajectory data
- During recorded time span: interpolate from background recording TrackSection frames (same as existing ghost positioning)
- Outside recorded time span: orbital propagation from last known orbit, or surface-stationary
- Modify `ParsekFlight.cs` LateUpdate ghost positioning: detect chain ghost (via VesselGhoster), route to background trajectory interpolation

**Tests:**
- Chain ghost positioned from background trajectory data
- Chain ghost falls back to orbital propagation when no trajectory data
- Surface chain ghost stays at surface coordinates

**Done when:** Chain ghosts follow correct trajectory. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `ParsekFlight.cs`. Depends on 6b-2.

#### Task 6b-5: Save/load chain re-evaluation

**Scope:**
- Ghost chain state is NOT persisted — re-derived from committed recordings on every load
- On `ParsekScenario.OnLoad` and `ParsekFlight` scene enter: recompute chains, re-ghost claimed vessels
- On `ParsekScenario.OnSave`: no chain state saved (chains are derived data)
- Handle mid-chain save: player saves while a vessel is ghosted → on load, chain walker re-evaluates, re-ghosts

**Tests:**
- Save during active ghost chain → load → chain restored
- Load save from before any commits → no chains
- Load save from after chain tip → no active chain (vessel already spawned)

**Done when:** Ghost chains survive save/load. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `ParsekScenario.cs` or `ParsekFlight.cs`. Depends on 6b-2.

---

### Phase 6c: Spawn Safety

**Goal:** Prevent spawn collisions. Ghost extension past recording end. Terrain correction. Can begin after 6b-3 (spawn wiring exists).

#### Task 6c-1: SpawnCollisionDetector

**New file:** `SpawnCollisionDetector.cs`

**Scope:**
- `internal static Bounds ComputeVesselBounds(ConfigNode vesselSnapshot)` — compute approximate bounding box from PART nodes (position + size from part config)
- `internal static (bool overlap, float distance) CheckOverlap(Vector3d spawnPos, Bounds spawnBounds, float padding)` — check against all loaded real vessels
- 200m warning threshold (separate from bounding box block)

**Tests:** `SpawnCollisionDetectorTests.cs`
- Bounding box from single-part vessel
- Bounding box from multi-part vessel
- Overlap detected when vessels overlap
- No overlap when vessels are separated
- Padding margin respected

**Done when:** Overlap detection works. `dotnet test` passes.

**Files:** 1 new + 1 test. Touches 0 existing.

#### Task 6c-2: GhostExtender

**New file:** `GhostExtender.cs`

**Scope:**
- `internal static Vector3d PropagateOrbital(double inc, double ecc, double sma, double lan, double argPe, double mAe, double epoch, string bodyName, double currentUT)` — Keplerian propagation from recording's terminal orbit
- `internal static Vector3d PropagateSurface(double lat, double lon, double alt, string bodyName)` — surface-stationary position
- Uses same orbit math as existing `TrajectoryMath.PositionGhostFromOrbit`

**Tests:** `GhostExtenderTests.cs`
- Orbital propagation produces valid positions at future UTs
- Surface propagation returns same position
- Edge case: no terminal orbit → returns last recorded position

**Done when:** Ghost position computed past recording end. `dotnet test` passes.

**Files:** 1 new + 1 test. Touches 0 existing.

#### Task 6c-3: TerrainCorrector + terrain height recording

**New file:** `TerrainCorrector.cs`

**Scope:**
- `internal static double CorrectSpawnAltitude(double lat, double lon, CelestialBody body, double recordedClearance)` — terrain raycast, return `currentTerrainHeight + recordedClearance`
- Add `TerrainHeightAtEnd` field (double, NaN = not set) to `Recording.cs`
- Add serialization in `RecordingStore.cs` (key: `terrainHeightAtEnd`)
- Modify `FlightRecorder.cs`: capture terrain height when segment ends in surface-stationary state

**Tests:** `TerrainCorrectorTests.cs`
- Clearance math: `currentTerrain + (recordedAlt - recordedTerrain)` = correct altitude
- Serialization round-trip for terrain height field
- NaN default when not surface-stationary

**Done when:** Surface spawns use corrected altitude. `dotnet test` passes.

**Files:** 1 new + 1 test. Modifies `Recording.cs`, `RecordingStore.cs`, `FlightRecorder.cs`.

#### Task 6c-4: Ghost extension loop + collision wiring

**Scope:**
- Modify `ParsekFlight.cs` spawn path: before spawning, call `SpawnCollisionDetector.CheckOverlap`
- If overlap: block spawn, continue ghost via `GhostExtender.PropagateOrbital/Surface`
- Each frame while blocked: recheck overlap at ghost's propagated position
- When overlap clears: spawn at propagated position (not original endpoint)
- Warp queue: add spawn position to pending entries, only flush spawns within physics bubble (~2.3km)

**Tests:** `SpawnCollisionWiringTests.cs`
- Spawn blocked when overlap detected
- Ghost extends past recording end during block
- Spawn fires when overlap clears
- Warp queue: in-bubble spawns flushed, out-of-bubble deferred

**Done when:** Blocked spawns extend, unblocked spawns fire. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `ParsekFlight.cs`. Depends on 6c-1, 6c-2.

---

### Phase 6d: UI

**Goal:** Spawn warning overlay. Ghost chain status in recording list.

#### Task 6d-1: SpawnWarningUI

**New file:** `SpawnWarningUI.cs`

**Scope:**
- On-screen warning when any real vessel is within 200m of a pending chain-tip spawn
- Shows: vessel name, distance, "spawns at UT=X" or "spawn blocked — move vessel"
- Persists until spawn completes or conflict resolves
- Drawn via `OnGUI` or attached to ParsekUI

**Tests:** `SpawnWarningUITests.cs`
- Warning trigger logic (200m threshold)
- Warning dismissal on spawn completion
- Multiple simultaneous warnings

**Done when:** Warning displays correctly. `dotnet test` passes.

**Files:** 1 new + 1 test. May modify `ParsekUI.cs` for integration.

#### Task 6d-2: Ghost label overlay (in-flight)

**Scope:**
- Floating text label on all ghost GOs: vessel name + "Ghost — spawns at UT=X" (or "Ghost — loop replay")
- Visible at all distances within Zone 1 and Zone 2 (scaled by distance)
- Uses Unity `GUI.Label` or world-space `TextMesh` anchored to ghost root
- This is the minimum viable visual distinction (full visual treatment deferred)

**Tests:** `GhostLabelTests.cs`
- Label text computed correctly for chain ghosts (spawn UT)
- Label text for loop ghosts (informational)
- Label hidden for ghosts in Zone 3 (Beyond)

**Done when:** Ghost labels visible in flight. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `ParsekUI.cs` or `ParsekFlight.cs` (OnGUI).

#### Task 6d-3: Chain status in recording list

**Scope:**
- Modify `ParsekUI.cs` recording list: show ghost chain status
- For claimed vessels: "Ghosted — spawns at UT=X" or "Ghosted — chain extends to UT=Y"
- For chain-tip recordings: "Chain tip — will spawn [vessel name]"

**Tests:** Status string computation from chain data

**Done when:** Chain status visible in UI. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `ParsekUI.cs`.

---

### Phase 6e: Relative-State Time Jump

**Goal:** Implement the discrete UT-skip operation that preserves physics bubble geometry. Separate from normal time warp.

*Source: parsek-vessel-interaction-paradox-addendum-time-jump.md Sections 2-4.*

#### Task 6e-1: BubbleSnapshot + epoch shift

**New file:** `TimeJumpManager.cs`

**Scope:**
- `internal static BubbleSnapshot CaptureBubble()` — for every object in physics bubble, capture: position, velocity, attitude, orbital elements, isGhost, chainTip UT
- `internal static void EpochShiftOrbit(Vessel v, double jumpDelta)` — shift mean anomaly at epoch by jumpDelta while keeping position/velocity/orbit shape unchanged
- `internal static void ExecuteJump(double targetUT)` — set `Planetarium.SetUniversalTime`, epoch-shift all vessels, process spawn queue, trigger game actions recalculation
- Surface case: no epoch shift needed (surface-fixed coordinates are UT-independent on body surface)

**Tests:** `TimeJumpManagerTests.cs`
- Epoch shift: position at new UT matches pre-jump position
- Surface case: lat/lon unchanged
- Jump delta computation
- Bubble snapshot captures all loaded objects

**Done when:** Time jump advances UT without moving vessels. `dotnet test` passes.

**Files:** 1 new + 1 test. Touches 0 existing. Depends on 6b-3 (spawn wiring).

#### Task 6e-2: Spawn queue integration during jump

**Scope:**
- During jump, identify all chain tips crossed between T0 and targetUT
- Process spawns in chronological order
- Apply bounding box check (from 6c-1) to each spawn
- If spawn blocked at ghost's current (unmoved) position, apply ghost extension or trajectory walkback

**Tests:**
- Single chain tip crossed → vessel spawns at correct position
- Multiple independent tips → spawned in chronological order
- Spawn blocked at jump position → ghost extension or walkback applies

**Done when:** Spawns fire correctly during time jump. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `TimeJumpManager.cs`. Depends on 6e-1, 6c-4.

#### Task 6e-3: Selective Spawning UI

**New file:** Extend `SpawnWarningUI.cs` or new `SelectiveSpawnUI.cs`

**Scope:**
- Spawn control panel shown when player is near ghosts with chain tips
- **[Sync & Spawn]** button per chain tip (vessel name, spawn UT, recording chain)
- Intermediate links hidden (spawn suppressed)
- Loop iterations shown as informational only
- Chronological constraint: selecting a tip auto-spawns all earlier independent tips. UI warns: "Also spawns: [name] at UT=[earlier]"
- Button triggers `TimeJumpManager.ExecuteJump(tipUT)`

**Tests:** `SelectiveSpawnUITests.cs`
- Chronological constraint: earlier tips auto-included
- Intermediate links not offered
- Loop iterations informational only

**Done when:** Player can selectively spawn chain-tip ghosts via UI. `dotnet test` passes.

**Files:** 1 new (or extend existing) + 1 test. Depends on 6e-1, 6a-3.

#### Task 6e-4: TIME_JUMP recording event

**Scope:**
- Add `SegmentEventType.TimeJump` enum value
- If FlightRecorder is active during a time jump, emit a TIME_JUMP SegmentEvent with pre-jump and post-jump state vectors (position, velocity, UT)
- Playback handles the discontinuity (visual cut — ghost teleports from pre-jump to post-jump position)
- Serialize in RecordingStore as SEGMENT_EVENT with jump metadata

**Tests:**
- TIME_JUMP event emitted during active recording
- Serialization round-trip
- Playback handles gap correctly (no interpolation across jump)

**Done when:** TIME_JUMP events recorded and played back. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `SegmentEvent.cs`, `FlightRecorder.cs`, `RecordingStore.cs`. Depends on 6e-1.

---

### Phase 6f: Ghost World Presence

**Goal:** Make ghosts visible in map view and tracking station. Register CommNet relay nodes for ghost antennas.

*Source: parsek-vessel-interaction-paradox-addendum-time-jump.md Sections 7.1-7.3.*

#### Task 6f-1: Ghost tracking station + map view markers

**Scope:**
- Ghosted vessels appear in tracking station with distinct icon and info panel (vessel name, "Ghost — spawns at UT=X", claiming recording)
- Ghost orbit line in map view (distinct color or dashed style)
- Player can set ghost as navigation target (approach vectors, closest approach, relative velocity)
- For loaded ghosts: derive position from ghost GO. For unloaded: orbital propagation from recording data.

**Implementation approach:** Register a lightweight tracking entry via KSP's `Vessel` tracking APIs or via custom map node rendering. Needs investigation of KSP's `MapNode` and `PlanetariumCamera` APIs. May require creating a minimal ProtoVessel entry for tracking station visibility without full vessel instantiation.

**Tests:**
- Ghost appears in tracking station list
- Ghost orbit visible in map view
- Navigation target set correctly

**Done when:** Ghosts visible in map view and tracking station. `dotnet test` passes for data paths; in-game verification needed.

**Files:** 1 new (e.g. `GhostMapPresence.cs`) + 1 test. May modify `ParsekUI.cs`.

#### Task 6f-2: Ghost CommNet relay registration

**Scope:**
- Record antenna data at commit time: part names, `antennaPower`, `antennaCombinable`, `antennaCombinableExponent` from `ModuleDataTransmitter` modules on each vessel
- Store as `ANTENNA_SPEC` ConfigNodes in recording sidecar or on Recording
- At ghost time: register CommNet node at ghost position with antenna specs
- Update CommNet node position each frame (loaded: from GO position; unloaded: from orbital propagation)
- Remove CommNet node when ghost is destroyed or chain tip spawns

**Recording data:** Add `List<AntennaSpec>` to Recording (or store in sidecar). Each spec: partName, power, combinable, exponent.

**Tests:**
- Antenna spec extraction from vessel snapshot
- Serialization round-trip
- CommNet node registration/removal lifecycle

**Done when:** Ghost antennas participate in CommNet relay. `dotnet test` passes for data paths; in-game CommNet verification needed.

**Files:** 1 new (e.g. `GhostCommNetRelay.cs`) + 1 test. Modifies `Recording.cs`, `RecordingStore.cs`, `FlightRecorder.cs` (antenna capture).

#### Task 6f-3: Unloaded ghost lifecycle

**Scope:**
- For unloaded ghosts (outside physics bubble): maintain CommNet node + tracking station entry + map marker, but no Unity GO
- Position updates via orbital propagation (same as GhostExtender from 6c-2)
- On player entering physics bubble near unloaded ghost: create Unity GO (transition to loaded ghost)
- On chain tip for unloaded ghost: create ProtoVessel directly (no bounding box check needed)

**Tests:**
- Unloaded ghost has CommNet node and map marker
- Transition to loaded on player approach
- Unloaded chain-tip spawn creates ProtoVessel

**Done when:** Unloaded ghost lifecycle works correctly. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `VesselGhoster.cs`, `GhostMapPresence.cs`. Depends on 6f-1, 6f-2, 6c-2.

---

### Phase 6c Addendum: Trajectory Walkback

#### Task 6c-5: Trajectory walkback for immovable vessel deadlock

*Source: addendum Section 6.7.*

**Scope:**
- Extend `SpawnCollisionDetector`: after spawn blocked for >5s with persistent overlap and blocking vessel hasn't moved, trigger walkback
- `WalkbackAlongTrajectory(Recording rec, double startUT, Bounds spawnBounds)` — walk backward through trajectory frames, check bounding box at each position, return latest non-overlapping position
- Spawn at walkback position, recompute orbital elements for new position
- Fallback: if entire trajectory overlaps, show manual placement UI (configurable radius)

**Tests:** `TrajectoryWalkbackTests.cs`
- Walkback finds valid position 3 frames back
- Walkback with no valid position → fallback triggered
- Timeout before walkback triggers (5s)

**Done when:** Spawn deadlocks with immovable vessels resolved. `dotnet test` passes.

**Files:** 0 new + 1 test. Modifies `SpawnCollisionDetector.cs`. Depends on 6c-1.

---

## 7. Dependencies and Ordering

```
Phase 6a (pure logic, no Unity dependency)
  ├── 6a-1 GhostingTriggerClassifier
  ├── 6a-2 GhostChain data model
  ├── 6a-3 GhostChainWalker ──────────── depends on 6a-1, 6a-2
  ├── 6a-4 Chain-aware spawn suppression ─ depends on 6a-3
  └── 6a-5 PID preservation ───────────── independent (can parallel with 6a-3/4)

Phase 6b (KSP integration)
  ├── 6b-1 VesselGhoster ──────────────── depends on 6a-5
  ├── 6b-2 Chain eval on scene load ────── depends on 6a-3, 6b-1
  ├── 6b-3a Spawn suppression wiring ───── depends on 6a-4, 6b-1, 6b-2
  ├── 6b-3b External ghost skip ────────── depends on 6b-1
  ├── 6b-4 Chain ghost trajectory ──────── depends on 6b-2
  └── 6b-5 Save/load re-evaluation ────── depends on 6b-2

Phase 6c (spawn safety — can start after 6b-3a)
  ├── 6c-1 SpawnCollisionDetector ──────── independent
  ├── 6c-2 GhostExtender ──────────────── independent
  ├── 6c-3 TerrainCorrector + recording ── independent
  ├── 6c-4 Ghost extension wiring ──────── depends on 6c-1, 6c-2
  └── 6c-5 Trajectory walkback ─────────── depends on 6c-1

Phase 6d (UI — can start after 6b-3a)
  ├── 6d-1 SpawnWarningUI ─────────────── depends on 6c-1 (overlap check)
  ├── 6d-2 Ghost label overlay ─────────── depends on 6b-2 (ghost state)
  └── 6d-3 Chain status in UI ─────────── depends on 6a-3 (chain data)

Phase 6e (time jump — can start after 6c-4)
  ├── 6e-1 BubbleSnapshot + epoch shift ── depends on 6b-3a (spawn wiring)
  ├── 6e-2 Spawn queue during jump ─────── depends on 6e-1, 6c-4
  ├── 6e-3 Selective spawning UI ────────── depends on 6e-1, 6a-3
  └── 6e-4 TIME_JUMP recording event ──── depends on 6e-1

Phase 6f (ghost world presence — can start after 6b-2)
  ├── 6f-1 Map view + tracking station ─── depends on 6b-2 (ghost state)
  ├── 6f-2 CommNet relay registration ──── depends on 6b-2
  └── 6f-3 Unloaded ghost lifecycle ────── depends on 6f-1, 6f-2, 6c-2
```

**Parallelization opportunities:**
- 6a-1, 6a-2, 6a-5 can run in parallel
- 6b-3b and 6b-3a can run in parallel (6b-3b only needs 6b-1; 6b-3a needs 6a-4 + 6b-1 + 6b-2)
- 6b-4 and 6b-5 can run in parallel (both depend on 6b-2)
- 6c-1, 6c-2, 6c-3 can all run in parallel
- 6d-1, 6d-2, and 6d-3 can run in parallel
- 6e-3 and 6e-4 can run in parallel (both depend on 6e-1)
- 6f-1 and 6f-2 can run in parallel (both depend on 6b-2)
- Phases 6d, 6e, 6f can begin in parallel once their prerequisites are met

**Bug prerequisites:**
- Bug #49 fix (PID cache): recommended before 6a-3 for performance, not blocking
- Bug #55 fix (anchor filtering): recommended before 6b-4 to avoid ghost/anchor conflicts

---

## 8. Risks

### 8.1 Real Vessel Despawning (High Impact)

First time Parsek modifies real game state. If ghost conversion fails (mod conflict, KSP API change, edge case), the player loses a vessel. The quicksave is the only backup.

**Mitigation:** `VesselGhoster.GhostVessel` wrapped in try/catch. On failure: log error, skip ghosting for that vessel, fall back to current behavior (real vessel untouched). Player sees the paradox-vulnerable behavior but doesn't lose anything.

### 8.2 Cross-Tree PID Linkage (Medium Impact)

Chain walker must connect chains across committed trees by matching leaf `VesselPersistentId` to subsequent MERGE `TargetVesselPersistentId`. This depends on PID preservation (Task 6a-5). If any code path spawns a chain-tip vessel without preserving PID, the cross-tree link breaks silently.

**Mitigation:** Task 6a-5 is an early deliverable. All chain-tip spawn paths route through `VesselGhoster.SpawnAtChainTip` which enforces `preserveIdentity=true`. Direct calls to `VesselSpawner.RespawnVessel` for chain-tip spawns are a review-failure.

### 8.3 KSP Vessel Lifecycle Side Effects (Medium Impact)

Despawning real vessels may interact with: contracts referencing the vessel, other mods tracking vessel references, KSP tracking station, crew roster, resource processing. Each is a potential edge case.

**Mitigation:** Test with common mod configurations. Document known incompatibilities. Graceful degradation — ghost conversion failure doesn't lose data.

### 8.4 Background Recording Coverage Gaps (Low Impact)

Chain ghosts rely on background recording for trajectory. If the claimed vessel was outside the physics bubble for part of the ghost window, there's no trajectory data.

**Mitigation:** `GhostExtender` (Task 6c-2) provides orbital propagation fallback from terminal orbit elements or surface position. Same pattern as existing on-rails vessel handling.

### 8.5 Save Mid-Ghost-Chain (Low Impact)

Player saves while a vessel is ghosted. On load, the chain must be restored. Since chain state is re-derived (not persisted), the save file doesn't need chain data — but the re-derivation must produce the same result.

**Mitigation:** Chain derivation is deterministic (from committed recordings + current UT). Re-derivation on load is guaranteed to match. The only subtlety: the despawned vessel is NOT in the save file (it was removed). The chain walker must ghost it again from the quicksave's vessel data, which may differ from the save. This needs careful handling — Task 6b-5 addresses it.

### 8.6 Chain Walker Performance (Low Impact)

Scanning all committed trees on every scene load. For typical game sessions (5-20 committed trees, 10-50 recordings each), this is microseconds. Could matter with hundreds of trees.

**Mitigation:** Defer optimization. If performance becomes an issue, cache chain results and invalidate on new commits.

### 8.7 Epoch Shift Precision (Medium Impact)

The time jump epoch-shifts orbital elements so that Keplerian propagation at the new UT produces the same position. Floating-point precision in mean anomaly computation may introduce sub-meter position errors. With long jump deltas (thousands of seconds), accumulated error could be larger.

**Mitigation:** Compute epoch shift from position/velocity vectors directly (state vector → elements at new epoch), not from delta-shifting the mean anomaly. This is numerically more stable. KSP's own `Orbit.UpdateFromStateVectors` can be used.

### 8.8 Planetarium Time Setting (Medium Impact)

`Planetarium.SetUniversalTime` may have side effects: triggering KSP's own time-warp handlers, firing GameEvents, updating celestial body positions. Need to understand what KSP does internally when UT changes outside of normal time progression.

**Mitigation:** Research KSP's time-setting API. May need to use `Planetarium.fetch.time` directly or call through the proper warp API with a zero-duration warp. Test thoroughly for side effects.

### 8.9 CommNet Registration — RESOLVED

**Investigation complete** (see `docs/dev/reference/` architecture analyses). Stock CommNet API `CommNetNetwork.Instance.CommNet.Add(CommNode)` works directly for ghost nodes. No Harmony needed, no ProtoVessel needed. The `CommNode` is a plain object with `precisePosition`, `antennaRelay.power`, and `antennaTransmit.power` fields. CommNetManager is transparent (ghost node maps to null vessel in `commNodesVessels` dict, harmless).

**Mod compatibility (from architecture analyses):**
- **Stock + CommNetManager:** Direct `CommNode` API. Works now.
- **RealAntennas:** Requires `RACommNode` type + 9 RF fields per antenna. Deferred to dedicated RA compat phase.
- **CommNetConstellation:** Requires Harmony prefix on `CNCCommNetwork.SetNodeConnection` for frequency matching. Deferred.
- **RemoteTech:** Completely disables CommNet, runs parallel network. Graceful skip: detect RT assembly, log warning, skip registration.

---

## 9. What Doesn't Change

These existing systems are explicitly NOT affected by Phase 6:

| System | Why Unaffected |
|--------|---------------|
| `SessionMerger` | Merge operates on recording data, not on ghost chain state. Chain evaluation happens post-merge. |
| `RecordingTree` DAG structure | Ghost chains are derived FROM the tree, not stored IN it. No new node types or edge types. |
| `GhostVisualBuilder` mesh building | Ghost meshes are built identically regardless of whether the source is a recording or a chain-ghosted vessel snapshot. |
| `GhostPlaybackState` per-ghost state | Structure unchanged. Chain ghosts use the same state struct as recording ghosts. |
| `TrajectoryMath` pure math | Interpolation, adaptive sampling, orbit search — all unchanged. |
| `PartStateSeeder` | Part state seeding is the same for chain ghosts and recording ghosts. |
| `RenderingZoneManager` | Zone classification unchanged. Chain ghosts are subject to the same zone rules. |
| `GhostSoftCapManager` | Soft caps apply to chain ghosts and recording ghosts equally. |
| Loop anchoring system | Loop anchor validation unchanged. Chain ghosts don't interact with loop anchoring. |
| `EnvironmentDetector` / `CrashCoalescer` | Recording-time classification. Not involved in playback or ghost chain evaluation. |
| Harmony patches | `PhysicsFramePatch` unchanged. No new patch targets needed for ghost chains. |

---

## 10. Backward Compatibility

### 10.1 Existing Saves

Saves from before Phase 6 have committed recordings without ghost chain metadata. The chain walker operates on the same recording data that already exists (BranchPoints, PartEvents, background recordings). **Existing committed recordings will retroactively trigger ghosting** if they contain MERGE/SPLIT events targeting pre-existing vessels. This is correct behavior — the paradox existed before, it just wasn't enforced.

### 10.2 Recording Format

Phase 6c adds `TerrainHeightAtEnd` (double, NaN default). Phase 6f adds `AntennaSpec` list. Phase 6e adds `SegmentEventType.TimeJump`. These are additive fields — old recordings without them play back normally (NaN/null/absent = not applicable). Version bump from v6 to v7.

Old recordings (v6 and below): no terrain height, no antenna specs, no TIME_JUMP events. Chain walker still evaluates them based on BranchPoints and PartEvents.

### 10.3 Ghost Conversion of Quicksave Vessels

On rewind, the quicksave loads vessels that existed at recording start. VesselGhoster despawns claimed vessels from the quicksave state. The quicksave itself is never modified — it remains a full backup. If the player loads the quicksave directly (bypassing Parsek rewind), all vessels are restored with no ghosting.

---

## 11. Diagnostic Logging Plan

### 11.1 Subsystem Tags

| Tag | Scope |
|-----|-------|
| `ChainWalker` | Chain computation, claiming detection, cross-tree linking, intermediate suppression |
| `Ghoster` | Vessel snapshot, despawn, ghost GO creation, chain-tip spawn, cleanup |
| `SpawnCollision` | Bounding box computation, overlap detection, ghost extension, trajectory walkback |
| `TerrainCorrect` | Terrain raycast, altitude correction, clearance computation |
| `TimeJump` | Bubble snapshot, epoch shift, UT set, spawn queue processing |
| `GhostMap` | Tracking station entry, map view marker, navigation target |
| `GhostCommNet` | Antenna spec extraction, CommNet node registration/removal, position updates |
| `SpawnWarning` | 200m threshold warnings, spawn-blocked notifications |

### 11.2 Decision Points That Must Log

**Chain evaluation (ChainWalker):**
- "Vessel PID={pid} claimed by tree={treeId} via {MERGE/SPLIT/BACKGROUND_EVENT} at UT={ut}" — every claim
- "Chain built: vessel={pid} links={count} tip={tipRecId} spawnUT={ut} terminated={bool}" — every chain
- "Cross-tree link: vessel={pid} tree1.leaf={recId} → tree2.MERGE target={pid}" — cross-tree connections
- "No claims found in {count} committed trees" — empty result (normal case)
- "Intermediate spawn suppressed: rec={recId} vessel={name} — later link at UT={laterUT}" — suppression

**Ghost conversion (Ghoster):**
- "Ghosting vessel: pid={pid} name={name} — snapshot captured, despawning" — before despawn
- "Vessel ghosted: pid={pid} — ghost GO created at ({lat},{lon},{alt})" — after success
- "Ghost conversion FAILED: pid={pid} — {exception}. Vessel left untouched." — error path
- "Chain tip spawn: pid={pid} vessel={name} preserveIdentity=true — real vessel created" — spawn

**Spawn collision (SpawnCollision):**
- "Spawn blocked: vessel={name} overlaps with {blockerName} at {distance}m" — block
- "Ghost extending past recording end: orbital propagation from {elements}" — extension start
- "Spawn cleared: vessel={name} — overlap resolved after {seconds}s" — unblock
- "Trajectory walkback: vessel={name} — found valid position {frames} frames back" — walkback

**Time jump (TimeJump):**
- "Time jump initiated: T0={t0} target={target} delta={delta}s objects={count}" — start
- "Epoch-shifted vessel: pid={pid} name={name} dMeanAnomaly={shift}" — per-vessel
- "Time jump complete: {spawnCount} vessels spawned, {ghostCount} ghosts remaining" — end

---

## 12. Critical Test Scenarios

Tests that guard against the most dangerous regressions:

| Scenario | What It Guards Against | Phase |
|----------|----------------------|-------|
| **Single-tree linear chain: A docks to S, rewind, S ghosted, S+A spawns at tip** | Basic chain logic broken → vessel lost or paradox unprotected | 6a, 6b |
| **Cross-tree chain: R1 (Tree1) + R2 (Tree2) chain through S** | PID preservation broken → cross-tree link fails → intermediate spawn fires prematurely | 6a-3, 6a-5 |
| **VesselGhoster failure mid-operation (snapshot ok, despawn fails)** | Partial failure → vessel despawned but no ghost created → vessel lost | 6b-1 |
| **Spawn collision with player vessel at chain tip** | Physics explosion from overlapping spawn | 6c-1, 6c-4 |
| **Save/load during active ghost chain** | Chain state lost on load → claimed vessel not re-ghosted → paradox | 6b-5 |
| **Time jump epoch shift accuracy** | Sub-meter position error after jump → docking alignment destroyed | 6e-1 |
| **Existing v6 recordings retroactively trigger ghosting** | Backward compat broken → old saves crash or behave incorrectly | 6a-3 |
| **Standalone recording (no chain) spawn unchanged** | Chain logic accidentally suppresses normal spawns → all spawn-at-end broken | 6a-4 |
| **Looped recording at chain tip: first loop spawns, subsequent don't** | Loop ghosts create duplicate vessels or extend chain indefinitely | 6a-3 |

---

## 13. Design Constraints and Notes

### 13.1 Atmospheric Time Jump

The time jump epoch-shifts orbital elements. This is exact for Keplerian orbits and trivial for surface-fixed vessels. For vessels in atmospheric flight, the trajectory is not Keplerian — epoch-shifting produces an approximation.

**Decision:** Time jump is available regardless of vessel environment. For atmospheric vessels, the epoch shift is an approximation (position preserved exactly, but post-jump orbital elements may not match the atmospheric trajectory that would have occurred). This is acceptable because:
- Atmospheric ghost chain tips are rare (most chain interactions are orbital or surface)
- The approximation preserves relative positions exactly, which is the primary goal
- Post-jump, KSP's atmospheric physics resumes immediately and corrects any element inconsistency within a few frames

### 13.2 ProtoVessel Boundary for Ghost World Presence

The addendum (Section 7.3) states "No promotion to KSP Vessel" for ghost world presence. However, tracking station visibility and CommNet participation may require some form of ProtoVessel-like registration.

**Boundary rule:** ProtoVessel creation is acceptable ONLY for:
1. **Unloaded chain-tip spawns** (Section 7.1) — the vessel is becoming real, this IS a spawn
2. **Tracking station entries** — if KSP's API requires a ProtoVessel for tracking station visibility, a minimal ProtoVessel with only orbit data and vessel name may be created, but it MUST be marked as non-interactable (no control, no crew, no resources, no contracts)

ProtoVessel creation is NOT acceptable for:
- Loaded ghosts (physics bubble) — these are Unity GameObjects only
- CommNet nodes — these should use the CommNet API directly, not require a Vessel

If investigation (Task 6f-1) reveals that tracking station visibility is impossible without a full ProtoVessel, this becomes a design escalation — discuss with the user before implementing.

### 13.3 Game Actions System Dependency

The time jump (Phase 6e) triggers game actions recalculation at the target UT. This depends on the game actions system (`parsek-game-actions-system-design.md`). If the game actions system is not implemented when Phase 6e executes:

- The time jump's core value (epoch-shift for rendezvous preservation) works independently
- The game actions recalculation step is skipped with a log warning: "Time jump: game actions system not available, skipping recalculation"
- Science/funds/rep/contracts are NOT updated for the jumped interval — the player must warp normally to trigger standard KSP processing if they need these updates

This is an acceptable degradation. The time jump can ship before the game actions system.

### 13.4 Ghost Chain Error Recovery

Ghost chain failures introduce a new failure mode: loss of a real vessel. Error recovery strategy:

| Failure | Recovery | Data Loss |
|---------|----------|-----------|
| Chain walker throws exception | Skip all ghosting. Log error. All vessels remain real. No paradox prevention. | None |
| VesselGhoster snapshot fails | Skip this vessel's ghosting. Log error. Vessel remains real. | None |
| VesselGhoster despawn fails after snapshot | Vessel may be in inconsistent state. Log critical error. Attempt to restore from snapshot. If that fails, quicksave is the backup. | Possible — vessel may be lost until quicksave reload |
| Ghost GO creation fails after despawn | Vessel despawned but no ghost visible. Chain walker tracks it as ghosted (spawns at tip). Player sees vessel disappear then reappear at chain tip. | Visual gap — no gameplay data loss |
| Chain-tip spawn fails | Ghost persists past tip. Log error. Player can reload quicksave. | Vessel stuck as ghost until manual intervention |
| Save during partial ghost conversion | On load, chain walker re-evaluates from committed recordings. If vessel is missing from save AND from quicksave, chain walker logs critical error and skips. | Possible — but both save and quicksave must be corrupted |

**Principle:** Every failure path defaults to "leave the vessel alone" or "the quicksave has it." The worst case (despawn succeeds but everything else fails) is recoverable via quicksave reload. This is communicated to the player via a clear error message: "Ghost conversion failed for [vessel]. Load your quicksave to recover."

---

## 14. Deferred Items

| Item | Reason | When to Revisit |
|------|--------|-----------------|
| Full ghost visual treatment (transparency, outlines, tint) | Ghost labels (Phase 6d-2) provide minimum viable distinction. Full treatment is polish. | When in-game testing reveals labels are insufficient |
| Resource transfer tracking | Moot in stock KSP (requires docking) | If mod compatibility (KAS pipes) becomes a goal |
| Chain walker caching | Trivial cost for typical tree sizes | If profiling shows scene-load latency |
| Manual placement UI (fallback for total trajectory overlap) | Rare edge case — blocking vessel must cover entire approach path | If trajectory walkback proves insufficient in testing |

---

*Document version: 1.2*
*Created: 2026-03-19*
*Updated: 2026-03-20 — Review fixes: split 6b-3, add ghost label (6d-2), add What Doesn't Change / Backward Compat / Diagnostic Logging / Test Plan / Error Recovery sections. Atmospheric time jump, ProtoVessel boundary, game actions dependency documented. "Done:" → "Done when:" throughout.*
*Depends on: parsek-vessel-interaction-paradox.md v1.5, parsek-vessel-interaction-paradox-addendum-time-jump.md v1.4, recording-system-design.md v2.9*
