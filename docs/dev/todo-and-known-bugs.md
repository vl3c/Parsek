# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18.
- `done/todo-and-known-bugs-v4.md` — the v0.8.3 cycle plus the v0.9.0 rewind / post-v0.8.0 finalization / TS-audit closures (closed bugs #462-#569 and the small remaining closures carried over from v3 during its archival). Archived 2026-04-25.
- `done/todo-and-known-bugs-v5.md` — the v0.9.1 / v0.9.2 cycle: Re-Fly Phase D wrap-up, debris-rendering PR stack through PR 3c and the always-shadow follow-up, Phase 11.5 storage and observability follow-ons, the multi-debris explosion-audio fix, and the carrying-over numbered items #570-#640. Archived 2026-05-10.

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## Done - v0.9.2 Rewound recording's vessel does not spawn when watched to terminal

- ~~After a Rewind-to-Separation onto a recording with a spawnable terminal state (Landed/Splashed/Orbiting), entering Watch and letting the ghost play through to its terminal point left the vessel un-materialized. `ParsekPlaybackPolicy.HandlePlaybackCompleted` reported `needsSpawn=False` because `ShouldBlockSpawnForRewindSuppression` short-circuited on the same-recording `SpawnSuppressedByRewind` marker (`#573 active/source recording protection`). Source: `logs/2026-05-12_2018_kerbalx-no-spawn`, recording `e4c8042527c649648b7f94a5175d312d`. The original #573 fix was scoped to protect against background ghost playback duplicating a vessel the player just stripped on rewind (chain-tip respawn next to the player's freshly-launched new vessel). It was overly broad for the case where the player explicitly Watches the rewound recording to its terminal point.~~

**Fix:** `ParsekScenario.TryClearSpawnSuppressionOnWatchEntry` lifts the same-recording `SpawnSuppressedByRewind` marker at Watch entry from `WatchModeController.EnterWatchMode`. Watching is the player's explicit signal that they want this recording's outcome to materialize, so the spawn-at-recording-end path runs naturally when ghost playback reaches the terminal. Only the same-recording reason is touched; legacy-unscoped markers continue to flow through `ShouldBlockSpawnForRewindSuppression`'s existing normalization path. Background ghosts the player ignores after rewind retain the marker exactly as before.

**Coverage:** `RewindSpawnSuppressionTests` covers the helper directly (same-recording marker cleared with audit log + subsequent `ShouldSpawnAtRecordingEnd` returns `needsSpawn=true`), no-op cases (null/empty/legacy-unscoped markers), and the full mark → watch → spawn sequence.

**Status:** CLOSED 2026-05-12.

---

## Done - v0.9.2 Re-Fly probe ghost duplicated after on-rails transition

- ~~In Watch mode after a probe Re-Fly reached space and vessels packed/on-rails, the probe booster ghost could appear doubled and the Recordings window could show two `Kerbal X Probe` exo/orbiting rows. In the retained repro (`logs/2026-05-11_1919_doubled-probe-ghost`), restore swapped the active recorder to the Re-Fly fork for PID `429255699`, but `RecordingTree.RebuildBackgroundMap()` left another non-active recording with the same PID eligible for background tracking. The background recorder kept flushing the old `51e41e...` recording while the active recorder wrote `rec_78ecd...`; optimization later split the stale old row into its own exo/orbiting segment, so both paths rendered and one duplicate path spawned a terminal orbital vessel.~~

**Fix:** Background-map eligibility now rejects any non-active recording whose `VesselPersistentId` matches the active recording's PID, even when the recording IDs differ. This keeps the active recorder as the only owner of the live vessel after in-place Re-Fly restore and logs `activePidSkips` during rebuild for future diagnosis.

**Coverage:** Added xUnit coverage for an in-place continuation tree containing an old probe recording and a new active fork with the same PID. The test verifies the old same-PID row is excluded from `BackgroundMap`, unrelated background vessels remain eligible, and the skip count is logged.

---

## Done - v0.9.2 probe ghost hidden by suborbital OrbitSegment radius gate

- ~~Probe-stage ghost playback could reject a valid suborbital `OrbitSegment` before resolving playback distance because the old guard treated `|sma| < body.Radius * 0.9` as invalid. The retained Kerbal X Probe repro includes an ascent segment around `sma=512 941 m` on Kerbin: below the 540 km threshold, but still the correct Kepler source for playback at that UT. Once rejected, the distance resolver could fall through to flat point metadata from a RELATIVE section and interpret anchor-local metre offsets as body-fixed lat/lon/alt, producing a bogus far-away distance and zone-hiding/jumping the ghost.~~

**Fix:** Orbit playback now uses a body-radius-independent usability check: orbital elements must be finite and `|sma| >= 1 m`, but suborbital conics are allowed. The flight distance resolver, orbit-tail gate, orbit positioning cache, checkpoint orbit cache, and pending-spawn interpolation share that rule, with degenerate segments falling back to point metadata rather than valid suborbital segments doing so.

**Coverage:** Added xUnit coverage that pins the `sma=512 941 m` suborbital case as usable, keeps zero/non-finite SMA rejected, verifies pending-spawn interpolation prefers the active suborbital orbit segment over points, verifies the orbit-tail gate skips degenerate segments, and preserves point fallback for a degenerate orbit segment.

---

## Done - v0.9.2 Re-Fly probe spawn rejected from frame-mismatch in tail-derived terminal orbit

- ~~A Re-Fly fork ending in a highly-eccentric stable orbit (the `Kerbal X Probe` recording in `logs/2026-05-10_2123` — `tOrbSma=4 547 677, tOrbEcc=0.822, periAlt ≈ 208 km`) was deferred-then-permanently-rejected at spawn time. The `TryDeriveTerminalOrbitSeedFromTrajectoryTail` helper added in the previous Done item ("Re-Fly spawn refused circularized upper stage with stale on-rails OrbitSegment") found the right tail frame but reseeded the orbit from world-absolute Y-up state vectors instead of body-relative Z-up, producing `sma=567 357, periAlt=−438 222 m` (subsurface). Safety gate deferred at currentUT=455.25 because propagated alt was −98 949 m; the rotation-drift gate then forced the retry to fall back to the recording's only stored OrbitSegment — the pre-burn ascent ellipse at `epoch=142.16, sma=512 941, periAlt=−382 km` — and the safety gate rejected `CannotSpawnSafely`. Probe never materialized; the `Kerbal X` upper-stage chained successor spawned because its tail carried an authoritative `OrbitalCheckpoint`.~~ Reproduced by `logs/2026-05-10_2123` recording `rec_f1363fc127ab47a28812ce4be6515453`. Investigation report: `docs/dev/research/probe-tail-orbit-spawn-frame-mismatch.md`.

**Root cause:** `Orbit.UpdateFromStateVectors` (decompiled from `Assembly-CSharp.dll`) requires `pos` to be RELATIVE to the reference body and `vel` to be in `Planetarium.Zup` local axes — both `(input - body.position).xzy` from the world-absolute Y-up vectors KSP exposes through `body.GetWorldSurfacePosition` / `rb_velocityD + Krakensbane.GetFrameVelocity()`. KSP's own canonical wrapper `Orbit.OrbitFromStateVectors` does this correctly. The Parsek tail-derive path was passing both axes through unchanged, producing a structurally-finite but physically-wrong orbit whose `|pos|` was off by `body.position` and whose orientation was rotated by the missing `.xzy`. `sma` is invariant under `.xzy` (axis swap preserves magnitude) but not invariant under the missing `(pos − body.position)` — `body.position` for Kerbin in flight scene with the active vessel parked on the launch pad evaluated to ~310 km of magnitude in the captured run, partially cancelling the 808 km surface offset and leaving the helper computing `sma` from `|pos|≈498 km`. For the upper stage (which had a stored `OrbitalCheckpoint` `OrbitSegment` covering its tail), the picker never reached the broken helper, so the bug only surfaced on recordings that ended in stable orbit without an authoritative segment closing them.

**Fix:** New `Source/Parsek/OrbitReseed.cs` centralizes the `Orbit.UpdateFromStateVectors` frame contract. `FromLatLonAltAndRecordedVelocity` handles body-fixed lat/lon/alt plus Y-up recorder velocity by applying `(pos - body.position).xzy` and `vel.xzy`; `FromWorldPosAndZupVelocity` handles world-absolute position plus already-Zup orbital velocity by applying the position transform only; `FromWorldPosAndRecordedVelocity` covers world-absolute position plus Y-up recorded velocity; and the pure input helpers expose those transforms to xUnit. `VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail` is routed through the shared tail resolver with `TailSeedUse.Spawn`, preserving the 30 s rotation-drift guard for spawn safety.

**Coverage:** Tightened the existing `TerminalOrbitFromTail_DerivesPostBurnCircularOrbit` in-game test (`Source/Parsek/InGameTests/RuntimeTests.cs`) to assert tight `sma` (within 5 km of the analytic 803 587 m), `ecc < 0.005`, and `inclination < 0.5°` — the prior assertions only checked `SpawnNow`, which the buggy frame happened to clear for that geometry. Added new in-game tests for the eccentric probe shape and for GhostMap's historical MapPresence tail seed. xUnit covers the pure `(worldPos - body.position).xzy` / recorded-velocity `.xzy` helpers, the Zup-velocity helper, the stale endpoint-tail predicate, EndpointTail dispatch narrowing, and EndpointTail visible-bounds precedence. Full residual/orbit validation remains KSP-runtime-only because `body.GetWorldSurfacePosition` and body rotation live behind Unity/KSP transforms.

**Sibling audit status after the orbit/ghost correctness pass:**

- ~~**`Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs` predicted-tail reseed**~~ — fixed via `OrbitReseed.FromLatLonAltAndRecordedVelocity`, matching the recorder-velocity frame contract. Retained logs confirmed the failure mode before the fix: `logs/2026-05-10_2123/KSP.log` reported `residualMeters=670062.87`, and `logs/2026-05-10_1713/KSP.log` showed the same ~666-671 km residual class.
- ~~**`Source/Parsek/GhostMapPresence.cs` state-vector create/update paths**~~ — fixed via `ResolveStateVectorWorldPosition` plus `OrbitReseed.FromWorldPosAndRecordedVelocity`; Relative/Absolute/OrbitalCheckpoint world-position resolution remains centralized before the state-vector reseed.
- ~~**`Source/Parsek/FlightRecorder.TryCanonicalizeReFlyRecordingOrbitSegment`**~~ — fixed via `OrbitReseed.FromWorldPosAndZupVelocity` for `Orbit.getOrbitalVelocityAtUT`, with non-finite orbital velocity now declining explicitly instead of falling back to `vessel.obt_velocity` in the wrong frame.
- ~~**`Source/Parsek/VesselSpawner.TryResolveEndpointStateVector` fallback**~~ — fixed via `OrbitReseed.FromLatLonAltAndRecordedVelocity` for recorder endpoint velocities.
- **Still open: `Source/Parsek/VesselSpawner.cs:1001` / spawn-position no-override paths** — caller-supplied velocity frame still depends on the entry point and remains a separate audit item.
- **Still open: `VesselGhoster.TryResolvePropagatedOrbitSeed` freshness policy** — this pass fixes GhostMapPresence map/Tracking Station ProtoVessel and orbit-line behavior; non-map propagated ghost paths should only be changed after a reproducer confirms the same stale endpoint-segment symptom there.

Player-visible breakage from these sites is masked today by other paths winning the orbit-seed picker (the spawn case here was the first one we caught where the broken helper was the *only* path the picker had).

---

## Done - v0.9.2 ghost map orbit line drawn from stale OrbitSegment for orbiting recordings whose post-burn frames superseded it

- ~~For recordings shaped like the Kerbal X bug above (one stored sub-orbital `OrbitSegment` from the pre-burn on-rails coast, plus an `ExoBallistic` Absolute tail frame that defines a post-burn circular orbit), the spawn path now reseeds correctly, but `GhostMapPresence.TryResolveGhostProtoOrbitSeed` still pulled from `RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed`, which returned the stale segment. Result: in the Tracking Station and on the map view, the ghost orbit line for these recordings showed the pre-burn sub-orbital ellipse passing through the planet — even though the spawned real vessel sat on the correct post-burn orbit.~~
- **Investigation 2026-05-10:** confirmed on current `main` with retained evidence. `logs/2026-05-10_2123` recording `rec_f1363fc127ab47a28812ce4be6515453` has stale sidecar orbit segments around `sma=512941`, `ecc=0.574602`, ending at UT `415.022`, followed by later Absolute `ExoBallistic` tail frames ending at UT `453.662`. The save metadata has the correct terminal orbit (`tOrbSma=4547677.2114545386`, `tOrbEcc=0.82238029649173194`, `tOrbEpoch=459.44214255408241`). GhostMap logged the stale segment source before the terminal data became usable, so spawn and ghost-map seed selection could disagree.
- **Code path:** `RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed` accepted the last same-body `OrbitSegment` without checking whether later tail frames superseded it. `GhostMapPresence.TryResolveGhostProtoOrbitSeed` inherited that behavior; `VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail` had already moved ahead by preferring the tail-derived seed first. `VesselGhoster.TryResolvePropagatedOrbitSeed` freshness policy remains a separate non-map follow-up above.

**Fix:** `GhostMapPresence.ResolveMapPresenceGhostSource` can now return an explicit `EndpointTail` source when a visible segment is an endpoint-stale segment: the recording is in its terminal map-presence region, the persisted endpoint phase/body is `OrbitSegment` for the same body, `RecordingEndpointResolver` itself reports `Source="endpoint-segment"`, and a `TailSeedUse.MapPresence` historical body-rotation tail seed is fresher than the latest stored segment. EndpointTail creation/update dispatches through the segment path with `source=EndpointTail`, stores synthetic tail bounds, and `TryGetVisibleOrbitBoundsForGhostVessel` now lets those stored bounds win for EndpointTail ghosts so orbit-line/icon clipping does not fall back to the stale committed segment window. TerminalOrbit-backed recordings are intentionally not promoted to EndpointTail.

**Diagnostics:** GhostMap decision lines now carry `endpointTailSeed=accept|decline`, `tailUT`, `tailSma`, `tailEcc`, latest stored segment end, rotation drift, tail frame source, historical-rotation flag, and endpoint source/phase/body details when EndpointTail is considered but declined.

**Coverage:** `GhostMapEndpointTailTests` covers stale endpoint override, legitimate in-window checkpoint preservation, TerminalOrbit-backed non-promotion, Segment decision logging when EndpointTail declines, and visible-bounds precedence after EndpointTail creation state is recorded. KSP-runtime validation remains in `GhostMapEndpointTail_UsesHistoricalTailSeedAcrossActivationDrift` because reconstructing historical body rotation depends on live KSP body transforms.

---

## Open - stale `RewindReplayTargetSourcePid` cross-contaminates `SpawnSuppressedByRewind` across consecutive rewinds

Audit of `Recording.SpawnSuppressedByRewind` after PR #829 surfaced this. `ParsekScenario.ShouldApplyRewindSpawnSuppression` has a standalone-recording branch (`ParsekScenario.cs:5677`) that returns `same-recording` when `rewoundTreeId == null` and `rec.VesselPersistentId == rewindSourcePid`. KSP reuses persistent IDs after vessel deletion, so a session that first rewinds tree A (which sets `RecordingStore.RewindReplayTargetSourcePid = pidA`) and then rewinds a standalone recording without that field being reset will mark **every committed recording whose PID matches the stale pidA** as `same-recording`. The PID-only path is meant to cover the legitimate "standalone (no tree) rewind by source PID" case but does not currently sanity-check that the source recording matching the rewind context is present.

**Symptom:** after a second consecutive rewind on a standalone recording, an unrelated tree's recordings sharing a recycled PID stop spawning at their terminal. No log line says "wrong recording marked" — the audit trail looks correct in isolation.

**Fix shape:** (1) clear `RewindReplayTargetSourcePid` / `RewindReplayTargetRecordingId` inside `RewindContext.EndRewind` (or wherever the unconsumed-fields drain runs) so a stale value cannot survive into the next `MarkRewoundTreeRecordingsAsGhostOnly` call; (2) tighten `ShouldApplyRewindSpawnSuppression`'s standalone-PID branch to require a real `rewindRecordingId` co-presence — without a real rewind target id, the PID-only path returns false. Add `MarkRewoundTreeRecordingsAsGhostOnly_StandaloneRewindAfterTreeRewind_DoesNotMarkUnrelatedPid` regression coverage. Severity: **medium** — silent cross-contamination, but requires two rewinds in one session to trigger.

---

## Open - `ShouldBlockSpawnForRewindSuppression` mutates inside a predicate

Same PR #829 audit. `GhostPlaybackLogic.cs:4924-4952` is named/typed like a pure read but auto-clears the marker and emits an `[Rewind] Info` log when the reason is `legacy-unscoped` (or null). Callers — `ShouldSpawnAtRecordingEnd`, `ShouldSpawnAtKscEnd`, `ShouldSpawnAtTrackingStationEnd`, `ParsekPlaybackPolicy.ShouldRetainMapPresenceForTerminalRealSpawn` — treat the function as a predicate and call it from per-frame hot paths. A legacy save that survived the load-time normalizer with a stale `legacy-unscoped` marker produces one `[Rewind]` clearance Info log on the first call and then mutates the recording mid-frame from inside what looks like a read. Idempotent in effect, but a real surprise-side-effect and log-noise hazard if the same recording is touched from multiple call sites in a single frame.

**Fix shape:** keep `ShouldBlockSpawnForRewindSuppression` strictly read-only (return false for non-same-recording reasons without clearing). Move the legacy-unscoped auto-clear into a one-shot maintenance pass that runs from `HandleRewindOnLoad` and `OnLoad` next to the existing `RecordingTree.NormalizeLegacyRewindSuppressionMarkers` so it lives alongside the other legacy-shape normalization. Add `ShouldBlockSpawnForRewindSuppression_LegacyMarker_DoesNotMutate` regression coverage that calls the predicate twice and asserts the marker is unchanged after the first call. Severity: **low** — the current implementation is correctness-equivalent on saves that load cleanly, but the architectural surprise survives PR review by being well-documented in comments rather than enforced by the type signature.

---

## Open - post-staging debris forward slide observability extended, fix shape contingent

Watch-mode playback of a parent-anchored debris ghost shows a visible ~2 m forward slide on the first lerp interval after a staging joint-break: "ghost appears in the right position then immediately slides about 2 metres in front." A previous attempt (PR 824 commits `140c1a5` / `1c85380` / `00b0df2`, all reverted in `8f57842` / `e7ccdcd` / `686a0e3`) tried to back-step every recorded sample by `Time.fixedDeltaTime * v_inertial` on the hypothesis that KSP's joint-break callbacks fire post-PhysX with `Planetarium.GetUniversalTime()` still at start-of-tick. The fix didn't kill the slide and was reverted along with all three commits.

This PR ships extended observability on top of the existing `TraceSeparation` window so the next investigation cycle can pick the right hypothesis without rebuilding between repros. New fields:

- `inFixed=` on every trace line — distinguishes FixedUpdate (pre-PhysX) capture sites from post-PhysX callbacks (`OnPartJointBreak`, `OnDecoupleNewVesselComplete`). If `inFixed=T` at a `JointBreak` row, the post-PhysX-callback hypothesis is wrong.
- `PARENT_AT_BREAK predictedSrfStep` and `predictedInertialStep` vs `|observedDelta|` — picks the right velocity frame for any back-step. If `|observedDelta|` matches `predictedSrfStep` (≈ |srfVel|·dt) but `predictedInertialStep` overshoots, the reverted fix was correcting in the wrong frame.
- `CHILD_PART_AT_BREAK childVsParentLLA / alongParentSrfVel` — signed projection of child part transform vs parent's stale-LLA reference along the parent's velocity direction. Positive value (in m) is the on-tick lead of the joint-child seed.
- `PartOriginSeed partVsVesselLLA / |observedDelta| / predictedSrfStep / predictedInertialStep` — same shape on the foreground joint-child seed site that the reverted fix patched.
- `DecoupleSeed` (new row at `OnDecoupleNewVesselDuringSplitCheck`) — observes the `new-vessel-root-part` fallback path's seed-vs-LLA delta and the new-vs-original parent LLA-world delta at the split UT.
- `BuildTP tickSinceBreak / |delta|` and `BG_CreateAbs tickSinceBreak / |delta|` — grep `tickSinceBreak=1.` to pick out the first per-tick sample after the joint break, and read `|delta|` to see whether per-tick samples have a `v·dt` offset (commit 3's hypothesis) or stay near zero (per-tick samples are in-phase, only structural-event sites need correction).
- `PositionDebris lerpAlpha / ghostWorldBefore / worldStep / |worldStep| / predictedWorld / predictedVsActual` — reconstructs InterpolateAndPosition's lerp output, captures the per-frame world jump (the visible slide), and compares the actual ghost world position against a manual bracket-LLA lerp so playback-math bugs can be distinguished from recorder-side LLA errors.
- `FG_ApplyRel` / `BG_ApplyRel` (recording side) — for every Relative-frame sample, logs the focus and anchor world positions, the world delta, the computed anchor-local offset, and a pair of distances: `recordedRelativeDist = |offset|` (what's about to be persisted into `frames[]`) and `recordedAbsoluteDist = |focusWorldPos − anchorWorldPos|` (the ground-truth world-space distance at the instant of capture). The `distMismatch` field flags any difference — these must agree exactly under the v13 local-rotation contract.
- `PositionDebris parentGhostWorld / renderedParentDist / recordedAnchorLocalDist / recordedBodyFixedDist` (playback side) — `renderedParentDist` is the on-screen parent-vs-debris distance (resolved via `GhostPlaybackEngine.TryGetGhostWorldByRecordingId(traj.DebrisParentRecordingId)`, backed by the new `GhostPlaybackState.recordingId` field). `recordedAnchorLocalDist` is the bracketing `frames[]` entry's anchor-local offset magnitude. `recordedBodyFixedDist` is computed independently by finding the parent's bracketing `bodyFixedFrames[]` sample (`RecordingStore.TryFindCommittedRecordingById`) and subtracting body-fixed primary world positions. These three together let a reader see whether playback faithfully reproduces what was recorded, or whether the two recording surfaces disagree internally.

**Next step (investigation):** enable `Settings → Diagnostics → Ghost render tracing`, fly a stage-separation in flight with watch-mode debris visible, then walk the resulting `[Trace-Sep]` log lines through these decision points:
1. At the `JointBreak` row, is `inFixed` `T` or `F`?
2. Does `|observedDelta|` match `predictedSrfStep`, `predictedInertialStep`, or neither?
3. At the `PartOriginSeed` row, what is `|observedDelta|` for the joint-child seed?
4. At consecutive `BuildTP` rows with `tickSinceBreak=0.something` then `tickSinceBreak=1.something`, does `|delta|` jump or stay flat?
5. At the first `PositionDebris` row (`first=True`), what is `|worldStep|`, and is `|predDelta|` ≈ 0 (math matches) or non-trivial (math diverges)?
6. At `BG_ApplyRel` / `FG_ApplyRel` rows during the window, is `distMismatch` ≈ 0 (recorder is self-consistent) or non-zero (rotation path adds scaling)?
7. At the first `PositionDebris` row, compare `renderedParentDist` to `recordedAnchorLocalDist` and `recordedBodyFixedDist`: do all three agree (playback reproduces recorded data faithfully), do the two recorded distances agree but `renderedParentDist` diverges (playback bug), or do the two recorded distances disagree (the two recording surfaces store inconsistent parent-vs-debris geometry)?

Based on the answers, the fix shape is one of: back-step only `part.transform.position`-using seed sites with `srf_velocity`; correct an upstream KSP timing assumption; address a playback-side anchor-vs-frame mismatch; or fix a recorder-side conversion that loses fidelity between the relative and body-fixed surfaces. Do not re-land any version of the reverted fix without a log bundle answering all seven questions.

---

## Open - controlled-vessel ghost initial slide observability landed, fix shape contingent

Watch-mode playback of an Absolute-section non-debris controlled-vessel ghost (e.g. Kerbal X Probe in `logs/2026-05-10_1713`) shows a brief visible slide on the first frame after activation. The position is correct after the slide; the user-perceived issue is the visible transition.

Phase 1 (this PR) ships permanent observability in `GhostRenderTrace`: new `EmitActivationDecision` structured phase emit (covers `RenderInRangeGhost` non-loop and `SynchronizeLoadedGhostForWatch` watch-resume), three new `AfterUpdate` fields (`rawPlaybackUT`, `visibleLead`, `clampFired`), an `activation-transition` detailed window opening on the first-visible transition for 1.0 s, and per-state hidden-pose tracking with a `hiddenPoseDelta` field on the transition row. Plan: `docs/dev/plans/fix-controlled-ghost-init-slide.md`.

Phase 2 is the investigation step: capture a fresh log bundle replaying the `s14` save through the probe-decouple → watch-frame sequence with `Settings → Diagnostics → Ghost render tracing` enabled, then walk the `phase=ActivationDecision` lines through the plan's decision matrix (`hiddenPoseDelta`, `clampFired`, post-activation `dM` vs `expectedDM`) to pick the Phase 3 fix shape.

**Investigation 2026-05-10:** the retained logs confirm observability landed, but do not contain a clean replay of the original `32d9674c...` Kerbal X Probe Absolute-section slide from `logs/2026-05-10_1713`. That older repro shows the slide (`AfterUpdate ... frame=80871 ... dM=56.78 ... active=false`, followed by `GhostAppearance ... firstFrameClamped=F ... activationLead=0.03`) but predates `ActivationDecision`, `hiddenPoseDelta`, `rawPlaybackUT`, `visibleLead`, and `clampFired`. The newer `logs/2026-05-10_2123` bundle has `ghostRenderTracing=True` and the new fields, but the original probe recording is suppressed/superseded; the Re-Fly successor `rec_f136...` activates from an `OrbitalCheckpoint` with `hiddenPoseDelta=0.000`.

The same newer bundle does expose a related controlled-vessel activation on parent `Kerbal X` (`e19eb61d...`): first-visible has `hiddenPoseDelta=489.225`, `clampFired=false`, and same-frame `AfterUpdate dM=489.22 expectedDM=34.92`. That evidence argues against blindly changing `InitialActivationHiddenMinimumFrames = 2 -> 3`; it looks more like a downstream/path discontinuity unless a fresh probe replay shows the original slide is purely a hidden-frame timing issue. Missing evidence: a fresh `s14` replay around frames 80869-80872 with `phase=ActivationDecision`, `hiddenPoseDelta`, `clampFired`, `rawPlaybackUT`, `visiblePlaybackUT`, `visibleLead`, `hideReason`, `framesRemaining`, and the next ~1s of post-activation `AfterUpdate` rows.

---

## Done - SegmentPhase saved value reflects start state, not end state

- Active unsplit tree leaves now persist a final endpoint `SegmentPhase`/`SegmentBodyName` instead of keeping the fork-start tag. Normal stop propagates the tagged `CaptureAtStop` phase into the active tree row using `tree.ActiveRecordingId` as the row proof (not `CaptureAtStop.RecordingId`, which is a fresh GUID). ForceStop/scene-exit finalization applies the endpoint phase after terminal orbit refresh and endpoint decision refresh, including records that already had `TerminalStateValue`. Committed chain segments and optimizer-owned non-active rows are preserved. RELATIVE sections are handled conservatively: section environment only applies when paired with terminal metadata or absolute-shadow endpoint evidence, and fallback never treats raw RELATIVE point latitude/longitude/altitude or stale start/body tags as real endpoint proof.
- **Investigation 2026-05-10:** confirmed as an actual persisted-state bug. `FlightRecorder.StopRecording()` builds `CaptureAtStop`; `ParsekFlight.StopRecording()` classifies the stop-time phase into that capture; `FlushRecorderToTreeRecording()` appends points/events/sections and start metadata but never copies `CaptureAtStop.SegmentPhase` or `SegmentBodyName` into the tree recording. The persisted field is what `RecordingTreeRecordCodec` writes and what the recordings table displays.
- **Runtime evidence:** `logs/2026-05-10_1713` recording `rec_b1566...` saved `terminalState = 0` (Orbiting) with `segmentPhase = atmo`. Its sidecar starts Atmospheric but ends in ExoBallistic/OrbitalCheckpoint sections with final `env = 2`, `ref = 2`, and `sma = 1186923...`. The optimizer detected the atmo->exo split but deferred it because this was the active Re-Fly recording, so optimizer splitting cannot be the only repair path.
- **Fix:** final/end tags now overwrite empty tags and Re-Fly fork-start tags only for the active unsplit tree leaf. Chain-boundary tags and optimizer split tags stay authoritative.

**Status:** DONE 2026-05-11 in `fix-segmentphase-persistence`.

---

## Done - dead-code SegmentPhase tag block in `ParsekFlight.StopRecording`

- **Investigation 2026-05-10:** `ParsekFlight.StopRecording` wrote the final phase tag to `recorder.CaptureAtStop.SegmentPhase`, not to the tree recording. Since `FlushRecorderToTreeRecording` did not propagate the field, this tag never landed on disk for tree-mode recordings.
- **Fix:** the block now uses the shared classifier and its `CaptureAtStop` tag is consumed by `FlushRecorderToTreeRecording` for the active tree row. Scene-exit paths still do not create `CaptureAtStop`; those are covered by finalization endpoint tagging.

**Status:** DONE 2026-05-11 in `fix-segmentphase-persistence`.

---

## Done - duplicated SegmentPhase classifier in three sites

- **Investigation 2026-05-10:** `ParsekFlight.TagSegmentPhaseIfMissing`, `ParsekFlight.StopRecording`, and `ChainSegmentManager.CommitVesselSwitchTermination` duplicated the same body/altitude/situation classification logic. Source review found no behavior drift, but the duplication was a cleanup-only drift hazard.
- **Fix:** `SegmentPhaseClassifier` now centralizes live-vessel classification and environment-to-phase mapping. `ParsekFlight.TagSegmentPhaseIfMissing`, the `StopRecording` final tag block, `ChainSegmentManager.CommitVesselSwitchTermination`, and optimizer section splits share that helper.

**Status:** DONE 2026-05-11 in `fix-segmentphase-persistence`.

---

## Done - debris relative-playback discontinuity under sparse anchor samples

- Same playtest, same log: `Kerbal X Debris` ghosts (`rec=3461390b…`, `311b452f…`, etc.) showed `dM=13.21 expectedDM=3.54` and similar 3-7× over-shoots between consecutive playback frames at the spawn window of the slot=1 Re-Fly. The recorded relative-frame samples around UT 31 have a ~2 s gap (UT 31.04 → 33.04) with a large local-offset change between adjacent samples; playback interpolation overshoots when the parent anchor (Kerbal X booster) is moving at ~150 m/s in the gap. This shows up visually as the user's "glitchy probe-booster ghost" complaint.
- **Fix:** Format v13 makes `bodyFixedFrames` the primary render surface for parent-anchored debris and treats anchor-local `frames` as the secondary/live-anchor path only for loop-anchored debris chains whose parent is itself in an active Relative section with covered parent frames. Flight, KSC, map-state-vector, tracking-station, standalone world-position lookup, and boundary-anchor consumers all fail closed on missing, stale, or unresolvable ordinary-debris body-fixed primary samples instead of clamping or replaying recorded Relative frames, and they log the deliberate recorded-relative suppression route. The recorder now uses parent-proximity tiers (full-rate at <=250 m, half-rate/Relative entry through <=500 m, Relative exit at >550 m), forces an immediate Relative-entry sample with a body-fixed peer, and playback no longer runs the old tumbling/gate router.

**Status:** DONE 2026-05-11 via `docs/dev/plans/debris-frame-contract-v13.md`.

---

## Done - debris ghost trajectories diverge during normal playback and Re-Fly cascades

- During ordinary Watch / table playback of a Kerbal X mission, the `Kerbal X Debris` rows render at "very, very inexact and wrong" world positions. Source: `logs/2026-05-06_2246_refly-vessel-spawn-debris-watch/KSP.log`. Background-recorded debris sections are saved as `referenceFrame=Relative` with sparse sampling — `[BgRecorder] TrackSection sparse sampling: pid=2236546571 env=Atmospheric ref=Relative frames=42 maxGap=1.640s threshold=0.50s largeGaps=2`, and `pid=3856523371 ... maxGap=1.846s largeGaps=5` — and they form a debris-anchored-on-debris chain (e.g. `RELATIVE mode entered: ... anchorRecordingId=ba1913864e3d4136a7970bcb14f6ccf0 ... source=Live diagnosticPid=2859430124`, which itself is anchored on `c67802c3...`). Each link in the chain is finalized at a different UT, so playback past the anchor's `endUT` produces `[WARN][RelativeAnchorResolver] relative-anchor-unresolved: reason=anchor-out-of-recorded-range recordingId=00964eb6... anchorRecordingId=00964eb6... ut=1228.43...` (more than 2000 suppressed). Recording `e13b6f3f` runs `[1228.4,1234.8]` while its declared anchor `00964eb6` ends at `1213.4` — the anchor is destroyed 15s before this child even starts, so the live anchor pose is unresolvable and the resolver falls through to the v7 absolute shadow: `[WARN][Playback] RELATIVE recorded-anchor fallback to absolute shadow: recording #9 "Kerbal X Debris" recordingId=e13b6f3f anchorRec=00964eb6... frames=26 sectionUT=[1228.4,1234.8]`. The shadow itself is sampled with the same sparse cadence, so the visible trajectory is whatever the shadow captured — coarse, drifty, and unrelated to where the debris would actually be.
- After a Re-Fly of the capsule, debris that the player vessel sheds during the Re-Fly attempt also renders at wrong positions. `BackgroundRecorder.TryGetBackgroundEligibleAnchorRecording` (`BackgroundRecorder.cs:3687-3693`) explicitly excludes `marker.ActiveReFlyRecordingId` from anchor selection: when the player's live vessel is the active Re-Fly, its recording is filtered out of the live-anchor candidate set. New debris born off that vessel must instead anchor on a still-loaded ghost candidate or fall through to Absolute. The ghost candidates' recorded world positions diverge from the player's live position by exactly the Re-Fly delta (the whole point of Re-Fly), so any debris the new run sheds is encoded in a Relative frame whose anchor is in the wrong place. On replay the new debris snaps onto the divergent ghost anchor, not to the player's actual breakup site.
- Follow-up session `logs/2026-05-06_2351_refly-phase-d-rewind-button-debris` confirms the defect is baked into recorded/merged trajectory data, not just a ghost mesh placement issue. The retained `KSP.log` contains 31 `MergeTree: boundary discontinuity` warnings, 49 `relative-anchor-unresolved` warnings, 21 `RELATIVE recorded-anchor fallback to absolute shadow` warnings, 12 `TrackSection sparse sampling` warnings, 14 forced Absolute transitions, 3 non-monotonic flush-stitch skips, and 89 sub-surface/finalizer warnings. At `23:47:47.856`, active playback switched a Relative anchor from probe `0cf6d9a1...` to ghost debris `c2c7d56a...` with `liveCandidates=0/0 ghostCandidates=4/4`, then recordings `0123b753...` and `0cf6d9a1...` immediately fell back to absolute shadow. At `23:48:04.224`, active Re-Fly relative samples logged offsets of `|offset|=2500.28m`, `1512.96m`, and `1728.92m`; a new Relative section closed with only 28 frames over ~21s and `maxGap=1.060s`; and `d3fa1e41...` produced `anchor-out-of-recorded-range` against ghost anchor `c73cca1b...` with `suppressed=1723`. The same window cascaded absolute-shadow fallback through debris recordings `c2c7d56a...`, `6213fe30...`, and `b2b5215a...`, then forced `c73cca1b...` to Absolute at UT `16519.71`.

**Diagnosis (symptom 1, common-case debris):** The debris-anchored-on-debris chain that `BackgroundRecorder.UpdateBackgroundAnchorDetection` builds (`BackgroundRecorder.cs:3441-3530`) is fragile under three compounding conditions native to atmospheric breakups: (a) anchor recordings are themselves short, fast-moving Background debris with sparse Atmospheric `ref=Relative` sampling (the warnings show `maxGap` up to 1.846s on 0.5s-threshold sections, see `[BgRecorder] TrackSection sparse sampling: ... maxGap=1.640s largeGaps=2`); (b) anchors finalize earlier than their dependents (e.g. `00964eb6` ends at UT 1213.4 but `e13b6f3f` starts at UT 1228.4 anchored on it); and (c) `TrajectoryTextSidecarCodec.cs:1575-1577` deliberately stops persisting `anchorPid` for `recordingFormatVersion >= RecordingAnchorChainFormatVersion (=11)`, so on reload the only anchor handle is `anchorRecordingId`, which dispatches through `RelativeAnchorResolver.TryResolveAnchorPose` (`RelativeAnchorResolver.cs:80-138`) and recursively walks the chain. Every chain hop multiplies the sampling-gap interpolation error and bottoms out on the unresolvable boundary, where `TryUseRelativeAbsoluteShadowFallback` (`ParsekFlight.cs:21852-21903`) saves rendering from full retirement but only by playing back the recorder's coarse absolute-shadow snapshot — it does not restore the resolution the user expects.

**Diagnosis (symptom 2, Re-Fly debris):** `BackgroundRecorder.TryGetBackgroundEligibleAnchorRecording` (`BackgroundRecorder.cs:3687-3693`) hard-excludes the active Re-Fly recording from anchor candidacy, presumably because playback of existing non-loop Relative data must not follow the diverged live vessel. In current Phase D code, that playback contract lives in `RelativeAnchorResolver.TryResolveActiveReFlyAnchorRecording` (`RelativeAnchorResolver.cs:943-974`) and `ParsekFlight.ShouldUsePreReFlyAnchorTrajectory` (`ParsekFlight.cs:20750-20774`): when an active Re-Fly recording is resolved as an anchor, playback uses the frozen pre-Re-Fly trajectory or falls back to recorded shadow data, not the live vessel. That contract is correct for *playback* of pre-existing relative recordings, but it is catastrophically wrong when reused as a *recording* filter for new debris created during the Re-Fly: the recorder still picks the nearest non-excluded anchor, which is some other ghost vessel candidate whose recorded coords are by definition the un-Re-Flown trajectory. The new debris is then encoded as `(dx,dy,dz)` in that wrong anchor frame, persisted as a v11 Relative section, and on playback rendered against that same recorded-but-displaced anchor. Both symptoms ultimately go through the same v11 chain-resolver and v7 shadow-fallback machinery, but symptom 2's data is poisoned at recording time while symptom 1's data is sound but exhausts its anchor span on playback.

**Additional evidence from `2026-05-06_2351`:** The sidecars and final save show the bad data persisted. `rec_0fd46f70...prec.txt` contains the active replacement `Kerbal X` recording with multiple Relative sections anchored to `0cf6d9a1...`, `c2c7d56a...`, and other debris/probe recordings. `d84e050b...prec.txt`, the new Re-Fly debris from branchpoint `ecb9b42...` at UT `16506.625`, starts Absolute at alt ~1297m, then switches into a Relative section `[16507.145,16509.965]` anchored to `0123b753...` with extreme oscillating local-offset payloads in the misleading v6/v11 `latitude/longitude/altitude` fields (`lat=93.47 lon=-134.53 alt=-115.5`, then `lat=11.73 lon=-117.08 alt=-29.1`, then `lat=193.02 lon=-139.89 alt=-22.83`). The merge pass later persisted boundary discontinuities of `105148.80m`, `406011.50m`, and `8147542.00m` for old `Kerbal X Debris`; up to `16479040.00m` for new Re-Fly debris `d84e050b...`; and up to `19299100.00m` for active replacement `rec_0fd46...`, with causes alternating between `sample-skip` and `frame-mismatch`. The final save had 10 committed recordings and 5 branchpoints, including supersede `e1ea034b... -> rec_0fd46...`, plus debris/probe recordings `c73cca1b...`, `d3fa1e41...`, `c2c7d56a...`, `6213fe30...`, `b2b5215a...`, `0123b753...`, and `d84e050b...`; this rules out a transient render-only state.

**Sub-surface / terminal-state evidence:** The same session repeatedly computed live-orbit fallback states deep under Kerbin (`alt=-599xxx`) for debris, then had the finalizer suppress or reject those states because nearby recorded surface points contradicted the fallback. Examples: `Start rejected: sub-surface state ... classifying recording as Destroyed`, `TryFinalizeRecording: suppressing sub-surface Destroyed ... because a nearby recorded surface point contradicts the live-orbit fallback`, and `FinalizerCache Apply rejected ... RejectedTerminalBeforeLastSample` for `c73cca1b...`, `d3fa1e41...`, and `d84e050b...`. One retained line shows `SnapshotPatchedConicChain: vessel=Kerbal X Debris solver unavailable | suppressed=48`; another shows `Apply rejected: consumer=EndDebrisRecording reason=RejectedTerminalBeforeLastSample rec=d84e050b... lastAuthoredUT=16525.562 terminal=Destroyed terminalUT=16517.137`. This likely shares root cause with bad debris trajectories: when the background recorder loses a reliable live/recorded anchor frame, the orbit/finalizer fallback reports impossible sub-surface state, and the terminal-state cleanup has to guess whether to trust the fallback or the last authored trajectory point.

**Fix:** The final v13 contract keeps the debris-parent id, but no longer depends on legacy compatibility gates. New v13 recordings always stamp the current format and any non-v13 recording/sidecar is rejected instead of partially loaded or migrated. Parent-anchored debris records a body-fixed primary surface and an anchor-local secondary surface; ordinary debris playback uses the body-fixed primary first across flight, KSC, Tracking Station, and map-state-vector paths, while loop-anchored debris chains try live relative playback only when the child frames and each parent link have active Relative coverage and otherwise fall back to body-fixed primary. Background debris enters Relative only while its parent is loaded/unpacked and within the parent-proximity band, exits through hysteresis beyond 550 m, and records at the proximity cadence needed for nearby debris, including an immediate Relative-entry sample with a body-fixed peer. The obsolete legacy shadow gate, tumbling-parent reliability gate, Re-Fly post-load debris settle suppression, and v11/v12 migration tests were removed.

**Status:** DONE 2026-05-11 via `docs/dev/plans/debris-frame-contract-v13.md`. Remaining sub-surface finalizer polish from the old work queue is not part of the debris frame contract and should be tracked separately if it reproduces after v13 recordings.

---

## Open - reset recording/rendering schema versions to v0 and delete pre-release compatibility

- After the ghost rendering / Re-Fly Phase D cleanup lands, reset Parsek's recording and rendering sidecar version baselines to zero. We have no public users yet, so do not preserve the old v1-v11 compatibility ladder or spend effort migrating older saves. The goal is a cleaner codebase where "v0" means the current post-redesign recording contract, not the historic pre-v6 legacy format.

**Current state to reset:** `Source/Parsek/RecordingStore.cs:57-65` still carries the historical ladder `LaunchToLaunchLoopIntervalFormatVersion = 4`, `PredictedOrbitSegmentFormatVersion = 5`, `RelativeLocalFrameFormatVersion = 6`, `RelativeAbsoluteShadowFormatVersion = 7`, `BoundarySeamFlagFormatVersion = 8`, `TerrainGroundClearanceFormatVersion = 9`, `StructuralEventFlagFormatVersion = 10`, `RecordingAnchorChainFormatVersion = 11`, `DebrisParentRecordingFormatVersion = 12`, `DebrisFrameContractFormatVersion = 13`, and `CurrentRecordingFormatVersion = DebrisFrameContractFormatVersion`. `Source/Parsek/TrajectorySidecarBinary.cs:31-63` mirrors that ladder with binary versions and per-field decode gates. The retained validation session `logs/2026-05-06_2351_refly-phase-d-rewind-button-debris` still wrote pre-v13 data, so the reset has not been exercised yet.

**Implementation intent:** Collapse the current full schema to v0 for new saves and sidecars. Remove or rewrite version branches whose only purpose is to support old internal saves: pre-v4 loop-interval migration, v5 predicted-orbit compatibility, pre-v6 Relative lat/lon/alt interpretation, v7 body-fixed primary history, v8 boundary-seam gates, v9 terrain-ground-clearance defaulting, v10 structural-event defaulting, v11 anchor-chain gates, v12 debris-parent gates, and v13 debris-frame gates. Prefer strict rejection or discard of older Parsek recording files with a clear WARN/UI message over best-effort migration. Keep feature flags or named constants only when they describe code behavior, not save compatibility history.

**Files / areas to audit:** `RecordingStore.cs`, `RecordingSidecarStore.cs`, `TrajectorySidecarBinary.cs`, `TrajectoryTextSidecarCodec.cs`, `RecordingTreeRecordCodec.cs`, `ParsekScenario.cs`, `FlightRecorder.cs`, `BackgroundRecorder.cs`, `ParsekFlight.cs`, `GhostMapPresence.cs`, `ParsekKSC.cs`, `ProductionAnchorWorldFrameResolver.cs`, `GhostPlaybackEngine.cs`, and rendering sidecars such as `PannotationsSidecarBinary.cs` / smoothing/co-bubble caches that embed `sourceRecordingFormatVersion`. Delete or update tests whose only value was old-version compatibility (`FormatVersionTests`, binary/text sidecar legacy round trips, loop migration tests, old Relative contract tests) and replace them with tests that pin the new v0 full contract plus strict refusal/discard of pre-reset files.

**Injector / showcase work:** Update `Source/Parsek.Tests/Generators/RecordingBuilder.cs`, `RecordingStorageFixtures.cs`, `ScenarioWriter.cs`, and `SyntheticRecordingTests.InjectAllRecordings` so generated/injected recordings write `recordingFormatVersion = 0` and sidecar `version = 0` while still containing the full current payload. Refresh `Source/Parsek.Tests/Fixtures/DefaultCareer/` and any showcase/default-career recording sidecars: that fixture currently mixes `recordingFormatVersion = 0` and `3`, while its retained `.prec` files are `version = 5`. Update `scripts/inject-recordings.ps1` / any showcase injection workflow docs so the injected save is generated from the v0 baseline and does not rely on legacy fixture migration. Run `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter InjectAllRecordings` after closing KSP or pointing `KSPDIR` at an isolated install, then run the relevant in-game showcase / ghost playback tests.

**Acceptance gates:** New recordings, tree metadata, text `.prec`, binary `.prec`, pannotations/co-bubble smoothing sidecars, synthetic fixtures, and injected showcase recordings all report version `0`. Grep should show no raw historical version constants `4` through `11` used as recording-format gates, no legacy loop/predicted/relative migration helpers, no acceptable sidecar-version lag path, and no read-side silent drop for old pre-Re-Fly payloads such as `PRE_REFLY_ORIGINAL`. Loading old Parsek recordings should produce an explicit refusal/discard path rather than a partial migration. Documentation in `.claude/CLAUDE.md`, `AGENTS.md` if needed, and relevant design docs should say v0 is the post-reset baseline.

**Status:** OPEN 2026-05-07.

---

## Active - v0.9.2 Re-Fly cleanup and v0 reset

- After PR #708 merges, continue from `docs/dev/plans/ghost-anchor-recording-chain-plan.md` rather than adding more stabilization into the PR708 branch. PR708's merge scope is Phases A-C plus playtest hardening: v11 `TrackSection.anchorRecordingId`, recorder-side recording-id anchor selection, non-loop Relative playback through `RelativeAnchorResolver`, frozen/body-fixed Re-Fly display alignment, Watch activation/tail/LOD stabilization, and the follow-up fixes documented in `docs/dev/plans/pr708-playtest-followup-plan.md`. Final PR708 validation evidence is `logs/2026-05-03_2007_pr708-final-watch-good`: KSP log validation passed, no Parsek errors or exception signatures were found, Watch activation gates hid the bad Probe/debris primer frames, renderer LOD hysteresis stopped the 2300m flicker, the final save contains the expected `RECORDING_TREE`, and focused/broad non-live xUnit passed (`239/239`, `10670/10670`).

**D.0 decision:** active Re-Fly ghosts must detach from the live vessel and render only at original recorded coordinates during divergent Re-Fly. Divergence is a product signal, not something the renderer should hide by translating old ghosts toward the live attempt.

**D.1 implementation:** remove the frozen body-fixed display-alignment cache and consumers (`ReFlyDisplayAlignment`, `TryGetReFlyTreeAnchorOffset`, ghost `reFlyTreeOffset`, root-part pinning, active Re-Fly render interpolation, and point-trend smoothing). Recorded-coordinate playback now feeds ghost placement directly; the separate D.5 pass removed the temporary Re-Fly activation gate while leaving the generic fresh-spawn playback-sync defer path intact.

**D.2 implementation:** remove the stale-anchor/no-live-anchor absolute-shadow fallback branches and the active-Re-Fly live-anchor bypass selector. Loop Relative playback now uses its explicit live anchor or retires; non-loop Relative playback continues through the recording-id resolver and recorded-coordinate fallback path.

**D.3 implementation:** remove the RELATIVE absolute-shadow forward-bridge fallback (`TryFindAbsoluteShadowForwardBridgeFrame`) and its adjacent-section append path. Sparse RELATIVE sections no longer borrow future absolute/shadow frames; playback stays section-local and lets the recorded-coordinate resolver contract decide the visible pose or retirement.

**D.4 implementation:** remove the non-loop live-PID anchor contract from flight playback. `IGhostPositioner.TryGetLiveAnchorWorldPosition`, `GhostPlaybackEngine.DescribeAppearanceLiveAnchorContext`, legacy anchor-PID appearance/watch logs, and recorded-Relative trace emissions that echoed `TrackSection.anchorVesselId` are gone. Non-loop Relative diagnostics now report `anchorRecordingId` or `anchorRec=missing`; loop Relative playback keeps its explicit live-anchor PID contract.

**D.5 implementation:** remove the Re-Fly external activation-defer gate (`GhostPlaybackState.externalActivationDeferred`, `RefreshReFlyAnchorActivationGate`, `ShouldRaiseExternalActivationGate`, `ReFlyActivationGate` trace/log phases) and the orphaned Re-Fly anchor-sampling helpers that only fed that gate. The engine still keeps `deferVisibilityUntilPlaybackSync` for fresh/rebuilt ghost first-frame synchronization.

**D.6 implementation:** no production change required. `ProductionAnchorWorldFrameResolver.TryResolveRelativeBoundaryWorldPos` was already clean after Phase C; the remaining live-PID resolver is loop-only (`TryResolveLoopAnchorWorldPos`) by design.

**D.7 implementation:** fence KSC and map Relative playback away from live vessel PID lookups. `ParsekKSC` now resolves Relative playback poses through recorded anchor IDs, and `GhostMapPresence` state-vector Relative branches use `RecordedRelativeAnchorPoseResolver` instead of `FlightRecorder.FindVesselByPid`, `ResolveAnchorInScene`, `AnchorResolvableForTesting`, or `TryResolveActiveReFlyAbsoluteShadowPoint`. The create-time active-Re-Fly lookahead is now a recorded-anchor-chain no-op instead of a live-PID suppression scan.

**Grep-audit guard:** `scripts/grep-audit-non-loop-live-pid.ps1` plus `GrepAuditNonLoopLivePidTests` enforce the deleted non-loop live-PID surfaces. `Rendering.NonLoopLivePidGuard` also exposes a DEBUG-only regression counter for future live-PID lookup attempts in non-loop Relative paths.

**Carry-forward validation:** keep the PR708 final bundle as the baseline and consider one targeted map/tracking terminal-spawn smoke if later Phase D work depends on terminal handoff behaviour. Do not treat pre-v11 recordings as correctness fixtures; regenerate any runnable regression fixture under v11 with real `anchorRecordingId` chains. Keep the transient pre-merge-dialog stranded-sidecar save warning as a separate follow-up, not a PR708 merge blocker, unless new evidence shows retained save corruption.

**Branch B (v0 format reset, not started):** plan doc `docs/dev/plans/refly-cleanup-and-v0-reset.md` §3 / §4 Branch B. Reset `CurrentRecordingFormatVersion` from 13 to 0 with a discriminator that makes pre-reset saves unloadable, drop the v4-v13 reader code path, delete `TrackSection.anchorVesselId` if no longer needed after loop-anchor follow-up, bump the mod to v0.10.0. All existing playtest saves under `Kerbal Space Program/saves/` become unloadable; acceptable per the user sign-off in plan §3.5 ("no career save needs preservation"). UX on load: one-time warn log per unsupported recording, recordings-table empty state, orphan sidecars left on disk, no partial-load recovery.

*Strictly required by the reset:*

- Eight-axis version reset together: trajectory data (`.prec` binary + `.prec.txt` text), recording-tree topology (`RecordingTree.CurrentTreeFormatVersion`), vessel/ghost snapshots (`SnapshotSidecarCodec.CurrentFormatVersion`), pannotations (`PannotationsBinaryVersion` / `AlgorithmStampVersion` / `CanonicalEncoderVersion`), career ledger (`Ledger.CurrentLedgerVersion`), `ReFlySessionMarker` schema (implicit; field-presence-defined), other ScenarioModule `.sfs` data (plan §3.10). The named feature constants `LaunchToLaunchLoopIntervalFormatVersion` ... `RecordingAnchorChainFormatVersion` (`RecordingStore.cs:57-65`) collapse to a single `CurrentRecordingFormatVersion = 0`.
- Discriminator (two layers, both required because some paths are binary-only and some are `.sfs`-embedded text). Layer 1: binary magic prefix change — `PRKB` → new tag (suggested `PSK0`) for `.prec`; `PANN`/`PANC` and `PRKS` get parallel new tags. Layer 2: new `RecordingSchemaGeneration = 1` field stamped at write time with **strict equality** read gate; reject reasons distinguished in the warn log: `magic-mismatch`, `generation-missing`, `generation-older`, `generation-newer`, `format-version-mismatch`. Strict equality (not `>=`) because future resets bump only the generation, so a `>=` reader would let a future-generation save silently load on an older binary.
- Delete the v4-v11 binary write/read ladder, the `formatVersion >= N` gates throughout the codebase, the legacy `.prec.txt` load path (text codec survives only as debug-mirror writer gated by an existing diagnostics setting). See plan §3.3 for the verified gate inventory across `TrajectorySidecarBinary.cs`, `TrajectoryTextSidecarCodec.cs`, `RecordingStore.cs`, `RecordingSidecarStore.cs`, `RelativeAnchorResolver.cs`, `FlightRecorder.cs`, `ParsekFlight.cs`, `GhostPlaybackEngine.cs`, `PannotationsSidecarBinary.cs`, `SnapshotSidecarCodec.cs`, `Ledger.cs`.
- Schema refusal at every load entry point: both `LoadRecordingTrees` (committed trees) and `TryRestoreActiveTreeNode` (active trees) apply the same `IsSchemaCompatible` predicate before `AddCommittedInternal` / pending-tree stash. Drop empty trees, drop trees whose `RootRecordingId` is rejected, clear `tree.ActiveRecordingId` when it points at a rejected recording, drop `BranchPoint`/`SupersedeRelation` rows referencing rejected recordings, clear `pendingActiveTreeResumeRewindSave` (`ParsekScenario.cs:4674` declaration; assigned at `:3145`) and call `ClearPendingQuickloadResumeContext()` on active-tree refusal. Sidecar files stay on disk (no auto-delete).
- Test fixture regeneration: every checked-in `.sfs` fixture under `Source/Parsek.Tests/Fixtures/` re-baked at v0; `RecordingBuilder` / `ScenarioWriter` / `VesselSnapshotBuilder` defaults flip to v0 + generation 1; `LegacyTreeMigrationTests.cs` and `RecordingBuilderV6Tests.cs` deleted; `FormatVersionTests.cs` rewritten as discriminator-refusal tests. Loader-refusal tests with three explicit cases: legacy v11 binary (`magic-mismatch`), legacy default-0 record with no generation field (`generation-missing`), synthetic future-generation save (`generation-newer`).
- `.sfs` schema audit: stamp `RecordingSchemaGeneration` on every ScenarioModule write that needs to round-trip — `ParsekScenario.OnSave`, `ReFlySessionMarker.SaveInto`, `MergeJournal.OnSave`, `CrewReservationManager`, `GroupHierarchyStore`, `RecordingGroupStore`, `RewindInvoker` RP metadata. Reject on read where the stamp is missing or `!= CurrentSchemaGeneration`. No "default to current and stamp on next write" silent migration — that defeats strict equality.

*Bundled with Branch B (convenience, not strictly the reset):*

- Delete `TrackSection.anchorVesselId` field (`TrackSection.cs:56`). Phase D made it unused in production, but the field can only be removed when the serialized format version is changing.
- Delete `LegacyMergeStateMigrationCount`, `EmitLegacyMergeStateMigrationLogOnce`, `BumpLegacyMergeStateMigrationCounterForTesting`, `ResetLegacyMergeStateMigrationForTesting` (committed-bool tri-state migration helpers in `RecordingStore.cs:135-164`); `LegacyGloopsGroupName` (group rename migration at `RecordingStore.cs:78`); `LegacyPrefix` (log compatibility at `RecordingStore.cs:194-202`). Pre-existing one-shot migrations from older save shapes — piggybacking because the migration targets are dead.
- Delete the `RecordingTreeRecordCodec` PRE_REFLY_ORIGINAL silent-drop read tolerance (the comment-only write side at `:315` already removed in PR #751; the read tolerance becomes unreachable once loader refusal lands).
- Mod version v0.9.x → v0.10.0 — both `Parsek.version` and `AssemblyInfo.cs` (`scripts/release.py` validates they match).
- Branch A's deferred scenario assertion: once v0 fixtures exist, add watch + Re-Fly playback coverage that asserts `NonLoopLivePidGuard.LivePidLookupAttemptsForTesting == 0` after playback completes (Branch A only ships the unit test for the guard's reset/count semantics; the runtime safety net needs the scenario fixtures Branch B creates).

*Commit shape (plan §4):*

1. Write/read gate audit — document every `>= N` gate per the §3.3 inventory, decide its fate (collapse to unconditional vs delete), no value flips yet.
2. Introduce binary magic prefix + `RecordingSchemaGeneration` field stamped on writes only; readers still accept legacy. Either Option A (promote probe data to persisted fields on `Recording`: `RecordingSchemaGenerationLoaded`, `LoadedMagicTag`, `LoadResultSchemaCompatible`) or Option B (`LoadRecordingFiles` returns a `LoadRecordingResult` struct). Pick during commit 2.
3. The actual flip: `CurrentRecordingFormatVersion = 0`, all other version constants reset per plan §3.6, legacy readers deleted, `anchorVesselId` deleted, fixtures regenerated, in-game test version literals updated, migration helpers deleted, version bump.
4. `.sfs` schema audit pass.

*Acceptance:* `dotnet test` (full headless) green against regenerated fixtures; `dotnet test --filter InjectAllRecordings` green against re-baked synthetic recordings; in-game smoke on a fresh v0 save (Watch + active Re-Fly + map view + KSC ghost view) with no `[ERROR]` lines in `KSP.log`; loader-refusal tests pass against pre-reset legacy fixtures (3 cases above); `scripts/grep-audit-non-loop-live-pid.ps1` and `scripts/grep-audit-ers-els.ps1` green; Branch B grep gate — after commit 3, `RecordingFormatVersion\s*=\s*\d+` / `formatVersion\s*=\s*\d+` / `binaryVersion\s*=\s*\d+` / `PeerSourceFormatVersion\s*=\s*\d+` literals other than 0 must be zero outside negative-test cases.

*Rollback:* tag `pre-v0-reset` on the parent commit before merging Branch B. A revert of the Branch B merge is the right shape; legacy reader deletions are too broad to forward-fix on top of v0. Document tag name and revert recipe in the Branch B PR description.

*Out of scope (Branch C or never):* the old `absoluteFrames` compatibility story has been superseded by the v13 `bodyFixedFrames` primary surface and strict pre-v13 refusal. Branch B should collapse the remaining version history into v0 rather than carrying a separate Branch C shadow-data deletion. Loop-anchored recordings still keep `LoopAnchorVesselId` live-vessel anchoring; switching that to recording-id is a separate plan. Phase F promote-to-absolute permanently deferred per `ghost-anchor-recording-chain-plan.md` §9.3.

*Documentation updates Branch B owns (same-commit):* `CHANGELOG.md` entry under v0.10.0 with a public-history note that the mod version drops from v0.9.x to v0.10.0 while the recording format renumbers from v11 to v0; `.claude/CLAUDE.md` and `AGENTS.md` "Recording storage" gotcha blocks rewritten to v0 (remove the v6/v7/v10/v11 enum constants section); `MEMORY.md` refresh `project_format_v0_reset.md` pointer plus new `project_post_v0_reset_arc.md` entry pointing to the plan.

**Status:** Phase D implementation is complete on `refly-phase-d`; focused xUnit, broad non-injection xUnit, the ERS/ELS grep audit, and the non-loop live-PID grep audit are green. Full xUnit currently reaches the `InjectAllRecordings` test and is blocked locally because the running KSP instance holds `KSP.log`; optional in-game smoke remains the final runtime validation step before merge. Branch B (v0 format reset) is the next deliverable; pick up from a fresh worktree off `origin/main` once Branch A merges.

---

## TODO - STASH auto-seal persisted reason metadata

**Status:** TODO - deferred schema follow-up from PR #696 review.

`ChildSlot.Sealed` / `SealedRealTime` intentionally stay schema-minimal in the STASH safety PR, and the runtime INFO log distinguishes player Seal from system auto-seal with `reason=<closeReason>`. The persisted slot does not yet retain `SealedBy` / `SealedReason`, so a future Timeline or Recordings-table explanation UI would need to reconstruct the reason from logs. Add explicit persisted metadata before building any in-game "why was this sealed?" affordance.

---

## 640. Stock committed-future overlay v2 follow-ups

**Status:** TODO - future investigation / review item from PR #721.

PR #721 ships the v1 scope: stock R&D, Astronaut Complex, and Mission
Control committed-future overlays, plus click-blocks for duplicated tech,
contract accept, kerbal hire, and facility upgrade actions. The following
ideas are deliberately out of v1 scope and should be reviewed as separate
follow-ups after in-game verification:

- KSC facility-upgrade visual overlays in the top-down KSC view. The
  click-block already exists via `FacilityUpgradePatch`; v2 would add the
  visual badge and extend the overlay/click-block invariant to facilities.
- Future-completed / future-failed contract badges in Mission Control, not
  only future-accepted contract badges.
- Administration strategy activation overlays, paired with matching
  click-block behavior if the stock UI has a clickable affordance.
- Per-row claim / override UI for cases where the player intentionally wants
  to bypass a committed-future action, instead of using the global setting.
- Per-user dismissible badges for "hide this warning until next session" style
  workflows.
- Non-stock screen integrations, such as Contract Configurator's own Mission
  Control replacement or other mod-provided building screens.
- Modded flight-scene building overlays. The current v1 overlays are
  `SPACECENTER` scene-bound, while the lower-level click-blocks remain
  scene-agnostic.
- Tooltip styling polish using KSP's richer
  `KSP.UI.TooltipTypes.TooltipController_Text` path instead of the v1
  `GUI.skin.box` fallback.

**Review guidance:** keep the v1 invariant intact for every clickable action:
if a stock or modded UI exposes a clickable affordance, the overlay candidate
set and the click-block predicate must share the same `MilestoneStore` source
helper, with any UI-only suppression kept outside the click-block predicate.

## Phase 5 known gaps (deferred to later phases)

- The Phase 5 commit-time detector runs against `RecordingStore.CommittedRecordings` only — recordings persisted as part of the same commit batch but not yet appended to the live store at the time of `PersistAfterCommit` are added to the snapshot list explicitly. Multi-recording commit batches that span more than one persistence call still rely on the next `PersistAfterCommit` (or load-time lazy recompute, both of which now also persist peer-side `.pann` files symmetrically per review-pass-3 P3-1) to populate the missing-side trace.
- The `CoBubbleBlender` evaluates the offset against the primary's RECORDING for HR-15 compliance; if both the primary and peer have splines fitted, the peer's render aligns to the primary's smoothed position. If the primary's spline is missing (e.g. a section that never qualified for fit), the blender still returns the recorded offset against the primary's raw lerp. Visual residual under that condition is bounded by the primary's standalone fidelity.
- §7.7 BubbleEntry / BubbleExit and §7.9 SurfaceContinuous remain Phase 7 territory. Phase 5 did not promote either: BubbleEntry/Exit needs a session-time physics-active timeline scanner; SurfaceContinuous needs the Phase 7 per-frame terrain raycast.

## Phase 6 known gaps (deferred to later phases)

- ~~§7.7 BubbleEntry / BubbleExit candidates are not emitted by the Phase 6 builder.~~ Shipped: `AnchorCandidateBuilder.EmitBubbleEntryExitCandidates` walks adjacent `TrackSection` pairs and emits at every `Active|Background ↔ Checkpoint` source-class transition; `IAnchorWorldFrameResolver.TryResolveBubbleEntryExitWorldPos` reads the LAST/FIRST physics-active sample as the high-fidelity world reference. Mainline shipped this at `AlgorithmStampVersion=5`; on the Phase 5 stack it lands inside the v8 alg-stamp window. Residual gap: RELATIVE-frame physics-active sections adjacent to a Checkpoint segment are deferred with a `bubble-entry-exit-relative-section-deferred` Verbose (uncommon in practice — vessel docked to its anchor while a Checkpoint splices in).
- ~~§7.8 CoBubblePeer anchors are reserved in the enum but emit no candidates — Phase 5 territory.~~ Phase 5 ships a separate co-bubble offset trace pipeline (`.pann CoBubbleOffsetTraces` block + `CoBubbleBlender`); the `AnchorSource.CoBubblePeer` enum slot stays reserved for any future anchor-based co-bubble pathway but is no longer the active mechanism.
- The 2.5 km bubble-radius HR-9 Warn (`RenderSessionState.cs:836-848`) only fires from the LiveSeparation path inside `RebuildFromMarker`. Anchors written via `AnchorPropagator.TryWriteAnchor → PutAnchorWithPriority` (§7.4 / §7.5 / §7.6 / §7.7 / §7.10) skip the magnitude check, so a non-LiveSeparation ε of, say, 12 km lands silently. Lift the magnitude check into `PutAnchorWithPriority` (or the per-source dispatch) in a follow-up PR so all anchor types are uniformly guarded — pre-existing gap, not introduced by §7.7.
- §7.9 SurfaceContinuous emits a marker only with ε = 0; the per-frame terrain raycast that resolves ε is Phase 7 work. Phase 6 demoted the rank from 2 to 6 to prevent the zero stub from winning ties against real OrbitalCheckpoint ε; Phase 7 must promote back to rank 2 once the resolver ships and bump `AlgorithmStampVersion` so existing `.pann` re-resolve.
- The split anchor sources (Undock / EVA / JointBreak) currently share the `DockOrMerge` enum byte (priority rank 4 either way). Logs label them by `BranchPointType` rather than by enum value to preserve telemetry granularity. If a future phase needs to differentiate split priorities from dock priorities, expand the `AnchorSource` enum and bump `AlgorithmStampVersion`.

---

## Observability Audit - 2026-04-26

Full report: `docs/dev/observability-audit-2026-04-26.md`.
Implementation plan: `docs/dev/plan-observability-logging-visibility.md`.

Open implementation follow-up: make Parsek's runtime decisions reconstructable
from `KSP.log` without reintroducing per-frame spam. The audit prioritizes:

- P1 current spam hygiene: finalizer-cache summaries, patched-snapshot /
  extrapolator repeats, current map/proto-vessel/tracking-station repeaters,
  diagnostics sidecar warnings, ledger no-op summaries, sandbox patch skips,
  and KSC playback spam fixes.
- P2 ~~flight ghost skip reasons, playback frame skip summaries~~, rewind
  `CanInvoke` reason logging, sidecar/path severity and context, duplicate
  `OnLoad` timing cleanup, post-switch auto-record no-trigger summaries,
  background recorder drift warnings, game-action skip summaries, and ~~UI/map
  marker skip summaries for ghost/proto-vessel map presence and watch focus~~.
- P3 shared rate-limit key cleanup, repeated-warning rate limits, noisy resource
  event aggregation, production warning-prefix cleanup, and low-risk
  cleanup/reflection summaries.

Phase 0 guardrails started on `observability/guardrails`: retained-log signal
analysis, stricter post-hoc log validation, and guaranteed validation artifacts
from `collect-logs.py`.

2026-04-26 Phase 1 update: the current retained-log hygiene slice is closed for
the finalization/map signal called out in
`logs/2026-04-26_0118_refly-postfix-still-broken`. The fix keys
`FinalizerCache refresh summary` by owner/recording/terminal state, rate-limits
stable no-delta and repeated classification summaries, collapses the
patched-snapshot missing-body / captured and extrapolator seeded-OFR repeaters
with `VerboseOnChange`, rate-limits empty GhostMap cleanup, gates map-visible
window diagnostics on source/window changes, and folds the Task 1.5 ledger /
sandbox-patcher repeaters into state-change gated summaries. Focused xUnit log
assertions pin each gate. The broader observability audit remains open for later
missing-decision logs and save/load context work.

Status update (`observability/playback-visibility`): closed the Phase 2 flight
playback visibility slice for ghost skip reasons, on-change skip logging, engine
aggregate skip counters, fast-forward watch handoff reasons, and watch-camera
infrastructure failures. The branch also added map-view/proto-vessel visibility
reasoning for missing map objects, orbit renderers, draw-icon state, native-icon
suppression, renderer force-enable, and watched-ghost map-focus restore blockers.
Review follow-up: map-focus restore logging now uses one stable on-change
identity with the watched recording/pid/reason in the state key, avoiding
per-recording cache growth while preserving reason-change visibility.
Review follow-up: Flight scene teardown and `DestroyAllTimelineGhosts` now clear
ghost-skip reason state and the matching `Flight|ghost-skip|` `VerboseOnChange`
identities, with coverage showing per-recording skip reasons re-emit after
scene cleanup and rewind/timeline destruction.
Remaining observability audit items stay open.

Phase 3 persistence/rewind observability is closed on
`observability/persistence-rewind` (2026-04-26): `OnSave` / `OnLoad` now carry
top-level exception context and single phase/status timing; recording sidecar,
snapshot-probe, path-resolution, and transient cleanup failures now surface
Warn/Error context with recording id, save folder, epoch, ghost snapshot mode,
file kind, paths, staged-file count, and exception details; Rewind/Re-Fly
`CanInvoke` plus disabled slot decisions now log only on reason changes. This
closes the audit follow-up for duplicate/miscounted `OnLoad` timing, sidecar/path
failure severity/context, and rewind precondition reason visibility. Remaining
observability-audit work stays in the non-persistence phases: KSC/playback spam
hygiene, ghost skip summaries, recorder/auto-record decision logs, game-action
aggregation, and map/UI/test-runner visibility.

Review follow-up: legacy text snapshot parse exceptions again flow to the
outer `exception:<Type>` sidecar failure path; resolve-only path lookups now log
missing save context at Verbose while directory-creation entry points keep Warn;
and Rewind/Re-Fly slot `VerboseOnChange` identities are cleared when RP state is
loaded, closed, reaped, discarded, or rolled back.

Runtime-gaps branch progress (2026-04-26): Phase 4/5 recorder and
game-visible runtime decisions are now covered for the high-priority gaps:
background recorder attach/clear and drift warnings, active-to-background
missing-vessel/finalizer diagnostics, post-switch auto-record no-trigger and
manifest-delta summaries, EVA/boarding split skips, ParsekUI map-marker skip
summaries, Tracking Station atmospheric-marker skip summaries, ghost orbit-line
suppression decisions, game-action converter skip-by-type summaries, event
reject logs, kerbal recalculation counters, Real Spawn Control auto-close
reasons, and test-runner scene-eligibility skip aggregation.
Review follow-up: post-switch manifest logging preserves trigger-priority
short-circuiting, marking lower-priority delta families as `skipped` instead of
diffing every manifest category on each 0.25s evaluation tick; the background
state-drift throttle now has a backwards-UT rollback test.

Remaining observability follow-up after runtime-gaps: the earlier P1/P2
save/load exception context, sidecar/path severity expansion, rewind
`CanInvoke` reason-change logging, playback-engine frame skip counters, and
Phase 6 retained in-game log-package validation still need separate passes.

Review follow-up coverage (2026-04-26): closed the deferred log-assertion gaps
for finalizer refresh identity isolation, Diagnostics missing-sidecar path
warning scopes, `ComputePlaybackFlags` ghost-skip emit/suppress behavior,
`OnSave` exception context/RecState, and unsupported snapshot probe logging.

Post-merge spam fix (2026-04-26, `fix/rewindui-canInvokeSlot-spam`): the
2026-04-26_1025 playtest log showed 1389 identical `[RewindUI] CanInvokeSlot:
slot-ok` lines in 6 seconds for a single rp/slot — the existing
`ParsekLog.VerboseOnChange` gate did not suppress the repeats from the OnGUI
draw loop, while the matching `[Rewind] CanInvoke:` site (same code path,
same dictionary) suppressed correctly. The xUnit 200-call repro passes, so
the failure is Unity-runtime-specific. `LogRewindSlotCanInvokeDecision` now
tracks the last-emitted decision stateKey in a file-local
`Dictionary<string,string>` and only calls `ParsekLog.Verbose` when it
changes — mirroring the `lastCanInvoke` pattern already used by
`DrawUnfinishedFlightRewindButton` ~300 lines above. Existing
`ClearRewindSlotCanInvokeLogState` callers (LoadTimeSweep, RewindPointAuthor,
RewindPointReaper, TreeDiscardPurge, ParsekScenario.OnLoad) clear the new
dict alongside the original `ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix`
call. Review follow-up: removed the per-OnGUI-pass clear that
`RecordingsTableUI.DrawIfOpen` was firing while the Recordings window was
closed — it wiped the cache before TimelineWindowUI's Fly button could
reuse it, re-spamming `slot-ok` whenever Timeline was open without
Recordings. Regression tests:
`RewindSlotCanInvoke_ManyConsecutiveCalls_EmitsOnceForStableSlotOk` drives
200 calls and asserts a single emit;
`RewindSlotCanInvoke_TimelineOnlyCalls_DoNotRespamAfterRecordingsClose`
drives 200 Timeline-style calls after a single close-transition clear and
asserts only 2 emits total.

---

# Known Bugs

## 435. Multi-recording Gloops trees (main + debris + crew children, no vessel spawn)

**Source:** world-model conversation on #432 (2026-04-17). The aspirational design for Gloops: when the player records a Gloops flight that stages or EVAs, the capture produces a **tree of ghost-only recordings** — main + debris children + crew children — all flagged `IsGhostOnly`, all grouped under a per-flight Gloops parent in the Recordings Manager, and none of them spawning a real vessel at ghost-end. Structurally the same as the normal Parsek recording tree (decouple → debris background recording, EVA → linked crew child), with the ghost-only flag applied uniformly and the vessel-spawn-at-end path skipped.

**Guiding architectural principle:** per `docs/dev/gloops-recorder-design.md`, Gloops is on track to be extracted as a standalone mod on which Parsek will depend. Parsek's recorder and tree infrastructure will become the base that both Gloops and Parsek share — Gloops exposes the trajectory recorder + playback engine, Parsek layers the career-state / tree / DAG / world-presence envelope on top via the `IPlaybackTrajectory` boundary. Multi-recording Gloops must therefore **reuse Parsek's existing recorder, tree, and BackgroundRecorder infrastructure** rather than growing a parallel Gloops-flavored implementation. The ghost-only distinction is a per-recording flag on top of shared machinery, not a separate code path.

**2026-04-19 boundary note:** `GhostPlaybackEngine.ResolveGhostActivationStartUT` no longer casts back to `Recording`; the engine now resolves activation start from playable payload bounds through `PlaybackTrajectoryBoundsResolver` over `IPlaybackTrajectory`. #435 remains otherwise unchanged, but this leak is no longer part of the extraction risk surface.

**Current state (audited 2026-04-17):**

- `gloopsRecorder` is a **parallel** `FlightRecorder` instance with no `ActiveTree` (`ParsekFlight.cs:7460`) — a temporary workaround that the extraction direction wants to retire.
- `BackgroundRecorder` is never initialized in the Gloops path — only alongside `activeTree` for normal recordings. Staging during a Gloops flight does not produce a debris child.
- `FlightRecorder.HandleVesselSwitchDuringRecording` auto-stops Gloops on any vessel switch (`FlightRecorder.cs:5143-5151`), so EVA does not produce a linked crew child either.
- `RecordingStore.CommitGloopsRecording` accepts a single `Recording`, adds it to the flat `"Gloops - Ghosts Only"` group (`RecordingStore.cs:394-418`). No `CommitGloopsTree`, no nested group structure.
- No conditional `IsGloopsMode` branch inside `RecordingTree`, no half-finished Gloops tree scaffolding.

**Net: Gloops is strictly single-recording by design today**, implemented as a parallel workaround. Multi-recording Gloops is a separate, sizable feature that should also consolidate Gloops onto the shared Parsek recorder (retire the parallel `gloopsRecorder` path).

**Desired behavior:**

- Gloops uses Parsek's main `FlightRecorder` + `RecordingTree` + `BackgroundRecorder` path, with a tree-level `IsGhostOnly` flag propagated to every leaf at commit. No parallel `gloopsRecorder`.
- Starting a Gloops recording creates a `RecordingTree` with the ghost-only flag; normal recording continues alongside on the same machinery if already active, or the tree operates solo if not. How the two modes interleave in the UI (explicit toggle, implicit based on UI state, etc.) is for the implementing PR to decide — possibly in coordination with a UI gate preventing concurrent career + Gloops capture.
- Staging during a Gloops flight → debris gets its own ghost-only recording via the normal `BackgroundRecorder` split path, with `IsGhostOnly = true` inherited from the tree.
- EVA during a Gloops flight → linked child ghost-only recording via the normal EVA split path.
- Commit: the whole Gloops tree flushes as a nested group under `"Gloops - Ghosts Only"` — e.g. `"Gloops - Ghosts Only / Mk3 Airshow Flight"` with child debris / crew recordings under it. Every leaf is `IsGhostOnly`.
- No vessel-spawn-at-end for any recording in a Gloops tree. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` already gates on `!rec.IsGhostOnly` (see `GhostPlaybackLogic.cs:3001`); the tree case reuses this.
- Per-recording delete / regroup / rename in the Recordings Manager works the same as normal trees.
- Apply-side: #432's filter reads `rec.IsGhostOnly` per-recording, so every leaf in a Gloops tree is already excluded from the ledger with no extra work.

**Files likely to touch (sketch, not exhaustive):**

- `Source/Parsek/ParsekFlight.cs` — retire `gloopsRecorder` in favor of the main `recorder`/`activeTree` path; the "Start Gloops" action creates a tree flagged ghost-only. `CheckGloopsAutoStoppedByVesselSwitch` goes away or is folded into normal tree commit.
- `Source/Parsek/FlightRecorder.cs` — remove `IsGloopsMode` branches once the parallel recorder is retired; the recorder becomes agnostic to career semantics (aligning with the extraction boundary in `gloops-recorder-design.md`).
- `Source/Parsek/BackgroundRecorder.cs` — carry a tree-level ghost-only flag so debris children inherit it.
- `Source/Parsek/RecordingStore.cs` — collapse `CommitGloopsRecording` into the normal tree commit path; the ghost-only distinction is per-tree (or per-leaf, if partial-Gloops trees ever become a thing, which they shouldn't).
- `Source/Parsek/UI/GloopsRecorderUI.cs` — controls now drive the main recorder with a ghost-only flag rather than spinning up a parallel instance.
- `Source/Parsek.Tests/` — tree-structural tests for multi-recording Gloops capture and commit.

**Dependencies / sequencing:**

- Ships after #432 (which closes the existing single-recording leak and establishes the per-recording `IsGhostOnly` apply-side filter that multi-recording Gloops will rely on).
- Coordinates loosely with the Gloops extraction work (`docs/dev/gloops-recorder-design.md` Section 11 — the extraction sequence); ideally this consolidation happens before extraction so the extraction moves a single unified recorder, not two.
- Not tied to the deterministic-timeline correctness cluster — this is a feature extension, not a correctness bug.

**Out of scope:**

- Making Gloops spawn real vessels at ghost-end (explicitly not wanted — Gloops is visual-only).
- Turning the existing single-recording Gloops path into a tree retroactively for existing saves (beta, restart the save if you want the new behavior).
- Actually extracting Gloops into its own mod. That's covered by `docs/dev/gloops-recorder-design.md`'s extraction plan. #435 is a preparatory consolidation step on the Parsek side.

**Priority:** Medium. Feature extension + architectural cleanup. Worth scoping after #432 lands.

**Status:** TODO. Size: L. New feature — not a follow-up to anything shipped today.

---

## 430. "Why is this blocked?" explainer for the committed-action dialog

**Source:** follow-up on the "paradox communication" thread — currently when the player tries to re-research a tech or re-upgrade a facility that's already committed to a future timeline event, `CommittedActionDialog` pops up with a short "Blocked action: X — reason" message. The reason is generic and the player has no way to see *which* committed action is causing the block, or *when* it will play out.

**Partial mitigation:** PR #721 adds stock R&D / Astronaut Complex / Mission Control row badges with tooltips for committed-future actions, including the event UT and source recording when available. This helps before the click, but does not replace the structured blocked-action dialog below: the dialog still needs conflict context, Timeline navigation, and the rewind shortcut.

**Desired behavior:**

- Replace the one-line reason with a structured block:
  - The action the player tried (e.g. "Research node: Heavier Rocketry").
  - The committed action that blocks it, including the source recording and its UT (e.g. "Already scheduled at UT 183420 in recording 'Mun Lander 3'").
  - A `Go to Timeline` button that opens the Timeline window and scrolls to the offending entry (reuses `TimelineWindowUI.ScrollToRecording`).
  - A `Revert to launch` shortcut if the player actually wants to undo it (routes to the existing rewind dialog pre-filled with the blocking recording).
- Keep the OK/close path unchanged so existing muscle memory still works.

**Why it matters:**

The mental model of "you can't do this because the timeline already did" is counter-intuitive for a first-time player. Showing the *which* and *when* turns a mysterious block into a debuggable constraint, reinforcing the ledger-as-truth principle every time a block fires.

**Files to touch:**

- `Source/Parsek/CommittedActionDialog.cs` — extend the dialog body; accept an optional `blockingRecordingId` + `blockingUT` + `blockingAction` tuple.
- `Source/Parsek/Patches/*Patch.cs` (where blocks are triggered for tech research / facility upgrade / part purchase) — pass the conflict context into the dialog instead of just the short reason string.
- `Source/Parsek/UI/TimelineWindowUI.cs` — already has `ScrollToRecording`; no changes beyond what's there.

**Out of scope for v1:**

- Auto-resolving the block by rewinding silently; this stays an informational dialog, not a one-click rewind.
- Collapsing multiple overlapping blocks into a summary (each block fires its own dialog as today).

**Status:** TODO. Size: S-M. Best quality-per-effort of the paradox-comms work.

---

## 428. Preview-rewind pane

**Source:** follow-up on the "cost-of-rewind is hard to intuit" thread. Rewind is the most consequential single action in Parsek — it moves the player back to a chosen launch point and replays forward with existing ghosts. But right now the rewind confirmation dialog shows a single summary line ("Rewind to 'Mun Lander 3' at Y1 D23?") and a raw count of "how many future recordings exist". A player can't tell before confirming: which exact recordings will be preserved, which will be replayed, which resources / contracts / milestones will be re-rolled, whether crew reservations will shift.

**Desired behavior:**

- Replace the existing one-line confirmation with a two-pane preview dialog anchored on the rewind button.
- Left pane: **"Before rewind point"** — committed recordings whose `EndUT <= rewindTargetUT` (stay intact on the ledger and their ledger effects remain applied); game-action milestones that already fired before the target; crew reservations that complete before the target.
- Right pane: **"Re-rolled forward"** — committed recordings whose `StartUT > rewindTargetUT` (they stay committed; their resource deltas + events re-apply from the target UT forward as the player plays); milestones pending at UT > target (they'll re-fire); crew reservations spanning the target (stand-in chain resets).
- Each pane shows a count + a preview list of the first ~5 items with `...and N more` if longer.
- Confirm / Cancel buttons unchanged.

**Why it matters:**

Rewind currently feels like a commitment to the unknown — the player isn't sure what they'll lose. Making the consequences legible before the dialog closes reduces regret and teaches the two buckets (before / re-rolled), which is the honest mental model: rewind is deterministic replay, nothing is thrown away.

**Files to touch:**

- `Source/Parsek/UI/RewindConfirmationUI.cs` (new or extension of the existing confirmation helper — current code is inlined in `RecordingsTableUI.ShowRewindConfirmation`).
- A `RewindPreview.Build(recordings, ledgerActions, milestones, rewindTargetUT, liveUT)` pure helper that classifies each item as "before rewind point" or "re-rolled forward". Lives next to `TimelineBuilder` since both walk similar data.
- Tests: classification helper fully covered (happy path + each bucket's edge cases + an item spanning the target UT).

**Out of scope for v1:**

- Previewing the new resource balance after rewind. Just show counts + first few items.
- Undo for rewind. One-way operation stays one-way.

**Status:** TODO. Size: M-L. Biggest UX win per dollar on the rewind mechanic.

---

## 427. Proactive paradox warnings surface

**Source:** follow-up on the conversation after shipping the Career State window. Today the mod prevents paradoxes mostly via blocks (action-blocked dialog) and a single red over-committed warning in the Timeline's resource footer. There's no centralized surface that says "your committed timeline has these N potential issues" — so a player can build up a career with, e.g., a contract that expires before its committed completion, or a facility upgrade requiring a level that won't be reached in time, and only discover the contradiction when it fires (or silently zeroes out).

**Desired behavior:**

- A **Warnings** badge on the main ParsekUI button row — hidden when count is 0, shown as `Warnings (N)` when any warning rules fire.
- Clicking opens a small scrollable window listing each warning as a row:
  - Category tag (`Contract`, `Facility`, `Strategy`, `Resource`, `Crew`).
  - One-line description (`Contract "Rescue Kerbal" deadline UT 240000 is before committed completion at UT 250000`).
  - `Go to ...` button linking to the relevant other window (Timeline scroll, Career State tab, etc.).
- Warnings are computed once per `OnTimelineDataChanged` fan-out (same cache-invalidation channel everything else uses).
- Starter rule set, each as a pure static helper in `WarningRules.cs`:
  - **ContractDeadlineMissed** — active contract's `DeadlineUT < terminal-UT of its committed completion recording`.
  - **FacilityLevelRequirement** — an action requires facility level N but the facility doesn't reach N until after that action's UT.
  - **StrategySlotOverflow** — projected active strategies > projected max slots (currently only warned in log, not UI).
  - **ContractSlotOverflow** — same for contracts.
  - **CrewDoubleBooking** — a stand-in appears in two chains at overlapping UT ranges.
  - **ResourceOverCommit** — already shown in Timeline budget footer, but also listed here for one-stop-shop.

**Why it matters:**

Action blocking catches paradoxes at the moment the player tries to violate them. Warnings catch *latent* contradictions that the ledger can detect but won't error on — the subtle ones where the ledger silently picks a resolution the player didn't intend (e.g. contract gets zeroed out because its deadline passed unexpectedly). Surfacing these early turns the mod's "structural paradox prevention" into a communicated design contract rather than a hidden invariant.

**Files to touch:**

- `Source/Parsek/UI/WarningsWindowUI.cs` — new scrollable list window.
- `Source/Parsek/WarningRules.cs` — new pure-static rule evaluators, one method per rule, each returning `List<Warning>` given `(ledger, recordings, modules)`. Heavy unit-test coverage.
- `Source/Parsek/ParsekUI.cs` — add the badge button + open toggle; integrate with `OnTimelineDataChanged` cache invalidation.
- `Source/Parsek.Tests/WarningRulesTests.cs` — one test per rule (happy + each flag condition).

**Out of scope for v1:**

- Auto-fix for any warning. Pure read-only surface.
- Severity levels / color-coding. All warnings are equal in v1; add severity in a follow-up if there are too many of one kind.
- Per-rule disable toggles. Playtesting can decide which rules feel noisy before we add knobs.

**Status:** TODO. Size: M. Complements the help popup (#426) — where help explains the system, warnings explain *your career's* specific issues. Together they turn the mod from "learn by experimenting" to "learn by seeing the model."

---

## 426. In-window help popups explaining each Parsek system

**Source:** follow-up conversation during the #416 UI polish pass. A player unfamiliar with the mod has to read `docs/user-guide.md` (out of the game) to understand what each window's sections and columns mean. The mechanics are specific enough (slots vs. stand-ins vs. reservations, per-recording fates, timeline tiers, resource budget semantics, etc.) that even tooltips-on-hover don't carry the full picture. An in-game help surface keeps the explanation next to the thing it explains.

**Desired behavior:**

- A small `?` icon button rendered in the title bar (or as the last button in the main toolbar row) of each Parsek window: Recordings, Timeline, Kerbals, Career State, Real Spawn Control, Gloops Flight Recorder, Settings.
- Clicking the `?` opens a small modal-ish popup window titled `Parsek - {Window} Help` anchored next to the parent window.
- The popup body is static help text tailored to that window. For tabbed windows (Kerbals, Career State), the help content should also cover each tab, either as one scrolling document or as a small tab-match sub-structure inside the popup. Keep each section brief (5-15 sentences) — the goal is orientation, not exhaustive docs.
- A "Close" button and `GUI.DragWindow()` so the popup can be moved.
- Help text can be hard-coded string constants in `Source/Parsek/UI/HelpContent/` (one file per window). No runtime load, no localization for v1.
- Suggested starter content:
  - **Recordings** — column-by-column walkthrough, L/R/FF/W/Hide button meanings, group vs chain vs ghost-only distinction.
  - **Timeline** — Overview vs Details tiers, Recordings/Actions/Events source toggles, time-range filter, resource-budget footer, loop toggle semantics on entry rows, GoTo cross-link.
  - **Kerbals** — slots vs stand-ins vs reservations (Roster State tab), chronological outcomes per kerbal (Mission Outcomes tab), outcome-click-scrolls-Timeline.
  - **Career State** — contracts / strategies / facilities / milestones tabs, current-vs-projected columns when the timeline holds pending recordings, Mission Control / Administration slot math.
  - **Real Spawn Control** — what it does (warp-to-vessel-spawn), State column, 500m proximity trigger.
  - **Gloops** — ghost-only manual recording, loop-by-default commit, X delete button in Recordings.
  - **Settings** — group-by-group overview (Recording, Looping, Ghosts, Diagnostics, Recorder Sample Density, Data Management); call out Auto-merge, Auto-launch, Camera cutoff, Show-ghosts-in-Tracking-Station.

**Out of scope for v1:**

- Inline tooltips on every sub-control (hover-tooltips already exist for a few buttons; expanding them is a separate follow-up).
- Localization / translation.
- Interactive tutorials.
- Search within help content.
- External hyperlinks (no browser launch from KSP IMGUI reliably).

**Files to touch:**

- New: `Source/Parsek/UI/HelpWindowUI.cs` (shared small popup window; takes a `windowKey` + body-text source).
- New: `Source/Parsek/UI/HelpContent/*.cs` (one static class per window, each exposes `public const string Body` or a `BuildBody()` method if dynamic content is needed later).
- Each existing window UI file (RecordingsTableUI, TimelineWindowUI, KerbalsWindowUI, CareerStateWindowUI, SpawnControlUI, GloopsRecorderUI, SettingsWindowUI): add a small `?` button and an `IsHelpOpen` toggle that feeds HelpWindowUI.
- `ParsekUI.cs`: add a single shared `HelpWindowUI` field + accessor so every window delegates to the same instance (only one popup open at a time).
- `CHANGELOG.md` entry under Unreleased.
- `docs/user-guide.md` can mention the new `?` buttons briefly but stays as the authoritative long-form reference.

**Status:** TODO. Size: M. Style it the same way as the rest of the mod (shared section headers, dark list box for paragraph groups, pressed toggle idiom if any sub-tabs appear).

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

2026-04-25 update: deferred spawn queue outside-physics-bubble waits are no longer
a spam source; the per-recording kept line and repeated warp-ended summary were
replaced with a rate-limited queue wait summary.

2026-04-25 update (UnfinishedFlights + missed-vessel-switch):
`logs/2026-04-25_1314_marker-validator-fix/KSP.log` was 96 MB / 540k lines, of
which ~511k (94%) were `[Parsek][VERBOSE][UnfinishedFlights]
IsUnfinishedFlight=…` decisions and ~1k were `[Parsek][WARN][Flight] Update:
recovering missed vessel switch` lines. Both fired from per-frame paths:
`EffectiveState.IsUnfinishedFlight` is invoked once per recording per frame from
`RecordingsTableUI` row drawing, `UnfinishedFlightsGroup` membership filtering,
and `TimelineBuilder`; the missed-vessel-switch warn fires in `ParsekFlight`
`Update()` until the recovery handler clears the predicate, which in this
playtest took dozens to hundreds of frames per vessel. Each of the 7 return
paths in `IsUnfinishedFlight` now uses `ParsekLog.VerboseRateLimited` keyed by
`{reason}-{recordingId}` so each (recording, reason) pair logs once per
rate-limit window. The missed-vessel-switch warn now uses
`ParsekLog.WarnRateLimited` keyed by `missed-vessel-switch-{activeVesselPid}`
so each vessel logs at most once per window. Regression
`EffectiveStateTests.IsUnfinishedFlight_RepeatedCallsSameRec_RateLimitedToOneLine`
calls the predicate 100x with the same recording and asserts a single emitted
line.

2026-04-25 update (post-#591 second-tier cleanup): the `2026-04-25_1933_refly-bugs`
KSP.log surfaced six more spam sources, addressed as numbered bugs #592-#596
(closed in this commit) plus #597 (open underlying-logic concern). #592 covers
the ~3300 `Time warp rate changed` / `CheckpointAllVessels` / `Active vessel
orbit segments handled` lines from KSP's chatty `onTimeWarpRateChanged`
GameEvent. #593 covers ~1190 lines from repeatable record milestones
(`Records*` IDs) re-emitting the same `Milestone funds` / `stays effective` /
`Milestone rep at UT` line on every recalc walk. #594 covers 221 KspStatePatcher
bare-Id fallback lines. #595 widens the OrbitalCheckpoint playback and Recorder
sample-skipped rate-limit windows from 1-2s to the default 5s. #596 gates the
PatchFacilities INFO summary on having actual work. #597 later closed the
underlying duplicate checkpoint work with a same-tree/same-rate/same-UT guard
plus recorder-level duplicate-boundary idempotence.

2026-04-26 update (observability Phase 1 current spam hygiene): the newest
retained package `2026-04-26_0118_refly-postfix-still-broken` surfaced a
different top-repeat set: finalizer-cache periodic summaries, repeated
patched-snapshot missing-body/captured pairs, repeated extrapolator seeded
orbital-frame-rotation lines, and small GhostMap cleanup/window repeaters. This
branch keys finalizer summaries by owner/recording/terminal state, removes the
no-delta Info backstop, keeps only the first unique classification at Info,
gates patched-snapshot and OFR-seeding details with `VerboseOnChange`, and
rate-limits empty GhostMap cleanup plus diagnostics missing-sidecar warnings.
The follow-up also gates repeated all-zero ledger summaries and sandbox/no-target
KSP patch skips with `VerboseOnChange`. Focused xUnit log assertions pin each
gate. Remaining broader audit work stays tracked by the Observability Audit
section above.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1` section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary `v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen without unpacking the authoritative files first.

Remaining high-value work should stay measurement-gated and follow `docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`:

- any further snapshot-side work now has to clear a higher bar: `.prec` and `_ghost.craft` are already roughly equal buckets after compression, and `_vessel.craft` is small, so "focus on snapshots next" only applies if a future corpus shifts the split back toward snapshots
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if a future rebaseline against a larger / more vessel-heavy corpus shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay covered by sidecar/load diagnostics

**Priority:** Current Phase 11.5 follow-on work — measurement-gated guidance for future shrink work rather than active tasks

---

## TODO — Ghost Visuals

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

Latest investigation: a second procedural-truss attempt was tested against fresh collected logs in `logs/2026-04-13_1529_fairing-truss-artifact`. The run correctly detected `FairingJettisoned` and rebuilt the ghost with `showMesh=True`, but the generated truss still looked bad in game: visible dark bars with transparent gaps following the fairing outline from base to tip. This confirms the simplified procedural replacement is still not shippable.

Important constraint: the current ghost snapshot is just a normal `ProtoVessel`/`ConfigNode` capture (`BackupVessel` output copied into `GhostVisualSnapshot`). That preserves fairing state such as `fsm`, `ModuleStructuralNodeToggle.showMesh`, and `XSECTION`, but it does not preserve the live runtime-generated stock Cap/Truss mesh deformation/material state from `ModuleProceduralFairing`. So the ghost cannot reproduce the exact stock truss visual from snapshot data alone.

To implement properly: prefer a stock-authoritative approach instead of another simplified procedural mesh. Most likely options are either capturing the live stock fairing truss render/mesh state at record time, or spawning/regenerating a hidden stock fairing from the snapshot and cloning the resulting stock truss renderers for the ghost. Only fall back to custom geometry if it can genuinely match stock quality.

**Status:** Open — do not revive the current simplified procedural-strip truss

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort
