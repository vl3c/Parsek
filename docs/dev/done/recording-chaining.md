# Recording Chaining

## Goal

Support multi-segment missions as a continuous replay: land a vessel, EVA a kerbal, walk around, board a vessel, fly again — all chained together as one mission that plays back seamlessly.

## Existing Foundation

### EVA Child Recording Pattern

The EVA child recording system already establishes parent-child linkage:
- `Recording.ParentRecordingId` — links child to parent
- `Recording.EvaCrewName` — identifies the EVA kerbal
- Auto-commit: parent recording commits when EVA starts, child recording begins on the EVA kerbal
- On revert: both ghosts play back (vessel ghost and EVA ghost)
- Crew handling: `VesselSpawner.RemoveSpecificCrewFromSnapshot` strips EVA'd crew from parent vessel at spawn time

**Key files:**
- `FlightRecorder.cs` — EVA detection, auto-stop parent, start child
- `VesselSpawner.cs` — crew stripping for EVA'd kerbals
- `ParsekScenario.cs` — serialization of parent/child linkage

### Ghost Handoff Research

From `docs/done/ghost-vessel-visual-replay-research.md` (line 225):
> "replay split actors in parallel while preserving parent/child timelines"

The existing playback system already handles multiple recordings playing simultaneously. The challenge is making the transitions look seamless.

## Design Considerations

### Chain Topology

A mission chain could look like:
```
Vessel A (launch → land) → EVA Kerbal (walk to site) → Vessel A (board → fly) → EVA Kerbal (plant flag) → Vessel A (board → orbit)
```

This is a sequence of recordings linked by vessel/crew identity, not just parent-child pairs.

### What Needs to Generalize

The current EVA system is a special case of chaining:
1. **Trigger**: EVA event during recording → auto-stop parent, start child
2. **Linkage**: `ParentRecordingId` (one level only)
3. **Ghost transition**: Parent ghost ends, child ghost begins
4. **Vessel handling**: Parent vessel snapshot excludes EVA'd crew

For general chaining, we need:
1. **Trigger**: Any vessel change during recording (EVA, boarding, switching)
2. **Linkage**: Chain of recording IDs (not just parent-child)
3. **Ghost transition**: Previous segment ghost ends, next segment ghost begins at the same position/time
4. **Vessel handling**: Crew and vessel state must be continuous across chain boundaries

### Open Questions

- How does the merge dialog work for a chain? One dialog for the whole chain, or per-segment?
- What if the player reverts mid-chain? Does the whole chain get merged or just the completed segments?
- How should "Keep Vessel" work for a chain — spawn the vessel in its final state from the last segment?
- Should chained recordings share a single vessel snapshot (the final state) or each keep their own?

### Edge Cases

- Boarding a different vessel mid-chain (vessel A → EVA → board vessel B)
- Docking during a chain segment
- Vessel destroyed mid-chain (remaining segments become ghost-only)
- Crew member dies mid-chain

## Deep Dive Findings (2026-02-15)

### EVA Child Recording Flow (Detailed)

1. **Detection**: `GameEvents.onCrewOnEva` fires in `ParsekFlight.OnCrewOnEva()` (ParsekFlight.cs:350-382)
   - Checks EVA is from vessel currently being recorded: `data.from.vessel.persistentId == recorder.RecordingVesselId`
   - Sets flags: `pendingEvaChildRecord = true`, `pendingEvaCrewName = kerbalName`, `pendingAutoRecord = true`
   - Does NOT immediately stop parent — returns and lets physics frame handle it

2. **Parent auto-stop**: Next physics frame in `FlightRecorder.OnPhysicsFrame()` (FlightRecorder.cs:450-498)
   - Detects vessel switch: `v.persistentId != RecordingVesselId`
   - Calls `DecideOnVesselSwitch()` (FlightRecorder.cs:728-738) which returns `Stop`

3. **Parent auto-commit**: `ParsekFlight.Update()` (ParsekFlight.cs:134-169)
   - Checks: `pendingEvaChildRecord && !recorder.IsRecording && recorder.CaptureAtStop != null`
   - Stashes + commits: `RecordingStore.StashPending()` → `RecordingStore.CommitPending()`
   - Links parent → child via `activeChildParentId` and `activeChildCrewName` fields

4. **Child auto-start**: Deferred one frame via `pendingAutoRecord` flag (ParsekFlight.cs:172-198)
   - Checks: `pendingAutoRecord && !IsRecording && FlightGlobals.ActiveVessel.isEVA`
   - Creates new `FlightRecorder`, begins sampling
   - Sets `recorder.RecordingStartedAsEva = true`

5. **On revert**: `OnFlightReady()` (ParsekFlight.cs:416-438)
   - Checks if `pending.ParentRecordingId` exists in `RecordingStore.CommittedRecordings`
   - If parent found: auto-commits child **without merge dialog**
   - If parent NOT found: shows merge dialog

### Key Limitation: `DecideOnVesselSwitch`

```csharp
// FlightRecorder.cs:728-738
internal static VesselSwitchDecision DecideOnVesselSwitch(
    uint recordingVesselId, uint currentVesselId, bool currentIsEva, bool recordingStartedAsEva)
{
    if (currentVesselId == recordingVesselId) return None;
    if (currentIsEva && recordingStartedAsEva) return ContinueOnEva;
    return Stop;
}
```

Only allows EVA continuation if recording **started as EVA**. This blocks the core chaining pattern (Vessel → EVA → board vessel → EVA → etc).

### Ghost Playback: Already Supports Multiple Recordings

`ParsekFlight.UpdateTimelinePlayback()` (ParsekFlight.cs:627-739) iterates all committed recordings each frame, managing independent `GhostPlaybackState` per recording in a `Dictionary<int, GhostPlaybackState>`.

**Ghost lifecycle per recording:**
1. **ENTER**: UT enters `[StartUT, EndUT]` and no ghost active → spawn ghost
2. **UPDATE**: While in range → interpolate ghost position, apply part events
3. **EXIT (spawn)**: UT crosses `EndUT`, vessel snapshot exists → hold at final position, spawn vessel, destroy ghost
4. **EXIT (no spawn)**: UT leaves range, no vessel to spawn → destroy ghost

Parent and child ghosts play back simultaneously as independent entries. There is **no handoff concept** — just independent ghosts with overlapping/adjacent time ranges.

### Merge Dialog: Single-Recording

`MergeDialog.Show()` (MergeDialog.cs:10-98) handles one pending recording at a time. Recommends action based on vessel state:
- **Recover**: vessel barely moved
- **Persist**: vessel intact, will spawn at EndUT
- **MergeOnly**: vessel destroyed

EVA children bypass the dialog entirely (auto-committed). A chain would need either one dialog for the whole chain or per-segment decisions.

### Crew Handling Across Recordings

- **Exclusion**: `BuildExcludeCrewSet()` (VesselSpawner.cs:342-360) only walks **one level** — finds immediate children by `ParentRecordingId`
- **Reservation**: `ReserveSnapshotCrew()` (ParsekScenario.cs:331-433) operates per-recording
- **Swap**: `SwapReservedCrewInFlight()` (ParsekScenario.cs:527-583) removes reserved crew from active vessel, inserts replacement
- For chains: crew exclusion must walk the **entire chain** to avoid duplicate crew across spawned vessels

### Recording Data Model (Linkage Fields)

```csharp
// RecordingStore.cs:38-106
public class Recording
{
    public string ParentRecordingId;      // Links child → parent (single reference)
    public string EvaCrewName;            // Identifies EVA kerbal
    // No NextRecordingId, no ChainId, no ChildRecordingIds
}
```

Identification: `RecordingId = Guid.NewGuid().ToString("N")` — immutable UUID.

### What Needs to Generalize

| Area | Current (EVA only) | Needed for Chaining |
|------|-------------------|-------------------|
| **Linkage** | Single `ParentRecordingId` | Chain references (next/previous or chain ID) |
| **Triggers** | `onCrewOnEva` only | Boarding, vessel switch, docking, undocking |
| **Vessel switch** | Stop unless EVA-started-as-EVA | Allow Vessel→EVA→Vessel sequences |
| **Merge dialog** | One recording; child auto-commits | Chain-aware (one dialog or per-segment?) |
| **Crew exclusion** | Walk one level of children | Walk entire chain |
| **Vessel spawn** | Each recording spawns independently | Decide: final-only or per-segment? |
| **Ghost handoff** | Independent ghosts | Position continuity at boundaries |

### Linkage Model Options

- **Option A**: `NextRecordingId` field — linked list forward. Walk chain by following next pointers.
- **Option B**: `ChainId` field — all recordings in a chain share a group ID. Query by chain.
- **Option C**: Keep `ParentRecordingId`, add `NextRecordingId` — doubly-linked list.

### Trigger Expansion Needed

Current: Only `onCrewOnEva` triggers chain continuation.

For full chaining, must also handle:
1. **Board vessel** from EVA (opposite of EVA)
2. **Switch to different vessel** mid-flight (e.g., `[`/`]` keys)
3. **Dock** with another vessel (merge)
4. **Undock** (split)
5. **Crew transfer** between docked vessels

No `onBoardVessel` GameEvent exists in KSP — boarding is detected implicitly as vessel-switch from EVA vessel to non-EVA vessel.

### Open Design Questions (Expanded)

1. **Spawn strategy**: Only the final vessel in a chain? Or each segment's vessel at its EndUT?
2. **Merge UX**: One dialog per chain, or per-segment cherry-picking?
3. **Mid-chain revert**: Commit whole chain or only completed segments?
4. **Ghost continuity**: How to handle position gaps at chain boundaries? (interpolate? teleport? visual indicator?)
5. **Docking**: Is it a chain trigger? How do merged vessels map to recordings?
6. **Crew death mid-chain**: Clear later segments' snapshots? Ghost-only from that point?
7. **Resource deltas**: Apply incrementally through chain, or all-at-once at final EndUT?

### Key Files & Line References

| Area | File | Lines | Function |
|------|------|-------|----------|
| Recording class | RecordingStore.cs | 38-106 | `Recording` |
| Recording capture | FlightRecorder.cs | 327-445 | `StartRecording()`, `StopRecording()` |
| Physics sampling | FlightRecorder.cs | 450-537 | `OnPhysicsFrame()` |
| Vessel switch decision | FlightRecorder.cs | 728-738 | `DecideOnVesselSwitch()` |
| Ghost playback loop | ParsekFlight.cs | 627-739 | `UpdateTimelinePlayback()` |
| Ghost lifecycle | ParsekFlight.cs | 816-932 | `SpawnTimelineGhost()`, `DestroyTimelineGhost()` |
| EVA auto-commit | ParsekFlight.cs | 134-169 | `Update()` |
| EVA auto-record start | ParsekFlight.cs | 172-198 | `Update()` |
| Child auto-commit on revert | ParsekFlight.cs | 416-438 | `OnFlightReady()` |
| Merge dialog | MergeDialog.cs | 10-98 | `Show()` |
| Save | ParsekScenario.cs | 28-82 | `OnSave()` |
| Load | ParsekScenario.cs | 90-246 | `OnLoad()` |
| Crew reservation | ParsekScenario.cs | 331-433 | `ReserveSnapshotCrew()` |
| Crew swap | ParsekScenario.cs | 527-583 | `SwapReservedCrewInFlight()` |
| Vessel spawning | VesselSpawner.cs | 207-340 | `SpawnOrRecoverIfTooClose()` |
| Crew exclusion | VesselSpawner.cs | 342-360 | `BuildExcludeCrewSet()` |
| Ghost building | GhostVisualBuilder.cs | 37-150 | `BuildTimelineGhostFromSnapshot()` |
| Part events | ParsekFlight.cs | 934-999 | `ApplyPartEvents()` |

## Codex Alternative Deep Dive & Feedback (2026-02-15)

### 1. Challenging Assumptions

- The linked-list/chain model may be **over-designed for v1**. A simpler approach: infer "continuation groups" at playback time from UT adjacency + actor continuity (same kerbal name or vessel lineage), keeping recordings otherwise independent in storage.
- Better abstraction than a recording list: **model transitions as edges** — a separate `Transition` record with `fromRecordingId`, `toRecordingId`, `triggerType`, `boundaryUT`, `actorId`. Recordings stay immutable; chain logic lives entirely in edges. This avoids rewriting recording nodes when inserting or removing segments.

### 2. Risks & Pitfalls (Hard Blockers)

- **State continuity is the real blocker**, not linkage. Crew/vessel/ownership consistency across segment boundaries is where bugs will live.
- **Crew exclusion is one-hop only** (`VesselSpawner.BuildExcludeCrewSet`, VesselSpawner.cs:342) — will duplicate crew in long chains if not generalized to walk the full chain.
- **Save integrity risk**: current load logic relies on recording index order for state restore; chain reordering/insertion can desync replay and resource state (ParsekScenario.cs:99).
- **Corruption risks**:
  - Cycles or dangling IDs in chain metadata
  - Partial deletes leaving orphan segments/files
  - Two heads pointing to same child (forks) unless explicitly supported
- **KSP-specific**: docking/undocking mutates vessel identity in non-intuitive ways — "same vessel" is not a stable assumption after topology changes.
- **Revert path risk**: auto-commit paths are EVA-special-cased and may bypass intended user decisions (ParsekFlight.cs:408).

### 3. Linkage Model Evaluation

| Option | Pros | Cons |
|--------|------|------|
| **A: NextRecordingId** | Simple write path | Fragile under deletion/insertion, needs head discovery, one bad pointer corrupts chain |
| **B: ChainId** | Robust grouping, easiest migration/queries | Needs separate ordering field (`ChainOrder` or boundary UT) |
| **C: Doubly-linked** | Bidirectional traversal | Highest maintenance, two pointers must stay consistent forever |
| **D: Edge table (new)** | Most flexible, safest for future triggers, least mutation of existing recordings | Slightly more complex schema |

**Recommendation**: **Option B (ChainId + explicit order)** is the best of the listed options. **Option D (edge table)** is even better if the schema cost is acceptable — recordings stay immutable, chain logic is fully external.

### 4. Scope v1 Narrowly

- Handling EVA, boarding, vessel switch, docking, undocking, AND crew transfer in v1 is **too broad**.
- **Recommended v1 scope**:
  - EVA exit from recorded vessel
  - Boarding from EVA back to vessel
  - No docking/undocking/crew-transfer chaining
- Docking and transfer are "combinatorial complexity multipliers" that will dominate bug volume and stall stabilization.

### 5. Ghost Handoff is Serious

- Even small UT/sample mismatches create **visible position pops**, especially near ground contact.
- Practical fixes (in priority order):
  1. **Explicit boundary anchor**: copy prior segment's final pose as next segment's initial pose at record time
  2. **Short blend window** (0.25–1.0s overlap/crossfade) at playback time
  3. **Intentional visual discontinuity** (brief fade/marker) for gaps exceeding a threshold — never silent teleport
- Without boundary handling, chains may be technically correct but visually broken.

### 6. Merge Dialog UX

- Per-segment dialogs are technically flexible but **player-hostile**.
- **Chain-level dialog** matches the player mental model ("this mission").
- Best UX: chain-level dialog with default action + expandable per-segment overrides (v2).
- **v1**: ship chain-level only. Per-segment cherry-picking can come later.

### 7. Blind Spots & Missing Considerations

- **Graph validator on load**: detect cycles, missing IDs, duplicate order, orphaned segments. Degrade safely to standalone playback rather than crashing.
- **Migration policy**: old recordings with no chain data must remain playable unchanged. Chain fields should be optional/nullable.
- **Spawn policy for multi-vessel chains**: if a chain crosses vessels (A → EVA → board B), spawning every segment's vessel creates duplicates/conflicts. Need clear rule: spawn only the final segment's vessel? Or only segments with unique vessel IDs?
- **Deterministic identity rules**:
  - Crew identity by name can break in modded games (name changes)
  - Vessel identity after docking/undocking is unstable
- **Invariants to document and test**:
  - Exactly one chain head (unless branching is explicitly supported)
  - Segment time monotonicity within a chain
  - No crew member appears in two live spawned vessels simultaneously

### Bottom Line

Ship a narrow v1: **EVA/board chaining only**, Option B with order (or transition-edge model), explicit boundary anchoring, chain-level merge UX, and strict load-time validation with fallback. Avoid docking/transfer in v1.

## Reference Docs

- `docs/done/ghost-vessel-visual-replay-research.md` — ghost handoff patterns
- `Source/Parsek/FlightRecorder.cs` — EVA child recording implementation
- `Source/Parsek/VesselSpawner.cs` — crew stripping for EVA
- `Source/Parsek/ParsekScenario.cs` — parent/child serialization
