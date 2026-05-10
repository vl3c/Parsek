# Recording & Ghost Rendering Policies — Audit

Date: 2026-05-07
Branch: `claude/investigate-recording-policies-6tMZ9`
Scope: read-only audit. No code changes proposed; this is a reference document.

---

## TL;DR — Simple Overview

### What's relative to what, and when

| Source of recording | Default frame | Relative anchor (when Relative) |
|--------------------|---------------|--------------------------------|
| Focused vessel — alone | Absolute (planet-fixed) | n/a |
| Focused vessel — within 2300 m of an eligible vessel | Relative | The nearest **live, eligible** vessel at sample time |
| Background vessel — alone | Absolute | n/a |
| Background vessel — within 2300 m of focused or another tracked vessel | Relative | The nearest eligible recording / vessel at sample time |
| Background vessel on rails (timewarp / unloaded) | **No TrackSections at all** | Only `OrbitSegment`s are stored |
| Debris (background) | Absolute initial point, then per-section choice | Whatever was nearest at sample time — **NOT necessarily the parent** |
| Re-Fly main vessel | Same as regular flight after settle | Same as regular flight |
| Re-Fly debris | Same as regular debris | Same as regular debris |

### Hysteresis thresholds

- Enter Relative when distance to anchor < `2300 m` (`PhysicsBubbleMeters`, `ParsekConfig.cs:34,44`)
- Exit Relative when distance to anchor ≥ `2500 m` (`ParsekConfig.cs:45`)

### Playback frame dispatch (correction — see review history)

The Absolute/Relative contract is decided per-section by `section.referenceFrame`. There is **no** `IsDebris`, no `IsReFly`, no scenario-aware branching anywhere in playback — what changes between scenarios is the **data the recorder wrote**, not the playback logic.

**However**, the dispatch is *physically dispersed*, not a single line. The world-position math lives across `IGhostPositioner` methods (`InterpolateAndPosition`, `InterpolateAndPositionRelative`, `PositionLoop`, etc.) implemented in `ParsekFlight.cs` (~15961-16082+). Additional `referenceFrame` checks exist at `GhostPlaybackEngine.cs:4578, 4631, 4647`. The earlier draft of this audit called it a "single dispatch at line 2369" — that's a gating helper inside `TryGetRelativeSectionAtUT`, not the actual world-position dispatcher. Changes to the resolver have wide blast radius but do not represent "one place to fix things."

---

## Your Expectation vs. Reality (Debris)

> "My expectation is that debris is always recorded and rendered using relative data anchored to its debris parent."

**This is not what the code does.** Here is exactly what does happen:

### What's true

- A debris recording does carry a topology link to its parent: `Recording.ParentBranchPointId` (Recording.cs:239), and `BranchPoint.ParentRecordingIds` lists the parent recording ID.
- Right after a split, debris is *physically close to* the parent, so the per-section nearest-anchor search **usually** picks the parent recording as the anchor for its early Relative sections.

### What's false (and why bugs land here)

1. **The parent link is topology, not a frame anchor.** Nothing in the recording or playback path reads `ParentBranchPointId` to decide what to anchor to.

2. **The first sample of a freshly-split debris recording is always Absolute**, not relative-to-parent. See `RegisterChildRecordingsFromSplit` at `BackgroundRecorder.cs:1059`:
   - Line 1104-1107: `childInitialPoint = CreateAbsoluteTrajectoryPointFromVessel(...)`
   - Line 1108: `OnVesselBackgrounded(child.VesselPersistentId, inherited, initialTrajectoryPoint: childInitialPoint)`

3. **Subsequent sections obey the standard nearest-anchor rule**, not a parent-anchor rule. Each Relative section's `anchorRecordingId` is whatever was the nearest *eligible* recording at sample time. That's often the parent in the first second after a stage separation, but:
   - If the focused vessel is closer than the parent (e.g. you're flying the daughter craft and the booster is the debris), the focused recording gets picked instead.
   - If a third vessel is closer (a station, another launched ship), it can be picked instead.
   - As the debris drifts away from the parent past the 2500 m exit threshold, sections flip back to **Absolute**.

4. **Cascade cap drops debris-of-debris entirely.** `MaxRecordingGeneration = 1` (`BackgroundRecorder.cs:88`). When a piece of debris breaks up further, the new fragments get **no recording at all** (the parent debris recording keeps sampling). If you've seen "second-stage breakup ghosts don't appear", this is why.

5. **Debris TTL = 60 seconds.** `DebrisTTLSeconds = 60.0` (`BackgroundRecorder.cs:82`). After 60 s the debris recording is finalized (recording stops). Anything that happens to the debris physically after that is invisible to playback.

6. **Focused-vessel debris and background-vessel debris use *different* registration paths.** Focused-vessel debris goes through the crash-coalescer window before becoming a child recording (`ParsekFlight.DeferredJointBreakCheck` → `crashCoalescer.OnSplitEvent`). Background-vessel debris registers immediately one frame after the joint break (`BackgroundRecorder.RegisterChildRecordingsFromSplit`). Branch-point timing semantics differ between the two.

### Likely bug families this produces

- **Debris ghost teleports / desyncs** when the recorded `anchorRecordingId` is a vessel that is itself far away from where it was at recording time. Because anchor selection is "nearest at sample time", an anchor that was once nearby can later drift, and playback reproduces the original offset relative to *current* anchor pose — so the debris appears in the wrong place.
- **Debris ghost disappears mid-flight** — TTL hit at 60 s.
- **Debris ghost never spawns** — generation cap or `SpawnSuppressedByRewind` metadata from a Re-Fly stripper.
- **Debris ghost renders fine on first replay, broken after a Re-Fly merge** — because the parent that was its anchor got superseded, and `RelativeAnchorResolver` now has to chase a re-fly chain to find the substitute pose.
- **Cascade breakup invisible** — `MaxRecordingGeneration = 1` silently drops the second generation.

If you want debris to be *truly* parent-anchored as a design contract, that's a deliberate change: store the parent recording ID at split, write it into every Relative section's `anchorRecordingId` for that recording's lifetime, and skip the nearest-anchor search for debris recordings. The plumbing does not exist today.

---

## Detailed Audit

### 1. Recording policies by scenario

#### 1a. Main (focused) vessel during regular flight

**Decision site:** `FlightRecorder.StartRecording` (`FlightRecorder.cs:5386`), per-frame sampling via Harmony patch into `OnPhysicsFrame`.

**Reference frame (per `TrackSection`, hysteresis-gated):**
- Constants: `RelativeFrame.EntryMeters = PhysicsBubbleMeters = 2300m`, `RelativeFrame.ExitMeters = 2500m` (`ParsekConfig.cs:34,42-46`).
- Decision: `AnchorDetector.ShouldUseRelativeFrame(distance, currentlyRelative)` (`AnchorDetector.cs:311`). Read at `FlightRecorder.cs:5284`.

**Cadence:** adaptive via `TrajectoryMath.ShouldRecordPoint` (motion + attitude thresholds). High-fidelity proximity boost when another tracked vessel is close. Discrete events (`onPartDie`, `onPartJointBreak`) appended immediately. Structural-event snapshot on each joint break (`FlightRecorder.cs:1032`).

**Anchor selection:** `BuildRecordingAnchorCandidateList` (`FlightRecorder.cs:4894` area). Eligibility filter `IsLiveRecordingAnchorVesselCandidate` (`FlightRecorder.cs:5048-5073`) **excludes** `Debris`, `EVA`, `SpaceObject`, `Flag`, `LANDED`, `SPLASHED`, `PRELAUNCH`.

#### 1b. Debris from main vessel during regular flight (multi-stage)

1. `FlightRecorder.OnPartJointBreak` (`FlightRecorder.cs:976`) appends `Decoupled` `PartEvent` to the **parent** recording, sets `HasPendingJointBreakCheck = true` (`FlightRecorder.cs:1037`).
2. One frame later: `ParsekFlight.DeferredJointBreakCheck` (`ParsekFlight.cs:4584`) classifies via `SegmentBoundaryLogic.ClassifyJointBreakResult` (`ParsekFlight.cs:4708`). Result ∈ `{WithinSegment, StructuralSplit, DebrisSplit}`.
3. **`WithinSegment`** (parts broke off but vessel didn't split): no child recording; `SegmentEvent.PartDestroyed` emitted on parent (`ParsekFlight.cs:4722-4750`).
4. **`StructuralSplit`** / **`DebrisSplit`**: each new vessel PID fed to `crashCoalescer.OnSplitEvent` (`ParsekFlight.cs:4800`) with pre-captured snapshot + trajectory point. Coalescer batches splits inside its window before emitting BREAKUP branch and creating child recordings.
5. Bug-#263 fallback at `ParsekFlight.cs:4827` ensures every new vessel root has a `Decoupled` PartEvent on the parent even if KSP dropped the original `onPartJointBreak`.

**Frame for the new debris recording:** registered into `tree.BackgroundMap` via the coalescer; from then on sampled by `BackgroundRecorder` with the same per-section frame logic as any other background vessel. Initial point is **Absolute**.

**`IsDebris` flag:** set when the new vessel has no controller (`!hasController` — `BuildBackgroundSplitBranchData` at `BackgroundRecorder.cs:1176`). Controlled splits (e.g. separated probe core) record normally with no TTL.

#### 1c. Debris from a background vessel

**Decision site:** `BackgroundRecorder.OnBackgroundPartJointBreak` (`BackgroundRecorder.cs:423`) — **separate handler from FlightRecorder**, both subscribed to `GameEvents.onPartJointBreak`. Early-out at line 437: only fires when the broken-joint vessel is in `tree.BackgroundMap`. Mutually exclusive with the focused path by membership check.

Same structural-joint guard (`IsStructuralJointBreak`, `FlightRecorder.cs:1109`) and dedup (`BackgroundRecorder.cs:447, 458`). Schedules a deferred split check (`pendingBackgroundSplitChecks`); processed by `ProcessPendingSplitChecks` (`BackgroundRecorder.cs:538`) → `RegisterChildRecordingsFromSplit` (`BackgroundRecorder.cs:1059`).

**Cascade cap:** `MaxRecordingGeneration = 1` (`BackgroundRecorder.cs:88`). `ShouldSkipForCascadeCap` (`BackgroundRecorder.cs:1456`) returns `parentGeneration >= MaxRecordingGeneration`. Debris-of-debris **gets no recording**.

**Debris TTL:** `DebrisTTLSeconds = 60.0` (`BackgroundRecorder.cs:82`). Set at creation: `debrisTTLExpiry[childPid] = branchUT + 60s` (`BackgroundRecorder.cs:1116`). Sole lifetime enforcement: `CheckDebrisTTL` (`BackgroundRecorder.cs:1191`).

**Reference frame:** child inherits parent's frame at the boundary point, then makes its own per-section frame decisions via `AnchorDetector.ShouldUseRelativeFrame` at `BackgroundRecorder.cs:3476`.

**Controlled vs uncontrolled:** uncontrolled → `IsDebris = true` + 60 s TTL; controlled → `IsDebris = false`, no TTL.

#### 1d. Other nearby real vessels (non-focused, non-debris)

Every vessel in the tree's `BackgroundMap` gets per-frame sampling via `BackgroundRecorder`. Two states:

- **On-rails** (`BackgroundOnRailsState`, `BackgroundRecorder.cs:149`): packed/timewarped vessels; closed orbit segments are wrapped by `OrbitalCheckpoint`/`Checkpoint` sections for section-authoritative persistence, but the path still emits **no env-classified per-frame TrackSections** and runs no boundary classification. Early-return on `bgVessel.packed` in `OnBackgroundPhysicsFrame`. Invariant called out in CLAUDE.md.
- **Loaded** (`BackgroundVesselState`, the loaded counterpart): full per-physics-frame trajectory sampling. Same hysteresis as focused recorder.

**Anchor selection for background vessels:** distinct code path from focused — `BuildBackgroundRecordingAnchorCandidates` (around `BackgroundRecorder.cs:3534`) calls `TryGetBackgroundEligibleAnchorRecording` (around `BackgroundRecorder.cs:3675`).

#### 1e. Main vessel during Re-Fly mode

**Gating flag:** `ParsekScenario.ActiveReFlySessionMarker`, set by `RewindInvoker.cs:1013`, cleared on abort/completion. Holds `ActiveReFlyRecordingId` (the provisional re-fly recording) and `InPlaceContinuation` semantics.

**Recording uses the same `FlightRecorder` path as regular flight**, with two suppressions:
1. **Settle suppression:** for ~1-2 s post-load, trajectory writes blocked via `ShouldSuppressReFlyPostLoadTrajectoryWrite` (`FlightRecorder.cs` ~5728). After unpacked-frame + min-settle-time both clear, suppression releases.
2. **Spawn-death suppression:** `ParsekPlaybackPolicy` checks the marker to skip the spawn-death loop while re-fly is active.

After settle clears, recorder behaves identically to regular flight. The provisional recording's ID lives in `marker.ActiveReFlyRecordingId`, distinct from the origin recording it supersedes. On commit, `SupersedeCommit.cs:65` appends `RecordingSupersedeRelation` rows.

#### 1f. Debris from main vessel during Re-Fly mode

Same decouple path as 1b (FlightRecorder → DeferredJointBreakCheck → coalescer → BackgroundRecorder). **Recording itself is unchanged.** The Re-Fly difference is *playback policy*: new debris recordings created during a Re-Fly session may carry `Recording.SpawnSuppressedByRewind` metadata (set by `PostLoadStripper.Strip` referenced from `MergeJournalOrchestrator`). This metadata is read at *playback*, not recording.

#### 1g. Debris from a background vessel during Re-Fly mode

Same as 1c — `BackgroundRecorder.OnBackgroundPartJointBreak` → `RegisterChildRecordingsFromSplit`. Re-Fly does not change the background-debris recording path.

### 2. Ghost rendering policies

Frame decision per section:

```
if (section.referenceFrame != ReferenceFrame.Relative) { /* absolute */ }
else { /* relative — requires anchorRecordingId in v11+ */ }
```

This decision reads `section.referenceFrame` only. **Does NOT check `IsDebris`, vessel type, or any scenario flag.** Every recording — focused, debris, background, re-fly — uses the same scheme. (See "Playback frame dispatch (correction)" earlier in the document — the actual world-position math is dispersed across `IGhostPositioner` methods on `ParsekFlight`, not a single dispatcher line.)

#### 2a. Main vessel ghost (regular flight)

- **Absolute sections:** `body.GetWorldSurfacePosition(lat, lon, alt)` — planet-fixed.
- **Relative sections (v11):** `RelativeAnchorResolver.TryResolveRelativeSectionPose` (`RelativeAnchorResolver.cs:540`). Reads `section.anchorRecordingId`. Resolves anchor pose recursively through `TryResolveAnchorPose` (`RelativeAnchorResolver.cs:82`) — anchor itself can have Absolute or Relative sections; recursion handles chains with cycle detection (line 99).
- **Loop-anchor live-PID fallback:** if `recording.LoopAnchorVesselId != 0` (`RelativeAnchorResolver.cs:154`), anchor is a *live* vessel resolved by persistent ID. Loop-replay live-PID contract; gated for loop replays only.
- Map / tracking-station rendering goes through `GhostMapPresence.cs` which uses the same resolver chain.

#### 2b. Debris ghost (regular flight)

**Same dispatch as 2a.** Engine doesn't branch on `IsDebris`. Debris recordings have whatever `section.referenceFrame` and `anchorRecordingId` the recorder chose at sample time. Visually they orbit / track relative to their anchor *if and only if* the recorder was in Relative mode at that point.

`IsDebris` is read elsewhere — spawn skipping (`GhostPlaybackLogic.cs:4264, 4286`), tracking-station marker filtering (`AtmosphericMarkerSkipReason.Debris`, `ParsekTrackingStation.cs:619`), map presence (`GhostMapPresence.cs:3496`) — **never to choose between Absolute and Relative.**

#### 2c. Background real vessel ghost

Same dispatch. Background recordings have either Absolute sections or Relative sections cross-anchored to **another** recording (typically the focused vessel's recording when within physics-bubble range, or another loaded background recording). Self-anchoring is **not possible**: `AnchorDetector.IsRecordingAnchorEligible` (`AnchorDetector.cs:190`) rejects any candidate whose `RecordingId` equals the focus recording's, and the background candidate builders (`BuildBackgroundRecordingAnchorCandidates` / `AddBackgroundLiveAnchorCandidates`) skip the queried recording's own ID. So a Relative section's `anchorRecordingId` always points to a different recording.

#### 2d. Main vessel ghost during Re-Fly

Provisional recording (`marker.ActiveReFlyRecordingId`) plays through the same engine. `RelativeAnchorResolver` has Re-Fly-aware logic (`TryResolveActiveReFlyAnchorRecording` around `RelativeAnchorResolver.cs:943`, unverified line) that walks back to the origin's sibling at the rewind point when the provisional's anchor is itself a recording superseded by the re-fly.

#### 2e. Debris ghost during Re-Fly

Same dispatch. The Re-Fly difference is purely **whether the ghost spawns at all** (`SpawnSuppressedByRewind` gate in `ParsekPlaybackPolicy`), not how its trajectory is computed.

#### 2f. Other / background nearby vessel ghosts

Same dispatch. Sections are either Absolute or Relative cross-anchored to another recording, identical to 2c. Self-anchoring is not possible.

### 3. Coupling analysis — where changing one breaks another

| # | Coupling | Risk |
|---|----------|------|
| 1 | **Frame dispatch is unified-by-contract but physically dispersed.** Per-section `section.referenceFrame` checks span `IGhostPositioner` methods on `ParsekFlight` (`InterpolateAndPosition*`, `PositionLoop` at ~`ParsekFlight.cs:15961-16082`) plus additional gates at `GhostPlaybackEngine.cs:4578, 4631, 4647`. No scenario-aware branching, but no single dispatch line either. | Any change to anchor resolution instantly affects focused, debris, background, *and* re-fly playback. No per-scenario insulation. |
| 2 | **Two parallel anchor-candidate builders.** Focused: `BuildRecordingAnchorCandidateList` (`FlightRecorder.cs` ~4894) + eligibility at `IsLiveRecordingAnchorVesselCandidate` (`FlightRecorder.cs:5048`). Background: `BuildBackgroundRecordingAnchorCandidates` (`BackgroundRecorder.cs` ~3534) + `TryGetBackgroundEligibleAnchorRecording` (~3675). | Eligibility rules can drift. A change in one place won't propagate. |
| 3 | **Hysteresis state lives on the recorder instance.** Focused reads `currentTrackSection.referenceFrame == Relative` (`FlightRecorder.cs:5205`). Background reads `state.isRelativeMode` (`BackgroundRecorder.cs:3476`). | Vessel switches (focused ↔ background, re-fly load) must reconcile hysteresis state. Stale state can produce frame mismatch at the boundary. |
| 4 | **Two `onPartJointBreak` handlers.** `FlightRecorder.OnPartJointBreak` (`FlightRecorder.cs:976`) and `BackgroundRecorder.OnBackgroundPartJointBreak` (`BackgroundRecorder.cs:423`). Mutually exclusive by `BackgroundMap` membership; both call `IsStructuralJointBreak` (`FlightRecorder.cs:1109`). | Adding behaviour to one handler and forgetting the other splits debris recording behaviour by parent type. |
| 5 | **Cascade cap is a single magic int** (`MaxRecordingGeneration = 1`, `BackgroundRecorder.cs:88`) checked only inside the background path. | Bumping the cap to 2 breaks downstream "debris-of-debris is silently dropped" assumptions with no compile-time signal. CLAUDE.md flags this. |
| 6 | **Debris TTL is the only lifetime enforcement** for uncontrolled split children (`CheckDebrisTTL`, `BackgroundRecorder.cs:1191`; `DebrisTTLSeconds = 60.0`). | Any code path creating a debris recording but skipping the TTL set (line 1116) will record forever. No defensive sweep. |
| 7 | **`ReFlySessionMarker` is a single-instance flag on `ParsekScenario`** read by ≥6 subsystems: `FlightRecorder` (settle suppression), `ParsekPlaybackPolicy` (spawn-death + spawn-suppress), `RewindInvoker`, `MergeJournalOrchestrator` (`PostLoadStripper`), `RewindPointReaper`, `EffectiveState`, `TreeDiscardPurge`, `ReFlyRevertButtonGate`, `MarkerValidator`. | Lifecycle bugs (early clear, double-set, partial mid-session clear) ripple through all of them simultaneously. No per-subsystem latch. |
| 8 | **`SpawnSuppressedByRewind` is metadata, not a flag.** Stamped on individual recordings during `PostLoadStripper.Strip`. | If metadata is set on the provisional but not on debris created *after* the stripper ran, those debris ghosts spawn during re-fly. Symmetric problem on cleanup: marker cleared before stripping metadata back off → suppression persists past re-fly. |
| 9 | **Crash coalescer windows decouple recording-time from registration-time.** Focused-vessel debris fed to `crashCoalescer.OnSplitEvent` (`ParsekFlight.cs:4800`) and only later registered in `BackgroundMap`. | A second decouple inside the coalescer window is grouped with the first; tweaking the window length affects which debris pieces share a branch point. Background-vessel debris bypasses the coalescer (registers directly in deferred check), so the two debris sources have **different branching semantics**. |
| 10 | **Debris parent linkage is topology-only.** `Recording.ParentBranchPointId` (`Recording.cs:239`) records the DAG link, but there is no code that uses it to determine `anchorRecordingId` at recording time. | If callers assume "debris is anchored to parent" (a reasonable mental model), they can be wrong: anchor selection runs on the same nearest-eligible-anchor algorithm as any vessel, and an unrelated nearby recording can be picked as the anchor instead. |

### 4. Bottom line — are policies cleanly separated?

**Cleanly separated:**
- Two recording entry points (focused → `FlightRecorder`, all others → `BackgroundRecorder`) are mutually exclusive by `BackgroundMap` membership, with explicit early-outs. Joint-break handling correctly bifurcates.
- On-rails vs loaded distinction inside `BackgroundRecorder` is enforced by separate state classes (`BackgroundOnRailsState` vs `BackgroundVesselState`) with a deliberate invariant: on rails may emit orbit-only checkpoint sections, but not env-classified per-frame sections.

**Tangled and risky:**
- **Playback world-position math is dispersed** across `IGhostPositioner` methods on `ParsekFlight` plus per-section `referenceFrame` checks across multiple files. Touching `RelativeAnchorResolver` is global-blast-radius — the resolver feeds every scenario. (Earlier wording called it a "single dispatch"; that was wrong. The contract is unified, the implementation is scattered.)
- **Recording-level anchor eligibility is centralized** in `AnchorDetector.IsRecordingAnchorEligible` (`AnchorDetector.cs:190`), shared by both recorders. Vessel-level live-peer filtering (`IsLiveRecordingAnchorVesselCandidate`, `FlightRecorder.cs:5048`) is focused-only by design — not a duplication. (Earlier wording flagged this as duplicated; that was wrong.)
- **Re-Fly is a constellation of singletons** (`ActiveReFlySessionMarker` + per-recording `SpawnSuppressedByRewind` + journal phase) read across many files, with no single "Re-Fly observability" module.
- **The cascade cap is a magic constant** with comments noting that callers assume `=1` semantics.
- **Debris is *not* parent-anchored by design** — it's nearest-anchor like everything else. If you want a parent-anchor contract for debris, that's a deliberate change.

### 5. Refactor leverage points

If you want refactor-safety, the highest-leverage centralizations:

1. **Single `AnchorEligibility` module** shared by both recorders.
2. **Typed `ReFlyContext`** instead of raw marker reads scattered across files.
3. **Explicit per-scenario tests for the playback dispatch** — a Relative section authored for each scenario, then verified to render in the right frame regardless of recording origin.
4. **Optional: parent-anchored debris contract** — if that's the desired design, plumb `ParentBranchPointId` → `anchorRecordingId` at `RegisterChildRecordingsFromSplit` time and skip the nearest-anchor search for debris recordings.

### 6. Open / unverified items

These were in the original draft and were not independently confirmed in this pass:

1. Exact line of `TryResolveActiveReFlyAnchorRecording` in `RelativeAnchorResolver.cs` (claimed ~943; file is 1026 lines so plausible).
2. Whether `SpawnSuppressedByRewind` is set on the entire stripped subtree or only the active-focus recording (`PostLoadStripper.Strip` exact behaviour).
3. Whether orphaned provisional recordings are reaped on load if a Re-Fly session is abandoned without merging — `LoadTimeSweep.cs` exists per CLAUDE.md but its closure rules were not read.
4. Whether a debris recording's `anchorRecordingId` is frozen at split (likely) or can re-resolve if the parent is later re-anchored — needs reading `RegisterChildRecordingsFromSplit` followers.

---

## Quick-reference cheat sheet

```
RECORDER          WHEN                          FRAME         ANCHOR
================  ============================  ============  ==================================
FlightRecorder    Focused vessel, alone         Absolute      n/a
FlightRecorder    Focused, peer < 2300 m        Relative      Nearest live eligible vessel
BackgroundRec.    Loaded, alone                 Absolute      n/a
BackgroundRec.    Loaded, peer < 2300 m         Relative      Nearest eligible recording
BackgroundRec.    On-rails (packed)             OrbitalCP     Orbit-only checkpoint sections
BackgroundRec.    Debris just split             Absolute      First sample is absolute
BackgroundRec.    Debris, parent < 2300 m       Relative      USUALLY parent (because nearest)
BackgroundRec.    Debris, parent ≥ 2500 m       Absolute      Per nearest-anchor rule
BackgroundRec.    Debris-of-debris              —             SKIPPED (cascade cap = 1)
BackgroundRec.    Debris > 60 s old             —             FINALIZED (TTL)
FlightRecorder    Re-Fly main, in settle        —             SUPPRESSED for ~1-2 s post-load
FlightRecorder    Re-Fly main, post-settle      Same as       Same as regular flight
                                                regular
BackgroundRec.    Re-Fly debris                 Same as       Same as regular debris
                                                regular        (+ may carry SpawnSuppressedByRewind)
```

```
PLAYBACK              ALL SCENARIOS use the same dispatch
====================  =========================================
section.referenceFrame == Absolute  →  body.GetWorldSurfacePosition(lat, lon, alt)
section.referenceFrame == Relative  →  RelativeAnchorResolver(section.anchorRecordingId)
                                       ├─ Anchor's Absolute → planet-fixed pose
                                       ├─ Anchor's Relative → recurse (cycle-detected)
                                       └─ Loop-PID fallback if recording.LoopAnchorVesselId set
```

The dispatch is identical for focused, debris, background, and Re-Fly — **what differs is the data**, not the rendering policy.
