# Ghost rendering anchor — recording chain rearchitecture

**Status:** approved; Phase A implemented 2026-05-01, Phase B+ pending.
**Author:** synthesised from user's stated intent across multiple sessions.
**Supersedes:** `relative-anchor-rearchitecture-prompt.md` (had drift on the architectural intent).

---

## 1. Goal

Render every ghost in the playback scene at a position derived **only from recorded data** for **non-loop track-section playback** (the Absolute / Relative / OrbitalCheckpoint section types written by the active and background recorders). The live (player-controlled) vessel must never enter the math that places a ghost in those sections. This produces deterministic, jitter-free ghost playback that is invariant under player behaviour during re-fly.

> **Loop-section carve-out.** Looped recordings (`Recording.LoopAnchorVesselId != 0`) are anchored on a designated live vessel by recorder design (e.g., a station the looped craft formation-flies around). v1 leaves the loop-anchor mechanism untouched; the "no live vessel pose" invariant applies to non-loop track sections only. Switching loop anchoring to recording-id is a separate user decision and a separate plan. (See §3.4 for the implementation carve-out and the resolver's loop termination case.)

> **⚠️ Re-Fly UX behaviour change.** Today's Re-Fly tree anchor lock applies a per-frame world-space translation `live_active(now) − recorded_active(t)` to every ghost in the tree, so ghosts visually follow the player's live vessel. **This plan removes that translation.** After Phase D, ghosts in an active Re-Fly tree play back at their **original recorded coordinates** regardless of where the player flies the live vessel. A divergent re-fly (player taking a wildly different path) shows ghosts continuing on the original mission's recorded trajectory; the player's live vessel and the ghost reference frame visibly separate. This is the explicit user intent (see Appendix verbatim quotes), but it is a **product behaviour change**, not a refactor — confirm before Phase D lands.

> **Private-development format break.** Legacy recordings are disposable. Correctness for new v11 recordings is the only compatibility target. v7-v10 recordings may warn, skip Relative sections, or fall back to recorded absolute-shadow data for debugging, but they are **not migrated** and are **not required to play correctly**. Test fixtures and manual repro saves should be re-recorded under v11.

### Downstream consequences of the Re-Fly change

Removing the live Re-Fly anchor lock changes every consumer that assumes ghosts stay spatially near the live re-fly vessel. Phase D cannot be treated as a rendering-only cleanup.

- **Watch camera:** `WatchModeController` / watch mode may follow ghosts to their original recorded positions while the player flies the live re-fly vessel somewhere else. Before D.1 starts, Phase D.0 must decide and record the camera policy: either keep Watch anchored to the live re-fly target during active Re-Fly, or explicitly accept that Watch can leave the player's live vessel to follow recorded ghosts.
- **Distance / soft caps / zone rendering:** `ResolvePlaybackDistanceFromReferencePosition` and zone policy become recorded-coordinate decisions after D.1. In a divergent re-fly, a ghost that was near the player under the old live-anchor translation may be far from the live vessel and may soft-cap or despawn. That is a product behaviour change, not only an internal consistency fix, and needs a v11 divergence test.
- **Map/KSC / tracking views:** map and KSC ghost views also consume ghost positions. D.7 must prove they use the same recorded-coordinate chain, or consciously skip unresolved Relative sections, so the user does not see flight-view and map-view disagreement.

## 2. The architectural model

Each `Recording` has track sections. Some sections are `Absolute` (body-fixed lat/lon/alt). Some are `Relative` (anchor-local offset, with an absolute-shadow alongside in v7+). Today, `Relative` sections store `anchorVesselId` — a runtime live-vessel PID. The plan replaces that with a recording-id chain and treats pid-only Relative sections as legacy/incomplete data.

### Render rule

For any ghost at playback UT `t`:

```
ghost_world(t) = anchor_recording_world(t) + anchor_local_offset(t)
```

where:
- `anchor_local_offset(t)` is the recorded offset stored in the section (the existing `lat`/`lon`/`alt`-as-metres triple in v6+ format, or the world-space delta in legacy v5).
- `anchor_recording_world(t)` is **the world position at UT `t` of the recording the section is anchored on** — recursively resolved by the same render rule.

For `Absolute` sections, no anchor is needed; the section's own data gives world coordinates directly.

### The recursion

Anchors form a **DAG of recording IDs**. Resolution walks the DAG until it reaches an Absolute section. If a new v11 Relative section has no `anchorRecordingId`, that is a recorder bug. The v7 absolute-shadow can still be used as a defensive debug backstop for missing/corrupt anchors, but it is not a compatibility promise.

The DAG is acyclic by construction: every recording is anchored only on recordings that **already existed** when its samples were taken. A re-fly creates a new recording that anchors on existing ghosts; it cannot create a cycle.

### Walk-through (the canonical example)

1. **Original mission.** Lower stage L and upper stage U are recorded. They separate at UT `t_sep`.
   - `R(L)` exists. Its track sections are mostly `Absolute`.
   - `R(U)` exists. The proximity window after separation is `Relative` with `anchorRecordingId = R(L).id`. The rest of `R(U)` (after the bubble exits) is `Absolute`.
   - The stored offset in `R(U)`'s relative section is `U_recorded - L_recorded` per sample — common-mode-clean.

2. **Re-fly L from `t_sep`.** Player spawns L2 (live vessel) and flies a new path.
   - `R(L)` is **suppressed visually** (no ghost rendered). Its trajectory data stays in memory and is used as anchor reference for any recording anchored on it.
   - `R(U)` is rendered as a ghost. For its proximity-window sections: `R(U)_world(t) = R(L)_world(t) + recorded_offset(t)`. Since `R(L)`'s data is unchanged and the offset is common-mode-clean, `R(U)` renders at exactly its original world trajectory — independent of L2's flight.
   - L2 itself is rendered as a normal live KSP vessel. Player input affects L2 only.

3. **The new recording R(L2).** During the re-fly, the recorder captures L2.
   - When L2 is in proximity to `R(U)`'s rendered ghost (the same separation event as the original), the recorder writes `Relative` sections for `R(L2)` with `anchorRecordingId = R(U).id`. The "nearby vessel" is a ghost; the recorder treats it as a valid anchor candidate via its recording id.
   - When L2 has no nearby ghost or live peer, `R(L2)` writes `Absolute` sections (today's behaviour for solo flight).

4. **The chain after re-fly.** `R(L) ← R(U) ← R(L2)`.
   - `R(L2)` rendering calls `R(U)_world(t)` which calls `R(L)_world(t)` which is direct.
   - All positions trace back to recorded data only.

### Why this is accurate

The relative offset between two co-bubble vessels is sub-meter accurate at recording time (common-mode noise cancels in the difference). When playback adds a recorded offset to a recorded anchor world position, the result is the original geometric truth, modulo float-precision in one addition. **For non-loop sections** there is no live vessel in the math, so player behaviour during re-fly cannot perturb ghost positions.

### Vessel symmetry (any vessel can be re-flown)

After a separation, the player may choose **any** of the resulting vessels to re-fly. Whichever they pick is render-suppressed (no ghost mesh drawn for it); the others render as ghosts. The chain math is symmetric under this choice:

- "Suppressed" is a **render-only** mask. The suppressed recording's trajectory data stays fully in memory and continues to participate in chain resolution as an anchor for any recording that points at it.
- Whichever vessel is suppressed, the others walk the chain through the suppressed recording's *recorded* pose at UT — never its live counterpart's live pose. Trajectories are deterministic given `(recordings, suppression mask, UT)`; switching which vessel is the live one swaps the mask but not the math.
- The recorder's per-section anchor structure is asymmetric in practice (e.g. only the focused vessel writes Relative-anchored sections; the BG vessel's recording is mostly Absolute). That asymmetry is fine: re-flying the *other* vessel just means the formerly-BG-now-rendered recording renders directly from its own data, while any Relative sections in the now-suppressed recording continue to chain into the suppressed-but-in-memory data.
- Multi-re-fly composition: re-flying L produces `R(L2)` anchored on `R(U)`; later re-flying U from the same RP produces `R(U2)` anchored on `R(L)`. Both are independent chains rooted in originals; both work because anchors point at recorded data, never at live state.

**Implementation requirement made explicit:** the resolver's lookup scope (§3.3 step 1) must find a recording regardless of its render-suppression state. Suppression filters affect mesh drawing only — they must not filter the recording out of `RecordingTree.Recordings` lookups, the active-provisional overlay, or the `PendingTree` walk. Spelling this out so an implementer doesn't accidentally apply a "skip suppressed" filter inside the resolver.

### Debris and other co-bubble vessels (no special-casing)

The playback chain model is uniform across vessel kinds. There is no separate "debris pipeline" or "third-party vessel pipeline" — every recording's playback path is the same.

Recording production is **not** uniform in the current code: `FlightRecorder` already has REL/ABS section switching, while `BackgroundRecorder` currently emits Absolute track sections (`StartBackgroundTrackSection(..., ReferenceFrame.Absolute, ...)`) and absolute samples. Phase B explicitly adds background REL state and sampling; the bullets below describe the target v11 behaviour, not what `BackgroundRecorder` already does today.

- **Decoupler debris** (radial boosters, jettisoned fairings, etc.) is created at separation time as background-recorded vessels. Each piece becomes its own `Recording` in the same tree as the parent. After Phase B, the background recorder writes mostly Absolute sections with brief Relative-anchored windows during proximity to whichever eligible peer was nearest at section start (often the parent recording, sometimes another sibling debris piece).
- **Pre-existing scene vessels** (a station the player approaches, an idle ship in another orbit) are background-recorded from scene-load. Their recordings are independent of the player's mission tree.
- **Vessels from other missions** (a different `RecordingTree`) follow the same recorder rules after Phase B; v1 skips cross-`CommittedTree` candidates and writes Absolute if no eligible same-scope recording-id anchor exists.

**Mid-re-fly debris caveat (v1).** Because still-being-appended active provisional recordings are not valid anchor targets, fresh debris created during a live re-fly cannot anchor on the live re-fly recording even though that data exists in memory. That debris records Absolute until it has another eligible same-scope anchor, or until a future phase explicitly supports reading active provisionals as anchors.

During re-fly, all of these render via the same chain math:

1. If the section is `Absolute` → render direct from recorded data.
2. If the section is `Relative` → resolver walks the chain to the anchor recording; anchor's pose is read from recorded data regardless of whether the anchor recording is itself suppressed (live re-fly target) or rendered.
3. If the section is `OrbitalCheckpoint` → Kepler propagation.
4. If the section is in a loop-anchored recording → bounces to recorded-shadow / hide per §3.4.

Debris that anchored on the re-flown vessel during the original recording renders at its original position because the chain still resolves through recorded data. Pieces that anchored on each other (sibling debris) chain through whichever sibling is reachable first.

**Cross-tree caveat (v1 limitation).** A debris piece or third-party vessel that would otherwise choose a recording in a *different* `CommittedTree` (e.g. a launch vessel that approached an external space station mid-recording) falls outside §3.3's tree-local + provisional-overlay scope. Normal v11 recorder output must skip that candidate and write Absolute or choose the next eligible same-scope anchor. If corrupt/manual data contains such an anchor anyway, v1 resolves failure to recorded-shadow fallback or section hide with a Warn. Phase 1.5 closes the gap with a global recording-id index in `RecordingStore`. This is the same limitation that affects normal cross-tree dock-anchored sections; it is not specific to debris.

### Non-relative sections

- `Absolute` sections render directly from recorded data. Same as today.
- `OrbitalCheckpoint` sections render via Kepler propagation. Same as today.
- `Relative` sections use the recording-anchor model above.
- **Looped recordings (`Recording.LoopAnchorVesselId != 0`)** continue using the legacy live-anchor mechanism, untouched in v1. This is a per-recording flag, not a per-section flag. v11 non-loop Relative sections must not anchor on looped recordings; the recorder skips loop-anchored candidates and chooses the next valid candidate or writes Absolute. If corrupt/legacy data points a chain at a loop-anchored recording, the resolver returns `false` and the caller uses the recorded-shadow debug fallback or hides the affected Relative section. See §3.4 for the carve-out rationale.

The chain optimization (§9.6 promote-to-absolute) is offline-only and optional.

---

## 3. What changes

### 3.1 Format (`TrackSection.cs` + version bump)

Add **`anchorRecordingId : string`** field to `TrackSection`. For v11 Relative sections this is the anchor identity. `anchorVesselId` remains in the codebase only as a legacy/read-only field while old data paths are being fenced; v11 playback must not read it and normal v11 sidecars should not serialize `anchorPid`. Lifecycle endpoint: keep the field through v11 for legacy fencing/read diagnostics, then remove the field and legacy parser branch in the next intentional format cleanup (`v12`) once v11 fixtures have replaced old saves.

- Format-version bump: append `RecordingAnchorChainFormatVersion = 11` to `RecordingStore.cs:57-63`. Set `CurrentRecordingFormatVersion = 11`. Mirror the existing version-pin discipline: in `TrajectorySidecarBinary.cs`, `RecordingAnchorChainBinaryVersion = RecordingStore.RecordingAnchorChainFormatVersion` (not a literal `11`), so a future bump can't drift the two halves.
- Binary codec bump: extend `IsSupportedBinaryVersion` and `GetBinaryEncoding`. **Also update `TrajectorySidecarBinary.CurrentBinaryVersion` from the current `StructuralEventFlagBinaryVersion` to `RecordingAnchorChainBinaryVersion`, and insert `RecordingAnchorChainBinaryVersion` at the top of the write-version ladder in `Write(...)` before the terrain/boundary/relative-shadow branches.** The write path currently chooses the header at `TrajectorySidecarBinary.cs:148-164`; if that ladder still caps at v10, canonical `.prec` files never satisfy `binaryVersion >= RecordingAnchorChainBinaryVersion` and `anchorRecordingId` silently does not serialize. Read/write `anchorRecordingId` gated on `binaryVersion >= RecordingAnchorChainBinaryVersion`. Older recordings may load as legacy data, but there is no v7-v10 correctness requirement and no v11 migration step.
- ConfigNode codec: track sections are serialized via **`TrajectoryTextSidecarCodec.SerializeTrackSections` / `DeserializeTrackSections`** as `TRACK_SECTION` nodes inside the trajectory sidecar — NOT in `RecordingTreeRecordCodec` (that's for `.sfs` recording metadata, which doesn't hold per-section blocks). Add a `anchorRecordingId` value key to the `TRACK_SECTION` ConfigNode. The round-trip test targets the trajectory sidecar ConfigNode, not the `.sfs` recording entry.
- Format-break rule: a v11 `Relative` section with `anchorRecordingId == null` is invalid for chain playback. It logs a clear recorder/format error and either uses `absoluteFrames` as a debug fallback or hides the ghost for that Relative section's UT range. It never falls back to live PID placement.
- Non-playback consumer audit: every `TrackSection` consumer that currently treats `anchorVesselId` as Relative-section identity must switch to a recording-id-aware key. Known required audit points include `SessionMerger.HealBackgroundActiveUnrecordedGapBoundaries` (`SessionMerger.cs:626`, with the Relative anchor comparison at `:649-650`), `RecordingOptimizer` split/merge decisions, `RecordingStore.TrySyncFlatTrajectoryFromTrackSections`, map/KSC trajectory readers, and rendering anchor candidate/builders. Add an `AnchorIdentityKey(section)` helper or equivalent so v11 sections with different `anchorRecordingId` values are never collapsed merely because `anchorVesselId == 0`. The map/KSC readers are not a passive audit note; they get an explicit Phase D fence (§5 D.7) because they currently call live-PID anchor lookup.

### 3.2 Recorder (`FlightRecorder.cs` + `BackgroundRecorder.cs`)

The recorder's anchor-selection at section start opens with a unified rule: every "anchor candidate" must be a recording id, never a live PID.

- When the focused vessel enters proximity with another entity (live vessel OR ghost), look up the entity's **active or background recording id** and store that as `anchorRecordingId`.
  - Live vessel → its current foreground / background recording (the recorder already tracks this in `RecordingStore`).
  - Ghost → its source recording (the engine knows this; new active-ghost candidate accessor needed: `IPlaybackTrajectory.RecordingId` is already available, but the recorder needs the active ghost index/recording-id stream without synthesizing PIDs).
- Candidate eligibility for v1:
  - Candidate recording must be resolvable in the same composite scope the resolver will search: focus tree, finalized/safe provisional overlay, or matching `PendingTree`.
  - A still-being-appended active provisional recording is not a valid anchor target in v1. It can be the focus recording being captured, but normal v11 candidate selection must not anchor another recording on it until it is finalized/merged.
  - Candidate recording must not have `LoopAnchorVesselId != 0`; loop-anchored recordings are not valid v11 Relative anchors.
  - Cross-`CommittedTree` candidates are invalid for v1. Skip them and select the next nearest eligible candidate, or write Absolute if none exists.
- **Selection rule when both live peer and ghost peer are in proximity simultaneously:** nearest-peer-wins (Euclidean distance). Live and ghost candidates are treated uniformly — same proximity threshold, same selection criterion. The recorder does not prefer one source over the other.
- The recorder does **not** dual-write `anchorVesselId` into normal v11 trajectory sidecars. If the peer is a live vessel, the recorder may log the peer PID at section-open time for diagnostics, but serialized correctness is `anchorRecordingId` only. Keeping a stale PID beside the recording id creates an attractive wrong fallback; the v11 contract removes that ambiguity.

Required recorder-state replacement:

- `FlightRecorder` replaces the v11 REL lifecycle around `currentAnchorPid` with `currentAnchorRecordingId` plus a per-sample anchor-pose evaluator. The old PID field may remain only for legacy fencing/log comparison until v12; v11 mode entry, mode exit, false-alarm resume, section identity, boundary seeding, and sample conversion must not depend on it.
- `SeedRelativeBoundaryPoint(Vessel v, uint anchorPid, ...)` becomes recording-id/pose based: it receives the selected `RecordingAnchorCandidate` or resolves `(anchorRecordingId, boundaryUT)` to an `AnchorPose`, then computes the boundary offset from that pose. It must not call `FindVesselByPid(anchorPid)`.
- `ApplyRelativeOffset(ref TrajectoryPoint point, Vessel v)` no longer calls `FindVesselByPid(currentAnchorPid)`. For each sample, it resolves the selected anchor pose at the sample UT, computes the anchor-local position/rotation payload, writes `currentTrackSection.anchorRecordingId`, and falls back to Absolute if the selected recording id becomes unresolvable.
- `RestoreTrackSectionAfterFalseAlarm(...)` gets the same conversion explicitly. Current code restores `resumeSection.anchorVesselId`, validates it via `FindVesselByPid(resumeAnchor)`, sets `currentAnchorPid`, and writes `currentTrackSection.anchorVesselId`. v11 resume instead restores `resumeSection.anchorRecordingId`, validates it by resolving `(anchorRecordingId, resumeUT)` through `RelativeAnchorResolver`, sets `currentAnchorRecordingId`, and writes `currentTrackSection.anchorRecordingId`. If the recording-id anchor cannot resolve, resume reopens as Absolute or hides/fences per the normal v11 unresolved-section rule; it never revives PID semantics.
- Live peer capture is still allowed to read the peer's live transform **at recording time** to create the sample offset, because that is capture math, not ghost placement math. The serialized anchor identity remains the peer's recording id. Ghost peer capture never reads the ghost transform for math; it uses `RelativeAnchorResolver` at the physics UT.
- `BackgroundRecorder` gets the same state machine explicitly. Current code is Absolute-only, so Phase B adds `BackgroundVesselState` fields equivalent to `currentAnchorRecordingId`, current reference frame, and selected-anchor diagnostics; closes/opens sections when REL/ABS state or anchor recording id changes; seeds boundary points with the same recording-id/pose helper; and converts background samples with the same relative-offset math. If no eligible same-scope anchor exists, background stays Absolute.

### 3.3 Playback (`GhostPlaybackEngine.cs` + `IGhostPositioner.cs` + `ParsekFlight.cs` + new `RelativeAnchorResolver`)

Replace the live-vessel-pid playback contract, not only the lookup inside `ParsekFlight`. Current code is PID-shaped at the engine boundary: `GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT(...)` returns `uint anchorVesselId`, `TryPositionRelativeSectionAtPlaybackUT(...)` passes that PID through, and `IGhostPositioner.InterpolateAndPositionRelative(...)` accepts `uint anchorVesselId`. v11 changes that API surface so Relative playback carries the `TrackSection`/section index and `anchorRecordingId` through to the host positioner.

Target API shape:

```csharp
internal readonly struct RelativeSectionPlaybackTarget
{
    public readonly int SectionIndex;
    public readonly TrackSection Section;
    public readonly string AnchorRecordingId;
}

// GhostPlaybackEngine.cs
internal static bool TryGetRelativeSectionAtUT(
    IPlaybackTrajectory traj,
    double playbackUT,
    out RelativeSectionPlaybackTarget target);

// IGhostPositioner.cs
void InterpolateAndPositionRelative(
    int index,
    IPlaybackTrajectory traj,
    GhostPlaybackState state,
    double ut,
    bool suppressFx,
    RelativeSectionPlaybackTarget target);
```

`TryGetRelativeSectionAtUT` returns `false` for non-Relative sections. For v11 Relative sections it requires a non-empty `target.AnchorRecordingId`; missing ids are a recorder/format error and are handled by the caller's recorded-shadow fallback or section hide. It must not synthesize an anchor from `traj.LoopAnchorVesselId`; loop playback remains on the separate `PositionLoop` path. Legacy pid-only Relative sections are fenced by Phase E and never routed into live-PID placement.

`ParsekFlight` then uses a recording-id resolver that returns a **full pose** (position + rotation), not just a position:

```csharp
// New: Source/Parsek/Rendering/RelativeAnchorResolver.cs
internal readonly struct AnchorPose
{
    public readonly Vector3d WorldPos;
    public readonly Quaternion WorldRotation;
    public readonly int ResolvedSectionIndex;   // diagnostic
}

internal readonly struct RelativeAnchorResolverContext
{
    public readonly string FocusRecordingId;
    public readonly string FocusTreeId;
    public readonly ReFlySessionMarker ActiveReFlyMarker;
}

internal static bool TryResolveAnchorPose(
    RelativeAnchorResolverContext context,
    string anchorRecordingId, double ut,
    HashSet<string> visited,
    out AnchorPose pose);
```

The resolver:
1. Looks up the anchor recording using the explicit `RelativeAnchorResolverContext`. **Search scope (v1):** a composite lookup, in this order:
   1. The focus ghost's own `RecordingTree`.
   2. A finalized/safe provisional overlay from the live re-fly session (if any) — `ParsekScenario.ActiveReFlySessionMarker.ActiveReFlyRecordingId` and the matching `Recording` from `RecordingStore.AddProvisional` state — only when the recording is safe to read as immutable for the current lookup. A still-being-appended active provisional recording is not a v1 anchor target.
   3. `RecordingStore.PendingTree` if it shares the marker's `TreeId`.
   4. Cross-`CommittedTree` anchors are invalid v11 data for v1. The recorder must not create them; if the resolver sees one anyway, it logs `anchor-cross-tree-out-of-scope` and returns `false` for recorded-shadow debug fallback or section hide.

   **Suppressed-but-discoverable invariant.** The lookup MUST find an anchor recording regardless of whether the recording is currently render-suppressed (its ghost mesh is hidden because the player is re-flying that vessel). Render suppression is a per-frame mesh-drawing decision; it must not be a filter applied inside the resolver's recording lookup. The chain math reads the anchor's *recorded* pose, which exists in memory regardless of suppression state. Concretely: the resolver iterates `RecordingTree.Recordings` plus the active-provisional / `PendingTree` overlays without consulting any session-suppression set. This is the property that makes "any vessel can be re-flown" work — see §2 "Vessel symmetry" for the full statement.

   **Active-provisional read contract (v1):** the active provisional overlay is available so the focus recording and current re-fly session can be resolved consistently, but v1 must not depend on reading a still-being-appended provisional recording as an anchor target for another recording. In the canonical case, the live provisional `R(L2)` records sections anchored on existing `R(U)`; `R(L2)` is not queried as another ghost's anchor until it is finalized/merged. Multi-re-fly composition that would require querying a still-recording provisional as an anchor is future work and needs a separate concurrency review.
2. Calls into the standard playback pipeline to evaluate the anchor recording's pose at UT (position **and** rotation).
3. Recurses if the anchor recording's section at UT is itself `Relative`. Both position and rotation are composed at each link:
   - `chainPos = parentAnchorPose.WorldPos + parentAnchorPose.WorldRotation * recordedAnchorLocalOffset`
   - `chainRot = parentAnchorPose.WorldRotation * recordedAnchorLocalRotation`
4. Cycle guard via `visited` set (defensive — DAG is acyclic by construction, but a corrupted recording could create one). **A cycle-guard hit is a real corruption signal and emits its own `WarnRateLimited` log distinct from the cross-tree-out-of-scope Warn**, keyed on `(victimRecordingId, "anchor-cycle-detected")`. Don't let the defensive path swallow corruption silently.
5. If the anchor recording has no playable data at the requested UT (for example it ended after an explosion at `t_dead < t`), the resolver returns `false` with a distinct `anchor-out-of-recorded-range` Warn key. The caller then uses recorded-shadow fallback if available, otherwise hides the affected Relative section. Do not extrapolate from the last anchor pose.
6. Terminating cases: anchor section at UT is `Absolute` (recursion ends, pose comes from recorded sample) or `OrbitalCheckpoint` (recursion ends, pose comes from Kepler propagation). **Loop-anchored recordings (`anchorRecording.LoopAnchorVesselId != 0`) terminate with `false`** because loop anchoring is per-recording, not per-section. Normal v11 recorder output must never create a Relative chain pointing at such a recording.
7. OrbitalCheckpoint pose must include rotation, not only position. Use the same rotation source as the existing checkpoint playback path (`InterpolateAndPositionCheckpointSection` / `TrajectoryMath` orbit interpolation). If the orbit path cannot produce a finite rotation, the resolver returns `false`; a position-only orbital terminator is not acceptable for Relative rotation composition.

**Floating-origin invariance.** `WorldPos` is in KSP world coords (post-Krakensbane). The composition `parentAnchorPose.WorldPos + parentAnchorPose.WorldRotation * recordedAnchorLocalOffset` has both operands in the same world frame at call time, so floating-origin shifts cancel cleanly. The resolver must not cache intermediate world poses across frames; floating-origin invariance depends on completing the chain in one call or using a per-frame cache that is invalidated at the frame boundary. No special LateUpdate re-pos needed beyond what the existing pipeline already does at the Relative-section call site.

**Time-warp / cache safety.** Resolver inputs include `ut` because anchor pose changes with time. **Any future memoization (the deferred Phase F fallback, see §5) MUST key on `(recordingId, sectionIndex, ut_quantized_to_frame)` — never on `(recordingId, sectionIndex)` alone.** A UT-less cache silently returns stale poses under warp where `currentUT` jumps between frames. Documented here so a future implementer doesn't introduce that bug.

### 3.4 What gets deleted

After Phase D acceptance:

- `ParsekFlight.TryGetReFlyTreeAnchorOffset` and the per-tree Re-Fly anchor lock (`ghost_world += live_active(now) - recorded_active(t)`).
- The `no-live-anchor` fallback branch in `TryUseAbsoluteShadowForActiveReFlyRelativeSection`.
- The `stale-anchor` fallback branch (PR #680).
- The "RELATIVE absolute shadow forward bridge" mechanism (`TryFindAbsoluteShadowForwardBridgeFrame`).
- The `FindVesselByPid(section.anchorVesselId)` call in Relative section playback, plus the upstream engine/interface contract that currently passes `uint anchorVesselId` through `GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT`, `TryPositionRelativeSectionAtPlaybackUT`, and `IGhostPositioner.InterpolateAndPositionRelative`.
- Per-frame Re-Fly anchor activation gate (`RefreshReFlyAnchorActivationGate`, `externalActivationDeferred`).
- **Live-vessel reads inside the rendering pipeline outside ParsekFlight's Relative-section path.** `Source/Parsek/Rendering/AnchorPropagator.cs` runs through `ProductionAnchorWorldFrameResolver` (and similar) which currently reads `anchorVesselId` via live vessel lookup for relative-boundary anchors when shadow data is unavailable. These resolvers must be updated to call into the chain `RelativeAnchorResolver` before falling back to anything else. Phase C extends the chain resolver across `AnchorPropagator` call sites; Phase D removes the live-vessel-lookup branches inside those resolvers.

**Out of scope for v1 (loops):** `Recording.LoopAnchorVesselId` is a separate orthogonal mechanism on the whole `Recording`, not on individual sections. A looped recording is anchored on a designated live vessel by recorder design (e.g., a station the looped craft formation-flies around). Switching loop anchoring to recording-id is a separate decision the user must make explicitly. v1 leaves the loop-anchor mechanism untouched for loop playback itself, but forbids using looped recordings as `anchorRecordingId` roots for non-loop v11 Relative sections. Document the carve-out at the `Recording.LoopAnchorVesselId` declaration site (`Recording.cs:57`).

**Resolver behaviour when an anchor chain reaches a looped recording.** If `R(L)` is loop-anchored on a live station and `R(U)` Relatively anchors on `R(L)` for a docking sequence inside the loop, walking `R(L)`'s pose at UT would otherwise route through the legacy live-loop-anchor path — which is exactly the live-vessel coupling the plan removes for non-loop sections. In v11 this anchor is illegal: the recorder must skip `R(L)` as an anchor candidate and write Absolute or pick another valid candidate. If corrupt/legacy data still points at `R(L)`, the resolver returns `false` with a `WarnRateLimited` log keyed on `(victimRecordingId, anchorRecordingId, "loop-anchor-out-of-scope")`; the caller uses recorded-shadow debug fallback if present, otherwise hides the affected Relative section. Closing the gap (loops folded into recording-id anchoring) is a future plan, not Phase 1.5.

The "no live vessels in ghost placement math" invariant is therefore qualified: it applies to **non-loop track-section playback**. Loop anchoring is a known exception, intentional, scoped to a future plan.

### 3.5 What stays

- v7 `absoluteFrames` shadow data on disk. Kept as a defensive/debug backstop only — callers that own the victim `TrackSection` may fall back to it after `RelativeAnchorResolver` returns `false` for missing, cross-tree, loop-rooted, or corrupt anchors. The resolver itself does not read `absoluteFrames`; its API receives only a target recording id and UT. These cases are invalid for normal v11 recorder output; shadow is not a migration mechanism and not required for v11 correctness.
- The recorder's REL/ABS proximity heuristic. Untouched. If you don't like the messy short alternating sections at separation events, that's a separate plan.
- The mathematical model in `docs/parsek-ghost-trajectory-rendering-design.md` (smoothing, anchor correction, common-mode cancellation, DAG propagation). The doc's intent is preserved; the implementation gap that's closed is "anchor on recording, not on live vessel".

---

## 4. The example, restated as math

```
R(L)  is Absolute everywhere.
R(U)  is Relative-anchored-on-R(L) for t in [t_sep, t_bubble_exit], Absolute for t > t_bubble_exit.
R(L2) is Relative-anchored-on-R(U) for the proximity window during re-fly, Absolute when alone.

Render at UT t:

  L is suppressed (R(L) not drawn), its data still in memory.

  U_world(t) = R(L)_world(t) + R(U).relative_offset(t)        for t in proximity window
             = R(U).absolute(t)                                for t outside the window

  L2_world(t) = R(U)_world(t) + R(L2).relative_offset(t)      during re-fly proximity
              = R(L2).absolute(t)                              for t outside the window

  Live L2 player vessel renders via normal KSP, not via this pipeline.
```

No live vessel pose appears anywhere on the right side.

---

## 5. Phased implementation

Each phase is independently reviewable and reverts cleanly. Phase A/B are schema and recording-pipeline staging phases, not a promise that newly produced v11 Relative recordings have correct visual playback before Phase C. Each phase ends with `dotnet test` green plus the in-game smoke test described.

### Phase A — Format break (v11 schema, no playback switch yet)

- Add `anchorRecordingId : string` (nullable) to `TrackSection.cs` alongside the existing `anchorVesselId` field.
- Bump `RecordingAnchorChainFormatVersion = 11` in `RecordingStore.cs`. Bump `CurrentRecordingFormatVersion = 11`.
- Audit `CurrentRecordingFormatVersion` consumers before committing the bump: run a source search for `CurrentRecordingFormatVersion` and inspect comparison/gating sites. Any feature-specific gate should use the named feature version (`RecordingAnchorChainFormatVersion`, `StructuralEventFlagFormatVersion`, etc.), not silently inherit the v11 bump. Record the audit result in the Phase A review notes.
- In `TrajectorySidecarBinary.cs`: pin `RecordingAnchorChainBinaryVersion = RecordingStore.RecordingAnchorChainFormatVersion` (NOT a literal `11` — mirrors the existing peer-pinning discipline at `TrajectorySidecarBinary.cs:48-61`). Set `CurrentBinaryVersion = RecordingAnchorChainBinaryVersion`. Extend `IsSupportedBinaryVersion` and `GetBinaryEncoding`. Insert the new version at the top of the `Write(...)` version-selection ladder so v11 recordings actually write a v11 binary header. Gate read/write of `anchorRecordingId` on `binaryVersion >= RecordingAnchorChainBinaryVersion`. Older versions can deserialize as legacy data, but no migration or playback-correctness guarantee is required.
- ConfigNode codec lands in **`TrajectoryTextSidecarCodec.SerializeTrackSections` / `DeserializeTrackSections`** — the per-section `TRACK_SECTION` node block in the trajectory sidecar. **NOT** `RecordingTreeRecordCodec` (that's `.sfs` recording metadata and does not hold per-section blocks; mirroring there would pass metadata round-trip while silently failing the actual trajectory sidecar round-trip). Add an `anchorRecordingId` value key to the `TRACK_SECTION` node, written when non-null.
- Recorder writes `anchorRecordingId = null` only during this transitional phase; these Phase A recordings are not correctness fixtures and should be discarded/re-recorded after Phase B.
- Ship the unused `RelativeAnchorResolver` read API surface now: `AnchorPose`, `RelativeAnchorResolverContext`, and `TryResolveAnchorPose(...)` compile and have unit-testable initial pure helpers. Phase B depends on this API to resolve ghost peer poses at physics UT, so it must exist before Phase B starts. Include a real single-link `Relative -> Absolute` resolver unit test in Phase A even though production playback does not call it yet; the API must resolve an Absolute terminator, not just compile as a skeleton.
- Before Phase B changes recorder behaviour, rerun or synthesize the `logs/2026-05-01_1731_watch-separation-wobble/` scenario on the Phase A baseline and confirm the old wobble/repro signal still exists. If the original scenario no longer reproduces, pick and document a replacement repro before touching the recorder; otherwise Phase C has no comparable evidence loop.
- Playback unchanged.

**Acceptance:** new v11 recordings round-trip `anchorRecordingId` through binary and text sidecars, `TrajectorySidecarBinary.CurrentBinaryVersion` and the write-version ladder emit a v11 header for v11 recordings, `CurrentRecordingFormatVersion` consumer grep/audit is recorded with no accidental v11 feature gates, the resolver API passes focused unit tests for empty/missing-anchor inputs plus a single-link Absolute-terminator chain, and the watch-separation-wobble repro is either confirmed on the Phase A baseline or replaced with a documented equivalent. Phase A does not assert legacy cutoff render behaviour because playback is unchanged until Phase C/E.

**Phase A implementation note (2026-05-01):** v11 schema and binary/text sidecar round-trips are in place, `RelativeAnchorResolver` resolves a single-link Relative-to-Absolute chain through recorded data only, and focused plus full xUnit passed. The `CurrentRecordingFormatVersion` audit found default stamping/probe checks plus one risky feature gate in `FlightRecorder.MaybeUpgradeActiveRecordingRelativeContract`; that gate now uses a named target and defers pid-only Relative sections at v10 instead of silently stamping them as v11. `SessionMerger.HealBackgroundActiveUnrecordedGapBoundaries` now compares Relative anchors with `AnchorIdentityKey(section)` so v11 sections with different `anchorRecordingId` values do not collapse because both legacy PIDs are zero. Remaining `anchorVesselId` playback/map/KSC live lookups are intentionally left for Phase C/D/E fences. Runtime baseline was not executed by the agent; use the in-game checklist in `docs/dev/todo-and-known-bugs.md` before Phase B/C visual acceptance.

### Phase B — Recorder writes the new field

- Recorder's anchor-selection logic at `FlightRecorder.StartNewTrackSection` and `BackgroundRecorder` equivalents now resolves the proximity peer's recording id and stores it as `anchorRecordingId`. It does not dual-write `anchorVesselId` for normal v11 sidecars.

**B.1 New engine accessor (the candidate stream that doesn't exist yet).** The plan was previously hand-wavy about "iterate `GhostPlaybackEngine.ActiveGhostStates`" — that name does not exist. The actual engine state is `internal readonly Dictionary<int, GhostPlaybackState> ghostStates` (`GhostPlaybackEngine.cs:25`), which is keyed by index, and `GhostPlaybackState` does **not** carry the source recording id directly — the recording id lives on the `IPlaybackTrajectory` instance the engine receives as a parameter to `UpdatePlayback`. The closest existing accessor, `internal IEnumerable<(int index, Vector3 position)> GetActiveGhostPositions()` (`:3067`), returns positions but no recording id.

Phase B introduces a richer accessor that joins recording-id back to active ghost index, plus a spawn-frame flag to make "skip the spawn frame" testable. It does **not** make the rendered transform authoritative for recorder math; B.5 resolves ghost position from recorded data at the physics UT.

```csharp
// Source/Parsek/GhostPlaybackEngine.cs — new accessor
internal readonly struct GhostAnchorCandidate
{
    public readonly int Index;
    public readonly string RecordingId;
    public readonly bool PositionedThisFrame; // false on spawn frame
}

internal IEnumerable<GhostAnchorCandidate> GetActiveAnchorCandidates(
    IReadOnlyList<IPlaybackTrajectory> trajectories);

// Source/Parsek/GhostPlaybackState.cs — new field
internal bool positionedThisFrame; // set by engine after positioner runs;
                                    // cleared at top of UpdatePlayback per-frame
```

The accessor iterates `ghostStates`, joins each entry's `index` into the caller-supplied `trajectories` list to read `IPlaybackTrajectory.RecordingId`, and yields the candidate. Empty / null recording id entries are filtered. The recorder later asks `RelativeAnchorResolver` for the candidate's pose at the current physics UT.

`positionedThisFrame` is set inside `RenderInRangeGhost` after the positioner's transform write, and cleared at the head of `UpdatePlayback`. Phase B's spawn-frame skip rule reads this flag explicitly — no fragile heuristic ("transform at origin", "first frame of state lifetime") needed.

**B.2 Candidate list construction.** The new model treats every candidate as a recorder-side `RecordingAnchorCandidate` with recording identity and pose:

```csharp
internal enum AnchorCandidateSource { Live, Ghost }

internal readonly struct RecordingAnchorCandidate
{
    public readonly string RecordingId;
    public readonly Vector3d WorldPos;
    public readonly Quaternion WorldRotation;
    public readonly AnchorCandidateSource Source;
    public readonly uint DiagnosticPid; // logs only; never serialized/fallback
    public readonly int GhostIndex;      // -1 for live candidates
}
```

- Live `Vessel` peer → its current foreground/background recording id: foreground is `FlightRecorder.ActiveTree.ActiveRecordingId` for the active vessel, background is `RecordingTree.BackgroundMap[peerPid]` / `BackgroundVesselState.recordingId` for background vessels. Add a small helper such as `TryResolveRecordingIdForLivePeer(uint peerPid, out string recordingId)` that uses these actual sources and rejects missing/self ids. The peer's live world pose is used only for capture-time offset math. Live PID may appear in logs only; it is not a serialized fallback key.
- Ghost peer → identity from `engine.GetActiveAnchorCandidates(trajectories)` (B.1), pose from `RelativeAnchorResolver` at the current physics UT. Skip any candidate where `PositionedThisFrame == false` or resolver pose fails.
- **Skip any candidate that cannot resolve to a recording id.** Pid-only synthetic vessels are ignored as anchor candidates.

**B.3 Replace `AnchorDetector.FindNearestAnchor` with a recording-id detector.** The current detector signature is:

```csharp
internal static (uint anchorPid, double distance) FindNearestAnchor(
    uint focusedVesselPid,
    Vector3d focusedPosition,
    List<(uint pid, Vector3d position)> vesselInfos,
    HashSet<uint> treeVesselPids)
```

That shape is legacy-PID specific and has no `VesselInfo` abstraction to extend. Phase B replaces/adds a recording-id variant, for example:

```csharp
internal static (RecordingAnchorCandidate candidate, double distance, bool found)
    FindNearestRecordingAnchor(
        string focusedRecordingId,
        uint focusedVesselPid,
        Vector3d focusedPosition,
        IReadOnlyList<RecordingAnchorCandidate> candidates);
```

`FlightRecorder.cs:4452` and the new `BackgroundRecorder` REL path call this recording-id detector. `BuildVesselInfoList` may continue filtering ghost-map vessels for the legacy live-vessel list; ghost candidates enter only through `GetActiveAnchorCandidates`. The detector performs a single linear scan over resolved candidates, explicitly skips any candidate whose `RecordingId == focusedRecordingId` or `DiagnosticPid == focusedVesselPid`, and then applies the §3.2 tie-break. Candidate construction should also avoid adding the focused vessel/recording, but the detector keeps the hard guard so a malformed candidate list cannot create distance-0 self-relative sections or resolver cycles.

**B.4 Tie-break for nearest-peer-wins:** primary key is Euclidean distance in world meters; secondary is `recordingId` lexicographic order (stable, source-neutral); tertiary is source kind + engine index for deterministic tests. Distance dominates in practice; no tie-break depends on a live PID.

**B.5 Frame-ordering reality.** Earlier drafts of this plan asserted: *"That transform is set by `GhostPlaybackEngine.UpdatePlayback` during `OnFixedUpdate` before the recorder's per-physics-frame sample callback runs."* **That is wrong.** `engine.UpdatePlayback` is invoked from `ParsekFlight.Update()` at `ParsekFlight.cs:14113` (Unity Update — once per render frame). The recorder's sample callback is a Harmony postfix on `VesselPrecalculate.CalculatePhysicsStats` (`Patches/PhysicsFramePatch.cs:103`) — runs at physics-tick frequency, fires 0/1/N times per render frame. Under high warp the recorder fires N physics frames per Update; ghost transforms are last positioned by the *previous* render frame.

The recorder cannot rely on "engine positioned this physics frame" being true. Resolution: **Phase B's recorder does not rely on stale ghost transforms at all.** Instead, when the recorder needs an anchor pose for a ghost peer at the current physics-frame UT `t`, it builds a `RelativeAnchorResolverContext` for the focused recording/session and calls `RelativeAnchorResolver.TryResolveAnchorPose(context, ghostRecordingId, t, ...)` directly (the same resolver Phase C ships for playback). The resolver returns the ghost's pose at the current physics UT from recorded data — no dependence on the rendered transform, no Update-vs-physics ordering question. Ghost transforms become irrelevant to anchor selection; the recorder asks the resolver "where is recording X at UT t" and uses that answer.

This collapses three earlier sub-points (audit ordering, skip spawn frame, document contract) into one clean rule: **the recorder uses `RelativeAnchorResolver` as its sole source of truth for ghost peer poses.** `positionedThisFrame` and `GetActiveAnchorCandidates` are still used to decide *which ghosts are active candidates*, but the *position* used for proximity comparison comes from the resolver, not the transform.

**Phase ordering note:** B.5 means Phase B depends on a small surface of `RelativeAnchorResolver` (the read API, not the deletions). To keep phasing clean, ship the resolver's read API as part of Phase A's format work (it can sit unused on the shelf), then Phase B turns it on for the recorder, then Phase C turns it on for playback, then Phase D deletes the old live-vessel paths.

**Acceptance:** new recordings have populated `anchorRecordingId` on every `Relative` section where a recording-id-resolvable peer was in proximity. Phase B-produced Relative recordings are data-generation fixtures only until Phase C playback lands; do not use them as visual correctness evidence yet. `dotnet test` covers (a) recorder unit test verifying field is set on live + ghost peers, (b) loop-anchored and cross-committed-tree candidates are skipped, (c) the spawn-frame skip via `positionedThisFrame`, (d) the deterministic tie-break on equidistant input, (e) self-candidate rejection by both recording id and vessel pid, (f) false-alarm resume restores `anchorRecordingId` without calling `FindVesselByPid`, and (g) an in-game test that runs at 4× warp and asserts the recorder's anchor pose for a ghost peer is exact (not stale-by-one-render-frame).

### Phase C — Playback uses the chain

- New `RelativeAnchorResolver` (file path above). Pure-function tests for: single-link resolution, two-link chain, cycle guard, missing-anchor returns `false` with a Warn reason, `Absolute` anchor section (terminating recursion). Shadow fallback is tested at the caller layer, not in `RelativeAnchorResolverTests`, because only the caller owns the victim `TrackSection.absoluteFrames`.
- `GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT` / `TryPositionRelativeSectionAtPlaybackUT` and `IGhostPositioner.InterpolateAndPositionRelative` are reshaped per §3.3 so the engine passes `RelativeSectionPlaybackTarget` instead of `uint anchorVesselId`. This is part of Phase C, before deleting old paths in Phase D, because otherwise v11 sections have no recording id at the positioner boundary.
- `ParsekFlight.cs` Relative-section playback builds a `RelativeAnchorResolverContext` from the focused trajectory/recording tree and calls `RelativeAnchorResolver.TryResolveAnchorPose(context, target.AnchorRecordingId, ut, ...)`. On success, it composes the anchor-local offset and local rotation against the returned full pose. On miss, it logs a strict resolver failure and either uses `target.Section.absoluteFrames` as a debug fallback or marks the current Relative section unresolved and hides the ghost for that section's UT range. **It never falls back to today's pid-based live-vessel path.**
- No user-facing setting preserves the old live-PID render path. A temporary developer trace may compute the old position for comparison logs, but rendering must use the chain resolver or the recorded-shadow debug fallback.

**Acceptance:** the re-captured/generated v11 fixture based on `logs/2026-05-01_1731_watch-separation-wobble/` plays back without the 1.2 s decouple stall. `[PlaybackTrace]` lines through UT 41.74-46.74 show smooth `dM` / `dSpd`, no `RELATIVE absolute shadow forward bridge` WARNs. In-game test exercises a separation event in regular Watch and reports zero-jitter ghost playback.

### Phase D — Delete the live-anchor band-aids

Each deletion is its own commit with an observability check.

#### D.0: Product behaviour confirmation gate

Before any Phase D deletion lands, the user must confirm the product behaviour change in writing after seeing the downstream consequences from §1:

- Ghosts in an active divergent Re-Fly tree stay at original recorded coordinates rather than following the live vessel.
- Watch camera policy is chosen explicitly: either keep Watch anchored to the live re-fly target during active Re-Fly, or allow Watch to follow recorded ghosts away from the player.
- Distance / soft-cap / zone behaviour is accepted as recorded-coordinate based, even when that means ghosts despawn because they are far from the live re-fly vessel.

This is a required milestone, not a comment. D.1-D.7 do not start without it.

#### D.1: Delete `TryGetReFlyTreeAnchorOffset` and the per-tree Re-Fly anchor lock

`TryGetReFlyTreeAnchorOffset` and `GhostPosEntry.reFlyTreeOffset` are spread through inline positioner code, method definitions, XML comments, and LateUpdate re-position branches. The exact hit count has already drifted between reviews, so the implementation rule is discovery-gated:

- Before D.1 edits, run a fresh source search for `TryGetReFlyTreeAnchorOffset`, `TryGetReFlyTreeAnchorOffsetUncached`, and `reFlyTreeOffset` in `Source/Parsek/ParsekFlight.cs`.
- Classify every hit as method definition/memo, executable position shift, `GhostPosEntry` field/write, LateUpdate read, or comment.
- After D.1, CI/source-review gate: `TryGetReFlyTreeAnchorOffset`, `TryGetReFlyTreeAnchorOffsetUncached`, and `reFlyTreeOffset` must have **zero** hits in `Source/Parsek/ParsekFlight.cs`. If a comment is worth preserving, rewrite it without those identifiers so the grep gate stays mechanical.

Known executable shift sites from the current spot-check include:

| Call site | Positioner path | Disposition |
|---|---|---|
| `:15902` | `InterpolateAndPosition` (Relative + Absolute point interp) | Covered by chain resolver (Relative path); no shift needed for Absolute (ghosts at recorded coords by design). |
| `:16818` | `PositionGhostAt` single-point hold | Delete — Absolute-frame single point at recorded coords. |
| `:17258` | `PositionFromOrbit` orbit-driven | Delete — orbit-driven ghosts use Kepler propagation in inertial coords. |
| `:17415` | `PositionGhostAtSurface` landed/splashed | Delete — surface ghosts at recorded lat/lon/alt with terrain correction. |
| `:17705` | `RefreshReFlyAnchorActivationGate` | Delete (covered by D.5 below). |
| `:18381` | Distance resolver `ResolvePlaybackDistanceFromReferencePosition` | Delete — distance resolution against recorded coords matches ghost render coords. |
| `:18562` | `InterpolateAndPositionCheckpointSection` (orbital checkpoint) | Delete — checkpoint sections render via Kepler. |
| `:20646` | `PositionLoopGhost` loop playback Absolute | Delete — loops anchor on `LoopAnchorVesselId` separately (orthogonal mechanism, §3.4 carve-out). |
| `:20670`, `:20754` | `PositionLoopGhost` loop playback Relative | Loop playback itself stays for the `LoopAnchorVesselId` live-vessel behaviour, untouched in v1. These calls move behind the explicitly named loop-live helper split from D.4. Non-loop Relative chains never enter this path; if a chain reaches a looped recording, the resolver returns false per §3.4. |

Known method/memo hits include `:17491`, `:17519`, `:17535`, `:17545`, plus related XML comments near `:17673` / `:17683`. Delete the helper family and memo state; do not leave comments that preserve the old symbol names.

Known `LateUpdate` `e.reFlyTreeOffset` reads include `ParsekFlight.cs:1173`, `:1183`, `:1202`, `:1293`, `:1364`, `:1412`, `:1450`, and `:1479`. Known non-LateUpdate writes/reads include `:15946`, `:15964`, `:16831`, `:17299`, `:17431`, `:18614`, `:20684`, and `:20777`. Treat this as a current snapshot, not an exhaustive future guarantee; the zero-hit grep gate is authoritative.

The deletion is therefore: replace every executable use with either (a) a chain-resolver call when the section is Relative non-loop, or (b) nothing (for Absolute / OrbitalCheckpoint / Surface — those are at recorded coords by design and the Re-Fly anchor lock was forcibly translating them to follow the live player, which §1's behaviour change explicitly removes). Memo struct, helper signatures, field writes, and all LateUpdate reads go.

#### D.2: Delete `no-live-anchor` and `stale-anchor` branches

In `ParsekFlight.TryUseAbsoluteShadowForActiveReFlyRelativeSection` and `RelativeAnchorResolution`. They are unreachable once Phase C is the primary path because Phase C never tries a live anchor first — it goes straight to the chain resolver.

#### D.3: Delete the forward-bridge fallback

`TryFindAbsoluteShadowForwardBridgeFrame` and its callers. Sparse Relative sections no longer need cross-section interpolation; they resolve via the chain anchor at the section's own UT range.

#### D.4: Remove the live-PID contract from the non-loop Relative-section playback path

The non-loop Relative section playback path no longer carries or consumes `uint anchorVesselId`. This is a structural API deletion, not a line-window grep only:

- `GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT` is replaced by `TryGetRelativeSectionAtUT(... out RelativeSectionPlaybackTarget target)`.
- `GhostPlaybackEngine.TryPositionRelativeSectionAtPlaybackUT` passes the target, not a PID.
- `IGhostPositioner.InterpolateAndPositionRelative` takes `RelativeSectionPlaybackTarget`, not `uint anchorVesselId`.
- `ParsekFlight` splits shared helpers that currently serve both non-loop Relative playback and loop-relative playback. Non-loop helpers use `target.AnchorRecordingId` / `target.Section`, never `section.anchorVesselId`. Loop helpers keep an explicit live-loop contract rooted in `Recording.LoopAnchorVesselId`, not a generic Relative-section PID path.

Required helper split:

| Current shared helper | v11 split |
|---|---|
| `InterpolateAndPositionRelative` | `InterpolateAndPositionRecordedRelative(..., RelativeSectionPlaybackTarget target)` for non-loop sections; loop playback calls a separately named loop helper. |
| `TryResolvePlaybackWorldPosition` | Recorded-coordinate branch calls `RelativeAnchorResolver`; loop branch is explicit and only entered from `PositionLoop`. |
| `TryResolveRelativeAnchorPose` | Rename/split to `TryResolveRecordedRelativeAnchorPose` (recording id) and `TryResolveLoopLiveAnchorPose` (loop anchor vessel id). |
| `PositionGhostRelativeAt` | Split into recorded-relative and loop-relative variants so D.4 can grep the recorded helper without breaking loops. |
| `LateUpdate` Relative re-position branch | Recorded-relative entries store enough resolved recorded pose data to reapply floating-origin correction without live lookup; loop entries remain tagged as loop mode. |

The old pid field becomes legacy/read-only fencing data, not a v11 render input. Other call sites of `FindVesselByPid` outside ghost placement and non-loop Relative playback may stay if they are unrelated to this plan (spawn bookkeeping, game-state recovery, diagnostics, etc.). Loop live-anchor lookup may also stay, but only in helpers named/tested as loop playback; no shared non-loop helper may depend on it.

#### D.5: Delete `RefreshReFlyAnchorActivationGate` and `externalActivationDeferred`

Plus the spawn-frame-defer plumbing in `GhostPlaybackEngine.ActivateGhostVisualsIfNeeded`. Ghosts activate the moment the resolver returns — which is always immediately, since the chain is in-memory recorded data.

#### D.6: Fence `AnchorPropagator` / `ProductionAnchorWorldFrameResolver`

`RenderSessionState` runs `AnchorPropagator` through `ProductionAnchorWorldFrameResolver`, so the live-vessel cleanup cannot stop at ParsekFlight's Relative-section path. Enumerated live reads in `Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs`:

| Call site | Current live read | Disposition |
|---|---|---|
| `:70-72` in `TryResolveRelativeBoundaryWorldPos` | `relSection.anchorVesselId` then `TryFindVesselByPid(anchorPid)` | Replace with `RelativeAnchorResolver` using a `RelativeAnchorResolverContext` built from `(rec, sectionIndex, boundaryUT)`. If resolver fails, use recorded shadow or return false; no live lookup. |
| `:118-120` in `TryResolveLoopAnchorWorldPos` | `rec.LoopAnchorVesselId` then `TryFindVesselByPid(...)` | Stays only for loop playback itself. It is never used to resolve a non-loop Relative chain; chain resolver returns false before entering loop playback. |
| `:306` `TryFindVesselByPid` helper | Shared helper for the above | After D.6, no non-loop render path calls it. Add tests/counters so any future non-loop call fails visibly. |

Acceptance for D.6: `AnchorPropagator` relative-boundary tests cover v11 `anchorRecordingId`, loop-rooted invalid anchor, cross-tree invalid anchor, and missing anchor. The only passing live-vessel resolver test left is an explicit loop-playback test.

#### D.7: Fence Map / KSC Relative playback readers

Map and KSC ghost views are playback surfaces, not just metadata readers. They currently have their own live-PID Relative anchor lookups and must be fenced with the same rule: v11 non-loop Relative sections resolve through `anchorRecordingId` or are hidden/skipped; they never use `anchorVesselId` to find a live vessel.

Known required edits:

| Call site | Current live-PID shape | Disposition |
|---|---|---|
| `GhostMapPresence.ResolveAnchorInScene` / `TryResolveStateVectorMapPointPure` (`GhostMapPresence.cs:4231` / `:4251` entry points, actual anchor PID read at `:4290-4302`) | `Func<uint, bool>` anchor resolvability and `currentSection.Value.anchorVesselId` | Replace with a recording-id-aware map pose resolver. For v11 Relative sections, resolve the section's world pose through `RelativeAnchorResolver` and emit/update the map state vector from recorded coordinates. If resolver fails, return a specific unresolved-relative reason and skip map presence for that section. |
| `GhostMapPresence` anchor PID snapshots / update helpers (`GhostMapPresence.cs:1099-1131`, `:4969`, `:4993`, `:5036`, `:5058`, `:5112-5117`, `:5370`, `:5631`) | `section.anchorVesselId` / `AnchorPid` carried into update paths, then `FlightRecorder.FindVesselByPid(...)` | Audit all of these in D.7. Replace non-loop Relative map/KSC placement with the same recorded-pose resolver or skip/hide. Preserve live-PID lookup only for explicitly loop-anchored playback if that path is actually map-supported and named as loop-only. |
| `ParsekKSC.TryResolveKscRelativePose` / `TryLookupKscAnchorFrame` (`ParsekKSC.cs:1487`, `:1582`) | `uint anchorVesselId` → `KscAnchorLookup` → `FlightRecorder.FindVesselByPid` | Change KSC Relative pose resolution to accept `RelativeSectionPlaybackTarget` / `anchorRecordingId` and call `RelativeAnchorResolver`. On failure, skip the KSC ghost for that section or use recorded shadow if the KSC caller owns it; no live lookup. |

Acceptance for D.7: Map/KSC unit tests cover v11 Relative sections with a valid `anchorRecordingId`, missing anchor id, loop-rooted invalid anchor, and cross-tree invalid anchor. A structural grep/counter covers all `GhostMapPresence` and `ParsekKSC` Relative helpers so `section.anchorVesselId` / `AnchorPid` cannot become a live lookup key for non-loop Relative playback outside the flight scene. Add an in-game regression test that loads a v11 chain, enters map view and KSC/tracking surfaces, and asserts ghost markers/orbits resolve through `anchorRecordingId` or skip unresolved sections with the expected Warn.

**Acceptance:** all `[PlaybackTrace]` lines during re-fly show ghost positions independent of live vessel motion. Player can fly L2 in a wildly divergent path; ghosts of U, debris, and other tree members continue at original recorded positions. Each band-aid being deleted has a v11 fixture re-captured or synthesized from the original repro log and re-exercised through the chain resolver, producing the same observable output (ghost at recorded position, no jitter):
- D.1 / D.2 / D.3: `logs/2026-05-01_1731_watch-separation-wobble/` — booster watch through Probe decouple, no shadow-bridge WARN, no `[PlaybackTrace]` jumps at section boundaries.
- D.4: structural CI grep gates the named non-loop recorded-relative helpers (`GhostPlaybackEngine.TryGetRelativeSectionAtUT`, `GhostPlaybackEngine.TryPositionRelativeSectionAtPlaybackUT`, `IGhostPositioner.InterpolateAndPositionRelative`, `ParsekFlight.InterpolateAndPositionRecordedRelative`, `TryResolveRecordedPlaybackWorldPosition`, `TryResolveRecordedRelativeAnchorPose`, `PositionGhostRecordedRelativeAt`, and the recorded-relative `LateUpdate` branch): neither `anchorVesselId` nor `FindVesselByPid` may appear in those helper bodies except in comments explicitly naming deleted legacy behaviour. A separate allowlist covers loop-only helpers such as `TryResolveLoopLiveAnchorPose` / `PositionLoopGhost`. Add a `[Conditional("DEBUG")]` counter on non-loop Relative render entries that increments if a live PID lookup is attempted; v11 watch/re-fly tests assert the counter stays 0. Old Relative sections are legacy-incompatible and do not use that path either.
- D.5: the spawn-frame-stall reported by the original `[PlaybackTrace]` analysis (Probe rebuild + bridge insertion + ghost spawn collision) shows ghosts active at first frame with no defer.
- D.6: `ProductionAnchorWorldFrameResolver.TryResolveRelativeBoundaryWorldPos` resolves through `anchorRecordingId` or recorded shadow only; `TryResolveLoopAnchorWorldPos` is exercised only by loop-playback tests.
- D.7: Map and KSC Relative playback either resolve through `anchorRecordingId` or skip/hide with a Warn; no map/KSC non-loop Relative helper calls `FindVesselByPid(section.anchorVesselId)` or carries `AnchorPid` into a live lookup.

### Phase E — Legacy cutoff and fixture reset

There is **no v7-v10 migration phase**. The project is still in private development, and the user explicitly does not care about preserving legacy recording correctness. The correct response to old pid-anchored data is to re-record it under v11, not to infer recording IDs from stale PIDs.

Load-time behaviour:

- If a recording format is older than `RecordingAnchorChainFormatVersion`, mark it `legacy-incompatible` for anchor-chain correctness and log once per recording/session.
- Do not walk `RecordingTree.BackgroundMap` to backfill `anchorRecordingId`.
- Do not persist inferred `anchorRecordingId` values into old recordings.
- Do not call `FindVesselByPid(section.anchorVesselId)` from any ghost placement path as a legacy escape hatch.
- If an old Relative section has usable `absoluteFrames`, the renderer may use them as a **debug fallback** with a Warn. If not, mark that Relative section unresolved and hide the ghost for that section's UT range rather than placing it from a live vessel.
- Rebuild synthetic fixtures and manual repro saves under v11 before using them as acceptance evidence. The old logs remain useful for expected behaviour and trace comparison, but not as loadable compatibility fixtures.

**Acceptance:** old v7-v10 Relative recordings do not get migrated and do not invoke live PID placement. They either warn and use recorded-shadow data, or warn and hide the unresolved Relative section. All correctness tests use newly generated v11 fixtures with real `anchorRecordingId` chains.

### Phase F — Promote-to-absolute optimization (DEFERRED — do not build)

Originally drafted as an offline pass that walks `Relative` sections and converts their offsets into self-contained `Absolute` data, breaking the anchor chain dependency.

**Decision (2026-05-01 review):** **deferred indefinitely.** No current evidence shows anchor-chain walking as a playback hotspot, and converting sections to Absolute would add irreversible data complexity before there is a measured need. The optimization solves an unproven problem.

If a future scenario produces measurably hot chain walks (perf trace shows it), the cheap remediation is **per-frame memoization in the resolver** (cache the `(recordingId, sectionIndex, ut_quantized_to_frame)` → pose lookup, invalidate at frame boundary) — not a one-way data conversion. Build memoization before considering Phase F. The chain stays at runtime; the absolute-shadow `absoluteFrames` block remains a debug backstop only.

---

## 6. Format-break discipline

- v11 is a **private-development breaking format**. v7-v10 recordings are disposable and are not correctness targets.
- New recordings (v11+) write `anchorRecordingId` directly. For v11 Relative sections, `anchorRecordingId` is required.
- `anchorVesselId` is not a v11 playback contract and is not dual-written into normal v11 trajectory sidecars. Existing code fields remain through v11 only to read/fence old data, then are removed in v12; new playback code MUST NOT use `anchorVesselId` to place ghosts.
- The v7 `absoluteFrames` shadow stays only as a defensive/debug fallback when anchor resolution fails (e.g., recording-id points to a missing recording). It is not a migration mechanism.
- Missing `anchorRecordingId` in a v11 Relative section is treated as a recorder bug: log it, use recorded-shadow fallback if available, otherwise hide the ghost for that section's UT range. Never recover by live vessel lookup.

---

## 7. Test plan

### Unit tests
- `RelativeAnchorResolverTests.cs`:
  - Single-link resolve (`Relative` anchored on `Absolute`).
  - Two-link chain (`Relative` → `Relative` → `Absolute`).
  - Two-link chain rotation accuracy: assert composed rotation matches expected (separate from position; position-only test will pass even if rotation is wrong, so rotation needs an explicit pin).
  - Resolver context scoping: same `anchorRecordingId` searched from focus tree, finalized/safe provisional overlay, and matching pending tree; still-being-appended active provisional and cross-`CommittedTree` recording ids are rejected as anchor targets.
  - Cycle guard (synthetic corrupt input where A → B → A).
  - Missing anchor recording → resolver returns false with Warn reason; caller-level tests cover recorded-shadow fallback / unresolved-section hide.
  - Anchor recording has no data at requested UT → resolver returns false with `anchor-out-of-recorded-range` and does not extrapolate from the last pose.
  - Anchor recording has `LoopAnchorVesselId != 0` → resolver returns false with `loop-anchor-out-of-scope`; v11 recorder tests prove this cannot be emitted by normal candidate selection.
  - Anchor section is `OrbitalCheckpoint` (Kepler resolves; recursion terminates) with both position and rotation asserted against the existing checkpoint playback rotation source.
  - Still-being-appended active provisional recording is not accepted as an anchor target in v1; resolver returns false or the recorder skips the candidate until finalization/merge.
  - Cross-`CommittedTree` anchor explicitly NOT in scope: anchor recording lives in a different committed tree → resolver returns false, caller uses recorded-shadow debug fallback or section hide (negative test pinning the v1 limitation).
- `RelativeSectionPlaybackContractTests.cs`:
  - `GhostPlaybackEngine.TryGetRelativeSectionAtUT` returns a `RelativeSectionPlaybackTarget` with section index, section, and `anchorRecordingId` for v11 Relative sections.
  - The engine/positioner contract no longer exposes `uint anchorVesselId` on non-loop Relative playback. Reflection or compile-time test helpers pin `IGhostPositioner.InterpolateAndPositionRelative(..., RelativeSectionPlaybackTarget target)`.
  - Missing `anchorRecordingId` in a v11 Relative section does not synthesize `traj.LoopAnchorVesselId`; it returns unresolved/fenced and increments the debug counter if any live-PID lookup is attempted.
- `RelativeSectionFallbackTests.cs`:
  - Caller-owned `section.absoluteFrames` is used as debug fallback only after `RelativeAnchorResolver` returns false.
  - Missing/cross-tree/loop-rooted/corrupt anchors without usable shadow hide the affected Relative section for its UT range.
  - `RelativeAnchorResolver` is not passed or expected to read `absoluteFrames`.
- `TrackSectionAnchorIdentityTests.cs`:
  - `SessionMerger.HealBackgroundActiveUnrecordedGapBoundaries` treats two v11 Relative sections with different `anchorRecordingId` values as different anchors even when `anchorVesselId == 0`.
  - `RecordingOptimizer` split/merge helpers and flat trajectory sync helpers use `anchorRecordingId` before legacy `anchorVesselId` for v11 Relative identity.
- `RecorderAnchorSelectionTests.cs`:
  - Live peer in proximity → `anchorRecordingId` populated to peer's foreground/background recording id.
  - Ghost peer in proximity → `anchorRecordingId` populated to ghost's source recording id, sourced via `GhostPlaybackEngine.GetActiveAnchorCandidates(trajectories)` (the new accessor introduced in §5 Phase B.1; recording id joined from `IPlaybackTrajectory.RecordingId` per index, not via pid synthesis).
  - Loop-anchored peer in proximity → skipped as invalid v11 Relative anchor; next eligible peer wins or section records Absolute.
  - Cross-`CommittedTree` peer in proximity → skipped as invalid v1 candidate; next eligible peer wins or section records Absolute.
  - Ghost peer on its spawn frame (`positionedThisFrame == false`) → recorder skips it as anchor candidate.
  - Ghost peer pose under high warp: recorder uses `RelativeAnchorResolver` to fetch the pose at the current physics-frame UT, NOT the rendered transform. Test runs at 4× warp and asserts the recorder's stored offset uses the resolver's pose, not a stale Update-frame transform.
  - Both live and ghost peer in proximity, ghost is closer → ghost wins (nearest-peer tie-break).
  - Both peers exactly equidistant on synthetic input → deterministic tie-break by recordingId lexicographic order.
  - No peer → `anchorRecordingId == null`, section reverts to `Absolute`.
- `BackgroundRecorderAnchorSelectionTests.cs`:
  - Background recorder starts Absolute in current conditions, enters Relative only when a same-scope recording-id candidate is eligible, and writes `anchorRecordingId`.
  - Background section changes close/open when REL/ABS state changes or when the selected `anchorRecordingId` changes.
  - Background boundary seed and sample conversion use the recording-id/pose helper, not `FindVesselByPid(anchorPid)`.
  - Background loop-rooted, cross-tree, unresolved, and no-peer candidates write Absolute rather than pid-only Relative sections.
- `LegacyCutoffTests.cs`:
  - v10 Relative recording with `anchorVesselId` and missing `anchorRecordingId` → no migration, no `FindVesselByPid`, legacy-incompatible Warn fires.
  - v10 Relative recording with `absoluteFrames` → recorded-shadow debug fallback is allowed, live PID playback is not.
  - v10 Relative recording without `absoluteFrames` → affected Relative section is hidden with Warn, not placed from a live vessel.
- `FormatRoundtripTests.cs`:
  - v11 recording with `anchorRecordingId` round-trips through binary `.prec` codec.
  - `TrajectorySidecarBinary.CurrentBinaryVersion == RecordingAnchorChainBinaryVersion`, and the write-version ladder emits a v11 header for a v11 recording so the `anchorRecordingId` branch is actually exercised.
  - v11 recording with `anchorRecordingId` round-trips through `TrajectoryTextSidecarCodec` by constructing/reading real `TRACK_SECTION` ConfigNode entries in the trajectory sidecar — NOT `.sfs` metadata and NOT `RecordingTreeRecordCodec`. Assert v11 normal sidecars do not emit `anchorPid`.
  - v11 Relative section with missing `anchorRecordingId` is rejected/fenced by the resolver path with a clear log.
- `PlaybackDistanceResolverTests.cs`:
  - Distance/zone decisions after D.1 use the same recorded-coordinate pose that render uses; no stale Re-Fly live offset is applied to decide in-range vs out-of-range.
  - Divergent Re-Fly soft-cap behaviour is pinned: a ghost far from the live re-fly vessel is evaluated against recorded-coordinate ghost position, and any hide/despawn decision is logged as recorded-coordinate policy rather than live-anchor fallback.
- `LoopRelativeHelperSplitTests.cs`:
  - Non-loop recorded-relative helpers resolve only by `anchorRecordingId` and trip the debug counter on any live-PID lookup attempt.
  - Loop playback still resolves through explicitly named loop-live helpers rooted in `LoopAnchorVesselId`; these helpers are the only allowlisted live-PID path.
  - A loop-anchored recording used as a non-loop `anchorRecordingId` target returns false before entering the loop-live helper.
- `MapKscRelativeAnchorTests.cs`:
  - `GhostMapPresence` resolves v11 Relative sections through `anchorRecordingId` and emits recorded-coordinate state vectors; missing/cross-tree/loop-rooted anchors skip with an explicit unresolved reason.
  - `ParsekKSC` resolves v11 Relative pose through `RelativeAnchorResolver`; missing/cross-tree/loop-rooted anchors skip or caller-shadow fallback without calling `FindVesselByPid`.
  - Structural grep/counter asserts no non-loop Map/KSC Relative helper uses `section.anchorVesselId` as a live lookup key.

### Regression pins
- Re-capture or synthesize a v11 fixture from the watch session at `logs/2026-05-01_1731_watch-separation-wobble/` before using it for acceptance. The old log is the reference trace only; it is not the loadable fixture. The v11 fixture with the booster's REL [41.78, 42.24] one-frame section anchored on the Probe must render through the new resolver without the forward-bridge WARN, and `[PlaybackTrace]` `dM` / `dSpd` must stay within 5% of the recorded velocity through the section.
- Re-capture or synthesize a v11 fixture for PR #688's optimizer-split chain successor case from `Parsek-fix-refly-anchor-chain-walk`; the chain-walk itself is now the primary path, not a fallback.

### In-game tests (`InGameTests/RuntimeTests.cs`)
- `GhostAnchorChain_ReFly_GhostsIndependentOfLiveVessel`: re-fly a multi-stage launch, divergence test — fly the live vessel in an arbitrary path, verify ghosts still play their original trajectory. Sample ghost world positions at multiple UTs and assert they match recorded positions within 1 m.
- `GhostAnchorChain_Watch_NoLiveVesselNeeded`: watch a recording with no Re-Fly active. Verify Relative sections render correctly without ever calling `FindVesselByPid`.
- `GhostAnchorChain_ReFly_WatchCameraPolicy`: during active divergent Re-Fly, verify the Phase D.0 camera policy is honoured (live re-fly target remains camera anchor, or Watch intentionally follows recorded ghosts away from the player).
- `GhostAnchorChain_MapKsc_RecordedAnchor`: load a v11 chain, enter map/tracking and KSC ghost views, and assert markers/orbits resolve through `anchorRecordingId` or skip unresolved Relative sections with the expected Warn.

---

## 8. Acceptance criteria

The plan is complete when:

1. Phase D.0 written product-behaviour confirmation is captured, and all Phase D deletions/fences land cleanly (per-tree Re-Fly anchor lock, no-live-anchor, stale-anchor, forward-bridge, gate / activation defer, engine/positioner Relative PID contract, `AnchorPropagator` / `ProductionAnchorWorldFrameResolver`, and Map/KSC non-loop live-vessel reads).
2. Phase E cutoff is enforced: old pid-anchored recordings do not migrate and do not call live PID ghost placement. All correctness fixtures are newly generated v11 recordings with `anchorRecordingId`.
3. **Quantitative jitter check.** Re-captured/generated v11 fixture based on `logs/2026-05-01_1731_watch-separation-wobble/`: replay the booster recording through UT 41.74-46.74. `[PlaybackTrace]` per-frame `dSpd` must stay within **±5% of the recorded velocity at each sampled UT** (not "smooth" hand-wave), AND `dM` between consecutive trace frames must not exceed `(recorded_speed × dt × 1.05)` for any frame. Section-boundary frames (`sectionCrossed` flagged) get a +0.1 m absolute slack to absorb single-frame numeric noise. The 269 m / 1.2-second spawn-frame stall reported in the original analysis goes away entirely — no per-frame `dSpd` exceeds the recorded velocity by more than 5%.
4. The user-cited intent ("render the ghosts relative to the initial recording trajectory and not to the real vessel") is satisfied: a re-fly with an extreme divergent player path shows ghosts at original recorded positions, untouched. **Quantitative check:** measure the world-space distance between a recorded-coordinate sampled position and the rendered ghost transform at the same UT; assert delta < 0.1 m across the whole re-fly window.
5. **Per-deleted-band-aid regression pin.** Each Phase D deletion has a v11 fixture re-captured or synthesized from the **original repro log** that motivated the band-aid. The old log is reference evidence only; the runnable fixture must be v11 with real `anchorRecordingId`. Each fixture is re-exercised through the chain resolver, producing the same observable output (ghost at recorded position, no jitter) — see §5 Phase D acceptance for the per-deletion log mapping.
6. `dotnet test` green; no `[ERROR]` lines in `KSP.log` during the regression scenarios.

**Loop scope qualification.** Criteria 3-5 apply to non-loop track-section playback. Looped recordings (`Recording.LoopAnchorVesselId != 0`) continue using the legacy live-anchor mechanism; they are explicitly out of scope per §1 / §3.4 and are not subject to the "ghost at recorded position" invariant.

---

## 9. Decisions made (was: open questions)

Resolved during the 2026-05-01 plan review (initial draft → Opus review → user-supplied confirmations + extra review items). Recording the rationale here so the implementing agent doesn't have to re-litigate these.

1. **Anchor selection when live peer and ghost peer are both in proximity:** nearest-peer-wins. Live and ghost candidates are treated uniformly — same proximity threshold, same selection criterion. **Tie-break:** distance first (Euclidean meters), then `recordingId` lexicographic (stable, source-neutral), then source kind + engine index for deterministic tests. **Skip rule:** any candidate that cannot resolve to a recording id is ignored entirely. (See §3.2.)

2. **Tree scope:** tree-local anchor resolution for v1, **with a composite-lookup overlay** that includes finalized/safe provisional state and `RecordingStore.PendingTree`. A still-being-appended active provisional recording can be the focus recording being captured, but it is not a valid anchor target until finalization/merge. Cross-`CommittedTree` anchors are illegal v11 recorder output for v1; if corrupt/manual data contains one, the resolver warns and uses recorded-shadow debug fallback or section hide. If a real docking-cross-tree bug surfaces, raise Phase 1.5 with a global recording-id index in `RecordingStore`.

3. **Promote-to-absolute (Phase F):** deferred indefinitely. There is no measured chain-walk hotspot today. If a future perf trace shows hot chain walks, the cheap remediation is per-frame resolver memoization, **not** Phase F. (See §5 Phase F entry.)

4. **`anchorVesselId` field lifecycle:** not part of the v11 contract. It remains through v11 only as a legacy/read-only field while old data paths are fenced, then is removed in the next intentional format cleanup (`v12`). Normal v11 sidecars do not dual-write `anchorPid`, and playback MUST NOT use `anchorVesselId` to place ghosts. Diagnostics should log live PIDs at section-open time instead of preserving them as a fallback key. (See §3.2 and §6.)

5. **Re-Fly UX behaviour change:** explicitly accepted (§1 callout). After Phase D, ghosts in an active Re-Fly tree no longer follow the player's live vessel — they play back at original recorded coordinates. Confirmed by user as the explicit intent (Appendix verbatim quotes).

---

## 10. Risk surface

- **Recorder change has the largest blast radius.** Phase B touches the hot foreground recording loop, adds a new engine accessor (`GetActiveAnchorCandidates`) and a new `GhostPlaybackState.positionedThisFrame` field, routes ghost peer poses through the resolver, and adds a matching REL/ABS state machine to the currently Absolute-only `BackgroundRecorder`. The safety strategy is strict logging, comparison traces, and small commits — not a live-PID render fallback.
- **Playback contract change crosses module boundaries.** The `GhostPlaybackEngine` → `IGhostPositioner` boundary is currently `uint anchorVesselId` shaped. Phase C must change the contract before Phase D deletes old lookup branches, or v11 `anchorRecordingId` will not reach the host positioner. Pin this with contract tests rather than relying on implementation review.
- **Map/KSC are playback surfaces.** Flight-scene rendering is not the only live-PID consumer. `GhostMapPresence` and `ParsekKSC` currently have their own Relative anchor lookups; Phase D.7 treats them as required fences so map/tracking/KSC views cannot preserve the inaccurate live-PID placement path after flight rendering is fixed.
- **Loop playback shares helper names today.** Some current `ParsekFlight` Relative helpers are used by both non-loop playback and loop playback. The plan intentionally splits those helpers instead of deleting every PID read by name; otherwise the implementation would either break loop playback or leave a disguised live-PID branch in a shared non-loop helper.
- **Format bump cost.** v11 is a breaking private-development format. The cost is re-recording fixtures, not migration. This is intentional because the current live-PID path is the source of the bad Re-Fly/separation positions.
- **Legacy recordings.** They are disposable. Pre-v11 Relative sections must not preserve the legacy `FindVesselByPid` ghost-placement path. They can use recorded-shadow debug fallback if available; otherwise they warn and hide the affected Relative section. Any fixture that matters gets regenerated under v11.
- **Crash recovery during a v11 write.** The `.prec` binary sidecar uses `FileIOUtils.SafeWriteBytes` (atomic tmp + rename — same pattern as the existing `.sfs` write path). Confirm at code review that the v11 write path goes through the same atomic pattern. A crash mid-write may leave a pre-v11 file behind; that file is legacy-incompatible and should be re-recorded if it matters.
- **Save-during-active-Re-Fly with mid-chain provisional.** If the player saves while a re-fly session is mid-flight and the active provisional `R(L2)` has already written sections anchored on `R(U)`, the saved marker carries `ActiveReFlyRecordingId = R(L2).id`. On load, the §3.3 composite overlay may find `R(L2)` as the focus/provisional recording and then resolve its anchor `R(U)` from the stable focus tree. It must not let another recording anchor on the still-being-appended `R(L2)` until finalization/merge. **Sanity check:** if the saved marker's `ActiveReFlyRecordingId` does not resolve in the composite recording lookup (e.g., crash between provisional creation and marker update), log a Warn and fall back to recorded shadow or hide the affected Relative section. Pin this with a load-time integrity test.
- **Cross-tree v1 limitation.** Cross-`CommittedTree` anchors are illegal v11 recorder output for v1. If a hand-authored/corrupt recording points at a different committed tree, playback uses recorded shadow or hides the affected Relative section with one Warn per session. Phase 1.5 closes the real feature gap with a global recording-id index in `RecordingStore`; until then the recorder skips cross-tree candidates. If a real docking-cross-tree bug surfaces in playtest, escalate to Phase 1.5.
- **Time warp.** Resolver inputs include `ut` and the documented memoization key invariants (§3.3 "Time-warp / cache safety") prevent stale-cache bugs. No further mitigation needed beyond the documented contract.
- **Phase D is enumerative deletion, not single-point swap.** The pre-existing Re-Fly tree anchor lock (`TryGetReFlyTreeAnchorOffset`) is not behind any abstraction layer — the offset is computed by a free-standing helper and applied inline across multiple positioner, distance, loop, checkpoint, and `LateUpdate` paths in `ParsekFlight.cs`. The exact count is intentionally not treated as stable; D.1 requires fresh source discovery and then a zero-hit grep gate for `TryGetReFlyTreeAnchorOffset`, `TryGetReFlyTreeAnchorOffsetUncached`, and `reFlyTreeOffset`. `AnchorPropagator` / `ProductionAnchorWorldFrameResolver` has a similar inline-call shape (live-vessel reads at `:70-72`, `:119-120`, `:306` per the spot-check in earlier review notes). **There is no single integration point** where flipping one assignment would replace the live-anchor system with the chain resolver. Phase D handles this by **discovery-gated enumeration** (the representative per-site disposition table in §5 D.1 plus source grep gates) rather than by introducing an `IAnchorResolver` strategy interface first. The trade-off is that Phase D is mechanical surgery across many small edits rather than one strategy swap; reviewers should expect that shape, not a single-point cutover. Tests cover regression for each deletion (§5 D acceptance maps each band-aid to its original repro fixture).

---

## Appendix: where the verbatim user intent came from

User's exact wording, copied from session transcripts:
- 2026-04-30 22:13 (`62cad115` line 1262): "I said to let the **initial recording of the vessel that gets spawned as real** be the anchor for the relative position of the ghost which appears next to the real vessel."
- 2026-05-01 14:48 (line 3662): "ghost relative to old recording (not rendered) ghost in the case of Re-Fly (not ghost relative to real vessel)"
- 2026-05-01 15:14 (line 3835): "render the ghosts relative to the initial recording trajectory and not to the real vessel"

The user's worked example, also from this session: L (lower) and U (upper) recorded together; U's relative trajectory is anchored on L; re-fly L → spawn L2, hide R(L), render R(U) using R(L) as reference, record R(L2) anchored on R(U); after merge, optionally promote R(U) to absolute so R(L2) hangs off it directly.
